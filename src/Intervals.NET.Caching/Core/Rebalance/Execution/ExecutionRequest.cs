using Intervals.NET;
using Intervals.NET.Domain.Abstractions;
using Intervals.NET.Caching.Core.Rebalance.Intent;

namespace Intervals.NET.Caching.Core.Rebalance.Execution;

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
/// through the execution pipeline. It owns a <see cref="CancellationTokenSource"/> (held as a private
/// field) and exposes only the derived <see cref="CancellationToken"/> to consumers, ensuring that
/// only this class controls cancellation and disposal of the token source.
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
/// disposal races gracefully by catching and ignoring ObjectDisposedException.
/// </para>
/// </remarks>
internal sealed class ExecutionRequest<TRange, TData, TDomain> : IDisposable
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private readonly CancellationTokenSource _cts;

    /// <summary>
    /// The rebalance intent that triggered this execution request.
    /// </summary>
    public Intent<TRange, TData, TDomain> Intent { get; }

    /// <summary>
    /// The desired cache range for this rebalance operation.
    /// </summary>
    public Range<TRange> DesiredRange { get; }

    /// <summary>
    /// The desired no-rebalance range for this rebalance operation, or null if not applicable.
    /// </summary>
    public Range<TRange>? DesiredNoRebalanceRange { get; }

    /// <summary>
    /// The cancellation token for this execution request. Cancelled when superseded or disposed.
    /// </summary>
    public CancellationToken CancellationToken => _cts.Token;

    /// <summary>
    /// Initializes a new execution request with the specified intent, ranges, and cancellation token source.
    /// </summary>
    /// <param name="intent">The rebalance intent that triggered this request.</param>
    /// <param name="desiredRange">The desired cache range.</param>
    /// <param name="desiredNoRebalanceRange">The desired no-rebalance range, or null.</param>
    /// <param name="cts">The cancellation token source owned by this request.</param>
    public ExecutionRequest(
        Intent<TRange, TData, TDomain> intent,
        Range<TRange> desiredRange,
        Range<TRange>? desiredNoRebalanceRange,
        CancellationTokenSource cts)
    {
        Intent = intent;
        DesiredRange = desiredRange;
        DesiredNoRebalanceRange = desiredNoRebalanceRange;
        _cts = cts;
    }

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
    /// Catches and ignores ObjectDisposedException to handle disposal races gracefully.
    /// This follows the "best-effort cancellation" pattern for background operations.
    /// </para>
    /// </remarks>
    public void Cancel()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // CancellationTokenSource already disposed - cancellation is best-effort
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
    /// Catches and ignores ObjectDisposedException to ensure cleanup always completes without
    /// propagating exceptions during disposal.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        try
        {
            _cts.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed - best-effort cleanup
        }
    }
}
