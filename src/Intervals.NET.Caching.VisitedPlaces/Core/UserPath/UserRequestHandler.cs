using Intervals.NET.Extensions;
using Intervals.NET.Domain.Abstractions;
using Intervals.NET.Caching.Dto;
using Intervals.NET.Caching.Extensions;
using Intervals.NET.Caching.Infrastructure.Scheduling;
using Intervals.NET.Caching.VisitedPlaces.Infrastructure.Storage;
using Intervals.NET.Caching.VisitedPlaces.Public.Instrumentation;

namespace Intervals.NET.Caching.VisitedPlaces.Core.UserPath;

/// <summary>
/// Handles user requests on the User Path: reads cached segments, computes gaps, fetches missing
/// data from <c>IDataSource</c>, assembles the result, and publishes a
/// <see cref="BackgroundEvent{TRange,TData}"/> (fire-and-forget) for the Background Storage Loop.
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
/// <item><description>Publish a <see cref="BackgroundEvent{TRange,TData}"/> (fire-and-forget)</description></item>
/// </list>
/// </remarks>
internal sealed class UserRequestHandler<TRange, TData, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private readonly ISegmentStorage<TRange, TData> _storage;
    private readonly IDataSource<TRange, TData> _dataSource;
    private readonly IWorkScheduler<BackgroundEvent<TRange, TData>> _scheduler;
    private readonly ICacheDiagnostics _diagnostics;
    private readonly TDomain _domain;

    // Disposal state: 0 = active, 1 = disposed
    private int _disposeState;

    /// <summary>
    /// Initializes a new <see cref="UserRequestHandler{TRange,TData,TDomain}"/>.
    /// </summary>
    public UserRequestHandler(
        ISegmentStorage<TRange, TData> storage,
        IDataSource<TRange, TData> dataSource,
        IWorkScheduler<BackgroundEvent<TRange, TData>> scheduler,
        ICacheDiagnostics diagnostics,
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
    /// <item><description>Increment activity counter (S.H.1), publish BackgroundEvent (fire-and-forget)</description></item>
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

        _diagnostics.UserRequestServed(); // todo this event must be at the very end accordingly to the name - served, means all the work in user path is done

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
            _diagnostics.DataSourceFetchGap();

            var chunk = await _dataSource.FetchAsync(requestedRange, cancellationToken)
                .ConfigureAwait(false);

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
            _diagnostics.DataSourceFetchGap(); // todo: looks like this diagnostic is not so precise.

            fetchedChunks = [..chunks];

            // Assemble result from cached segments + fetched chunks.
            (resultData, actualRange) = AssembleMixed(requestedRange, hittingSegments, fetchedChunks, _domain);
        }

        // Step 6: Publish BackgroundEvent (fire-and-forget).
        // NOTE: The scheduler (ChannelBasedWorkScheduler) increments the activity counter
        // inside PublishWorkItemAsync before enqueuing — we must NOT increment it here too.
        var backgroundEvent = new BackgroundEvent<TRange, TData>(
            requestedRange,
            hittingSegments,
            fetchedChunks);

        // Fire-and-forget: we do not await the scheduler. The background loop handles it.
        // The scheduler's PublishWorkItemAsync is ValueTask-returning; we discard the result
        // intentionally. Any scheduling failure is handled inside the scheduler infrastructure.
        // TODO: we have to await this call - see SWC implementation for example. This doesn't break fire and forget - this allows to make it work properly.
        _ = _scheduler.PublishWorkItemAsync(backgroundEvent, cancellationToken)
            .AsTask()
            .ContinueWith(
                static t =>
                {
                    // Swallow scheduling exceptions to avoid unobserved task exceptions.
                    // The scheduler's WorkFailed diagnostic will have already fired.
                    _ = t.Exception;
                },
                TaskContinuationOptions.OnlyOnFaulted);

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
    /// <paramref name="hittingSegments"/> (sorted ascending by range start).
    /// </summary>
    /// TODO try to refactor this method in a way to avoid temp list or array allocations - utilize IEnumerable where possible
    private static List<Range<TRange>> ComputeGaps(
        Range<TRange> requestedRange,
        IReadOnlyList<CachedSegment<TRange, TData>> hittingSegments)
    {
        var gaps = new List<Range<TRange>>();

        if (hittingSegments.Count == 0)
        {
            // Full miss — the whole requested range is a gap.
            gaps.Add(requestedRange);
            return gaps;
        }

        // Sort segments by start value for gap computation.
        var sorted = hittingSegments
            .OrderBy(s => s.Range.Start.Value)
            .ToList();

        var cursor = requestedRange.Start.Value;
        var requestEnd = requestedRange.End.Value;

        // TODO reconsider the gap calculation logic - I guess we can utilize the Intervals.NET's extensions for Range<T> to get except ranges (.Except() method).
        foreach (var seg in sorted)
        {
            var segStart = seg.Range.Start.Value;
            var segEnd = seg.Range.End.Value;

            // If the segment starts after the cursor, there's a gap before it.
            if (segStart.CompareTo(cursor) > 0)
            {
                // Gap from cursor to segment start (exclusive).
                gaps.Add(Factories.Range.Closed<TRange>(cursor, Predecessor(segStart)));
            }

            // Advance cursor past this segment.
            if (segEnd.CompareTo(cursor) > 0)
            {
                cursor = Successor(segEnd);
            }

            // Short-circuit: if cursor is past request end, we're done.
            if (cursor.CompareTo(requestEnd) > 0)
            {
                break;
            }
        }

        // Trailing gap: if cursor hasn't reached request end yet.
        if (cursor.CompareTo(requestEnd) <= 0)
        {
            gaps.Add(Factories.Range.Closed<TRange>(cursor, requestEnd));
        }

        return gaps;
    }

    /// <summary>
    /// Assembles result data for a full-hit scenario from the hitting segments.
    /// </summary>
    /// TODO: refactor this method to avoid temp list allocations - utilize IEnumerable where possible and do not materialize the whole list of pieces in memory before concatenation, but rather concatenate on the fly while enumerating segments
    private static ReadOnlyMemory<TData> AssembleFromSegments(
        Range<TRange> requestedRange,
        IReadOnlyList<CachedSegment<TRange, TData>> segments,
        TDomain domain)
    {
        // Collect all data pieces within the requested range.
        var pieces = new List<ReadOnlyMemory<TData>>();
        var totalLength = 0;

        var sorted = segments.OrderBy(s => s.Range.Start.Value).ToList();

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
            if (!chunk.Range.HasValue)
            {
                continue;
            }

            var intersection = chunk.Range.Value.Intersect(requestedRange);
            if (!intersection.HasValue)
            {
                continue;
            }

            var chunkData = MaterialiseData(chunk.Data);
            // Slice the chunk data to the intersection within the chunk's range.
            var offsetInChunk = (int)ComputeSpan(chunk.Range.Value.Start.Value, intersection.Value.Start.Value, chunk.Range.Value, domain);
            var sliceLength = (int)intersection.Value.Span(domain).Value;
            var slicedChunkData = chunkData.Slice(offsetInChunk, Math.Min(sliceLength, chunkData.Length - offsetInChunk));
            pieces.Add((intersection.Value.Start.Value, slicedChunkData));
        }

        if (pieces.Count == 0)
        {
            return (ReadOnlyMemory<TData>.Empty, null);
        }

        // Sort pieces by start and concatenate.
        pieces.Sort(static (a, b) => a.Start.CompareTo(b.Start));

        var totalLength = pieces.Sum(p => p.Data.Length);
        var assembled = ConcatenateMemory(pieces.Select(p => p.Data).ToList(), totalLength);

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
        var offsetInSegment = (int)ComputeSpan(segment.Range.Start.Value, intersection.Start.Value, segment.Range, domain);
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
    /// Computes the number of discrete domain elements between <paramref name="from"/> and
    /// <paramref name="to"/> (exclusive of <paramref name="to"/>), where both values are inclusive
    /// boundaries within <paramref name="contextRange"/>.
    /// Returns 0 when <paramref name="from"/> equals <paramref name="to"/>.
    /// </summary>
    private static long ComputeSpan(TRange from, TRange to, Range<TRange> contextRange, TDomain domain)
    {
        if (from.CompareTo(to) == 0)
        {
            return 0;
        }

        // Build a half-open range [from, to) using the same inclusivity as contextRange.Start.
        // Since our segments/intersections always use closed ranges (both ends inclusive),
        // we can compute span([from, predecessor(to)]) = span of closed range from..to-1.
        var subRange = Factories.Range.Closed<TRange>(from, Predecessor(to));
        return subRange.Span(domain).Value;
    }

    private static ReadOnlyMemory<TData> MaterialiseData(IEnumerable<TData> data)
        => new(data.ToArray());

    private static ReadOnlyMemory<TData> ConcatenateMemory(
        IList<ReadOnlyMemory<TData>> pieces,
        int totalLength)
    {
        if (pieces.Count == 0)
        {
            return ReadOnlyMemory<TData>.Empty;
        }

        if (pieces.Count == 1)
        {
            return pieces[0];
        }

        var result = new TData[totalLength];
        var offset = 0;

        foreach (var piece in pieces)
        {
            piece.Span.CopyTo(result.AsSpan(offset));
            offset += piece.Length;
        }

        return result;
    }

    /// <summary>Returns the immediate predecessor of a range value.</summary>
    /// <remarks>
    /// This is a best-effort generic predecessor. For integer domains, uses the int predecessor.
    /// For other types, returns the same value (gap boundary is inclusive).
    /// </remarks>
    /// TODO: this is very strange method - it must not exist at all.
    private static TRange Predecessor(TRange value)
    {
        if (value is int i)
        {
            return (TRange)(object)(i - 1);
        }

        return value;
    }

    /// <summary>Returns the immediate successor of a range value.</summary>
    /// /// TODO: this is very strange method - it must not exist at all.
    private static TRange Successor(TRange value)
    {
        if (value is int i)
        {
            return (TRange)(object)(i + 1);
        }

        return value;
    }
}
