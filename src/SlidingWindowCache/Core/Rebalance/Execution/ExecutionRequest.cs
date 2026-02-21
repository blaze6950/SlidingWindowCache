using Intervals.NET;
using Intervals.NET.Domain.Abstractions;
using SlidingWindowCache.Core.Rebalance.Intent;

namespace SlidingWindowCache.Core.Rebalance.Execution;

/// <summary>
/// Execution request message sent from IntentController to IRebalanceExecutionController implementations.
/// Contains all information needed to execute a rebalance operation.
/// </summary>
/// <typeparam name="TRange">The type representing the range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <typeparam name="TDomain">The type representing the domain of the ranges.</typeparam>
/// <remarks>
/// <para><strong>Architectural Role:</strong></para>
/// <para>
/// This record encapsulates the validated rebalance decision from IntentController and carries it
/// through the execution pipeline. It includes the CancellationTokenSource for cancellation coordination
/// when superseded by newer rebalance requests.
/// </para>
/// <para><strong>Lifecycle:</strong></para>
/// <list type="number">
/// <item><description>Created by IRebalanceExecutionController.PublishExecutionRequest()</description></item>
/// <item><description>Stored as LastExecutionRequest for cancellation coordination</description></item>
/// <item><description>Processed by execution strategy (task chain or channel loop)</description></item>
/// <item><description>Cancelled if superseded by newer request (Cancel() method)</description></item>
/// <item><description>Disposed after execution completes/cancels (Dispose() method)</description></item>
/// </list>
/// <para><strong>Thread Safety:</strong></para>
/// <para>
/// The Cancel() and Dispose() methods are designed to be safe for multiple calls and handle
/// disposal races gracefully by catching and ignoring exceptions.
/// </para>
/// </remarks>
internal record ExecutionRequest<TRange, TData, TDomain>(
    Intent<TRange, TData, TDomain> Intent,
    Range<TRange> DesiredRange,
    Range<TRange>? DesiredNoRebalanceRange,
    CancellationTokenSource CancellationTokenSource
)
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    /// <summary>
    /// Cancels this execution request by cancelling its CancellationTokenSource.
    /// Safe to call multiple times and handles disposal races gracefully.
    /// </summary>
    /// <remarks>
    /// <para><strong>Usage Context:</strong></para>
    /// <para>
    /// Called by IntentController when a newer rebalance request supersedes this one,
    /// or during disposal to signal early exit from pending operations.
    /// </para>
    /// <para><strong>Exception Handling:</strong></para>
    /// <para>
    /// Catches and ignores all exceptions to handle disposal races gracefully.
    /// This follows the "best-effort cancellation" pattern for background operations.
    /// </para>
    /// </remarks>
    public void Cancel()
    {
        try
        {
            CancellationTokenSource.Cancel();
        }
        catch
        {
            // Ignore disposal errors - cancellation is best-effort
            // If CancellationTokenSource is already disposed, we don't care
        }
    }

    /// <summary>
    /// Disposes the CancellationTokenSource associated with this execution request.
    /// Safe to call multiple times.
    /// </summary>
    /// <remarks>
    /// <para><strong>Usage Context:</strong></para>
    /// <para>
    /// Called after execution completes/cancels/fails to clean up the CancellationTokenSource.
    /// Always called in the finally block of execution processing.
    /// </para>
    /// <para><strong>Exception Handling:</strong></para>
    /// <para>
    /// Catches and ignores all exceptions to ensure cleanup always completes without
    /// propagating exceptions during disposal.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        try
        {
            CancellationTokenSource.Dispose();
        }
        catch
        {
            // Ignore disposal errors - best-effort cleanup
        }
    }
}
