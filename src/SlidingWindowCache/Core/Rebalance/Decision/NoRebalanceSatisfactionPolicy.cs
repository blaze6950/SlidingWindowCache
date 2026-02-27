using Intervals.NET;
using Intervals.NET.Extensions;

namespace SlidingWindowCache.Core.Rebalance.Decision;

/// <summary>
/// Evaluates whether rebalancing should occur based on no-rebalance range containment.
/// This is a pure decision evaluator - planning logic has been separated to
/// <see cref="Planning.NoRebalanceRangePlanner{TRange,TDomain}"/>.
/// </summary>
/// <typeparam name="TRange">The type representing the range boundaries.</typeparam>
/// <remarks>
/// <para><strong>Role:</strong> Rebalance Policy - Decision Evaluation</para>
/// <para><strong>Responsibility:</strong> Determine if a requested range violates the no-rebalance zone</para>
/// <para><strong>Characteristics:</strong> Pure function, stateless</para>
/// <para><strong>Execution Context:</strong> Background thread (intent processing loop)</para>
/// <para>
/// Invoked by <see cref="RebalanceDecisionEngine{TRange,TDomain}"/> during Stages 1-2 (stability validation),
/// which executes in the background intent processing loop (see <c>IntentController.ProcessIntentsAsync</c>).
/// </para>
/// </remarks>
internal readonly struct NoRebalanceSatisfactionPolicy<TRange>
    where TRange : IComparable<TRange>
{
    /// <summary>
    /// Determines whether rebalancing should occur based on whether the requested range
    /// is contained within the no-rebalance zone.
    /// </summary>
    /// <param name="noRebalanceRange">The stability zone within which rebalancing is suppressed.</param>
    /// <param name="requested">The range requested by the user.</param>
    /// <returns>True if rebalancing should occur (request is outside no-rebalance zone); otherwise false.</returns>
    public bool ShouldRebalance(Range<TRange> noRebalanceRange, Range<TRange> requested) =>
        !noRebalanceRange.Contains(requested);
}
