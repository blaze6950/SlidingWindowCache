using Intervals.NET.Caching.Extensions;
using Intervals.NET.Domain.Abstractions;

namespace Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Executors;

/// <summary>
/// An <see cref="IEvictionExecutor{TRange,TData}"/> that evicts segments using the
/// Smallest-First strategy: segments with the narrowest range span are evicted first.
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <typeparam name="TDomain">The range domain type used to compute segment spans.</typeparam>
/// <remarks>
/// <para><strong>Strategy:</strong> Evicts the segment(s) with the smallest span
/// (narrowest range coverage), computed as <c>segment.Range.Span(domain)</c>.</para>
/// <para><strong>Execution Context:</strong> Background Path (single writer thread)</para>
/// <para>
/// Smallest-First optimizes for total domain coverage: wide segments (covering more of the domain)
/// are retained over narrow ones. Best for workloads where wider segments are more valuable
/// because they are more likely to be re-used.
/// </para>
/// <para><strong>Invariant VPC.E.3 — Just-stored immunity:</strong>
/// The <c>justStored</c> segment is always excluded from the eviction candidate set.</para>
/// <para><strong>Invariant VPC.E.2a — Single-pass eviction:</strong>
/// A single invocation satisfies ALL fired evaluator constraints simultaneously.</para>
/// </remarks>
internal sealed class SmallestFirstEvictionExecutor<TRange, TData, TDomain> : IEvictionExecutor<TRange, TData>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private readonly TDomain _domain;

    /// <summary>
    /// Initializes a new <see cref="SmallestFirstEvictionExecutor{TRange,TData,TDomain}"/>.
    /// </summary>
    /// <param name="domain">The range domain used to compute segment spans.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="domain"/> is <see langword="null"/>.
    /// </exception>
    public SmallestFirstEvictionExecutor(TDomain domain)
    {
        if (domain is null)
        {
            throw new ArgumentNullException(nameof(domain));
        }

        _domain = domain;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Increments <see cref="SegmentStatistics.HitCount"/> and sets
    /// <see cref="SegmentStatistics.LastAccessedAt"/> to <paramref name="now"/>
    /// for each segment in <paramref name="usedSegments"/>.
    /// </remarks>
    public void UpdateStatistics(IReadOnlyList<CachedSegment<TRange, TData>> usedSegments, DateTime now)
    {
        foreach (var segment in usedSegments)
        {
            segment.Statistics.HitCount++;
            segment.Statistics.LastAccessedAt = now;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para><strong>Selection algorithm:</strong></para>
    /// <list type="number">
    /// <item><description>Build the candidate set = all segments except <paramref name="justStored"/> (immunity rule)</description></item>
    /// <item><description>Sort candidates ascending by <c>segment.Range.Span(domain)</c></description></item>
    /// <item><description>Compute target removal count = max of all fired evaluator removal counts</description></item>
    /// <item><description>Return the first <c>removalCount</c> candidates</description></item>
    /// </list>
    /// </remarks>
    public IReadOnlyList<CachedSegment<TRange, TData>> SelectForEviction(
        IReadOnlyList<CachedSegment<TRange, TData>> allSegments,
        CachedSegment<TRange, TData>? justStored,
        IReadOnlyList<IEvictionEvaluator<TRange, TData>> firedEvaluators)
    {
        var candidates = allSegments
            .Where(s => !ReferenceEquals(s, justStored))
            .OrderBy(s => s.Range.Span(_domain).Value)
            .ToList();

        if (candidates.Count == 0)
        {
            // All segments are immune — no-op (Invariant VPC.E.3a)
            return [];
        }

        var removalCount = firedEvaluators.Max(e => e.ComputeRemovalCount(allSegments.Count, allSegments));
        return candidates.Take(removalCount).ToList();
    }
}
