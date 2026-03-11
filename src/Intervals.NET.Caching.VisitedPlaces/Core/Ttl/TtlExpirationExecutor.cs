using Intervals.NET.Caching.VisitedPlaces.Core.Eviction;
using Intervals.NET.Caching.VisitedPlaces.Infrastructure.Storage;
using Intervals.NET.Caching.VisitedPlaces.Public.Instrumentation;

namespace Intervals.NET.Caching.VisitedPlaces.Core.Ttl;

/// <summary>
/// Executes <see cref="TtlExpirationWorkItem{TRange,TData}"/> items on the TTL background loop.
/// For each work item: waits until the segment's expiration timestamp, then removes it directly
/// from storage and notifies the eviction engine if the segment had not already been removed.
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <remarks>
/// <para><strong>Execution Context:</strong> TTL background loop (independent of the Background Storage Loop).
/// Multiple TTL work items execute concurrently — one per stored segment — when
/// <see cref="ConcurrentWorkScheduler{TWorkItem}"/> is used as the scheduler.</para>
/// <para><strong>Algorithm per work item:</strong></para>
/// <list type="number">
/// <item><description>
///   Compute remaining delay as <c>ExpiresAt - UtcNow</c>.
///   If already past expiry (delay &lt;= zero), proceed immediately.
/// </description></item>
/// <item><description>
///   Await <c>Task.Delay(delay, cancellationToken)</c>.
///   If cancelled (cache disposal), <see cref="OperationCanceledException"/> propagates to
///   the scheduler's cancellation handler and the segment is NOT removed.
/// </description></item>
/// <item><description>
///   Call <see cref="ISegmentStorage{TRange,TData}.Remove"/> — which atomically claims
///   ownership via <see cref="CachedSegment{TRange,TData}.MarkAsRemoved()"/> internally
///   (<c>Interlocked.CompareExchange</c>) and returns <see langword="true"/> only for the
///   first caller. If it returns <see langword="false"/> the segment was already removed by
///   eviction; fire <see cref="IVisitedPlacesCacheDiagnostics.TtlSegmentExpired"/> and return
///   (idempotent no-op for storage and engine).
/// </description></item>
/// <item><description>
///   Call <see cref="EvictionEngine{TRange,TData}.OnSegmentRemoved"/> to update stateful
///   policy aggregates (e.g. <c>MaxTotalSpanPolicy._totalSpan</c> via
///   <see cref="System.Threading.Interlocked.Add(ref long, long)"/>).
///   The single-segment overload is used to avoid allocating a temporary collection.
/// </description></item>
/// <item><description>Fire <see cref="IVisitedPlacesCacheDiagnostics.TtlSegmentExpired"/>.</description></item>
/// </list>
/// <para><strong>Thread safety — concurrent removal with the Background Storage Loop:</strong></para>
/// <para>
/// Both this executor and <c>CacheNormalizationExecutor</c> may call
/// <see cref="ISegmentStorage{TRange,TData}.Remove"/> and
/// <see cref="EvictionEngine{TRange,TData}.OnSegmentsRemoved"/> concurrently.
/// Safety is guaranteed at each point of contention:
/// </para>
/// <list type="bullet">
/// <item><description>
///   <see cref="ISegmentStorage{TRange,TData}.Remove"/> internally calls
///   <see cref="CachedSegment{TRange,TData}.MarkAsRemoved()"/> via
///   <c>Interlocked.CompareExchange</c> — exactly one caller wins; the other returns
///   <see langword="false"/> and becomes a no-op.
/// </description></item>
/// <item><description>
///   <see cref="EvictionEngine{TRange,TData}.OnSegmentRemoved"/> is only reached by the winner
///   of <c>Remove</c>, so double-notification is impossible.
/// </description></item>
/// <item><description>
///   <see cref="EvictionEngine{TRange,TData}.OnSegmentsRemoved"/> updates
///   <c>MaxTotalSpanPolicy._totalSpan</c> via <c>Interlocked.Add</c> — safe under concurrent
///   calls from any thread.
/// </description></item>
/// </list>
/// <para><strong>Exception handling:</strong></para>
/// <para>
/// <see cref="OperationCanceledException"/> is intentionally NOT caught here — the scheduler's
/// execution pipeline handles it by firing <c>WorkCancelled</c> and swallowing it.
/// All other exceptions are also handled by the scheduler pipeline (<c>WorkFailed</c>), so this
/// executor does not need its own try/catch.
/// </para>
/// <para>Alignment: Invariants VPC.T.1, VPC.T.2, VPC.A.10.</para>
/// </remarks>
internal sealed class TtlExpirationExecutor<TRange, TData>
    where TRange : IComparable<TRange>
{
    private readonly ISegmentStorage<TRange, TData> _storage;
    private readonly EvictionEngine<TRange, TData> _evictionEngine;
    private readonly IVisitedPlacesCacheDiagnostics _diagnostics;

    /// <summary>
    /// Initializes a new <see cref="TtlExpirationExecutor{TRange,TData}"/>.
    /// </summary>
    /// <param name="storage">
    /// The segment storage. <see cref="ISegmentStorage{TRange,TData}.Remove"/> is called
    /// after <see cref="CachedSegment{TRange,TData}.MarkAsRemoved()"/> succeeds.
    /// </param>
    /// <param name="evictionEngine">
    /// The eviction engine. <see cref="EvictionEngine{TRange,TData}.OnSegmentsRemoved"/> is
    /// called after successful removal to keep stateful policy aggregates consistent.
    /// </param>
    /// <param name="diagnostics">Diagnostics sink; must never throw.</param>
    public TtlExpirationExecutor(
        ISegmentStorage<TRange, TData> storage,
        EvictionEngine<TRange, TData> evictionEngine,
        IVisitedPlacesCacheDiagnostics diagnostics)
    {
        _storage = storage;
        _evictionEngine = evictionEngine;
        _diagnostics = diagnostics;
    }

    /// <summary>
    /// Waits until the work item's expiration time, then removes the segment if it is still live.
    /// </summary>
    /// <param name="workItem">The TTL expiration work item to process.</param>
    /// <param name="cancellationToken">
    /// Cancellation token from the work item. Cancelled on cache disposal to abort pending delays.
    /// </param>
    /// <returns>A <see cref="Task"/> that completes when the expiration is processed or cancelled.</returns>
    public async Task ExecuteAsync(
        TtlExpirationWorkItem<TRange, TData> workItem,
        CancellationToken cancellationToken)
    {
        // Compute remaining delay from now to expiry.
        // If already past expiry, delay is zero and we proceed immediately.
        var remaining = workItem.ExpiresAt - DateTimeOffset.UtcNow;

        if (remaining > TimeSpan.Zero)
        {
            // Await expiry. OperationCanceledException propagates on cache disposal —
            // handled by the scheduler pipeline (not caught here).
            await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
        }

        // Delegate removal to storage, which atomically claims ownership via MarkAsRemoved()
        // and returns true only for the first caller. If the segment was already evicted by
        // the Background Storage Loop, this returns false and we fire only the diagnostic.
        if (!_storage.Remove(workItem.Segment))
        {
            // Already removed — still fire the diagnostic so TTL events are always counted.
            _diagnostics.TtlSegmentExpired();
            return;
        }

        // Notify stateful policies (e.g. decrements MaxTotalSpanPolicy._totalSpan atomically).
        // Single-segment overload avoids any intermediate collection allocation.
        _evictionEngine.OnSegmentRemoved(workItem.Segment);

        _diagnostics.TtlSegmentExpired();
    }
}
