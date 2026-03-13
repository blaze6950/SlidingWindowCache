using Intervals.NET.Caching.Infrastructure.Diagnostics;

namespace Intervals.NET.Caching.SlidingWindow.Public.Instrumentation;

/// <summary>
/// Diagnostics interface for tracking cache behavioral events in
/// <see cref="Cache.SlidingWindowCache{TRange,TData,TDomain}"/>.
/// Extends <see cref="ICacheDiagnostics"/> with SlidingWindow-specific rebalance lifecycle events.
/// All methods are fire-and-forget; implementations must never throw.
/// </summary>
/// <remarks>
/// <para>
/// The default no-op implementation is <see cref="NoOpDiagnostics"/>.
/// For testing and observability, use <see cref="EventCounterCacheDiagnostics"/> or
/// provide a custom implementation.
/// </para>
/// <para><strong>Execution Context Summary</strong></para>
/// <para>
/// Each method fires synchronously on the thread that triggers the event.
/// See the individual method's <c>Context:</c> annotation for details.
/// </para>
/// <list type="table">
/// <listheader><term>Method</term><term>Thread Context</term></listheader>
/// <item><term><see cref="CacheExpanded"/></term><term>User Thread or Background Thread (Rebalance Execution)</term></item>
/// <item><term><see cref="CacheReplaced"/></term><term>User Thread or Background Thread (Rebalance Execution)</term></item>
/// <item><term><see cref="DataSourceFetchSingleRange"/></term><term>User Thread</term></item>
/// <item><term><see cref="DataSourceFetchMissingSegments"/></term><term>User Thread or Background Thread (Rebalance Execution)</term></item>
/// <item><term><see cref="DataSegmentUnavailable"/></term><term>User Thread or Background Thread (Rebalance Execution)</term></item>
/// <item><term><see cref="RebalanceIntentPublished"/></term><term>User Thread</term></item>
/// <item><term><see cref="RebalanceExecutionStarted"/></term><term>Background Thread (Rebalance Execution)</term></item>
/// <item><term><see cref="RebalanceExecutionCompleted"/></term><term>Background Thread (Rebalance Execution)</term></item>
/// <item><term><see cref="RebalanceExecutionCancelled"/></term><term>Background Thread (Rebalance Execution)</term></item>
/// <item><term><see cref="RebalanceSkippedCurrentNoRebalanceRange"/></term><term>Background Thread (Intent Processing Loop)</term></item>
/// <item><term><see cref="RebalanceSkippedPendingNoRebalanceRange"/></term><term>Background Thread (Intent Processing Loop)</term></item>
/// <item><term><see cref="RebalanceSkippedSameRange"/></term><term>Background Thread (Rebalance Execution)</term></item>
/// <item><term><see cref="RebalanceScheduled"/></term><term>Background Thread (Intent Processing Loop)</term></item>
/// </list>
/// <para>
/// Inherited from <see cref="ICacheDiagnostics"/>: <c>UserRequestServed</c>,
/// <c>UserRequestFullCacheHit</c>, <c>UserRequestPartialCacheHit</c>,
/// <c>UserRequestFullCacheMiss</c> — all User Thread.
/// <c>BackgroundOperationFailed</c> — Background Thread (Intent Processing Loop or Rebalance Execution).
/// </para>
/// </remarks>
public interface ISlidingWindowCacheDiagnostics : ICacheDiagnostics
{
    // ============================================================================
    // CACHE MUTATION COUNTERS
    // ============================================================================

    /// <summary>
    /// Records when cache extension analysis determines that expansion is needed (intersection exists).
    /// Called during range analysis in CacheDataExtensionService.CalculateMissingRanges when determining
    /// which segments need to be fetched. This indicates the cache WILL BE expanded, not that mutation occurred.
    /// Note: This is called by the shared CacheDataExtensionService used by both User Path and Rebalance Path.
    /// The actual cache mutation (Rematerialize) only happens in Rebalance Execution.
    /// Location: CacheDataExtensionService.CalculateMissingRanges (when intersection exists)
    /// Related: Invariant SWC.A.12b (Cache Contiguity Rule)
    /// </summary>
    /// <remarks>
    /// <para><strong>Context:</strong> User Thread (Partial Cache Hit — Scenario U4) or Background Thread (Rebalance Execution)</para>
    /// </remarks>
    void CacheExpanded();

