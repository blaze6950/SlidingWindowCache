using Intervals.NET;

namespace Intervals.NET.Caching.Core.Rebalance.Decision;

/// <summary>
/// Represents the result of a rebalance decision evaluation.
/// </summary>
/// <typeparam name="TRange">The type representing the range boundaries.</typeparam>
internal readonly struct RebalanceDecision<TRange>
    where TRange : IComparable<TRange>
{
    /// <summary>
    /// Gets a value indicating whether rebalance execution should proceed.
    /// </summary>
    public bool IsExecutionRequired { get; }

    /// <summary>
    /// Gets the desired cache range if execution is allowed, otherwise null.
    /// </summary>
    public Range<TRange>? DesiredRange { get; }

    /// <summary>
    /// Gets the desired no-rebalance range for the target cache state, or null if skipping.
    /// </summary>
    public Range<TRange>? DesiredNoRebalanceRange { get; }

    /// <summary>
    /// Gets the reason for this decision outcome.
    /// </summary>
    public RebalanceReason Reason { get; }

    private RebalanceDecision(
        bool isExecutionRequired,
        Range<TRange>? desiredRange,
        Range<TRange>? desiredNoRebalanceRange,
        RebalanceReason reason)
    {
        IsExecutionRequired = isExecutionRequired;
        DesiredRange = desiredRange;
        DesiredNoRebalanceRange = desiredNoRebalanceRange;
        Reason = reason;
    }

    /// <summary>
    /// Creates a decision to skip rebalance execution with the specified reason.
    /// </summary>
    /// <param name="reason">The reason for skipping rebalance.</param>
    public static RebalanceDecision<TRange> Skip(RebalanceReason reason) =>
        new(false, null, null, reason);

    /// <summary>
    /// Creates a decision to execute rebalance with the specified desired range.
    /// </summary>
    /// <param name="desiredRange">The target cache range for rebalancing.</param>
    /// <param name="desiredNoRebalanceRange">The no-rebalance range for the target cache state.</param>
    public static RebalanceDecision<TRange> Execute(
        Range<TRange> desiredRange,
        Range<TRange>? desiredNoRebalanceRange) =>
        new(true, desiredRange, desiredNoRebalanceRange, RebalanceReason.RebalanceRequired);
}
