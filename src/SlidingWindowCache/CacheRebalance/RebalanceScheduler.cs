using Intervals.NET.Data;
using Intervals.NET.Domain.Abstractions;
using SlidingWindowCache.CacheRebalance.Executor;

namespace SlidingWindowCache.CacheRebalance;

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
        Task.Run(async () =>
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
                    return; // Obsolete intent, don't execute
                }

                // Execute the rebalance pipeline
                await ExecutePipelineAsync(deliveredData, intentToken);
            }
            catch (OperationCanceledException)
            {
                // Expected when intent is cancelled or superseded
                // This is normal behavior, not an error
            }
        }, intentToken);
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
#if DEBUG
            Instrumentation.CacheInstrumentationCounters.OnRebalanceSkippedNoRebalanceRange();
#endif
            return;
        }

#if DEBUG
        Instrumentation.CacheInstrumentationCounters.OnRebalanceExecutionStarted();
#endif

        // Step 3: If execution is allowed, invoke Executor with delivered data
        // The executor will use delivered data as authoritative source, merge with existing cache,
        // expand to desired range, trim excess, and update cache state
        try
        {
            await _executor.ExecuteAsync(deliveredData, decision.DesiredRange!.Value, cancellationToken);
#if DEBUG
            Instrumentation.CacheInstrumentationCounters.OnRebalanceExecutionCompleted();
#endif
        }
        catch (OperationCanceledException)
        {
#if DEBUG
            Instrumentation.CacheInstrumentationCounters.OnRebalanceExecutionCancelled();
#endif
            throw;
        }
    }
}
