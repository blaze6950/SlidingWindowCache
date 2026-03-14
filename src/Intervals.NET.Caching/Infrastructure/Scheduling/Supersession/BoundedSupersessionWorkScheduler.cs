using System.Threading.Channels;
using Intervals.NET.Caching.Infrastructure.Concurrency;
using Intervals.NET.Caching.Infrastructure.Diagnostics;

namespace Intervals.NET.Caching.Infrastructure.Scheduling.Supersession;

/// <summary>
/// Serial work scheduler that serializes work item execution using a bounded
/// <see cref="Channel{T}"/> with backpressure support,
/// and implements supersession semantics: each new published item automatically cancels the previous one.
/// See docs/shared/components/infrastructure.md for design details.
/// </summary>
/// <typeparam name="TWorkItem">
/// The type of work item processed by this scheduler.
/// Must implement <see cref="ISchedulableWorkItem"/> so the scheduler can cancel and dispose items.
/// </typeparam>
internal sealed class BoundedSupersessionWorkScheduler<TWorkItem>
    : SupersessionWorkSchedulerBase<TWorkItem>
    where TWorkItem : class, ISchedulableWorkItem
{
    private readonly Channel<TWorkItem> _workChannel;
    private readonly Task _executionLoopTask;

    /// <summary>
    /// Initializes a new instance of <see cref="BoundedSupersessionWorkScheduler{TWorkItem}"/>.
    /// </summary>
    /// <param name="executor">Delegate that performs the actual work for a given work item.</param>
    /// <param name="debounceProvider">Returns the current debounce delay.</param>
    /// <param name="diagnostics">Diagnostics for work lifecycle events.</param>
    /// <param name="activityCounter">Activity counter for tracking active operations.</param>
    /// <param name="capacity">The bounded channel capacity for backpressure control. Must be >= 1.</param>
    /// <param name="singleWriter">
    /// When <see langword="true"/>, the channel is configured for a single writer thread (minor perf hint).
    /// When <see langword="false"/>, multiple threads may concurrently call <see cref="PublishWorkItemAsync"/>.
    /// Pass <see langword="true"/> for SWC (IntentController loop is the sole publisher);
    /// pass <see langword="false"/> when multiple threads may publish concurrently.
    /// </param>
    /// <param name="timeProvider">
    /// Time provider for debounce delays. When <see langword="null"/>,
    /// <see cref="TimeProvider.System"/> is used.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="capacity"/> is less than 1.</exception>
    public BoundedSupersessionWorkScheduler(
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

        _workChannel = Channel.CreateBounded<TWorkItem>(
            new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = singleWriter,
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
