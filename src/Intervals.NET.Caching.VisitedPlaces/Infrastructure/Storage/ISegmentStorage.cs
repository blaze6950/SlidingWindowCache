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
/// <item><description><see cref="Add"/>, <see cref="TryRemove"/>, <see cref="TryGetRandomSegment"/> — Background Path only (single writer)</description></item>
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
    /// <returns>
    /// <see langword="true"/> if this call was the first to remove the segment
    /// (i.e., <see cref="CachedSegment{TRange,TData}.TryMarkAsRemoved"/> returned <see langword="true"/>
    /// for this call); <see langword="false"/> if the segment was already removed by a concurrent
    /// caller (idempotent no-op).
    /// </returns>
    /// <remarks>
    /// <para><strong>Execution Context:</strong> Background Path (single writer) or TTL</para>
    /// <para>Implementations may use soft-delete internally; the segment
    /// becomes immediately invisible to all read operations after this call.</para>
    /// <para>The call is idempotent. Safe to call several times.</para>
    /// </remarks>
    bool TryRemove(CachedSegment<TRange, TData> segment);

    /// <summary>
    /// Returns a single randomly-selected live (non-removed) segment from storage.
    /// </summary>
    /// <returns>
    /// A live segment chosen uniformly at random, or <see langword="null"/> when the storage
    /// is empty or all candidates within the retry budget were soft-deleted.
    /// </returns>
    /// <remarks>
    /// <para><strong>Execution Context:</strong> Background Path only (single writer)</para>
    /// <para>
    /// Implementations use a bounded retry loop to skip over soft-deleted segments.
    /// If the retry budget is exhausted before finding a live segment, <see langword="null"/>
    /// is returned. Callers (eviction selectors) are responsible for handling this by treating
    /// it as "pool exhausted" for one sample slot.
    /// </para>
    /// <para>
    /// The <see cref="System.Random"/> instance used for index selection is owned privately
    /// by each implementation — no synchronization is required since this method is
    /// Background-Path-only.
    /// </para>
    /// </remarks>
    CachedSegment<TRange, TData>? TryGetRandomSegment();
}