    /// <summary>
    /// Records when cache extension analysis determines that full replacement is needed (no intersection).
    /// Called during range analysis in CacheDataExtensionService.CalculateMissingRanges when determining
    /// that RequestedRange does NOT intersect CurrentCacheRange. This indicates cache WILL BE replaced,
    /// not that mutation occurred. The actual cache mutation (Rematerialize) only happens in Rebalance Execution.
    /// Note: This is called by the shared CacheDataExtensionService used by both User Path and Rebalance Path.
    /// Location: CacheDataExtensionService.CalculateMissingRanges (when no intersection exists)
    /// Related: Invariant SWC.A.12b (Cache Contiguity Rule - forbids gaps)
    /// </summary>
    /// <remarks>
    /// <para><strong>Context:</strong> User Thread (Full Cache Miss — Scenario U5) or Background Thread (Rebalance Execution)</para>
    /// </remarks>
    void CacheReplaced();

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
    /// <remarks>
    /// <para><strong>Context:</strong> User Thread</para>
    /// </remarks>
    void DataSourceFetchSingleRange();

    /// <summary>
    /// Records a missing-segments fetch from IDataSource during cache extension.
    /// Called when extending cache to cover RequestedRange by fetching only the missing segments (gaps between RequestedRange and CurrentCacheRange).
    /// Indicates IDataSource.FetchAsync(IEnumerable&lt;Range&gt;) invocation with computed missing ranges.
    /// Location: CacheDataExtensionService.ExtendCacheAsync (partial cache hit optimization)
    /// Related: User Scenario U4 and Rebalance Execution cache extension operations
    /// </summary>
    /// <remarks>
    /// <para><strong>Context:</strong> User Thread (Partial Cache Hit — Scenario U4) or Background Thread (Rebalance Execution)</para>
    /// </remarks>
    void DataSourceFetchMissingSegments();

    /// <summary>
    /// Called when a data segment is unavailable because the DataSource returned a null Range.
    /// This typically occurs when prefetching or extending the cache hits physical boundaries
    /// (e.g., database min/max IDs, time-series with temporal limits, paginated APIs with max pages).
    /// </summary>
    /// <remarks>
    /// <para><strong>Context:</strong> User Thread (Partial Cache Hit — Scenario 3) and Background Thread (Rebalance Execution)</para>
    /// <para>
    /// This is informational only - the system handles boundaries gracefully by skipping
    /// unavailable segments during cache union (UnionAll), preserving cache contiguity (Invariant A.12b).
    /// </para>
    /// <para><strong>Typical Scenarios:</strong></para>
    /// <list type="bullet">
    /// <item><description>Database with min/max ID bounds - extension tries to expand beyond available range</description></item>
    /// <item><description>Time-series data with temporal limits - requesting future/past data not yet/no longer available</description></item>
    /// <item><description>Paginated API with maximum pages - attempting to fetch beyond last page</description></item>
    /// </list>
    /// <para>
    /// Location: CacheDataExtensionService.UnionAll (when a fetched chunk has a null Range)
    /// </para>
    /// <para>
    /// Related: Invariant SWC.G.5 (IDataSource Boundary Semantics), Invariant SWC.A.12b (Cache Contiguity)
    /// </para>
    /// </remarks>
    void DataSegmentUnavailable();

    // ============================================================================
    // REBALANCE INTENT LIFECYCLE COUNTERS
    // ============================================================================

