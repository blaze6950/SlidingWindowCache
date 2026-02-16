namespace SlidingWindowCache.Infrastructure.Instrumentation;

/// <summary>
/// Instance-based diagnostics interface for tracking cache behavioral events in DEBUG mode.
/// Mirrors the public API of CacheInstrumentationCounters to enable dependency injection.
/// Used for testing and verification of system invariants.
/// </summary>
public interface ICacheDiagnostics
{
    // ============================================================================
    // USER PATH COUNTERS
    // ============================================================================

    /// <summary>
    /// Records a completed user request served by the User Path.
    /// Called at the end of UserRequestHandler.HandleRequestAsync after data is returned to the user and intent is published.
    /// Tracks completion of all user scenarios: cold start (U1), full cache hit (U2, U3), partial cache hit (U4), and full cache miss/jump (U5).
    /// Location: UserRequestHandler.HandleRequestAsync (final step)
    /// </summary>
    void UserRequestServed();

    /// <summary>
    /// Records when cache extension analysis determines that expansion is needed (intersection exists).
    /// Called during range analysis in CacheDataExtensionService.CalculateMissingRanges when determining
    /// which segments need to be fetched. This indicates the cache WILL BE expanded, not that mutation occurred.
    /// Note: This is called by the shared CacheDataExtensionService used by both User Path and Rebalance Path.
    /// The actual cache mutation (Rematerialize) only happens in Rebalance Execution.
    /// Location: CacheDataExtensionService.CalculateMissingRanges (when intersection exists)
    /// Related: Invariant 9a (Cache Contiguity Rule)
    /// </summary>
    void CacheExpanded();

    /// <summary>
    /// Records when cache extension analysis determines that full replacement is needed (no intersection).
    /// Called during range analysis in CacheDataExtensionService.CalculateMissingRanges when determining
    /// that RequestedRange does NOT intersect CurrentCacheRange. This indicates cache WILL BE replaced,
    /// not that mutation occurred. The actual cache mutation (Rematerialize) only happens in Rebalance Execution.
    /// Note: This is called by the shared CacheDataExtensionService used by both User Path and Rebalance Path.
    /// Location: CacheDataExtensionService.CalculateMissingRanges (when no intersection exists)
    /// Related: Invariant 9a (Cache Contiguity Rule - forbids gaps)
    /// </summary>
    void CacheReplaced();

    /// <summary>
    /// Records a full cache hit where all requested data is available in cache without fetching from IDataSource.
    /// Called when CurrentCacheRange fully contains RequestedRange, allowing direct read from cache.
    /// Represents optimal performance path (User Scenarios U2, U3).
    /// Location: UserRequestHandler.HandleRequestAsync (Scenario 2: Full Cache Hit)
    /// </summary>
    void UserRequestFullCacheHit();

    /// <summary>
    /// Records a partial cache hit where RequestedRange intersects CurrentCacheRange but is not fully contained.
    /// Called when some data is available in cache and missing segments are fetched from IDataSource and merged.
    /// Indicates efficient cache extension with partial reuse (User Scenario U4).
    /// Location: UserRequestHandler.HandleRequestAsync (Scenario 3: Partial Cache Hit)
    /// </summary>
    void UserRequestPartialCacheHit();

    /// <summary>
    /// Records a full cache miss requiring complete fetch from IDataSource.
    /// Called in two scenarios: cold start (no cache) or non-intersecting jump (cache exists but RequestedRange doesn't intersect).
    /// Indicates most expensive path with no cache reuse (User Scenarios U1, U5).
    /// Location: UserRequestHandler.HandleRequestAsync (Scenario 1: Cold Start, Scenario 4: Full Cache Miss)
    /// </summary>
    void UserRequestFullCacheMiss();

    // ============================================================================
    // DATA SOURCE ACCESS COUNTERS
    // ============================================================================

