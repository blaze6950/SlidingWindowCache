using Intervals.NET.Caching.Dto;
using Intervals.NET.Caching.Infrastructure.Scheduling;

namespace Intervals.NET.Caching.VisitedPlaces.Core;

/// <summary>
/// Represents a unit of work published to the Background Storage Loop after a user request completes.
/// See docs/visited-places/ for design details.
/// </summary>
internal sealed class CacheNormalizationRequest<TRange, TData> : ISchedulableWorkItem
    where TRange : IComparable<TRange>
{
    /// <summary>The original range requested by the user on the User Path.</summary>
    public Range<TRange> RequestedRange { get; }

    /// <summary>
    /// Segments that were served from the cache on the User Path.
    /// Empty when the request was a full miss (no cache hit at all).
    /// Used by the executor to update statistics in Background Path step 1.
    /// </summary>
    public IReadOnlyList<CachedSegment<TRange, TData>> UsedSegments { get; }

    /// <summary>
    /// Data freshly fetched from <c>IDataSource</c> to fill gaps in the cache.
    /// <see langword="null"/> when the request was a full cache hit (no data source call needed).
    /// Always a materialized collection — data is captured on the User Path before crossing
    /// the thread boundary to the Background Storage Loop.
    /// </summary>
    public IReadOnlyList<RangeChunk<TRange, TData>>? FetchedChunks { get; }

    internal CacheNormalizationRequest(
        Range<TRange> requestedRange,
        IReadOnlyList<CachedSegment<TRange, TData>> usedSegments,
        IReadOnlyList<RangeChunk<TRange, TData>>? fetchedChunks)
    {
        RequestedRange = requestedRange;
        UsedSegments = usedSegments;
        FetchedChunks = fetchedChunks;
    }

    /// <inheritdoc/>
    public CancellationToken CancellationToken => CancellationToken.None;

    /// <inheritdoc/>
    public void Cancel() { }

    /// <inheritdoc/>
    public void Dispose() { }
}
