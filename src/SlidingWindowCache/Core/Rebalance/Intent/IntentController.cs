using Intervals.NET;
using Intervals.NET.Data;
using Intervals.NET.Domain.Abstractions;
using SlidingWindowCache.Core.Rebalance.Decision;
using SlidingWindowCache.Core.Rebalance.Execution;
using SlidingWindowCache.Core.State;
using SlidingWindowCache.Infrastructure.Concurrency;
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
/// Manages the lifecycle of rebalance intents using a single-threaded loop with burst resistance.
/// This is the IntentController actor - fast, CPU-bound decision and coordination logic.
/// </summary>
/// <typeparam name="TRange">The type representing the range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <typeparam name="TDomain">The type representing the domain of the ranges.</typeparam>
/// <remarks>
/// <para><strong>Architectural Model - Single-Threaded Intent Processing:</strong></para>
/// <para>
/// IntentController runs a single-threaded loop that continuously processes intents from user requests.
/// User threads write intents using Interlocked.Exchange on _pendingIntent field, then signal a semaphore.
/// The processing loop waits on the semaphore, reads the pending intent atomically, evaluates the decision,
/// and enqueues execution requests to RebalanceExecutionController.
/// </para>
/// <para><strong>Burst Resistance:</strong></para>
/// <para>
/// The "latest intent wins" semantic naturally handles request bursts:
/// <list type="bullet">
/// <item><description>User threads atomically replace _pendingIntent with newest intent</description></item>
/// <item><description>Only the most recent intent gets processed (older ones are discarded)</description></item>
/// <item><description>Semaphore prevents CPU spinning while waiting for intents</description></item>
/// <item><description>Decision evaluation happens serially, preventing thrashing</description></item>
/// </list>
/// </para>
/// <para><strong>IntentController Actor Responsibilities:</strong></para>
/// <list type="bullet">
/// <item><description>Waits on semaphore signal from user threads</description></item>
/// <item><description>Reads pending intent via Interlocked.Exchange (atomic)</description></item>
/// <item><description>Evaluates DecisionEngine (CPU-only, O(1), lightweight)</description></item>
/// <item><description>Cancels previous execution if new rebalance is needed</description></item>
/// <item><description>Enqueues execution request to RebalanceExecutionController</description></item>
/// <item><description>Signals idle state semaphore after processing</description></item>
/// </list>
/// <para><strong>Two-Phase Pipeline:</strong></para>
/// <list type="number">
/// <item><description><strong>Phase 1 (Intent Processing):</strong> IntentController evaluates decision, cancels old execution, enqueues new execution request</description></item>
/// <item><description><strong>Phase 2 (Execution):</strong> RebalanceExecutionController debounces, executes, mutates cache</description></item>
/// </list>
/// </remarks>
internal sealed class IntentController<TRange, TData, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private readonly RebalanceDecisionEngine<TRange, TDomain> _decisionEngine;
    private readonly RebalanceExecutionController<TRange, TData, TDomain> _executionController;
    private readonly CacheState<TRange, TData, TDomain> _state;
    private readonly ICacheDiagnostics _cacheDiagnostics;

    // Shared intent field - user threads write via Interlocked.Exchange, processing loop reads
    private Intent<TRange, TData, TDomain>? _pendingIntent;

    // Semaphore for signaling new intents - prevents CPU spinning
    private readonly SemaphoreSlim _intentSignal = new(0);

    // Activity counter for tracking active operations (intents + executions)
    private readonly AsyncActivityCounter _activityCounter;

    // Processing loop task
    private readonly Task _processingLoopTask;

    // Cancellation token source for the processing loop (used during disposal) todo implement disposing
    private readonly CancellationTokenSource _loopCancellation = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="IntentController{TRange,TData,TDomain}"/> class.
    /// </summary>
    /// <param name="state">The cache state.</param>
    /// <param name="decisionEngine">The decision engine for rebalance logic.</param>
    /// <param name="executionController">The execution controller actor for performing rebalance operations.</param>
    /// <param name="cacheDiagnostics">The diagnostics interface for recording cache metrics and events related to rebalance intents.</param>
    /// <param name="activityCounter">Activity counter for tracking active operations.</param>
    /// <remarks>
    /// This constructor initializes the single-threaded processing loop infrastructure.
    /// The loop starts immediately and runs for the lifetime of the cache instance.
    /// </remarks>
    public IntentController(
        CacheState<TRange, TData, TDomain> state,
        RebalanceDecisionEngine<TRange, TDomain> decisionEngine,
        RebalanceExecutionController<TRange, TData, TDomain> executionController,
        ICacheDiagnostics cacheDiagnostics,
        AsyncActivityCounter activityCounter
    )
    {
        _state = state;
        _decisionEngine = decisionEngine;
        _executionController = executionController;
        _cacheDiagnostics = cacheDiagnostics;
        _activityCounter = activityCounter;

        // Start processing loop immediately - runs for cache lifetime
        _processingLoopTask = ProcessIntentsAsync();
    }

    /// <summary>
    /// Publishes a rebalance intent triggered by a user request.
    /// This method is fire-and-forget and returns immediately after setting the intent.
    /// </summary>
    /// <param name="intent">The intent containing the requested range and delivered data.</param>
    /// <remarks>
    /// <para><strong>Burst-Resistant Pattern:</strong></para>
    /// <para>
    /// This method executes in the user thread and performs minimal work:
    /// <list type="number">
    /// <item><description>Increment activity counter (tracks intent processing activity)</description></item>
    /// <item><description>Atomically replace _pendingIntent with new intent (latest wins)</description></item>
    /// <item><description>Signal intent semaphore to wake up processing loop</description></item>
    /// <item><description>Record diagnostic event</description></item>
    /// <item><description>Return immediately</description></item>
    /// </list>
    /// </para>
    /// <para><strong>Latest Intent Wins:</strong></para>
    /// <para>
    /// If multiple user threads publish intents rapidly (burst scenario), only the most recent
    /// intent is processed. Older intents are atomically discarded via Interlocked.Exchange.
    /// This prevents intent queue buildup and naturally handles bursts.
    /// </para>
    /// </remarks>
    public void PublishIntent(Intent<TRange, TData, TDomain> intent)
    {
        // Atomically set the pending intent (latest wins)
        Interlocked.Exchange(ref _pendingIntent, intent);

        // Increment activity counter for intent processing BEFORE signaling
        _activityCounter.IncrementActivity();

        // Signal the processing loop to wake up and process the intent
        // TryRelease returns false if semaphore is already signaled (count at max), which is fine
        _intentSignal.Release();

        _cacheDiagnostics.RebalanceIntentPublished();
    }

    /// <summary>
    /// Processing loop that continuously reads intents and coordinates rebalance execution.
    /// Runs on a single background thread for the lifetime of the cache instance.
    /// </summary>
    /// <remarks>
    /// <para><strong>Single-Threaded Loop Semantics:</strong></para>
    /// <para>
    /// This loop waits on _intentSignal semaphore (blocks without CPU spinning), then atomically
    /// reads _pendingIntent via Interlocked.Exchange. For each intent:
    /// <list type="number">
    /// <item><description>Wait on semaphore (blocks until user thread signals)</description></item>
    /// <item><description>Atomically read and clear _pendingIntent</description></item>
    /// <item><description>Evaluate DecisionEngine (CPU-only, lightweight)</description></item>
    /// <item><description>If skip: record diagnostic and signal idle state</description></item>
    /// <item><description>If schedule: Cancel previous execution, create CTS, enqueue execution request</description></item>
    /// <item><description>Signal idle state semaphore after processing</description></item>
    /// </list>
    /// </para>
    /// <para><strong>Burst Handling:</strong></para>
    /// <para>
    /// The "latest intent wins" semantic via Interlocked.Exchange naturally handles bursts.
    /// Multiple rapid user requests will atomically replace _pendingIntent, and only the
    /// most recent intent gets processed. This prevents queue buildup and thrashing.
    /// </para>
    /// </remarks>
    private async Task ProcessIntentsAsync()
    {
        try
        {
            while (!_loopCancellation.Token.IsCancellationRequested)
            {
                try
                {
                    // Wait for signal from user thread
                    await _intentSignal.WaitAsync(_loopCancellation.Token).ConfigureAwait(false);

                    // Atomically read and clear pending intent (latest intent wins)
                    var intent = Interlocked.Exchange(ref _pendingIntent, null);

                    if (intent == null)
                    {
                        // No intent to process - continue waiting
                        // NOTE: No increment happened, so no decrement needed
                        continue;
                    }

                    // Evaluate DecisionEngine INSIDE loop (avoids race conditions)
                    var lastExecutionRequest = _executionController.LastExecutionRequest;
                    var decision = _decisionEngine.Evaluate(
                        requestedRange: intent.RequestedRange,
                        currentCacheState: _state,
                        lastExecutionRequest: lastExecutionRequest
                    );

                    // Record decision reason for observability
                    RecordReason(decision.Reason);

                    // If decision says skip, continue (decrement happens in finally)
                    if (!decision.ShouldSchedule)
                    {
                        continue;
                    }

                    // Cancel previous execution
                    lastExecutionRequest?.Cancel();

                    _executionController.PublishExecutionRequest(
                        intent: intent,
                        desiredRange: decision.DesiredRange!.Value,
                        desiredNoRebalanceRange: decision.DesiredNoRebalanceRange
                    );
                }
                catch (OperationCanceledException) when (_loopCancellation.Token.IsCancellationRequested)
                {
                    // Loop cancellation - exit gracefully
                    break;
                }
                catch (Exception ex)
                {
                    // Actor loop must never crash - log and continue processing
                    _cacheDiagnostics.RebalanceExecutionFailed(ex);
                }
                finally
                {
                    // Decrement activity counter for intent processing
                    // This ALWAYS happens after processing a real intent, even if skip or exception
                    _activityCounter.DecrementActivity();
                }
            }
        }
        catch (Exception ex)
        {
            // Fatal error in processing loop
            _cacheDiagnostics.RebalanceExecutionFailed(ex);
        }
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
}