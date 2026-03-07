namespace Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Pressure;

/// <summary>
/// An <see cref="IEvictionPressure{TRange,TData}"/> that tracks whether the segment count
/// exceeds a configured maximum. Each <see cref="Reduce"/> call decrements the tracked count.
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <remarks>
/// <para><strong>Produced by:</strong> <see cref="Policies.MaxSegmentCountPolicy{TRange,TData}"/></para>
/// <para><strong>Constraint:</strong> <c>currentCount &gt; maxCount</c></para>
/// <para><strong>Reduce behavior:</strong> Decrements <c>currentCount</c> by 1 (count-based eviction
/// is order-independent — every segment removal equally satisfies the constraint).</para>
/// </remarks>
internal sealed class SegmentCountPressure<TRange, TData> : IEvictionPressure<TRange, TData>
    where TRange : IComparable<TRange>
{
    private int _currentCount;
    private readonly int _maxCount;

    /// <summary>
    /// Initializes a new <see cref="SegmentCountPressure{TRange,TData}"/>.
    /// </summary>
    /// <param name="currentCount">The current number of segments in storage.</param>
    /// <param name="maxCount">The maximum allowed segment count.</param>
    internal SegmentCountPressure(int currentCount, int maxCount)
    {
        _currentCount = currentCount;
        _maxCount = maxCount;
    }

    /// <inheritdoc/>
    public bool IsExceeded => _currentCount > _maxCount;

    /// <inheritdoc/>
    /// <remarks>Decrements the tracked segment count by 1.</remarks>
    public void Reduce(CachedSegment<TRange, TData> removedSegment)
    {
        _currentCount--;
    }
}
