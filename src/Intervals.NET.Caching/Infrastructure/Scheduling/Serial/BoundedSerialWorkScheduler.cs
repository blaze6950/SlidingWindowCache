using System.Threading.Channels;
using Intervals.NET.Caching.Infrastructure.Concurrency;
using Intervals.NET.Caching.Infrastructure.Diagnostics;
using Intervals.NET.Caching.Infrastructure.Scheduling.Base;

namespace Intervals.NET.Caching.Infrastructure.Scheduling.Serial;

/// <summary>
/// Serial work scheduler that serializes work item execution using a bounded
/// <see cref="Channel{T}"/> with backpressure support.
/// See docs/shared/components/infrastructure.md for design details.
/// </summary>
/// <typeparam name="TWorkItem">
/// The type of work item processed by this scheduler.
/// Must implement <see cref="ISchedulableWorkItem"/> so the scheduler can cancel and dispose items.
/// </typeparam>
internal sealed class BoundedSerialWorkScheduler<TWorkItem> : SerialWorkSchedulerBase<TWorkItem>
    where TWorkItem : class, ISchedulableWorkItem
{
    private readonly Channel<TWorkItem> _workChannel;
    private readonly Task _executionLoopTask;

    /// <summary>
    /// Initializes a new instance of <see cref="BoundedSerialWorkScheduler{TWorkItem}"/>.
    /// </summary>
    /// <param name="executor">Delegate that performs the actual work for a given work item.</param>
    /// <param name="debounceProvider">Returns the current debounce delay.</param>
    /// <param name="diagnostics">Diagnostics for work lifecycle events.</param>
    /// <param name="activityCounter">Activity counter for tracking active operations.</param>
    /// <param name="capacity">The bounded channel capacity for backpressure control. Must be >= 1.</param>
    /// <param name="singleWriter">
    /// When <see langword="true"/>, the channel is configured for a single writer thread (minor perf hint).
    /// When <see langword="false"/>, multiple threads may concurrently call <see cref="PublishWorkItemAsync"/>.
    /// Pass <see langword="false"/> for VPC (concurrent user-thread publishers);
    /// pass <see langword="true"/> only when the caller guarantees a single publishing thread.
    /// </param>
    /// <param name="timeProvider">
    /// Time provider for debounce delays. When <see langword="null"/>,
    /// <see cref="TimeProvider.System"/> is used.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="capacity"/> is less than 1.</exception>
    public BoundedSerialWorkScheduler(
        Func<TWorkItem, CancellationToken, Task> executor,
        Func<TimeSpan> debounceProvider,
        IWorkSchedulerDiagnostics diagnostics,
        AsyncActivityCounter activityCounter,
        int capacity,
        bool singleWriter,
        TimeProvider? timeProvider = null
    ) : base(executor, debounceProvider, diagnostics, activityCounter, timeProvider)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity),
                "Capacity must be greater than or equal to 1.");
        }

        // Initialize bounded channel with single reader; writer concurrency controlled by singleWriter.
        // SingleReader: only execution loop reads.
        // SingleWriter: set by caller — true only when a single thread publishes work items;
        //               false when multiple threads (e.g. concurrent user requests in VPC) publish concurrently.
        _workChannel = Channel.CreateBounded<TWorkItem>(
            new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = singleWriter,
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
    /// </summary>
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
        // Complete the channel — signals execution loop to exit after current item
        _workChannel.Writer.Complete();

        // Wait for execution loop to complete gracefully
        await _executionLoopTask.ConfigureAwait(false);
    }
}
