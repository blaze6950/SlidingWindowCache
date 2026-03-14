using Intervals.NET.Caching.Infrastructure.Concurrency;
using Intervals.NET.Caching.Infrastructure.Diagnostics;

namespace Intervals.NET.Caching.Infrastructure.Scheduling.Base;

/// <summary>
/// Intermediate base class for serial work schedulers. Adds template-method hooks
/// for supersession and serialization-specific disposal over <see cref="WorkSchedulerBase{TWorkItem}"/>.
/// See docs/shared/components/infrastructure.md for hierarchy and design details.
/// </summary>
/// <typeparam name="TWorkItem">
/// The type of work item processed by this scheduler.
/// Must implement <see cref="ISchedulableWorkItem"/> so the scheduler can cancel and dispose items.
/// </typeparam>
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
        AsyncActivityCounter activityCounter,
        TimeProvider? timeProvider = null)
        : base(executor, debounceProvider, diagnostics, activityCounter, timeProvider)
    {
    }

    /// <summary>
    /// Publishes a work item: disposal guard, activity counter increment, hooks, then enqueue.
    /// </summary>
    /// <param name="workItem">The work item to schedule.</param>
    /// <param name="loopCancellationToken">
    /// Cancellation token from the caller's processing loop.
    /// Used by channel-based strategies to unblock a blocked <c>WriteAsync</c> during disposal.
    /// </param>
    /// <returns>A <see cref="ValueTask"/> that completes when the item is enqueued.</returns>
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

        try
        {
            // Hook for SupersessionWorkSchedulerBase: cancel previous item, record new item.
            // No-op for FIFO serial schedulers.
            OnBeforeEnqueue(workItem);

            // Delegate to the concrete scheduling mechanism (task chaining or channel write).
            return EnqueueWorkItemAsync(workItem, loopCancellationToken);
        }
        catch
        {
            // If enqueue fails, decrement the activity counter to avoid a permanent leak.
            // Successful enqueue paths decrement in the processing pipeline's finally block.
            ActivityCounter.DecrementActivity();
            throw;
        }
    }

    /// <summary>
    /// Hook called before enqueue. Supersession subclasses override to cancel previous item.
    /// </summary>
    private protected virtual void OnBeforeEnqueue(TWorkItem workItem) { }

    /// <summary>
    /// Enqueues the work item using the concrete scheduling mechanism (task chaining or channel write).
    /// </summary>
    private protected abstract ValueTask EnqueueWorkItemAsync(TWorkItem workItem, CancellationToken loopCancellationToken);

    /// <summary>
    /// Calls <see cref="OnBeforeSerialDispose"/> then <see cref="DisposeSerialAsyncCore"/>.
    /// </summary>
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
    /// Hook called before serial disposal. Supersession subclasses override to cancel last item.
    /// </summary>
    private protected virtual void OnBeforeSerialDispose() { }

    /// <summary>
    /// Performs strategy-specific teardown (await task chain or complete channel + await loop).
    /// </summary>
    private protected abstract ValueTask DisposeSerialAsyncCore();
}
