namespace Intervals.NET.Caching.Infrastructure.Scheduling;

/// <summary>
/// Represents a unit of work that can be scheduled, cancelled, and disposed by a work scheduler.
/// </summary>
/// <remarks>
/// <para><strong>Purpose:</strong></para>
/// <para>
/// This interface is the <c>TWorkItem</c> constraint for
/// <see cref="IWorkScheduler{TWorkItem}"/>, <see cref="WorkSchedulerBase{TWorkItem}"/>,
/// <see cref="TaskBasedWorkScheduler{TWorkItem}"/>, and
/// <see cref="ChannelBasedWorkScheduler{TWorkItem}"/>.
/// It combines the two operations that the scheduler must perform on a work item
/// beyond passing it to the executor:
/// </para>
/// <list type="bullet">
/// <item><description><see cref="Cancel"/> — signal early exit to the running or waiting work item</description></item>
/// <item><description><see cref="IDisposable.Dispose"/> — release owned resources (e.g. <see cref="CancellationTokenSource"/>)</description></item>
/// </list>
/// <para><strong>Implementations:</strong></para>
/// <para>
/// SlidingWindow's <c>ExecutionRequest&lt;TRange,TData,TDomain&gt;</c> is the canonical implementation.
/// Future cache types (e.g. VisitedPlacesCache) will provide their own work-item types.
/// </para>
/// <para><strong>Thread Safety:</strong></para>
/// <para>
/// Both <see cref="Cancel"/> and <see cref="IDisposable.Dispose"/> must be safe to call
/// multiple times and must handle disposal races gracefully (e.g. by catching
/// <see cref="ObjectDisposedException"/>).
/// </para>
/// </remarks>
internal interface ISchedulableWorkItem : IDisposable
{
    /// <summary>
    /// The cancellation token associated with this work item.
    /// Cancelled when <see cref="Cancel"/> is called or when the item is superseded.
    /// Passed to the executor delegate by the scheduler.
    /// </summary>
    CancellationToken CancellationToken { get; }

    /// <summary>
    /// Signals this work item to exit early.
    /// Safe to call multiple times and after <see cref="IDisposable.Dispose"/>.
    /// </summary>
    void Cancel();
}
