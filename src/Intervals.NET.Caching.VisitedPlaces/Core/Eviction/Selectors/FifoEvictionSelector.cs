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
/// initialization and never updated — FIFO ignores subsequent access patterns. If a segment's
/// metadata is missing or belongs to a different selector when first sampled,
/// <see cref="EnsureMetadata"/> lazily attaches a new <see cref="FifoMetadata"/> using the
/// current timestamp — the segment is treated as if it was just created.</para>
/// <para><strong>Time source:</strong> All timestamps are obtained from the injected
/// <see cref="TimeProvider"/> (defaults to <see cref="TimeProvider.System"/>), enabling
/// deterministic testing.</para>
/// <para><strong>Performance:</strong> O(SampleSize) per candidate selection; no sorting,
/// no collection copying. SampleSize defaults to
/// <see cref="EvictionSamplingOptions.DefaultSampleSize"/> (32).</para>
/// </remarks>
/// <summary>
/// Non-generic factory companion for <see cref="FifoEvictionSelector{TRange,TData}"/>.
/// Enables type inference at the call site: <c>FifoEvictionSelector.Create&lt;int, MyData&gt;()</c>.
/// </summary>
public static class FifoEvictionSelector
{
    /// <summary>
    /// Creates a new <see cref="FifoEvictionSelector{TRange,TData}"/>.
    /// </summary>
    /// <typeparam name="TRange">The type representing range boundaries.</typeparam>
    /// <typeparam name="TData">The type of data being cached.</typeparam>
    /// <param name="samplingOptions">
    /// Optional sampling configuration. When <see langword="null"/>,
    /// <see cref="EvictionSamplingOptions.Default"/> is used (SampleSize = 32).
    /// </param>
    /// <param name="timeProvider">
    /// Optional time provider. When <see langword="null"/>, <see cref="TimeProvider.System"/> is used.
    /// </param>
    /// <returns>A new <see cref="FifoEvictionSelector{TRange,TData}"/> instance.</returns>
    public static FifoEvictionSelector<TRange, TData> Create<TRange, TData>(
        EvictionSamplingOptions? samplingOptions = null,
        TimeProvider? timeProvider = null)
        where TRange : IComparable<TRange>
        => new(samplingOptions, timeProvider);
}

/// <inheritdoc cref="FifoEvictionSelector{TRange,TData}"/>
public sealed class FifoEvictionSelector<TRange, TData> : SamplingEvictionSelector<TRange, TData>
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
    /// <param name="timeProvider">
    /// Optional time provider used to obtain the current UTC timestamp for metadata creation.
    /// When <see langword="null"/>, <see cref="TimeProvider.System"/> is used.
    /// </param>
    public FifoEvictionSelector(
        EvictionSamplingOptions? samplingOptions = null,
        TimeProvider? timeProvider = null)
        : base(samplingOptions, timeProvider)
    {
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <paramref name="candidate"/> is worse than <paramref name="current"/> when it was
    /// stored earlier — i.e., its <see cref="FifoMetadata.CreatedAt"/> is older.
    /// Both segments are guaranteed to carry valid <see cref="FifoMetadata"/> when this method
    /// is called (<see cref="EnsureMetadata"/> has already been invoked on both).
    /// </remarks>
    protected override bool IsWorse(
        CachedSegment<TRange, TData> candidate,
        CachedSegment<TRange, TData> current)
    {
        var candidateTime = ((FifoMetadata)candidate.EvictionMetadata!).CreatedAt;
        var currentTime = ((FifoMetadata)current.EvictionMetadata!).CreatedAt;

        return candidateTime < currentTime;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// If the segment does not carry a <see cref="FifoMetadata"/> instance, attaches a new one
    /// with <c>CreatedAt</c> set to the current UTC time from <see cref="TimeProvider"/>.
    /// This handles segments that were stored before this selector was active or whose metadata
    /// was cleared.
    /// </remarks>
    protected override void EnsureMetadata(CachedSegment<TRange, TData> segment)
    {
        if (segment.EvictionMetadata is not FifoMetadata)
        {
            segment.EvictionMetadata = new FifoMetadata(TimeProvider.GetUtcNow().UtcDateTime);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Creates a <see cref="FifoMetadata"/> instance with <c>CreatedAt</c> set to the
    /// current UTC time from <see cref="TimeProvider"/> and attaches it to the segment.
    /// </remarks>
    public override void InitializeMetadata(CachedSegment<TRange, TData> segment)
    {
        segment.EvictionMetadata = new FifoMetadata(TimeProvider.GetUtcNow().UtcDateTime);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// No-op for FIFO. <see cref="FifoMetadata.CreatedAt"/> is immutable — access patterns
    /// do not affect FIFO ordering.
    /// </remarks>
    public override void UpdateMetadata(IReadOnlyList<CachedSegment<TRange, TData>> usedSegments)
    {
        // FIFO metadata is immutable after creation — nothing to update.
    }
}
