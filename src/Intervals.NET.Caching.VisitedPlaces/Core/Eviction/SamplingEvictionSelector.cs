using Intervals.NET.Caching.VisitedPlaces.Public.Configuration;

namespace Intervals.NET.Caching.VisitedPlaces.Core.Eviction;

/// <summary>
/// Abstract base class for sampling-based eviction selectors.
/// Implements the <see cref="IEvictionSelector{TRange,TData}.TrySelectCandidate"/> contract
/// using random sampling, delegating only the comparison logic to derived classes.
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <remarks>
/// <para><strong>Sampling Algorithm:</strong></para>
/// <list type="number">
/// <item><description>
///   Clamp the sample size to <c>min(SampleSize, segments.Count)</c> so that small caches
///   are fully examined without any configuration change.
/// </description></item>
/// <item><description>
///   Iterate up to <c>SampleSize</c> times: pick a random index from the segment list.
///   If the segment at that index is immune, skip it and continue.
///   Otherwise compare it to the current worst candidate using <see cref="IsWorse"/>.
/// </description></item>
/// <item><description>
///   After the loop, return the worst candidate found (if any non-immune segment was reached).
/// </description></item>
/// </list>
/// <para><strong>Sampling with replacement:</strong></para>
/// <para>
/// The algorithm samples with replacement (the same index may be picked twice). For the
/// expected sample sizes (16–64) this is acceptable: the probability of collision is low
/// and avoiding it would require a <c>HashSet</c> allocation per selection call.
/// </para>
/// <para><strong>Execution Context:</strong> Background Path (single writer thread)</para>
/// <para><strong>Thread safety:</strong>
/// The <see cref="_random"/> instance is private to this class and only accessed on the
/// Background Path — no synchronization is required.
/// </para>
/// </remarks>
internal abstract class SamplingEvictionSelector<TRange, TData> : IEvictionSelector<TRange, TData>
    where TRange : IComparable<TRange>
{
    private readonly Random _random;

    /// <summary>
    /// The number of segments randomly examined per <see cref="TrySelectCandidate"/> call.
    /// </summary>
    protected int SampleSize { get; }

    /// <summary>
    /// Initializes a new <see cref="SamplingEvictionSelector{TRange,TData}"/>.
    /// </summary>
    /// <param name="samplingOptions">
    /// Optional sampling configuration. When <see langword="null"/>,
    /// <see cref="EvictionSamplingOptions.Default"/> is used (SampleSize = 32).
    /// </param>
    protected SamplingEvictionSelector(EvictionSamplingOptions? samplingOptions = null)
    {
        var options = samplingOptions ?? EvictionSamplingOptions.Default;
        SampleSize = options.SampleSize;
        _random = new Random();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Randomly samples up to <see cref="SampleSize"/> segments from <paramref name="segments"/>,
    /// skipping any that are in <paramref name="immuneSegments"/>, and returns the worst
    /// candidate according to <see cref="IsWorse"/>.
    /// Returns <see langword="false"/> when no eligible candidate is found (all segments are
    /// immune, or the pool is empty).
    /// </remarks>
    public bool TrySelectCandidate(
        IReadOnlyList<CachedSegment<TRange, TData>> segments,
        IReadOnlySet<CachedSegment<TRange, TData>> immuneSegments,
        out CachedSegment<TRange, TData> candidate)
    {
        var count = segments.Count;
        if (count == 0)
        {
            candidate = default!;
            return false;
        }

        CachedSegment<TRange, TData>? worst = null;

        // Perform up to SampleSize random index picks.
        // The loop count is not clamped to count — for small pools (count < SampleSize)
        // we still do SampleSize iterations (with replacement), which naturally degrades
        // to examining the same segments multiple times without any special-casing.
        for (var i = 0; i < SampleSize; i++)
        {
            var index = _random.Next(count);
            var segment = segments[index];

            // Skip immune segments (just-stored + already selected in this eviction pass).
            if (immuneSegments.Contains(segment))
            {
                continue;
            }

            if (worst is null || IsWorse(segment, worst))
            {
                worst = segment;
            }
        }

        if (worst is null)
        {
            // All sampled segments were immune — no candidate found.
            candidate = default!;
            return false;
        }

        candidate = worst;
        return true;
    }

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
    /// Derived selectors implement strategy-specific comparison:
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
    public abstract void InitializeMetadata(CachedSegment<TRange, TData> segment, DateTime now);

    /// <inheritdoc/>
    public abstract void UpdateMetadata(
        IReadOnlyList<CachedSegment<TRange, TData>> usedSegments,
        DateTime now);
}
