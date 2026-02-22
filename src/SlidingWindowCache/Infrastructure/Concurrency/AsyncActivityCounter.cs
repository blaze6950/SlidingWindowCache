namespace SlidingWindowCache.Infrastructure.Concurrency;

/// <summary>
/// Lock-free, thread-safe activity counter that provides awaitable idle state notification.
/// Tracks active operations using atomic counter and signals completion via TaskCompletionSource.
/// </summary>
/// <remarks>
/// <para><strong>Thread-Safety Model:</strong></para>
/// <para>
/// This class is fully lock-free, using only <see cref="Interlocked"/> and <see cref="Volatile"/> operations
/// for all synchronization. It supports concurrent calls from multiple threads:
/// <list type="bullet">
/// <item><description>User thread (via IntentController.PublishIntent)</description></item>
/// <item><description>Intent processing loop (background)</description></item>
/// <item><description>Execution controllers (background)</description></item>
/// </list>
/// </para>
/// <para><strong>Usage Pattern:</strong></para>
/// <list type="number">
/// <item><description>Call <see cref="IncrementActivity"/> when starting work (user thread or processing loop)</description></item>
/// <item><description>Call <see cref="DecrementActivity"/> in finally block when work completes (processing loop)</description></item>
/// <item><description>Await <see cref="WaitForIdleAsync"/> to wait for all active operations to complete</description></item>
/// </list>
/// <para><strong>Critical Activity Tracking Invariants (docs/invariants.md Section H):</strong></para>
/// <para>
/// This class implements two architectural invariants that create an orchestration barrier:
/// <list type="bullet">
/// <item><description><strong>H.47 - Increment-Before-Publish:</strong> Work MUST call IncrementActivity() BEFORE becoming visible</description></item>
/// <item><description><strong>H.48 - Decrement-After-Completion:</strong> Work MUST call DecrementActivity() in finally block AFTER completion</description></item>
/// <item><description><strong>H.49 - "Was Idle" Semantics:</strong> WaitForIdleAsync() uses eventual consistency model</description></item>
/// </list>
/// These invariants ensure idle detection never misses scheduled-but-not-yet-started work.
/// See docs/invariants.md Section H for detailed explanation and call site verification.
/// </para>
/// <para><strong>Idle State Semantics - STATE-BASED, NOT EVENT-BASED:</strong></para>
/// <para>
/// Counter starts at 0 (idle). When counter transitions from 0→1, a new TCS is created.
/// When counter transitions from N→0, the TCS is signaled. Multiple waiters can await the same TCS.
/// </para>
/// <para>
/// <strong>CRITICAL:</strong> This is a state-based completion primitive, NOT an event-based signaling primitive.
/// TaskCompletionSource is the correct primitive because:
/// <list type="bullet">
/// <item><description>✅ State-based: Task.IsCompleted persists, all future awaiters complete immediately</description></item>
/// <item><description>✅ Multiple awaiters: All threads awaiting the same TCS complete when signaled</description></item>
/// <item><description>✅ No lost signals: Idle state is preserved until next busy period</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Why NOT SemaphoreSlim:</strong> SemaphoreSlim is token/event-based. Release() is consumed by first WaitAsync(),
/// subsequent waiters block. This violates idle state semantics where ALL awaiters should observe idle state.
/// </para>
/// <para><strong>Memory Model Guarantees:</strong></para>
/// <para>
/// TCS lifecycle uses explicit memory barriers via <see cref="Volatile.Write"/> (publish) and <see cref="Volatile.Read"/> (observe):
/// <list type="bullet">
/// <item><description>Increment (0→1): Creates TCS, publishes via Volatile.Write (release fence)</description></item>
/// <item><description>Decrement (N→0): Reads TCS via Volatile.Read (acquire fence), signals idle</description></item>
/// <item><description>WaitForIdleAsync: Snapshots TCS via Volatile.Read (acquire fence)</description></item>
/// </list>
/// This ensures proper visibility: readers always observe fully-constructed TCS instances.
/// </para>
/// <para><strong>Idle Detection Semantics:</strong></para>
/// <para>
/// <see cref="WaitForIdleAsync"/> completes when the system <strong>was idle at some point in time</strong>.
/// It does NOT guarantee the system is still idle after completion (new activity may start immediately).
/// This is correct behavior for eventual consistency models - callers must re-check state if needed.
/// </para>
/// </remarks>
internal sealed class AsyncActivityCounter
{
    // Activity counter - incremented when work starts, decremented when work finishes
    // Atomic operations via Interlocked.Increment/Decrement
    private int _activityCount;

    // Current TaskCompletionSource - signaled when counter reaches 0
    // Access via Volatile.Read/Write for proper memory barriers
    // Published via Volatile.Write on 0→1 transition, observed via Volatile.Read on N→0 transition and WaitForIdleAsync
    private TaskCompletionSource<bool> _idleTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Initializes a new instance of the <see cref="AsyncActivityCounter"/> class.
    /// Counter starts at 0 (idle state) with a pre-completed TCS.
    /// </summary>
    public AsyncActivityCounter()
    {
        // Start in idle state with completed TCS
        _idleTcs.TrySetResult(true);
    }

