using Intervals.NET.Domain.Abstractions;
using Intervals.NET.Caching.Dto;
using Intervals.NET.Caching.Infrastructure.Concurrency;
using Intervals.NET.Caching.Infrastructure.Scheduling;
using Intervals.NET.Caching.Infrastructure.Scheduling.Serial;
using Intervals.NET.Caching.VisitedPlaces.Core;
using Intervals.NET.Caching.VisitedPlaces.Core.Background;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction;
using Intervals.NET.Caching.VisitedPlaces.Core.Ttl;
using Intervals.NET.Caching.VisitedPlaces.Core.UserPath;
using Intervals.NET.Caching.VisitedPlaces.Infrastructure.Adapters;
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
/// <see cref="CacheNormalizationExecutor{TRange,TData,TDomain}"/> via the scheduler.
/// </para>
/// <para><strong>Internal Actors:</strong></para>
/// <list type="bullet">
/// <item><description><strong>UserRequestHandler</strong> — User Path (read-only, fires events)</description></item>
/// <item><description><strong>CacheNormalizationExecutor</strong> — Background Storage Loop (single writer for Add)</description></item>
/// <item><description><strong>UnboundedSerialWorkScheduler / BoundedSerialWorkScheduler</strong> — serializes background events, manages activity</description></item>
/// <item><description><strong>TtlEngine</strong> — TTL expiration path (concurrent, fire-and-forget)</description></item>
/// </list>
/// <para><strong>Threading Model:</strong></para>
/// <para>
/// Two logical threads: the User Thread (serves requests) and the Background Storage Loop
/// (processes events, adds to storage, executes eviction). The User Path is strictly read-only
/// (Invariant VPC.A.10). TTL expirations run concurrently on the ThreadPool and use atomic
/// operations (<see cref="CachedSegment{TRange,TData}.MarkAsRemoved()"/>) to coordinate
/// removal with the Background Storage Loop.
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
/// TODO: think about moving some part of the logic into the Intervals.NET, maybe we can move out the collection of not overlapped disjoint data ranges
public sealed class VisitedPlacesCache<TRange, TData, TDomain>
    : IVisitedPlacesCache<TRange, TData, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private readonly UserRequestHandler<TRange, TData, TDomain> _userRequestHandler;
    private readonly AsyncActivityCounter _activityCounter;
    private readonly TtlEngine<TRange, TData>? _ttlEngine;

    // Disposal state: 0 = active, 1 = disposing, 2 = disposed (three-state for idempotency)
    private int _disposeState;

    // TaskCompletionSource for concurrent disposal coordination (loser threads await this)
    private TaskCompletionSource? _disposalCompletionSource;

    /// <summary>
    /// Initializes a new instance of <see cref="VisitedPlacesCache{TRange,TData,TDomain}"/>.
    /// </summary>
    /// <remarks>
    /// This constructor is <see langword="internal"/>. Use <see cref="VisitedPlacesCacheBuilder"/>
    /// to create instances via the fluent builder API, which is the intended public entry point.
    /// </remarks>
    /// <param name="dataSource">The data source from which to fetch missing data.</param>
    /// <param name="domain">The domain defining range characteristics (used by domain-aware eviction policies).</param>
    /// <param name="options">Configuration options (storage strategy, scheduler type/capacity).</param>
    /// <param name="policies">
    /// One or more eviction policies. Eviction runs when ANY produces an exceeded pressure (OR semantics, Invariant VPC.E.1a).
    /// </param>
    /// <param name="selector">Eviction selector; determines candidate ordering for eviction execution.</param>
    /// <param name="cacheDiagnostics">
    /// Optional diagnostics sink. When <see langword="null"/>, <see cref="NoOpDiagnostics.Instance"/> is used.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="dataSource"/>, <paramref name="options"/>,
    /// <paramref name="policies"/>, or <paramref name="selector"/> is <see langword="null"/>.
    /// </exception>
    internal VisitedPlacesCache(
        IDataSource<TRange, TData> dataSource,
        TDomain domain,
        VisitedPlacesCacheOptions<TRange, TData> options,
        IReadOnlyList<IEvictionPolicy<TRange, TData>> policies,
        IEvictionSelector<TRange, TData> selector,
        IVisitedPlacesCacheDiagnostics? cacheDiagnostics = null)
    {
        // Fall back to no-op diagnostics so internal actors never receive null.
        cacheDiagnostics ??= NoOpDiagnostics.Instance;

        // Shared activity counter: incremented by scheduler on enqueue, decremented after execution.
        _activityCounter = new AsyncActivityCounter();

        // Create storage via the strategy options object (Factory Method pattern).
        var storage = options.StorageStrategy.Create();

        // Eviction engine: encapsulates selector metadata, policy evaluation, execution,
        // and eviction-specific diagnostics. Storage mutations remain in the processor.
        var evictionEngine = new EvictionEngine<TRange, TData>(policies, selector, cacheDiagnostics);

        // TTL engine: constructed only when SegmentTtl is configured. Encapsulates the work item
        // type, concurrent scheduler, activity counter, and disposal CTS behind a single facade.
        // Uses ConcurrentWorkScheduler internally — each TTL work item awaits Task.Delay
        // independently on the ThreadPool, so items do not serialize behind each other's delays.
        // Thread safety is provided by CachedSegment.MarkAsRemoved() (Interlocked.CompareExchange)
        // and EvictionEngine.OnSegmentsRemoved (Interlocked.Add in MaxTotalSpanPolicy).
        if (options.SegmentTtl.HasValue)
        {
            _ttlEngine = new TtlEngine<TRange, TData>(
                options.SegmentTtl.Value,
                storage,
                evictionEngine,
                cacheDiagnostics);
        }

        // Cache normalization executor: single writer for Add, executes the four-step Background Path.
        var executor = new CacheNormalizationExecutor<TRange, TData, TDomain>(
            storage,
            evictionEngine,
            cacheDiagnostics,
            _ttlEngine);

        // Diagnostics adapter: maps IWorkSchedulerDiagnostics → IVisitedPlacesCacheDiagnostics.
        var schedulerDiagnostics = new VisitedPlacesWorkSchedulerDiagnostics(cacheDiagnostics);

        // Scheduler: serializes background events without delay (debounce = zero).
        // When EventChannelCapacity is null, use unbounded serial scheduler (default).
        // When EventChannelCapacity is set, use bounded serial scheduler with backpressure.
        ISerialWorkScheduler<CacheNormalizationRequest<TRange, TData>> scheduler = options.EventChannelCapacity is { } capacity
            ? new BoundedSerialWorkScheduler<CacheNormalizationRequest<TRange, TData>>(
                executor: (evt, ct) => executor.ExecuteAsync(evt, ct),
                debounceProvider: static () => TimeSpan.Zero,
                diagnostics: schedulerDiagnostics,
                activityCounter: _activityCounter,
                capacity: capacity)
            : new UnboundedSerialWorkScheduler<CacheNormalizationRequest<TRange, TData>>(
                executor: (evt, ct) => executor.ExecuteAsync(evt, ct),
                debounceProvider: static () => TimeSpan.Zero,
                diagnostics: schedulerDiagnostics,
                activityCounter: _activityCounter);

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
    /// <item><description>Dispose <see cref="UserRequestHandler{TRange,TData,TDomain}"/> (cascades to normalization scheduler)</description></item>
    /// <item><description>Dispose <see cref="TtlEngine{TRange,TData}"/> (if TTL is enabled) — cancels pending delays, stops scheduler, drains in-flight items</description></item>
    /// <item><description>Transition state →2</description></item>
    /// </list>
    /// <para>
    /// <see cref="TtlEngine{TRange,TData}.DisposeAsync"/> coordinates the full TTL teardown:
    /// it cancels the shared disposal token (aborting all pending <c>Task.Delay</c> calls),
    /// stops the scheduler, and awaits the activity counter — guaranteeing that no TTL work
    /// item outlives the cache instance (Invariant VPC.T.3).
    /// </para>
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

                if (_ttlEngine != null)
                {
                    await _ttlEngine.DisposeAsync().ConfigureAwait(false);
                }

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
}
