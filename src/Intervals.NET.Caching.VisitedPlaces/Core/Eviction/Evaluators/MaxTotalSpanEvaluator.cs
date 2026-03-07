using Intervals.NET.Caching.Extensions;
using Intervals.NET.Domain.Abstractions;

namespace Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Evaluators;

/// <summary>
/// An <see cref="IEvictionEvaluator{TRange,TData}"/> that fires when the sum of all cached
/// segment spans (total domain coverage) exceeds a configured maximum.
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <typeparam name="TDomain">The range domain type used to compute spans.</typeparam>
/// <remarks>
/// <para><strong>Firing Condition:</strong>
/// <c>sum(segment.Range.Span(domain) for segment in allSegments) &gt; MaxTotalSpan</c></para>
/// <para>
/// This evaluator limits the total cached domain coverage regardless of how many
/// segments it is split into. More meaningful than segment count when segments vary
/// significantly in span.
/// </para>
/// <para><strong>Span Computation:</strong> Uses <typeparamref name="TDomain"/> to compute each
/// segment's span at evaluation time. The domain is captured at construction.</para>
/// </remarks>
internal sealed class MaxTotalSpanEvaluator<TRange, TData, TDomain> : IEvictionEvaluator<TRange, TData>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private readonly TDomain _domain;

    /// <summary>
    /// The maximum total span allowed across all cached segments before eviction is triggered.
    /// </summary>
    public int MaxTotalSpan { get; }

    /// <summary>
    /// Initializes a new <see cref="MaxTotalSpanEvaluator{TRange,TData,TDomain}"/> with the
    /// specified maximum total span and domain.
    /// </summary>
    /// <param name="maxTotalSpan">
    /// The maximum total span (in domain units). Must be &gt;= 1.
    /// </param>
    /// <param name="domain">The range domain used to compute segment spans.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="maxTotalSpan"/> is less than 1.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="domain"/> is <see langword="null"/>.
    /// </exception>
    public MaxTotalSpanEvaluator(int maxTotalSpan, TDomain domain)
    {
        if (maxTotalSpan < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxTotalSpan),
                "MaxTotalSpan must be greater than or equal to 1.");
        }

        if (domain is null)
        {
            throw new ArgumentNullException(nameof(domain));
        }

        MaxTotalSpan = maxTotalSpan;
        _domain = domain;
    }

    /// <inheritdoc/>
    public bool ShouldEvict(int count, IReadOnlyList<CachedSegment<TRange, TData>> allSegments) =>
        allSegments.Sum(s => s.Range.Span(_domain).Value) > MaxTotalSpan;

    /// <inheritdoc/>
    public int ComputeRemovalCount(int count, IReadOnlyList<CachedSegment<TRange, TData>> allSegments)
    {
        var totalSpan = allSegments.Sum(s => s.Range.Span(_domain).Value);
        var excessSpan = totalSpan - MaxTotalSpan;
        if (excessSpan <= 0)
        {
            return 0;
        }

        // Estimate the minimum number of segments to remove to bring the total span within limit.
        // Sort segments by span descending and greedily remove from largest to find the lower bound.
        // The executor may choose a different order (LRU, FIFO, etc.), so this is an estimate;
        // partial satisfaction is acceptable — the next storage event will trigger another pass.
        var sortedSpans = allSegments
            .Select(s => s.Range.Span(_domain).Value)
            .OrderByDescending(span => span);

        long removedSpan = 0;
        var segCount = 0;
        foreach (var span in sortedSpans)
        {
            removedSpan += span;
            segCount++;
            if (removedSpan >= excessSpan)
            {
                break;
            }
        }

        return segCount;
    }
}
