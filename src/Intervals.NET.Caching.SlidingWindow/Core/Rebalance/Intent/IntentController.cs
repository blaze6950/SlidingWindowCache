using Intervals.NET.Caching.Infrastructure.Concurrency;
using Intervals.NET.Caching.Infrastructure.Scheduling;
using Intervals.NET.Domain.Abstractions;
using Intervals.NET.Caching.SlidingWindow.Core.Rebalance.Decision;
using Intervals.NET.Caching.SlidingWindow.Core.Rebalance.Execution;
using Intervals.NET.Caching.SlidingWindow.Core.State;
using Intervals.NET.Caching.SlidingWindow.Public.Instrumentation;

namespace Intervals.NET.Caching.SlidingWindow.Core.Rebalance.Intent;

/// <summary>
/// Manages the lifecycle of rebalance intents using a single-threaded loop with burst resistance. See docs/sliding-window/ for design details.
/// </summary>
/// <typeparam name="TRange">The type representing the range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <typeparam name="TDomain">The type representing the domain of the ranges.</typeparam>
internal sealed class IntentController<TRange, TData, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private readonly RebalanceDecisionEngine<TRange, TDomain> _decisionEngine;
    private readonly ISupersessionWorkScheduler<ExecutionRequest<TRange, TData, TDomain>> _scheduler;
    private readonly CacheState<TRange, TData, TDomain> _state;
    private readonly ISlidingWindowCacheDiagnostics _cacheDiagnostics;

    // Shared intent field - user threads write via Interlocked.Exchange, processing loop reads
    private Intent<TRange, TData, TDomain>? _pendingIntent;

    // Semaphore for signaling new intents - prevents CPU spinning
    private readonly SemaphoreSlim _intentSignal = new(0);

    // Activity counter for tracking active operations (intents + executions)
    private readonly AsyncActivityCounter _activityCounter;

    // Processing loop task
    private readonly Task _processingLoopTask;

    // Cancellation token source for the processing loop (used during disposal)
    private readonly CancellationTokenSource _loopCancellation = new();

    // Disposal state tracking (lock-free using Interlocked)
    // 0 = not disposed, 1 = disposed
    private int _disposeState;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntentController{TRange,TData,TDomain}"/> class and starts the processing loop.
    /// </summary>
    /// <param name="state">The cache state.</param>
    /// <param name="decisionEngine">The decision engine for rebalance logic.</param>
    /// <param name="scheduler">The supersession work scheduler for serializing and executing rebalance work items.</param>
    /// <param name="cacheDiagnostics">The diagnostics interface for recording cache metrics and events.</param>
    /// <param name="activityCounter">Activity counter for tracking active operations.</param>
    public IntentController(
        CacheState<TRange, TData, TDomain> state,
        RebalanceDecisionEngine<TRange, TDomain> decisionEngine,
        ISupersessionWorkScheduler<ExecutionRequest<TRange, TData, TDomain>> scheduler,
        ISlidingWindowCacheDiagnostics cacheDiagnostics,
        AsyncActivityCounter activityCounter
    )
    {
        _state = state;
        _decisionEngine = decisionEngine;
        _scheduler = scheduler;
        _cacheDiagnostics = cacheDiagnostics;
        _activityCounter = activityCounter;

        // Start processing loop immediately - runs for cache lifetime
        _processingLoopTask = ProcessIntentsAsync();
    }

    /// <summary>
    /// Publishes a rebalance intent triggered by a user request. Fire-and-forget, returns immediately.
    /// </summary>
    /// <param name="intent">The intent containing the requested range and delivered data.</param>
    public void PublishIntent(Intent<TRange, TData, TDomain> intent)
    {
        // Check disposal state using Volatile.Read (lock-free)
        if (Volatile.Read(ref _disposeState) != 0)
        {
            throw new ObjectDisposedException(
                nameof(IntentController<TRange, TData, TDomain>),
                "Cannot publish intent to a disposed controller.");
        }

        // Increment activity counter BEFORE making the intent visible to any thread,
        // ensuring WaitForIdleAsync cannot observe zero activity while work is pending.
        // (Invariant S.H.1: increment before work is made visible.)
        _activityCounter.IncrementActivity();

        try
        {
            // Atomically set the pending intent (latest wins)
            Interlocked.Exchange(ref _pendingIntent, intent);

            // Signal the processing loop to wake up and process the intent.
            // Release() may throw ObjectDisposedException in the rare race where disposal
            // completes (disposes the semaphore) between the disposal guard above and this call.
            // The try/finally ensures the activity counter is always decremented in that case.
            _intentSignal.Release();

            _cacheDiagnostics.RebalanceIntentPublished();
        }
        catch
        {
            // Compensate for the increment above so WaitForIdleAsync does not hang.
            _activityCounter.DecrementActivity();
            throw;
        }
    }

    /// <summary>
    /// Processing loop that continuously reads intents and coordinates rebalance execution.
    /// </summary>
    private async Task ProcessIntentsAsync()
    {
        try
        {
            while (!_loopCancellation.Token.IsCancellationRequested)
            {
                // Track whether we successfully consumed a semaphore signal
                // This prevents activity counter imbalance when disposal cancels WaitAsync
                var consumedSignal = false;

                try
                {
                    // Wait for signal from user thread
                    await _intentSignal.WaitAsync(_loopCancellation.Token).ConfigureAwait(false);

                    // Signal successfully consumed - we must decrement in finally
                    consumedSignal = true;

                    // Atomically read and clear pending intent (latest intent wins)
                    var intent = Interlocked.Exchange(ref _pendingIntent, null);

                    if (intent == null)
                    {
                        // Signal was consumed but no intent available
                        // This can happen if multiple intents overwrote each other before we read
                        // The increment happened in PublishIntent, so decrement still needed (in finally)
                        continue;
                    }

                    // THREADING CONTEXT: Executing in BACKGROUND THREAD (intent processing loop)
                    // User thread returned immediately after PublishIntent() signaled the semaphore
                    // All decision evaluation (DecisionEngine, Planners, Policy) happens HERE in background
                    // Evaluate DecisionEngine INSIDE loop (avoids race conditions)

                    // Read the pending desired state from the last work item for anti-thrashing.
                    // The scheduler owns cancellation of this item — we must NOT cancel it here.
                    // _state.Storage.Range and _state.NoRebalanceRange are read without explicit
                    // synchronization. This is intentional: the decision engine operates on an
                    // eventually-consistent snapshot of cache state. A slightly stale range or
                    // NoRebalanceRange value may cause one extra or skipped rebalance, but the
                    // system self-corrects on the next intent. The single-writer architecture
                    // guarantees no torn writes; CopyOnReadStorage protects the Range value via its
                    // internal lock only for reads inside Read()/ToRangeData(); bare Range reads
                    // here accept the same eventual-consistency contract.
                    var decision = _decisionEngine.Evaluate(
                        requestedRange: intent.RequestedRange,
                        currentNoRebalanceRange: _state.NoRebalanceRange,
                        currentCacheRange: _state.Storage.Range,
                        pendingNoRebalanceRange: _scheduler.LastWorkItem?.DesiredNoRebalanceRange
                    );

                    // Record decision reason for observability
                    RecordDecisionOutcome(decision.Reason);

                    // If decision says skip, continue (decrement happens in finally)
                    if (!decision.IsExecutionRequired)
                    {
                        continue;
                    }

                    // Create execution request (work item) with a fresh CancellationTokenSource.
                    // The scheduler will automatically cancel the previous work item on publish
                    // (supersession semantics — no manual cancel needed here).
                    var request = new ExecutionRequest<TRange, TData, TDomain>(
                        intent,
                        decision.DesiredRange!.Value,
                        decision.DesiredNoRebalanceRange,
                        new CancellationTokenSource()
                    );

                    await _scheduler.PublishWorkItemAsync(
                        request,
                        _loopCancellation.Token
                    ).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_loopCancellation.Token.IsCancellationRequested)
                {
                    // Loop cancellation - exit gracefully
                    break;
                }
                catch (Exception ex)
                {
                    // Actor loop must never crash - log and continue processing
                    _cacheDiagnostics.BackgroundOperationFailed(ex);
                }
                finally
                {
                    // Only decrement if we successfully consumed a semaphore signal
                    // This prevents negative counter when disposal cancels WaitAsync
                    if (consumedSignal)
                    {
                        _activityCounter.DecrementActivity();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Fatal error in processing loop
            _cacheDiagnostics.BackgroundOperationFailed(ex);
        }
    }

    /// <summary>
    /// Records the decision outcome for diagnostic and observability purposes.
    /// </summary>
    private void RecordDecisionOutcome(RebalanceReason reason)
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
    /// Disposes the intent controller, shutting down the processing loop and execution scheduler.
    /// </summary>
    /// <returns>A ValueTask representing the asynchronous disposal operation.</returns>
    public async ValueTask DisposeAsync()
    {
        // Idempotent check using lock-free Interlocked.CompareExchange
        if (Interlocked.CompareExchange(ref _disposeState, 1, 0) != 0)
        {
            return; // Already disposed
        }

        // Cancel the processing loop
        await _loopCancellation.CancelAsync();

        // Wait for processing loop to complete gracefully
        try
        {
            await _processingLoopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during cancellation
        }
        catch (Exception ex)
        {
            // Log via diagnostics but don't throw
            _cacheDiagnostics.BackgroundOperationFailed(ex);
        }

        // Dispose work scheduler (stops execution loop)
        await _scheduler.DisposeAsync().ConfigureAwait(false);

        // Dispose resources
        _loopCancellation.Dispose();
        _intentSignal.Dispose();
    }
}
