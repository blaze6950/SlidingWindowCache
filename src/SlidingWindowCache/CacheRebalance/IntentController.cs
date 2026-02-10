using Intervals.NET.Data;
using Intervals.NET.Domain.Abstractions;
using SlidingWindowCache.CacheRebalance.Executor;

namespace SlidingWindowCache.CacheRebalance;

/// <summary>
/// Manages the lifecycle of rebalance intents.
/// This is the Intent Controller component within the Rebalance Intent Manager actor.
/// </summary>
/// <typeparam name="TRange">The type representing the range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <typeparam name="TDomain">The type representing the domain of the ranges.</typeparam>
/// <remarks>
/// <para><strong>Architectural Model:</strong></para>
/// <para>
/// The Rebalance Intent Manager is a single logical ACTOR in the system architecture.
/// Internally, it is decomposed into two cooperating components:
/// </para>
/// <list type="number">
/// <item><description><strong>IntentController (this class)</strong> - Intent lifecycle management</description></item>
/// <item><description><strong>RebalanceScheduler</strong> - Timing, debounce, pipeline orchestration</description></item>
/// </list>
/// <para><strong>Intent Controller Responsibilities:</strong></para>
/// <list type="bullet">
/// <item><description>Receives rebalance intents on every user access</description></item>
/// <item><description>Owns intent identity and versioning (CancellationTokenSource)</description></item>
/// <item><description>Cancels and invalidates obsolete intents</description></item>
/// <item><description>Exposes cancellation interface to User Path</description></item>
/// </list>
/// <para><strong>Explicit Non-Responsibilities:</strong></para>
/// <list type="bullet">
/// <item><description>❌ Does NOT perform scheduling or timing logic (Scheduler's responsibility)</description></item>
/// <item><description>❌ Does NOT decide whether rebalance is logically required (DecisionEngine's job)</description></item>
/// <item><description>❌ Does NOT orchestrate execution pipeline (Scheduler's responsibility)</description></item>
/// </list>
/// </remarks>
internal sealed class IntentController<TRange, TData, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private readonly RebalanceScheduler<TRange, TData, TDomain> _scheduler;
    
    /// <summary>
    /// The current rebalance cancellation token source.
    /// Represents the identity and lifecycle of the latest rebalance intent.
    /// </summary>
    private CancellationTokenSource? _currentIntentCts;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntentController{TRange,TData,TDomain}"/> class.
    /// </summary>
    /// <param name="state">The cache state.</param>
    /// <param name="decisionEngine">The decision engine for rebalance logic.</param>
    /// <param name="executor">The executor for performing rebalance operations.</param>
    /// <param name="debounceDelay">The debounce delay before executing rebalance.</param>
    /// <remarks>
    /// This constructor composes the Intent Controller with the Execution Scheduler
    /// to form the complete Rebalance Intent Manager actor.
    /// </remarks>
    public IntentController(
        CacheState<TRange, TData, TDomain> state,
        RebalanceDecisionEngine<TRange, TDomain> decisionEngine,
        RebalanceExecutor<TRange, TData, TDomain> executor,
        TimeSpan debounceDelay)
    {
        // Compose with scheduler component
        _scheduler = new RebalanceScheduler<TRange, TData, TDomain>(
            state,
            decisionEngine,
            executor,
            debounceDelay);
    }

    /// <summary>
    /// Cancels any pending or ongoing rebalance execution.
    /// This method is called by the User Path to ensure exclusive cache access
    /// before performing cache mutations (satisfies Invariant A.1-0a).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method is synchronous and returns immediately after signaling cancellation.
    /// The background rebalance task will handle the cancellation asynchronously.
    /// </para>
    /// <para>
    /// User Path never waits for rebalance to fully complete - it just ensures
    /// the cancellation signal is sent before proceeding with its own mutations.
    /// </para>
    /// </remarks>
    public void CancelPendingRebalance()
    {
        if (_currentIntentCts == null)
        {
            return;
        }

        _currentIntentCts.Cancel();
        _currentIntentCts.Dispose();
        _currentIntentCts = null;
        
#if DEBUG
        Instrumentation.CacheInstrumentationCounters.OnRebalanceIntentCancelled();
#endif
    }

    /// <summary>
    /// Publishes a rebalance intent triggered by a user request.
    /// This method is fire-and-forget and returns immediately.
    /// </summary>
    /// <param name="deliveredData">The data that was actually delivered to the user for the requested range.</param>
    /// <remarks>
    /// <para>
    /// Every user access produces a rebalance intent. This method implements the
    /// Intent Controller pattern by:
    /// <list type="bullet">
    /// <item><description>Invalidating the previous intent (if any)</description></item>
    /// <item><description>Creating a new intent with unique identity (CancellationTokenSource)</description></item>
    /// <item><description>Delegating to scheduler for debounce and execution</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// The intent contains both the requested range and the actual data delivered to the user.
    /// This allows Rebalance Execution to use the delivered data as an authoritative source,
    /// avoiding duplicate fetches and ensuring consistency.
    /// </para>
    /// <para>
    /// This implements Invariant C.18: "Any previously created rebalance intent is obsolete
    /// after a new intent is generated."
    /// </para>
    /// <para>
    /// Responsibility separation: Intent lifecycle management is handled here,
    /// while scheduling/execution is delegated to RebalanceScheduler.
    /// </para>
    /// </remarks>
    public void PublishIntent(RangeData<TRange, TData, TDomain> deliveredData)
    {
        // Invalidate previous intent (Invariant C.18: "Any previously created rebalance intent is obsolete")
        _currentIntentCts?.Cancel();
        _currentIntentCts?.Dispose();
        
        // Create new intent identity
        _currentIntentCts = new CancellationTokenSource();
        var intentToken = _currentIntentCts.Token;
        
#if DEBUG
        Instrumentation.CacheInstrumentationCounters.OnRebalanceIntentPublished();
#endif
        
        // Delegate to scheduler for debounce and execution
        // The scheduler owns timing, debounce, and pipeline orchestration
        _scheduler.ScheduleRebalance(deliveredData, intentToken);
    }
}
