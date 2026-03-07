using Intervals.NET.Caching.Dto;
using Intervals.NET.Caching.Infrastructure;
using Intervals.NET.Domain.Abstractions;

namespace Intervals.NET.Caching.Layered;

/// <summary>
/// Adapts an <see cref="IRangeCache{TRange,TData,TDomain}"/> instance to the
/// <see cref="IDataSource{TRange,TData}"/> interface, enabling any cache to serve as the
/// data source for another cache layer.
/// </summary>
/// <typeparam name="TRange">
/// The type representing range boundaries. Must implement <see cref="IComparable{T}"/>.
/// </typeparam>
/// <typeparam name="TData">
/// The type of data being cached.
/// </typeparam>
/// <typeparam name="TDomain">
/// The type representing the domain of the ranges. Must implement <see cref="IRangeDomain{TRange}"/>.
/// </typeparam>
/// <remarks>
/// <para><strong>Purpose:</strong></para>
/// <para>
/// This adapter is the composition point for building multi-layer (L1/L2/L3/...) caches.
/// It bridges the gap between <see cref="IRangeCache{TRange,TData,TDomain}"/> (the consumer API)
/// and <see cref="IDataSource{TRange,TData}"/> (the producer API), allowing any cache instance
/// to act as a backing store for a higher (closer-to-user) cache layer.
/// </para>
/// <para><strong>Data Flow:</strong></para>
/// <para>
/// When the outer (higher) cache needs to fetch data, it calls this adapter's
/// <see cref="FetchAsync(Range{TRange}, CancellationToken)"/> method. The adapter
/// delegates to the inner (deeper) cache's <see cref="IRangeCache{TRange,TData,TDomain}.GetDataAsync"/>,
/// which returns data from the inner cache's window. The <see cref="ReadOnlyMemory{T}"/> from
/// <see cref="RangeResult{TRange,TData}"/> is wrapped in a <see cref="ReadOnlyMemoryEnumerable{T}"/>
/// and passed directly as <see cref="RangeChunk{TRange,TData}.Data"/>, avoiding a temporary
/// <typeparamref name="TData"/>[] allocation proportional to the data range.
/// </para>
/// <para><strong>Consistency Model:</strong></para>
/// <para>
/// The adapter uses <c>GetDataAsync</c> (eventual consistency). Each layer manages its own
/// rebalance lifecycle independently. This is the correct model for layered caches: the user
/// always gets correct data immediately, and prefetch optimization happens asynchronously at each layer.
/// </para>
/// <para><strong>Lifecycle:</strong></para>
/// <para>
/// The adapter does NOT own the inner cache. It holds a reference but does not dispose it.
/// Lifecycle management is the responsibility of the caller (typically
/// <see cref="LayeredRangeCacheBuilder{TRange,TData,TDomain}"/> via <see cref="LayeredRangeCache{TRange,TData,TDomain}"/>).
/// </para>
/// </remarks>
public sealed class RangeCacheDataSourceAdapter<TRange, TData, TDomain>
    : IDataSource<TRange, TData>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private readonly IRangeCache<TRange, TData, TDomain> _innerCache;

    /// <summary>
    /// Initializes a new instance of <see cref="RangeCacheDataSourceAdapter{TRange,TData,TDomain}"/>.
    /// </summary>
    /// <param name="innerCache">
    /// The cache instance to adapt as a data source. Must not be null.
    /// The adapter does not take ownership; the caller is responsible for disposal.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="innerCache"/> is null.
    /// </exception>
    public RangeCacheDataSourceAdapter(IRangeCache<TRange, TData, TDomain> innerCache)
    {
        _innerCache = innerCache ?? throw new ArgumentNullException(nameof(innerCache));
    }

    /// <summary>
    /// Fetches data for the specified range from the inner cache.
    /// </summary>
    /// <param name="range">The range for which to fetch data.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
    /// <returns>
    /// A <see cref="RangeChunk{TRange,TData}"/> containing the data available in the inner cache
    /// for the requested range.
    /// </returns>
    public async Task<RangeChunk<TRange, TData>> FetchAsync(
        Range<TRange> range,
        CancellationToken cancellationToken)
    {
        var result = await _innerCache.GetDataAsync(range, cancellationToken).ConfigureAwait(false);
        return new RangeChunk<TRange, TData>(result.Range, new ReadOnlyMemoryEnumerable<TData>(result.Data));
    }
}
