namespace Intervals.NET.Caching.VisitedPlaces.Core.Eviction;

/// <summary>
/// An <see cref="IEvictionPolicy{TRange,TData}"/> that maintains incremental internal state
/// by receiving segment lifecycle notifications from the <see cref="EvictionPolicyEvaluator{TRange,TData}"/>.
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <remarks>
/// <para><strong>Purpose:</strong></para>
/// <para>
/// Stateless policies recompute their constraint from the full segment list on every
/// <see cref="IEvictionPolicy{TRange,TData}.Evaluate"/> call. This is acceptable for O(1) metrics
/// (e.g., <c>allSegments.Count</c>), but becomes a bottleneck for O(N) metrics such as total span,
/// which requires iterating all segments and calling <c>Span(domain)</c> on each.
/// </para>
/// <para>
/// Stateful policies avoid this by maintaining a running aggregate that is updated incrementally
/// via <see cref="OnSegmentAdded"/> and <see cref="OnSegmentRemoved"/>. The aggregate is always
/// current when <see cref="IEvictionPolicy{TRange,TData}.Evaluate"/> is called, so
/// <c>Evaluate</c> only needs to compare the cached value against the configured threshold — O(1).
/// </para>
/// <para><strong>Contract:</strong></para>
/// <list type="bullet">
/// <item><description>
///   <see cref="OnSegmentAdded"/> is called by <see cref="EvictionPolicyEvaluator{TRange,TData}"/>
///   immediately after each segment is added to storage (Background Path only).
/// </description></item>
/// <item><description>
///   <see cref="OnSegmentRemoved"/> is called by <see cref="EvictionPolicyEvaluator{TRange,TData}"/>
///   immediately after each segment is removed from storage (Background Path only).
/// </description></item>
/// <item><description>
///   Both methods run on the Background Path (single writer thread) and must never be called
///   from the User Path.
/// </description></item>
/// <item><description>
///   Implementations must be lightweight and allocation-free in both lifecycle methods.
/// </description></item>
/// </list>
/// <para><strong>Execution Context:</strong> Background Path (single writer thread)</para>
/// </remarks>
internal interface IStatefulEvictionPolicy<TRange, TData> : IEvictionPolicy<TRange, TData>
    where TRange : IComparable<TRange>
{
    /// <summary>
    /// Notifies this policy that a new segment has been added to storage.
    /// Implementations should update their internal running aggregate to include
    /// the contribution of <paramref name="segment"/>.
    /// </summary>
    /// <param name="segment">The segment that was just added to storage.</param>
    void OnSegmentAdded(CachedSegment<TRange, TData> segment);

    /// <summary>
    /// Notifies this policy that a segment has been removed from storage.
    /// Implementations should update their internal running aggregate to exclude
    /// the contribution of <paramref name="segment"/>.
    /// </summary>
    /// <param name="segment">The segment that was just removed from storage.</param>
    void OnSegmentRemoved(CachedSegment<TRange, TData> segment);
}
