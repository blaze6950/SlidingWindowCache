using Intervals.NET;
using Intervals.NET.Data;
using Intervals.NET.Data.Extensions;
using Intervals.NET.Domain.Abstractions;
using Intervals.NET.Extensions;
using SlidingWindowCache.Core.Rebalance.Execution;
using SlidingWindowCache.Core.Rebalance.Intent;
using SlidingWindowCache.Core.State;
using SlidingWindowCache.Infrastructure.Instrumentation;
using SlidingWindowCache.Public;
using SlidingWindowCache.Public.Dto;

namespace SlidingWindowCache.Core.UserPath;

/// <summary>
/// Handles user requests synchronously, serving data from cache or data source.
/// This is the Fast Path Actor that operates in the User Thread.
/// </summary>
/// <typeparam name="TRange">The type representing the range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <typeparam name="TDomain">The type representing the domain of the ranges.</typeparam>
/// <remarks>
/// <para><strong>Execution Context:</strong> User Thread</para>
/// <para><strong>Critical Contract:</strong></para>
/// <para>
/// Every user access produces a rebalance intent.
/// The UserRequestHandler NEVER invokes decision logic.
/// </para>
/// <para><strong>Responsibilities:</strong></para>
/// <list type="bullet">
/// <item><description>Handles user requests synchronously</description></item>
/// <item><description>Decides how to serve RequestedRange (from cache, from IDataSource, or mixed)</description></item>
/// <item><description>Assembles data for the requested range (from cache, IDataSource, or combined) without mutating cache state</description></item>
/// <item><description>Triggers rebalance intent (fire-and-forget)</description></item>
/// <item><description>Never blocks on rebalance</description></item>
/// </list>
/// <para><strong>Explicit Non-Responsibilities:</strong></para>
/// <list type="bullet">
/// <item><description>❌ NEVER checks NoRebalanceRange (belongs to DecisionEngine)</description></item>
/// <item><description>❌ NEVER computes DesiredCacheRange (belongs to GeometryPolicy)</description></item>
/// <item><description>❌ NEVER decides whether to rebalance (belongs to DecisionEngine)</description></item>
/// <item><description>❌ No cache normalization</description></item>
/// <item><description>❌ No trimming or shrinking</description></item>
/// </list>
/// </remarks>
internal sealed class UserRequestHandler<TRange, TData, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private readonly CacheState<TRange, TData, TDomain> _state;
    private readonly CacheDataExtensionService<TRange, TData, TDomain> _cacheExtensionService;
    private readonly IntentController<TRange, TData, TDomain> _intentController;
    private readonly IDataSource<TRange, TData> _dataSource;
    private readonly ICacheDiagnostics _cacheDiagnostics;

    // Disposal state tracking (lock-free using Interlocked)
    // 0 = not disposed, 1 = disposed
    private int _disposeState;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserRequestHandler{TRange,TData,TDomain}"/> class.
    /// </summary>
    /// <param name="state">The cache state.</param>
    /// <param name="cacheExtensionService">The cache data fetcher for extending cache coverage.</param>
    /// <param name="intentController">The intent controller for publishing rebalance intents.</param>
    /// <param name="dataSource"> The data source to request missing data from.</param>
    /// <param name="cacheDiagnostics">The diagnostics interface for recording cache metrics and events related to user requests.</param>
    public UserRequestHandler(CacheState<TRange, TData, TDomain> state,
        CacheDataExtensionService<TRange, TData, TDomain> cacheExtensionService,
        IntentController<TRange, TData, TDomain> intentController,
        IDataSource<TRange, TData> dataSource,
        ICacheDiagnostics cacheDiagnostics
    )
    {
        _state = state;
        _cacheExtensionService = cacheExtensionService;
        _intentController = intentController;
        _dataSource = dataSource;
        _cacheDiagnostics = cacheDiagnostics;
    }

    /// <summary>
    /// Handles a user request for the specified range.
    /// </summary>
    /// <param name="requestedRange">The range requested by the user.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains a 
    /// <see cref="RangeResult{TRange, TData}"/> with the actual available range and data.
    /// The Range may be null if no data is available, or a subset of requestedRange if truncated at boundaries.
    /// </returns>
    /// <remarks>
    /// <para>This method implements the User Path logic (READ-ONLY with respect to cache state):</para>
    /// <list type="number">
    /// <item><description>Check if requested range is fully or partially covered by cache</description></item>
    /// <item><description>Fetch missing data from IDataSource as needed</description></item>
    /// <item><description>Compute actual available range (intersection of requested and available)</description></item>
    /// <item><description>Materialize assembled data to array</description></item>
    /// <item><description>Publish rebalance intent with delivered data (fire-and-forget)</description></item>
    /// <item><description>Return RangeResult immediately</description></item>
    /// </list>
    /// <para><strong>CRITICAL: User Path is READ-ONLY</strong></para>
    /// <para>
    /// User Path NEVER writes to cache state. All cache mutations are performed exclusively
    /// by Rebalance Execution Path (single-writer architecture). The User Path:
    /// <list type="bullet">
    /// <item><description>✅ May READ from cache</description></item>
    /// <item><description>✅ May READ from IDataSource</description></item>
    /// <item><description>❌ NEVER writes to Cache (no Rematerialize calls)</description></item>
    /// <item><description>❌ NEVER writes to LastRequested</description></item>
    /// <item><description>❌ NEVER writes to NoRebalanceRange</description></item>
    /// </list>
    /// </para>
    /// <para><strong>Boundary Handling:</strong></para>
    /// <para>
    /// When DataSource has physical boundaries (e.g., database min/max IDs), the returned
    /// RangeResult.Range indicates what portion of the request was actually available.
    /// This allows graceful handling of out-of-bounds requests without exceptions.
    /// </para>
    /// </remarks>
    public async ValueTask<RangeResult<TRange, TData>> HandleRequestAsync(
        Range<TRange> requestedRange,
        CancellationToken cancellationToken)
    {
        // Check disposal state using Volatile.Read (lock-free)
        if (Volatile.Read(ref _disposeState) != 0)
        {
            throw new ObjectDisposedException(
                nameof(UserRequestHandler<TRange, TData, TDomain>),
                "Cannot handle request on a disposed handler.");
        }

        // Check if cache is cold (never used) - use ToRangeData to detect empty cache
        var cacheStorage = _state.Cache;
        var isColdStart = !_state.LastRequested.HasValue;

        RangeData<TRange, TData, TDomain>? assembledData = null;
        Range<TRange>? actualRange = null;
        ReadOnlyMemory<TData> resultData;

        try
        {
            if (isColdStart)
            {
                // Scenario 1: Cold Start
                // Cache has never been populated - fetch data ONLY for requested range
                _cacheDiagnostics.DataSourceFetchSingleRange();
                var fetchedChunk = await _dataSource.FetchAsync(requestedRange, cancellationToken);

                // Handle boundary: chunk.Range may be null or truncated
                if (fetchedChunk.Range.HasValue)
                {
                    assembledData = fetchedChunk.Data.ToRangeData(fetchedChunk.Range.Value, _state.Domain);
                    actualRange = fetchedChunk.Range.Value;
                    resultData = new ReadOnlyMemory<TData>(assembledData.Data.ToArray());
                }
                else
                {
                    // No data available for requested range
                    // Use default RangeData (empty)
                    assembledData = null;
                    actualRange = null;
                    resultData = ReadOnlyMemory<TData>.Empty;
                }

                _cacheDiagnostics.UserRequestFullCacheMiss();
            }
            else
            {
                var fullyInCache = cacheStorage.Range.Contains(requestedRange);

                if (fullyInCache)
                {
                    // Scenario 2: Full Cache Hit
                    // All requested data is available in cache - read from cache (no IDataSource call)
                    assembledData = cacheStorage.ToRangeData();

                    _cacheDiagnostics.UserRequestFullCacheHit();

                    // Return a requested range data using the cache storage's Read method, which may return a view or a copy depending on the strategy
                    resultData = cacheStorage.Read(requestedRange);
                    actualRange = requestedRange; // Fully in cache, so actual = requested
                }
                else
                {
                    var hasOverlap = cacheStorage.Range.Overlaps(requestedRange);

                    if (hasOverlap)
                    {
                        // Scenario 3: Partial Cache Hit
                        // RequestedRange intersects CurrentCacheRange - read from cache and fetch missing parts
                        // ExtendCacheAsync will compute missing ranges and fetch only those parts
                        // NOTE: The usage of storage.Read doesn't make sense here because we need to assemble a contiguous range that may require concatenating multiple segments (cached + fetched)
                        assembledData = await _cacheExtensionService.ExtendCacheAsync(
                            cacheStorage.ToRangeData(),
                            requestedRange,
                            cancellationToken
                        );

                        _cacheDiagnostics.UserRequestPartialCacheHit();

                        // Compute actual available range (intersection of requested and assembled)
                        // assembledData.Range may not fully cover requestedRange if DataSource returned truncated/null chunks
                        // (e.g., bounded source where some segments are unavailable)
                        actualRange = assembledData.Range.Intersect(requestedRange);

                        // Slice to the actual available range (may be smaller than requestedRange)
                        if (actualRange.HasValue)
                        {
                            var slicedData = assembledData[actualRange.Value];
                            resultData = new ReadOnlyMemory<TData>(slicedData.Data.ToArray());
                        }
                        else
                        {
                            // No actual intersection after extension (defensive fallback)
                            assembledData = default;
                            resultData = ReadOnlyMemory<TData>.Empty;
                        }
                    }
                    else
                    {
                        // Scenario 4: Full Cache Miss (Non-intersecting Jump)
                        // RequestedRange does NOT intersect CurrentCacheRange
                        // Fetch ONLY the requested range from IDataSource
                        // NOTE: The logic is similar to cold start
                        _cacheDiagnostics.DataSourceFetchSingleRange();
                        var fetchedChunk = await _dataSource.FetchAsync(requestedRange, cancellationToken).ConfigureAwait(false);

                        // Handle boundary: chunk.Range may be null or truncated
                        if (fetchedChunk.Range.HasValue)
                        {
                            assembledData = fetchedChunk.Data.ToRangeData(fetchedChunk.Range.Value, _state.Domain);
                            actualRange = fetchedChunk.Range.Value;
                            resultData = new ReadOnlyMemory<TData>(assembledData.Data.ToArray());
                        }
                        else
                        {
                            // No data available for requested range
                            // Use default RangeData (empty)
                            assembledData = default;
                            actualRange = null;
                            resultData = ReadOnlyMemory<TData>.Empty;
                        }

                        _cacheDiagnostics.UserRequestFullCacheMiss();
                    }
                }
            }
        }
        finally
        {
            // Always publish intent and increment counter, even for boundary misses.
            // This ensures:
            // 1. Consistent diagnostics - every user request is counted
            // 2. Rebalance system is aware of access patterns (even failed attempts)
            // 3. Intent represents "user requested this range" not "data was available"
            // 
            // Exception case: If assembledData is NULL due to exception during fetch,
            // we skip publishing to avoid triggering redundant failing rebalances.
            // In boundary miss case (actualRange = null), assembledData will be default(RangeData),
            // which is still a valid (empty) RangeData structure we can publish.
            var shouldPublishIntent = assembledData is not null || actualRange == null;
            
            if (shouldPublishIntent)
            {
                // For boundary misses (actualRange = null), assembledData is null, so use default(RangeData)
                // which represents an empty but valid RangeData structure (struct default is never null)
                var intentData = assembledData ?? default(RangeData<TRange, TData, TDomain>);
                var intent = new Intent<TRange, TData, TDomain>(requestedRange, intentData!);

                // Publish rebalance intent with assembled data range (fire-and-forget)
                // Rebalance Execution will use this as the authoritative source
                _intentController.PublishIntent(intent);

                _cacheDiagnostics.UserRequestServed();
            }
        }

        // Return RangeResult with actual available range and data
        return new RangeResult<TRange, TData>(actualRange, resultData);
    }

    /// <summary>
    /// Disposes the user request handler and releases all managed resources.
    /// Gracefully shuts down the intent controller.
    /// </summary>
    /// <returns>A ValueTask representing the asynchronous disposal operation.</returns>
    /// <remarks>
    /// <para><strong>Disposal Sequence:</strong></para>
    /// <list type="number">
    /// <item><description>Mark as disposed (prevents new user requests)</description></item>
    /// <item><description>Dispose intent controller (cascades to execution controller)</description></item>
    /// </list>
    /// <para><strong>Thread Safety:</strong></para>
    /// <para>
    /// This method is thread-safe and idempotent using lock-free Interlocked operations.
    /// Multiple concurrent calls will execute disposal only once.
    /// </para>
    /// </remarks>
    internal async ValueTask DisposeAsync()
    {
        // Idempotent check using lock-free Interlocked.CompareExchange
        if (Interlocked.CompareExchange(ref _disposeState, 1, 0) != 0)
        {
            return; // Already disposed
        }

        // Dispose intent controller (cascades to execution controller)
        await _intentController.DisposeAsync().ConfigureAwait(false);
    }
}