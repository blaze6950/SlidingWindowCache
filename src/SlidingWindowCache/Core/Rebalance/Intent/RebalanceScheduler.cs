using Intervals.NET.Domain.Abstractions;
using SlidingWindowCache.Core.Rebalance.Execution;
using SlidingWindowCache.Infrastructure.Instrumentation;

namespace SlidingWindowCache.Core.Rebalance.Decision;

/// <summary>
/// Responsible for scheduling and executing rebalance operations in the background.
/// This is the Execution Scheduler component within the Rebalance Intent Manager actor.
/// </summary>
/// <typeparam name="TRange">The type representing the range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <typeparam name="TDomain">The type representing the domain of the ranges.</typeparam>
/// <remarks>
/// <para><strong>Architectural Role:</strong></para>
/// <para>
/// This component is the Execution Scheduler within the larger Rebalance Intent Manager actor.
/// It works in tandem with IntentController to form a complete
/// rebalance management system.
/// </para>
/// <para><strong>Responsibilities:</strong></para>
/// <list type="bullet">
/// <item><description>Debounce delay and delayed execution</description></item>
/// <item><description>Ensures at most one rebalance execution is active</description></item>
/// <item><description>Executes rebalance asynchronously in background thread pool</description></item>
/// <item><description>Checks intent validity before execution starts</description></item>
/// <item><description>Propagates cancellation to executor</description></item>
/// <item><description>Orchestrates DecisionEngine → Executor pipeline</description></item>
/// </list>
/// <para><strong>Explicit Non-Responsibilities:</strong></para>
/// <list type="bullet">
/// <item><description>❌ Does NOT decide whether rebalance is logically required (DecisionEngine's job)</description></item>
/// <item><description>❌ Does NOT own intent identity or versioning (IntentManager's job)</description></item>
/// </list>
/// </remarks>
internal sealed class RebalanceScheduler<TRange, TData, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private readonly RebalanceExecutor<TRange, TData, TDomain> _executor;
    private readonly TimeSpan _debounceDelay;
    private readonly ICacheDiagnostics _cacheDiagnostics;

    /// <summary>
    /// Initializes a new instance of the <see cref="RebalanceScheduler{TRange,TData,TDomain}"/> class.
    /// </summary>
    /// <param name="executor">The executor for performing rebalance operations.</param>
    /// <param name="debounceDelay">The debounce delay before executing rebalance.</param>
    /// <param name="cacheDiagnostics">The diagnostics interface for recording rebalance-related metrics and events.</param>
    public RebalanceScheduler(
        RebalanceExecutor<TRange, TData, TDomain> executor,
        TimeSpan debounceDelay,
        ICacheDiagnostics cacheDiagnostics
    )
    {
        _executor = executor;
        _debounceDelay = debounceDelay;
        _cacheDiagnostics = cacheDiagnostics;
    }

    /// <summary>
    /// Schedules a rebalance operation to execute after the debounce delay.
    /// Checks intent validity before starting execution.
    /// </summary>
    /// <param name="intent">The intent with data that was actually assembled in UserPath and the requested range.</param>
    /// <param name="decision">The pre-validated rebalance decision from DecisionEngine.</param>
    /// <returns>A PendingRebalance snapshot representing the scheduled rebalance operation.</returns>
    /// <remarks>
    /// <para>
    /// This method is fire-and-forget. It schedules execution in the background thread pool
    /// and returns immediately with a snapshot of the pending rebalance state.
    /// </para>
    /// <para><strong>Complete Infrastructure Ownership:</strong></para>
    /// <para>
    /// The scheduler now owns the COMPLETE execution infrastructure:
    /// - Creates and manages CancellationTokenSource internally
    /// - Manages background Task lifecycle
    /// - Handles debounce timing
    /// - Orchestrates exception handling
    /// IntentController only works with the returned PendingRebalance domain object.
    /// </para>
    /// <para>
    /// Decision logic has already been evaluated by IntentController. This method performs
    /// mechanical scheduling and execution orchestration only.
    /// </para>
    /// </remarks>
    public PendingRebalance<TRange> ScheduleRebalance(
        Intent<TRange, TData, TDomain> intent,
        RebalanceDecision<TRange> decision)
    {
        // Create CancellationTokenSource - scheduler owns complete execution infrastructure
        var pendingCts = new CancellationTokenSource();
        var intentToken = pendingCts.Token;

        // Create PendingRebalance snapshot with encapsulated CTS
        var pendingRebalance = new PendingRebalance<TRange>(
            decision.DesiredRange!.Value,
            decision.DesiredNoRebalanceRange,
            pendingCts
        );

        // Fire-and-forget: schedule execution in background thread pool
        var backgroundTask = Task.Run(async () =>
        {
            try
            {
                await ExecuteAfterAsync(
                    executePipelineAsync: () => ExecutePipelineAsync(intent, decision, intentToken),
                    intentToken: intentToken
                );
            }
            catch (OperationCanceledException)
            {
                // Expected when intent is cancelled or superseded
                // This is normal behavior, not an error
                _cacheDiagnostics.RebalanceIntentCancelled();
            }
            catch (Exception)
            {
                // All other exceptions are already recorded via RebalanceExecutionFailed
                // They bubble up from ExecutePipelineAsync and are swallowed here to prevent
                // unhandled task exceptions from crashing the application.
                // 
                // ⚠️ CRITICAL: Applications MUST subscribe to RebalanceExecutionFailed events
                // and implement appropriate error handling (logging, alerting, monitoring).
                // Ignoring this event means silent failures in background operations.
            }
        }, CancellationToken.None);
        // NOTE: Do NOT pass intentToken to Task.Run ^ - it should only be used inside the lambda
        // to ensure the try-catch properly handles all OperationCanceledExceptions

        // Set execution task on PendingRebalance for direct await scenarios
        pendingRebalance.ExecutionTask = backgroundTask;

        return pendingRebalance;
    }

    /// <summary>
    /// Executes the provided function after a debounce delay, checking intent validity before execution.
    /// </summary>
    /// <param name="executePipelineAsync">
    /// The asynchronous function to execute after the debounce delay. This typically encapsulates the entire
    /// decision and execution pipeline for rebalance. It receives the delivered data and intent token as context.
    /// The function should respect the intentToken for cancellation to ensure timely yielding to new intents.
    /// </param>
    /// <param name="intentToken">
    /// The cancellation token associated with the current intent. This token is used to implement single-flight execution and intent invalidation.
    /// If this token is cancelled during the debounce delay, the execution will be aborted and the pipeline will not start. If the token is cancelled during execution, the pipeline should respond to cancellation as soon as possible to yield to new intents.
    /// This token is owned and managed by the Intent Manager, which creates a new token for each intent and cancels the previous one when a new intent is published.
    /// </param>
    private async Task ExecuteAfterAsync(Func<Task> executePipelineAsync, CancellationToken intentToken)
    {
        // Debounce delay: wait before executing
        // This can be cancelled if a new intent arrives during the delay
        await Task.Delay(_debounceDelay, intentToken);

        // Intent validity check: discard if cancelled during debounce
        // This implements Invariant C.20: "If intent becomes obsolete before execution begins, execution must not start"
        intentToken.ThrowIfCancellationRequested();

        // Execute the provided function
        await executePipelineAsync();
    }

    /// <summary>
    /// Executes the mechanical rebalance pipeline in the background.
    /// </summary>
    /// <param name="intent">The intent with data that was actually assembled in UserPath and the requested range.</param>
    /// <param name="decision">The pre-validated rebalance decision with target ranges.</param>
    /// <param name="cancellationToken">Cancellation token to support cancellation.</param>
    /// <remarks>
    /// <para><strong>Pipeline Flow:</strong></para>
    /// <list type="number">
    /// <item><description>Check if intent is still valid (cancellation check)</description></item>
    /// <item><description>Invoke Executor with decision parameters (DesiredRange, DesiredNoRebalanceRange)</description></item>
    /// </list>
    /// <para>
    /// Decision logic has already been evaluated. This method performs mechanical execution only.
    /// </para>
    /// </remarks>
    private async Task ExecutePipelineAsync(
        Intent<TRange, TData, TDomain> intent,
        RebalanceDecision<TRange> decision,
        CancellationToken cancellationToken)
    {
        // Final cancellation check before execution
        // Ensures we don't do work for an obsolete intent
        if (cancellationToken.IsCancellationRequested)
        {
            _cacheDiagnostics.RebalanceIntentCancelled();
            return;
        }

        _cacheDiagnostics.RebalanceExecutionStarted();

        // Invoke Executor with pre-validated decision parameters
        // Executor performs mechanical mutations without decision logic
        try
        {
            await _executor.ExecuteAsync(
                intent,
                decision.DesiredRange!.Value,
                decision.DesiredNoRebalanceRange,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _cacheDiagnostics.RebalanceExecutionCancelled();
            throw;
        }
        catch (Exception ex)
        {
            // Record failure for diagnostic tracking
            // WARNING: This is a fire-and-forget background operation failure
            // Applications MUST monitor RebalanceExecutionFailed events and implement
            // appropriate error handling (logging, alerting, etc.)
            _cacheDiagnostics.RebalanceExecutionFailed(ex);
            throw;
        }
    }
}