    /// <summary>
    /// Records publication of a rebalance intent by the User Path.
    /// Called after UserRequestHandler publishes an intent containing delivered data to IntentController.
    /// Intent is published only when the user request results in assembled data (assembledData != null).
    /// Physical boundary misses — where IDataSource returns null for the requested range — do not produce an intent
    /// because there is no delivered data to embed in the intent (see Invariant C.8e).
    /// Location: IntentController.PublishIntent (after scheduler receives intent)
    /// Related: Invariant SWC.A.5 (User Path is sole source of rebalance intent), Invariant SWC.C.8e (Intent must contain delivered data)
    /// Note: Intent publication does NOT guarantee execution (opportunistic behavior)
    /// </summary>
    /// <remarks>
    /// <para><strong>Context:</strong> User Thread</para>
    /// </remarks>
    void RebalanceIntentPublished();

    // ============================================================================
    // REBALANCE EXECUTION LIFECYCLE COUNTERS
    // ============================================================================

    /// <summary>
    /// Records the start of rebalance execution after decision engine approves execution.
    /// Called when DecisionEngine determines rebalance is necessary (RequestedRange outside NoRebalanceRange and DesiredCacheRange != CurrentCacheRange).
    /// Indicates transition from Decision Path to Execution Path (Decision Scenario D3).
    /// Location: UnboundedSupersessionWorkScheduler.ExecuteRequestAsync / BoundedSupersessionWorkScheduler.ProcessExecutionRequestsAsync (before executor invocation)
    /// Related: Invariant SWC.D.5 (Rebalance triggered only if confirmed necessary)
    /// </summary>
    /// <remarks>
    /// <para><strong>Context:</strong> Background Thread (Rebalance Execution)</para>
    /// </remarks>
    void RebalanceExecutionStarted();

    /// <summary>
    /// Records successful completion of rebalance execution.
    /// Called after RebalanceExecutor successfully extends cache to DesiredCacheRange, trims excess data, and updates cache state.
    /// Indicates cache normalization completed and state mutations applied (Rebalance Scenarios R1, R2).
    /// Location: RebalanceExecutor.ExecuteAsync (final step after UpdateCacheState)
    /// Related: Invariant SWC.F.2 (Only Rebalance Execution writes to cache), Invariant SWC.B.2 (Changes to CacheData and CurrentCacheRange are performed atomically)
    /// </summary>
    /// <remarks>
    /// <para><strong>Context:</strong> Background Thread (Rebalance Execution)</para>
    /// </remarks>
    void RebalanceExecutionCompleted();

    /// <summary>
    /// Records cancellation of rebalance execution due to a new user request or intent supersession.
    /// Called when intentToken is cancelled during rebalance execution (after execution started but before completion).
    /// Indicates User Path priority enforcement and single-flight execution (yielding to new requests).
    /// Location: UnboundedSupersessionWorkScheduler.ExecuteRequestAsync / BoundedSupersessionWorkScheduler.ProcessExecutionRequestsAsync (catch OperationCanceledException during execution)
    /// Related: Invariant SWC.F.1a (Rebalance Execution must yield to User Path immediately)
    /// </summary>
    /// <remarks>
    /// <para><strong>Context:</strong> Background Thread (Rebalance Execution)</para>
    /// </remarks>
    void RebalanceExecutionCancelled();

    // ============================================================================
    // REBALANCE SKIP OPTIMIZATION COUNTERS
    // ============================================================================

    /// <summary>
    /// Records a rebalance skipped due to RequestedRange being within the CURRENT cache's NoRebalanceRange (Stage 1).
    /// Called when DecisionEngine Stage 1 validation determines that the requested range is fully covered
    /// by the current cache's no-rebalance threshold zone, making rebalance unnecessary.
    /// This is the fast-path optimization that prevents unnecessary decision computation.
    /// </summary>
    /// <remarks>
    /// <para><strong>Decision Pipeline Stage:</strong> Stage 1 - Current Cache Stability Check</para>
    /// <para><strong>Context:</strong> Background Thread (Intent Processing Loop)</para>
    /// <para><strong>Location:</strong> IntentController.RecordReason (RebalanceReason.WithinCurrentNoRebalanceRange)</para>
    /// <para><strong>Related Invariants:</strong></para>
    /// <list type="bullet">
    /// <item><description>D.3: No rebalance if RequestedRange ⊆ CurrentNoRebalanceRange</description></item>
    /// <item><description>Stage 1 is the primary fast-path optimization</description></item>
    /// </list>
    /// </remarks>
    void RebalanceSkippedCurrentNoRebalanceRange();

