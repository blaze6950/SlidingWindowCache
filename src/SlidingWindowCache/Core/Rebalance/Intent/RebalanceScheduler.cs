using Intervals.NET.Data;
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

#if DEBUG
    /// <summary>
    /// Tracks the latest scheduled rebalance background Task for deterministic idle synchronization.
    /// Used by WaitForIdleAsync() to provide race-free testing infrastructure.
    /// This field exists only in DEBUG builds and has zero RELEASE overhead.
    /// </summary>
    private Task _idleTask = Task.CompletedTask;
#endif

    /// <summary>
    /// Initializes a new instance of the <see cref="RebalanceScheduler{TRange,TData,TDomain}"/> class.
    /// </summary>
    /// <param name="state">The cache state.</param>
    /// <param name="decisionEngine">The decision engine for rebalance logic.</param>
    /// <param name="executor">The executor for performing rebalance operations.</param>
    /// <param name="debounceDelay">The debounce delay before executing rebalance.</param>
    public RebalanceScheduler(
        CacheState<TRange, TData, TDomain> state,
        RebalanceDecisionEngine<TRange, TDomain> decisionEngine,
        RebalanceExecutor<TRange, TData, TDomain> executor,
        TimeSpan debounceDelay)
    {
        _state = state;
        _decisionEngine = decisionEngine;
        _executor = executor;
        _debounceDelay = debounceDelay;
    }

    /// <summary>
    /// Schedules a rebalance operation to execute after the debounce delay.
    /// Checks intent validity before starting execution.
    /// </summary>
    /// <param name="deliveredData">The data that was actually delivered to the user for the requested range.</param>
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
    /// <para>
    /// The delivered data is passed through to Rebalance Execution, allowing it to use
    /// the data already fetched and delivered to the user as an authoritative source.
    /// </para>
    /// </remarks>
    public void ScheduleRebalance(RangeData<TRange, TData, TDomain> deliveredData, CancellationToken intentToken)
    {
        // Fire-and-forget: schedule execution in background thread pool
        var backgroundTask = Task.Run(async () =>
        {
            try
            {
                // Debounce delay: wait before executing
                // This can be cancelled if a new intent arrives during the delay
                await Task.Delay(_debounceDelay, intentToken);

                // Intent validity check: discard if cancelled during debounce
                // This implements Invariant C.20: "If intent becomes obsolete before execution begins, execution must not start"
                if (intentToken.IsCancellationRequested)
                {
                    CacheInstrumentationCounters.OnRebalanceIntentCancelled();
                    return; // Obsolete intent, don't execute
                }

                // Execute the rebalance pipeline
                await ExecutePipelineAsync(deliveredData, intentToken);
            }
            catch (OperationCanceledException)
            {
                // Expected when intent is cancelled or superseded
                // This is normal behavior, not an error
                CacheInstrumentationCounters.OnRebalanceIntentCancelled();
            }
        }, CancellationToken.None);
        // NOTE: Do NOT pass intentToken to Task.Run - it should only be used inside the lambda
        // to ensure the try-catch properly handles all OperationCanceledExceptions

#if DEBUG
        // Track the latest background task for deterministic idle synchronization (DEBUG-only)
        _idleTask = backgroundTask;
#endif
    }

    /// <summary>
    /// Executes the decision-execution pipeline in the background.
    /// </summary>
    /// <param name="deliveredData">The data that was actually delivered to the user for the requested range.</param>
    /// <param name="cancellationToken">Cancellation token to support cancellation.</param>
    /// <remarks>
    /// <para><strong>Pipeline Flow:</strong></para>
    /// <list type="number">
    /// <item><description>Check if intent is still valid (cancellation check)</description></item>
    /// <item><description>Invoke DecisionEngine to determine if rebalance is needed</description></item>
    /// <item><description>If needed, invoke Executor to perform rebalance using delivered data</description></item>
    /// </list>
    /// </remarks>
    private async Task ExecutePipelineAsync(RangeData<TRange, TData, TDomain> deliveredData, CancellationToken cancellationToken)
    {
        // Final cancellation check before decision logic
        // Ensures we don't do work for an obsolete intent
        if (cancellationToken.IsCancellationRequested)
        {
            CacheInstrumentationCounters.OnRebalanceIntentCancelled();
            return;
        }

        // Step 1: Invoke DecisionEngine (pure decision logic)
        // This checks NoRebalanceRange and computes DesiredCacheRange
        var decision = _decisionEngine.ShouldExecuteRebalance(
            deliveredData.Range,
            _state.NoRebalanceRange);

        // Step 2: If decision says skip, return early (no-op)
        if (!decision.ShouldExecute)
        {
            CacheInstrumentationCounters.OnRebalanceSkippedNoRebalanceRange();
            return;
        }

        CacheInstrumentationCounters.OnRebalanceExecutionStarted();

        // Step 3: If execution is allowed, invoke Executor with delivered data
        // The executor will use delivered data as authoritative source, merge with existing cache,
        // expand to desired range, trim excess, and update cache state
        try
        {
            await _executor.ExecuteAsync(deliveredData, decision.DesiredRange!.Value, cancellationToken);
            CacheInstrumentationCounters.OnRebalanceExecutionCompleted();
        }
        catch (OperationCanceledException)
        {
            CacheInstrumentationCounters.OnRebalanceExecutionCancelled();
            throw;
        }
    }

#if DEBUG
    /// <summary>
    /// Waits for the latest scheduled rebalance background Task to complete.
    /// Provides deterministic synchronization for testing without relying on instrumentation counters.
    /// </summary>
    /// <param name="timeout">
    /// Maximum time to wait for idle state. Defaults to 30 seconds.
    /// Throws <see cref="TimeoutException"/> if the Task does not stabilize within this period.
    /// </param>
    /// <returns>A Task that completes when the background rebalance has finished.</returns>
    /// <remarks>
    /// <para><strong>DEBUG-only Infrastructure:</strong></para>
    /// <para>
    /// This method exists only in DEBUG builds to support deterministic testing.
    /// It has zero overhead in RELEASE builds (returns completed Task immediately).
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
#else
    /// <summary>
    /// No-op in RELEASE builds. Returns a completed Task immediately.
    /// Task lifecycle tracking exists only in DEBUG builds for testing infrastructure.
    /// </summary>
    /// <param name="timeout">Ignored in RELEASE builds.</param>
    /// <returns>A completed Task.</returns>
    public Task WaitForIdleAsync(TimeSpan? timeout = null)
    {
        return Task.CompletedTask;
    }
#endif
}
