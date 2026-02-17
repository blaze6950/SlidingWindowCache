namespace SlidingWindowCache.Infrastructure.Instrumentation;

/// <summary>
/// No-op implementation of ICacheDiagnostics for production use where performance is critical and diagnostics are not needed.
/// </summary>
public class NoOpDiagnostics : ICacheDiagnostics
{
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
    public void RebalanceIntentCancelled()
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