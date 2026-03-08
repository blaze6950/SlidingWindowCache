namespace Intervals.NET.Caching.VisitedPlaces.Public.Instrumentation;

/// <summary>
/// Diagnostics interface for tracking behavioral events in
/// <see cref="Cache.VisitedPlacesCache{TRange,TData,TDomain}"/>.
/// Extends <see cref="ICacheDiagnostics"/> with VisitedPlaces-specific normalization and eviction events.
/// All methods are fire-and-forget; implementations must never throw.
/// </summary>
/// <remarks>
/// <para>
/// The default implementation is <see cref="NoOpDiagnostics"/>, which silently discards all events.
/// For testing and observability, provide a custom implementation or use
/// <c>EventCounterCacheDiagnostics</c> from the test infrastructure package.
/// </para>
/// </remarks>
public interface IVisitedPlacesCacheDiagnostics : ICacheDiagnostics
{
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
    /// Records a normalization request received and started processing by the Background Path.
    /// Location: CacheNormalizationExecutor.ExecuteAsync (entry)
    /// Related: Invariant VPC.B.2
    /// </summary>
    void NormalizationRequestReceived();

    /// <summary>
    /// Records a normalization request fully processed by the Background Path (all 4 steps completed).
    /// Location: CacheNormalizationExecutor.ExecuteAsync (exit)
    /// Related: Invariant VPC.B.3
    /// </summary>
    void NormalizationRequestProcessed();

    /// <summary>
    /// Records statistics updated for used segments (Background Path step 1).
    /// Location: CacheNormalizationExecutor.ExecuteAsync (step 1)
    /// Related: Invariant VPC.E.4b
    /// </summary>
    void BackgroundStatisticsUpdated();

    /// <summary>
    /// Records a new segment stored in the cache (Background Path step 2).
    /// Location: CacheNormalizationExecutor.ExecuteAsync (step 2)
    /// Related: Invariant VPC.B.3, VPC.C.1
    /// </summary>
    void BackgroundSegmentStored();

    // ============================================================================
    // EVICTION COUNTERS
    // ============================================================================

    /// <summary>
    /// Records an eviction evaluation pass (Background Path step 3).
    /// Called once per storage step, regardless of whether any evaluator fired.
    /// Location: CacheNormalizationExecutor.ExecuteAsync (step 3)
    /// Related: Invariant VPC.E.1a
    /// </summary>
    void EvictionEvaluated();

    /// <summary>
    /// Records that at least one eviction evaluator fired and eviction will be executed.
    /// Location: CacheNormalizationExecutor.ExecuteAsync (step 3, at least one evaluator fired)
    /// Related: Invariant VPC.E.1a, VPC.E.2a
    /// </summary>
    void EvictionTriggered();

    /// <summary>
    /// Records a completed eviction execution pass (Background Path step 4).
    /// Location: CacheNormalizationExecutor.ExecuteAsync (step 4)
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
}