    /// <summary>
    /// Increments the activity counter atomically.
    /// If this is a transition from idle (0) to busy (1), creates a new TaskCompletionSource.
    /// </summary>
    /// <remarks>
    /// <para><strong>CRITICAL INVARIANT - H.47 Increment-Before-Publish:</strong></para>
    /// <para>
    /// Callers MUST call this method BEFORE making work visible to consumers (e.g., semaphore signal, channel write).
    /// This ensures idle detection never misses scheduled-but-not-yet-started work.
    /// See docs/invariants.md Section H.47 for detailed explanation and call site verification.
    /// </para>
    /// <para><strong>Thread-Safety:</strong></para>
    /// <para>
    /// Uses <see cref="Interlocked.Increment"/> for atomic counter manipulation.
    /// TCS creation uses <see cref="Volatile.Write"/> for lock-free publication with release fence semantics.
    /// Only the thread that observes newCount == 1 creates and publishes the new TCS.
    /// </para>
    /// <para><strong>Memory Barriers:</strong></para>
    /// <para>
    /// Volatile.Write provides release fence: all prior writes (TCS construction) are visible to readers.
    /// This ensures readers via Volatile.Read observe fully-constructed TCS instances.
    /// </para>
    /// <para><strong>Concurrent 0→1 Transitions:</strong></para>
    /// <para>
    /// If multiple threads call IncrementActivity concurrently from idle state, Interlocked.Increment
    /// guarantees only ONE thread observes newCount == 1. That thread creates the TCS for this busy period.
    /// </para>
    /// <para><strong>Call Sites (verified in docs/invariants.md Section H.47):</strong></para>
    /// <list type="bullet">
    /// <item><description>IntentController.PublishIntent() - line 173 before semaphore signal at line 177</description></item>
    /// <item><description>TaskBasedRebalanceExecutionController.PublishExecutionRequest() - line 196 before Volatile.Write(_lastExecutionRequest) at line 214 and task chain publication at line 220</description></item>
    /// <item><description>ChannelBasedRebalanceExecutionController.PublishExecutionRequest() - line 220 before channel write at line 239</description></item>
    /// </list>
    /// </remarks>
    public void IncrementActivity()
    {
        var newCount = Interlocked.Increment(ref _activityCount);

        // Check if this is a transition from idle (0) to busy (1)
        if (newCount == 1)
        {
            // Create new TCS for this busy period
            var newTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            
            // Publish new TCS with release fence (Volatile.Write)
            // Ensures TCS construction completes before reference becomes visible
            Volatile.Write(ref _idleTcs, newTcs);
        }
    }

    /// <summary>
    /// Decrements the activity counter atomically.
    /// If this is a transition from busy to idle (counter reaches 0), signals the TaskCompletionSource.
    /// </summary>
    /// <remarks>
    /// <para><strong>CRITICAL INVARIANT - H.48 Decrement-After-Completion:</strong></para>
    /// <para>
    /// Callers MUST call this method in a finally block AFTER work completes (success/cancellation/exception).
    /// This ensures activity counter remains balanced and WaitForIdleAsync never hangs due to counter leaks.
    /// See docs/invariants.md Section H.48 for detailed explanation and call site verification.
    /// </para>
    /// <para><strong>Thread-Safety:</strong></para>
    /// <para>
    /// Uses <see cref="Interlocked.Decrement"/> for atomic counter manipulation.
    /// <see cref="TaskCompletionSource{TResult}.TrySetResult"/> is inherently thread-safe and idempotent
    /// (only first call succeeds, others are no-ops). No lock needed.
    /// </para>
    /// <para><strong>Memory Barriers:</strong></para>
    /// <para>
    /// <see cref="Volatile.Read"/> provides acquire fence: observes TCS published via Volatile.Write.
    /// Ensures we signal the correct TCS for this busy period.
    /// </para>
    /// <para><strong>Race Scenario (Decrement + Increment Interleaving):</strong></para>
    /// <para>
    /// If T1 decrements to 0 while T2 increments to 1:
    /// <list type="bullet">
    /// <item><description>T1 observes count=0, reads TCS_old via Volatile.Read, signals TCS_old (completes old busy period)</description></item>
    /// <item><description>T2 observes count=1, creates TCS_new, publishes via Volatile.Write (starts new busy period)</description></item>
    /// <item><description>Result: TCS_old=completed, _idleTcs=TCS_new (uncompleted), count=1 - ALL CORRECT</description></item>
    /// </list>
    /// This race is benign: old busy period ends, new busy period begins. No corruption.
    /// </para>
    /// <para><strong>Call Sites (verified in docs/invariants.md Section H.48):</strong></para>
    /// <list type="bullet">
    /// <item><description>IntentController.ProcessIntentsAsync() - finally block at line 271</description></item>
    /// <item><description>TaskBasedRebalanceExecutionController.ExecuteRequestAsync() - finally block at line 349</description></item>
    /// <item><description>ChannelBasedRebalanceExecutionController.ProcessExecutionRequestsAsync() - finally block at line 327</description></item>
    /// <item><description>ChannelBasedRebalanceExecutionController.PublishExecutionRequest() - catch block at line 245 (channel write failure)</description></item>
    /// </list>
    /// <para><strong>Critical Contract:</strong></para>
    /// <para>
    /// MUST be called in finally block to ensure decrement happens even on exceptions.
    /// Unbalanced increment/decrement will cause counter leaks and WaitForIdleAsync to hang.
    /// </para>
    /// </remarks>
    public void DecrementActivity()
    {
        var newCount = Interlocked.Decrement(ref _activityCount);

        // Sanity check - counter should never go negative
        if (newCount < 0)
        {
            // This indicates a bug - decrement without matching increment
            // Restore to 0 and throw to alert developers
            Interlocked.CompareExchange(ref _activityCount, 0, newCount);
            throw new InvalidOperationException(
                $"AsyncActivityCounter decremented below zero. This indicates unbalanced IncrementActivity/DecrementActivity calls.");
        }

        // Check if this is a transition to idle (counter reached 0)
        if (newCount == 0)
        {
            // Read current TCS with acquire fence (Volatile.Read)
            // Ensures we observe TCS published by Volatile.Write in IncrementActivity
            var tcs = Volatile.Read(ref _idleTcs);
            
            // Signal idle state - TrySetResult is thread-safe and idempotent
            // Multiple threads might see count=0 simultaneously, but only first TrySetResult succeeds
            tcs.TrySetResult(true);
        }
    }

