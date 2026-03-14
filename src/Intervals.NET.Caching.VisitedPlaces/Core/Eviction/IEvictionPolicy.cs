namespace Intervals.NET.Caching.VisitedPlaces.Core.Eviction;

/// <summary>
/// Evaluates cache state and produces an <see cref="IEvictionPressure{TRange,TData}"/> object
/// representing whether a configured constraint has been violated.
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <remarks>
/// Policies maintain incremental state via <see cref="OnSegmentAdded"/> and
/// <see cref="OnSegmentRemoved"/>, enabling O(1) evaluation. Multiple policies use OR
/// semantics: eviction triggers when ANY policy is exceeded.
/// </remarks>
public interface IEvictionPolicy<TRange, TData>
    where TRange : IComparable<TRange>
{
    /// <summary>
    /// Notifies this policy that a new segment has been added to storage.
    /// </summary>
    /// <param name="segment">The segment that was just added to storage.</param>
    void OnSegmentAdded(CachedSegment<TRange, TData> segment);

    /// <summary>
    /// Notifies this policy that a segment has been removed from storage.
    /// </summary>
    /// <param name="segment">The segment that was just removed from storage.</param>
    /// <remarks>
    /// Implementations must use thread-safe operations. See invariant VPC.D.6.
    /// </remarks>
    void OnSegmentRemoved(CachedSegment<TRange, TData> segment);

    /// <summary>
    /// Evaluates whether the configured constraint is violated and returns a pressure object
    /// that tracks constraint satisfaction as segments are removed.
    /// </summary>
    /// <returns>
    /// An <see cref="IEvictionPressure{TRange,TData}"/> whose <see cref="IEvictionPressure{TRange,TData}.IsExceeded"/>
    /// indicates whether eviction is needed.
    /// </returns>
    IEvictionPressure<TRange, TData> Evaluate();
}
