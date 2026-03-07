using Intervals.NET.Domain.Abstractions;
using Intervals.NET.Caching.Dto;
using Intervals.NET.Caching.Infrastructure.Concurrency;
using Intervals.NET.Caching.Infrastructure.Scheduling;
using Intervals.NET.Caching.VisitedPlaces.Core;
using Intervals.NET.Caching.VisitedPlaces.Core.Background;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction;
using Intervals.NET.Caching.VisitedPlaces.Core.UserPath;
using Intervals.NET.Caching.VisitedPlaces.Infrastructure.Adapters;
using Intervals.NET.Caching.VisitedPlaces.Infrastructure.Storage;
using Intervals.NET.Caching.VisitedPlaces.Public.Configuration;
using Intervals.NET.Caching.VisitedPlaces.Public.Instrumentation;

namespace Intervals.NET.Caching.VisitedPlaces.Public.Cache;

/// <inheritdoc cref="IVisitedPlacesCache{TRange,TData,TDomain}"/>
/// <remarks>
/// <para><strong>Architecture:</strong></para>
/// <para>
/// <see cref="VisitedPlacesCache{TRange,TData,TDomain}"/> acts as a <strong>Public Facade</strong>
/// and <strong>Composition Root</strong>. It wires together all internal actors but implements no
/// business logic itself. All user requests are delegated to the internal
/// <see cref="UserRequestHandler{TRange,TData,TDomain}"/>; all background work is handled by
/// <see cref="BackgroundEventProcessor{TRange,TData,TDomain}"/> via the scheduler.
/// </para>
/// <para><strong>Internal Actors:</strong></para>
/// <list type="bullet">
/// <item><description><strong>UserRequestHandler</strong> — User Path (read-only, fires events)</description></item>
/// <item><description><strong>BackgroundEventProcessor</strong> — Background Storage Loop (single writer)</description></item>
/// <item><description><strong>ChannelBasedWorkScheduler</strong> — serializes background events, manages activity</description></item>
/// </list>
/// <para><strong>Threading Model:</strong></para>
/// <para>
/// Two logical threads: the User Thread (serves requests) and the Background Storage Loop
/// (processes events, mutates storage, executes eviction). The User Path is strictly read-only
/// (Invariant VPC.A.10).
/// </para>
/// <para><strong>Consistency Modes:</strong></para>
/// <list type="bullet">
/// <item><description>Eventual: <see cref="GetDataAsync"/> — returns immediately</description></item>
/// <item><description>Strong: <c>GetDataAndWaitForIdleAsync</c> — awaits <see cref="WaitForIdleAsync"/> after each call</description></item>
/// </list>
/// <para><strong>Resource Management:</strong></para>
/// <para>
/// Always dispose via <c>await using</c>. Disposal stops the background scheduler and waits for
/// the processing loop to drain gracefully.
/// </para>
/// </remarks>
public sealed class VisitedPlacesCache<TRange, TData, TDomain>
    : IVisitedPlacesCache<TRange, TData, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private readonly UserRequestHandler<TRange, TData, TDomain> _userRequestHandler;
    private readonly AsyncActivityCounter _activityCounter;

    // Disposal state: 0 = active, 1 = disposing, 2 = disposed (three-state for idempotency)
    private int _disposeState;

    // TaskCompletionSource for concurrent disposal coordination (loser threads await this)
    private TaskCompletionSource? _disposalCompletionSource;

    /// <summary>
    /// Initializes a new instance of <see cref="VisitedPlacesCache{TRange,TData,TDomain}"/>.
    /// </summary>
    /// <param name="dataSource">The data source from which to fetch missing data.</param>
    /// <param name="domain">The domain defining range characteristics (used by domain-aware eviction executors).</param>
    /// <param name="options">Configuration options (storage strategy, channel capacity).</param>
    /// <param name="evaluators">
    /// One or more eviction evaluators. Eviction runs when ANY fires (OR semantics, Invariant VPC.E.1a).
    /// </param>
    /// <param name="executor">Eviction executor; maintains per-segment statistics and performs eviction.</param>
    /// <param name="cacheDiagnostics">
    /// Optional diagnostics sink. When <see langword="null"/>, <see cref="NoOpDiagnostics.Instance"/> is used.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="dataSource"/>, <paramref name="options"/>,
    /// <paramref name="evaluators"/>, or <paramref name="executor"/> is <see langword="null"/>.
    /// </exception>
    public VisitedPlacesCache(
        IDataSource<TRange, TData> dataSource,
        TDomain domain,
        VisitedPlacesCacheOptions options,
        // todo think about defining evaluators and executors inside options
        IReadOnlyList<IEvictionEvaluator<TRange, TData>> evaluators,
        IEvictionExecutor<TRange, TData> executor,
        ICacheDiagnostics? cacheDiagnostics = null)
    {
        // Fall back to no-op diagnostics so internal actors never receive null.
        cacheDiagnostics ??= NoOpDiagnostics.Instance;

        // Shared activity counter: incremented by scheduler on enqueue, decremented after execution.
        _activityCounter = new AsyncActivityCounter();

        // Create storage based on configured strategy.
        var storage = CreateStorage(options.StorageStrategy);

        // Background event processor: single writer, executes the four-step Background Path.
        var processor = new BackgroundEventProcessor<TRange, TData, TDomain>(
            storage,
            evaluators,
            executor,
            cacheDiagnostics);

        // Diagnostics adapter: maps IWorkSchedulerDiagnostics → ICacheDiagnostics.
        // todo maybe we can get rid of this weird adapter by utilizing interface inheritance?
        var schedulerDiagnostics = new VisitedPlacesWorkSchedulerDiagnostics(cacheDiagnostics);

        // Scheduler: serializes background events via a bounded channel.
        // Debounce is always zero — VPC processes every event without delay.
        // TODO: allow to use not only channel based scheduler - there is another one based on Task chaining. Check SWC implementation for reference.
        var scheduler = new ChannelBasedWorkScheduler<BackgroundEvent<TRange, TData>>(
            executor: (evt, ct) => processor.ProcessEventAsync(evt, ct),
            debounceProvider: static () => TimeSpan.Zero,
            diagnostics: schedulerDiagnostics,
            activityCounter: _activityCounter,
            capacity: options.EventChannelCapacity);

        // User request handler: read-only User Path, publishes events to the scheduler.
        _userRequestHandler = new UserRequestHandler<TRange, TData, TDomain>(
            storage,
            dataSource,
            scheduler,
            cacheDiagnostics,
            domain);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Thin delegation to <see cref="UserRequestHandler{TRange,TData,TDomain}.HandleRequestAsync"/>.
    /// This facade implements no business logic.
    /// </remarks>
    public ValueTask<RangeResult<TRange, TData>> GetDataAsync(
        Range<TRange> requestedRange,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposeState) != 0)
        {
            throw new ObjectDisposedException(
                nameof(VisitedPlacesCache<TRange, TData, TDomain>),
                "Cannot retrieve data from a disposed cache.");
        }

        return _userRequestHandler.HandleRequestAsync(requestedRange, cancellationToken);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// Delegates to <see cref="AsyncActivityCounter.WaitForIdleAsync"/>. The activity counter
    /// is incremented by the scheduler on each event enqueue and decremented after processing
    /// completes. Idle means all background events have been processed.
    /// </para>
    /// <para><strong>Idle Semantics ("was idle at some point"):</strong></para>
    /// <para>
    /// Completes when the system <em>was</em> idle — not that it <em>is currently</em> idle.
    /// New events may be published immediately after. Re-check state if stronger guarantees are needed.
    /// </para>
    /// </remarks>
    public Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposeState) != 0)
        {
            throw new ObjectDisposedException(
                nameof(VisitedPlacesCache<TRange, TData, TDomain>),
                "Cannot access a disposed cache instance.");
        }

        return _activityCounter.WaitForIdleAsync(cancellationToken);
    }

    /// <summary>
    /// Asynchronously disposes the cache and releases all background resources.
    /// </summary>
    /// <returns>A <see cref="ValueTask"/> that completes when all background work has stopped.</returns>
    /// <remarks>
    /// <para><strong>Three-state disposal (0=active, 1=disposing, 2=disposed):</strong></para>
    /// <list type="bullet">
    /// <item><description>Winner thread (first to CAS 0→1): creates TCS, runs disposal, signals completion</description></item>
    /// <item><description>Loser threads (see state=1): await TCS without CPU burn</description></item>
    /// <item><description>Already-disposed threads (see state=2): return immediately (idempotent)</description></item>
    /// </list>
    /// <para><strong>Disposal sequence:</strong></para>
    /// <list type="number">
    /// <item><description>Transition state 0→1</description></item>
    /// <item><description>Dispose <see cref="UserRequestHandler{TRange,TData,TDomain}"/> (cascades to scheduler)</description></item>
    /// <item><description>Transition state →2</description></item>
    /// </list>
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        var previousState = Interlocked.CompareExchange(ref _disposeState, 1, 0);

        if (previousState == 0)
        {
            // Winner thread: perform disposal and signal completion.
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _disposalCompletionSource, tcs);

            try
            {
                await _userRequestHandler.DisposeAsync().ConfigureAwait(false);
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
                throw;
            }
            finally
            {
                Volatile.Write(ref _disposeState, 2);
            }
        }
        else if (previousState == 1)
        {
            // Loser thread: wait for winner to finish (brief spin until TCS is published).
            TaskCompletionSource? tcs;
            var spinWait = new SpinWait();

            while ((tcs = Volatile.Read(ref _disposalCompletionSource)) == null)
            {
                spinWait.SpinOnce();
            }

            await tcs.Task.ConfigureAwait(false);
        }
        // previousState == 2: already disposed — return immediately (idempotent).
    }

    /// <summary>
    /// Creates the segment storage implementation for the specified strategy.
    /// </summary>
    private static ISegmentStorage<TRange, TData> CreateStorage(StorageStrategy strategy) =>
        strategy switch
        {
            StorageStrategy.SnapshotAppendBuffer =>
                new SnapshotAppendBufferStorage<TRange, TData>(),
            StorageStrategy.LinkedListStrideIndex =>
                new LinkedListStrideIndexStorage<TRange, TData>(),
            _ => throw new ArgumentOutOfRangeException(
                nameof(strategy),
                strategy,
                "Unknown storage strategy.")
        };
}
