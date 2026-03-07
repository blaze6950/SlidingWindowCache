namespace Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Executors;

/// <summary>
/// An <see cref="IEvictionExecutor{TRange,TData}"/> that evicts segments using
/// the Least Recently Used (LRU) strategy.
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <remarks>
/// <para><strong>Strategy:</strong> Evicts the segment(s) with the oldest
/// <see cref="SegmentStatistics.LastAccessedAt"/>.</para>
/// <para><strong>Execution Context:</strong> Background Path (single writer thread)</para>
/// <para><strong>Invariant VPC.E.3 — Just-stored immunity:</strong>
/// The <c>justStored</c> segment is always excluded from the eviction candidate set.</para>
/// <para><strong>Invariant VPC.E.2a — Single-pass eviction:</strong>
/// A single invocation satisfies ALL fired evaluator constraints simultaneously by computing
/// the combined target count before beginning the removal loop.</para>
/// </remarks>
internal sealed class LruEvictionExecutor<TRange, TData> : IEvictionExecutor<TRange, TData>
    where TRange : IComparable<TRange>
{
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
    /// <item><description>Sort candidates ascending by <see cref="SegmentStatistics.LastAccessedAt"/></description></item>
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
            .OrderBy(s => s.Statistics.LastAccessedAt)
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
