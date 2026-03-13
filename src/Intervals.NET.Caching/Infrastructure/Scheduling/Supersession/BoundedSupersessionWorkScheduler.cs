using System.Threading.Channels;
using Intervals.NET.Caching.Infrastructure.Concurrency;
using Intervals.NET.Caching.Infrastructure.Diagnostics;
using Intervals.NET.Caching.Infrastructure.Scheduling.Serial;

namespace Intervals.NET.Caching.Infrastructure.Scheduling.Supersession;

/// <summary>
/// Serial work scheduler that serializes work item execution using a bounded
/// <see cref="Channel{T}"/> with backpressure support,
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
/// <para><strong>Serialization Mechanism — Bounded Channel:</strong></para>
/// <para>
/// Uses a bounded <see cref="Channel{T}"/> with single-reader/single-writer semantics.
/// When capacity is reached, <see cref="IWorkScheduler{TWorkItem}.PublishWorkItemAsync"/> blocks
/// (awaits <c>WriteAsync</c>) until space becomes available, creating backpressure that throttles
/// the caller's processing loop.
/// </para>
/// <para><strong>Single-Writer Guarantee:</strong></para>
/// <para>
/// The channel's single-reader loop ensures NO TWO WORK ITEMS execute concurrently.
/// This is the foundational invariant for consumers that perform single-writer mutations
/// (e.g. <c>RebalanceExecutor</c>).
/// </para>
/// <para><strong>Trade-offs:</strong></para>
/// <list type="bullet">
/// <item><description>✅ Bounded memory usage (fixed queue size = capacity × item size)</description></item>
/// <item><description>✅ Natural backpressure (throttles upstream when full)</description></item>
/// <item><description>✅ Automatic cancel-previous on publish</description></item>
/// <item><description>⚠️ Caller's processing loop blocks when full (intentional throttling mechanism)</description></item>
/// </list>
/// <para><strong>When to Use:</strong></para>
/// <list type="bullet">
/// <item><description>High-frequency rebalance requests (&gt;1000 requests/sec) requiring supersession</description></item>
/// <item><description>Resource-constrained environments where predictable memory usage is required</description></item>
/// </list>
/// <para>See also: <see cref="UnboundedSupersessionWorkScheduler{TWorkItem}"/> for the unbounded supersession alternative.</para>
/// <para>See also: <see cref="BoundedSerialWorkScheduler{TWorkItem}"/> for the bounded FIFO variant (no supersession).</para>
/// </remarks>
internal sealed class BoundedSupersessionWorkScheduler<TWorkItem>
    : SupersessionWorkSchedulerBase<TWorkItem>
    where TWorkItem : class, ISchedulableWorkItem
{
    private readonly Channel<TWorkItem> _workChannel;
    private readonly Task _executionLoopTask;

    /// <summary>
    /// Initializes a new instance of <see cref="BoundedSupersessionWorkScheduler{TWorkItem}"/>.
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
    public BoundedSupersessionWorkScheduler(
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

        _workChannel = Channel.CreateBounded<TWorkItem>(
            new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
                FullMode = BoundedChannelFullMode.Wait
            });

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
    private protected override async ValueTask EnqueueWorkItemAsync(TWorkItem workItem, CancellationToken loopCancellationToken)
    {
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
            throw;
        }
    }

    private async Task ProcessWorkItemsAsync()
    {
        await foreach (var workItem in _workChannel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            await ExecuteWorkItemCoreAsync(workItem).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    private protected override async ValueTask DisposeSerialAsyncCore()
    {
        _workChannel.Writer.Complete();
        await _executionLoopTask.ConfigureAwait(false);
    }
}
