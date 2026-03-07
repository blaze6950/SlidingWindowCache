namespace Intervals.NET.Caching.VisitedPlaces.Core.Eviction;

/// <summary>
/// Evaluates cache state and produces an <see cref="IEvictionPressure{TRange,TData}"/> object
/// representing whether a configured constraint has been violated.
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <remarks>
/// <para><strong>Execution Context:</strong> Background Path (single writer thread)</para>
/// <para><strong>Responsibilities:</strong></para>
/// <list type="bullet">
/// <item><description>Inspects the current segment collection after each storage step</description></item>
/// <item><description>Returns an <see cref="IEvictionPressure{TRange,TData}"/> that tracks constraint satisfaction</description></item>
/// <item><description>Returns <see cref="Pressure.NoPressure{TRange,TData}.Instance"/> when the constraint is not violated</description></item>
/// </list>
/// <para><strong>Architectural Invariant — Policies must NOT:</strong></para>
/// <list type="bullet">
/// <item><description>Know about eviction strategy (selector order)</description></item>
/// <item><description>Estimate how many segments to remove</description></item>
/// <item><description>Make assumptions about which segments will be removed</description></item>
/// </list>
/// <para><strong>OR Semantics (Invariant VPC.E.1a):</strong></para>
/// <para>
/// Multiple policies may be active simultaneously. Eviction is triggered when ANY policy
/// produces a pressure with <see cref="IEvictionPressure{TRange,TData}.IsExceeded"/> = <c>true</c>.
/// The executor removes segments until ALL pressures are satisfied (Invariant VPC.E.2a).
/// </para>
/// </remarks>
public interface IEvictionPolicy<TRange, TData>
    where TRange : IComparable<TRange>
{
    /// <summary>
    /// Evaluates whether the configured constraint is violated and returns a pressure object
    /// that tracks constraint satisfaction as segments are removed.
    /// </summary>
    /// <param name="allSegments">All currently stored segments.</param>
    /// <returns>
    /// An <see cref="IEvictionPressure{TRange,TData}"/> whose <see cref="IEvictionPressure{TRange,TData}.IsExceeded"/>
    /// indicates whether eviction is needed. Returns <see cref="Pressure.NoPressure{TRange,TData}.Instance"/>
    /// when the constraint is not violated.
    /// </returns>
    IEvictionPressure<TRange, TData> Evaluate(IReadOnlyList<CachedSegment<TRange, TData>> allSegments);
}
