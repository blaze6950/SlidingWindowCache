using Intervals.NET.Domain.Abstractions;
using Intervals.NET.Extensions;
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
public sealed class VisitedPlacesCache<TRange, TData, TDomain>
    : IVisitedPlacesCache<TRange, TData, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private readonly UserRequestHandler<TRange, TData, TDomain> _userRequestHandler;
    private readonly AsyncActivityCounter _activityCounter;
    private readonly TtlEngine<TRange, TData>? _ttlEngine;

    // Disposal state: tracks active/disposing/disposed states and coordinates concurrent callers.
    private readonly DisposalState _disposal = new();

    /// <summary>
    /// Initializes a new instance of <see cref="VisitedPlacesCache{TRange,TData,TDomain}"/>.
    /// Prefer <see cref="VisitedPlacesCacheBuilder"/> for the fluent builder API.
    /// The constructor is available for advanced scenarios such as benchmarking or testing
    /// where direct instantiation with pre-built configuration is required.
    /// </summary>
    public VisitedPlacesCache(
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

        // Inject storage into the selector so it can sample directly via GetRandomSegment()
        // without requiring the full segment list to be passed at each call site.
        // Cast to the internal IStorageAwareEvictionSelector — ISegmentStorage is internal and
        // cannot appear on the public IEvictionSelector interface.
        if (selector is IStorageAwareEvictionSelector<TRange, TData> storageAwareSelector)
        {
            storageAwareSelector.Initialize(storage);
        }

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
        ISerialWorkScheduler<CacheNormalizationRequest<TRange, TData>> scheduler =
            options.EventChannelCapacity is { } capacity
                ? new BoundedSerialWorkScheduler<CacheNormalizationRequest<TRange, TData>>(
                    executor: (evt, ct) => executor.ExecuteAsync(evt, ct),
                    debounceProvider: static () => TimeSpan.Zero,
                    diagnostics: schedulerDiagnostics,
                    activityCounter: _activityCounter,
                    capacity: capacity,
                    singleWriter: false) // VPC: multiple user threads may publish concurrently
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
    public ValueTask<RangeResult<TRange, TData>> GetDataAsync(
        Range<TRange> requestedRange,
        CancellationToken cancellationToken)
    {
        _disposal.ThrowIfDisposed(nameof(VisitedPlacesCache<TRange, TData, TDomain>));

        // Invariant S.R.1: requestedRange must be bounded (finite on both ends).
        if (!requestedRange.IsBounded())
        {
            throw new ArgumentException(
                "The requested range must be bounded (finite on both ends). Unbounded ranges cannot be fetched or cached.",
                nameof(requestedRange));
        }

        return _userRequestHandler.HandleRequestAsync(requestedRange, cancellationToken);
    }

    /// <inheritdoc/>
    public Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        _disposal.ThrowIfDisposed(nameof(VisitedPlacesCache<TRange, TData, TDomain>));

        return _activityCounter.WaitForIdleAsync(cancellationToken);
    }

    /// <summary>
    /// Asynchronously disposes the cache and releases all background resources.
    /// </summary>
    /// <returns>A <see cref="ValueTask"/> that completes when all background work has stopped.</returns>
    /// <remarks>
    /// Safe to call multiple times (idempotent). Concurrent callers wait for the first disposal to complete.
    /// </remarks>
    public ValueTask DisposeAsync() =>
        _disposal.DisposeAsync(async () =>
        {
            await _userRequestHandler.DisposeAsync().ConfigureAwait(false);

            if (_ttlEngine != null)
            {
                await _ttlEngine.DisposeAsync().ConfigureAwait(false);
            }
        });
}