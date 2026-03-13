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
/// <see cref="CachedSegment{TRange,TData}.EvictionMetadata"/>. Metadata is initialized at
/// segment creation time via <see cref="InitializeMetadata"/>. If a segment's metadata is
/// missing or belongs to a different selector when first sampled, <see cref="EnsureMetadata"/>
/// lazily attaches a new <see cref="LruMetadata"/> using the current timestamp — the segment
/// is treated as if it was just accessed.</para>
/// <para><strong>Time source:</strong> All timestamps are obtained from the injected
/// <see cref="TimeProvider"/> (defaults to <see cref="TimeProvider.System"/>), enabling
/// deterministic testing.</para>
/// <para><strong>Performance:</strong> O(SampleSize) per candidate selection; no sorting,
/// no collection copying. SampleSize defaults to
/// <see cref="EvictionSamplingOptions.DefaultSampleSize"/> (32).</para>
/// </remarks>
/// <summary>
/// Non-generic factory companion for <see cref="LruEvictionSelector{TRange,TData}"/>.
/// Enables type inference at the call site: <c>LruEvictionSelector.Create&lt;int, MyData&gt;()</c>.
/// </summary>
public static class LruEvictionSelector
{
    /// <summary>
    /// Creates a new <see cref="LruEvictionSelector{TRange,TData}"/>.
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
    /// <returns>A new <see cref="LruEvictionSelector{TRange,TData}"/> instance.</returns>
    public static LruEvictionSelector<TRange, TData> Create<TRange, TData>(
        EvictionSamplingOptions? samplingOptions = null,
        TimeProvider? timeProvider = null)
        where TRange : IComparable<TRange>
        => new(samplingOptions, timeProvider);
}

/// <inheritdoc cref="LruEvictionSelector{TRange,TData}"/>
public sealed class LruEvictionSelector<TRange, TData> : SamplingEvictionSelector<TRange, TData>
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
    /// <param name="timeProvider">
    /// Optional time provider used to obtain the current UTC timestamp for metadata creation
    /// and updates. When <see langword="null"/>, <see cref="TimeProvider.System"/> is used.
    /// </param>
    public LruEvictionSelector(
        EvictionSamplingOptions? samplingOptions = null,
        TimeProvider? timeProvider = null)
        : base(samplingOptions, timeProvider)
    {
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <paramref name="candidate"/> is worse than <paramref name="current"/> when it was
    /// accessed less recently — i.e., its <see cref="LruMetadata.LastAccessedAt"/> is older.
    /// Both segments are guaranteed to carry valid <see cref="LruMetadata"/> when this method
    /// is called (<see cref="EnsureMetadata"/> has already been invoked on both).
    /// </remarks>
    protected override bool IsWorse(
        CachedSegment<TRange, TData> candidate,
        CachedSegment<TRange, TData> current)
    {
        var candidateTime = ((LruMetadata)candidate.EvictionMetadata!).LastAccessedAt;
        var currentTime = ((LruMetadata)current.EvictionMetadata!).LastAccessedAt;

        return candidateTime < currentTime;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// If the segment does not carry a <see cref="LruMetadata"/> instance, attaches a new one
    /// with <c>LastAccessedAt</c> set to the current UTC time from <see cref="TimeProvider"/>.
    /// This handles segments that were stored before this selector was active or whose metadata
    /// was cleared.
    /// </remarks>
    protected override void EnsureMetadata(CachedSegment<TRange, TData> segment)
    {
        if (segment.EvictionMetadata is not LruMetadata)
        {
            segment.EvictionMetadata = new LruMetadata(TimeProvider.GetUtcNow().UtcDateTime);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Creates a <see cref="LruMetadata"/> instance with <c>LastAccessedAt</c> set to the
    /// current UTC time from <see cref="TimeProvider"/> and attaches it to the segment.
    /// </remarks>
    public override void InitializeMetadata(CachedSegment<TRange, TData> segment)
    {
        segment.EvictionMetadata = new LruMetadata(TimeProvider.GetUtcNow().UtcDateTime);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Sets <c>LastAccessedAt</c> to the current UTC time from <see cref="TimeProvider"/>
    /// on each used segment's <see cref="LruMetadata"/>.
    /// If a segment's metadata is <see langword="null"/> or belongs to a different selector,
    /// it is replaced with a new <see cref="LruMetadata"/> (lazy initialization).
    /// </remarks>
    public override void UpdateMetadata(IReadOnlyList<CachedSegment<TRange, TData>> usedSegments)
    {
        var now = TimeProvider.GetUtcNow().UtcDateTime;

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
