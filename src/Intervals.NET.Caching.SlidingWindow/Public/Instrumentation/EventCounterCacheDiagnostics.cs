using System.Diagnostics;

namespace Intervals.NET.Caching.SlidingWindow.Public.Instrumentation;

/// <summary>
/// Default implementation of <see cref="ISlidingWindowCacheDiagnostics"/> that uses thread-safe counters to track cache events and metrics.
/// </summary>
public sealed class EventCounterCacheDiagnostics : ISlidingWindowCacheDiagnostics
{
    private int _userRequestServed;
    private int _cacheExpanded;
    private int _cacheReplaced;
    private int _rebalanceIntentPublished;
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
    private int _backgroundOperationFailed;

    public int UserRequestServed => Volatile.Read(ref _userRequestServed);
    public int CacheExpanded => Volatile.Read(ref _cacheExpanded);
    public int CacheReplaced => Volatile.Read(ref _cacheReplaced);
    public int UserRequestFullCacheHit => Volatile.Read(ref _userRequestFullCacheHit);
    public int UserRequestPartialCacheHit => Volatile.Read(ref _userRequestPartialCacheHit);
    public int UserRequestFullCacheMiss => Volatile.Read(ref _userRequestFullCacheMiss);
    public int DataSourceFetchSingleRange => Volatile.Read(ref _dataSourceFetchSingleRange);
    public int DataSourceFetchMissingSegments => Volatile.Read(ref _dataSourceFetchMissingSegments);
    public int DataSegmentUnavailable => Volatile.Read(ref _dataSegmentUnavailable);
    public int RebalanceIntentPublished => Volatile.Read(ref _rebalanceIntentPublished);
    public int RebalanceExecutionStarted => Volatile.Read(ref _rebalanceExecutionStarted);
    public int RebalanceExecutionCompleted => Volatile.Read(ref _rebalanceExecutionCompleted);
    public int RebalanceExecutionCancelled => Volatile.Read(ref _rebalanceExecutionCancelled);
    public int RebalanceSkippedCurrentNoRebalanceRange => Volatile.Read(ref _rebalanceSkippedCurrentNoRebalanceRange);
    public int RebalanceSkippedPendingNoRebalanceRange => Volatile.Read(ref _rebalanceSkippedPendingNoRebalanceRange);
    public int RebalanceSkippedSameRange => Volatile.Read(ref _rebalanceSkippedSameRange);
    public int RebalanceScheduled => Volatile.Read(ref _rebalanceScheduled);
    public int BackgroundOperationFailed => Volatile.Read(ref _backgroundOperationFailed);

    /// <inheritdoc/>
    void ISlidingWindowCacheDiagnostics.CacheExpanded() => Interlocked.Increment(ref _cacheExpanded);

    /// <inheritdoc/>
    void ISlidingWindowCacheDiagnostics.CacheReplaced() => Interlocked.Increment(ref _cacheReplaced);

    /// <inheritdoc/>
    void ISlidingWindowCacheDiagnostics.DataSourceFetchMissingSegments() =>
        Interlocked.Increment(ref _dataSourceFetchMissingSegments);

    /// <inheritdoc/>
    void ISlidingWindowCacheDiagnostics.DataSegmentUnavailable() =>
        Interlocked.Increment(ref _dataSegmentUnavailable);

    /// <inheritdoc/>
    void ISlidingWindowCacheDiagnostics.DataSourceFetchSingleRange() => Interlocked.Increment(ref _dataSourceFetchSingleRange);

    /// <inheritdoc/>
    void ISlidingWindowCacheDiagnostics.RebalanceExecutionCancelled() => Interlocked.Increment(ref _rebalanceExecutionCancelled);

    /// <inheritdoc/>
    void ISlidingWindowCacheDiagnostics.RebalanceExecutionCompleted() => Interlocked.Increment(ref _rebalanceExecutionCompleted);

    /// <inheritdoc/>
    void ISlidingWindowCacheDiagnostics.RebalanceExecutionStarted() => Interlocked.Increment(ref _rebalanceExecutionStarted);

    /// <inheritdoc/>
    void ISlidingWindowCacheDiagnostics.RebalanceIntentPublished() => Interlocked.Increment(ref _rebalanceIntentPublished);

