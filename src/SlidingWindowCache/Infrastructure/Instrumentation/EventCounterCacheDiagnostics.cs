using System.Diagnostics;

namespace SlidingWindowCache.Infrastructure.Instrumentation;

/// <summary>
/// Default implementation of <see cref="ICacheDiagnostics"/> that uses thread-safe counters to track cache events and metrics.
/// </summary>
public class EventCounterCacheDiagnostics : ICacheDiagnostics
{
    private int _userRequestServed;
    private int _cacheExpanded;
    private int _cacheReplaced;
    private int _rebalanceIntentPublished;
    private int _rebalanceIntentCancelled;
    private int _rebalanceExecutionStarted;
    private int _rebalanceExecutionCompleted;
    private int _rebalanceExecutionCancelled;
    private int _rebalanceSkippedCurrentNoRebalanceRange;
    private int _rebalanceSkippedPendingNoRebalanceRange;
    private int _rebalanceSkippedSameRange;
    private int _rebalanceScheduled;
    private int _userRequestFullCacheHit;
    private int _userRequestPartialCacheHit;
    private int _userRequestFullCacheMiss;
    private int _dataSourceFetchSingleRange;
    private int _dataSourceFetchMissingSegments;
    private int _dataSegmentUnavailable;
    private int _rebalanceExecutionFailed;

    public int UserRequestServed => _userRequestServed;
    public int CacheExpanded => _cacheExpanded;
    public int CacheReplaced => _cacheReplaced;
    public int UserRequestFullCacheHit => _userRequestFullCacheHit;
    public int UserRequestPartialCacheHit => _userRequestPartialCacheHit;
    public int UserRequestFullCacheMiss => _userRequestFullCacheMiss;
    public int DataSourceFetchSingleRange => _dataSourceFetchSingleRange;
    public int DataSourceFetchMissingSegments => _dataSourceFetchMissingSegments;
    public int DataSegmentUnavailable => _dataSegmentUnavailable;
    public int RebalanceIntentPublished => _rebalanceIntentPublished;
    public int RebalanceIntentCancelled => _rebalanceIntentCancelled;
    public int RebalanceExecutionStarted => _rebalanceExecutionStarted;
    public int RebalanceExecutionCompleted => _rebalanceExecutionCompleted;
    public int RebalanceExecutionCancelled => _rebalanceExecutionCancelled;
    public int RebalanceSkippedCurrentNoRebalanceRange => _rebalanceSkippedCurrentNoRebalanceRange;
    public int RebalanceSkippedPendingNoRebalanceRange => _rebalanceSkippedPendingNoRebalanceRange;
    public int RebalanceSkippedSameRange => _rebalanceSkippedSameRange;
    public int RebalanceScheduled => _rebalanceScheduled;
    public int RebalanceExecutionFailed => _rebalanceExecutionFailed;

    /// <inheritdoc/>
    void ICacheDiagnostics.CacheExpanded() => Interlocked.Increment(ref _cacheExpanded);

    /// <inheritdoc/>
    void ICacheDiagnostics.CacheReplaced() => Interlocked.Increment(ref _cacheReplaced);

    /// <inheritdoc/>
    void ICacheDiagnostics.DataSourceFetchMissingSegments() =>
        Interlocked.Increment(ref _dataSourceFetchMissingSegments);

    /// <inheritdoc/>
    void ICacheDiagnostics.DataSegmentUnavailable() =>
        Interlocked.Increment(ref _dataSegmentUnavailable);

    /// <inheritdoc/>
    void ICacheDiagnostics.DataSourceFetchSingleRange() => Interlocked.Increment(ref _dataSourceFetchSingleRange);

    /// <inheritdoc/>
    void ICacheDiagnostics.RebalanceExecutionCancelled() => Interlocked.Increment(ref _rebalanceExecutionCancelled);

    /// <inheritdoc/>
    void ICacheDiagnostics.RebalanceExecutionCompleted() => Interlocked.Increment(ref _rebalanceExecutionCompleted);

    /// <inheritdoc/>
    void ICacheDiagnostics.RebalanceExecutionStarted() => Interlocked.Increment(ref _rebalanceExecutionStarted);

    /// <inheritdoc/>
    void ICacheDiagnostics.RebalanceIntentCancelled() => Interlocked.Increment(ref _rebalanceIntentCancelled);

    /// <inheritdoc/>
    void ICacheDiagnostics.RebalanceIntentPublished() => Interlocked.Increment(ref _rebalanceIntentPublished);

    /// <inheritdoc/>
    void ICacheDiagnostics.RebalanceSkippedCurrentNoRebalanceRange() =>
        Interlocked.Increment(ref _rebalanceSkippedCurrentNoRebalanceRange);

    /// <inheritdoc/>
    void ICacheDiagnostics.RebalanceSkippedPendingNoRebalanceRange() =>
        Interlocked.Increment(ref _rebalanceSkippedPendingNoRebalanceRange);

    /// <inheritdoc/>
    void ICacheDiagnostics.RebalanceSkippedSameRange() => Interlocked.Increment(ref _rebalanceSkippedSameRange);

    /// <inheritdoc/>
    void ICacheDiagnostics.RebalanceScheduled() => Interlocked.Increment(ref _rebalanceScheduled);

    /// <inheritdoc/>
    void ICacheDiagnostics.RebalanceExecutionFailed(Exception ex)
    {
        Interlocked.Increment(ref _rebalanceExecutionFailed);

        // ⚠️ WARNING: This default implementation only writes to Debug output!
        // For production use, you MUST create a custom implementation that:
        // 1. Logs to your logging framework (e.g., ILogger, Serilog, NLog)
        // 2. Includes full exception details (message, stack trace, inner exceptions)
        // 3. Considers alerting/monitoring for repeated failures
        //
        // Example:
        // _logger.LogError(ex, "Cache rebalance execution failed. Cache may not be optimally sized.");
        Debug.WriteLine($"⚠️ Rebalance execution failed: {ex}");
    }

    /// <inheritdoc/>
    void ICacheDiagnostics.UserRequestFullCacheHit() => Interlocked.Increment(ref _userRequestFullCacheHit);

    /// <inheritdoc/>
    void ICacheDiagnostics.UserRequestFullCacheMiss() => Interlocked.Increment(ref _userRequestFullCacheMiss);

    /// <inheritdoc/>
    void ICacheDiagnostics.UserRequestPartialCacheHit() => Interlocked.Increment(ref _userRequestPartialCacheHit);

    /// <inheritdoc/>
    void ICacheDiagnostics.UserRequestServed() => Interlocked.Increment(ref _userRequestServed);

    /// <summary>
    /// Resets all counters to zero. Use this before each test to ensure clean state.
    /// </summary>
    public void Reset()
    {
        _userRequestServed = 0;
        _cacheExpanded = 0;
        _cacheReplaced = 0;
        _rebalanceIntentPublished = 0;
        _rebalanceIntentCancelled = 0;
        _rebalanceExecutionStarted = 0;
        _rebalanceExecutionCompleted = 0;
        _rebalanceExecutionCancelled = 0;
        _rebalanceSkippedCurrentNoRebalanceRange = 0;
        _rebalanceSkippedPendingNoRebalanceRange = 0;
        _rebalanceSkippedSameRange = 0;
        _rebalanceScheduled = 0;
        _userRequestFullCacheHit = 0;
        _userRequestPartialCacheHit = 0;
        _userRequestFullCacheMiss = 0;
        _dataSourceFetchSingleRange = 0;
        _dataSourceFetchMissingSegments = 0;
        _dataSegmentUnavailable = 0;
        _rebalanceExecutionFailed = 0;
    }
}