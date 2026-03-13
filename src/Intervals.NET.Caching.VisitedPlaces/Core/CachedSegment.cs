using Intervals.NET.Caching.VisitedPlaces.Core.Eviction;
using Intervals.NET.Caching.VisitedPlaces.Infrastructure.Storage;

namespace Intervals.NET.Caching.VisitedPlaces.Core;

/// <summary>
/// Represents a single contiguous cached segment: a range, its data, and optional selector-owned eviction metadata.
/// </summary>
/// <typeparam name="TRange">The range boundary type. Must implement <see cref="IComparable{T}"/>.</typeparam>
/// <typeparam name="TData">The type of cached data.</typeparam>
/// <remarks>
/// <para><strong>Invariant VPC.C.3:</strong> Overlapping segments are not permitted.
/// Each point in the domain is cached in at most one segment.</para>
/// <para><strong>Invariant VPC.C.2:</strong> Segments are never merged, even if adjacent.</para>
/// </remarks>
public sealed class CachedSegment<TRange, TData>
    where TRange : IComparable<TRange>
{
    /// <summary>The range covered by this segment.</summary>
    public Range<TRange> Range { get; }

    /// <summary>The data stored for this segment.</summary>
    public ReadOnlyMemory<TData> Data { get; }

    /// <summary>
    /// Optional selector-owned eviction metadata. Set and interpreted exclusively by the
    /// configured <see cref="IEvictionSelector{TRange,TData}"/>. <see langword="null"/> when
    /// the selector requires no metadata (e.g., <c>SmallestFirstEvictionSelector</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The selector initializes this field via <c>InitializeMetadata</c> when the segment
    /// is stored and updates it via <c>UpdateMetadata</c> when the segment is used.
    /// If a selector encounters a metadata object from a different selector type, it replaces
    /// it with its own (lazy initialization pattern).
    /// </para>
    /// <para><strong>Thread safety:</strong> Only mutated by the Background Path (single writer).</para>
    /// </remarks>
    public IEvictionMetadata? EvictionMetadata { get; internal set; }

    // Removal state: 0 = live, 1 = removed.
    // Accessed atomically via Interlocked.CompareExchange (TryMarkAsRemoved) and Volatile.Read (IsRemoved).
    private int _isRemoved;

    /// <summary>
    /// Indicates whether this segment has been logically removed from the cache.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This flag is <strong>monotonic</strong>: once set to <see langword="true"/> by
    /// <see cref="TryMarkAsRemoved"/> it is never reset to <see langword="false"/>.
    /// It lives on the segment object itself, so it survives storage compaction
    /// (normalization passes that rebuild the snapshot / stride index).
    /// </para>
    /// <para>
    /// Storage implementations use this flag as the primary soft-delete filter:
    /// <see cref="ISegmentStorage{TRange,TData}.FindIntersecting"/> and
    /// <c>TryGetRandomSegment</c> check <see cref="IsRemoved"/> instead of consulting a
    /// separate <c>_softDeleted</c> collection, which eliminates any shared mutable
    /// collection between the Background Path and the TTL thread.
    /// </para>
    /// <para><strong>Thread safety:</strong> Read via <c>Volatile.Read</c> (acquire fence).
    /// Written atomically by <see cref="TryMarkAsRemoved"/> via
    /// <c>Interlocked.CompareExchange</c>.</para>
    /// </remarks>
    internal bool IsRemoved => Volatile.Read(ref _isRemoved) != 0;

    /// <summary>
    /// Attempts to transition this segment from live to removed.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if this call performed the transition (segment was live);
    /// <see langword="false"/> if the segment was already removed (idempotent no-op).
    /// </returns>
    /// <remarks>
    /// <para>
    /// Uses <c>Interlocked.CompareExchange</c> to guarantee that exactly one caller
    /// wins the transition even when called concurrently from the Background Path
    /// (eviction) and the TTL thread. The winning caller is responsible for
    /// decrementing any reference counts or aggregates; losing callers are no-ops.
    /// </para>
    /// <para>
    /// This method is called by storage implementations inside
    /// <see cref="ISegmentStorage{TRange,TData}.TryRemove"/> — callers do not set the flag
    /// directly. This centralises the one-way transition logic and makes the contract
    /// explicit.
    /// </para>
    /// </remarks>
    internal bool TryMarkAsRemoved() =>
        Interlocked.CompareExchange(ref _isRemoved, 1, 0) == 0;

    /// <summary>
    /// Initializes a new <see cref="CachedSegment{TRange,TData}"/>.
    /// </summary>
    /// <param name="range">The range this segment covers.</param>
    /// <param name="data">The cached data for this range.</param>
    internal CachedSegment(Range<TRange> range, ReadOnlyMemory<TData> data)
    {
        Range = range;
        Data = data;
    }
}
