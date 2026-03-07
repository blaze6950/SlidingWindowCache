namespace Intervals.NET.Caching.VisitedPlaces.Core.Eviction;

/// <summary>
/// Defines the order in which eviction candidates are considered for removal,
/// and owns the per-segment metadata required to implement that strategy.
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <remarks>
/// <para><strong>Execution Context:</strong> Background Path (single writer thread)</para>
/// <para><strong>Responsibilities:</strong></para>
/// <list type="bullet">
/// <item><description>Orders eviction candidates by strategy-specific priority (e.g., LRU, FIFO, SmallestFirst)</description></item>
/// <item><description>Creates and attaches selector-specific metadata when a new segment is stored</description></item>
/// <item><description>Updates selector-specific metadata when segments are used on the User Path</description></item>
/// <item><description>Does NOT filter candidates (just-stored immunity is handled by the executor)</description></item>
/// <item><description>Does NOT decide how many segments to remove (that is the pressure's role)</description></item>
/// </list>
/// <para><strong>Metadata ownership:</strong></para>
/// <para>
/// Each selector defines its own <see cref="IEvictionMetadata"/> implementation (nested inside the selector class).
/// Metadata is stored on <see cref="CachedSegment{TRange,TData}.EvictionMetadata"/>.
/// Selectors that need no metadata (e.g., SmallestFirst) leave this property <see langword="null"/>.
/// </para>
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
    /// The read only list is used intentiaonally - the collection of segment that are candidates to remove
    /// can NOT be IEnumerable because these candidates are used one by one to remove them from the actual storage.
    /// </returns>
    IReadOnlyList<CachedSegment<TRange, TData>> OrderCandidates(
        IReadOnlyList<CachedSegment<TRange, TData>> candidates);

    /// <summary>
    /// Attaches selector-specific metadata to a newly stored segment.
    /// Called by <c>BackgroundEventProcessor</c> immediately after each segment is added to storage.
    /// </summary>
    /// <param name="segment">The newly stored segment to initialize metadata for.</param>
    /// <param name="now">The current UTC timestamp at the time of storage.</param>
    /// <remarks>
    /// Selectors that require no metadata (e.g., <c>SmallestFirstEvictionSelector</c>)
    /// implement this as a no-op and leave <see cref="CachedSegment{TRange,TData}.EvictionMetadata"/> null.
    /// </remarks>
    void InitializeMetadata(CachedSegment<TRange, TData> segment, DateTime now);

    /// <summary>
    /// Updates selector-specific metadata on segments that were accessed on the User Path.
    /// Called by <c>BackgroundEventProcessor</c> in Step 1 of each background event cycle.
    /// </summary>
    /// <param name="usedSegments">The segments that were read during the User Path request.</param>
    /// <param name="now">The current UTC timestamp at the time of the background event.</param>
    /// <remarks>
    /// Selectors whose metadata is immutable after creation (e.g., <c>FifoEvictionSelector</c>)
    /// implement this as a no-op. Selectors that track access time (e.g., <c>LruEvictionSelector</c>)
    /// update <c>LastAccessedAt</c> on each segment's metadata.
    /// </remarks>
    void UpdateMetadata(IReadOnlyList<CachedSegment<TRange, TData>> usedSegments, DateTime now);
}
