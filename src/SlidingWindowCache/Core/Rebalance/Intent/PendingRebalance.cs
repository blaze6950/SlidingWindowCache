using Intervals.NET;

namespace SlidingWindowCache.Core.Rebalance.Intent;

/// <summary>
/// Represents an immutable snapshot of a pending rebalance operation's target state.
/// Used by the decision engine to evaluate stability without coupling to execution details.
/// </summary>
/// <typeparam name="TRange">The type representing the range boundaries.</typeparam>
/// <remarks>
/// <para><strong>Architectural Role:</strong></para>
/// <para>
/// This class provides a stable, immutable view of a scheduled rebalance's intended outcome,
/// allowing the decision engine to perform Stage 2 anti-thrashing validation (pending desired
/// cache stability check) without creating dependencies on scheduler or executor internals.
/// </para>
/// <para><strong>Lifetime:</strong></para>
/// <para>
/// Created when a rebalance is scheduled, captured atomically by IntentController,
/// and passed to DecisionEngine for subsequent decision evaluations.
/// </para>
/// <para><strong>DDD Enhancement:</strong></para>
/// <para>
/// Includes encapsulated cancellation token and execution task tracking,
/// enabling direct cancellation and wait-for-idle scenarios without proxy methods.
/// </para>
/// </remarks>
internal sealed class PendingRebalance<TRange>
    where TRange : IComparable<TRange>
{
    /// <summary>
    /// Gets the desired cache range that the pending rebalance will establish.
    /// </summary>
    public Range<TRange> DesiredRange { get; }

    /// <summary>
    /// Gets the no-rebalance range that will be active after the pending rebalance completes.
    /// May be null if not yet computed or if rebalance was skipped.
    /// </summary>
    public Range<TRange>? DesiredNoRebalanceRange { get; }

    /// <summary>
    /// Gets the cancellation token for this pending rebalance operation.
    /// External callers can monitor this token for cancellation status.
    /// </summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>
    /// Gets the execution task for this pending rebalance operation.
    /// External callers can await this task to wait for rebalance completion.
    /// Set by scheduler after scheduling background execution.
    /// </summary>
    public Task? ExecutionTask { get; internal set; }

    private readonly CancellationTokenSource? _cts;

    /// <summary>
    /// Initializes a new instance of the <see cref="PendingRebalance{TRange}"/> class.
    /// </summary>
    /// <param name="desiredRange">The desired cache range for the pending rebalance.</param>
    /// <param name="desiredNoRebalanceRange">The no-rebalance range for the target state.</param>
    /// <param name="cancellationTokenSource">Optional cancellation token source for this rebalance.</param>
    public PendingRebalance(
        Range<TRange> desiredRange,
        Range<TRange>? desiredNoRebalanceRange,
        CancellationTokenSource? cancellationTokenSource = null)
    {
        DesiredRange = desiredRange;
        DesiredNoRebalanceRange = desiredNoRebalanceRange;
        _cts = cancellationTokenSource;
        CancellationToken = cancellationTokenSource?.Token ?? CancellationToken.None;
    }

    /// <summary>
    /// Cancels this pending rebalance operation.
    /// DDD-style behavior encapsulation for direct cancellation.
    /// </summary>
    /// <remarks>
    /// This method provides a more DDD-aligned approach where the domain object
    /// encapsulates its own behavior (cancellation) rather than requiring external
    /// management through the IntentController.
    /// </remarks>
    public void Cancel()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}