namespace Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Evaluators;

/// <summary>
/// An <see cref="IEvictionEvaluator{TRange,TData}"/> that fires when the number of cached
/// segments exceeds a configured maximum count.
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <remarks>
/// <para><strong>Firing Condition:</strong> <c>count &gt; MaxCount</c></para>
/// <para><strong>Removal Count:</strong> <c>count - MaxCount</c> (the excess)</para>
/// <para>
/// This is the simplest evaluator: it limits the total number of independently-cached segments
/// regardless of their span or data size.
/// </para>
/// </remarks>
internal sealed class MaxSegmentCountEvaluator<TRange, TData> : IEvictionEvaluator<TRange, TData>
    where TRange : IComparable<TRange>
{
    /// <summary>
    /// The maximum number of segments allowed in the cache before eviction is triggered.
    /// </summary>
    public int MaxCount { get; }

    /// <summary>
    /// Initializes a new <see cref="MaxSegmentCountEvaluator{TRange,TData}"/> with the specified maximum segment count.
    /// </summary>
    /// <param name="maxCount">
    /// The maximum number of segments. Must be &gt;= 1.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="maxCount"/> is less than 1.
    /// </exception>
    public MaxSegmentCountEvaluator(int maxCount)
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
    public bool ShouldEvict(int count, IReadOnlyList<CachedSegment<TRange, TData>> allSegments) =>
        count > MaxCount;

    /// <inheritdoc/>
    public int ComputeRemovalCount(int count, IReadOnlyList<CachedSegment<TRange, TData>> allSegments) =>
        Math.Max(0, count - MaxCount);
}
