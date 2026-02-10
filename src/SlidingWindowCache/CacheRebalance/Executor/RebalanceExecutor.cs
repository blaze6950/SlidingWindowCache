using Intervals.NET;
using Intervals.NET.Data;
using Intervals.NET.Domain.Abstractions;
using SlidingWindowCache.CacheRebalance.Policy;

namespace SlidingWindowCache.CacheRebalance.Executor;

/// <summary>
/// Executes rebalance operations by fetching missing data, merging with existing cache,
/// and trimming to the desired range. This is the sole component responsible for cache normalization.
/// </summary>
/// <typeparam name="TRange">The type representing the range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <typeparam name="TDomain">The type representing the domain of the ranges.</typeparam>
/// <remarks>
/// <para><strong>Execution Context:</strong> Background / ThreadPool</para>
/// <para><strong>Characteristics:</strong> Asynchronous, cancellable, heavyweight</para>
/// <para><strong>Responsibility:</strong> Cache normalization (expand, trim, recompute NoRebalanceRange)</para>
/// </remarks>
internal sealed class RebalanceExecutor<TRange, TData, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private readonly CacheState<TRange, TData, TDomain> _state;
    private readonly CacheDataFetcher<TRange, TData, TDomain> _cacheFetcher;
    private readonly ThresholdRebalancePolicy<TRange, TDomain> _rebalancePolicy;

    public RebalanceExecutor(
        CacheState<TRange, TData, TDomain> state,
        CacheDataFetcher<TRange, TData, TDomain> cacheFetcher,
        ThresholdRebalancePolicy<TRange, TDomain> rebalancePolicy)
    {
        _state = state;
        _cacheFetcher = cacheFetcher;
        _rebalancePolicy = rebalancePolicy;
    }

    /// <summary>
    /// Executes rebalance by normalizing the cache to the desired range.
    /// This is the ONLY component that mutates cache state (single-writer architecture).
    /// </summary>
    /// <param name="deliveredData">The data that was actually delivered to the user for the requested range.</param>
    /// <param name="desiredRange">The target cache range to normalize to.</param>
    /// <param name="cancellationToken">Cancellation token to support cancellation at all stages.</param>
    /// <returns>A task representing the asynchronous rebalance operation.</returns>
    /// <remarks>
    /// <para>
    /// This executor is the sole writer of all cache state including:
    /// <list type="bullet">
    /// <item><description>Cache.Rematerialize (cache data and range)</description></item>
    /// <item><description>LastRequested field</description></item>
    /// <item><description>NoRebalanceRange field</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// The delivered data from the intent is used as the authoritative base source,
    /// avoiding duplicate fetches and ensuring consistency with what the user received.
    /// </para>
    /// </remarks>
    public async Task ExecuteAsync(
        RangeData<TRange, TData, TDomain> deliveredData,
        Range<TRange> desiredRange,
        CancellationToken cancellationToken)
    {
        // Use delivered data as the base - this is what the user received
        var baseData = deliveredData;

        // Check if desired range equals delivered data range (Decision Path D2)
        // This is a final check before expensive I/O operations
        if (deliveredData.Range == desiredRange)
        {
#if DEBUG
            Instrumentation.CacheInstrumentationCounters.OnRebalanceSkippedSameRange();
#endif
            // Even though ranges match, we still need to update cache state since
            // User Path no longer writes to cache. Use delivered data directly.
            // Skip to cache state update without I/O.
            goto UpdateCacheState;
        }

        // Cancellation check after decision but before expensive I/O
        // Satisfies Invariant 34a: "Rebalance Execution MUST yield to User Path requests immediately"
        cancellationToken.ThrowIfCancellationRequested();

        // Phase 1: Extend delivered data to cover desired range (fetch only truly missing data)
        // Use delivered data as base instead of current cache to ensure consistency
        var extended = await _cacheFetcher.ExtendCacheAsync(baseData, desiredRange, cancellationToken);

        // Cancellation check after I/O but before mutation
        // If User Path cancelled us, don't apply the rebalance result
        cancellationToken.ThrowIfCancellationRequested();

        // Phase 2: Trim to desired range (rebalancing-specific: discard data outside desired range)
        baseData = extended[desiredRange];

        // Final cancellation check before applying mutation
        // Ensures we don't apply obsolete rebalance results
        cancellationToken.ThrowIfCancellationRequested();

        UpdateCacheState:
        // Phase 3: Update the cache with the rebalanced data (atomic mutation)
        // SINGLE-WRITER: This is the ONLY place where cache state is written
        _state.Cache.Rematerialize(baseData);

        // Phase 4: Update LastRequested to the original user's requested range
        // SINGLE-WRITER: Only Rebalance Execution writes to LastRequested
        _state.LastRequested = baseData.Range;

        // Phase 5: Update the no-rebalance range to prevent unnecessary rebalancing
        // SINGLE-WRITER: Only Rebalance Execution writes to NoRebalanceRange
        _state.NoRebalanceRange = _rebalancePolicy.GetNoRebalanceRange(_state.Cache.Range);
    }
}