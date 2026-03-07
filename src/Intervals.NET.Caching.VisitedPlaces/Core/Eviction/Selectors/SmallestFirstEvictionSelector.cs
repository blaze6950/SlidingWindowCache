using Intervals.NET.Caching.Extensions;
using Intervals.NET.Domain.Abstractions;

namespace Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Selectors;

/// <summary>
/// An <see cref="IEvictionSelector{TRange,TData}"/> that orders eviction candidates using the
/// Smallest-First strategy: segments with the narrowest range span are evicted first.
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <typeparam name="TDomain">The range domain type used to compute segment spans.</typeparam>
/// <remarks>
/// <para><strong>Strategy:</strong> Orders candidates ascending by span
/// (computed as <c>segment.Range.Span(domain)</c>) — the narrowest segment is first
/// (highest eviction priority).</para>
/// <para><strong>Execution Context:</strong> Background Path (single writer thread)</para>
/// <para>
/// Smallest-First optimizes for total domain coverage: wide segments (covering more of the domain)
/// are retained over narrow ones. Best for workloads where wider segments are more valuable
/// because they are more likely to be re-used.
/// </para>
/// <para><strong>Metadata:</strong> No metadata needed — ordering is derived entirely from
/// <c>segment.Range.Span(domain)</c>. <see cref="CachedSegment{TRange,TData}.EvictionMetadata"/>
/// is left <see langword="null"/> for segments managed by this selector.</para>
/// </remarks>
internal sealed class SmallestFirstEvictionSelector<TRange, TData, TDomain> : IEvictionSelector<TRange, TData>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private readonly TDomain _domain;

    /// <summary>
    /// Initializes a new <see cref="SmallestFirstEvictionSelector{TRange,TData,TDomain}"/>.
    /// </summary>
    /// <param name="domain">The range domain used to compute segment spans.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="domain"/> is <see langword="null"/>.
    /// </exception>
    public SmallestFirstEvictionSelector(TDomain domain)
    {
        if (domain is null)
        {
            throw new ArgumentNullException(nameof(domain));
        }

        _domain = domain;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// No-op — SmallestFirst requires no per-segment metadata.
    /// </remarks>
    public void InitializeMetadata(CachedSegment<TRange, TData> segment, DateTime now)
    {
        // SmallestFirst derives ordering from segment span — no metadata needed.
    }

    /// <inheritdoc/>
    /// <remarks>
    /// No-op — SmallestFirst ordering is based on span, which is immutable after segment creation.
    /// Access patterns do not affect eviction priority.
    /// </remarks>
    public void UpdateMetadata(IReadOnlyList<CachedSegment<TRange, TData>> usedSegments, DateTime now)
    {
        // SmallestFirst derives ordering from segment span — no metadata to update.
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Sorts candidates ascending by <c>segment.Range.Span(domain)</c>.
    /// The narrowest segment is first in the returned list.
    /// </remarks>
    public IReadOnlyList<CachedSegment<TRange, TData>> OrderCandidates(
        IReadOnlyList<CachedSegment<TRange, TData>> candidates)
    {
        return candidates
            .OrderBy(s => s.Range.Span(_domain).Value) // todo: think about defining metadata for this type of selector in order to prevent calculating span for every segment inside this method. Segments are immutable, we can calculate span on metadata initialization and then just use it for this method. 
            .ToList();
    }
}
