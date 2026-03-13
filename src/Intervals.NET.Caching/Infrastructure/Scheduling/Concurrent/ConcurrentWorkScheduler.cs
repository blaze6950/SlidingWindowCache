using Intervals.NET.Caching.Infrastructure.Concurrency;
using Intervals.NET.Caching.Infrastructure.Diagnostics;
using Intervals.NET.Caching.Infrastructure.Scheduling.Base;
using Intervals.NET.Caching.Infrastructure.Scheduling.Serial;

namespace Intervals.NET.Caching.Infrastructure.Scheduling.Concurrent;

/// <summary>
/// Concurrent work scheduler that launches each work item independently on the ThreadPool without
/// serialization. Every <see cref="PublishWorkItemAsync"/> call starts a new concurrent
/// execution — there is no "previous task" to await.
/// </summary>
/// <typeparam name="TWorkItem">
/// The type of work item processed by this scheduler.
/// Must implement <see cref="ISchedulableWorkItem"/> so the scheduler can cancel and dispose items.
/// </typeparam>
/// <remarks>
/// <para><strong>Design Intent — TTL Work Items:</strong></para>
/// <para>
/// The primary consumer of this scheduler is the TTL expiration path. Each TTL work item
/// does <c>await Task.Delay(remaining)</c> before removing its segment, meaning it holds a
/// continuation for the duration of the TTL window. If a serialized scheduler
/// (e.g. <see cref="UnboundedSerialWorkScheduler{TWorkItem}"/>) were used, every pending
/// <c>Task.Delay</c> would block all subsequent TTL items from starting — the second item
/// would wait for the first delay to finish, the third would wait for the first two, and so
/// on. This scheduler avoids that serialization entirely.
/// </para>
/// <para><strong>Concurrency Model:</strong></para>
/// <para>
/// Unlike <see cref="UnboundedSerialWorkScheduler{TWorkItem}"/> (which chains tasks to ensure
/// sequential execution) or <see cref="BoundedSerialWorkScheduler{TWorkItem}"/> (which uses a
/// bounded channel), this scheduler makes no ordering or exclusion guarantees between items.
/// Each work item executes independently via <see cref="ThreadPool.QueueUserWorkItem"/>. For TTL removals this is
/// correct: <c>CachedSegment.MarkAsRemoved()</c> is atomic (Interlocked) and idempotent, and
/// <c>EvictionEngine.OnSegmentRemoved</c> uses <c>Interlocked.Add</c> for
/// <c>_totalSpan</c> — so concurrent removals are safe.
/// </para>
/// <para><strong>Disposal:</strong></para>
/// <para>
/// <see cref="WorkSchedulerBase{TWorkItem}.DisposeAsync"/> delegates to
/// <see cref="DisposeAsyncCore"/>, which is a no-op for this scheduler.
/// For TTL work items, the cancellation token passed into each work item at construction is a
/// shared disposal token owned by the cache — the cache cancels it during its own
/// <c>DisposeAsync</c>, causing ALL pending <c>Task.Delay</c> calls to throw
/// <see cref="OperationCanceledException"/> and drain immediately. The caller (e.g.
/// <c>VisitedPlacesCache.DisposeAsync</c>) awaits the TTL activity counter going idle to
/// confirm all in-flight work items have completed before returning.
/// </para>
/// <para><strong>Activity Counter:</strong></para>
/// <para>
/// The activity counter is incremented in <see cref="PublishWorkItemAsync"/> before dispatching
/// to the ThreadPool and decremented in the base
/// <see cref="WorkSchedulerBase{TWorkItem}.ExecuteWorkItemCoreAsync"/> <c>finally</c>
/// block, matching the contract of all other scheduler implementations.
/// </para>
/// <para><strong>Trade-offs:</strong></para>
/// <list type="bullet">
/// <item><description>✅ No inter-item serialization (TTL delays run concurrently)</description></item>
/// <item><description>✅ Simple implementation — thinner than task-chaining or channel-based</description></item>
/// <item><description>✅ Fire-and-forget: <see cref="PublishWorkItemAsync"/> always returns synchronously</description></item>
/// <item><description>✅ WASM compatible: uses <see cref="ThreadPool.QueueUserWorkItem"/> instead of <c>Task.Run</c></description></item>
/// <item><description>⚠️ No ordering guarantees — callers must not rely on sequential execution</description></item>
/// <item><description>⚠️ Unbounded concurrency — use only for work items whose concurrent execution is safe</description></item>
/// </list>
/// <para>See also: <see cref="UnboundedSerialWorkScheduler{TWorkItem}"/> for serialized execution.</para>
/// </remarks>
internal sealed class ConcurrentWorkScheduler<TWorkItem> : WorkSchedulerBase<TWorkItem>
    where TWorkItem : class, ISchedulableWorkItem
{
    /// <summary>
    /// Initializes a new instance of <see cref="ConcurrentWorkScheduler{TWorkItem}"/>.
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
    public ConcurrentWorkScheduler(
        Func<TWorkItem, CancellationToken, Task> executor,
        Func<TimeSpan> debounceProvider,
        IWorkSchedulerDiagnostics diagnostics,
        AsyncActivityCounter activityCounter,
        TimeProvider? timeProvider = null
    ) : base(executor, debounceProvider, diagnostics, activityCounter, timeProvider)
    {
    }

    /// <summary>
    /// Publishes a work item by dispatching it to the ThreadPool independently.
    /// Returns immediately (fire-and-forget). No serialization with previously published items.
    /// </summary>
    /// <param name="workItem">The work item to schedule.</param>
    /// <param name="loopCancellationToken">
    /// Accepted for API consistency; not used by this strategy (never blocks on publishing).
    /// </param>
    /// <returns><see cref="ValueTask.CompletedTask"/> — always completes synchronously.</returns>
    /// <remarks>
    /// <para>
    /// Each call increments the activity counter and posts the work item to the ThreadPool via
    /// <see cref="ThreadPool.QueueUserWorkItem"/>. The base pipeline
    /// (<see cref="WorkSchedulerBase{TWorkItem}.ExecuteWorkItemCoreAsync"/>)
    /// decrements the counter in its <c>finally</c> block, preserving the
    /// increment-before / decrement-after contract of all scheduler implementations.
    /// </para>
    /// </remarks>
    public override ValueTask PublishWorkItemAsync(TWorkItem workItem, CancellationToken loopCancellationToken)
    {
        if (IsDisposed)
        {
            throw new ObjectDisposedException(
                nameof(ConcurrentWorkScheduler<TWorkItem>),
                "Cannot publish a work item to a disposed scheduler.");
        }

        // Increment activity counter before dispatching.
        ActivityCounter.IncrementActivity();

        // Launch independently via ThreadPool.QueueUserWorkItem.
        // This is used instead of Task.Run / Task.Factory.StartNew for three reasons:
        // 1. It always posts to the ThreadPool (ignores any caller SynchronizationContext),
        //    preserving the concurrent execution guarantee even inside test harnesses that
        //    install a custom SynchronizationContext (e.g. xUnit v2).
        // 2. Unlike ThreadPool.UnsafeQueueUserWorkItem, it captures and flows ExecutionContext,
        //    so diagnostic hooks executing inside the work item have access to AsyncLocal<T>
        //    values — tracing context, culture, activity IDs, etc. — from the publishing caller.
        // 3. It is available on net8.0-browser / WebAssembly, where Task.Run is not suitable
        //    in single-threaded environments.
        ThreadPool.QueueUserWorkItem(
            static state => _ = state.scheduler.ExecuteWorkItemCoreAsync(state.workItem),
            state: (scheduler: this, workItem),
            preferLocal: false);

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// No-op: this scheduler does not maintain a task chain or channel to drain.
    /// Cancellation of all in-flight work items is driven by the shared disposal
    /// <see cref="CancellationToken"/> owned by the cache (passed into each work item at
    /// construction time). The cache's <c>DisposeAsync</c> cancels that token — causing all
    /// pending <c>Task.Delay</c> calls to complete immediately — then awaits the TTL activity
    /// counter going idle to confirm all work items have finished.
    /// </remarks>
    private protected override ValueTask DisposeAsyncCore() => ValueTask.CompletedTask;
}
