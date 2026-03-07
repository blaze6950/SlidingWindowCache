namespace Intervals.NET.Caching.VisitedPlaces.Core.Eviction;

/// <summary>
/// Executes eviction by removing segments in selector-defined order until all eviction pressures
/// are satisfied (constraint satisfaction loop).
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <remarks>
/// <para><strong>Execution Context:</strong> Background Path (single writer thread)</para>
/// <para><strong>Execution Flow:</strong></para>
/// <list type="number">
/// <item><description>Filter out just-stored segments (Invariant VPC.E.3 — just-stored immunity)</description></item>
/// <item><description>Order remaining candidates via <see cref="IEvictionSelector{TRange,TData}"/></description></item>
/// <item><description>Iterate candidates: for each, call <see cref="IEvictionPressure{TRange,TData}.Reduce"/>
///   on the composite pressure, then check <see cref="IEvictionPressure{TRange,TData}.IsExceeded"/></description></item>
/// <item><description>Stop when <c>IsExceeded = false</c> (all constraints satisfied) or candidates exhausted</description></item>
/// </list>
/// <para><strong>Key Design Property:</strong></para>
/// <para>
/// Unlike the old evaluator/executor split where evaluators estimated removal counts assuming
/// a specific order, this executor uses actual constraint tracking. The pressure objects track
/// real satisfaction as segments are removed, regardless of the selector's order. This eliminates
/// the mismatch between span-based evaluators and order-based executors.
/// </para>
/// <para><strong>Single-pass eviction (Invariant VPC.E.2a):</strong></para>
/// <para>
/// The executor runs at most once per background event. A single invocation satisfies ALL
/// policy constraints simultaneously via the composite pressure.
/// </para>
/// </remarks>
internal sealed class EvictionExecutor<TRange, TData>
    where TRange : IComparable<TRange>
{
    private readonly IEvictionSelector<TRange, TData> _selector;

    /// <summary>
    /// Initializes a new <see cref="EvictionExecutor{TRange,TData}"/>.
    /// </summary>
    /// <param name="selector">The selector that determines eviction candidate order.</param>
    internal EvictionExecutor(IEvictionSelector<TRange, TData> selector)
    {
        _selector = selector;
    }

    /// <summary>
    /// Executes the constraint satisfaction eviction loop. Removes segments in selector-defined
    /// order until the composite pressure is no longer exceeded or candidates are exhausted.
    /// </summary>
    /// <param name="pressure">
    /// The composite (or single) pressure tracking constraint satisfaction.
    /// Must have <see cref="IEvictionPressure{TRange,TData}.IsExceeded"/> = <c>true</c> when called.
    /// </param>
    /// <param name="allSegments">All currently stored segments (the full candidate pool).</param>
    /// <param name="justStoredSegments">
    /// All segments stored during the current event processing cycle (immune from eviction per
    /// Invariant VPC.E.3). Empty when no segments were stored in this cycle.
    /// </param>
    /// <returns>
    /// The segments that should be removed from storage. The caller is responsible for actual
    /// removal from <see cref="Infrastructure.Storage.ISegmentStorage{TRange,TData}"/>.
    /// May be empty if all candidates are immune (Invariant VPC.E.3a).
    /// </returns>
    internal IReadOnlyList<CachedSegment<TRange, TData>> Execute(
        IEvictionPressure<TRange, TData> pressure,
        IReadOnlyList<CachedSegment<TRange, TData>> allSegments,
        IReadOnlyList<CachedSegment<TRange, TData>> justStoredSegments)
    {
        // Step 1: Build the candidate set by filtering out just-stored segments (immunity).
        var eligibleCandidates = FilterImmune(allSegments, justStoredSegments);

        if (eligibleCandidates.Count == 0)
        {
            // All segments are immune — no-op (Invariant VPC.E.3a).
            return [];
        }

        // Step 2: Order candidates by selector strategy.
        var orderedCandidates = _selector.OrderCandidates(eligibleCandidates);

        // Step 3: Constraint satisfaction loop — remove segments until pressure is satisfied.
        var toRemove = new List<CachedSegment<TRange, TData>>();

        foreach (var candidate in orderedCandidates)
        {
            toRemove.Add(candidate);
            pressure.Reduce(candidate);

            if (!pressure.IsExceeded)
            {
                break;
            }
        }

        return toRemove;
    }

    /// <summary>
    /// Filters out just-stored segments from the candidate pool (Invariant VPC.E.3).
    /// </summary>
    private static List<CachedSegment<TRange, TData>> FilterImmune(
        IReadOnlyList<CachedSegment<TRange, TData>> allSegments,
        IReadOnlyList<CachedSegment<TRange, TData>> justStoredSegments)
    {
        if (justStoredSegments.Count == 0)
        {
            // No immunity — all segments are candidates.
            return new List<CachedSegment<TRange, TData>>(allSegments);
        }

        var result = new List<CachedSegment<TRange, TData>>(allSegments.Count);
        foreach (var segment in allSegments)
        {
            if (!justStoredSegments.Contains(segment))
            {
                result.Add(segment);
            }
        }

        return result;
    }
}
