namespace Intervals.NET.Caching.VisitedPlaces.Core.Eviction;

/// <summary>
/// Executes eviction by repeatedly asking the selector for a candidate until all eviction
/// pressures are satisfied or no more eligible candidates exist (constraint satisfaction loop).
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <remarks>
/// <para><strong>Execution Context:</strong> Background Path (single writer thread)</para>
/// <para><strong>Execution Flow:</strong></para>
/// <list type="number">
/// <item><description>Build the immune set from <c>justStoredSegments</c> (Invariant VPC.E.3).</description></item>
/// <item><description>Loop: call <see cref="IEvictionSelector{TRange,TData}.TrySelectCandidate"/> with the
///   current immune set; the selector samples directly from its injected storage.</description></item>
/// <item><description>If a candidate is returned, add it to <c>toRemove</c>, call
///   <see cref="IEvictionPressure{TRange,TData}.Reduce"/>, and add it to the immune set so it
///   cannot be selected again in this pass.</description></item>
/// <item><description>Stop when <c>IsExceeded = false</c> (all constraints satisfied) or
///   <see cref="IEvictionSelector{TRange,TData}.TrySelectCandidate"/> returns
///   <see langword="false"/> (no eligible candidates remain).</description></item>
/// </list>
/// <para><strong>Immunity handling:</strong></para>
/// <para>
/// Rather than pre-filtering to build a separate eligible-candidate list (O(N) allocation
/// scaling with cache size), the immune set is passed directly to the selector, which skips
/// immune segments inline during sampling. This keeps eviction cost at O(SampleSize) per
/// candidate selection regardless of total cache size.
/// </para>
/// <para><strong>Key Design Property:</strong></para>
/// <para>
/// The pressure objects track real constraint satisfaction as segments are removed. The
/// executor does not need to know how many segments to remove in advance — it simply loops
/// until the pressure reports satisfaction or candidates are exhausted.
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
    /// <param name="selector">The selector that picks eviction candidates via random sampling.</param>
    internal EvictionExecutor(IEvictionSelector<TRange, TData> selector)
    {
        _selector = selector;
    }

    /// <summary>
    /// Executes the constraint satisfaction eviction loop. Repeatedly selects candidates via
    /// the selector until the composite pressure is no longer exceeded or no more eligible
    /// candidates exist.
    /// </summary>
    /// <param name="pressure">
    /// The composite (or single) pressure tracking constraint satisfaction.
    /// Must have <see cref="IEvictionPressure{TRange,TData}.IsExceeded"/> = <c>true</c> when called.
    /// </param>
    /// <param name="justStoredSegments">
    /// All segments stored during the current event processing cycle (immune from eviction per
    /// Invariant VPC.E.3). Empty when no segments were stored in this cycle.
    /// </param>
    /// <returns>
    /// An <see cref="IEnumerable{T}"/> of segments that should be removed from storage, yielded
    /// one at a time as they are selected. The caller is responsible for actual removal from
    /// <see cref="Infrastructure.Storage.ISegmentStorage{TRange,TData}"/>.
    /// May yield nothing if all candidates are immune (Invariant VPC.E.3a).
    /// </returns>
    /// <remarks>
    /// <para><strong>Lazy immune-set allocation:</strong></para>
    /// <para>
    /// The <see cref="HashSet{T}"/> used as the immune set is constructed only when the loop
    /// body executes for the first time (i.e., only when <c>pressure.IsExceeded</c> is true
    /// on the first check). When no policy fires or all constraints are already satisfied,
    /// the HashSet is never allocated — zero cost on the common no-eviction path.
    /// </para>
    /// </remarks>
    internal IEnumerable<CachedSegment<TRange, TData>> Execute(
        IEvictionPressure<TRange, TData> pressure,
        IReadOnlyList<CachedSegment<TRange, TData>> justStoredSegments)
    {
        // Lazy-init: only build the HashSet if pressure is actually exceeded.
        // When no policy fires (NoPressure or all constraints satisfied up-front),
        // the HashSet is never allocated — zero cost on the common no-eviction path.
        HashSet<CachedSegment<TRange, TData>>? immune = null;

        while (pressure.IsExceeded)
        {
            // Build the immune set on first use (first eviction iteration).
            // justStoredSegments immunity (Invariant VPC.E.3) + already-selected candidates
            // are both tracked here. Constructed from justStoredSegments so all just-stored
            // entries are immune from the first selection attempt.
            immune ??= [..justStoredSegments];

            if (!_selector.TrySelectCandidate(immune, out var candidate))
            {
                // No eligible candidates remain (all immune or pool exhausted).
                yield break;
            }

            immune.Add(candidate);   // Prevent re-selecting this segment in the same pass.
            pressure.Reduce(candidate);
            yield return candidate;
        }
    }
}
