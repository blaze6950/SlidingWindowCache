using Intervals.NET;
using Intervals.NET.Data;
using Intervals.NET.Data.Extensions;
using Intervals.NET.Domain.Abstractions;
using Intervals.NET.Extensions;
using SlidingWindowCache.Infrastructure.Instrumentation;
using SlidingWindowCache.Public;
using SlidingWindowCache.Public.Dto;

namespace SlidingWindowCache.Core.Rebalance.Execution;

/// <summary>
/// Fetches missing data from the data source to extend the cache.
/// Does not perform trimming - that's the responsibility of the caller based on their context.
/// </summary>
/// <typeparam name="TRange">
/// The type representing the range boundaries. Must implement <see cref="IComparable{T}"/>.
/// </typeparam>
/// <typeparam name="TData">
/// The type of data being cached.
/// </typeparam>
/// <typeparam name="TDomain">
/// The type representing the domain of the ranges. Must implement <see cref="IRangeDomain{TRange}"/>.
/// </typeparam>
internal sealed class CacheDataFetcher<TRange, TData, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private readonly IDataSource<TRange, TData> _dataSource;
    private readonly TDomain _domain;

    /// <summary>
    /// Initializes a new instance of the <see cref="CacheDataFetcher{TRange, TData, TDomain}"/> class.
    /// </summary>
    /// <param name="dataSource">
    /// The data source from which to fetch data.
    /// </param>
    /// <param name="domain">
    /// The domain defining the range characteristics.
    /// </param>
    public CacheDataFetcher(
        IDataSource<TRange, TData> dataSource,
        TDomain domain
    )
    {
        _dataSource = dataSource;
        _domain = domain;
    }

    /// <summary>
    /// Extends the cache to cover the requested range by fetching only missing data segments.
    /// Preserves all existing cached data without trimming.
    /// </summary>
    /// <param name="current">The current cached data.</param>
    /// <param name="requested">The requested range that needs to be covered by the cache.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Extended cache containing all existing data plus newly fetched data to cover the requested range.
    /// </returns>
    /// <remarks>
    /// <para><strong>Operation:</strong> Extends cache to cover requested range (NO trimming of existing data).</para>
    /// <para><strong>Use case:</strong> User requests (GetDataAsync) where we want to preserve all cached data for future rebalancing.</para>
    /// <para><strong>Optimization:</strong> Only fetches data not already in cache (partial cache hit optimization).</para>
    /// <para><strong>Example:</strong></para>
    /// <code>
    /// Cache: [100, 200], Requested: [150, 250]
    /// - Already cached: [150, 200]
    /// - Missing (fetched): (200, 250]
    /// - Result: [100, 250] (ALL existing data preserved + newly fetched)
    /// 
    /// Later rebalance to [50, 300] can reuse [100, 250] without re-fetching!
    /// </code>
    /// </remarks>
    public async Task<RangeData<TRange, TData, TDomain>> ExtendCacheAsync(
        RangeData<TRange, TData, TDomain> current,
        Range<TRange> requested,
        CancellationToken ct
    )
    {
        CacheInstrumentationCounters.OnDataSourceFetchMissingSegments();

        // Step 1: Calculate which ranges are missing
        var missingRanges = CalculateMissingRanges(current.Range, requested);

        // Step 2: Fetch the missing data from data source
        var fetchedResults = await _dataSource.FetchAsync(missingRanges, ct);

        // Step 3: Union fetched data with current cache
        return UnionAll(current, fetchedResults, _domain);
    }

    /// <summary>
    /// Calculates which ranges are missing from the current cache to cover the requested range.
    /// Uses range intersection and subtraction to determine gaps.
    /// </summary>
    /// <param name="currentRange">The range currently covered by the cache.</param>
    /// <param name="requestedRange">The range that needs to be covered.</param>
    /// <returns>
    /// An enumerable of missing ranges that need to be fetched, or null if there's no intersection
    /// (meaning the entire requested range needs to be fetched).
    /// </returns>
    private static IEnumerable<Range<TRange>> CalculateMissingRanges(
        Range<TRange> currentRange,
        Range<TRange> requestedRange
    )
    {
        var intersection = currentRange.Intersect(requestedRange);

        if (intersection.HasValue)
        {
            // Calculate the missing segments using range subtraction
            return requestedRange.Except(intersection.Value);
        }

        // No overlap - indicate that entire requested range is missing
        // This signals to fetch the whole requested range without trying to calculate missing segments, as they are all missing.
        return [requestedRange];
    }

    /// <summary>
    /// Combines the existing cached data with the newly fetched data,
    /// ensuring that the resulting range data is correctly merged and consistent with the domain.
    /// </summary>
    private static RangeData<TRange, TData, TDomain> UnionAll(
        RangeData<TRange, TData, TDomain> current,
        IEnumerable<RangeChunk<TRange, TData>> rangeChunks,
        TDomain domain
    )
    {
        // Combine existing data with fetched data
        foreach (var (range, data) in rangeChunks)
        {
            // It is important to call Union on the current range data to overwrite outdated
            // intersected segments with the newly fetched data, ensuring that the most up-to-date
            // information is retained in the cache.
            current = current.Union(data.ToRangeData(range, domain))!;
        }

        return current;
    }

    /// <summary>
    /// Fetches data for the requested range without extending or merging with existing cache.
    /// Used for cold start or full cache replacement scenarios.
    /// </summary>
    /// <param name="requested">The range to fetch.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>New RangeData containing only the requested range.</returns>
    /// <remarks>
    /// <para><strong>Operation:</strong> Fetches ONLY the requested range (no merging with existing cache).</para>
    /// <para><strong>Use case:</strong> Cold start or non-intersecting requests (Invariant A.3.8, A.3.9b).</para>
    /// <para><strong>Example:</strong></para>
    /// <code>
    /// Cache: [100, 200], Requested: [300, 400] (no intersection)
    /// - Old cache is discarded per Invariant A.3.9b
    /// - Fetch: [300, 400]
    /// - Result: [300, 400] (old cache is NOT preserved)
    /// </code>
    /// </remarks>
    public async Task<RangeData<TRange, TData, TDomain>> FetchDataAsync(
        Range<TRange> requested,
        CancellationToken ct
    )
    {
        CacheInstrumentationCounters.OnDataSourceFetchFullRange();

        return (await _dataSource.FetchAsync(requested, ct)).ToRangeData(requested, _domain);
    }
}