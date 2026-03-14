using Intervals.NET.Caching.Infrastructure.Concurrency;
using Intervals.NET.Caching.Infrastructure.Diagnostics;
using Intervals.NET.Caching.Infrastructure.Scheduling.Base;

namespace Intervals.NET.Caching.Infrastructure.Scheduling.Serial;

/// <summary>
/// Serial work scheduler that serializes work item execution using task continuation chaining.
/// See docs/shared/components/infrastructure.md for design details.
/// </summary>
/// <typeparam name="TWorkItem">
/// The type of work item processed by this scheduler.
/// Must implement <see cref="ISchedulableWorkItem"/> so the scheduler can cancel and dispose items.
/// </typeparam>
internal sealed class UnboundedSerialWorkScheduler<TWorkItem> : SerialWorkSchedulerBase<TWorkItem>
    where TWorkItem : class, ISchedulableWorkItem
{
    // Task chaining state — protected by _chainLock for multi-writer safety.
    // The lock is held only for the duration of the read-chain-write sequence (no awaits),
    // so contention is negligible even under concurrent publishers.
    private readonly object _chainLock = new();
    private Task _currentExecutionTask = Task.CompletedTask;

    /// <summary>
    /// Initializes a new instance of <see cref="UnboundedSerialWorkScheduler{TWorkItem}"/>.
    /// </summary>
    /// <param name="executor">Delegate that performs the actual work for a given work item.</param>
    /// <param name="debounceProvider">Returns the current debounce delay.</param>
    /// <param name="diagnostics">Diagnostics for work lifecycle events.</param>
    /// <param name="activityCounter">Activity counter for tracking active operations.</param>
    /// <param name="timeProvider">
    /// Time provider for debounce delays. When <see langword="null"/>,
    /// <see cref="TimeProvider.System"/> is used.
    /// </param>
    public UnboundedSerialWorkScheduler(
        Func<TWorkItem, CancellationToken, Task> executor,
        Func<TimeSpan> debounceProvider,
        IWorkSchedulerDiagnostics diagnostics,
        AsyncActivityCounter activityCounter,
        TimeProvider? timeProvider = null
    ) : base(executor, debounceProvider, diagnostics, activityCounter, timeProvider)
    {
    }

    /// <summary>
    /// Enqueues the work item by chaining it to the previous execution task.
    /// Returns immediately (fire-and-forget).
    /// Uses a lock to make the read-chain-write sequence atomic, ensuring serialization
    /// is preserved even under concurrent publishers.
    /// </summary>
    /// <param name="workItem">The work item to schedule.</param>
    /// <param name="loopCancellationToken">
    /// Accepted for API consistency; not used by the task-based strategy (never blocks).
    /// </param>
    /// <returns><see cref="ValueTask.CompletedTask"/> — always completes synchronously.</returns>
    private protected override ValueTask EnqueueWorkItemAsync(TWorkItem workItem, CancellationToken loopCancellationToken)
    {
        // Atomically read the previous task, chain to it, and write the new task.
        // The lock guards the non-atomic read-chain-write sequence: without it, two concurrent
        // publishers can both capture the same previousTask, both chain to it, and the second
        // Volatile.Write overwrites the first — causing both chained tasks to run concurrently
        // (breaking serialization) and orphaning the overwritten chain from disposal.
        // The lock is never held across an await, so contention duration is minimal.

        lock (_chainLock)
        {
            _currentExecutionTask = ChainExecutionAsync(_currentExecutionTask, workItem);
        }

        // Return immediately — fire-and-forget execution model
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Chains a new work item to await the previous task's completion before executing.
    /// </summary>
    /// <param name="previousTask">The previous execution task to await.</param>
    /// <param name="workItem">The work item to execute after the previous task completes.</param>
    /// <returns>A Task representing the chained execution operation.</returns>
    private async Task ChainExecutionAsync(Task previousTask, TWorkItem workItem)
    {
        // Immediately yield to the ThreadPool so the entire method body runs on a background thread.
        // This frees the caller's thread at once and guarantees background-thread execution even when:
        //   (a) the executor is fully synchronous (returns Task.CompletedTask immediately), or
        //   (b) previousTask is already completed (await below would otherwise return synchronously).
        // Sequential ordering is preserved: await previousTask still blocks the current work item
        // until the previous one finishes — it just does so on a ThreadPool thread, not the caller's.
        await Task.Yield();

        try
        {
            // Await previous task completion (enforces sequential execution).
            await previousTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Previous task failed — log but continue with current execution.
            // Each work item is independent; a previous failure should not block the current one.
            Diagnostics.WorkFailed(ex);
        }

        try
        {
            // Execute current work item via the shared pipeline
            await ExecuteWorkItemCoreAsync(workItem).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // ExecuteWorkItemCoreAsync already handles exceptions internally, but catch here for safety
            Diagnostics.WorkFailed(ex);
        }
    }

    /// <inheritdoc/>
    private protected override async ValueTask DisposeSerialAsyncCore()
    {
        // Capture current task chain reference under the lock so we get the latest chain,
        // not a stale reference that might be overwritten by a concurrent publisher
        // racing with disposal.
        Task currentTask;
        lock (_chainLock)
        {
            currentTask = _currentExecutionTask;
        }

        // Wait for task chain to complete gracefully
        await currentTask.ConfigureAwait(false);
    }
}