    /// <summary>
    /// Records a single-range fetch from IDataSource for a complete range.
    /// Called in cold start or non-intersecting jump scenarios where the entire RequestedRange must be fetched as one contiguous range.
    /// Indicates IDataSource.FetchAsync(Range) invocation for user-facing data assembly.
    /// Location: UserRequestHandler.HandleRequestAsync (Scenarios 1 and 4: Cold Start and Non-intersecting Jump)
    /// Related: User Path direct fetch operations
    /// </summary>
    void DataSourceFetchSingleRange();

    /// <summary>
    /// Records a missing-segments fetch from IDataSource during cache extension.
    /// Called when extending cache to cover RequestedRange by fetching only the missing segments (gaps between RequestedRange and CurrentCacheRange).
    /// Indicates IDataSource.FetchAsync(IEnumerable&lt;Range&gt;) invocation with computed missing ranges.
    /// Location: CacheDataExtensionService.ExtendCacheAsync (partial cache hit optimization)
    /// Related: User Scenario U4 and Rebalance Execution cache extension operations
    /// </summary>
    void DataSourceFetchMissingSegments();

    // ============================================================================
    // REBALANCE INTENT LIFECYCLE COUNTERS
    // ============================================================================

    /// <summary>
    /// Records publication of a rebalance intent by the User Path.
    /// Called after UserRequestHandler publishes an intent containing delivered data to IntentController.
    /// Every user request produces exactly one intent publication (fire-and-forget).
    /// Location: IntentController.PublishIntent (after scheduler receives intent)
    /// Related: Invariant A.3 (User Path is sole source of rebalance intent), Invariant 24e (Intent must contain delivered data)
    /// Note: Intent publication does NOT guarantee execution (opportunistic behavior)
    /// </summary>
    void RebalanceIntentPublished();

    /// <summary>
    /// Records cancellation of a rebalance intent before or during execution.
    /// Called when a new user request arrives and cancels the previous intent's CancellationToken, or when intent becomes obsolete during debounce delay.
    /// Indicates single-flight execution pattern and priority enforcement (User Path cancels Rebalance).
    /// Location: RebalanceScheduler (three scenarios: cancellation during debounce, cancellation before decision, cancellation during execution)
    /// Related: Invariant A.0 (User Path priority), Invariant A.0a (User Request must cancel ongoing rebalance), Invariant C.20 (Obsolete intent must not start)
    /// </summary>
    void RebalanceIntentCancelled();

    // ============================================================================
    // REBALANCE EXECUTION LIFECYCLE COUNTERS
    // ============================================================================

    /// <summary>
    /// Records the start of rebalance execution after decision engine approves execution.
    /// Called when DecisionEngine determines rebalance is necessary (RequestedRange outside NoRebalanceRange and DesiredCacheRange != CurrentCacheRange).
    /// Indicates transition from Decision Path to Execution Path (Decision Scenario D3).
    /// Location: RebalanceScheduler.ExecutePipelineAsync (after decision approval, before executor invocation)
    /// Related: Invariant 28 (Rebalance triggered only if confirmed necessary)
    /// </summary>
    void RebalanceExecutionStarted();

    /// <summary>
    /// Records successful completion of rebalance execution.
    /// Called after RebalanceExecutor successfully extends cache to DesiredCacheRange, trims excess data, and updates cache state.
    /// Indicates cache normalization completed and state mutations applied (Rebalance Scenarios R1, R2).
    /// Location: RebalanceExecutor.ExecuteAsync (final step after UpdateCacheState)
    /// Related: Invariant 34 (Only Rebalance Execution writes to cache), Invariant 35 (Cache state update is atomic)
    /// </summary>
    void RebalanceExecutionCompleted();

    /// <summary>
    /// Records cancellation of rebalance execution due to a new user request or intent supersession.
    /// Called when intentToken is cancelled during rebalance execution (after execution started but before completion).
    /// Indicates User Path priority enforcement and single-flight execution (yielding to new requests).
    /// Location: RebalanceScheduler.ExecutePipelineAsync (catch OperationCanceledException during execution)
    /// Related: Invariant 34a (Rebalance Execution must yield to User Path immediately)
    /// </summary>
    void RebalanceExecutionCancelled();

