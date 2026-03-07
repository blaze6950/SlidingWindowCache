namespace Intervals.NET.Caching.VisitedPlaces.Core.Eviction;

/// <summary>
/// Determines whether the cache has exceeded a configured policy limit and
/// computes how many segments must be removed to return to within-policy state.
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <remarks>
/// <para><strong>Execution Context:</strong> Background Path (single writer thread)</para>
/// <para><strong>Responsibilities:</strong></para>
/// <list type="bullet">
/// <item><description>Inspects the current segment collection after each storage step</description></item>
/// <item><description>Returns <see langword="true"/> when the policy limit has been exceeded</description></item>
/// <item><description>Computes the minimum number of evictions needed to satisfy the constraint</description></item>
/// </list>
/// <para><strong>OR Semantics (Invariant VPC.E.1a):</strong></para>
/// <para>
/// Multiple evaluators may be active simultaneously. Eviction is triggered when ANY evaluator fires.
/// The <see cref="IEvictionExecutor{TRange,TData}"/> receives all fired evaluators and satisfies
/// all their constraints in a single pass (Invariant VPC.E.2a).
/// </para>
/// </remarks>
public interface IEvictionEvaluator<TRange, TData>
    where TRange : IComparable<TRange>
{
    /// <summary>
    /// Returns <see langword="true"/> when the policy limit has been exceeded and eviction should run.
    /// </summary>
    /// <param name="count">The current number of segments in storage.</param>
    /// <param name="allSegments">All currently stored segments.</param>
    /// <returns><see langword="true"/> if eviction should run; otherwise <see langword="false"/>.</returns>
    /// TODO: looks like we can merge ShouldEvict and ComputeRemovalCount into a single method that returns the number of segments to remove (0 if eviction should not run). This would simplify the logic and avoid redundant enumeration of segments in some cases.
    bool ShouldEvict(int count, IReadOnlyList<CachedSegment<TRange, TData>> allSegments);

    /// <summary>
    /// Computes the number of segments that must be removed to satisfy this evaluator's constraint.
    /// Only called after <see cref="ShouldEvict"/> returns <see langword="true"/>.
    /// </summary>
    /// <param name="count">The current number of segments in storage.</param>
    /// <param name="allSegments">All currently stored segments.</param>
    /// <returns>The minimum number of segments to remove.</returns>
    int ComputeRemovalCount(int count, IReadOnlyList<CachedSegment<TRange, TData>> allSegments);
}
