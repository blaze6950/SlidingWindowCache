namespace Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Selectors;

/// <summary>
/// An <see cref="IEvictionSelector{TRange,TData}"/> that orders eviction candidates using
/// the First In, First Out (FIFO) strategy.
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <remarks>
/// <para><strong>Strategy:</strong> Orders candidates ascending by
/// <see cref="FifoMetadata.CreatedAt"/> — the oldest segment is first (highest eviction priority).</para>
/// <para><strong>Execution Context:</strong> Background Path (single writer thread)</para>
/// <para>
/// FIFO treats the cache as a fixed-size sliding window over time. It does not reflect access
/// patterns and is most appropriate for workloads where all segments have similar
/// re-access probability.
/// </para>
/// <para><strong>Metadata:</strong> Uses <see cref="FifoMetadata"/> stored on
/// <see cref="CachedSegment{TRange,TData}.EvictionMetadata"/>. <c>CreatedAt</c> is set at
/// initialization and never updated — FIFO ignores subsequent access patterns.</para>
/// </remarks>
internal sealed class FifoEvictionSelector<TRange, TData> : IEvictionSelector<TRange, TData>
    where TRange : IComparable<TRange>
{
    /// <summary>
    /// Selector-specific metadata for <see cref="FifoEvictionSelector{TRange,TData}"/>.
    /// Records when the segment was first stored in the cache.
    /// </summary>
    internal sealed class FifoMetadata : IEvictionMetadata
    {
        /// <summary>
        /// The UTC timestamp at which the segment was added to the cache.
        /// Immutable — FIFO ordering is determined solely by insertion time.
        /// </summary>
        public DateTime CreatedAt { get; }

        /// <summary>
        /// Initializes a new <see cref="FifoMetadata"/> with the given creation timestamp.
        /// </summary>
        /// <param name="createdAt">The UTC timestamp at which the segment was stored.</param>
        public FifoMetadata(DateTime createdAt)
        {
            CreatedAt = createdAt;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Creates a <see cref="FifoMetadata"/> instance with <c>CreatedAt = now</c>
    /// and attaches it to the segment.
    /// </remarks>
    public void InitializeMetadata(CachedSegment<TRange, TData> segment, DateTime now)
    {
        segment.EvictionMetadata = new FifoMetadata(now);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// No-op for FIFO. <see cref="FifoMetadata.CreatedAt"/> is immutable — access patterns
    /// do not affect FIFO ordering.
    /// </remarks>
    public void UpdateMetadata(IReadOnlyList<CachedSegment<TRange, TData>> usedSegments, DateTime now)
    {
        // FIFO metadata is immutable after creation — nothing to update.
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Sorts candidates ascending by <see cref="FifoMetadata.CreatedAt"/>.
    /// The oldest segment is first in the returned list.
    /// If a segment has no <see cref="FifoMetadata"/> (e.g., metadata was never initialized),
    /// it defaults to <see cref="DateTime.MinValue"/> and is treated as the highest eviction priority.
    /// </remarks>
    public IReadOnlyList<CachedSegment<TRange, TData>> OrderCandidates(
        IReadOnlyList<CachedSegment<TRange, TData>> candidates)
    {
        return candidates
            .OrderBy(s => s.EvictionMetadata is FifoMetadata meta ? meta.CreatedAt : DateTime.MinValue)
            .ToList();
    }
}
