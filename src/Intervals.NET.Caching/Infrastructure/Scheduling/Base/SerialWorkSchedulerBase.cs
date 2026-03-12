using Intervals.NET.Caching.Infrastructure.Concurrency;
using Intervals.NET.Caching.Infrastructure.Diagnostics;
using Intervals.NET.Caching.Infrastructure.Scheduling.Supersession;

namespace Intervals.NET.Caching.Infrastructure.Scheduling.Base;

/// <summary>
/// Intermediate abstract base class for serial work scheduler implementations.
/// Extends <see cref="WorkSchedulerBase{TWorkItem}"/> with serialization-specific concerns:
/// a template-method <see cref="PublishWorkItemAsync"/> that handles the shared guards and hooks,
/// and a template-method disposal path that allows subclasses to inject pre-teardown behaviour.
/// </summary>
/// <typeparam name="TWorkItem">
/// The type of work item processed by this scheduler.
/// Must implement <see cref="ISchedulableWorkItem"/> so the scheduler can cancel and dispose items.
/// </typeparam>
/// <remarks>
/// <para><strong>Hierarchy:</strong></para>
/// <code>
/// WorkSchedulerBase&lt;TWorkItem&gt;                   — generic execution pipeline, disposal guard
///   └── SerialWorkSchedulerBase&lt;TWorkItem&gt;       — template Publish + Dispose; ISerialWorkScheduler
///         ├── UnboundedSerialWorkScheduler        — task chaining (FIFO)
///         ├── BoundedSerialWorkScheduler          — channel-based (FIFO)
///         └── SupersessionWorkSchedulerBase       — cancel-previous + LastWorkItem; ISupersessionWorkScheduler
///               ├── UnboundedSupersessionWorkScheduler — task chaining (supersession)
///               └── BoundedSupersessionWorkScheduler   — channel-based (supersession)
/// </code>
/// <para><strong>Template Method — PublishWorkItemAsync:</strong></para>
/// <para>
/// <see cref="PublishWorkItemAsync"/> is implemented here as a sealed template method that:
/// </para>
/// <list type="number">
/// <item><description>Guards against publish after disposal.</description></item>
/// <item><description>Increments the activity counter.</description></item>
/// <item><description>Calls <see cref="OnBeforeEnqueue"/> — virtual no-op; supersession subclasses override to cancel the previous item and store the new one.</description></item>
/// <item><description>Calls <see cref="EnqueueWorkItemAsync"/> — abstract; concrete classes implement the scheduling mechanism (task chaining or channel write).</description></item>
/// </list>
/// <para><strong>Template Method — DisposeAsyncCore:</strong></para>
/// <para>
/// <see cref="WorkSchedulerBase{TWorkItem}.DisposeAsyncCore"/> is overridden here as a sealed
/// template that:
/// </para>
/// <list type="number">
/// <item><description>Calls <see cref="OnBeforeSerialDispose"/> — virtual no-op; supersession subclasses override to cancel the last in-flight item, allowing early exit from debounce or I/O.</description></item>
/// <item><description>Calls <see cref="DisposeSerialAsyncCore"/> — abstract; concrete classes stop their serialization mechanism (await chain / complete channel + await loop).</description></item>
/// </list>
/// <para>
/// After <see cref="DisposeSerialAsyncCore"/> returns, all work items have passed through the
/// <see cref="WorkSchedulerBase{TWorkItem}.ExecuteWorkItemCoreAsync"/> <c>finally</c> block
/// and have been disposed. No separate dispose-last-item step is needed.
/// </para>
/// <para><strong>Why Two Layers (Serial vs Supersession):</strong></para>
/// <para>
/// <see cref="WorkSchedulerBase{TWorkItem}"/> is intentionally generic — it only owns logic
/// that is identical for ALL scheduler types (execution pipeline, disposal guard, diagnostics,
/// activity counter). This class adds serial-specific concerns (template hooks, serialization
/// teardown). The supersession concern (cancel-previous, <c>LastWorkItem</c> tracking) is a
/// further specialisation owned by <see cref="SupersessionWorkSchedulerBase{TWorkItem}"/> and
/// exposed via <see cref="ISupersessionWorkScheduler{TWorkItem}"/>.
/// </para>
/// </remarks>
internal abstract class SerialWorkSchedulerBase<TWorkItem> : WorkSchedulerBase<TWorkItem>, ISerialWorkScheduler<TWorkItem>
    where TWorkItem : class, ISchedulableWorkItem
{
    /// <summary>
    /// Initializes the shared fields.
    /// </summary>
    private protected SerialWorkSchedulerBase(
        Func<TWorkItem, CancellationToken, Task> executor,
        Func<TimeSpan> debounceProvider,
        IWorkSchedulerDiagnostics diagnostics,
        AsyncActivityCounter activityCounter)
        : base(executor, debounceProvider, diagnostics, activityCounter)
    {
    }

    /// <summary>
    /// Publishes a work item using the template-method pattern.
    /// Handles the disposal guard, activity counter increment, and the two virtual hooks
    /// before delegating to the concrete scheduling mechanism.
    /// </summary>
    /// <param name="workItem">The work item to schedule.</param>
    /// <param name="loopCancellationToken">
    /// Cancellation token from the caller's processing loop.
    /// Forwarded to <see cref="EnqueueWorkItemAsync"/> for channel-based strategies that
    /// may need to unblock a blocked <c>WriteAsync</c> during disposal.
    /// </param>
    /// <returns>
    /// A <see cref="ValueTask"/> that completes synchronously for task-based strategies
    /// and asynchronously for channel-based strategies when the channel is full (backpressure).
    /// </returns>
    /// <remarks>
    /// <para><strong>Template Steps:</strong></para>
    /// <list type="number">
    /// <item><description>Disposal guard — throws <see cref="ObjectDisposedException"/> if already disposed.</description></item>
    /// <item><description><see cref="ActivityCounter"/> increment — counted before enqueue so the counter is accurate from the moment the item is accepted.</description></item>
    /// <item><description><see cref="OnBeforeEnqueue"/> — supersession subclasses cancel the previous item and record the new one here.</description></item>
    /// <item><description><see cref="EnqueueWorkItemAsync"/> — concrete strategy-specific enqueue (task chaining or channel write).</description></item>
    /// </list>
    /// </remarks>
    public sealed override ValueTask PublishWorkItemAsync(TWorkItem workItem, CancellationToken loopCancellationToken)
    {
        if (IsDisposed)
        {
            throw new ObjectDisposedException(
                GetType().Name,
                "Cannot publish a work item to a disposed scheduler.");
        }

        // Increment activity counter before enqueue so it is accurate from the moment
        // the item is accepted. The base-class pipeline decrements it in the finally block
        // after execution completes, cancels, or fails (or in the error path of EnqueueWorkItemAsync).
        ActivityCounter.IncrementActivity();

        // Hook for SupersessionWorkSchedulerBase: cancel previous item, record new item.
        // No-op for FIFO serial schedulers.
        OnBeforeEnqueue(workItem);

        // Delegate to the concrete scheduling mechanism (task chaining or channel write).
        return EnqueueWorkItemAsync(workItem, loopCancellationToken);
    }

    /// <summary>
    /// Called inside <see cref="PublishWorkItemAsync"/> after the activity counter is incremented
    /// and before the work item is passed to <see cref="EnqueueWorkItemAsync"/>.
    /// </summary>
    /// <param name="workItem">The work item about to be enqueued.</param>
    /// <remarks>
    /// The default implementation is a no-op.
    /// <see cref="SupersessionWorkSchedulerBase{TWorkItem}"/> overrides this to cancel the
    /// previous work item and store the new one as <c>LastWorkItem</c>.
    /// </remarks>
    private protected virtual void OnBeforeEnqueue(TWorkItem workItem) { }

    /// <summary>
    /// Enqueues the work item using the concrete scheduling mechanism.
    /// Called by <see cref="PublishWorkItemAsync"/> after all shared guards and hooks have run.
    /// </summary>
    /// <param name="workItem">The work item to enqueue.</param>
    /// <param name="loopCancellationToken">
    /// Cancellation token from the caller's processing loop.
    /// Used by channel-based strategies to unblock a blocked <c>WriteAsync</c> during disposal.
    /// Task-based strategies may ignore this parameter.
    /// </param>
    /// <returns>
    /// A <see cref="ValueTask"/> that completes synchronously for task-based strategies
    /// and asynchronously for channel-based strategies when the channel is full (backpressure).
    /// </returns>
    /// <remarks>
    /// Implementations are responsible for handling their own error paths (e.g. channel write
    /// failure): they must call <see cref="WorkSchedulerBase{TWorkItem}.ActivityCounter"/>
    /// <c>.DecrementActivity()</c> and dispose the work item if the enqueue fails without
    /// passing the item through <see cref="WorkSchedulerBase{TWorkItem}.ExecuteWorkItemCoreAsync"/>.
    /// </remarks>
    private protected abstract ValueTask EnqueueWorkItemAsync(TWorkItem workItem, CancellationToken loopCancellationToken);

    /// <summary>
    /// Cancels the last work item (if any) to signal early exit from debounce or I/O,
    /// then delegates to <see cref="DisposeSerialAsyncCore"/> for strategy-specific teardown.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called by <see cref="WorkSchedulerBase{TWorkItem}.DisposeAsync"/> after the idempotent
    /// disposal guard fires.
    /// </para>
    /// <para>
    /// After <see cref="DisposeSerialAsyncCore"/> returns, all in-flight work items have passed
    /// through the <see cref="WorkSchedulerBase{TWorkItem}.ExecuteWorkItemCoreAsync"/>
    /// <c>finally</c> block and been disposed — no separate dispose-last-item step is needed.
    /// </para>
    /// </remarks>
    private protected sealed override async ValueTask DisposeAsyncCore()
    {
        // Hook for SupersessionWorkSchedulerBase: cancel the last in-flight item so it can exit
        // early from debounce or I/O before we await the chain / execution loop.
        // No-op for FIFO serial schedulers.
        OnBeforeSerialDispose();

        // Strategy-specific teardown (await task chain / complete channel + await loop task).
        await DisposeSerialAsyncCore().ConfigureAwait(false);
    }

    /// <summary>
    /// Called at the start of <see cref="DisposeAsyncCore"/> before
    /// <see cref="DisposeSerialAsyncCore"/> is awaited.
    /// </summary>
    /// <remarks>
    /// The default implementation is a no-op.
    /// <see cref="SupersessionWorkSchedulerBase{TWorkItem}"/> overrides this to cancel the
    /// last work item, allowing early exit from debounce or I/O.
    /// </remarks>
    private protected virtual void OnBeforeSerialDispose() { }

    /// <summary>
    /// Performs strategy-specific teardown during disposal.
    /// Called after <see cref="OnBeforeSerialDispose"/> has run.
    /// </summary>
    /// <remarks>
    /// Implementations should stop the serialization mechanism here:
    /// <list type="bullet">
    /// <item><description><strong>Task-based:</strong> await the current task chain</description></item>
    /// <item><description><strong>Channel-based:</strong> complete the channel writer and await the loop task</description></item>
    /// </list>
    /// </remarks>
    private protected abstract ValueTask DisposeSerialAsyncCore();
}
