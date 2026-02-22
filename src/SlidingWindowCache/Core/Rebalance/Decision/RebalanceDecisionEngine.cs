using Intervals.NET;
using Intervals.NET.Domain.Abstractions;
using SlidingWindowCache.Core.Planning;
using SlidingWindowCache.Core.Rebalance.Execution;
using SlidingWindowCache.Core.State;

namespace SlidingWindowCache.Core.Rebalance.Decision;

/// <summary>
/// Evaluates whether rebalance execution is required based on cache geometry policy.
/// This is the SOLE AUTHORITY for rebalance necessity determination.
/// </summary>
/// <typeparam name="TRange">The type representing the range boundaries.</typeparam>
/// <typeparam name="TDomain">The type representing the domain of the ranges.</typeparam>
/// <remarks>
/// <para><strong>Execution Context:</strong> Background Thread (Intent Processing Loop)</para>
/// <para>
/// This component executes in the background intent processing loop of <see cref="IntentController{TRange,TData,TDomain}"/>.
/// Invoked synchronously within loop iteration after user thread signals intent via semaphore.
/// Decision logic is CPU-only, side-effect free, and lightweight (completes in microseconds).
/// This architecture enables burst resistance and work avoidance without blocking user requests.
/// </para>
/// <para><strong>Visibility:</strong> Not visible to external users, owned and invoked by IntentController</para>
/// <para><strong>Invocation:</strong> Called synchronously within the background intent processing loop of <see cref="IntentController{TRange,TData,TDomain}"/> after a semaphore signal from <see cref="IntentController{TRange,TData,TDomain}.PublishIntent"/></para>
/// <para><strong>Characteristics:</strong> Pure, deterministic, side-effect free, CPU-only (no I/O)</para>
/// <para><strong>Decision Pipeline (5 Stages):</strong></para>
/// <list type="number">
/// <item><description>Stage 1: Current Cache NoRebalanceRange stability check (fast path work avoidance)</description></item>
/// <item><description>Stage 2: Pending Rebalance NoRebalanceRange stability check (anti-thrashing)</description></item>
/// <item><description>Stage 3: Compute DesiredCacheRange and DesiredNoRebalanceRange</description></item>
/// <item><description>Stage 4: Equality short-circuit (DesiredRange == CurrentRange - no-op prevention)</description></item>
/// <item><description>Stage 5: Rebalance required - return full decision</description></item>
/// </list>
/// <para><strong>Smart Eventual Consistency:</strong></para>
/// <para>
/// Enables work avoidance through multi-stage validation. Prevents thrashing, reduces redundant I/O,
/// and maintains stability under rapidly changing access patterns while ensuring eventual convergence.
/// </para>
/// </remarks>
internal sealed class RebalanceDecisionEngine<TRange, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private readonly ThresholdRebalancePolicy<TRange, TDomain> _policy;
    private readonly ProportionalRangePlanner<TRange, TDomain> _planner;
    private readonly NoRebalanceRangePlanner<TRange, TDomain> _noRebalancePlanner;

    public RebalanceDecisionEngine(
        ThresholdRebalancePolicy<TRange, TDomain> policy,
        ProportionalRangePlanner<TRange, TDomain> planner,
        NoRebalanceRangePlanner<TRange, TDomain> noRebalancePlanner)
    {
        _policy = policy;
        _planner = planner;
        _noRebalancePlanner = noRebalancePlanner;
    }

    /// <summary>
    /// Evaluates whether rebalance execution should proceed based on multi-stage validation.
    /// This is the SOLE AUTHORITY for rebalance necessity determination.
    /// </summary>
    /// <param name="requestedRange">The range requested by the user.</param>
    /// <param name="currentCacheState">The current cache state snapshot.</param>
    /// <param name="lastExecutionRequest">The last rebalance execution request, if any.</param>
    /// <returns>A decision indicating whether to schedule rebalance with explicit reasoning.</returns>
    /// <remarks>
    /// <para><strong>Multi-Stage Validation Pipeline:</strong></para>
    /// <para>
    /// Each stage acts as a guard, potentially short-circuiting execution.
    /// All stages must confirm necessity before rebalance is scheduled.
    /// </para>
    /// </remarks>
    public RebalanceDecision<TRange> Evaluate<TData>(
        Range<TRange> requestedRange,
        CacheState<TRange, TData, TDomain> currentCacheState,
        ExecutionRequest<TRange, TData, TDomain>? lastExecutionRequest)
    {
        // Stage 1: Current Cache Stability Check (fast path)
        // If requested range is fully contained within current NoRebalanceRange, skip rebalancing
        if (currentCacheState.NoRebalanceRange.HasValue &&
            !_policy.ShouldRebalance(currentCacheState.NoRebalanceRange.Value, requestedRange))
        {
            return RebalanceDecision<TRange>.Skip(RebalanceReason.WithinCurrentNoRebalanceRange);
        }

        // Stage 2: Pending Rebalance Stability Check (anti-thrashing)
        // If there's a pending rebalance AND requested range will be covered by its NoRebalanceRange,
        // skip scheduling a new rebalance to avoid cancellation storms
        if (lastExecutionRequest?.DesiredNoRebalanceRange != null &&
            !_policy.ShouldRebalance(lastExecutionRequest.DesiredNoRebalanceRange.Value, requestedRange))
        {
            return RebalanceDecision<TRange>.Skip(RebalanceReason.WithinPendingNoRebalanceRange);
        }

        // Stage 3: Desired Range Computation
        // Compute the target cache geometry using policy
        var desiredCacheRange = _planner.Plan(requestedRange);
        var desiredNoRebalanceRange = _noRebalancePlanner.Plan(desiredCacheRange);

        // Stage 4: Equality Short Circuit
        // If desired range matches current cache range, no mutation needed
        var currentCacheRange = currentCacheState.Cache.Range;
        if (desiredCacheRange.Equals(currentCacheRange))
        {
            return RebalanceDecision<TRange>.Skip(RebalanceReason.DesiredEqualsCurrent);
        }

        // Stage 5: Rebalance Required
        // All validation stages passed - rebalance is necessary
        return RebalanceDecision<TRange>.Execute(desiredCacheRange, desiredNoRebalanceRange);
    }
}
