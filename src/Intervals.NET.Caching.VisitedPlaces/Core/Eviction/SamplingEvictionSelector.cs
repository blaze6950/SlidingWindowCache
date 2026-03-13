using Intervals.NET.Caching.VisitedPlaces.Infrastructure.Storage;
using Intervals.NET.Caching.VisitedPlaces.Public.Configuration;

namespace Intervals.NET.Caching.VisitedPlaces.Core.Eviction;

/// <summary>
/// Abstract base class for sampling-based eviction selectors.
/// Implements the <see cref="IEvictionSelector{TRange,TData}.TrySelectCandidate"/> contract
/// using random sampling via <see cref="ISegmentStorage{TRange,TData}.TryGetRandomSegment"/>,
/// delegating only the comparison logic to derived classes.
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <remarks>
/// <para><strong>Sampling Algorithm:</strong></para>
/// <list type="number">
/// <item><description>
///   Call <see cref="ISegmentStorage{TRange,TData}.GetRandomSegment"/> up to
///   <c>SampleSize</c> times. Each call returns a single randomly-selected live segment
///   from storage (O(1) per call, bounded retries for soft-deleted entries).
/// </description></item>
/// <item><description>
///   If the returned segment is immune, skip it and continue.
///   Otherwise call <see cref="EnsureMetadata"/> to guarantee valid metadata, then compare
///   it to the current worst candidate using <see cref="IsWorse"/>.
/// </description></item>
/// <item><description>
///   After the loop, return the worst candidate found (if any non-immune segment was reached).
/// </description></item>
/// </list>
/// <para><strong>Metadata guarantee:</strong></para>
/// <para>
/// Before <see cref="IsWorse"/> is called on any segment, <see cref="EnsureMetadata"/> is
/// invoked to attach or repair selector-specific metadata. This guarantees that
/// <see cref="IsWorse"/> always receives segments with valid metadata and never needs to
/// apply fallback defaults or perform null/type checks.
/// Repaired metadata persists on the segment — future sampling passes skip the repair.
/// </para>
/// <para><strong>Storage injection:</strong></para>
/// <para>
/// The storage reference is injected post-construction via <see cref="Initialize"/>,
/// because storage is created after the selector in the composition root.
/// <see cref="TrySelectCandidate"/> requires <see cref="Initialize"/> to have been called first.
/// </para>
/// <para><strong>Execution Context:</strong> Background Path (single writer thread)</para>
/// </remarks>
public abstract class SamplingEvictionSelector<TRange, TData>
    : IEvictionSelector<TRange, TData>, IStorageAwareEvictionSelector<TRange, TData>
    where TRange : IComparable<TRange>
{
    private ISegmentStorage<TRange, TData>? _storage;

    /// <summary>
    /// The number of segments randomly examined per <see cref="TrySelectCandidate"/> call.
    /// </summary>
    protected int SampleSize { get; }

    /// <summary>
    /// Provides the current UTC time for time-aware selectors (e.g., LRU, FIFO).
    /// Time-agnostic selectors (e.g., SmallestFirst) may ignore this.
    /// </summary>
    protected TimeProvider TimeProvider { get; }

    /// <summary>
    /// Initializes a new <see cref="SamplingEvictionSelector{TRange,TData}"/>.
    /// </summary>
    /// <param name="samplingOptions">
    /// Optional sampling configuration. When <see langword="null"/>,
    /// <see cref="EvictionSamplingOptions.Default"/> is used (SampleSize = 32).
    /// </param>
    /// <param name="timeProvider">
    /// Optional time provider. When <see langword="null"/>,
    /// <see cref="TimeProvider.System"/> is used.
    /// </param>
    protected SamplingEvictionSelector(
        EvictionSamplingOptions? samplingOptions = null,
        TimeProvider? timeProvider = null)
    {
        var options = samplingOptions ?? EvictionSamplingOptions.Default;
        SampleSize = options.SampleSize;
        TimeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    void IStorageAwareEvictionSelector<TRange, TData>.Initialize(ISegmentStorage<TRange, TData> storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _storage = storage;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Calls <see cref="ISegmentStorage{TRange,TData}.TryGetRandomSegment"/> up to
    /// <see cref="SampleSize"/> times, skipping any segment that is in
    /// <paramref name="immuneSegments"/> or is soft-deleted (<see langword="null"/> return from
    /// storage), and returns the worst candidate according to <see cref="IsWorse"/>.
    /// Before each comparison, <see cref="EnsureMetadata"/> is called to guarantee the segment
    /// carries valid selector-specific metadata.
    /// Returns <see langword="false"/> when no eligible candidate is found (all segments are
    /// immune, or the pool is empty / exhausted).
    /// </remarks>
    public bool TrySelectCandidate(
        IReadOnlySet<CachedSegment<TRange, TData>> immuneSegments,
        out CachedSegment<TRange, TData> candidate)
    {
        var storage = _storage!; // initialized before first use

        CachedSegment<TRange, TData>? worst = null;

        for (var i = 0; i < SampleSize; i++)
        {
            var segment = storage.TryGetRandomSegment();

            if (segment is null)
            {
                // Storage empty or retries exhausted for this slot — skip.
                continue;
            }

            // Skip immune segments (just-stored + already selected in this eviction pass).
            if (immuneSegments.Contains(segment))
            {
                continue;
            }

            // Guarantee valid metadata before comparison so IsWorse can stay pure.
            EnsureMetadata(segment);

            if (worst is null)
            {
                worst = segment;
            }
            else
            {
                // EnsureMetadata has already been called on worst when it was first selected.
                if (IsWorse(segment, worst))
                {
                    worst = segment;
                }
            }
        }

        if (worst is null)
        {
            // All sampled segments were immune or pool exhausted — no candidate found.
            candidate = default!;
            return false;
        }

        candidate = worst;
        return true;
    }

    /// <summary>
    /// Ensures the segment carries valid selector-specific metadata before it is passed to
    /// <see cref="IsWorse"/>. If the segment's metadata is <see langword="null"/> or belongs
    /// to a different selector type, this method creates and attaches the correct metadata.
    /// </summary>
    /// <param name="segment">The segment to validate and, if necessary, repair.</param>
    /// <remarks>
    /// <para>
    /// This method is called inside the sampling loop in
    /// <see cref="TrySelectCandidate"/> before any call to <see cref="IsWorse"/>,
    /// guaranteeing that <see cref="IsWorse"/> always receives segments with correct metadata.
    /// </para>
    /// <para>
    /// Repaired metadata persists on the segment — subsequent sampling passes will find the
    /// metadata already in place and skip the repair.
    /// </para>
    /// <para>
    /// Derived selectors implement the repair using whatever context they need:
    /// time-aware selectors (LRU, FIFO) call <see cref="TimeProvider"/> to obtain the current
    /// timestamp; segment-derived selectors (SmallestFirst) compute the value from the segment
    /// itself (e.g., <c>segment.Range.Span(domain).Value</c>).
    /// </para>
    /// </remarks>
    protected abstract void EnsureMetadata(CachedSegment<TRange, TData> segment);

    /// <summary>
    /// Determines whether <paramref name="candidate"/> is a worse eviction choice than
    /// <paramref name="current"/> — i.e., whether <paramref name="candidate"/> should be
    /// preferred for eviction over <paramref name="current"/>.
    /// </summary>
    /// <param name="candidate">The newly sampled segment to evaluate.</param>
    /// <param name="current">The current worst candidate found so far.</param>
    /// <returns>
    /// <see langword="true"/> if <paramref name="candidate"/> is more eviction-worthy than
    /// <paramref name="current"/>; <see langword="false"/> otherwise.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Both <paramref name="candidate"/> and <paramref name="current"/> are guaranteed to carry
    /// valid selector-specific metadata when this method is called —
    /// <see cref="EnsureMetadata"/> has already been invoked on both segments before any
    /// comparison occurs. Implementations can safely cast
    /// <see cref="CachedSegment{TRange,TData}.EvictionMetadata"/> without null checks or
    /// type-mismatch guards.
    /// </para>
    /// <para>Derived selectors implement strategy-specific comparison:</para>
    /// <list type="bullet">
    /// <item><description>LRU: <c>candidate.LastAccessedAt &lt; current.LastAccessedAt</c></description></item>
    /// <item><description>FIFO: <c>candidate.CreatedAt &lt; current.CreatedAt</c></description></item>
    /// <item><description>SmallestFirst: <c>candidate.Span &lt; current.Span</c></description></item>
    /// </list>
    /// </remarks>
    protected abstract bool IsWorse(
        CachedSegment<TRange, TData> candidate,
        CachedSegment<TRange, TData> current);

    /// <inheritdoc/>
    public abstract void InitializeMetadata(CachedSegment<TRange, TData> segment);

    /// <inheritdoc/>
    public abstract void UpdateMetadata(IReadOnlyList<CachedSegment<TRange, TData>> usedSegments);
}
