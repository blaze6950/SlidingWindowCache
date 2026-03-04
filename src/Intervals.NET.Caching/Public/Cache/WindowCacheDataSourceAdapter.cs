using Intervals.NET;
using Intervals.NET.Domain.Abstractions;
using Intervals.NET.Caching.Infrastructure.Collections;
using Intervals.NET.Caching.Public.Dto;

namespace Intervals.NET.Caching.Public.Cache;

/// <summary>
/// Adapts an <see cref="IWindowCache{TRange,TData,TDomain}"/> instance to the
/// <see cref="IDataSource{TRange,TData}"/> interface, enabling it to serve as the
/// data source for another <see cref="WindowCache{TRange,TData,TDomain}"/>.
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
/// It bridges the gap between <see cref="IWindowCache{TRange,TData,TDomain}"/> (the consumer API)
/// and <see cref="IDataSource{TRange,TData}"/> (the producer API), allowing any cache instance
/// to act as a backing store for a higher (closer-to-user) cache layer.
/// </para>
/// <para><strong>Data Flow:</strong></para>
/// <para>
/// When the outer (higher) cache needs to fetch data, it calls this adapter's
/// <see cref="FetchAsync(Range{TRange}, CancellationToken)"/> method. The adapter
/// delegates to the inner (deeper) cache's <see cref="IWindowCache{TRange,TData,TDomain}.GetDataAsync"/>,
/// which returns data from the inner cache's window (possibly triggering a background rebalance
/// in the inner cache). The <see cref="ReadOnlyMemory{T}"/> from <see cref="RangeResult{TRange,TData}"/>
/// is wrapped in a <see cref="ReadOnlyMemoryEnumerable{T}"/> and passed directly as
/// <see cref="RangeChunk{TRange,TData}.Data"/>, avoiding a temporary <typeparamref name="TData"/>[]
/// allocation proportional to the data range.
/// </para>
/// <para><strong>Consistency Model:</strong></para>
/// <para>
/// The adapter uses <c>GetDataAsync</c> (eventual consistency), not <c>GetDataAndWaitForIdleAsync</c>.
/// Each layer manages its own rebalance lifecycle independently. The inner cache converges to its
/// optimal window in the background; the outer cache does not block waiting for it.
/// This is the correct model for layered caches: the user always gets correct data immediately,
/// and prefetch optimization happens asynchronously at each layer.
/// </para>
/// <para><strong>Boundary Semantics:</strong></para>
/// <para>
/// Boundary signals from the inner cache are correctly propagated. When
/// <see cref="RangeResult{TRange,TData}.Range"/> is <see langword="null"/> (no data available),
/// the adapter returns a <see cref="RangeChunk{TRange,TData}"/> with a <see langword="null"/> Range,
/// following the <see cref="IDataSource{TRange,TData}"/> contract for bounded data sources.
/// </para>
/// <para><strong>Lifecycle:</strong></para>
/// <para>
/// The adapter does NOT own the inner cache. It holds a reference but does not dispose it.
/// Lifecycle management is the responsibility of the caller. When using
/// <see cref="LayeredWindowCacheBuilder{TRange,TData,TDomain}"/>, the resulting
/// <see cref="LayeredWindowCache{TRange,TData,TDomain}"/> owns and disposes all layers.
/// </para>
/// <para><strong>Typical Usage (via Builder):</strong></para>
/// <code>
/// await using var cache = WindowCacheBuilder.Layered(realDataSource, domain)
///     .AddLayer(new WindowCacheOptions(10.0, 10.0, UserCacheReadMode.CopyOnRead, 0.3, 0.3))
///     .AddLayer(new WindowCacheOptions(0.5, 0.5, UserCacheReadMode.Snapshot))
///     .Build();
///
/// var data = await cache.GetDataAsync(range, ct);
/// </code>
/// <para><strong>Manual Usage:</strong></para>
/// <code>
/// // Innermost layer — reads from real data source
/// var innerCache = new WindowCache&lt;int, byte[], IntegerFixedStepDomain&gt;(
///     realDataSource, domain,
///     new WindowCacheOptions(10.0, 10.0, UserCacheReadMode.CopyOnRead));
///
/// // Adapt inner cache as a data source for the outer layer
/// var adapter = new WindowCacheDataSourceAdapter&lt;int, byte[], IntegerFixedStepDomain&gt;(innerCache);
///
/// // Outermost layer — reads from the inner cache via adapter
/// var outerCache = new WindowCache&lt;int, byte[], IntegerFixedStepDomain&gt;(
///     adapter, domain,
///     new WindowCacheOptions(0.5, 0.5, UserCacheReadMode.Snapshot));
/// </code>
/// </remarks>
public sealed class WindowCacheDataSourceAdapter<TRange, TData, TDomain>
    : IDataSource<TRange, TData>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private readonly IWindowCache<TRange, TData, TDomain> _innerCache;

    /// <summary>
    /// Initializes a new instance of <see cref="WindowCacheDataSourceAdapter{TRange,TData,TDomain}"/>.
    /// </summary>
    /// <param name="innerCache">
    /// The cache instance to adapt as a data source. Must not be null.
    /// The adapter does not take ownership; the caller is responsible for disposal.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="innerCache"/> is null.
    /// </exception>
    public WindowCacheDataSourceAdapter(IWindowCache<TRange, TData, TDomain> innerCache)
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
    /// for the requested range. The chunk's <c>Range</c> may be a subset of or equal to
    /// <paramref name="range"/> (following inner cache boundary semantics), or <see langword="null"/>
    /// if no data is available.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Delegates to <see cref="IWindowCache{TRange,TData,TDomain}.GetDataAsync"/>, which may
    /// also trigger a background rebalance in the inner cache (eventual consistency).
    /// </para>
    /// <para>
    /// The <see cref="ReadOnlyMemory{T}"/> returned by the inner cache is wrapped in a
    /// <see cref="ReadOnlyMemoryEnumerable{T}"/>, avoiding a temporary <typeparamref name="TData"/>[]
    /// allocation proportional to the data range. The wrapper holds only a reference to the
    /// existing backing array via <see cref="ReadOnlyMemory{T}"/>, keeping it reachable for the
    /// lifetime of the enumerable. Enumeration is deferred: the data is read lazily when the
    /// outer cache's rebalance path materializes the <see cref="RangeChunk{TRange,TData}.Data"/>
    /// sequence (a single pass).
    /// </para>
    /// </remarks>
    public async Task<RangeChunk<TRange, TData>> FetchAsync(
        Range<TRange> range,
        CancellationToken cancellationToken)
    {
        var result = await _innerCache.GetDataAsync(range, cancellationToken).ConfigureAwait(false);
        return new RangeChunk<TRange, TData>(result.Range, new ReadOnlyMemoryEnumerable<TData>(result.Data));
    }
}
