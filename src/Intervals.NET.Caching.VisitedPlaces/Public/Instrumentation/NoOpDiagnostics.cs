namespace Intervals.NET.Caching.VisitedPlaces.Public.Instrumentation;

/// <summary>
/// No-op implementation of <see cref="ICacheDiagnostics"/> that silently discards all events.
/// Used as the default when no diagnostics are configured.
/// </summary>
/// <remarks>
/// Access the singleton via <see cref="Instance"/>. Do not construct additional instances.
/// </remarks>
public sealed class NoOpDiagnostics : ICacheDiagnostics
{
    /// <summary>The singleton no-op diagnostics instance.</summary>
    public static readonly ICacheDiagnostics Instance = new NoOpDiagnostics();

    private NoOpDiagnostics() { }

    /// <inheritdoc/>
    public void UserRequestServed() { }

    /// <inheritdoc/>
    public void UserRequestFullCacheHit() { }

    /// <inheritdoc/>
    public void UserRequestPartialCacheHit() { }

    /// <inheritdoc/>
    public void UserRequestFullCacheMiss() { }

    /// <inheritdoc/>
    public void DataSourceFetchGap() { }

    /// <inheritdoc/>
    public void BackgroundEventReceived() { }

    /// <inheritdoc/>
    public void BackgroundEventProcessed() { }

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

    /// <inheritdoc/>
    public void BackgroundEventProcessingFailed(Exception ex) { }
}
