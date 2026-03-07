namespace Intervals.NET.Caching.SlidingWindow.Core.Rebalance.Decision;

/// <summary>
/// Specifies the reason for a rebalance decision outcome.
/// </summary>
internal enum RebalanceReason
{
    /// <summary>
    /// Default unspecified value. This value should never appear in practice.
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// Request falls within the current cache's no-rebalance range (Stage 1 stability).
    /// </summary>
    WithinCurrentNoRebalanceRange,

    /// <summary>
    /// Request falls within the pending rebalance's desired no-rebalance range (Stage 2 stability).
    /// </summary>
    WithinPendingNoRebalanceRange,

    /// <summary>
    /// Desired cache range equals current cache range (Stage 4 short-circuit).
    /// </summary>
    DesiredEqualsCurrent,

    /// <summary>
    /// Rebalance is required to satisfy the request (Stage 5 execution).
    /// </summary>
    RebalanceRequired
}
