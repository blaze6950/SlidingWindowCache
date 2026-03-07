namespace Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Selectors;

/// <summary>
/// An <see cref="IEvictionSelector{TRange,TData}"/> that orders eviction candidates using
/// the Least Recently Used (LRU) strategy.
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <remarks>
/// <para><strong>Strategy:</strong> Orders candidates ascending by
/// <see cref="SegmentStatistics.LastAccessedAt"/> — the least recently accessed segment
/// is first (highest eviction priority).</para>
/// <para><strong>Execution Context:</strong> Background Path (single writer thread)</para>
/// </remarks>
internal sealed class LruEvictionSelector<TRange, TData> : IEvictionSelector<TRange, TData>
    where TRange : IComparable<TRange>
{
    /// <inheritdoc/>
    /// <remarks>
    /// Sorts candidates ascending by <see cref="SegmentStatistics.LastAccessedAt"/>.
    /// The segment with the oldest access time is first in the returned list.
    /// </remarks>
    public IReadOnlyList<CachedSegment<TRange, TData>> OrderCandidates(
        IReadOnlyList<CachedSegment<TRange, TData>> candidates)
    {
        return candidates
            .OrderBy(s => s.Statistics.LastAccessedAt)
            .ToList();
    }
}
