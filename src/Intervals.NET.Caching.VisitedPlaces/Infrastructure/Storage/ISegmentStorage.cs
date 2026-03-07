using Intervals.NET.Caching.VisitedPlaces.Core;

namespace Intervals.NET.Caching.VisitedPlaces.Infrastructure.Storage;

/// <summary>
/// Defines the internal storage contract for the non-contiguous segment collection
/// used by <c>VisitedPlacesCache</c>.
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <remarks>
/// <para><strong>Threading Model:</strong></para>
/// <list type="bullet">
/// <item><description><see cref="FindIntersecting"/> — User Path; concurrent reads are safe</description></item>
/// <item><description><see cref="Add"/>, <see cref="Remove"/>, <see cref="GetAllSegments"/> — Background Path only (single writer)</description></item>
/// </list>
/// <para><strong>RCU Semantics (Invariant VPC.B.5):</strong>
/// User Path reads operate on a stable snapshot published via <c>Volatile.Write</c>.
/// No intermediate (partially-updated) state is ever visible to User Path threads.</para>
/// <para><strong>Non-Contiguity (Invariant VPC.C.1):</strong>
/// Gaps between segments are permitted. Segments are never merged.</para>
/// <para><strong>No-Overlap (Invariant VPC.C.3):</strong>
/// Overlapping segments are not permitted; this is the caller's responsibility.</para>
/// </remarks>
internal interface ISegmentStorage<TRange, TData>
    where TRange : IComparable<TRange>
{
    /// <summary>
    /// Returns the current number of segments in the storage.
    /// </summary>
    /// <remarks>
    /// Called by eviction evaluators on the Background Path.
    /// </remarks>
    int Count { get; }

    /// <summary>
    /// Returns all segments whose ranges intersect <paramref name="range"/>.
    /// </summary>
    /// <param name="range">The range to search for intersecting segments.</param>
    /// <returns>
    /// A list of segments whose ranges intersect <paramref name="range"/>.
    /// May be empty if no segments intersect.
    /// </returns>
    /// <remarks>
    /// <para><strong>Execution Context:</strong> User Path (read-only, concurrent)</para>
    /// <para>Soft-deleted segments are excluded from results.</para>
    /// </remarks>
    IReadOnlyList<CachedSegment<TRange, TData>> FindIntersecting(Range<TRange> range);

    /// <summary>
    /// Adds a new segment to the storage.
    /// </summary>
    /// <param name="segment">The segment to add.</param>
    /// <remarks>
    /// <para><strong>Execution Context:</strong> Background Path (single writer)</para>
    /// </remarks>
    void Add(CachedSegment<TRange, TData> segment);

    /// <summary>
    /// Removes a segment from the storage.
    /// </summary>
    /// <param name="segment">The segment to remove.</param>
    /// <remarks>
    /// <para><strong>Execution Context:</strong> Background Path (single writer)</para>
    /// <para>Implementations may use soft-delete internally; the segment
    /// becomes immediately invisible to <see cref="FindIntersecting"/> after this call.</para>
    /// </remarks>
    void Remove(CachedSegment<TRange, TData> segment);

    /// <summary>
    /// Returns all currently stored (non-deleted) segments.
    /// </summary>
    /// <returns>A snapshot of all live segments.</returns>
    /// <remarks>
    /// <para><strong>Execution Context:</strong> Background Path only (single writer)</para>
    /// <para>Used by eviction executors and evaluators.</para>
    /// </remarks>
    IReadOnlyList<CachedSegment<TRange, TData>> GetAllSegments();
}