    /// <summary>
    /// Returns a Task that completes when the activity counter reaches zero (idle state).
    /// </summary>
    /// <param name="cancellationToken">
    /// Cancellation token to cancel the wait operation.
    /// </param>
    /// <returns>
    /// A Task that completes when counter reaches 0, or throws OperationCanceledException if cancelled.
    /// </returns>
    /// <remarks>
    /// <para><strong>Thread-Safety:</strong></para>
    /// <para>
    /// Uses <see cref="Volatile.Read"/> to snapshot current TCS with acquire fence semantics.
    /// Ensures we observe TCS published via Volatile.Write in <see cref="IncrementActivity"/>.
    /// </para>
    /// <para><strong>Behavior:</strong></para>
    /// <list type="bullet">
    /// <item><description>If already idle (count=0), returns completed Task immediately</description></item>
    /// <item><description>If busy (count>0), returns Task that completes when counter reaches 0</description></item>
    /// <item><description>Multiple callers can await the same Task (TCS supports multiple awaiters)</description></item>
    /// <item><description>If cancelled, throws OperationCanceledException</description></item>
    /// </list>
    /// <para><strong>Idle State Semantics - "WAS Idle" NOT "IS Idle":</strong></para>
    /// <para>
    /// This method completes when the system <strong>was idle at some point in time</strong>.
    /// It does NOT guarantee the system is still idle after completion (new activity may start immediately).
    /// </para>
    /// <para><strong>Race Scenario (Reading Completed TCS):</strong></para>
    /// <para>
    /// Possible execution: T1 decrements to 0 and signals TCS_old, T2 increments to 1 and creates TCS_new,
    /// T3 calls WaitForIdleAsync and reads TCS_old (already completed). Result: WaitForIdleAsync completes immediately
    /// even though count=1. This is CORRECT behavior - system WAS idle between T1 and T2.
    /// </para>
    /// <para><strong>Why This is Correct (Not a Bug):</strong></para>
    /// <para>
    /// Idle detection uses eventual consistency semantics. Observing "was idle recently" is sufficient for
    /// callers like tests (WaitForIdleAsync) and disposal (ensure background work completes). Callers requiring
    /// stronger guarantees must implement application-specific logic (e.g., re-check state after await).
    /// </para>
    /// <para><strong>Cancellation Handling:</strong></para>
    /// <para>
    /// Uses Task.WaitAsync(.NET 6+) for simplified cancellation. If token fires, throws OperationCanceledException.
    /// </para>
    /// </remarks>
    public Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        // Snapshot current TCS with acquire fence (Volatile.Read)
        // Ensures we observe TCS published by Volatile.Write in IncrementActivity
        var tcs = Volatile.Read(ref _idleTcs);
        
        // Use Task.WaitAsync for simplified cancellation (available in .NET 6+)
        // If already completed, returns immediately
        // If pending, waits until signaled or cancellation token fires
        return tcs.Task.WaitAsync(cancellationToken);
    }
}
