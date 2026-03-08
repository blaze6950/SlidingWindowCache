using Intervals.NET.Caching.VisitedPlaces.Public.Instrumentation;

namespace Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure;

/// <summary>
/// A thread-safe diagnostics spy that counts all events fired by
/// <see cref="Intervals.NET.Caching.VisitedPlaces.Public.Cache.VisitedPlacesCache{TRange,TData,TDomain}"/>.
/// Suitable for use across all three test tiers (unit, integration, invariants).
/// </summary>
/// <remarks>
/// All counters are updated via <see cref="Interlocked.Increment"/> and read via
/// <see cref="Volatile.Read"/> to guarantee safe access from concurrent test threads.
/// </remarks>
public sealed class EventCounterCacheDiagnostics : IVisitedPlacesCacheDiagnostics
{
    // ============================================================
    // BACKING FIELDS
    // ============================================================

    private int _userRequestServed;
    private int _userRequestFullCacheHit;
    private int _userRequestPartialCacheHit;
    private int _userRequestFullCacheMiss;
    private int _dataSourceFetchGap;
    private int _normalizationRequestReceived;
    private int _normalizationRequestProcessed;
    private int _backgroundStatisticsUpdated;
    private int _backgroundSegmentStored;
    private int _evictionEvaluated;
    private int _evictionTriggered;
    private int _evictionExecuted;
    private int _evictionSegmentRemoved;
    private int _backgroundOperationFailed;

    // ============================================================
    // USER PATH COUNTERS
    // ============================================================

    /// <summary>Number of user requests successfully served.</summary>
    public int UserRequestServed => Volatile.Read(ref _userRequestServed);

    /// <summary>Number of requests that were full cache hits (no data source call).</summary>
    public int UserRequestFullCacheHit => Volatile.Read(ref _userRequestFullCacheHit);

    /// <summary>Number of requests that were partial cache hits (gap fetch required).</summary>
    public int UserRequestPartialCacheHit => Volatile.Read(ref _userRequestPartialCacheHit);

    /// <summary>Number of requests that were full cache misses (all data fetched from source).</summary>
    public int UserRequestFullCacheMiss => Volatile.Read(ref _userRequestFullCacheMiss);

    // ============================================================
    // DATA SOURCE COUNTERS
    // ============================================================

    /// <summary>Total number of gap-range fetches issued to the data source.</summary>
    public int DataSourceFetchGap => Volatile.Read(ref _dataSourceFetchGap);

    // ============================================================
    // BACKGROUND PROCESSING COUNTERS
    // ============================================================

    /// <summary>Number of normalization requests received and started processing.</summary>
    public int NormalizationRequestReceived => Volatile.Read(ref _normalizationRequestReceived);

    /// <summary>Number of normalization requests that completed all four processing steps.</summary>
    public int NormalizationRequestProcessed => Volatile.Read(ref _normalizationRequestProcessed);

    /// <summary>Number of statistics-update steps executed (Background Path step 1).</summary>
    public int BackgroundStatisticsUpdated => Volatile.Read(ref _backgroundStatisticsUpdated);

    /// <summary>Number of segments stored in the cache (Background Path step 2).</summary>
    public int BackgroundSegmentStored => Volatile.Read(ref _backgroundSegmentStored);

    // ============================================================
    // EVICTION COUNTERS
    // ============================================================

    /// <summary>Number of eviction evaluation passes (Background Path step 3).</summary>
    public int EvictionEvaluated => Volatile.Read(ref _evictionEvaluated);

    /// <summary>Number of times eviction was triggered (at least one evaluator fired).</summary>
    public int EvictionTriggered => Volatile.Read(ref _evictionTriggered);

    /// <summary>Number of eviction execution passes (Background Path step 4).</summary>
    public int EvictionExecuted => Volatile.Read(ref _evictionExecuted);

    /// <summary>Total number of segments removed during eviction.</summary>
    public int EvictionSegmentRemoved => Volatile.Read(ref _evictionSegmentRemoved);

    // ============================================================
    // ERROR COUNTERS
    // ============================================================