    // ============================================================================
    // REBALANCE SKIP OPTIMIZATION COUNTERS
    // ============================================================================

    /// <summary>
    /// Records a rebalance skipped due to RequestedRange being within NoRebalanceRange.
    /// Called when DecisionEngine determines rebalance is unnecessary because RequestedRange falls inside the no-rebalance threshold zone.
    /// Indicates policy-based skip decision before expensive operations (Decision Scenario D1).
    /// Location: RebalanceScheduler.ExecutePipelineAsync (after DecisionEngine returns ShouldExecute=false)
    /// Related: Invariant D.26 (No rebalance if inside NoRebalanceRange), Invariant D.27 (Policy-based skip tracking)
    /// </summary>
    void RebalanceSkippedNoRebalanceRange();

    /// <summary>
    /// Records a rebalance skipped because CurrentCacheRange equals DesiredCacheRange.
    /// Called when RebalanceExecutor detects that delivered data range already matches desired range, avoiding redundant I/O.
    /// Indicates same-range optimization preventing unnecessary fetch operations (Decision Scenario D2).
    /// Location: RebalanceExecutor.ExecuteAsync (before expensive I/O operations)
    /// Related: Invariant D.27 (No rebalance if DesiredCacheRange == CurrentCacheRange), Invariant D.28 (Same-range optimization tracking)
    /// </summary>
    void RebalanceSkippedSameRange();

    /// <summary>
    /// Records a rebalance execution failure due to an exception during execution.
    /// Called when an unhandled exception occurs during RebalanceExecutor.ExecuteAsync.
    /// </summary>
    /// <param name="ex">
    /// The exception that caused the rebalance execution to fail. This parameter provides details about the failure and can be used for logging and diagnostics.
    /// </param>
    /// <remarks>
    /// <para><strong>⚠️ CRITICAL: Applications MUST handle this event</strong></para>
    /// <para>
    /// Rebalance operations execute in fire-and-forget background tasks. When an exception occurs,
    /// the task catches it, records this event, and silently swallows the exception to prevent
    /// application crashes from unhandled task exceptions.
    /// </para>
    /// <para><strong>Consequences of ignoring this event:</strong></para>
    /// <list type="bullet">
    /// <item><description>Silent failures in background operations</description></item>
    /// <item><description>Cache may stop rebalancing without any visible indication</description></item>
    /// <item><description>Degraded performance with no diagnostics</description></item>
    /// <item><description>Data source errors may go unnoticed</description></item>
    /// </list>
    /// <para><strong>Recommended implementation:</strong></para>
    /// <para>
    /// At minimum, log all RebalanceExecutionFailed events with full exception details.
    /// Consider also implementing:
    /// </para>
    /// <list type="bullet">
    /// <item><description>Structured logging with context (requested range, cache state)</description></item>
    /// <item><description>Alerting for repeated failures (circuit breaker pattern)</description></item>
    /// <item><description>Metrics tracking failure rate and exception types</description></item>
    /// <item><description>Graceful degradation strategies (e.g., disable rebalancing after N failures)</description></item>
    /// </list>
    /// <para><strong>Example implementation:</strong></para>
    /// <code>
    /// public class LoggingCacheDiagnostics : ICacheDiagnostics
    /// {
    ///     private readonly ILogger _logger;
    ///     
    ///     public void RebalanceExecutionFailed(Exception ex)
    ///     {
    ///         _logger.LogError(ex, "Cache rebalance execution failed. Cache may not be optimally sized.");
    ///         // Optional: Increment error counter for monitoring
    ///         // Optional: Trigger alert if failure rate exceeds threshold
    ///     }
    ///     
    ///     // ...other methods...
    /// }
    /// </code>
    /// <para>
    /// Location: RebalanceScheduler.ExecutePipelineAsync (catch block around ExecuteAsync)
    /// </para>
    /// </remarks>
    void RebalanceExecutionFailed(Exception ex);
}