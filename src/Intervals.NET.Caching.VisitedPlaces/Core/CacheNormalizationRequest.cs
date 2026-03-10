using Intervals.NET.Caching.Dto;
using Intervals.NET.Caching.Infrastructure.Scheduling;

namespace Intervals.NET.Caching.VisitedPlaces.Core;

/// <summary>
/// Represents a unit of work published to the Background Storage Loop after a user request
/// completes. Carries the access statistics and any freshly-fetched data to be stored.
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <remarks>
/// <para><strong>Execution Context:</strong></para>
/// <para>
/// Created on the User Path; processed on the Background Storage Loop (single writer).
/// </para>
/// <para><strong>Payload semantics:</strong></para>
/// <list type="bullet">
/// <item><description>
///   <see cref="UsedSegments"/> — segments that were read from the cache on the User Path
///   (empty on a full miss). Used by the executor to update statistics (step 1).
/// </description></item>
/// <item><description>
///   <see cref="FetchedChunks"/> — data freshly fetched from <c>IDataSource</c> (null on a
///   full hit). Each chunk with a non-null <see cref="RangeChunk{TRange,TData}.Range"/> is
///   stored as a new <see cref="CachedSegment{TRange,TData}"/> (step 2).
/// </description></item>
/// <item><description>
///   <see cref="RequestedRange"/> — the original range the user requested. Used for diagnostic
///   and tracing purposes.
/// </description></item>
/// </list>
/// <para><strong>Cancellation (Invariant VPC.A.11):</strong></para>
/// <para>
/// CacheNormalizationRequests are NEVER cancelled — the FIFO queue processes all requests regardless of
/// order. <see cref="Cancel"/> is a no-op and <see cref="CancellationToken"/> is always
/// <see cref="CancellationToken.None"/>.
/// </para>
/// </remarks>
internal sealed class CacheNormalizationRequest<TRange, TData> : ISchedulableWorkItem
    where TRange : IComparable<TRange>
{
    /// <summary>The original range requested by the user on the User Path.</summary>
    public Range<TRange> RequestedRange { get; }

    /// <summary>
    /// Segments that were served from the cache on the User Path.
    /// Empty when the request was a full miss (no cache hit at all).
    /// Used by the executor to update statistics in Background Path step 1.
    /// </summary>
    public IReadOnlyList<CachedSegment<TRange, TData>> UsedSegments { get; }

    /// <summary>
    /// Data freshly fetched from <c>IDataSource</c> to fill gaps in the cache.
    /// <see langword="null"/> when the request was a full cache hit (no data source call needed).
    /// Each non-null <see cref="RangeChunk{TRange,TData}.Range"/> entry is stored as a new segment
    /// in Background Path step 2.
    /// </summary>
    /// <remarks>
    /// Typed as <see cref="IEnumerable{T}"/> rather than <see cref="IReadOnlyList{T}"/> because the
    /// executor only needs a single forward pass (<c>foreach</c>). This allows the User Path to pass
    /// the materialized chunks array directly without an extra wrapper allocation.
    /// </remarks>
    public IEnumerable<RangeChunk<TRange, TData>>? FetchedChunks { get; }

    /// <summary>
    /// Initializes a new <see cref="CacheNormalizationRequest{TRange,TData}"/>.
    /// </summary>
    /// <param name="requestedRange">The range the user requested.</param>
    /// <param name="usedSegments">Segments read from the cache on the User Path.</param>
    /// <param name="fetchedChunks">Data fetched from IDataSource; null on a full cache hit.</param>
    internal CacheNormalizationRequest(
        Range<TRange> requestedRange,
        IReadOnlyList<CachedSegment<TRange, TData>> usedSegments,
        IEnumerable<RangeChunk<TRange, TData>>? fetchedChunks)
    {
        RequestedRange = requestedRange;
        UsedSegments = usedSegments;
        FetchedChunks = fetchedChunks;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Always <see cref="CancellationToken.None"/>. CacheNormalizationRequests are never cancelled
    /// (Invariant VPC.A.11: FIFO queue, no supersession).
    /// </remarks>
    public CancellationToken CancellationToken => CancellationToken.None;

    /// <inheritdoc/>
    /// <remarks>
    /// No-op: CacheNormalizationRequests are never cancelled (Invariant VPC.A.11).
    /// </remarks>
    public void Cancel() { }

    /// <inheritdoc/>
    /// <remarks>
    /// No-op: CacheNormalizationRequests own no disposable resources.
    /// </remarks>
    public void Dispose() { }
}
