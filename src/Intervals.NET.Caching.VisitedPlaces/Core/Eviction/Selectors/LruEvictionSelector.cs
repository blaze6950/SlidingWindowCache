namespace Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Selectors;

/// <summary>
/// An <see cref="IEvictionSelector{TRange,TData}"/> that orders eviction candidates using
/// the Least Recently Used (LRU) strategy.
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <remarks>
/// <para><strong>Strategy:</strong> Orders candidates ascending by
/// <see cref="LruMetadata.LastAccessedAt"/> — the least recently accessed segment
/// is first (highest eviction priority).</para>
/// <para><strong>Execution Context:</strong> Background Path (single writer thread)</para>
/// <para><strong>Metadata:</strong> Uses <see cref="LruMetadata"/> stored on
/// <see cref="CachedSegment{TRange,TData}.EvictionMetadata"/>. If a segment's metadata
/// is missing or belongs to a different selector, it is lazily initialized with the segment's
/// creation time as the initial <c>LastAccessedAt</c>.</para>
/// </remarks>
internal sealed class LruEvictionSelector<TRange, TData> : IEvictionSelector<TRange, TData>
    where TRange : IComparable<TRange>
{
    /// <summary>
    /// Selector-specific metadata for <see cref="LruEvictionSelector{TRange,TData}"/>.
    /// Tracks the most recent access time for a cached segment.
    /// </summary>
    internal sealed class LruMetadata : IEvictionMetadata
    {
        /// <summary>
        /// The UTC timestamp of the last access to the segment on the User Path.
        /// </summary>
        public DateTime LastAccessedAt { get; set; }

        /// <summary>
        /// Initializes a new <see cref="LruMetadata"/> with the given access timestamp.
        /// </summary>
        /// <param name="lastAccessedAt">The initial last-accessed timestamp (typically the creation time).</param>
        public LruMetadata(DateTime lastAccessedAt)
        {
            LastAccessedAt = lastAccessedAt;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Creates a <see cref="LruMetadata"/> instance with <c>LastAccessedAt = now</c>
    /// and attaches it to the segment.
    /// </remarks>
    public void InitializeMetadata(CachedSegment<TRange, TData> segment, DateTime now)
    {
        segment.EvictionMetadata = new LruMetadata(now);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Sets <c>LastAccessedAt = now</c> on each used segment's <see cref="LruMetadata"/>.
    /// If a segment's metadata is null or belongs to a different selector, it is replaced
    /// with a new <see cref="LruMetadata"/> (lazy initialization).
    /// </remarks>
    public void UpdateMetadata(IReadOnlyList<CachedSegment<TRange, TData>> usedSegments, DateTime now)
    {
        foreach (var segment in usedSegments)
        {
            if (segment.EvictionMetadata is not LruMetadata meta)
            {
                meta = new LruMetadata(now);
                segment.EvictionMetadata = meta;
            }
            else
            {
                meta.LastAccessedAt = now;
            }
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Sorts candidates ascending by <see cref="LruMetadata.LastAccessedAt"/>.
    /// The segment with the oldest access time is first in the returned list.
    /// If a segment has no <see cref="LruMetadata"/> (e.g., metadata was never initialized),
    /// it defaults to <see cref="DateTime.MinValue"/> and is treated as the highest eviction priority.
    /// </remarks>
    public IReadOnlyList<CachedSegment<TRange, TData>> OrderCandidates(
        IReadOnlyList<CachedSegment<TRange, TData>> candidates)
    {
        return candidates
            .OrderBy(s => s.EvictionMetadata is LruMetadata meta ? meta.LastAccessedAt : DateTime.MinValue)
            .ToList();
    }
}