    /// <inheritdoc/>
    void ISlidingWindowCacheDiagnostics.RebalanceSkippedCurrentNoRebalanceRange() =>
        Interlocked.Increment(ref _rebalanceSkippedCurrentNoRebalanceRange);

    /// <inheritdoc/>
    void ISlidingWindowCacheDiagnostics.RebalanceSkippedPendingNoRebalanceRange() =>
        Interlocked.Increment(ref _rebalanceSkippedPendingNoRebalanceRange);

    /// <inheritdoc/>
    void ISlidingWindowCacheDiagnostics.RebalanceSkippedSameRange() => Interlocked.Increment(ref _rebalanceSkippedSameRange);

    /// <inheritdoc/>
    void ISlidingWindowCacheDiagnostics.RebalanceScheduled() => Interlocked.Increment(ref _rebalanceScheduled);

    /// <inheritdoc/>
    void ICacheDiagnostics.UserRequestFullCacheHit() => Interlocked.Increment(ref _userRequestFullCacheHit);

    /// <inheritdoc/>
    void ICacheDiagnostics.UserRequestFullCacheMiss() => Interlocked.Increment(ref _userRequestFullCacheMiss);

    /// <inheritdoc/>
    void ICacheDiagnostics.UserRequestPartialCacheHit() => Interlocked.Increment(ref _userRequestPartialCacheHit);

    /// <inheritdoc/>
    void ICacheDiagnostics.UserRequestServed() => Interlocked.Increment(ref _userRequestServed);

    /// <inheritdoc/>
    void ICacheDiagnostics.BackgroundOperationFailed(Exception ex)
    {
        Interlocked.Increment(ref _backgroundOperationFailed);

        // ?? WARNING: This default implementation only writes to Debug output!
        // For production use, you MUST create a custom implementation that:
        // 1. Logs to your logging framework (e.g., ILogger, Serilog, NLog)
        // 2. Includes full exception details (message, stack trace, inner exceptions)
        // 3. Considers alerting/monitoring for repeated failures
        //
        // Example:
        // _logger.LogError(ex, "Cache background operation failed. Cache may not be optimally sized.");
        Debug.WriteLine($"?? Background operation failed: {ex}");
    }

    /// <summary>
    /// Resets all counters to zero. Use this before each test to ensure clean state.
    /// </summary>
    /// <remarks>
    /// <para><strong>Warning — not atomic:</strong> This method resets each counter individually using
    /// <see cref="Volatile.Write"/>. In a concurrent environment, another thread may increment a counter
    /// between two consecutive resets, leaving the object in a partially-reset state. Only call this
    /// method when you can guarantee that no other thread is mutating the counters (e.g., after
    /// <c>WaitForIdleAsync</c> in tests).
    /// </para>
    /// </remarks>
    public void Reset()
    {
        Volatile.Write(ref _userRequestServed, 0);
        Volatile.Write(ref _cacheExpanded, 0);
        Volatile.Write(ref _cacheReplaced, 0);
        Volatile.Write(ref _rebalanceIntentPublished, 0);
        Volatile.Write(ref _rebalanceExecutionStarted, 0);
        Volatile.Write(ref _rebalanceExecutionCompleted, 0);
        Volatile.Write(ref _rebalanceExecutionCancelled, 0);
        Volatile.Write(ref _rebalanceSkippedCurrentNoRebalanceRange, 0);
        Volatile.Write(ref _rebalanceSkippedPendingNoRebalanceRange, 0);
        Volatile.Write(ref _rebalanceSkippedSameRange, 0);
        Volatile.Write(ref _rebalanceScheduled, 0);
        Volatile.Write(ref _userRequestFullCacheHit, 0);
        Volatile.Write(ref _userRequestPartialCacheHit, 0);
        Volatile.Write(ref _userRequestFullCacheMiss, 0);
        Volatile.Write(ref _dataSourceFetchSingleRange, 0);
        Volatile.Write(ref _dataSourceFetchMissingSegments, 0);
        Volatile.Write(ref _dataSegmentUnavailable, 0);
        Volatile.Write(ref _backgroundOperationFailed, 0);
    }
}
