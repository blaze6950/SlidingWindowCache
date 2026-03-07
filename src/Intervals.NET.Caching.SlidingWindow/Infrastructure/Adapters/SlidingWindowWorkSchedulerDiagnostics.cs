using Intervals.NET.Caching.Infrastructure.Scheduling;
using Intervals.NET.Domain.Abstractions;
using Intervals.NET.Caching.SlidingWindow.Public.Instrumentation;

namespace Intervals.NET.Caching.SlidingWindow.Infrastructure.Adapters;

/// <summary>
/// Bridges <see cref="ICacheDiagnostics"/> to <see cref="IWorkSchedulerDiagnostics"/> for use by
/// <see cref="TaskBasedWorkScheduler{TWorkItem}"/> and
/// <see cref="ChannelBasedWorkScheduler{TWorkItem}"/>.
/// </summary>
/// <remarks>
/// <para><strong>Purpose:</strong></para>
/// <para>
/// The generic work schedulers in <c>Intervals.NET.Caching</c> depend on the
/// narrow <see cref="IWorkSchedulerDiagnostics"/> interface rather than the full
/// <see cref="ICacheDiagnostics"/>. This adapter maps the three scheduler-lifecycle events
/// (<c>WorkStarted</c>, <c>WorkCancelled</c>, <c>WorkFailed</c>) to their SlidingWindow
/// counterparts (<c>RebalanceExecutionStarted</c>, <c>RebalanceExecutionCancelled</c>,
/// <c>RebalanceExecutionFailed</c>).
/// </para>
/// </remarks>
internal sealed class SlidingWindowWorkSchedulerDiagnostics : IWorkSchedulerDiagnostics
{
    private readonly ICacheDiagnostics _inner;

    /// <summary>
    /// Initializes a new instance of <see cref="SlidingWindowWorkSchedulerDiagnostics"/>.
    /// </summary>
    /// <param name="inner">The underlying SlidingWindow diagnostics to delegate to.</param>
    public SlidingWindowWorkSchedulerDiagnostics(ICacheDiagnostics inner)
    {
        _inner = inner;
    }

    /// <inheritdoc/>
    public void WorkStarted() => _inner.RebalanceExecutionStarted();

    /// <inheritdoc/>
    public void WorkCancelled() => _inner.RebalanceExecutionCancelled();

    /// <inheritdoc/>
    public void WorkFailed(Exception ex) => _inner.RebalanceExecutionFailed(ex);
}
