using System.Diagnostics;

namespace SlidingWindowCache.Instrumentation;

/// <summary>
/// Thread-safe static instrumentation counters for tracking cache behavioral events in DEBUG mode.
/// Used for testing and verification of system invariants.
/// </summary>
public static class CacheInstrumentationCounters
{
    private static int _userRequestsServed;
    private static int _cacheExpanded;
    private static int _cacheReplaced;
    private static int _rebalanceIntentPublished;
    private static int _rebalanceIntentCancelled;
    private static int _rebalanceExecutionStarted;
    private static int _rebalanceExecutionCompleted;
    private static int _rebalanceExecutionCancelled;
    private static int _rebalanceSkippedNoRebalanceRange;
    private static int _rebalanceSkippedSameRange;
    private static int _userRequestFullCacheHit;
    private static int _userRequestPartialCacheHit;
    private static int _userRequestFullCacheMiss;

    // User Path counters
    public static int UserRequestsServed => _userRequestsServed;
    public static int CacheExpanded => _cacheExpanded;
    public static int CacheReplaced => _cacheReplaced;
    public static int UserRequestFullCacheHit => _userRequestFullCacheHit;
    public static int UserRequestPartialCacheHit => _userRequestPartialCacheHit;
    public static int UserRequestFullCacheMiss => _userRequestFullCacheMiss;

    // Rebalance Intent lifecycle counters
    public static int RebalanceIntentPublished => _rebalanceIntentPublished;
    public static int RebalanceIntentCancelled => _rebalanceIntentCancelled;

    // Rebalance Execution lifecycle counters
    public static int RebalanceExecutionStarted => _rebalanceExecutionStarted;
    public static int RebalanceExecutionCompleted => _rebalanceExecutionCompleted;
    public static int RebalanceExecutionCancelled => _rebalanceExecutionCancelled;

    /// <summary>
    /// Incremented when rebalance is skipped due to RequestedRange being within NoRebalanceRange.
    /// This counter tracks policy-based skip decision (Invariant D.27).
    /// Location: RebalanceScheduler (after DecisionEngine returns ShouldExecute=false)
    /// </summary>
    public static int RebalanceSkippedNoRebalanceRange => _rebalanceSkippedNoRebalanceRange;

    /// <summary>
    /// Incremented when rebalance execution is skipped because CurrentCacheRange == DesiredCacheRange.
    /// This counter tracks same-range optimization (Invariant D.28).
    /// Location: RebalanceExecutor.ExecuteAsync (before expensive I/O operations)
    /// </summary>
    public static int RebalanceSkippedSameRange => _rebalanceSkippedSameRange;

    [Conditional("DEBUG")]
    internal static void OnUserRequestServed() => Interlocked.Increment(ref _userRequestsServed);

    [Conditional("DEBUG")]
    internal static void OnCacheExpanded() => Interlocked.Increment(ref _cacheExpanded);

    [Conditional("DEBUG")]
    internal static void OnCacheReplaced() => Interlocked.Increment(ref _cacheReplaced);

    [Conditional("DEBUG")]
    internal static void OnRebalanceIntentPublished() => Interlocked.Increment(ref _rebalanceIntentPublished);

    [Conditional("DEBUG")]
    internal static void OnRebalanceIntentCancelled() => Interlocked.Increment(ref _rebalanceIntentCancelled);

    [Conditional("DEBUG")]
    internal static void OnRebalanceExecutionStarted() => Interlocked.Increment(ref _rebalanceExecutionStarted);

    [Conditional("DEBUG")]
    internal static void OnRebalanceExecutionCompleted() => Interlocked.Increment(ref _rebalanceExecutionCompleted);

    [Conditional("DEBUG")]
    internal static void OnRebalanceExecutionCancelled() => Interlocked.Increment(ref _rebalanceExecutionCancelled);

    [Conditional("DEBUG")]
    internal static void OnRebalanceSkippedNoRebalanceRange() =>
        Interlocked.Increment(ref _rebalanceSkippedNoRebalanceRange);

    [Conditional("DEBUG")]
    internal static void OnRebalanceSkippedSameRange() => Interlocked.Increment(ref _rebalanceSkippedSameRange);

    [Conditional("DEBUG")]
    internal static void OnUserRequestFullCacheHit() => Interlocked.Increment(ref _userRequestFullCacheHit);

    [Conditional("DEBUG")]
    internal static void OnUserRequestPartialCacheHit() => Interlocked.Increment(ref _userRequestPartialCacheHit);

    [Conditional("DEBUG")]
    internal static void OnUserRequestFullCacheMiss() => Interlocked.Increment(ref _userRequestFullCacheMiss);

    /// <summary>
    /// Resets all counters to zero. Use this before each test to ensure clean state.
    /// </summary>
    [Conditional("DEBUG")]
    public static void Reset()
    {
        _userRequestsServed = 0;
        _cacheExpanded = 0;
        _cacheReplaced = 0;
        _rebalanceIntentPublished = 0;
        _rebalanceIntentCancelled = 0;
        _rebalanceExecutionStarted = 0;
        _rebalanceExecutionCompleted = 0;
        _rebalanceExecutionCancelled = 0;
        _rebalanceSkippedNoRebalanceRange = 0;
        _rebalanceSkippedSameRange = 0;
        _userRequestFullCacheHit = 0;
        _userRequestPartialCacheHit = 0;
        _userRequestFullCacheMiss = 0;
    }
}