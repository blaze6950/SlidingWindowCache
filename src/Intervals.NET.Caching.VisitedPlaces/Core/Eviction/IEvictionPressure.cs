namespace Intervals.NET.Caching.VisitedPlaces.Core.Eviction;

/// <summary>
/// Tracks whether an eviction constraint is satisfied. Updated incrementally as segments
/// are removed during eviction execution.
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <remarks>
/// <para><strong>Execution Context:</strong> Background Path (single writer thread)</para>
/// <para><strong>Lifecycle:</strong></para>
/// <list type="bullet">
/// <item><description>Created by an <see cref="IEvictionPolicy{TRange,TData}"/> during evaluation</description></item>
/// <item><description>Queried and updated by the <see cref="EvictionExecutor{TRange,TData}"/> during execution</description></item>
/// <item><description>Discarded after the eviction pass completes</description></item>
/// </list>
/// <para><strong>Contract:</strong></para>
/// <list type="bullet">
/// <item><description><see cref="IsExceeded"/> must be <c>true</c> when the constraint is violated</description></item>
/// <item><description><see cref="Reduce"/> must update internal state to reflect the removal of a segment</description></item>
/// <item><description>Implementations must be lightweight and allocation-free in <see cref="Reduce"/></description></item>
/// </list>
/// </remarks>
public interface IEvictionPressure<TRange, TData>
    where TRange : IComparable<TRange>
{
    /// <summary>
    /// Gets whether the constraint is currently violated and more segments need to be removed.
    /// </summary>
    bool IsExceeded { get; }

    /// <summary>
    /// Updates the pressure state to account for the removal of <paramref name="removedSegment"/>.
    /// Called by the executor after each segment is removed from storage.
    /// </summary>
    /// <param name="removedSegment">The segment that was just removed from storage.</param>
    void Reduce(CachedSegment<TRange, TData> removedSegment);
}
