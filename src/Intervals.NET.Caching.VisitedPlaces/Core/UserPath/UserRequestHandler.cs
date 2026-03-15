using System.Buffers;
using Intervals.NET.Extensions;
using Intervals.NET.Domain.Abstractions;
using Intervals.NET.Caching.Dto;
using Intervals.NET.Caching.Extensions;
using Intervals.NET.Caching.Infrastructure;
using Intervals.NET.Caching.Infrastructure.Scheduling;
using Intervals.NET.Caching.VisitedPlaces.Infrastructure.Storage;
using Intervals.NET.Caching.VisitedPlaces.Public.Instrumentation;
using Intervals.NET.Data;
using Intervals.NET.Data.Extensions;

namespace Intervals.NET.Caching.VisitedPlaces.Core.UserPath;

/// <summary>
/// Handles user requests on the User Path: reads cached segments, computes gaps, fetches missing
/// data, assembles the result, and publishes a normalization request for the Background Storage Loop.
/// See docs/visited-places/ for design details.
/// </summary>
internal sealed class UserRequestHandler<TRange, TData, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private readonly ISegmentStorage<TRange, TData> _storage;
    private readonly IDataSource<TRange, TData> _dataSource;
    private readonly ISerialWorkScheduler<CacheNormalizationRequest<TRange, TData>> _scheduler;
    private readonly IVisitedPlacesCacheDiagnostics _diagnostics;
    private readonly TDomain _domain;

    // Disposal state: 0 = active, 1 = disposed
    private int _disposeState;

    // Cached comparer for sorting RangeData pieces by range start in Assemble.
    // Static readonly ensures Comparer<T>.Create is called once per closed generic type —
    // no allocation on subsequent sort calls, unlike an inline Comparer<T>.Create(…) which
    // allocates a new ComparisonComparer<T> wrapper on every invocation.
    private static readonly Comparer<RangeData<TRange, TData, TDomain>> PieceComparer =
        Comparer<RangeData<TRange, TData, TDomain>>.Create(
            static (a, b) => a.Range.Start.CompareTo(b.Range.Start));

    /// <summary>
    /// Initializes a new <see cref="UserRequestHandler{TRange,TData,TDomain}"/>.
    /// </summary>
    public UserRequestHandler(
        ISegmentStorage<TRange, TData> storage,
        IDataSource<TRange, TData> dataSource,
        ISerialWorkScheduler<CacheNormalizationRequest<TRange, TData>> scheduler,
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
        // Architecturally irreducible allocation: RCU snapshot must be stable across the User Path
        // (Invariant VPC.B.5) and crosses thread boundary to background via CacheNormalizationRequest.
        var hittingSegments = _storage.FindIntersecting(requestedRange);

        CacheInteraction cacheInteraction;
        IReadOnlyList<RangeChunk<TRange, TData>>? fetchedChunks;
        ReadOnlyMemory<TData> resultData;
        Range<TRange>? actualRange;

        if (hittingSegments.Count == 0)
        {
            // Full Miss: no cached data at all for this range.
            // ComputeGaps is never called — skips its allocation entirely.
            cacheInteraction = CacheInteraction.FullMiss;
            _diagnostics.UserRequestFullCacheMiss();

            var chunk = await _dataSource.FetchAsync(requestedRange, cancellationToken)
                .ConfigureAwait(false);

            _diagnostics.DataSourceFetchGap();

            // [chunk] compiles to a <> z__ReadOnlyList wrapper (single-field, no array) — cheapest possible.
            fetchedChunks = [chunk];
            actualRange = chunk.Range;
            resultData = chunk.Range.HasValue
                ? new ReadOnlyMemory<TData>(chunk.Data.ToArray()) // irreducible: result array for caller
                : ReadOnlyMemory<TData>.Empty;
        }
        else
        {
            // At least one segment hit: map segments to RangeData.
            // Plain heap allocation — in the typical case (1–2 hitting segments) the array is tiny
            // and short-lived (Gen0). ArrayPool would add rental/return overhead and per-closed-generic
            // pool fragmentation with no structural benefit at this scale. If benchmarks reveal
            // pressure at very large segment counts, introduce a threshold-switched buffer type then.
            var hittingRangeData = new RangeData<TRange, TData, TDomain>[hittingSegments.Count];

            // Step 2: Map segments to RangeData — zero-copy via ReadOnlyMemoryEnumerable.
            var hittingCount = 0;
            foreach (var s in hittingSegments)
            {
                hittingRangeData[hittingCount++] =
                    new ReadOnlyMemoryEnumerable<TData>(s.Data).ToRangeData(s.Range, _domain);
            }

            // Step 3: Probe for coverage gaps using a single enumerator — no array allocation.
            // MoveNext() is called once here; if there is at least one gap the same enumerator
            // (with Current already set to the first gap) is resumed inside PrependAndResume,
            // so the chain is walked exactly once across both the probe and the fetch.
            using var gapsEnumerator = ComputeGaps(requestedRange, hittingSegments).GetEnumerator();

            if (!gapsEnumerator.MoveNext())
            {
                // Full Hit: entire requested range is covered by cached segments.
                cacheInteraction = CacheInteraction.FullHit;
                _diagnostics.UserRequestFullCacheHit();

                (resultData, actualRange) = Assemble(requestedRange, hittingRangeData, hittingCount);
                fetchedChunks = null; // Signal to background: no new data to store
            }
            else
            {
                // Partial Hit: some cached data, some gaps to fill.
                cacheInteraction = CacheInteraction.PartialHit;
                _diagnostics.UserRequestPartialCacheHit();

                // Fetch all gaps from IDataSource.
                // PrependAndResume yields gapsEnumerator.Current first, then resumes MoveNext —
                // the chain is never re-evaluated; FetchAsync walks it in one forward pass.
                // Materialize once: chunks array is used both for RangeData mapping below
                // and passed to CacheNormalizationRequest for the background path.
                // .ToArray() uses SegmentedArrayBuilder internally — 1 allocation.
                var chunksArray = (await _dataSource.FetchAsync(
                        PrependAndResume(gapsEnumerator.Current, gapsEnumerator), cancellationToken)
                    .ConfigureAwait(false)).ToArray();

                // Build merged sources (hittingRangeData + fetched chunks) in a single array.
                // Same rationale as hittingRangeData: plain allocation, typical count is small.
                var merged = new RangeData<TRange, TData, TDomain>[hittingCount + chunksArray.Length];

                // Copy hitting segments (already mapped to RangeData).
                Array.Copy(hittingRangeData, merged, hittingCount);
                var mergedCount = hittingCount;

                // Map fetched chunks to RangeData, append valid ones, and fire the diagnostic
                // per chunk — one pass serves both purposes, no separate iteration needed.
                foreach (var c in chunksArray)
                {
                    _diagnostics.DataSourceFetchGap();
                    if (c.Range.HasValue)
                    {
                        merged[mergedCount++] = c.Data.ToRangeData(c.Range!.Value, _domain);
                    }
                }

                (resultData, actualRange) = Assemble(requestedRange, merged, mergedCount);

                // Pass chunks array directly as IEnumerable — no wrapper needed.
                fetchedChunks = chunksArray;
            }
        }

        // Step 7: Publish CacheNormalizationRequest and await the enqueue (preserves activity counter correctness).
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
    /// Yields <paramref name="first"/> followed by the remaining elements of <paramref name="enumerator"/>.
    /// </summary>
    private static IEnumerable<Range<TRange>> PrependAndResume(
        Range<TRange> first,
        IEnumerator<Range<TRange>> enumerator)
    {
        yield return first;
        while (enumerator.MoveNext())
        {
            yield return enumerator.Current;
        }
    }

    /// <summary>
    /// Lazily computes the gaps in <paramref name="requestedRange"/> not covered by
    /// <paramref name="hittingSegments"/>, filtered to only real (non-empty) gaps in the domain.
    /// </summary>
    private IEnumerable<Range<TRange>> ComputeGaps(
        Range<TRange> requestedRange,
        IReadOnlyList<CachedSegment<TRange, TData>> hittingSegments)
    {
        // Caller guarantees hittingSegments.Count > 0 (Full Miss is handled before ComputeGaps).
        IEnumerable<Range<TRange>> remaining = [requestedRange];

        // Iteratively subtract each hitting segment's range from the remaining uncovered ranges.
        // The complexity is O(n*m) where n is the number of hitting segments
        // and m is the number of remaining ranges at each step,
        // but in practice m should be small (often 1) due to the nature of typical cache hits.
        for (var index = 0; index < hittingSegments.Count; index++)
        {
            var seg = hittingSegments[index];
            remaining = Subtract(remaining, seg.Range);
        }

        // Yield only gaps that contain at least one discrete domain point.
        // Gaps with span == 0 are phantom artifacts of continuous range algebra (e.g., the open
        // interval (9, 10) between adjacent integer segments [0,9] and [10,19]).
        foreach (var gap in remaining)
        {
            var span = gap.Span(_domain);
            if (span is { IsFinite: true, Value: > 0 })
            {
                yield return gap;
            }
        }

        yield break;

        // Static: captures nothing — segRange is passed explicitly, eliminating the closure
        // allocation that a lambda capturing segRange in the loop above would incur.
        static IEnumerable<Range<TRange>> Subtract(
            IEnumerable<Range<TRange>> ranges,
            Range<TRange> segRange)
        {
            foreach (var r in ranges)
            {
                var intersection = r.Intersect(segRange);
                if (intersection.HasValue)
                {
                    foreach (var gap in r.Except(intersection.Value))
                    {
                        yield return gap;
                    }
                }
                else
                {
                    yield return r;
                }
            }
        }
    }

    /// <summary>
    /// Assembles result data from sources clipped to <paramref name="requestedRange"/>.
    /// </summary>
    private static (ReadOnlyMemory<TData> Data, Range<TRange>? ActualRange) Assemble(
        Range<TRange> requestedRange,
        RangeData<TRange, TData, TDomain>[] sources,
        int sourceCount)
    {
        // Rent a working buffer for valid pieces. Returned in the finally block below.
        var piecesPool = ArrayPool<RangeData<TRange, TData, TDomain>>.Shared;
        var pieces = piecesPool.Rent(sourceCount);
        try
        {
            // Pass 1: intersect each source with the requested range, compute per-piece length from
            // domain spans (cheap arithmetic — no enumeration), accumulate total length inline.
            var piecesCount = 0;
            var totalLength = 0L;

            for (var i = 0; i < sourceCount; i++)
            {
                var source = sources[i];
                var intersectionRange = source.Range.Intersect(requestedRange);
                if (!intersectionRange.HasValue)
                {
                    continue;
                }

                var spanRangeValue = intersectionRange.Value.Span(source.Domain);
                if (!spanRangeValue.IsFinite || spanRangeValue.Value <= 0)
                {
                    continue;
                }

                // Slice lazily — no allocation, no enumeration yet.
                var length = spanRangeValue.Value;
                pieces[piecesCount++] = source[intersectionRange.Value];
                totalLength += length;
            }

            // Fast-path
            switch (piecesCount)
            {
                case 0:
                    // No pieces intersect the requested range — return empty result with null range.
                    return (ReadOnlyMemory<TData>.Empty, null);
                case 1:
                    // Single source — enumerate directly into a right-sized array, no extra work.
                    // Irreducible allocation: result array must outlive this method.
                    return (new ReadOnlyMemory<TData>(pieces[0].Data.ToArray()), requestedRange);
            }

            Array.Sort(pieces, 0, piecesCount, PieceComparer);

            // Pass 2: allocate one result array, enumerate each slice directly into it at its offset.
            // No intermediate arrays, no redundant copies.
            // Irreducible allocation: result array must outlive this method.
            var result = new TData[totalLength];
            var offset = 0;

            for (var i = 0; i < piecesCount; i++)
            {
                foreach (var item in pieces[i].Data)
                {
                    result[offset++] = item;
                }
            }

            return (result, requestedRange);
        }
        finally
        {
            // clearArray: true — RangeData is a reference type; stale refs must not linger in the pool.
            piecesPool.Return(pieces, clearArray: true);
        }
    }
}
