using Intervals.NET.Caching.Extensions;
using Intervals.NET.Caching.VisitedPlaces.Public.Configuration;
using Intervals.NET.Domain.Abstractions;

namespace Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Selectors;

/// <summary>
/// An <see cref="IEvictionSelector{TRange,TData}"/> that selects eviction candidates using the
/// Smallest-First strategy: among a random sample, the segment with the narrowest range span
/// is the worst eviction candidate.
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <typeparam name="TDomain">The range domain type used to compute segment spans.</typeparam>
/// <remarks>
/// <para><strong>Strategy:</strong> Among a random sample of segments, selects the one with
/// the smallest span (stored in <see cref="SmallestFirstMetadata.Span"/>) — the narrowest
/// segment covers the least domain and is the worst eviction candidate.</para>
/// <para><strong>Execution Context:</strong> Background Path (single writer thread)</para>
/// <para>
/// Smallest-First optimizes for total domain coverage: wide segments (covering more of the domain)
/// are retained over narrow ones. Best for workloads where wider segments are more valuable
/// because they are more likely to be re-used.
/// </para>
/// <para><strong>Metadata:</strong> Uses <see cref="SmallestFirstMetadata"/> stored on
/// <see cref="CachedSegment{TRange,TData}.EvictionMetadata"/>. The span is computed once at
/// initialization from <c>segment.Range.Span(domain).Value</c> and cached — segments are
/// immutable so the span never changes, and pre-computing it avoids redundant computation
/// during every <see cref="IEvictionSelector{TRange,TData}.TrySelectCandidate"/> call.
/// <c>UpdateMetadata</c> is a no-op because span is unaffected by access patterns.</para>
/// <para><strong>Performance:</strong> O(SampleSize) per candidate selection; no sorting,
/// no collection copying. SampleSize defaults to
/// <see cref="EvictionSamplingOptions.DefaultSampleSize"/> (32).</para>
/// </remarks>
internal sealed class SmallestFirstEvictionSelector<TRange, TData, TDomain>
    : SamplingEvictionSelector<TRange, TData>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    /// <summary>
    /// Selector-specific metadata for <see cref="SmallestFirstEvictionSelector{TRange,TData,TDomain}"/>.
    /// Caches the pre-computed span of the segment's range.
    /// </summary>
    internal sealed class SmallestFirstMetadata : IEvictionMetadata
    {
        /// <summary>
        /// The pre-computed span of the segment's range (in domain steps).
        /// Immutable — segment ranges never change after creation.
        /// </summary>
        public long Span { get; }

        /// <summary>
        /// Initializes a new <see cref="SmallestFirstMetadata"/> with the given span.
        /// </summary>
        /// <param name="span">The pre-computed span of the segment's range.</param>
        public SmallestFirstMetadata(long span)
        {
            Span = span;
        }
    }

    private readonly TDomain _domain;

    /// <summary>
    /// Initializes a new <see cref="SmallestFirstEvictionSelector{TRange,TData,TDomain}"/>.
    /// </summary>
    /// <param name="domain">The range domain used to compute segment spans.</param>
    /// <param name="samplingOptions">
    /// Optional sampling configuration. When <see langword="null"/>,
    /// <see cref="EvictionSamplingOptions.Default"/> is used (SampleSize = 32).
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="domain"/> is <see langword="null"/>.
    /// </exception>
    public SmallestFirstEvictionSelector(
        TDomain domain,
        EvictionSamplingOptions? samplingOptions = null)
        : base(samplingOptions)
    {
        if (domain is null)
        {
            throw new ArgumentNullException(nameof(domain));
        }

        _domain = domain;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <paramref name="candidate"/> is worse than <paramref name="current"/> when its span
    /// is smaller — narrower segments cover less domain and are evicted first.
    /// Falls back to live span computation when <see cref="SmallestFirstMetadata"/> is absent.
    /// </remarks>
    protected override bool IsWorse(
        CachedSegment<TRange, TData> candidate,
        CachedSegment<TRange, TData> current)
    {
        var candidateSpan = candidate.EvictionMetadata is SmallestFirstMetadata cm
            ? cm.Span
            : candidate.Range.Span(_domain).Value;

        var currentSpan = current.EvictionMetadata is SmallestFirstMetadata curm
            ? curm.Span
            : current.Range.Span(_domain).Value;

        return candidateSpan < currentSpan;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Computes <c>segment.Range.Span(domain).Value</c> once and stores it as a
    /// <see cref="SmallestFirstMetadata"/> instance on the segment. Because segment ranges
    /// are immutable, this value never needs to be recomputed.
    /// </remarks>
    public override void InitializeMetadata(CachedSegment<TRange, TData> segment, DateTime now)
    {
        segment.EvictionMetadata = new SmallestFirstMetadata(segment.Range.Span(_domain).Value);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// No-op — SmallestFirst ordering is based on span, which is immutable after segment creation.
    /// Access patterns do not affect eviction priority.
    /// </remarks>
    public override void UpdateMetadata(
        IReadOnlyList<CachedSegment<TRange, TData>> usedSegments,
        DateTime now)
    {
        // SmallestFirst derives ordering from segment span — no metadata to update.
    }
}
