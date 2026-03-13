using Intervals.NET.Caching.Infrastructure.Concurrency;
using Intervals.NET.Caching.Infrastructure.Diagnostics;
using Intervals.NET.Caching.Infrastructure.Scheduling.Serial;

namespace Intervals.NET.Caching.Infrastructure.Scheduling.Supersession;

/// <summary>
/// Serial work scheduler that serializes work item execution using task continuation chaining
/// and implements supersession semantics: each new published item automatically cancels the previous one.
/// </summary>
/// <typeparam name="TWorkItem">
/// The type of work item processed by this scheduler.
/// Must implement <see cref="ISchedulableWorkItem"/> so the scheduler can cancel and dispose items.
/// </typeparam>
/// <remarks>
/// <para><strong>Supersession Semantics:</strong></para>
/// <para>
/// When <see cref="IWorkScheduler{TWorkItem}.PublishWorkItemAsync"/> is called, the scheduler
/// automatically cancels the previously published work item (if any) before enqueuing the new one.
/// Only the most recently published item represents the intended pending work; all earlier items
/// are considered superseded and will exit early from debounce or I/O when possible.
/// Callers must NOT cancel the previous item themselves — this is the scheduler's responsibility.
/// </para>
/// <para><strong>Serialization Mechanism — Lock-Free Task Chaining:</strong></para>
/// <para>
/// Each new work item is chained to await the previous execution's completion before starting
/// its own, guaranteeing sequential (one-at-a-time) execution with minimal memory overhead.
/// Actual execution always happens asynchronously on the ThreadPool — guaranteed by
/// <c>await Task.Yield()</c> at the start of the chain method.
/// </para>
/// <para><strong>Single-Writer Guarantee:</strong></para>
/// <para>
/// Each task awaits the previous task's completion before starting, ensuring NO TWO WORK ITEMS
/// ever execute concurrently. This is the foundational invariant for consumers that perform
/// single-writer mutations (e.g. <c>RebalanceExecutor</c>).
/// </para>
/// <para><strong>Trade-offs:</strong></para>
/// <list type="bullet">
/// <item><description>✅ Lightweight (single Task reference, no lock object)</description></item>
/// <item><description>✅ No backpressure overhead (caller never blocks)</description></item>
/// <item><description>✅ Lock-free (volatile write for single-writer pattern)</description></item>
/// <item><description>✅ Automatic cancel-previous on publish</description></item>
/// <item><description>⚠️ Unbounded (can accumulate task chain under extreme sustained load)</description></item>
/// </list>
/// <para><strong>When to Use (default recommendation for supersession):</strong></para>
/// <list type="bullet">
/// <item><description>Rebalance execution scheduling in SlidingWindow cache (default)</description></item>
/// <item><description>Any scenario where only the latest request matters and earlier ones may be abandoned</description></item>
/// </list>
/// <para>See also: <see cref="BoundedSupersessionWorkScheduler{TWorkItem}"/> for the bounded supersession alternative with backpressure.</para>
/// <para>See also: <see cref="UnboundedSerialWorkScheduler{TWorkItem}"/> for the unbounded FIFO variant (no supersession).</para>
/// </remarks>
internal sealed class UnboundedSupersessionWorkScheduler<TWorkItem>
    : SupersessionWorkSchedulerBase<TWorkItem>
    where TWorkItem : class, ISchedulableWorkItem
{
    // Task chaining state (volatile write for single-writer pattern)
    private Task _currentExecutionTask = Task.CompletedTask;

    /// <summary>
    /// Initializes a new instance of <see cref="UnboundedSupersessionWorkScheduler{TWorkItem}"/>.
    /// </summary>
    /// <param name="executor">
    /// Delegate that performs the actual work for a given work item.
    /// Called once per item after the debounce delay, unless cancelled beforehand.
    /// </param>
    /// <param name="debounceProvider">
    /// Returns the current debounce delay. Snapshotted at the start of each execution
    /// to pick up any runtime changes ("next cycle" semantics).
    /// </param>
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
    /// </summary>
    /// <param name="workItem">The work item to schedule.</param>
    /// <param name="loopCancellationToken">
    /// Accepted for API consistency; not used by the task-based strategy (never blocks).
    /// </param>
    /// <returns><see cref="ValueTask.CompletedTask"/> — always completes synchronously.</returns>
    private protected override ValueTask EnqueueWorkItemAsync(TWorkItem workItem, CancellationToken loopCancellationToken)
    {
        // Chain execution to previous task (lock-free using volatile write — single-writer context)
        var previousTask = Volatile.Read(ref _currentExecutionTask);
        var newTask = ChainExecutionAsync(previousTask, workItem);
        Volatile.Write(ref _currentExecutionTask, newTask);

        // Return immediately — fire-and-forget execution model
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Chains a new work item to await the previous task's completion before executing.
    /// Ensures sequential execution (single-writer guarantee) and unconditional ThreadPool dispatch.
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
        // Capture current task chain reference (volatile read — no lock needed)
        var currentTask = Volatile.Read(ref _currentExecutionTask);

        // Wait for task chain to complete gracefully
        await currentTask.ConfigureAwait(false);
    }
}
