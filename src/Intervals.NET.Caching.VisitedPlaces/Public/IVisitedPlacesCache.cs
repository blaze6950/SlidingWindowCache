using Intervals.NET.Domain.Abstractions;

namespace Intervals.NET.Caching.VisitedPlaces.Public;

/// <summary>
/// Represents a visited places cache that stores and retrieves data for arbitrary,
/// non-contiguous ranges with pluggable eviction.
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
/// <remarks>
/// <para><strong>Non-Contiguous Storage:</strong></para>
/// <para>
/// Unlike a sliding window cache, the visited places cache stores independently-fetched segments
/// as separate, non-contiguous entries. Gaps between segments are explicitly permitted. No merging occurs.
/// </para>
/// <para><strong>Eventual Consistency:</strong></para>
/// <para>
/// <see cref="IRangeCache{TRange,TData,TDomain}.GetDataAsync"/> returns immediately after assembling
/// the response and publishing a background event. Statistics updates, segment storage, and eviction
/// all happen asynchronously. Use <see cref="IRangeCache{TRange,TData,TDomain}.WaitForIdleAsync"/>
/// or the shared <c>GetDataAndWaitForIdleAsync</c> extension for strong consistency.
/// </para>
/// <para><strong>Resource Management:</strong></para>
/// <para>
/// VisitedPlacesCache manages background processing tasks and resources that require explicit disposal.
/// Always call <see cref="IAsyncDisposable.DisposeAsync"/> when done using the cache instance.
/// </para>
/// <para><strong>Usage Pattern:</strong></para>
/// <code>
/// await using var cache = VisitedPlacesCacheBuilder
///     .For(dataSource, domain)
///     .WithOptions(o => o.WithStorageStrategy(StorageStrategy.SnapshotAppendBuffer))
///     .WithEviction(
///         evaluators: [new MaxSegmentCountEvaluator(maxCount: 100)],
///         executor: new LruEvictionExecutor&lt;int, MyData&gt;())
///     .Build();
/// var result = await cache.GetDataAsync(range, cancellationToken);
/// </code>
/// </remarks>
public interface IVisitedPlacesCache<TRange, TData, TDomain> : IRangeCache<TRange, TData, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
}
