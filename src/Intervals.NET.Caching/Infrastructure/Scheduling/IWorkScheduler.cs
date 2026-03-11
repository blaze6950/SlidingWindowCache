namespace Intervals.NET.Caching.Infrastructure.Scheduling;

/// <summary>
/// Abstraction for serialization strategies that schedule and execute work items one at a time.
/// </summary>
/// <typeparam name="TWorkItem">
/// The type of work item processed by this scheduler.
/// Must implement <see cref="ISchedulableWorkItem"/> so the scheduler can cancel and dispose items.
/// </typeparam>
/// <remarks>
/// <para><strong>Architectural Role — Cache-Agnostic Work Serializer:</strong></para>
/// <para>
/// This interface abstracts the mechanism for serializing work item execution.
/// The concrete implementation determines how work items are queued, scheduled,
/// and serialized to ensure at most one active execution at a time.
/// </para>
/// <para><strong>Implementations:</strong></para>
/// <list type="bullet">
/// <item><description>
/// <see cref="UnboundedSerialWorkScheduler{TWorkItem}"/> —
/// Unbounded task chaining; lightweight, default recommendation for most scenarios.
/// </description></item>
/// <item><description>
/// <see cref="BoundedSerialWorkScheduler{TWorkItem}"/> —
/// Bounded channel with backpressure; for high-frequency or resource-constrained scenarios.
/// </description></item>
/// </list>
/// <para><strong>Single-Writer Guarantee:</strong></para>
/// <para>
/// All implementations MUST guarantee serialized execution: no two work items may execute
/// concurrently. This is the foundational invariant that allows consumers (such as
/// SlidingWindow's <c>RebalanceExecutor</c>) to perform single-writer mutations without locks.
/// </para>
/// <para><strong>Supersession and Cancellation:</strong></para>
/// <para>
/// When a new work item is published, the previous item's
/// <see cref="ISchedulableWorkItem.Cancel"/> is called so it can exit early from debounce
/// or I/O. The scheduler tracks the most recently published item via
/// <see cref="LastWorkItem"/>, which callers (e.g. IntentController) use for cancellation
/// coordination and pending-state inspection.
/// </para>
/// <para><strong>Execution Context:</strong></para>
/// <para>
/// All implementations execute work on background threads (ThreadPool). The caller's
/// (user-facing) path is never blocked. The task-based implementation enforces this via
/// <c>await Task.Yield()</c> as the very first statement of <c>ChainExecutionAsync</c>,
/// which immediately frees the caller's thread so the entire method body — including
/// <c>await previousTask</c> and the executor — runs on the ThreadPool.
/// </para>
/// </remarks>
internal interface IWorkScheduler<TWorkItem> : IAsyncDisposable
    where TWorkItem : class, ISchedulableWorkItem
{
    /// <summary>
    /// Publishes a work item to be processed according to the scheduler's serialization strategy.
    /// </summary>
    /// <param name="workItem">The work item to schedule for execution.</param>
    /// <param name="loopCancellationToken">
    /// Cancellation token from the caller's processing loop.
    /// Used by the channel-based strategy to unblock a blocked <c>WriteAsync</c> during disposal.
    /// The task-based strategy accepts the parameter for API consistency but does not use it.
    /// </param>
    /// <returns>
    /// A <see cref="ValueTask"/> that completes synchronously for the unbounded serial strategy
    /// (fire-and-forget) or asynchronously for the bounded serial strategy when the channel is full
    /// (backpressure).
    /// </returns>
    /// <remarks>
    /// <para><strong>Strategy-Specific Behavior:</strong></para>
    /// <list type="bullet">
    /// <item><description>
    /// <strong>Unbounded Serial (<see cref="UnboundedSerialWorkScheduler{TWorkItem}"/>):</strong> chains the new item to the previous task and returns immediately.
    /// </description></item>
    /// <item><description>
    /// <strong>Bounded Serial (<see cref="BoundedSerialWorkScheduler{TWorkItem}"/>):</strong> enqueues the item; awaits <c>WriteAsync</c> if the channel
    /// is at capacity, creating intentional backpressure on the caller's loop.
    /// </description></item>
    /// </list>
    /// </remarks>
    ValueTask PublishWorkItemAsync(TWorkItem workItem, CancellationToken loopCancellationToken);

    /// <summary>
    /// Gets the most recently published work item, or <see langword="null"/> if none has been published yet.
    /// </summary>
    /// <remarks>
    /// <para><strong>Usage:</strong></para>
    /// <para>
    /// Callers (e.g. IntentController) read this before publishing a new item to cancel the
    /// previous pending execution and to inspect the pending desired state (e.g.
    /// <c>DesiredNoRebalanceRange</c>) for anti-thrashing decisions.
    /// </para>
    /// <para><strong>Thread Safety:</strong></para>
    /// <para>Implementations use <c>Volatile.Read</c> to ensure cross-thread visibility.</para>
    /// </remarks>
    TWorkItem? LastWorkItem { get; }
}
