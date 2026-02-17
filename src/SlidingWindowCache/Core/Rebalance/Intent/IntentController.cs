using Intervals.NET;
using Intervals.NET.Data;
using Intervals.NET.Domain.Abstractions;
using SlidingWindowCache.Core.Rebalance.Decision;
using SlidingWindowCache.Core.Rebalance.Execution;
using SlidingWindowCache.Core.State;
using SlidingWindowCache.Infrastructure.Instrumentation;

namespace SlidingWindowCache.Core.Rebalance.Intent;

/// <summary>
/// Represents the intent to rebalance the cache based on a requested range and the currently available range data.
/// </summary>
/// <param name="RequestedRange">
/// The range requested by the user that triggered the rebalance evaluation. This is the range for which the user is seeking data.
/// </param>
/// <param name="AvailableRangeData">
/// The current range of data available in the cache along with its associated data and domain information. This represents the state of the cache before any rebalance execution.
/// </param>
/// <typeparam name="TRange">
/// The type representing the range boundaries. Must implement <see cref="IComparable{T}"/> to allow for range comparisons and calculations.
/// </typeparam>
/// <typeparam name="TData">
/// The type of data being cached. This is the type of the elements stored within the ranges in the cache.
/// </typeparam>
/// <typeparam name="TDomain">
/// The type representing the domain of the ranges. Must implement <see cref="IRangeDomain{TRange}"/> to provide necessary domain-specific operations for range calculations and validations.
/// </typeparam>
public record Intent<TRange, TData, TDomain>(
    Range<TRange> RequestedRange,
    RangeData<TRange, TData, TDomain> AvailableRangeData
)
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>;

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
/// <item><description>Evaluates rebalance necessity via DecisionEngine</description></item>
/// <item><description>Cancels obsolete pending rebalances via PendingRebalance.Cancel()</description></item>
/// <item><description>Delegates scheduling to RebalanceScheduler</description></item>
/// <item><description>Exposes cancellation interface to User Path</description></item>
/// </list>
/// <para><strong>Explicit Non-Responsibilities:</strong></para>
/// <list type="bullet">
/// <item><description>❌ Does NOT manage CancellationTokenSource lifecycle (Scheduler's responsibility)</description></item>
/// <item><description>❌ Does NOT perform scheduling or timing logic (Scheduler's responsibility)</description></item>
/// <item><description>❌ Does NOT decide whether rebalance is logically required (DecisionEngine's job)</description></item>
/// <item><description>❌ Does NOT orchestrate execution pipeline (Scheduler's responsibility)</description></item>
/// </list>
/// <para><strong>Lock-Free Implementation:</strong></para>
/// <list type="bullet">
/// <item><description>✅ Thread-safe using <see cref="System.Threading.Interlocked"/> for atomic operations</description></item>
/// <item><description>✅ No locks, no <c>lock</c> statements, no mutexes</description></item>
/// <item><description>✅ No race conditions - atomic field replacement ensures correctness</description></item>
/// <item><description>✅ Guaranteed progress - non-blocking operations</description></item>
/// <item><description>✅ Validated under concurrent load by ConcurrencyStabilityTests</description></item>
/// </list>
/// </remarks>
internal sealed class IntentController<TRange, TData, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private readonly RebalanceScheduler<TRange, TData, TDomain> _scheduler;
    private readonly RebalanceDecisionEngine<TRange, TDomain> _decisionEngine;
    private readonly CacheState<TRange, TData, TDomain> _state;
    private readonly ICacheDiagnostics _cacheDiagnostics;

    /// <summary>
    /// Snapshot of the pending rebalance's target state, used for Stage 2 stability validation.
    /// Updated atomically when a new rebalance is scheduled.
    /// </summary>
    private PendingRebalance<TRange>? _pendingRebalance;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntentController{TRange,TData,TDomain}"/> class.
    /// </summary>
    /// <param name="state">The cache state.</param>
    /// <param name="decisionEngine">The decision engine for rebalance logic.</param>
    /// <param name="executor">The executor for performing rebalance operations.</param>
    /// <param name="debounceDelay">The debounce delay before executing rebalance.</param>
    /// <param name="cacheDiagnostics">The diagnostics interface for recording cache metrics and events related to rebalance intents.</param>
    /// <remarks>
    /// This constructor composes the Intent Controller with the Execution Scheduler
    /// to form the complete Rebalance Intent Manager actor.
    /// </remarks>
    public IntentController(
        CacheState<TRange, TData, TDomain> state,
        RebalanceDecisionEngine<TRange, TDomain> decisionEngine,
        RebalanceExecutor<TRange, TData, TDomain> executor,
        TimeSpan debounceDelay,
        ICacheDiagnostics cacheDiagnostics
    )
    {
        _state = state;
        _decisionEngine = decisionEngine;
        _cacheDiagnostics = cacheDiagnostics;
        // Compose with scheduler component
        _scheduler = new RebalanceScheduler<TRange, TData, TDomain>(
            executor,
            debounceDelay,
            cacheDiagnostics
        );
    }

    /// <summary>
    /// Publishes a rebalance intent triggered by a user request.
    /// This method is fire-and-forget and returns immediately.
    /// </summary>
    /// <param name="intent">The data that was actually delivered to the user for the requested range.</param>
    /// <remarks>
    /// <para>
    /// Every user access produces a rebalance intent. This method implements the
    /// decision-driven Intent Controller pattern by:
    /// <list type="bullet">
    /// <item><description>Evaluating rebalance necessity via DecisionEngine</description></item>
    /// <item><description>Conditionally canceling previous intent only if new rebalance should schedule</description></item>
    /// <item><description>Creating a new intent with unique identity (CancellationTokenSource)</description></item>
    /// <item><description>Delegating to scheduler for debounce and execution</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// The intent contains both the requested range and the assembled data.
    /// This allows Rebalance Execution to use the assembled data as an authoritative source,
    /// avoiding duplicate fetches and ensuring consistency.
    /// </para>
    /// <para>
    /// This implements the decision-driven model: Intent → Decision → Scheduling → Execution.
    /// No implicit triggers, no blind cancellations, no decision leakage across components.
    /// </para>
    /// <para>
    /// Responsibility separation: Decision logic in DecisionEngine, intent lifecycle here,
    /// scheduling/execution delegated to RebalanceScheduler.
    /// </para>
    /// </remarks>
    public void PublishIntent(Intent<TRange, TData, TDomain> intent)
    {
        _cacheDiagnostics.RebalanceIntentPublished();

        // Step 1: Evaluate rebalance necessity (Decision Engine is SOLE AUTHORITY)
        // Capture pending rebalance state for Stage 2 validation (atomic read)
        var pendingSnapshot = Volatile.Read(ref _pendingRebalance);

        var decision = _decisionEngine.Evaluate(
            requestedRange: intent.RequestedRange,
            currentCacheState: _state,
            pendingRebalance: pendingSnapshot
        );

        // Track skip reason for observability
        RecordReason(decision.Reason);

        // Step 2: If decision says skip, publish diagnostic and return early
        if (!decision.ShouldSchedule)
        {
            return;
        }

        // Step 3: Atomically cancel pending rebalance (race-free coordination)
        // Use Interlocked.Exchange to atomically read and clear _pendingRebalance in single operation
        // This prevents race where two threads could both call Cancel() on same PendingRebalance
        // This is NOT a blind cancellation - it only happens when DecisionEngine validated necessity
        var oldPending = Interlocked.Exchange(ref _pendingRebalance, null);
        oldPending?.Cancel();

        // Step 4: Delegate to scheduler and capture returned PendingRebalance
        // Scheduler fully owns execution infrastructure (CTS, Task, debounce, exceptions)
        // New rebalance scheduled AFTER old one is cancelled to ensure proper semaphore acquisition ordering
        var newPending = _scheduler.ScheduleRebalance(intent, decision);

        // Step 5: Update pending rebalance snapshot for next Stage 2 validation
        Volatile.Write(ref _pendingRebalance, newPending);
    }

    /// <summary>
    /// Records the skip reason for diagnostic and observability purposes.
    /// Maps decision reasons to diagnostic events.
    /// </summary>
    private void RecordReason(RebalanceReason reason)
    {
        switch (reason)
        {
            case RebalanceReason.WithinCurrentNoRebalanceRange:
                _cacheDiagnostics.RebalanceSkippedCurrentNoRebalanceRange();
                break;
            case RebalanceReason.WithinPendingNoRebalanceRange:
                _cacheDiagnostics.RebalanceSkippedPendingNoRebalanceRange();
                break;
            case RebalanceReason.DesiredEqualsCurrent:
                _cacheDiagnostics.RebalanceSkippedSameRange();
                break;
            case RebalanceReason.RebalanceRequired:
                _cacheDiagnostics.RebalanceScheduled();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unhandled rebalance reason");
        }
    }

    /// <summary>
    /// Waits for the latest scheduled rebalance background Task to complete.
    /// Provides deterministic synchronization for infrastructure scenarios.
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
    /// <item><description>Read current _pendingRebalance via Volatile.Read (safe observation)</description></item>
    /// <item><description>Await the ExecutionTask from the snapshot</description></item>
    /// <item><description>Re-check if _pendingRebalance changed (new rebalance scheduled)</description></item>
    /// <item><description>Loop until snapshot stabilizes and task completes</description></item>
    /// </list>
    /// <para>
    /// This ensures that no rebalance execution is running when the method returns,
    /// even under concurrent intent cancellation and rescheduling.
    /// </para>
    /// <para><strong>Implementation Note:</strong></para>
    /// <para>
    /// Uses PendingRebalance.ExecutionTask directly rather than maintaining a separate _idleTask field.
    /// This eliminates duplication and aligns with the DDD approach where the domain object
    /// (PendingRebalance) is the single source of truth for execution state.
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
            // Observe current pending rebalance (Volatile.Read ensures visibility)
            var observedPending = Volatile.Read(ref _pendingRebalance);

            // If no pending rebalance, we're idle
            if (observedPending?.ExecutionTask == null)
            {
                return;
            }

            // Await the observed task
            await observedPending.ExecutionTask.ConfigureAwait(false);

            // Check if _pendingRebalance changed while we were waiting
            var currentPending = Volatile.Read(ref _pendingRebalance);

            if (ReferenceEquals(observedPending, currentPending))
            {
                // Snapshot stabilized and task completed - we're idle
                return;
            }

            // Snapshot changed - a new rebalance was scheduled, loop again
        }

        // Timeout - provide diagnostic information
        var finalPending = Volatile.Read(ref _pendingRebalance);
        var finalTask = finalPending?.ExecutionTask;
        throw new TimeoutException(
            $"WaitForIdleAsync() timed out after {maxWait.TotalSeconds:F1}s. " +
            $"Final task state: {finalTask?.Status.ToString() ?? "null"}");
    }
}