using Intervals.NET.Extensions;
using Intervals.NET.Domain.Abstractions;
using Intervals.NET.Caching.Dto;
using Intervals.NET.Caching.Extensions;
using Intervals.NET.Caching.Infrastructure.Scheduling;
using Intervals.NET.Caching.VisitedPlaces.Infrastructure.Storage;
using Intervals.NET.Caching.VisitedPlaces.Public.Instrumentation;
using Intervals.NET.Data.Extensions;

namespace Intervals.NET.Caching.VisitedPlaces.Core.UserPath;

/// <summary>
/// Handles user requests on the User Path: reads cached segments, computes gaps, fetches missing
/// data from <c>IDataSource</c>, assembles the result, and publishes a
/// <see cref="CacheNormalizationRequest{TRange,TData}"/> (fire-and-forget) for the Background Storage Loop.
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <typeparam name="TDomain">The type representing the range domain.</typeparam>
/// <remarks>
/// <para><strong>Execution Context:</strong> User Thread</para>
/// <para><strong>Critical Contract — User Path is READ-ONLY (Invariant VPC.A.10):</strong></para>
/// <para>
/// This handler NEVER mutates <see cref="ISegmentStorage{TRange,TData}"/>. All cache writes are
/// performed exclusively by the Background Storage Loop (single writer).
/// </para>
/// <para><strong>Responsibilities:</strong></para>
/// <list type="bullet">
/// <item><description>Read intersecting segments from storage</description></item>
/// <item><description>Compute coverage gaps within the requested range</description></item>
/// <item><description>Fetch gap data from <c>IDataSource</c> (User Path — inline, synchronous w.r.t. the request)</description></item>
/// <item><description>Assemble and return a <see cref="RangeResult{TRange,TData}"/></description></item>
/// <item><description>Publish a <see cref="CacheNormalizationRequest{TRange,TData}"/> (fire-and-forget)</description></item>
/// </list>
/// </remarks>
internal sealed class UserRequestHandler<TRange, TData, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private readonly ISegmentStorage<TRange, TData> _storage;
    private readonly IDataSource<TRange, TData> _dataSource;
    private readonly IWorkScheduler<CacheNormalizationRequest<TRange, TData>> _scheduler;
    private readonly IVisitedPlacesCacheDiagnostics _diagnostics;
    private readonly TDomain _domain;

    // Disposal state: 0 = active, 1 = disposed
    private int _disposeState;

    /// <summary>
    /// Initializes a new <see cref="UserRequestHandler{TRange,TData,TDomain}"/>.
    /// </summary>
    public UserRequestHandler(
        ISegmentStorage<TRange, TData> storage,
        IDataSource<TRange, TData> dataSource,
        IWorkScheduler<CacheNormalizationRequest<TRange, TData>> scheduler,
        IVisitedPlacesCacheDiagnostics diagnostics,
        TDomain domain)
    {
        _storage = storage;
        _dataSource = dataSource;
        _scheduler = scheduler;
        _diagnostics = diagnostics;
        _domain = domain;
    }

    /// <summary>
    /// Handles a user request for the specified range.
    /// </summary>
    /// <param name="requestedRange">The range requested by the user.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
    /// <returns>
    /// A <see cref="ValueTask{T}"/> containing the assembled <see cref="RangeResult{TRange,TData}"/>.
    /// </returns>
    /// <remarks>
    /// <para><strong>Algorithm:</strong></para>
    /// <list type="number">
    /// <item><description>Find intersecting segments via <c>storage.FindIntersecting</c></description></item>
    /// <item><description>Compute gaps (sub-ranges not covered by any hitting segment)</description></item>
    /// <item><description>Determine scenario: FullHit (no gaps), FullMiss (no segments hit), or PartialHit (some gaps)</description></item>
    /// <item><description>Fetch gap data from IDataSource (FullMiss / PartialHit)</description></item>
    /// <item><description>Assemble result data from segments and/or fetched chunks</description></item>
    /// <item><description>Increment activity counter (S.H.1), publish CacheNormalizationRequest (fire-and-forget)</description></item>
    /// <item><description>Return RangeResult immediately</description></item>
    /// </list>
    /// </remarks>
    public async ValueTask<RangeResult<TRange, TData>> HandleRequestAsync(
        Range<TRange> requestedRange,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposeState) != 0)
        {
            throw new ObjectDisposedException(
                nameof(UserRequestHandler<TRange, TData, TDomain>),
                "Cannot handle request on a disposed handler.");
        }

        // Step 1: Read intersecting segments (read-only, Invariant VPC.A.10).
        var hittingSegments = _storage.FindIntersecting(requestedRange);

        // Step 2: Compute coverage gaps.
        var gaps = ComputeGaps(requestedRange, hittingSegments);

        CacheInteraction cacheInteraction;
        IReadOnlyList<RangeChunk<TRange, TData>>? fetchedChunks;
        ReadOnlyMemory<TData> resultData;
        Range<TRange>? actualRange;

        if (gaps.Count == 0 && hittingSegments.Count > 0)
        {
            // Full Hit: entire requested range is covered by cached segments.
            cacheInteraction = CacheInteraction.FullHit;
            _diagnostics.UserRequestFullCacheHit();

            resultData = AssembleFromSegments(requestedRange, hittingSegments, _domain);
            actualRange = requestedRange;
            fetchedChunks = null; // Signal to background: no new data to store
        }
        else if (hittingSegments.Count == 0)
        {
            // Full Miss: no cached data at all for this range.
            cacheInteraction = CacheInteraction.FullMiss;
            _diagnostics.UserRequestFullCacheMiss();

            var chunk = await _dataSource.FetchAsync(requestedRange, cancellationToken)
                .ConfigureAwait(false);

            _diagnostics.DataSourceFetchGap();

            fetchedChunks = [chunk];
            actualRange = chunk.Range;
            resultData = chunk.Range.HasValue
                ? MaterialiseData(chunk.Data)
                : ReadOnlyMemory<TData>.Empty;
        }
        else
        {
            // Partial Hit: some cached data, some gaps to fill.
            cacheInteraction = CacheInteraction.PartialHit;
            _diagnostics.UserRequestPartialCacheHit();

            // Fetch all gaps from IDataSource.
            var chunks = await _dataSource.FetchAsync(gaps, cancellationToken)
                .ConfigureAwait(false);

            fetchedChunks = [.. chunks];

            // Fire one diagnostic event per gap fetched.
            for (var i = 0; i < gaps.Count; i++)
            {
                _diagnostics.DataSourceFetchGap();
            }

            // Assemble result from cached segments + fetched chunks.
            (resultData, actualRange) = AssembleMixed(requestedRange, hittingSegments, fetchedChunks, _domain);
        }

        // Step 6: Publish CacheNormalizationRequest and await the enqueue (preserves activity counter correctness).
        // Awaiting PublishWorkItemAsync only waits for the channel enqueue — not background processing —
        // so fire-and-forget semantics are preserved. The background loop handles processing asynchronously.
        var request = new CacheNormalizationRequest<TRange, TData>(
            requestedRange,
            hittingSegments,
            fetchedChunks);

        await _scheduler.PublishWorkItemAsync(request, cancellationToken)
            .ConfigureAwait(false);

        _diagnostics.UserRequestServed();

        return new RangeResult<TRange, TData>(actualRange, resultData, cacheInteraction);
    }

    /// <summary>
    /// Disposes the handler and shuts down the background scheduler.
    /// </summary>
    internal async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposeState, 1, 0) != 0)
        {
            return; // Already disposed
        }

        await _scheduler.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Computes the gaps in <paramref name="requestedRange"/> not covered by
    /// <paramref name="hittingSegments"/>.
    /// </summary>
    private static IReadOnlyList<Range<TRange>> ComputeGaps(
        Range<TRange> requestedRange,
        IReadOnlyList<CachedSegment<TRange, TData>> hittingSegments)
    {
        if (hittingSegments.Count == 0)
        {
            return [requestedRange];
        }

        IEnumerable<Range<TRange>> remaining = [requestedRange];

        // Iteratively subtract each hitting segment's range from the remaining uncovered ranges.
        // The complexity is O(n*m) where n is the number of hitting segments
        // and m is the number of remaining ranges at each step,
        // but in practice m should be small (often 1) due to the nature of typical cache hits.
        foreach (var seg in hittingSegments)
        {
            var segRange = seg.Range;
            remaining = remaining.SelectMany(r =>
            {
                var intersection = r.Intersect(segRange);
                return intersection.HasValue ? r.Except(intersection.Value) : [r];
            });
        }

        return [..remaining];
    }

    /// <summary>
    /// Assembles result data for a full-hit scenario from the hitting segments.
    /// </summary>
    private static ReadOnlyMemory<TData> AssembleFromSegments(
        Range<TRange> requestedRange,
        IReadOnlyList<CachedSegment<TRange, TData>> segments,
        TDomain domain)
    {
        // Collect all data pieces within the requested range.
        var pieces = new List<ReadOnlyMemory<TData>>();
        var totalLength = 0;

        var sorted = segments.OrderBy(s => s.Range.Start.Value);

        foreach (var seg in sorted)
        {
            // Compute intersection of this segment with the requested range.
            var intersection = seg.Range.Intersect(requestedRange);
            if (!intersection.HasValue)
            {
                continue;
            }

            // Slice the segment data to the intersection.
            var slice = SliceSegment(seg, intersection.Value, domain);
            pieces.Add(slice);
            totalLength += slice.Length;
        }

        return ConcatenateMemory(pieces, totalLength);
    }

    /// <summary>
    /// Assembles result data for a partial-hit scenario from segments and fetched chunks.
    /// Returns the assembled data and the actual available range.
    /// </summary>
    /// TODO: looks like this method is redundant and actually does the same as AssembleFromSegments, think about getting rid of it
    private static (ReadOnlyMemory<TData> Data, Range<TRange>? ActualRange) AssembleMixed(
        Range<TRange> requestedRange,
        IReadOnlyList<CachedSegment<TRange, TData>> segments,
        IReadOnlyList<RangeChunk<TRange, TData>> fetchedChunks,
        TDomain domain)
    {
        // Build a list of (rangeStart, data) pairs covering what we have.
        var pieces = new List<(TRange Start, ReadOnlyMemory<TData> Data)>();

        foreach (var seg in segments)
        {
            var intersection = seg.Range.Intersect(requestedRange);
            if (!intersection.HasValue)
            {
                continue;
            }

            var slice = SliceSegment(seg, intersection.Value, domain);
            pieces.Add((intersection.Value.Start.Value, slice));
        }

        foreach (var chunk in fetchedChunks)
        {
            var intersection = chunk.Range?.Intersect(requestedRange);
            if (!intersection.HasValue)
            {
                continue;
            }

            // Wrap as lazy RangeData, slice in domain space, then materialize only the needed portion.
            // This avoids allocating a full-size backing array and immediately narrowing it —
            // the materialized array is exactly the size of the intersection.
            var rangeData = chunk.Data.ToRangeData(chunk.Range!.Value, domain);
            var sliced = rangeData[intersection.Value];
            var slicedChunkData = MaterialiseData(sliced.Data);
            pieces.Add((intersection.Value.Start.Value, slicedChunkData));
        }

        if (pieces.Count == 0)
        {
            return (ReadOnlyMemory<TData>.Empty, null);
        }

        // Sort pieces by start and concatenate.
        pieces.Sort(static (a, b) => a.Start.CompareTo(b.Start));

        var totalLength = pieces.Sum(p => p.Data.Length);
        var assembled = ConcatenateMemory(pieces.Select(p => p.Data), totalLength);

        // Determine actual range: from requestedRange.Start to requestedRange.End
        // (bounded by what we actually assembled — use requestedRange as approximation).
        return (assembled, requestedRange);
    }

    /// <summary>
    /// Slices a cached segment's data to the specified intersection range using domain-aware span computation.
    /// </summary>
    private static ReadOnlyMemory<TData> SliceSegment(
        CachedSegment<TRange, TData> segment,
        Range<TRange> intersection,
        TDomain domain)
    {
        // Compute element offset from segment start to intersection start.
        var offsetInSegment = (int)ComputeSpan(segment.Range.Start.Value, intersection.Start.Value, domain);
        // Compute the number of elements in the intersection.
        var sliceLength = (int)intersection.Span(domain).Value;

        // Guard against out-of-range slicing (defensive).
        var availableLength = segment.Data.Length - offsetInSegment;
        if (offsetInSegment >= segment.Data.Length || availableLength <= 0)
        {
            return ReadOnlyMemory<TData>.Empty;
        }

        return segment.Data.Slice(offsetInSegment, Math.Min(sliceLength, availableLength));
    }

    /// <summary>
    /// Computes the number of discrete domain elements between <paramref name="from"/> (inclusive)
    /// and <paramref name="to"/> (exclusive) using <see cref="IRangeDomain{T}.Distance"/>.
    /// Returns 0 when <paramref name="from"/> equals <paramref name="to"/>.
    /// </summary>
    private static long ComputeSpan(TRange from, TRange to, TDomain domain)
    {
        if (from.CompareTo(to) == 0)
        {
            return 0;
        }

        return domain.Distance(from, to);
    }

    private static ReadOnlyMemory<TData> MaterialiseData(IEnumerable<TData> data)
        => new(data.ToArray());

    private static ReadOnlyMemory<TData> ConcatenateMemory(
        IEnumerable<ReadOnlyMemory<TData>> pieces,
        int totalLength)
    {
        using var enumerator = pieces.GetEnumerator();

        if (!enumerator.MoveNext())
        {
            return ReadOnlyMemory<TData>.Empty;
        }

        var first = enumerator.Current;

        if (!enumerator.MoveNext())
        {
            return first;
        }

        var result = new TData[totalLength];
        var offset = 0;

        first.Span.CopyTo(result.AsSpan(offset));
        offset += first.Length;

        do
        {
            var piece = enumerator.Current;
            piece.Span.CopyTo(result.AsSpan(offset));
            offset += piece.Length;
        }
        while (enumerator.MoveNext());

        return result;
    }
}