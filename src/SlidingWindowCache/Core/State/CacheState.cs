using Intervals.NET;
using Intervals.NET.Domain.Abstractions;
using SlidingWindowCache.Infrastructure.Storage;
using SlidingWindowCache.Public;

namespace SlidingWindowCache.Core.State;

/// <summary>
/// Encapsulates the mutable state of a window cache.
/// This class is shared between <see cref="WindowCache{TRange,TData,TDomain}"/> and its internal
/// rebalancing components, providing clear ownership semantics.
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
internal sealed class CacheState<TRange, TData, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    /// <summary>
    /// The current cached data along with its range.
    /// </summary>
    public ICacheStorage<TRange, TData, TDomain> Cache { get; }

    /// <summary>
    /// The last requested range that triggered a cache access.
    /// </summary>
    /// <remarks>
    /// SINGLE-WRITER: Only Rebalance Execution Path may write to this field.
    /// User Path is read-only with respect to cache state.
    /// </remarks>
    public Range<TRange>? LastRequested { get; internal set; }

    /// <summary>
    /// The range within which no rebalancing should occur.
    /// It is based on configured threshold policies.
    /// </summary>
    /// <remarks>
    /// SINGLE-WRITER: Only Rebalance Execution Path may write to this field.
    /// This field is recomputed after each successful rebalance execution.
    /// </remarks>
    public Range<TRange>? NoRebalanceRange { get; internal set; }

    /// <summary>
    /// Gets the domain defining the range characteristics for this cache instance.
    /// </summary>
    public TDomain Domain { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="CacheState{TRange,TData,TDomain}"/> class.
    /// </summary>
    /// <param name="cacheStorage">The cache storage implementation.</param>
    /// <param name="domain">The domain defining the range characteristics.</param>
    public CacheState(ICacheStorage<TRange, TData, TDomain> cacheStorage, TDomain domain)
    {
        Cache = cacheStorage;
        Domain = domain;
    }
}