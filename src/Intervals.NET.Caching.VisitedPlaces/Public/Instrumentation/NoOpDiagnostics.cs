namespace Intervals.NET.Caching.VisitedPlaces.Public.Instrumentation;

/// <summary>
/// No-op implementation of <see cref="IVisitedPlacesCacheDiagnostics"/> that silently discards all events.
/// Used as the default when no diagnostics are configured.
/// </summary>
/// <remarks>
/// Access the singleton via <see cref="Instance"/>. Do not construct additional instances.
/// </remarks>
public sealed class NoOpDiagnostics : NoOpCacheDiagnostics, IVisitedPlacesCacheDiagnostics
{
    /// <summary>The singleton no-op diagnostics instance.</summary>
    public static new readonly IVisitedPlacesCacheDiagnostics Instance = new NoOpDiagnostics();

    private NoOpDiagnostics() { }

    /// <inheritdoc/>
    public void DataSourceFetchGap() { }

    /// <inheritdoc/>
    public void NormalizationRequestReceived() { }

    /// <inheritdoc/>
    public void NormalizationRequestProcessed() { }

    /// <inheritdoc/>
    public void BackgroundStatisticsUpdated() { }

    /// <inheritdoc/>
    public void BackgroundSegmentStored() { }

    /// <inheritdoc/>
    public void EvictionEvaluated() { }

    /// <inheritdoc/>
    public void EvictionTriggered() { }

    /// <inheritdoc/>
    public void EvictionExecuted() { }

    /// <inheritdoc/>
    public void EvictionSegmentRemoved() { }
}
