namespace Intervals.NET.Caching.VisitedPlaces.Core.Eviction;

/// <summary>
/// Defines the order in which eviction candidates are considered for removal.
/// Does NOT enforce any eviction policy — only determines candidate priority.
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <remarks>
/// <para><strong>Execution Context:</strong> Background Path (single writer thread)</para>
/// <para><strong>Responsibilities:</strong></para>
/// <list type="bullet">
/// <item><description>Orders eviction candidates by strategy-specific priority (e.g., LRU, FIFO, SmallestFirst)</description></item>
/// <item><description>Does NOT filter candidates (just-stored immunity is handled by the executor)</description></item>
/// <item><description>Does NOT decide how many segments to remove (that is the pressure's role)</description></item>
/// </list>
/// <para><strong>Architectural Invariant — Selectors must NOT:</strong></para>
/// <list type="bullet">
/// <item><description>Know about eviction policies or constraints</description></item>
/// <item><description>Decide when or whether to evict</description></item>
/// <item><description>Filter candidates based on immunity rules</description></item>
/// </list>
/// </remarks>
public interface IEvictionSelector<TRange, TData>
    where TRange : IComparable<TRange>
{
    /// <summary>
    /// Returns eviction candidates ordered by eviction priority (highest priority = first to be evicted).
    /// The executor iterates this list and removes segments until all pressures are satisfied.
    /// </summary>
    /// <param name="candidates">
    /// The eligible candidate segments (already filtered for immunity by the executor).
    /// </param>
    /// <returns>
    /// The same candidates ordered by eviction priority. The first element is the most eligible
    /// for eviction according to this selector's strategy.
    /// </returns>
    IReadOnlyList<CachedSegment<TRange, TData>> OrderCandidates(
        IReadOnlyList<CachedSegment<TRange, TData>> candidates);
}
