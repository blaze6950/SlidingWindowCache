using Intervals.NET.Caching.VisitedPlaces.Public.Configuration;

namespace Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Selectors;

/// <summary>
/// An <see cref="IEvictionSelector{TRange,TData}"/> that selects eviction candidates using
/// the Least Recently Used (LRU) strategy.
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <remarks>
/// <para><strong>Strategy:</strong> Among a random sample of segments, selects the one with
/// the oldest <see cref="LruMetadata.LastAccessedAt"/> — the least recently accessed segment
/// is the worst eviction candidate.</para>
/// <para><strong>Execution Context:</strong> Background Path (single writer thread)</para>
/// <para><strong>Metadata:</strong> Uses <see cref="LruMetadata"/> stored on
/// <see cref="CachedSegment{TRange,TData}.EvictionMetadata"/>. If a segment's metadata
/// is missing or belongs to a different selector, it is lazily initialized with the segment's
/// creation time as the initial <c>LastAccessedAt</c>.</para>
/// <para><strong>Performance:</strong> O(SampleSize) per candidate selection; no sorting,
/// no collection copying. SampleSize defaults to
/// <see cref="EvictionSamplingOptions.DefaultSampleSize"/> (32).</para>
/// </remarks>
internal sealed class LruEvictionSelector<TRange, TData> : SamplingEvictionSelector<TRange, TData>
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

    /// <summary>
    /// Initializes a new <see cref="LruEvictionSelector{TRange,TData}"/>.
    /// </summary>
    /// <param name="samplingOptions">
    /// Optional sampling configuration. When <see langword="null"/>,
    /// <see cref="EvictionSamplingOptions.Default"/> is used (SampleSize = 32).
    /// </param>
    public LruEvictionSelector(EvictionSamplingOptions? samplingOptions = null)
        : base(samplingOptions)
    {
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <paramref name="candidate"/> is worse than <paramref name="current"/> when it was
    /// accessed less recently — i.e., its <see cref="LruMetadata.LastAccessedAt"/> is older.
    /// Segments with no <see cref="LruMetadata"/> (metadata null or wrong type) are treated
    /// as having <see cref="DateTime.MinValue"/> access time and are therefore always the
    /// worst candidate.
    /// </remarks>
    protected override bool IsWorse(
        CachedSegment<TRange, TData> candidate,
        CachedSegment<TRange, TData> current)
    {
        var candidateTime = candidate.EvictionMetadata is LruMetadata cm
            ? cm.LastAccessedAt
            : DateTime.MinValue;

        var currentTime = current.EvictionMetadata is LruMetadata curm
            ? curm.LastAccessedAt
            : DateTime.MinValue;

        return candidateTime < currentTime;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Creates a <see cref="LruMetadata"/> instance with <c>LastAccessedAt = now</c>
    /// and attaches it to the segment.
    /// </remarks>
    public override void InitializeMetadata(CachedSegment<TRange, TData> segment, DateTime now)
    {
        segment.EvictionMetadata = new LruMetadata(now);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Sets <c>LastAccessedAt = now</c> on each used segment's <see cref="LruMetadata"/>.
    /// If a segment's metadata is null or belongs to a different selector, it is replaced
    /// with a new <see cref="LruMetadata"/> (lazy initialization).
    /// </remarks>
    public override void UpdateMetadata(
        IReadOnlyList<CachedSegment<TRange, TData>> usedSegments,
        DateTime now)
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
}
