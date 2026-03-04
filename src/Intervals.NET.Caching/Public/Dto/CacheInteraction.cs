namespace Intervals.NET.Caching.Public.Dto;

/// <summary>
/// Describes how a data request was fulfilled relative to the current cache state.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CacheInteraction"/> is reported on every <see cref="RangeResult{TRange,TData}"/> returned
/// by <see cref="IWindowCache{TRange,TData,TDomain}.GetDataAsync"/>. It tells the caller whether the
/// requested range was served entirely from the cache, assembled from a mix of cached and live
/// data-source data, or fetched entirely from the data source with no cache participation.
/// </para>
/// <para><strong>Relationship to consistency modes:</strong></para>
/// <para>
/// The value is the foundation for the opt-in hybrid consistency extension method
/// <c>GetDataAndWaitOnMissAsync</c>: that method awaits background rebalance completion only when the
/// interaction is <see cref="PartialHit"/> or <see cref="FullMiss"/>, ensuring the cache is warm around
/// the new position before returning. A <see cref="FullHit"/> returns immediately (eventual consistency).
/// </para>
/// <para><strong>Diagnostics relationship:</strong></para>
/// <para>
/// The same classification is reported through the optional <c>ICacheDiagnostics</c> callbacks
/// (<c>UserRequestFullCacheHit</c>, <c>UserRequestPartialCacheHit</c>, <c>UserRequestFullCacheMiss</c>).
/// <see cref="CacheInteraction"/> provides per-request, programmatic access to the same information
/// without requiring a diagnostics implementation.
/// </para>
/// </remarks>
public enum CacheInteraction
{
    /// <summary>
    /// The requested range was fully contained within the current cache. No <c>IDataSource</c> call was
    /// made on the user path. This is the fastest path and indicates the cache is well-positioned
    /// relative to the current access pattern.
    /// </summary>
    FullHit,

    /// <summary>
    /// The requested range partially overlapped the current cache. The cached portion was served from
    /// memory; the missing segments were fetched from <c>IDataSource</c> on the user path.
    /// Background rebalance has been triggered to expand the cache around the new position.
    /// </summary>
    PartialHit,

    /// <summary>
    /// The requested range had no overlap with the current cache, or the cache was uninitialized
    /// (cold start). The entire range was fetched directly from <c>IDataSource</c> on the user path.
    /// Background rebalance has been triggered to build or replace the cache around the new position.
    /// </summary>
    FullMiss,
}