    /// <summary>
    /// Records a rebalance skipped due to RequestedRange being within the PENDING rebalance's DesiredNoRebalanceRange (Stage 2).
    /// Called when DecisionEngine Stage 2 validation determines that the requested range will be covered
    /// by a pending rebalance's target no-rebalance zone, preventing cancellation storms and thrashing.
    /// This is the anti-thrashing optimization that protects scheduled-but-not-yet-executed rebalances.
    /// </summary>
    /// <remarks>
    /// <para><strong>Decision Pipeline Stage:</strong> Stage 2 - Pending Rebalance Stability Check (Anti-Thrashing)</para>
    /// <para><strong>Context:</strong> Background Thread (Intent Processing Loop)</para>
    /// <para><strong>Location:</strong> IntentController.RecordReason (RebalanceReason.WithinPendingNoRebalanceRange)</para>
    /// <para><strong>Related Invariants:</strong></para>
    /// <list type="bullet">
    /// <item><description>Stage 2 prevents cancellation storms</description></item>
    /// <item><description>Validates that pending rebalance will satisfy the request</description></item>
    /// <item><description>Key metric for measuring anti-thrashing effectiveness</description></item>
    /// </list>
    /// </remarks>
    void RebalanceSkippedPendingNoRebalanceRange();

    /// <summary>
    /// Records a rebalance skipped because CurrentCacheRange equals DesiredCacheRange.
    /// Called when RebalanceExecutor detects that delivered data range already matches desired range, avoiding redundant I/O.
    /// Indicates same-range optimization preventing unnecessary fetch operations (Decision Scenario D2).
    /// Location: RebalanceExecutor.ExecuteAsync (before expensive I/O operations)
    /// Related: Invariant SWC.D.4 (No rebalance if DesiredCacheRange == CurrentCacheRange), Invariant SWC.C.8c (RebalanceSkippedSameRange counter semantics)
    /// </summary>
    /// <remarks>
    /// <para><strong>Context:</strong> Background Thread (Rebalance Execution)</para>
    /// </remarks>
    void RebalanceSkippedSameRange();

    /// <summary>
    /// Records that a rebalance was scheduled for execution after passing all decision pipeline stages (Stage 5).
    /// Called when DecisionEngine completes all validation stages and determines rebalance is necessary,
    /// and IntentController successfully schedules the rebalance with the scheduler.
    /// This event occurs AFTER decision validation but BEFORE actual execution starts.
    /// </summary>
    /// <remarks>
    /// <para><strong>Decision Pipeline Stage:</strong> Stage 5 - Rebalance Required (Scheduling)</para>
    /// <para><strong>Context:</strong> Background Thread (Intent Processing Loop)</para>
    /// <para><strong>Location:</strong> IntentController.RecordReason (RebalanceReason.RebalanceRequired)</para>
    /// <para><strong>Lifecycle Position:</strong></para>
    /// <list type="number">
    /// <item><description>RebalanceIntentPublished - User request published intent</description></item>
    /// <item><description><strong>RebalanceScheduled</strong> - Decision validated, scheduled (THIS EVENT)</description></item>
    /// <item><description>RebalanceExecutionStarted - After debounce, execution begins</description></item>
    /// <item><description>RebalanceExecutionCompleted - Execution finished successfully</description></item>
    /// </list>
    /// <para><strong>Key Metrics:</strong></para>
    /// <list type="bullet">
    /// <item><description>Measures how many intents pass ALL decision stages</description></item>
    /// <item><description>Ratio vs RebalanceIntentPublished shows decision efficiency</description></item>
    /// <item><description>Ratio vs RebalanceExecutionStarted shows debounce/cancellation rate</description></item>
    /// </list>
    /// </remarks>
    void RebalanceScheduled();
}
