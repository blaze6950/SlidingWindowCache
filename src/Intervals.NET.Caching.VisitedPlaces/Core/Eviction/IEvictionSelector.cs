using Intervals.NET.Caching.VisitedPlaces.Infrastructure.Storage;

namespace Intervals.NET.Caching.VisitedPlaces.Core.Eviction;

/// <summary>
/// Extends <see cref="IEvictionSelector{TRange,TData}"/> with the post-construction storage
/// injection required by sampling-based selectors.
/// </summary>
/// <remarks>
/// This interface is intentionally <c>internal</c> because <see cref="ISegmentStorage{TRange,TData}"/>
/// is an internal type. The composition root casts to <see cref="IStorageAwareEvictionSelector{TRange,TData}"/>
/// to call <see cref="Initialize"/> after storage is created; the public
/// <see cref="IEvictionSelector{TRange,TData}"/> interface remains free of internal types.
/// </remarks>
internal interface IStorageAwareEvictionSelector<TRange, TData>
    where TRange : IComparable<TRange>
{
    /// <summary>
    /// Injects the storage instance into this selector.
    /// Must be called exactly once, before any call to
    /// <see cref="IEvictionSelector{TRange,TData}.TrySelectCandidate"/>.
    /// </summary>
    /// <param name="storage">The segment storage used to obtain random samples.</param>
    /// <remarks>
    /// This method exists because storage and selector are both created inside the composition
    /// root (<see cref="Public.Cache.VisitedPlacesCache{TRange,TData,TDomain}"/>) but the
    /// selector is constructed before storage. The composition root calls
    /// <c>Initialize(storage)</c> immediately after storage is created.
    /// </remarks>
    void Initialize(ISegmentStorage<TRange, TData> storage);
}

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
/// <item><description>Selects the single worst eviction candidate by randomly sampling segments via storage</description></item>
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
/// <para><strong>Storage injection:</strong></para>
/// <para>
/// Concrete implementations that sample from storage also implement the internal
/// <c>IStorageAwareEvictionSelector&lt;TRange, TData&gt;</c> interface, which provides the
/// <c>Initialize(ISegmentStorage)</c> post-construction injection point. The composition root
/// (<see cref="Public.Cache.VisitedPlacesCache{TRange,TData,TDomain}"/>) casts to that
/// internal interface to inject storage after it is created.
/// <c>Initialize</c> is intentionally absent from this public interface because
/// <c>ISegmentStorage</c> is an internal type.
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
{    /// <summary>
     /// Selects a single eviction candidate by randomly sampling segments from storage
     /// and returning the worst according to this selector's strategy.
     /// </summary>
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
     /// The selector calls <see cref="ISegmentStorage{TRange,TData}.GetRandomSegment"/> up to
     /// <c>SampleSize</c> times, skipping segments that are in <paramref name="immuneSegments"/>.
     /// </para>
     /// </remarks>
    bool TrySelectCandidate(
        IReadOnlySet<CachedSegment<TRange, TData>> immuneSegments,
        out CachedSegment<TRange, TData> candidate);

    /// <summary>
    /// Attaches selector-specific metadata to a newly stored segment.
    /// Called by <c>CacheNormalizationExecutor</c> immediately after each segment is added to storage.
    /// </summary>
    /// <param name="segment">The newly stored segment to initialize metadata for.</param>
    /// <remarks>
    /// Selectors that require no metadata (e.g., <c>SmallestFirstEvictionSelector</c>)
    /// implement this as a no-op and leave <see cref="CachedSegment{TRange,TData}.EvictionMetadata"/> null.
    /// Time-aware selectors (e.g., <c>LruEvictionSelector</c>, <c>FifoEvictionSelector</c>) obtain
    /// the current timestamp internally via an injected <see cref="TimeProvider"/>.
    /// </remarks>
    void InitializeMetadata(CachedSegment<TRange, TData> segment);

    /// <summary>
    /// Updates selector-specific metadata on segments that were accessed on the User Path.
    /// Called by <c>CacheNormalizationExecutor</c> in Step 1 of each background request cycle.
    /// </summary>
    /// <param name="usedSegments">The segments that were read during the User Path request.</param>
    /// <remarks>
    /// Selectors whose metadata is immutable after creation (e.g., <c>FifoEvictionSelector</c>)
    /// implement this as a no-op. Selectors that track access time (e.g., <c>LruEvictionSelector</c>)
    /// update <c>LastAccessedAt</c> on each segment's metadata using an injected
    /// <see cref="TimeProvider"/>.
    /// </remarks>
    void UpdateMetadata(IReadOnlyList<CachedSegment<TRange, TData>> usedSegments);
}
