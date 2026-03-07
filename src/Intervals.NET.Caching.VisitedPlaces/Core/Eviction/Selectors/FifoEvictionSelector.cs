namespace Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Selectors;

/// <summary>
/// An <see cref="IEvictionSelector{TRange,TData}"/> that orders eviction candidates using
/// the First In, First Out (FIFO) strategy.
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <remarks>
/// <para><strong>Strategy:</strong> Orders candidates ascending by
/// <see cref="SegmentStatistics.CreatedAt"/> — the oldest segment is first (highest eviction priority).</para>
/// <para><strong>Execution Context:</strong> Background Path (single writer thread)</para>
/// <para>
/// FIFO treats the cache as a fixed-size sliding window over time. It does not reflect access
/// patterns and is most appropriate for workloads where all segments have similar
/// re-access probability.
/// </para>
/// </remarks>
internal sealed class FifoEvictionSelector<TRange, TData> : IEvictionSelector<TRange, TData>
    where TRange : IComparable<TRange>
{
    /// <inheritdoc/>
    /// <remarks>
    /// Sorts candidates ascending by <see cref="SegmentStatistics.CreatedAt"/>.
    /// The oldest segment is first in the returned list.
    /// </remarks>
    public IReadOnlyList<CachedSegment<TRange, TData>> OrderCandidates(
        IReadOnlyList<CachedSegment<TRange, TData>> candidates)
    {
        return candidates
            .OrderBy(s => s.Statistics.CreatedAt)
            .ToList();
    }
}
