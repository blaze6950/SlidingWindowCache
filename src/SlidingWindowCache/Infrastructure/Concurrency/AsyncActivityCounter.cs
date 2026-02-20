namespace SlidingWindowCache.Infrastructure.Concurrency;

/// <summary>
/// Thread-safe activity counter that provides awaitable idle state notification.
/// Tracks active operations using atomic counter and signals completion via TaskCompletionSource.
/// </summary>
/// <remarks>
/// <para><strong>Thread-Safety Model:</strong></para>
/// <para>
/// This class uses lock-free atomic operations (Interlocked) for counter manipulation
/// and careful synchronization for TaskCompletionSource lifecycle management.
/// </para>
/// <para><strong>Usage Pattern:</strong></para>
/// <list type="number">
/// <item><description>Call <see cref="IncrementActivity"/> when starting work (user thread or processing loop)</description></item>
/// <item><description>Call <see cref="DecrementActivity"/> in finally block when work completes (processing loop)</description></item>
/// <item><description>Await <see cref="WaitForIdleAsync"/> to wait for all active operations to complete</description></item>
/// </list>
/// <para><strong>Idle State:</strong></para>
/// <para>
/// Counter starts at 0 (idle). When counter transitions from 0→1, a new TCS is created.
/// When counter transitions from N→0, the TCS is signaled. Multiple waiters can await the same TCS.
/// </para>
/// </remarks>
/// TODO try to analyze this implementation - maybe we can avoid using lock at all?
/// TODO consider about implementing this using SemaphoreSlim. I guess we can use it for signalling and avoids TCS allocations
internal sealed class AsyncActivityCounter
{
    // Activity counter - incremented when work starts, decremented when work finishes
    private int _activityCount;

    // Lock for synchronizing TCS creation and completion
    // Used only during idle→busy and busy→idle transitions, not on every increment/decrement
    private readonly object _tcsLock = new();

    // Current TaskCompletionSource - signaled when counter reaches 0
    // Protected by _tcsLock for creation/replacement, but reading can be done outside lock
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
    /// <para><strong>Thread-Safety:</strong></para>
    /// <para>
    /// Uses Interlocked.Increment for atomic counter manipulation.
    /// TCS creation is synchronized via lock to prevent races during idle→busy transition.
    /// </para>
    /// <para><strong>Call Sites:</strong></para>
    /// <list type="bullet">
    /// <item><description>UserRequestHandler.PublishIntent() - when user publishes intent</description></item>
    /// <item><description>IntentController.ProcessIntentsAsync() - before enqueuing execution request</description></item>
    /// </list>
    /// </remarks>
    public void IncrementActivity()
    {
        var newCount = Interlocked.Increment(ref _activityCount);

        // Check if this is a transition from idle (0) to busy (1)
        if (newCount == 1)
        {
            lock (_tcsLock)
            {
                // Double-check inside lock - another thread might have already created new TCS
                // If current TCS is not completed, we're already busy (race with another increment)
                if (_idleTcs.Task.IsCompleted)
                {
                    // Create new TCS for this busy period
                    _idleTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
        }
    }

    /// <summary>
    /// Decrements the activity counter atomically.
    /// If this is a transition from busy to idle (counter reaches 0), signals the TaskCompletionSource.
    /// </summary>
    /// <remarks>
    /// <para><strong>Thread-Safety:</strong></para>
    /// <para>
    /// Uses Interlocked.Decrement for atomic counter manipulation.
    /// TCS.TrySetResult is inherently thread-safe (only first call succeeds, others are no-ops).
    /// </para>
    /// <para><strong>Call Sites:</strong></para>
    /// <list type="bullet">
    /// <item><description>IntentController.ProcessIntentsAsync() - in finally block after processing intent</description></item>
    /// <item><description>RebalanceExecutionController.ProcessExecutionRequestsAsync() - in finally block after execution</description></item>
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
            // Signal idle state - TrySetResult is thread-safe (idempotent)
            // Multiple threads might see count=0 simultaneously, but only first TrySetResult succeeds
            lock (_tcsLock)
            {
                // Signal the current TCS - this is thread-safe
                _idleTcs.TrySetResult(true);
            }
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
    /// <para><strong>Behavior:</strong></para>
    /// <list type="bullet">
    /// <item><description>If already idle (count=0), returns completed Task immediately</description></item>
    /// <item><description>If busy (count>0), returns Task that completes when counter reaches 0</description></item>
    /// <item><description>Multiple callers can await the same Task</description></item>
    /// <item><description>If cancelled, throws OperationCanceledException</description></item>
    /// </list>
    /// <para><strong>Race Handling:</strong></para>
    /// <para>
    /// New activity might start immediately after this method observes idle state.
    /// Caller must use application-specific logic to determine true quiescence
    /// (e.g., re-check after await completes).
    /// </para>
    /// </remarks>
    public async Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        // Snapshot current TCS
        TaskCompletionSource<bool> tcs;
        lock (_tcsLock)
        {
            tcs = _idleTcs;
        }

        // If cancellation is not requested, we can just await the TCS task
        if (!cancellationToken.CanBeCanceled)
        {
            await tcs.Task.ConfigureAwait(false);
            return;
        }

        // Handle cancellation by racing the TCS task against cancellation token
        var tcsTask = tcs.Task;
        var cancellationTask = Task.Delay(Timeout.Infinite, cancellationToken);

        var completedTask = await Task.WhenAny(tcsTask, cancellationTask).ConfigureAwait(false);

        if (completedTask == cancellationTask)
        {
            // Cancellation won the race
            cancellationToken.ThrowIfCancellationRequested();
        }

        // Idle task completed - ensure it didn't fault
        await tcsTask.ConfigureAwait(false);
    }
}
