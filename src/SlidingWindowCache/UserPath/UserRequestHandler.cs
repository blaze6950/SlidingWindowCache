using Intervals.NET;
using Intervals.NET.Data;
using Intervals.NET.Domain.Abstractions;
using Intervals.NET.Extensions;
using SlidingWindowCache.CacheRebalance;
using SlidingWindowCache.CacheRebalance.Executor;

namespace SlidingWindowCache.UserPath;

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
/// <item><description>Updates LastRequestedRange and CacheData/CurrentCacheRange only to cover RequestedRange</description></item>
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
    private readonly CacheDataFetcher<TRange, TData, TDomain> _cacheFetcher;
    private readonly IntentController<TRange, TData, TDomain> _intentManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserRequestHandler{TRange,TData,TDomain}"/> class.
    /// </summary>
    /// <param name="state">The cache state.</param>
    /// <param name="cacheFetcher">The cache data fetcher for extending cache coverage.</param>
    /// <param name="intentManager">The intent controller for publishing rebalance intents.</param>
    public UserRequestHandler(
        CacheState<TRange, TData, TDomain> state,
        CacheDataFetcher<TRange, TData, TDomain> cacheFetcher,
        IntentController<TRange, TData, TDomain> intentManager)
    {
        _state = state;
        _cacheFetcher = cacheFetcher;
        _intentManager = intentManager;
    }

    /// <summary>
    /// Handles a user request for the specified range.
    /// </summary>
    /// <param name="requestedRange">The range requested by the user.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains a <see cref="ReadOnlyMemory{T}"/>
    /// of data for the specified range from the materialized cache.
    /// </returns>
    /// <remarks>
    /// <para>This method implements the User Path logic (READ-ONLY with respect to cache state):</para>
    /// <list type="number">
    /// <item><description>Cancel any pending/ongoing rebalance (Invariant A.0: User Path priority)</description></item>
    /// <item><description>Check if requested range is fully or partially covered by cache</description></item>
    /// <item><description>Fetch missing data from IDataSource as needed</description></item>
    /// <item><description>Materialize assembled data to array</description></item>
    /// <item><description>Return ReadOnlyMemory to user immediately</description></item>
    /// <item><description>Publish rebalance intent with delivered data (fire-and-forget)</description></item>
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
    /// </remarks>
    public async ValueTask<ReadOnlyMemory<TData>> HandleRequestAsync(
        Range<TRange> requestedRange,
        CancellationToken cancellationToken)
    {
        // CRITICAL: Cancel any pending/ongoing rebalance FIRST (Invariant A.0: User Path priority)
        // This ensures rebalance execution doesn't interfere even though User Path no longer mutates
        _intentManager.CancelPendingRebalance();

        // Check if cache is cold (never used) - use ToRangeData to detect empty cache
        var currentCacheData = _state.Cache.ToRangeData();
        var isColdStart = !_state.LastRequested.HasValue;

        RangeData<TRange, TData, TDomain> assembledData;

        if (isColdStart)
        {
            // Scenario 1: Cold Start
            // Cache has never been populated - fetch data ONLY for requested range
            assembledData = await _cacheFetcher.FetchDataAsync(requestedRange, cancellationToken);
            Instrumentation.CacheInstrumentationCounters.OnUserRequestFullCacheMiss();
        }
        else
        {
            var currentCacheRange = _state.Cache.Range;
            var fullyInCache = currentCacheRange.Contains(requestedRange);

            if (fullyInCache)
            {
                // Scenario 2: Full Cache Hit
                // All requested data is available in cache - read from cache (no IDataSource call)
                var cachedData = _state.Cache.Read(requestedRange);

                // Create RangeData from cached data for intent
                // Note: We must materialize to array to create proper RangeData for intent
                var array = cachedData.ToArray();
                assembledData = new RangeData<TRange, TData, TDomain>(requestedRange, array, _state.Domain);
                Instrumentation.CacheInstrumentationCounters.OnUserRequestFullCacheHit();
            }
            else
            {
                var hasIntersection = currentCacheData.Range.Intersect(requestedRange).HasValue;

                if (hasIntersection)
                {
                    // Scenario 3: Partial Cache Hit
                    // RequestedRange intersects CurrentCacheRange - read from cache and fetch missing parts
                    // ExtendCacheAsync will compute missing ranges and fetch only those parts
                    var extendedData = await _cacheFetcher.ExtendCacheAsync(currentCacheData, requestedRange, cancellationToken);

                    // Slice to requested range only (ExtendCacheAsync returns union of cache + requested)
                    assembledData = extendedData[requestedRange];
                    Instrumentation.CacheInstrumentationCounters.OnUserRequestPartialCacheHit();
                }
                else
                {
                    // Scenario 4: Full Cache Miss (Non-intersecting Jump)
                    // RequestedRange does NOT intersect CurrentCacheRange
                    // Fetch ONLY the requested range from IDataSource
                    assembledData = await _cacheFetcher.FetchDataAsync(requestedRange, cancellationToken);
                    Instrumentation.CacheInstrumentationCounters.OnUserRequestFullCacheMiss();
                }
            }
        }

        // CRITICAL: Materialize assembled data to array
        // This serves two purposes:
        // 1. Create ReadOnlyMemory<TData> to return to user
        // 2. Create RangeData<TRange,TData,TDomain> for intent
        // Note: assembledData.Data is IEnumerable, must materialize to array
        var materializedArray = assembledData.Data.ToArray();

        // Create ReadOnlyMemory to return to user immediately
        var result = new ReadOnlyMemory<TData>(materializedArray);

        // Create RangeData for intent using the same materialized array
        var deliveredData = new RangeData<TRange, TData, TDomain>(
            requestedRange,
            materializedArray,
            _state.Domain);

        // Publish rebalance intent with delivered data (fire-and-forget)
        // The intent contains both the requested range and the actual data delivered to the user
        // Rebalance Execution will use this as the authoritative source
        _intentManager.PublishIntent(deliveredData);

        Instrumentation.CacheInstrumentationCounters.OnUserRequestServed();

        // Return the data immediately (User Path never waits for rebalance)
        return result;
    }
}