namespace Intervals.NET.Caching.SlidingWindow.Public.Instrumentation;

/// <summary>
/// No-op implementation of <see cref="ISlidingWindowCacheDiagnostics"/> for production use
/// where performance is critical and diagnostics are not needed.
/// </summary>
public sealed class NoOpDiagnostics : NoOpCacheDiagnostics, ISlidingWindowCacheDiagnostics
{
    /// <summary>
    /// A shared singleton instance. Use this to avoid unnecessary allocations.
    /// </summary>
    public new static readonly NoOpDiagnostics Instance = new();

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
}
