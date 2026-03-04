namespace Intervals.NET.Caching.Public.Instrumentation;

/// <summary>
/// No-op implementation of ICacheDiagnostics for production use where performance is critical and diagnostics are not needed.
/// </summary>
public sealed class NoOpDiagnostics : ICacheDiagnostics
{
    /// <summary>
    /// A shared singleton instance. Use this to avoid unnecessary allocations.
    /// </summary>
    public static readonly NoOpDiagnostics Instance = new();

    /// <inheritdoc/>
    public void CacheExpanded()
    {
    }

    /// <inheritdoc/>
    public void CacheReplaced()
    {
    }

    /// <inheritdoc/>
    public void DataSourceFetchMissingSegments()
    {
    }

    /// <inheritdoc/>
    public void DataSegmentUnavailable()
    {
    }

    /// <inheritdoc/>
    public void DataSourceFetchSingleRange()
    {
    }

    /// <inheritdoc/>
    public void RebalanceExecutionCancelled()
    {
    }

    /// <inheritdoc/>
    public void RebalanceExecutionCompleted()
    {
    }

    /// <inheritdoc/>
    public void RebalanceExecutionStarted()
    {
    }

    /// <inheritdoc/>
    public void RebalanceIntentPublished()
    {
    }

    /// <inheritdoc/>
    public void RebalanceSkippedCurrentNoRebalanceRange()
    {
    }

    /// <inheritdoc/>
    public void RebalanceSkippedPendingNoRebalanceRange()
    {
    }

    /// <inheritdoc/>
    public void RebalanceSkippedSameRange()
    {
    }

    /// <inheritdoc/>
    public void RebalanceScheduled()
    {
    }

    /// <inheritdoc/>
    public void RebalanceExecutionFailed(Exception ex)
    {
        // Intentional no-op: this implementation discards all diagnostics including failures.
        // For production systems, use EventCounterCacheDiagnostics or a custom ICacheDiagnostics
        // implementation that logs to your observability pipeline.
    }

    /// <inheritdoc/>
    public void UserRequestFullCacheHit()
    {
    }

    /// <inheritdoc/>
    public void UserRequestFullCacheMiss()
    {
    }

    /// <inheritdoc/>
    public void UserRequestPartialCacheHit()
    {
    }

    /// <inheritdoc/>
    public void UserRequestServed()
    {
    }
}