namespace Intervals.NET.Caching.VisitedPlaces.Public.Instrumentation;

/// <summary>
/// Diagnostics interface for tracking behavioral events in
/// <see cref="Cache.VisitedPlacesCache{TRange,TData,TDomain}"/>.
/// All methods are fire-and-forget; implementations must never throw.
/// </summary>
/// <remarks>
/// <para>
/// The default implementation is <see cref="NoOpDiagnostics"/>, which silently discards all events.
/// For testing and observability, provide a custom implementation or use
/// <c>EventCounterCacheDiagnostics</c> from the test infrastructure package.
/// </para>
/// </remarks>
/// TODO: Consider deduplicate diagnostic methods into a common shared ICacheDiagnostics that will be inside Intervals.NET.Caching. SWC and VPC will have their own specific diagnostics that implement this common interface, and the User Request Handler and Background Event Processor can depend on the common interface instead of separate ones. This will simplify instrumentation code and allow shared invariants (like VPC.A.9b) to be tracked by a single counter instead of separate ones in each package.
public interface ICacheDiagnostics
{
    // ============================================================================
    // USER PATH COUNTERS
    // ============================================================================

    /// <summary>
    /// Records a completed user request served by the User Path.
    /// Called at the end of <c>UserRequestHandler.HandleRequestAsync</c> for all successful requests.
    /// Location: UserRequestHandler.HandleRequestAsync (final step)
    /// </summary>
    void UserRequestServed();

    /// <summary>
    /// Records a full cache hit where the union of cached segments fully covers <c>RequestedRange</c>.
    /// No <c>IDataSource</c> call was made.
    /// Location: UserRequestHandler.HandleRequestAsync (Scenario U2/U3)
    /// Related: Invariant VPC.A.9b
    /// </summary>
    void UserRequestFullCacheHit();

    /// <summary>
    /// Records a partial cache hit where cached segments partially cover <c>RequestedRange</c>.
    /// <c>IDataSource.FetchAsync</c> was called for the gap(s).
    /// Location: UserRequestHandler.HandleRequestAsync (Scenario U4)
    /// Related: Invariant VPC.A.9b
    /// </summary>
    void UserRequestPartialCacheHit();

    /// <summary>
    /// Records a full cache miss where no cached segments intersect <c>RequestedRange</c>.
    /// <c>IDataSource.FetchAsync</c> was called for the full range.
    /// Location: UserRequestHandler.HandleRequestAsync (Scenario U1/U5)
    /// Related: Invariant VPC.A.9b
    /// </summary>
    void UserRequestFullCacheMiss();

    // ============================================================================
    // DATA SOURCE ACCESS COUNTERS
    // ============================================================================

    /// <summary>
    /// Records a data source fetch for a single gap range (partial-hit gap or full-miss).
    /// Called once per gap in the User Path.
    /// Location: UserRequestHandler.HandleRequestAsync
    /// Related: Invariant VPC.F.1
    /// </summary>
    void DataSourceFetchGap();

    // ============================================================================
    // BACKGROUND PROCESSING COUNTERS
    // ============================================================================

    /// <summary>
    /// Records a background event received and started processing by the Background Path.
    /// Location: BackgroundEventProcessor.ProcessEventAsync (entry)
    /// Related: Invariant VPC.B.2
    /// </summary>
    void BackgroundEventReceived();

    /// <summary>
    /// Records a background event fully processed by the Background Path (all 4 steps completed).
    /// Location: BackgroundEventProcessor.ProcessEventAsync (exit)
    /// Related: Invariant VPC.B.3
    /// </summary>
    void BackgroundEventProcessed();

    /// <summary>
    /// Records statistics updated for used segments (Background Path step 1).
    /// Location: BackgroundEventProcessor.ProcessEventAsync (step 1)
    /// Related: Invariant VPC.E.4b
    /// </summary>
    void BackgroundStatisticsUpdated();

    /// <summary>
    /// Records a new segment stored in the cache (Background Path step 2).
    /// Location: BackgroundEventProcessor.ProcessEventAsync (step 2)
    /// Related: Invariant VPC.B.3, VPC.C.1
    /// </summary>
    void BackgroundSegmentStored();

    // ============================================================================
    // EVICTION COUNTERS
    // ============================================================================

    /// <summary>
    /// Records an eviction evaluation pass (Background Path step 3).
    /// Called once per storage step, regardless of whether any evaluator fired.
    /// Location: BackgroundEventProcessor.ProcessEventAsync (step 3)
    /// Related: Invariant VPC.E.1a
    /// </summary>
    void EvictionEvaluated();

    /// <summary>
    /// Records that at least one eviction evaluator fired and eviction will be executed.
    /// Location: BackgroundEventProcessor.ProcessEventAsync (step 3, at least one evaluator fired)
    /// Related: Invariant VPC.E.1a, VPC.E.2a
    /// </summary>
    void EvictionTriggered();

    /// <summary>
    /// Records a completed eviction execution pass (Background Path step 4).
    /// Location: BackgroundEventProcessor.ProcessEventAsync (step 4)
    /// Related: Invariant VPC.E.2a
    /// </summary>
    void EvictionExecuted();

    /// <summary>
    /// Records a single segment removed from the cache during eviction.
    /// Called once per segment actually removed.
    /// Location: Eviction executor during step 4
    /// Related: Invariant VPC.E.6
    /// </summary>
    void EvictionSegmentRemoved();

    // ============================================================================
    // ERROR REPORTING
    // ============================================================================

    /// <summary>
    /// Records an unhandled exception that occurred during background event processing.
    /// The background loop swallows the exception after reporting it here to prevent crashes.
    /// Location: BackgroundEventProcessor.ProcessEventAsync (catch)
    /// </summary>
    /// <param name="ex">The exception that was thrown.</param>
    void BackgroundEventProcessingFailed(Exception ex);
}
