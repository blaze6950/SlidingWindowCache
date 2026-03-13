using System.Threading.Channels;
using Intervals.NET.Caching.Infrastructure.Concurrency;
using Intervals.NET.Caching.Infrastructure.Diagnostics;
using Intervals.NET.Caching.Infrastructure.Scheduling.Base;
using Intervals.NET.Caching.Infrastructure.Scheduling.Supersession;

namespace Intervals.NET.Caching.Infrastructure.Scheduling.Serial;

/// <summary>
/// Serial work scheduler that serializes work item execution using a bounded
/// <see cref="Channel{T}"/> with backpressure support.
/// Provides bounded FIFO serialization with predictable memory usage.
/// </summary>
/// <typeparam name="TWorkItem">
/// The type of work item processed by this scheduler.
/// Must implement <see cref="ISchedulableWorkItem"/> so the scheduler can cancel and dispose items.
/// </typeparam>
/// <remarks>
/// <para><strong>Serialization Mechanism — Bounded Channel:</strong></para>
/// <para>
/// Uses <see cref="Channel.CreateBounded{T}"/> with single-reader/single-writer semantics for
/// optimal performance. The bounded capacity ensures predictable memory usage and prevents
/// runaway queue growth. When capacity is reached,
/// <see cref="IWorkScheduler{TWorkItem}.PublishWorkItemAsync"/> blocks
/// (awaits <c>WriteAsync</c>) until space becomes available, creating backpressure that
/// throttles the caller's processing loop.
/// </para>
/// <code>
/// // Bounded channel with backpressure:
/// await _workChannel.Writer.WriteAsync(workItem);  // Blocks when full
///
/// // Sequential processing loop:
/// await foreach (var item in _workChannel.Reader.ReadAllAsync())
/// {
///     await ExecuteWorkItemCoreAsync(item);  // One at a time
/// }
/// </code>
/// <para><strong>FIFO Semantics:</strong></para>
/// <para>
/// All published work items are processed in order; none are cancelled or superseded.
/// This makes the scheduler suitable for event queues where every item must be processed
/// (e.g. VisitedPlaces cache normalization requests).
/// For supersession semantics (latest item wins, previous cancelled), use
/// <see cref="BoundedSupersessionWorkScheduler{TWorkItem}"/> instead.
/// </para>
/// <para><strong>Backpressure Behavior:</strong></para>
/// <list type="bullet">
/// <item><description>Caller's processing loop pauses until execution completes and frees channel space</description></item>
/// <item><description>User requests continue to be served immediately (User Path never blocks)</description></item>
/// <item><description>System self-regulates under sustained high load</description></item>
/// <item><description>Prevents memory exhaustion from unbounded work item accumulation</description></item>
/// </list>
/// <para><strong>Single-Writer Guarantee:</strong></para>
/// <para>
/// The channel's single-reader loop ensures NO TWO WORK ITEMS execute concurrently.
/// Only one item is processed at a time, guaranteeing serialized mutations and eliminating
/// write-write race conditions.
/// </para>
/// <para><strong>Trade-offs:</strong></para>
/// <list type="bullet">
/// <item><description>✅ Bounded memory usage (fixed queue size = capacity × item size)</description></item>
/// <item><description>✅ Natural backpressure (throttles upstream when full)</description></item>
/// <item><description>✅ Predictable resource consumption</description></item>
/// <item><description>✅ Self-regulating under sustained high load</description></item>
/// <item><description>⚠️ Caller's processing loop blocks when full (intentional throttling mechanism)</description></item>
/// <item><description>⚠️ Slightly more complex than task-based approach</description></item>
/// </list>
/// <para><strong>When to Use:</strong></para>
/// <list type="bullet">
/// <item><description>High-frequency request patterns (&gt;1000 requests/sec)</description></item>
/// <item><description>Resource-constrained environments requiring predictable memory usage</description></item>
/// <item><description>Real-time dashboards with streaming data updates</description></item>
/// <item><description>Scenarios where backpressure throttling is desired</description></item>
/// </list>
/// <para>See also: <see cref="UnboundedSerialWorkScheduler{TWorkItem}"/> for the unbounded FIFO alternative.</para>
/// <para>See also: <see cref="BoundedSupersessionWorkScheduler{TWorkItem}"/> for the bounded supersession variant.</para>
/// </remarks>
internal sealed class BoundedSerialWorkScheduler<TWorkItem> : SerialWorkSchedulerBase<TWorkItem>
    where TWorkItem : class, ISchedulableWorkItem
{
    private readonly Channel<TWorkItem> _workChannel;
    private readonly Task _executionLoopTask;

    /// <summary>
    /// Initializes a new instance of <see cref="BoundedSerialWorkScheduler{TWorkItem}"/>.
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
    /// <param name="capacity">The bounded channel capacity for backpressure control. Must be >= 1.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="capacity"/> is less than 1.</exception>
    /// <remarks>
    /// <para><strong>Channel Configuration:</strong></para>
    /// <para>
    /// Creates a bounded channel with the specified capacity and single-reader/single-writer semantics.
    /// When full, <see cref="IWorkScheduler{TWorkItem}.PublishWorkItemAsync"/> will block
    /// (await <c>WriteAsync</c>) until space becomes available, throttling the caller's processing loop.
    /// </para>
    /// <para><strong>Execution Loop Lifecycle:</strong></para>
    /// <para>
    /// The execution loop starts immediately upon construction and runs for the lifetime of the
    /// scheduler instance. This guarantees single-threaded execution of all work items via
    /// sequential channel processing.
    /// </para>
    /// </remarks>
    public BoundedSerialWorkScheduler(
        Func<TWorkItem, CancellationToken, Task> executor,
        Func<TimeSpan> debounceProvider,
        IWorkSchedulerDiagnostics diagnostics,
        AsyncActivityCounter activityCounter,
        int capacity
    ) : base(executor, debounceProvider, diagnostics, activityCounter)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity),
                "Capacity must be greater than or equal to 1.");
        }

        // Initialize bounded channel with single reader/writer semantics.
        // Bounded capacity enables backpressure on the caller's processing loop.
        // SingleReader: only execution loop reads; SingleWriter: only caller's loop writes.
        _workChannel = Channel.CreateBounded<TWorkItem>(
            new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
                FullMode = BoundedChannelFullMode.Wait // Block on WriteAsync when full (backpressure)
            });

        // Start execution loop immediately — runs for scheduler lifetime
        _executionLoopTask = ProcessWorkItemsAsync();
    }

    /// <summary>
    /// Enqueues the work item to the bounded channel for sequential processing.
    /// Blocks if the channel is at capacity (backpressure).
    /// </summary>
    /// <param name="workItem">The work item to schedule.</param>
    /// <param name="loopCancellationToken">
    /// Cancellation token from the caller's processing loop.
    /// Unblocks <c>WriteAsync</c> during disposal to prevent hangs.
    /// </param>
    /// <returns>
    /// A <see cref="ValueTask"/> that completes when the item is enqueued.
    /// May block if the channel is at capacity.
    /// </returns>
    /// <remarks>
    /// <para><strong>Backpressure Behavior:</strong></para>
    /// <para>
    /// When the bounded channel is at capacity this method will AWAIT (not return) until space
    /// becomes available. This creates intentional backpressure that throttles the caller's
    /// processing loop, preventing excessive work item accumulation.
    /// </para>
    /// <para><strong>Cancellation Behavior:</strong></para>
    /// <para>
    /// The <paramref name="loopCancellationToken"/> enables graceful shutdown during disposal.
    /// If the channel is full and disposal begins, token cancellation unblocks <c>WriteAsync</c>,
    /// preventing disposal hangs. On cancellation the method cleans up resources and returns
    /// gracefully without throwing.
    /// </para>
    /// <para><strong>Error Path:</strong></para>
    /// <para>
    /// On cancellation or write failure the item is disposed and the activity counter is
    /// decremented here, because <see cref="WorkSchedulerBase{TWorkItem}.ExecuteWorkItemCoreAsync"/>
    /// will never run for this item.
    /// </para>
    /// </remarks>
    private protected override async ValueTask EnqueueWorkItemAsync(TWorkItem workItem, CancellationToken loopCancellationToken)
    {
        // Enqueue work item to bounded channel.
        // BACKPRESSURE: Will await if channel is at capacity, throttling the caller's loop.
        // CANCELLATION: loopCancellationToken enables graceful shutdown during disposal.
        try
        {
            await _workChannel.Writer.WriteAsync(workItem, loopCancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (loopCancellationToken.IsCancellationRequested)
        {
            // Write cancelled during disposal — clean up and exit gracefully.
            workItem.Dispose();
            ActivityCounter.DecrementActivity();
        }
        catch (Exception ex)
        {
            // Write failed (e.g. channel completed during disposal) — clean up and report.
            workItem.Dispose();
            ActivityCounter.DecrementActivity();
            Diagnostics.WorkFailed(ex);
            throw; // Re-throw to signal failure to caller
        }
    }

    /// <summary>
    /// Execution loop that processes work items sequentially from the bounded channel.
    /// This loop is the SOLE execution path for work items when this strategy is active.
    /// </summary>
    /// <remarks>
    /// <para><strong>Sequential Execution Guarantee:</strong></para>
    /// <para>
    /// This loop runs on a single background thread and processes items one at a time via Channel.
    /// NO TWO WORK ITEMS can ever run in parallel. The Channel ensures serial processing.
    /// </para>
    /// <para><strong>Backpressure Effect:</strong></para>
    /// <para>
    /// When this loop processes an item, it frees space in the bounded channel, allowing
    /// any blocked <see cref="IWorkScheduler{TWorkItem}.PublishWorkItemAsync"/> calls to proceed.
    /// This creates natural flow control.
    /// </para>
    /// </remarks>
    private async Task ProcessWorkItemsAsync()
    {
        await foreach (var workItem in _workChannel.Reader.ReadAllAsync())
        {
            await ExecuteWorkItemCoreAsync(workItem).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    private protected override async ValueTask DisposeSerialAsyncCore()
    {
        // Complete the channel — signals execution loop to exit after current item
        _workChannel.Writer.Complete();

        // Wait for execution loop to complete gracefully
        await _executionLoopTask.ConfigureAwait(false);
    }
}
