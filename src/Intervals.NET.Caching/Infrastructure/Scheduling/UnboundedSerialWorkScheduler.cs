using Intervals.NET.Caching.Infrastructure.Concurrency;

namespace Intervals.NET.Caching.Infrastructure.Scheduling;

/// <summary>
/// Serial work scheduler that serializes work item execution using task continuation chaining.
/// Provides unbounded serialization with minimal memory overhead.
/// </summary>
/// <typeparam name="TWorkItem">
/// The type of work item processed by this scheduler.
/// Must implement <see cref="ISchedulableWorkItem"/> so the scheduler can cancel and dispose items.
/// </typeparam>
/// <remarks>
/// <para><strong>Serialization Mechanism — Lock-Free Task Chaining:</strong></para>
/// <para>
/// Each new work item is chained to await the previous execution's completion before starting
/// its own. This ensures sequential processing with minimal memory overhead:
/// </para>
/// <code>
/// // Conceptual model (simplified):
/// var previousTask = _currentExecutionTask;
/// var newTask = ChainExecutionAsync(previousTask, workItem, cancellationToken);
/// Volatile.Write(ref _currentExecutionTask, newTask);
/// </code>
/// <para>
/// The task chain reference uses volatile write for visibility (single-writer context —
/// only the intent processing loop calls <see cref="PublishWorkItemAsync"/>).
/// No locks are needed. Actual execution always happens asynchronously on the ThreadPool —
/// guaranteed by <c>await Task.Yield()</c> at the very beginning of <see cref="ChainExecutionAsync"/>,
/// which immediately frees the caller's thread so the entire method body (including
/// <c>await previousTask</c> and the executor) runs on the ThreadPool.
/// </para>
/// <para><strong>Single-Writer Guarantee:</strong></para>
/// <para>
/// Each task awaits the previous task's completion before starting, ensuring that NO TWO
/// WORK ITEMS ever execute concurrently. This eliminates write-write race conditions for
/// consumers that mutate shared state (e.g. <c>RebalanceExecutor</c>).
/// </para>
/// <para><strong>Cancellation:</strong></para>
/// <para>
/// When a new item is published, the previous item's
/// <see cref="ISchedulableWorkItem.Cancel"/> is called (by the caller, before
/// <see cref="PublishWorkItemAsync"/>). Each item's <see cref="CancellationToken"/>
/// is checked after the debounce delay and during I/O, allowing early exit.
/// </para>
/// <para><strong>Fire-and-Forget Execution Model:</strong></para>
/// <para>
/// <see cref="PublishWorkItemAsync"/> returns <see cref="ValueTask.CompletedTask"/> immediately
/// after chaining. Execution happens asynchronously on the ThreadPool. Exceptions are captured
/// and reported via <see cref="IWorkSchedulerDiagnostics.WorkFailed"/>.
/// </para>
/// <para><strong>Trade-offs:</strong></para>
/// <list type="bullet">
/// <item><description>✅ Lightweight (single Task reference, no lock object)</description></item>
/// <item><description>✅ Simple implementation (fewer moving parts than channel-based)</description></item>
/// <item><description>✅ No backpressure overhead (caller never blocks)</description></item>
/// <item><description>✅ Lock-free (volatile write for single-writer pattern)</description></item>
/// <item><description>⚠️ Unbounded (can accumulate task chain under extreme sustained load)</description></item>
/// </list>
/// <para><strong>When to Use (default recommendation):</strong></para>
/// <list type="bullet">
/// <item><description>Standard web APIs with typical request patterns</description></item>
/// <item><description>IoT sensor processing with sequential access</description></item>
/// <item><description>Background batch processing</description></item>
/// <item><description>Any scenario where request bursts are temporary</description></item>
/// </list>
/// <para>See also: <see cref="BoundedSerialWorkScheduler{TWorkItem}"/> for the bounded alternative with backpressure.</para>
/// </remarks>
internal sealed class UnboundedSerialWorkScheduler<TWorkItem> : WorkSchedulerBase<TWorkItem>
    where TWorkItem : class, ISchedulableWorkItem
{
    // Task chaining state (volatile write for single-writer pattern)
    private Task _currentExecutionTask = Task.CompletedTask;

    /// <summary>
    /// Initializes a new instance of <see cref="UnboundedSerialWorkScheduler{TWorkItem}"/>.
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
    /// <remarks>
    /// <para><strong>Initialization:</strong></para>
    /// <para>
    /// Initializes the task chain with a completed task. The first published work item chains
    /// to this completed task, starting the execution chain. All subsequent items chain to
    /// the previous execution.
    /// </para>
    /// <para><strong>Execution Model:</strong></para>
    /// <para>
    /// Unlike the channel-based approach, there is no background loop started at construction.
    /// Executions are scheduled on-demand via task chaining when
    /// <see cref="PublishWorkItemAsync"/> is called.
    /// </para>
    /// </remarks>
    public UnboundedSerialWorkScheduler(
        Func<TWorkItem, CancellationToken, Task> executor,
        Func<TimeSpan> debounceProvider,
        IWorkSchedulerDiagnostics diagnostics,
        AsyncActivityCounter activityCounter
    ) : base(executor, debounceProvider, diagnostics, activityCounter)
    {
    }

    /// <summary>
    /// Publishes a work item by chaining it to the previous execution task.
    /// Returns immediately (fire-and-forget).
    /// </summary>
    /// <param name="workItem">The work item to schedule.</param>
    /// <param name="loopCancellationToken">
    /// Accepted for API consistency; not used by the task-based strategy (never blocks).
    /// </param>
    /// <returns><see cref="ValueTask.CompletedTask"/> — always completes synchronously.</returns>
    /// <remarks>
    /// <para><strong>Task Chaining Behavior:</strong></para>
    /// <para>
    /// Chains the new work item to the current execution task using volatile write for visibility.
    /// The chaining operation is lock-free (single-writer context).
    /// Returns immediately after chaining — actual execution always happens asynchronously on the
    /// ThreadPool, guaranteed by <c>await Task.Yield()</c> in <see cref="ChainExecutionAsync"/>.
    /// </para>
    /// <para><strong>Activity Counter:</strong></para>
    /// <para>
    /// Increments the activity counter before chaining; the base class pipeline decrements it
    /// in the <c>finally</c> block after execution completes/cancels/fails.
    /// </para>
    /// </remarks>
    public override ValueTask PublishWorkItemAsync(TWorkItem workItem, CancellationToken loopCancellationToken)
    {
        if (IsDisposed)
        {
            throw new ObjectDisposedException(
                nameof(UnboundedSerialWorkScheduler<TWorkItem>),
                "Cannot publish a work item to a disposed scheduler.");
        }

        // Increment activity counter for the new work item
        ActivityCounter.IncrementActivity();

        // Store as last work item (for cancellation coordination and pending-state inspection)
        StoreLastWorkItem(workItem);

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
    /// <returns>A Task representing the chained execution operation.</returns>
    /// <remarks>
    /// <para><strong>ThreadPool Guarantee — <c>await Task.Yield()</c>:</strong></para>
    /// <para>
    /// <c>await Task.Yield()</c> is the very first statement. Because <see cref="PublishWorkItemAsync"/>
    /// calls this method fire-and-forget (not awaited), the async state machine starts executing
    /// synchronously on the caller's thread until the first genuine yield point. By placing
    /// <c>Task.Yield()</c> first, the caller's thread is freed immediately and the entire method
    /// body — including <c>await previousTask</c>, its exception handler, and
    /// <c>ExecuteWorkItemCoreAsync</c> — runs on the ThreadPool.
    /// </para>
    /// <para>
    /// Sequential ordering is fully preserved: <c>await previousTask</c> still blocks execution
    /// of the current work item until the previous one completes — it just does so on a
    /// ThreadPool thread rather than the caller's thread.
    /// </para>
    /// <para><strong>Exception Handling:</strong></para>
    /// <para>
    /// Exceptions from the previous task are captured and reported via diagnostics.
    /// This prevents unobserved task exceptions and follows the "Background Path Exceptions"
    /// pattern from AGENTS.md. Each execution is independent — a previous failure does not
    /// block the current item.
    /// </para>
    /// </remarks>
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
    private protected override async ValueTask DisposeAsyncCore()
    {
        // Capture current task chain reference (volatile read — no lock needed)
        var currentTask = Volatile.Read(ref _currentExecutionTask);

        // Wait for task chain to complete gracefully
        await currentTask.ConfigureAwait(false);
    }
}
