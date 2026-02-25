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
internal sealed class CacheDataExtensionService<TRange, TData, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private readonly IDataSource<TRange, TData> _dataSource;
    private readonly TDomain _domain;
    private readonly ICacheDiagnostics _cacheDiagnostics;

    /// <summary>
    /// Initializes a new instance of the <see cref="CacheDataExtensionService{TRange,TData,TDomain}"/> class.
    /// </summary>
    /// <param name="dataSource">
    /// The data source from which to fetch data.
    /// </param>
    /// <param name="domain">
    /// The domain defining the range characteristics.
    /// </param>
    /// <param name="cacheDiagnostics">
    /// The diagnostics interface for recording cache operation metrics and events.
    /// </param>
    public CacheDataExtensionService(
        IDataSource<TRange, TData> dataSource,
        TDomain domain,
        ICacheDiagnostics cacheDiagnostics
    )
    {
        _dataSource = dataSource;
        _domain = domain;
        _cacheDiagnostics = cacheDiagnostics;
    }

    /// <summary>
    /// Extends the cache to cover the requested range by fetching only missing data segments.
    /// Preserves all existing cached data without trimming.
    /// </summary>
    /// <param name="currentCache">The current cached data.</param>
    /// <param name="requested">The requested range that needs to be covered by the cache.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Extended cache containing all existing data plus newly fetched data to cover the requested range.
    /// </returns>
    /// <remarks>
    /// <para><strong>Operation:</strong> Extends cache to cover requested range (NO trimming of existing data).</para>
    /// <para><strong>Use case:</strong> User requests (GetDataAsync) where we want to preserve all cached data for future rebalancing.</para>
    /// <para><strong>Optimization:</strong> Only fetches data not already in cache (partial cache hit optimization).</para>
    /// <para><strong>Note:</strong> This is an internal component that does not perform input validation or short-circuit checks. 
    /// All parameters are assumed to be pre-validated by the caller. Duplicating validation here would be unnecessary overhead.</para>
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
        RangeData<TRange, TData, TDomain> currentCache,
        Range<TRange> requested,
        CancellationToken ct
    )
    {
        _cacheDiagnostics.DataSourceFetchMissingSegments();

        // Step 1: Calculate which ranges are missing
        var missingRanges = CalculateMissingRanges(currentCache.Range, requested);

        // Step 2: Fetch the missing data from data source
        var fetchedResults = await _dataSource.FetchAsync(missingRanges, ct)
            .ConfigureAwait(false);

        // Step 3: Union fetched data with current cache (UnionAll will filter null ranges)
        return UnionAll(currentCache, fetchedResults, _domain);
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
    private IEnumerable<Range<TRange>> CalculateMissingRanges(
        Range<TRange> currentRange,
        Range<TRange> requestedRange
    )
    {
        var intersection = currentRange.Intersect(requestedRange);

        if (intersection.HasValue)
        {
            _cacheDiagnostics.CacheExpanded();
            // Calculate the missing segments using range subtraction
            return requestedRange.Except(intersection.Value);
        }

        _cacheDiagnostics.CacheReplaced();
        // No overlap - indicate that entire requested range is missing
        // This signals to fetch the whole requested range without trying to calculate missing segments, as they are all missing.
        return [requestedRange];
    }

    /// <summary>
    /// Combines the existing cached data with the newly fetched data,
    /// ensuring that the resulting range data is correctly merged and consistent with the domain.
    /// </summary>
    /// <remarks>
    /// <para><strong>Boundary Handling:</strong></para>
    /// <para>
    /// Segments with null Range (unavailable data from DataSource) are filtered out
    /// before union. This ensures cache only contains contiguous available data,
    /// preserving Invariant A.9a (Cache Contiguity).
    /// </para>
    /// <para>
    /// When DataSource returns RangeChunk with Range = null (e.g., request beyond database boundaries),
    /// those segments are skipped and do not affect the cache. The cache converges to maximum
    /// available data without gaps.
    /// </para>
    /// </remarks>
    private RangeData<TRange, TData, TDomain> UnionAll(
        RangeData<TRange, TData, TDomain> current,
        IEnumerable<RangeChunk<TRange, TData>> rangeChunks,
        TDomain domain
    )
    {
        // Combine existing data with fetched data
        foreach (var chunk in rangeChunks)
        {
            // Filter out segments with null ranges (unavailable data)
            // This preserves cache contiguity - only available data is stored
            if (!chunk.Range.HasValue)
            {
                _cacheDiagnostics.DataSegmentUnavailable();
                continue;
            }

            // It is important to call Union on the current range data to overwrite outdated
            // intersected segments with the newly fetched data, ensuring that the most up-to-date
            // information is retained in the cache.
            current = current.Union(chunk.Data.ToRangeData(chunk.Range!.Value, domain))!;
        }

        return current;
    }
}