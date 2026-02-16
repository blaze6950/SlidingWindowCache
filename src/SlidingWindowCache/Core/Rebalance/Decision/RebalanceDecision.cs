using Intervals.NET;

namespace SlidingWindowCache.Core.Rebalance.Decision;

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
    public bool ShouldExecute { get; }

    /// <summary>
    /// Gets the desired cache range if execution is allowed, otherwise null.
    /// </summary>
    public Range<TRange>? DesiredRange { get; }

    private RebalanceDecision(bool shouldExecute, Range<TRange>? desiredRange)
    {
        ShouldExecute = shouldExecute;
        DesiredRange = desiredRange;
    }

    /// <summary>
    /// Creates a decision to skip rebalance execution.
    /// </summary>
    public static RebalanceDecision<TRange> Skip() => new(false, null);

    /// <summary>
    /// Creates a decision to execute rebalance with the specified desired range.
    /// </summary>
    /// <param name="desiredRange">The target cache range for rebalancing.</param>
    public static RebalanceDecision<TRange> Execute(Range<TRange> desiredRange) =>
        new(true, desiredRange);
}
