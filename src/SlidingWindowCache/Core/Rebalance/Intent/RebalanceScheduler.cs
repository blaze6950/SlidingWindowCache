using Intervals.NET.Domain.Abstractions;
using SlidingWindowCache.Core.Rebalance.Decision;
using SlidingWindowCache.Core.Rebalance.Execution;
using SlidingWindowCache.Core.State;
using SlidingWindowCache.Infrastructure.Instrumentation;

namespace SlidingWindowCache.Core.Rebalance.Intent;

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
    private readonly CacheState<TRange, TData, TDomain> _state;
    private readonly RebalanceDecisionEngine<TRange, TDomain> _decisionEngine;
    private readonly RebalanceExecutor<TRange, TData, TDomain> _executor;
    private readonly TimeSpan _debounceDelay;
    private readonly ICacheDiagnostics _cacheDiagnostics;

    /// <summary>
    /// Tracks the latest scheduled rebalance background Task for deterministic idle synchronization.
    /// Used by WaitForIdleAsync() to provide race-free infrastructure API for testing, graceful shutdown,
    /// and health checks. This field exists in all builds to support the public WaitForIdleAsync() API.
    /// Memory overhead: one Task reference per cache instance.
    /// </summary>
    private Task _idleTask = Task.CompletedTask;

    /// <summary>
    /// Initializes a new instance of the <see cref="RebalanceScheduler{TRange,TData,TDomain}"/> class.
    /// </summary>
    /// <param name="state">The cache state.</param>
    /// <param name="decisionEngine">The decision engine for rebalance logic.</param>
    /// <param name="executor">The executor for performing rebalance operations.</param>
    /// <param name="debounceDelay">The debounce delay before executing rebalance.</param>
    /// <param name="cacheDiagnostics">The diagnostics interface for recording rebalance-related metrics and events.</param>
    public RebalanceScheduler(
        CacheState<TRange, TData, TDomain> state,
        RebalanceDecisionEngine<TRange, TDomain> decisionEngine,
        RebalanceExecutor<TRange, TData, TDomain> executor,
        TimeSpan debounceDelay,
        ICacheDiagnostics cacheDiagnostics
    )
    {
        _state = state;
        _decisionEngine = decisionEngine;
        _executor = executor;
        _debounceDelay = debounceDelay;
        _cacheDiagnostics = cacheDiagnostics;
    }

    /// <summary>
    /// Schedules a rebalance operation to execute after the debounce delay.
    /// Checks intent validity before starting execution.
    /// </summary>
    /// <param name="intent">The intent with data that was actually assembled in UserPath and the requested range.</param>
    /// <param name="intentToken">Cancellation token for this specific intent (owned by IntentManager).</param>
    /// <remarks>
    /// <para>
    /// This method is fire-and-forget. It schedules execution in the background thread pool
    /// and returns immediately.
    /// </para>
    /// <para>
    /// The scheduler ensures single-flight execution through the intent cancellation token.
    /// When a new intent arrives, the Intent Controller cancels the previous token, causing
    /// any pending or executing rebalance to be cancelled.
    /// </para>
    /// </remarks>
    public void ScheduleRebalance(Intent<TRange, TData, TDomain> intent, CancellationToken intentToken)
    {
        // Fire-and-forget: schedule execution in background thread pool
        // Fixing ambiguous invocation by explicitly specifying the type for Task.Run
        var backgroundTask = Task.Run(async () =>
        {
            try
            {
                await ExecuteAfterAsync(
                    executePipelineAsync: () => ExecutePipelineAsync(intent, intentToken),
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

        // Track the latest background task for deterministic idle synchronization
        // This supports the public WaitForIdleAsync() infrastructure API
        _idleTask = backgroundTask;
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
    /// Executes the decision-execution pipeline in the background.
    /// </summary>
    /// <param name="intent">The intent with data that was actually assembled in UserPath and the requested range.</param>
    /// <param name="cancellationToken">Cancellation token to support cancellation.</param>
    /// <remarks>
    /// <para><strong>Pipeline Flow:</strong></para>
    /// <list type="number">
    /// <item><description>Check if intent is still valid (cancellation check)</description></item>
    /// <item><description>Invoke DecisionEngine to determine if rebalance is needed</description></item>
    /// <item><description>If needed, invoke Executor to perform rebalance using delivered data</description></item>
    /// </list>
    /// </remarks>
    private async Task ExecutePipelineAsync(Intent<TRange, TData, TDomain> intent,
        CancellationToken cancellationToken)
    {
        // Final cancellation check before decision logic
        // Ensures we don't do work for an obsolete intent
        if (cancellationToken.IsCancellationRequested)
        {
            _cacheDiagnostics.RebalanceIntentCancelled();
            return;
        }

        // Step 1: Invoke DecisionEngine (pure decision logic)
        // This checks NoRebalanceRange and computes DesiredCacheRange
        var decision = _decisionEngine.ShouldExecuteRebalance(
            requestedRange: intent.RequestedRange,
            noRebalanceRange: _state.NoRebalanceRange
        );

        // Step 2: If decision says skip, return early (no-op)
        if (!decision.ShouldExecute)
        {
            _cacheDiagnostics.RebalanceSkippedNoRebalanceRange();
            return;
        }

        _cacheDiagnostics.RebalanceExecutionStarted();

        // Step 3: If execution is allowed, invoke Executor with delivered data
        // The executor will use delivered data as authoritative source, merge with existing cache,
        // expand to desired range, trim excess, and update cache state
        try
        {
            await _executor.ExecuteAsync(intent, decision.DesiredRange!.Value, cancellationToken);
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

    /// <summary>
    /// Waits for the latest scheduled rebalance background Task to complete.
    /// Provides deterministic synchronization without relying on instrumentation counters.
    /// </summary>
    /// <param name="timeout">
    /// Maximum time to wait for idle state. Defaults to 30 seconds.
    /// Throws <see cref="TimeoutException"/> if the Task does not stabilize within this period.
    /// </param>
    /// <returns>A Task that completes when the background rebalance has finished.</returns>
    /// <remarks>
    /// <para><strong>Infrastructure API:</strong></para>
    /// <para>
    /// This method provides deterministic synchronization with background rebalance operations.
    /// It is useful for testing, graceful shutdown, health checks, integration scenarios,
    /// and any situation requiring coordination with cache background work.
    /// </para>
    /// <para><strong>Observe-and-Stabilize Pattern:</strong></para>
    /// <list type="number">
    /// <item><description>Read current _idleTask via Volatile.Read (safe observation)</description></item>
    /// <item><description>Await the observed Task</description></item>
    /// <item><description>Re-check if _idleTask changed (new rebalance scheduled)</description></item>
    /// <item><description>Loop until Task reference stabilizes and completes</description></item>
    /// </list>
    /// <para>
    /// This ensures that no rebalance execution is running when the method returns,
    /// even under concurrent intent cancellation and rescheduling.
    /// </para>
    /// </remarks>
    /// <exception cref="TimeoutException">
    /// Thrown if the background Task does not stabilize within the specified timeout.
    /// </exception>
    public async Task WaitForIdleAsync(TimeSpan? timeout = null)
    {
        var maxWait = timeout ?? TimeSpan.FromSeconds(30);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        while (stopwatch.Elapsed < maxWait)
        {
            // Observe current idle task (Volatile.Read ensures visibility)
            var observedTask = Volatile.Read(ref _idleTask);

            // Await the observed task
            await observedTask;

            // Check if _idleTask changed while we were waiting
            var currentTask = Volatile.Read(ref _idleTask);

            if (ReferenceEquals(observedTask, currentTask))
            {
                // Task reference stabilized and completed - we're idle
                return;
            }

            // Task changed - a new rebalance was scheduled, loop again
        }

        // Timeout - provide diagnostic information
        var finalTask = Volatile.Read(ref _idleTask);
        throw new TimeoutException(
            $"WaitForIdleAsync() timed out after {maxWait.TotalSeconds:F1}s. " +
            $"Final task state: {finalTask.Status}");
    }
}