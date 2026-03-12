using Intervals.NET.Caching.Infrastructure.Concurrency;
using Intervals.NET.Caching.Infrastructure.Diagnostics;
using Intervals.NET.Caching.Infrastructure.Scheduling.Base;

namespace Intervals.NET.Caching.Infrastructure.Scheduling.Supersession;

/// <summary>
/// Intermediate abstract base class for supersession work scheduler implementations.
/// Extends <see cref="SerialWorkSchedulerBase{TWorkItem}"/> with the supersession contract:
/// when a new work item is published, the previously published (still-pending) item is
/// automatically cancelled before the new item is enqueued.
/// </summary>
/// <typeparam name="TWorkItem">
/// The type of work item processed by this scheduler.
/// Must implement <see cref="ISchedulableWorkItem"/> so the scheduler can cancel and dispose items.
/// </typeparam>
/// <remarks>
/// <para><strong>Hierarchy:</strong></para>
/// <code>
/// SerialWorkSchedulerBase&lt;TWorkItem&gt;                 — template Publish + Dispose; ISerialWorkScheduler
///   └── SupersessionWorkSchedulerBase&lt;TWorkItem&gt;     — cancel-previous + LastWorkItem; ISupersessionWorkScheduler
///         ├── UnboundedSupersessionWorkScheduler       — task chaining (EnqueueWorkItemAsync + DisposeSerialAsyncCore)
///         └── BoundedSupersessionWorkScheduler         — channel-based (EnqueueWorkItemAsync + DisposeSerialAsyncCore)
/// </code>
/// <para><strong>Supersession Contract:</strong></para>
/// <para>
/// Overrides <see cref="SerialWorkSchedulerBase{TWorkItem}.OnBeforeEnqueue"/> to cancel the
/// previous <see cref="LastWorkItem"/> (if any) and record the new item via
/// <c>Volatile.Write</c> before it is passed to <c>EnqueueWorkItemAsync</c>.
/// Overrides <see cref="SerialWorkSchedulerBase{TWorkItem}.OnBeforeSerialDispose"/> to cancel
/// the last item so it can exit early from debounce or I/O before the serialization mechanism
/// (task chain / channel + loop) is torn down.
/// </para>
/// <para>
/// Callers must NOT call <c>Cancel()</c> on the previous work item themselves — cancellation
/// is entirely owned by this class. Callers may read <see cref="LastWorkItem"/> to inspect
/// the pending item's desired state (e.g. for anti-thrashing decisions) before calling
/// <see cref="IWorkScheduler{TWorkItem}.PublishWorkItemAsync"/>.
/// </para>
/// <para><strong>Why a Shared Base (not per-leaf duplication):</strong></para>
/// <para>
/// The supersession logic — <c>_lastWorkItem</c> field, volatile read/write, cancel-on-publish,
/// cancel-on-dispose — is concurrency-sensitive. Duplicating it across both leaf classes creates
/// two independent mutation sites for the same protocol, which is a maintenance risk in a
/// codebase with formal concurrency invariants. A shared base provides a single source of truth
/// for this protocol, with the leaf classes responsible only for their serialization mechanism
/// (<c>EnqueueWorkItemAsync</c> and <c>DisposeSerialAsyncCore</c>).
/// </para>
/// </remarks>
internal abstract class SupersessionWorkSchedulerBase<TWorkItem>
    : SerialWorkSchedulerBase<TWorkItem>, ISupersessionWorkScheduler<TWorkItem>
    where TWorkItem : class, ISchedulableWorkItem
{
    // Supersession state: last published work item.
    // Written via Volatile.Write on every publish (release fence for cross-thread visibility).
    // Read via Volatile.Read in OnBeforeEnqueue, OnBeforeSerialDispose, and LastWorkItem.
    private TWorkItem? _lastWorkItem;

    /// <summary>
    /// Initializes the shared fields.
    /// </summary>
    private protected SupersessionWorkSchedulerBase(
        Func<TWorkItem, CancellationToken, Task> executor,
        Func<TimeSpan> debounceProvider,
        IWorkSchedulerDiagnostics diagnostics,
        AsyncActivityCounter activityCounter)
        : base(executor, debounceProvider, diagnostics, activityCounter)
    {
    }

    /// <inheritdoc/>
    public TWorkItem? LastWorkItem => Volatile.Read(ref _lastWorkItem);

    /// <summary>
    /// Cancels the current <see cref="LastWorkItem"/> (if any) and stores the new item
    /// as the last work item before it is enqueued.
    /// </summary>
    /// <param name="workItem">The new work item about to be enqueued.</param>
    /// <remarks>
    /// Called by the sealed <see cref="SerialWorkSchedulerBase{TWorkItem}.PublishWorkItemAsync"/>
    /// pipeline after the activity counter is incremented and before
    /// <c>EnqueueWorkItemAsync</c> is called. This ordering ensures the new item is always
    /// registered as <see cref="LastWorkItem"/> before it can be observed by other threads.
    /// </remarks>
    private protected sealed override void OnBeforeEnqueue(TWorkItem workItem)
    {
        // Cancel previous item so it can exit early from debounce or I/O.
        Volatile.Read(ref _lastWorkItem)?.Cancel();

        // Store new item as the current last work item (release fence for cross-thread visibility).
        Volatile.Write(ref _lastWorkItem, workItem);
    }

    /// <summary>
    /// Cancels the last work item so it can exit early from debounce or I/O before
    /// the serialization mechanism is torn down during disposal.
    /// </summary>
    /// <remarks>
    /// Called by the sealed <see cref="SerialWorkSchedulerBase{TWorkItem}.DisposeAsyncCore"/>
    /// pipeline before <c>DisposeSerialAsyncCore</c> is awaited. Cancelling first allows the
    /// in-flight item to unblock from <c>Task.Delay</c> or an awaited I/O operation so the
    /// teardown await returns promptly rather than waiting for the full debounce or execution.
    /// </remarks>
    private protected sealed override void OnBeforeSerialDispose()
    {
        Volatile.Read(ref _lastWorkItem)?.Cancel();
    }
}
