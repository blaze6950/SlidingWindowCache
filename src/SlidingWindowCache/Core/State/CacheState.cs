using Intervals.NET;
using Intervals.NET.Domain.Abstractions;
using SlidingWindowCache.Infrastructure.Storage;

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
/// <remarks>
/// <para><strong>Single-Writer Architecture:</strong></para>
/// <para>
/// All mutations to this state MUST go through <see cref="UpdateCacheState"/> which is the
/// sole method that writes to the three mutable fields. This enforces the Single-Writer invariant:
/// only Rebalance Execution (via <c>RebalanceExecutor</c>) may mutate cache state.
/// The User Path is strictly read-only with respect to all fields on this class.
/// </para>
/// </remarks>
internal sealed class CacheState<TRange, TData, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    /// <summary>
    /// The current cached data along with its range.
    /// </summary>
    public ICacheStorage<TRange, TData, TDomain> Storage { get; }

    /// <summary>
    /// Indicates whether the cache has been populated at least once (i.e., a rebalance execution
    /// has completed successfully at least once).
    /// </summary>
    /// <remarks>
    /// SINGLE-WRITER: Only Rebalance Execution Path may write to this field, via <see cref="UpdateCacheState"/>.
    /// User Path is read-only with respect to cache state.
    /// <c>false</c> means the cache is in a cold/uninitialized state; <c>true</c> means it has
    /// been populated at least once and the User Path may read from the storage.
    /// </remarks>
    public bool IsInitialized { get; private set; }

    /// <summary>
    /// The range within which no rebalancing should occur.
    /// It is based on configured threshold policies.
    /// </summary>
    /// <remarks>
    /// SINGLE-WRITER: Only Rebalance Execution Path may write to this field, via <see cref="UpdateCacheState"/>.
    /// This field is recomputed after each successful rebalance execution.
    /// </remarks>
    public Range<TRange>? NoRebalanceRange { get; private set; }

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
        Storage = cacheStorage;
        Domain = domain;
    }

    /// <summary>
    /// Applies a complete cache state mutation atomically.
    /// This is the ONLY method that may write to the mutable fields on this class.
    /// </summary>
    /// <param name="normalizedData">The normalized range data to write into storage.</param>
    /// <param name="noRebalanceRange">The pre-computed no-rebalance range for the new state.</param>
    /// <remarks>
    /// <para><strong>Single-Writer Contract:</strong></para>
    /// <para>
    /// MUST only be called from Rebalance Execution context (i.e., <c>RebalanceExecutor.UpdateCacheState</c>).
    /// The execution controller guarantees that no two rebalance executions run concurrently,
    /// so no additional synchronization is needed here.
    /// </para>
    /// </remarks>
    internal void UpdateCacheState(
        Intervals.NET.Data.RangeData<TRange, TData, TDomain> normalizedData,
        Range<TRange>? noRebalanceRange)
    {
        Storage.Rematerialize(normalizedData);
        IsInitialized = true;
        NoRebalanceRange = noRebalanceRange;
    }
}
