using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Pressure;

namespace Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Policies;

/// <summary>
/// An <see cref="IEvictionPolicy{TRange,TData}"/> that fires when the number of cached
/// segments exceeds a configured maximum count.
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <remarks>
/// <para><strong>Firing Condition:</strong> <c>allSegments.Count &gt; MaxCount</c></para>
/// <para><strong>Pressure Produced:</strong> <see cref="SegmentCountPressure{TRange,TData}"/>
/// with <c>currentCount = allSegments.Count</c> and <c>maxCount = MaxCount</c>.</para>
/// <para>
/// This is the simplest policy: it limits the total number of independently-cached segments
/// regardless of their span or data size. Count-based eviction is order-independent —
/// removing any segment equally satisfies the constraint.
/// </para>
/// </remarks>
internal sealed class MaxSegmentCountPolicy<TRange, TData> : IEvictionPolicy<TRange, TData>
    where TRange : IComparable<TRange>
{
    /// <summary>
    /// The maximum number of segments allowed in the cache before eviction is triggered.
    /// </summary>
    public int MaxCount { get; }

    /// <summary>
    /// Initializes a new <see cref="MaxSegmentCountPolicy{TRange,TData}"/> with the specified maximum segment count.
    /// </summary>
    /// <param name="maxCount">
    /// The maximum number of segments. Must be &gt;= 1.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="maxCount"/> is less than 1.
    /// </exception>
    public MaxSegmentCountPolicy(int maxCount)
    {
        if (maxCount < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxCount),
                "MaxCount must be greater than or equal to 1.");
        }

        MaxCount = maxCount;
    }

    /// <inheritdoc/>
    public IEvictionPressure<TRange, TData> Evaluate(IReadOnlyList<CachedSegment<TRange, TData>> allSegments)
    {
        var count = allSegments.Count;

        if (count <= MaxCount)
        {
            return NoPressure<TRange, TData>.Instance;
        }

        return new SegmentCountPressure<TRange, TData>(count, MaxCount);
    }
}
