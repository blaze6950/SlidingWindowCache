using Intervals.NET;
using Intervals.NET.Domain.Abstractions;
using SlidingWindowCache.Core.Planning;
using SlidingWindowCache.Core.Rebalance.Intent;

namespace SlidingWindowCache.Core.Rebalance.Decision;

/// <summary>
/// Evaluates whether rebalance execution is required based on cache geometry policy.
/// This component lives strictly in the background execution context and is never
/// invoked directly by the User Path.
/// </summary>
/// <typeparam name="TRange">The type representing the range boundaries.</typeparam>
/// <typeparam name="TDomain">The type representing the domain of the ranges.</typeparam>
/// <remarks>
/// <para><strong>Execution Context:</strong> Background / ThreadPool</para>
/// <para><strong>Visibility:</strong> Not visible to User Path, invoked only by RebalanceScheduler</para>
/// <para><strong>Characteristics:</strong> Pure, deterministic, side-effect free</para>
/// </remarks>
internal sealed class RebalanceDecisionEngine<TRange, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private readonly ThresholdRebalancePolicy<TRange, TDomain> _policy;
    private readonly ProportionalRangePlanner<TRange, TDomain> _planner;

    public RebalanceDecisionEngine(
        ThresholdRebalancePolicy<TRange, TDomain> policy,
        ProportionalRangePlanner<TRange, TDomain> planner)
    {
        _policy = policy;
        _planner = planner;
    }

    /// <summary>
    /// Evaluates whether rebalance execution should proceed based on the requested range
    /// and current cache state.
    /// </summary>
    /// <param name="requestedRange">The range requested by the user.</param>
    /// <param name="noRebalanceRange">The range within which no rebalancing should occur.</param>
    /// <returns>A decision indicating whether to execute rebalance and the desired range if applicable.</returns>
    public RebalanceDecision<TRange> ShouldExecuteRebalance(
        Range<TRange> requestedRange,
        Range<TRange>? noRebalanceRange)
    {
        // Decision Path D1: Check NoRebalanceRange (fast path)
        // If RequestedRange is fully contained within NoRebalanceRange, skip rebalancing
        if (noRebalanceRange.HasValue &&
            !_policy.ShouldRebalance(noRebalanceRange.Value, requestedRange))
        {
            return RebalanceDecision<TRange>.Skip();
        }

        // Decision Path D2/D3: Compute DesiredCacheRange
        var desiredRange = _planner.Plan(requestedRange);

        // Decision is to execute - IntentManager will check if desiredRange differs from current
        // before actually invoking the executor
        return RebalanceDecision<TRange>.Execute(desiredRange);
    }
}
