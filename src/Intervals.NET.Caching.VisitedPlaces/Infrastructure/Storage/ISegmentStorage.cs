using Intervals.NET.Caching.VisitedPlaces.Core;

namespace Intervals.NET.Caching.VisitedPlaces.Infrastructure.Storage;

/// <summary>
/// Internal storage contract for the non-contiguous segment collection.
/// See docs/visited-places/ for design details.
/// </summary>
internal interface ISegmentStorage<TRange, TData>
    where TRange : IComparable<TRange>
{
    /// <summary>
    /// Returns the current number of live segments in the storage.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Returns all non-removed segments whose ranges intersect <paramref name="range"/>.
    /// </summary>
    IReadOnlyList<CachedSegment<TRange, TData>> FindIntersecting(Range<TRange> range);

    /// <summary>
    /// Adds a new segment to the storage (Background Path only).
    /// </summary>
    void Add(CachedSegment<TRange, TData> segment);

    /// <summary>
    /// Adds multiple pre-validated, pre-sorted segments to the storage in a single bulk operation
    /// (Background Path only). Reduces normalization overhead from O(count/bufferSize) normalizations
    /// to a single pass — beneficial when a multi-gap partial-hit request produces many new segments.
    /// </summary>
    /// <remarks>
    /// The caller is responsible for ensuring all segments in <paramref name="segments"/> are
    /// non-overlapping and sorted by range start (Invariant VPC.C.3). Each segment must already
    /// have passed the overlap pre-check against current storage contents.
    /// </remarks>
    void AddRange(CachedSegment<TRange, TData>[] segments);

    /// <summary>
    /// Atomically removes a segment from the storage.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if this call was the first to remove the segment;
    /// <see langword="false"/> if already removed (idempotent).
    /// </returns>
    bool TryRemove(CachedSegment<TRange, TData> segment);

    /// <summary>
    /// Returns a single randomly-selected live segment, or <see langword="null"/> if none available.
    /// </summary>
    CachedSegment<TRange, TData>? TryGetRandomSegment();
}