    /// <summary>Number of background operations that failed with an unhandled exception.</summary>
    public int BackgroundOperationFailed => Volatile.Read(ref _backgroundOperationFailed);

    // ============================================================
    // RESET
    // ============================================================

    /// <summary>
    /// Resets all counters to zero. Useful for test isolation when a single cache instance
    /// is reused across multiple logical scenarios.
    /// </summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _userRequestServed, 0);
        Interlocked.Exchange(ref _userRequestFullCacheHit, 0);
        Interlocked.Exchange(ref _userRequestPartialCacheHit, 0);
        Interlocked.Exchange(ref _userRequestFullCacheMiss, 0);
        Interlocked.Exchange(ref _dataSourceFetchGap, 0);
        Interlocked.Exchange(ref _normalizationRequestReceived, 0);
        Interlocked.Exchange(ref _normalizationRequestProcessed, 0);
        Interlocked.Exchange(ref _backgroundStatisticsUpdated, 0);
        Interlocked.Exchange(ref _backgroundSegmentStored, 0);
        Interlocked.Exchange(ref _evictionEvaluated, 0);
        Interlocked.Exchange(ref _evictionTriggered, 0);
        Interlocked.Exchange(ref _evictionExecuted, 0);
        Interlocked.Exchange(ref _evictionSegmentRemoved, 0);
        Interlocked.Exchange(ref _backgroundOperationFailed, 0);
    }

    // ============================================================
    // IVisitedPlacesCacheDiagnostics IMPLEMENTATION (explicit to avoid name clash with counter properties)
    // ============================================================

    /// <inheritdoc/>
    void ICacheDiagnostics.UserRequestServed() => Interlocked.Increment(ref _userRequestServed);

    /// <inheritdoc/>
    void ICacheDiagnostics.UserRequestFullCacheHit() => Interlocked.Increment(ref _userRequestFullCacheHit);

    /// <inheritdoc/>
    void ICacheDiagnostics.UserRequestPartialCacheHit() => Interlocked.Increment(ref _userRequestPartialCacheHit);

    /// <inheritdoc/>
    void ICacheDiagnostics.UserRequestFullCacheMiss() => Interlocked.Increment(ref _userRequestFullCacheMiss);

    /// <inheritdoc/>
    void ICacheDiagnostics.BackgroundOperationFailed(Exception ex) =>
        Interlocked.Increment(ref _backgroundOperationFailed);

    /// <inheritdoc/>
    void IVisitedPlacesCacheDiagnostics.DataSourceFetchGap() => Interlocked.Increment(ref _dataSourceFetchGap);

    /// <inheritdoc/>
    void IVisitedPlacesCacheDiagnostics.NormalizationRequestReceived() => Interlocked.Increment(ref _normalizationRequestReceived);

    /// <inheritdoc/>
    void IVisitedPlacesCacheDiagnostics.NormalizationRequestProcessed() => Interlocked.Increment(ref _normalizationRequestProcessed);

    /// <inheritdoc/>
    void IVisitedPlacesCacheDiagnostics.BackgroundStatisticsUpdated() => Interlocked.Increment(ref _backgroundStatisticsUpdated);

    /// <inheritdoc/>
    void IVisitedPlacesCacheDiagnostics.BackgroundSegmentStored() => Interlocked.Increment(ref _backgroundSegmentStored);

    /// <inheritdoc/>
    void IVisitedPlacesCacheDiagnostics.EvictionEvaluated() => Interlocked.Increment(ref _evictionEvaluated);

    /// <inheritdoc/>
    void IVisitedPlacesCacheDiagnostics.EvictionTriggered() => Interlocked.Increment(ref _evictionTriggered);

    /// <inheritdoc/>
    void IVisitedPlacesCacheDiagnostics.EvictionExecuted() => Interlocked.Increment(ref _evictionExecuted);

    /// <inheritdoc/>
    void IVisitedPlacesCacheDiagnostics.EvictionSegmentRemoved() => Interlocked.Increment(ref _evictionSegmentRemoved);
}
