using Intervals.NET.Caching.Infrastructure.Concurrency;
using Intervals.NET.Caching.Infrastructure.Diagnostics;

namespace Intervals.NET.Caching.Infrastructure.Scheduling.Supersession;

/// <summary>
/// Serial work scheduler that serializes work item execution using task continuation chaining
/// and implements supersession semantics: each new published item automatically cancels the previous one.
/// See docs/shared/components/infrastructure.md for design details.
/// </summary>
/// <typeparam name="TWorkItem">
/// The type of work item processed by this scheduler.
/// Must implement <see cref="ISchedulableWorkItem"/> so the scheduler can cancel and dispose items.
/// </typeparam>
internal sealed class UnboundedSupersessionWorkScheduler<TWorkItem>
    : SupersessionWorkSchedulerBase<TWorkItem>
    where TWorkItem : class, ISchedulableWorkItem
{
    // Task chaining state — protected by _chainLock for multi-writer safety.
    // The lock is held only for the duration of the read-chain-write sequence (no awaits),
    // so contention is negligible even under concurrent publishers.
    private readonly object _chainLock = new();
    private Task _currentExecutionTask = Task.CompletedTask;

    /// <summary>
    /// Initializes a new instance of <see cref="UnboundedSupersessionWorkScheduler{TWorkItem}"/>.
    /// </summary>
    /// <param name="executor">Delegate that performs the actual work for a given work item.</param>
    /// <param name="debounceProvider">Returns the current debounce delay.</param>
    /// <param name="diagnostics">Diagnostics for work lifecycle events.</param>
    /// <param name="activityCounter">Activity counter for tracking active operations.</param>
    /// <param name="timeProvider">
    /// Time provider for debounce delays. When <see langword="null"/>,
    /// <see cref="TimeProvider.System"/> is used.
    /// </param>
    public UnboundedSupersessionWorkScheduler(
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
        // write overwrites the first — causing both chained tasks to run concurrently
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
    /// Ensures sequential execution and unconditional ThreadPool dispatch.
    /// </summary>
    /// <param name="previousTask">The previous execution task to await.</param>
    /// <param name="workItem">The work item to execute after the previous task completes.</param>
    private async Task ChainExecutionAsync(Task previousTask, TWorkItem workItem)
    {
        // Immediately yield to the ThreadPool so the entire method body runs on a background thread.
        await Task.Yield();

        try
        {
            await previousTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Previous task failed — log but continue with current execution.
            Diagnostics.WorkFailed(ex);
        }

        try
        {
            await ExecuteWorkItemCoreAsync(workItem).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
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
