using Intervals.NET.Caching.Infrastructure.Concurrency;
using Intervals.NET.Caching.Infrastructure.Diagnostics;
using Intervals.NET.Caching.Infrastructure.Scheduling;
using Intervals.NET.Caching.Infrastructure.Scheduling.Concurrent;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction;
using Intervals.NET.Caching.VisitedPlaces.Infrastructure.Storage;
using Intervals.NET.Caching.VisitedPlaces.Public.Instrumentation;

namespace Intervals.NET.Caching.VisitedPlaces.Core.Ttl;

/// <summary>
/// Facade that encapsulates the full TTL (Time-To-Live) subsystem: work item creation,
/// concurrent scheduling, activity tracking, and coordinated disposal.
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <remarks>
/// <para><strong>Execution Context:</strong> Created on the constructor thread; scheduling
/// is called from the Background Storage Loop; expiration executes fire-and-forget on the
/// thread pool via <see cref="ConcurrentWorkScheduler{TWorkItem}"/>.</para>
/// <para><strong>Responsibilities:</strong></para>
/// <list type="bullet">
/// <item><description>
///   Accepts newly stored segments via <see cref="ScheduleExpirationAsync"/> and publishes a
///   <see cref="TtlExpirationWorkItem{TRange,TData}"/> to the internal scheduler, computing
///   the absolute expiry time (<c>UtcNow + SegmentTtl</c>) at scheduling time.
/// </description></item>
/// <item><description>
///   Owns the shared <see cref="CancellationTokenSource"/> whose token is embedded in every
///   work item. A single <c>CancelAsync()</c> call during disposal simultaneously aborts all
///   pending <c>Task.Delay</c> calls across every in-flight TTL work item.
/// </description></item>
/// <item><description>
///   Owns the dedicated <see cref="AsyncActivityCounter"/> for TTL work items so that
///   <c>WaitForIdleAsync</c> on the main cache does NOT wait for long-running TTL delays.
/// </description></item>
/// <item><description>
///   Coordinates the full disposal sequence: cancel → stop scheduler → drain activity → release CTS.
/// </description></item>
/// </list>
/// <para><strong>Internal components (hidden from consumers):</strong></para>
/// <list type="bullet">
/// <item><description>
///   <see cref="TtlExpirationExecutor{TRange,TData}"/> — awaits the TTL delay, removes the
///   segment from storage, notifies the eviction engine, fires diagnostics.
/// </description></item>
/// <item><description>
///   <see cref="ConcurrentWorkScheduler{TWorkItem}"/> — dispatches each work item independently
///   to the thread pool so that multiple TTL delays run concurrently rather than serialised.
/// </description></item>
/// <item><description>
///   <see cref="AsyncActivityCounter"/> — tracks in-flight TTL work items for clean disposal.
/// </description></item>
/// <item><description>
///   <see cref="CancellationTokenSource"/> — shared disposal token; one signal cancels all delays.
/// </description></item>
/// </list>
/// <para><strong>Diagnostics split:</strong></para>
/// <para>
/// The engine fires <see cref="IVisitedPlacesCacheDiagnostics.TtlWorkItemScheduled"/> at
/// scheduling time (Background Storage Loop). The internal executor fires
/// <see cref="IVisitedPlacesCacheDiagnostics.TtlSegmentExpired"/> at expiration time (thread pool).
/// </para>
/// <para><strong>Storage access:</strong></para>
/// <para>
/// Unlike <see cref="EvictionEngine{TRange,TData}"/>, <see cref="TtlEngine{TRange,TData}"/>
/// does hold a reference to storage (passed through to the internal executor). TTL is a
/// background actor permitted to call <c>storage.Remove</c>; thread safety is guaranteed by
/// <see cref="CachedSegment{TRange,TData}.MarkAsRemoved()"/> (Interlocked.CompareExchange).
/// </para>
/// <para>Alignment: Invariants VPC.T.1, VPC.T.2, VPC.T.3, VPC.T.4.</para>
/// </remarks>
internal sealed class TtlEngine<TRange, TData> : IAsyncDisposable
    where TRange : IComparable<TRange>
{
    private readonly TimeSpan _segmentTtl;
    private readonly IWorkScheduler<TtlExpirationWorkItem<TRange, TData>> _scheduler;
    private readonly AsyncActivityCounter _activityCounter;
    private readonly CancellationTokenSource _disposalCts;
    private readonly IVisitedPlacesCacheDiagnostics _diagnostics;

    /// <summary>
    /// Initializes a new <see cref="TtlEngine{TRange,TData}"/> and wires all internal TTL
    /// infrastructure.
    /// </summary>
    /// <param name="segmentTtl">
    /// The time-to-live applied uniformly to every stored segment. Must be greater than
    /// <see cref="TimeSpan.Zero"/>.
    /// </param>
    /// <param name="storage">
    /// The segment storage. Passed through to <see cref="TtlExpirationExecutor{TRange,TData}"/>;
    /// <c>Remove</c> is called after the TTL delay elapses.
    /// </param>
    /// <param name="evictionEngine">
    /// The eviction engine. Passed through to <see cref="TtlExpirationExecutor{TRange,TData}"/>;
    /// <c>OnSegmentRemoved</c> is called after successful removal to keep stateful policy
    /// aggregates consistent.
    /// </param>
    /// <param name="diagnostics">Diagnostics sink; must never throw.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="storage"/>, <paramref name="evictionEngine"/>, or
    /// <paramref name="diagnostics"/> is <see langword="null"/>.
    /// </exception>
    public TtlEngine(
        TimeSpan segmentTtl,
        ISegmentStorage<TRange, TData> storage,
        EvictionEngine<TRange, TData> evictionEngine,
        IVisitedPlacesCacheDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(evictionEngine);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _segmentTtl = segmentTtl;
        _diagnostics = diagnostics;
        _disposalCts = new CancellationTokenSource();
        _activityCounter = new AsyncActivityCounter();

        var executor = new TtlExpirationExecutor<TRange, TData>(storage, evictionEngine, diagnostics);

        _scheduler = new ConcurrentWorkScheduler<TtlExpirationWorkItem<TRange, TData>>(
            executor: (workItem, ct) => executor.ExecuteAsync(workItem, ct),
            debounceProvider: static () => TimeSpan.Zero,
            diagnostics: NoOpWorkSchedulerDiagnostics.Instance,
            activityCounter: _activityCounter);
    }

    /// <summary>
    /// Schedules a TTL expiration work item for the given segment immediately after it has been
    /// stored in the Background Storage Loop.
    /// </summary>
    /// <param name="segment">The segment that was just added to storage.</param>
    /// <returns>A <see cref="ValueTask"/> that completes when the work item has been enqueued.</returns>
    /// <remarks>
    /// <para>
    /// Computes the absolute expiry time as <c>DateTimeOffset.UtcNow + SegmentTtl</c> and embeds
    /// the shared disposal <see cref="CancellationToken"/> into the work item so that a single
    /// <c>CancelAsync()</c> call during disposal simultaneously aborts all pending delays.
    /// </para>
    /// <para>
    /// Fires <see cref="IVisitedPlacesCacheDiagnostics.TtlWorkItemScheduled"/> after publishing.
    /// </para>
    /// <para><strong>Execution context:</strong> Background Storage Loop (Step 2 of
    /// <c>CacheNormalizationExecutor</c>), called once per stored segment when TTL is enabled.</para>
    /// </remarks>
    public async ValueTask ScheduleExpirationAsync(CachedSegment<TRange, TData> segment)
    {
        var workItem = new TtlExpirationWorkItem<TRange, TData>(
            segment,
            expiresAt: DateTimeOffset.UtcNow + _segmentTtl,
            _disposalCts.Token);

        await _scheduler.PublishWorkItemAsync(workItem, CancellationToken.None)
            .ConfigureAwait(false);

        _diagnostics.TtlWorkItemScheduled();
    }

    /// <summary>
    /// Asynchronously disposes the TTL engine and releases all owned resources.
    /// </summary>
    /// <returns>A <see cref="ValueTask"/> that completes when all in-flight TTL work has stopped.</returns>
    /// <remarks>
    /// <para><strong>Disposal sequence:</strong></para>
    /// <list type="number">
    /// <item><description>
    ///   Cancel the shared disposal token — simultaneously aborts all pending <c>Task.Delay</c>
    ///   calls across every in-flight TTL work item (zero per-item allocation).
    /// </description></item>
    /// <item><description>
    ///   Dispose the scheduler — stops accepting new work items.
    /// </description></item>
    /// <item><description>
    ///   Await <c>_activityCounter.WaitForIdleAsync()</c> — drains all in-flight work items.
    ///   Each item responds to cancellation by swallowing <see cref="OperationCanceledException"/>
    ///   and decrementing the counter, so this completes quickly after cancellation.
    /// </description></item>
    /// <item><description>
    ///   Dispose the <see cref="CancellationTokenSource"/>.
    /// </description></item>
    /// </list>
    /// <para>Alignment: Invariant VPC.T.3 (pending TTL delays cancelled on disposal).</para>
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        // Cancel the shared disposal token — simultaneously aborts all pending
        // Task.Delay calls across every in-flight TTL work item.
        await _disposalCts.CancelAsync().ConfigureAwait(false);

        // Stop accepting new TTL work items.
        await _scheduler.DisposeAsync().ConfigureAwait(false);

        // Drain all in-flight TTL work items. Each item responds to cancellation
        // by swallowing OperationCanceledException and decrementing the counter,
        // so this completes quickly after the token has been cancelled above.
        await _activityCounter.WaitForIdleAsync().ConfigureAwait(false);

        _disposalCts.Dispose();
    }
}
