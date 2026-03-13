using Intervals.NET.Caching.Infrastructure.Concurrency;
using Intervals.NET.Caching.Infrastructure.Diagnostics;

namespace Intervals.NET.Caching.Infrastructure.Scheduling.Base;

/// <summary>
/// Abstract base class providing the shared execution pipeline for all work scheduler implementations.
/// </summary>
/// <typeparam name="TWorkItem">
/// The type of work item processed by this scheduler.
/// Must implement <see cref="ISchedulableWorkItem"/> so the scheduler can cancel and dispose items.
/// </typeparam>
/// <remarks>
/// <para><strong>Purpose:</strong></para>
/// <para>
/// Centralizes the logic that is identical across ALL <see cref="IWorkScheduler{TWorkItem}"/>
/// implementations — regardless of whether they are serial or concurrent: shared fields,
/// the per-item execution pipeline (debounce → cancellation check → executor call →
/// diagnostics → cleanup), and the disposal guard. Each concrete subclass provides only its
/// scheduling mechanism (<see cref="PublishWorkItemAsync"/>) and strategy-specific teardown
/// (<see cref="DisposeAsyncCore"/>).
/// </para>
/// <para><strong>Hierarchy:</strong></para>
/// <code>
/// WorkSchedulerBase&lt;TWorkItem&gt;              — generic execution pipeline, disposal guard
///   ├── SerialWorkSchedulerBase&lt;TWorkItem&gt;  — serial-specific: LastWorkItem, cancel-on-dispose
///   │     ├── UnboundedSerialWorkScheduler   — task chaining
///   │     └── BoundedSerialWorkScheduler     — channel-based
///   └── ConcurrentWorkScheduler             — independent ThreadPool dispatch
/// </code>
/// <para><strong>Shared Execution Pipeline (<see cref="ExecuteWorkItemCoreAsync"/>):</strong></para>
/// <list type="number">
/// <item><description>Signal <c>WorkStarted</c> diagnostic</description></item>
/// <item><description>Snapshot debounce delay from the provider delegate ("next cycle" semantics)</description></item>
/// <item><description>Await <c>Task.Delay(debounceDelay, cancellationToken)</c> (skipped when <c>debounceDelay == TimeSpan.Zero</c>)</description></item>
/// <item><description>Explicit <c>IsCancellationRequested</c> check after debounce (Task.Delay race guard; skipped when debounce is zero)</description></item>
/// <item><description>Invoke the executor delegate with the work item and its cancellation token</description></item>
/// <item><description>Catch <c>OperationCanceledException</c> → <c>WorkCancelled</c> diagnostic</description></item>
/// <item><description>Catch all other exceptions → <c>WorkFailed</c> diagnostic</description></item>
/// <item><description><c>finally</c>: dispose the item, decrement the activity counter</description></item>
/// </list>
/// <para>
/// The <c>finally</c> block in step 8 is the canonical S.H.2 call site for scheduler-owned
/// decrements. Every work item is disposed here (or in <see cref="PublishWorkItemAsync"/>'s
/// error handler) — no separate dispose-last-item step is needed during disposal.
/// </para>
/// <para><strong>Disposal Protocol:</strong></para>
/// <para>
/// <see cref="DisposeAsync"/> handles the idempotent guard (Interlocked) and then delegates
/// to <see cref="DisposeAsyncCore"/> for strategy-specific teardown. Serial subclasses
/// extend this via <see cref="SerialWorkSchedulerBase{TWorkItem}"/>, which cancels the last
/// work item before calling their own <c>DisposeSerialAsyncCore</c>.
/// </para>
/// <para><strong>Cache-Agnostic Design:</strong></para>
/// <para>
/// All cache-type-specific logic is injected as delegates or interfaces:
/// </para>
/// <list type="bullet">
/// <item><description><c>executor</c> — <c>Func&lt;TWorkItem, CancellationToken, Task&gt;</c></description></item>
/// <item><description><c>debounceProvider</c> — <c>Func&lt;TimeSpan&gt;</c></description></item>
/// <item><description><c>diagnostics</c> — <see cref="IWorkSchedulerDiagnostics"/></description></item>
/// <item><description><c>activityCounter</c> — <see cref="AsyncActivityCounter"/></description></item>
/// </list>
/// </remarks>
internal abstract class WorkSchedulerBase<TWorkItem> : IWorkScheduler<TWorkItem>
    where TWorkItem : class, ISchedulableWorkItem
{
    /// <summary>Delegate that executes the actual work for a given work item.</summary>
    private protected readonly Func<TWorkItem, CancellationToken, Task> Executor;

    /// <summary>Returns the current debounce delay; snapshotted at the start of each execution ("next cycle" semantics).</summary>
    private protected readonly Func<TimeSpan> DebounceProvider;

    /// <summary>Diagnostics for scheduler-level lifecycle events.</summary>
    private protected readonly IWorkSchedulerDiagnostics Diagnostics;

    /// <summary>Activity counter for tracking active operations.</summary>
    private protected readonly AsyncActivityCounter ActivityCounter;

    /// <summary>Time provider used for debounce delays. Enables deterministic testing.</summary>
    private protected readonly TimeProvider TimeProvider;

    // Disposal state: 0 = not disposed, 1 = disposed (lock-free via Interlocked)
    private int _disposeState;

    /// <summary>
    /// Initializes the shared fields.
    /// </summary>
    private protected WorkSchedulerBase(
        Func<TWorkItem, CancellationToken, Task> executor,
        Func<TimeSpan> debounceProvider,
        IWorkSchedulerDiagnostics diagnostics,
        AsyncActivityCounter activityCounter,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(debounceProvider);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(activityCounter);

        Executor = executor;
        DebounceProvider = debounceProvider;
        Diagnostics = diagnostics;
        ActivityCounter = activityCounter;
        TimeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public abstract ValueTask PublishWorkItemAsync(TWorkItem workItem, CancellationToken loopCancellationToken);

    /// <summary>
    /// Executes a single work item: debounce → cancellation check → executor call → diagnostics → cleanup.
    /// This is the canonical execution pipeline shared by all strategy implementations.
    /// </summary>
    /// <remarks>
    /// <para><strong>Execution Steps:</strong></para>
    /// <list type="number">
    /// <item><description>Signal <c>WorkStarted</c> diagnostic</description></item>
    /// <item><description>Read cancellation token from the work item's <see cref="ISchedulableWorkItem.CancellationToken"/></description></item>
    /// <item><description>Snapshot debounce delay from provider at execution time ("next cycle" semantics)</description></item>
    /// <item><description>Await <c>Task.Delay(debounceDelay, cancellationToken)</c> (skipped entirely when <c>debounceDelay == TimeSpan.Zero</c>)</description></item>
    /// <item><description>Explicit <c>IsCancellationRequested</c> check after debounce (Task.Delay race guard; skipped when debounce is zero)</description></item>
    /// <item><description>Invoke executor delegate</description></item>
    /// <item><description>Catch <c>OperationCanceledException</c> → signal <c>WorkCancelled</c></description></item>
    /// <item><description>Catch other exceptions → signal <c>WorkFailed</c></description></item>
    /// <item><description><c>finally</c>: dispose item, decrement activity counter</description></item>
    /// </list>
    /// </remarks>
    private protected async Task ExecuteWorkItemCoreAsync(TWorkItem workItem)
    {
        try
        {
            // Step 0: Signal work-started and snapshot configuration.
            // These are inside the try so that any unexpected throw does not bypass the
            // finally block — keeping the activity counter balanced (Invariant S.H.2).
            Diagnostics.WorkStarted();

            // The work item owns its CancellationTokenSource and exposes the derived token.
            var cancellationToken = workItem.CancellationToken;

            // Snapshot debounce delay at execution time — picks up any runtime updates
            // published since this work item was enqueued ("next cycle" semantics).
            var debounceDelay = DebounceProvider();

            // Step 1: Apply debounce delay — allows superseded work items to be cancelled.
            // Skipped entirely when debounce is zero (e.g. VPC event processing) to avoid
            // unnecessary task allocation. ConfigureAwait(false) ensures continuation on thread pool.
            if (debounceDelay > TimeSpan.Zero)
            {
                await Task.Delay(debounceDelay, TimeProvider, cancellationToken)
                    .ConfigureAwait(false);

                // Step 2: Check cancellation after debounce.
                // NOTE: Task.Delay can complete normally just as cancellation is signalled (a race),
                // so we may reach here with cancellation requested but no exception thrown.
                // This explicit check provides a clean diagnostic path (WorkCancelled) for that case.
                if (cancellationToken.IsCancellationRequested)
                {
                    Diagnostics.WorkCancelled();
                    return;
                }
            }

            // Step 3: Execute the work item.
            await Executor(workItem, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Diagnostics.WorkCancelled();
        }
        catch (Exception ex)
        {
            Diagnostics.WorkFailed(ex);
        }
        finally
        {
            // Dispose the work item (releases its CancellationTokenSource etc.)
            // This is the canonical disposal site — every work item is disposed here,
            // so no separate dispose step is needed during scheduler disposal.
            workItem.Dispose();

            // Decrement activity counter — ALWAYS happens after execution completes/cancels/fails.
            ActivityCounter.DecrementActivity();
        }
    }

    /// <summary>
    /// Performs strategy-specific teardown during disposal.
    /// Called by <see cref="DisposeAsync"/> after the disposal guard has fired.
    /// </summary>
    /// <remarks>
    /// Implementations should stop their scheduling mechanism here:
    /// <list type="bullet">
    /// <item><description><strong>Task-based (serial):</strong> await the current task chain</description></item>
    /// <item><description><strong>Channel-based (serial):</strong> complete the channel writer and await the loop task</description></item>
    /// <item><description><strong>Concurrent:</strong> no-op — cancellation and drain are owned by the caller</description></item>
    /// </list>
    /// <para>
    /// Serial schedulers override this via <see cref="SerialWorkSchedulerBase{TWorkItem}"/>,
    /// which cancels the last work item before delegating to <c>DisposeSerialAsyncCore</c>.
    /// </para>
    /// </remarks>
    private protected abstract ValueTask DisposeAsyncCore();

    /// <summary>
    /// Returns whether the scheduler has been disposed.
    /// Subclasses use this to guard <see cref="PublishWorkItemAsync"/>.
    /// </summary>
    private protected bool IsDisposed => Volatile.Read(ref _disposeState) != 0;

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        // Idempotent guard using lock-free Interlocked.CompareExchange
        if (Interlocked.CompareExchange(ref _disposeState, 1, 0) != 0)
        {
            return; // Already disposed
        }

        // Strategy-specific teardown.
        // Serial subclasses (SerialWorkSchedulerBase) also cancel the last work item here,
        // allowing early exit from debounce / I/O before awaiting the task chain or loop.
        try
        {
            await DisposeAsyncCore().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Log via diagnostics but don't throw — best-effort disposal.
            // Follows "Background Path Exceptions" pattern from AGENTS.md.
            Diagnostics.WorkFailed(ex);
        }
    }
}
