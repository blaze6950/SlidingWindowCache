using Intervals.NET.Caching.VisitedPlaces.Public.Configuration;

namespace Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Selectors;

/// <summary>
/// An <see cref="IEvictionSelector{TRange,TData}"/> that selects eviction candidates using
/// the First In, First Out (FIFO) strategy.
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <remarks>
/// <para><strong>Strategy:</strong> Among a random sample of segments, selects the one with
/// the oldest <see cref="FifoMetadata.CreatedAt"/> — the segment that was stored earliest
/// is the worst eviction candidate.</para>
/// <para><strong>Execution Context:</strong> Background Path (single writer thread)</para>
/// <para>
/// FIFO treats the cache as a fixed-size sliding window over time. It does not reflect access
/// patterns and is most appropriate for workloads where all segments have similar
/// re-access probability.
/// </para>
/// <para><strong>Metadata:</strong> Uses <see cref="FifoMetadata"/> stored on
/// <see cref="CachedSegment{TRange,TData}.EvictionMetadata"/>. <c>CreatedAt</c> is set at
/// initialization and never updated — FIFO ignores subsequent access patterns.</para>
/// <para><strong>Performance:</strong> O(SampleSize) per candidate selection; no sorting,
/// no collection copying. SampleSize defaults to
/// <see cref="EvictionSamplingOptions.DefaultSampleSize"/> (32).</para>
/// </remarks>
internal sealed class FifoEvictionSelector<TRange, TData> : SamplingEvictionSelector<TRange, TData>
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

    /// <summary>
    /// Initializes a new <see cref="FifoEvictionSelector{TRange,TData}"/>.
    /// </summary>
    /// <param name="samplingOptions">
    /// Optional sampling configuration. When <see langword="null"/>,
    /// <see cref="EvictionSamplingOptions.Default"/> is used (SampleSize = 32).
    /// </param>
    public FifoEvictionSelector(EvictionSamplingOptions? samplingOptions = null)
        : base(samplingOptions)
    {
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <paramref name="candidate"/> is worse than <paramref name="current"/> when it was
    /// stored earlier — i.e., its <see cref="FifoMetadata.CreatedAt"/> is older.
    /// Segments with no <see cref="FifoMetadata"/> (metadata null or wrong type) are treated
    /// as having <see cref="DateTime.MinValue"/> creation time and are therefore always the
    /// worst candidate.
    /// </remarks>
    protected override bool IsWorse(
        CachedSegment<TRange, TData> candidate,
        CachedSegment<TRange, TData> current)
    {
        var candidateTime = candidate.EvictionMetadata is FifoMetadata cm
            ? cm.CreatedAt
            : DateTime.MinValue;

        var currentTime = current.EvictionMetadata is FifoMetadata curm
            ? curm.CreatedAt
            : DateTime.MinValue;

        return candidateTime < currentTime;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Creates a <see cref="FifoMetadata"/> instance with <c>CreatedAt = now</c>
    /// and attaches it to the segment.
    /// </remarks>
    public override void InitializeMetadata(CachedSegment<TRange, TData> segment, DateTime now)
    {
        segment.EvictionMetadata = new FifoMetadata(now);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// No-op for FIFO. <see cref="FifoMetadata.CreatedAt"/> is immutable — access patterns
    /// do not affect FIFO ordering.
    /// </remarks>
    public override void UpdateMetadata(
        IReadOnlyList<CachedSegment<TRange, TData>> usedSegments,
        DateTime now)
    {
        // FIFO metadata is immutable after creation — nothing to update.
    }
}
