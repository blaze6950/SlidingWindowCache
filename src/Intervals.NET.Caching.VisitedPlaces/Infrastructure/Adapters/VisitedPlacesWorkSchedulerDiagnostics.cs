using Intervals.NET.Caching.Infrastructure.Scheduling;
using Intervals.NET.Caching.VisitedPlaces.Public.Instrumentation;

namespace Intervals.NET.Caching.VisitedPlaces.Infrastructure.Adapters;

/// <summary>
/// Bridges <see cref="IVisitedPlacesCacheDiagnostics"/> to <see cref="IWorkSchedulerDiagnostics"/> for use
/// by <see cref="BoundedSerialWorkScheduler{TWorkItem}"/> in VisitedPlacesCache.
/// </summary>
/// <remarks>
/// <para><strong>Purpose:</strong></para>
/// <para>
/// The generic work schedulers in <c>Intervals.NET.Caching</c> depend on the narrow
/// <see cref="IWorkSchedulerDiagnostics"/> interface rather than the full
/// <see cref="IVisitedPlacesCacheDiagnostics"/>. This adapter maps the three scheduler-lifecycle events
/// (<c>WorkStarted</c>, <c>WorkCancelled</c>, <c>WorkFailed</c>) to their VPC counterparts.
/// </para>
/// <para><strong>Cancellation note:</strong></para>
/// <para>
/// CacheNormalizationRequests are never cancelled (Invariant VPC.A.11), so <c>WorkCancelled</c> is a
/// no-op: the scheduler may call it defensively, but it will never fire in practice.
/// </para>
/// </remarks>
internal sealed class VisitedPlacesWorkSchedulerDiagnostics : IWorkSchedulerDiagnostics
{
    private readonly IVisitedPlacesCacheDiagnostics _inner;

    /// <summary>
    /// Initializes a new instance of <see cref="VisitedPlacesWorkSchedulerDiagnostics"/>.
    /// </summary>
    /// <param name="inner">The underlying VPC diagnostics to delegate to.</param>
    public VisitedPlacesWorkSchedulerDiagnostics(IVisitedPlacesCacheDiagnostics inner)
    {
        _inner = inner;
    }

    /// <inheritdoc/>
    /// <remarks>Maps to <see cref="IVisitedPlacesCacheDiagnostics.NormalizationRequestReceived"/>.</remarks>
    public void WorkStarted() => _inner.NormalizationRequestReceived();

    /// <inheritdoc/>
    /// <remarks>
    /// No-op: CacheNormalizationRequests are never cancelled (Invariant VPC.A.11).
    /// The scheduler may call this defensively; it will never fire in practice.
    /// </remarks>
    public void WorkCancelled() { }

    /// <inheritdoc/>
    /// <remarks>Maps to <see cref="ICacheDiagnostics.BackgroundOperationFailed"/>.</remarks>
    public void WorkFailed(Exception ex) => _inner.BackgroundOperationFailed(ex);
}
