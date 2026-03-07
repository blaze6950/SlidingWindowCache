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
    /// Sorts candidates ascending by <c>segment.Range.Span(domain)</c>.
    /// The narrowest segment is first in the returned list.
    /// </remarks>
    public IReadOnlyList<CachedSegment<TRange, TData>> OrderCandidates(
        IReadOnlyList<CachedSegment<TRange, TData>> candidates)
    {
        return candidates
            .OrderBy(s => s.Range.Span(_domain).Value)
            .ToList();
    }
}
