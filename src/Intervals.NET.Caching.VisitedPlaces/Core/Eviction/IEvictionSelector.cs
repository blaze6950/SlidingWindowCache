namespace Intervals.NET.Caching.VisitedPlaces.Core.Eviction;

/// <summary>
/// Selects a single eviction candidate from the current segment pool using a
/// strategy-specific sampling approach, and owns the per-segment metadata required
/// to implement that strategy.
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <remarks>
/// <para><strong>Execution Context:</strong> Background Path (single writer thread)</para>
/// <para><strong>Responsibilities:</strong></para>
/// <list type="bullet">
/// <item><description>Selects the single worst eviction candidate from a random sample of segments</description></item>
/// <item><description>Creates and attaches selector-specific metadata when a new segment is stored</description></item>
/// <item><description>Updates selector-specific metadata when segments are used on the User Path</description></item>
/// <item><description>Does NOT decide how many segments to remove (that is the pressure's role)</description></item>
/// <item><description>Does NOT filter candidates for just-stored immunity — skips immune segments during sampling</description></item>
/// </list>
/// <para><strong>Sampling Contract:</strong></para>
/// <para>
/// Rather than sorting all segments (O(N log N)), selectors use random sampling: they
/// randomly examine a fixed number of segments (controlled by
/// <see cref="Public.Configuration.EvictionSamplingOptions.SampleSize"/>) and return the
/// worst candidate among the sample. This keeps eviction cost at O(SampleSize) regardless
/// of total cache size.
/// </para>
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
/// <item><description>Sort or scan the entire segment collection</description></item>
/// </list>
/// </remarks>
public interface IEvictionSelector<TRange, TData>
    where TRange : IComparable<TRange>
{
    /// <summary>
    /// Selects a single eviction candidate from <paramref name="segments"/> by randomly sampling
    /// a fixed number of segments and returning the worst according to this selector's strategy.
    /// </summary>
    /// <param name="segments">
    /// All currently stored segments (the full pool). The selector samples from this collection
    /// using random indexing and skips any segment present in <paramref name="immuneSegments"/>.
    /// </param>
    /// <param name="immuneSegments">
    /// Segments that must not be selected. Includes just-stored segments (Invariant VPC.E.3)
    /// and any segments already selected for eviction in the current pass.
    /// May be empty when no segments are immune.
    /// </param>
    /// <param name="candidate">
    /// When this method returns <see langword="true"/>, contains the selected eviction candidate.
    /// When this method returns <see langword="false"/>, this parameter is undefined.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if a candidate was found; <see langword="false"/> if no eligible
    /// candidate exists (e.g., all segments are immune, or the segment pool is empty).
    /// </returns>
    /// <remarks>
    /// <para>
    /// The caller is responsible for looping until pressure is satisfied or this method returns
    /// <see langword="false"/>. The executor adds each selected candidate to the immune set before
    /// the next call, preventing the same segment from being selected twice.
    /// </para>
    /// <para>
    /// When <paramref name="segments"/>.Count is smaller than the configured SampleSize, the selector
    /// naturally considers all eligible segments (the sample is clamped to the pool size).
    /// </para>
    /// </remarks>
    bool TrySelectCandidate(
        IReadOnlyList<CachedSegment<TRange, TData>> segments,
        IReadOnlySet<CachedSegment<TRange, TData>> immuneSegments,
        out CachedSegment<TRange, TData> candidate);

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
