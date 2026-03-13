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
/// <para><strong>Allocation strategy:</strong></para>
/// <list type="bullet">
/// <item><description>
///   Working buffers (<c>hittingRangeData</c>, merged sources, <c>pieces</c> in <see cref="Assemble"/>)
///   are rented from <see cref="ArrayPool{T}.Shared"/> and returned in <c>finally</c> blocks.
///   On WASM (single-threaded), pool-hit rate is ~100% with zero contention.
/// </description></item>
/// <item><description>
///   <c>ComputeGaps</c> returns a deferred <see cref="IEnumerable{T}"/>; the caller probes it
///   with a single <c>MoveNext()</c> call. On Partial Hit, <c>PrependAndResume</c> resumes the
///   same enumerator inside <c>FetchAsync</c> — the LINQ chain is walked exactly once, no
///   intermediate array is ever materialized for gaps.
/// </description></item>
/// <item><description>
///   The final result arrays (<see cref="ReadOnlyMemory{T}"/> payload returned to the caller) are
///   irreducible heap allocations — they must outlive this method.
/// </description></item>
/// </list>
/// </remarks>
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
    /// <param name="requestedRange">The range requested by the user.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
    /// <returns>
    /// A <see cref="ValueTask{T}"/> containing the assembled <see cref="RangeResult{TRange,TData}"/>.
    /// </returns>
    /// <remarks>
    /// <para><strong>Algorithm:</strong></para>
    /// <list type="number">
    /// <item><description>Find intersecting segments via <c>storage.FindIntersecting</c></description></item>
    /// <item><description>
    ///   If no segments hit (Full Miss): fetch full range from IDataSource directly — <c>ComputeGaps</c>
    ///   is never called, saving its allocation entirely.
    /// </description></item>
    /// <item><description>
    ///   Otherwise: map segments to <see cref="RangeData{TRangeType,TDataType,TRangeDomain}"/> into a
    ///   pooled buffer, compute gaps, and branch on Full Hit vs Partial Hit.
    /// </description></item>
    /// <item><description>Assemble result data from sources via a pooled buffer</description></item>
    /// <item><description>Publish CacheNormalizationRequest (fire-and-forget)</description></item>
    /// <item><description>Return RangeResult immediately</description></item>
    /// </list>
    /// <para><strong>Allocation profile per scenario:</strong></para>
    /// <list type="bullet">
    /// <item><description><strong>Full Hit:</strong> storage snapshot (irreducible) + result array (irreducible) = 2 allocations</description></item>
    /// <item><description><strong>Full Miss:</strong> storage snapshot + <c>[chunk]</c> wrapper + result data array = 3 allocations</description></item>
    /// <item><description><strong>Partial Hit:</strong> storage snapshot + <c>PrependAndResume</c> state machine + chunks array + result array = 4 allocations</description></item>
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
        // Architecturally irreducible allocation: RCU snapshot must be stable across the User Path
        // (Invariant VPC.B.5) and crosses thread boundary to background via CacheNormalizationRequest.
        var hittingSegments = _storage.FindIntersecting(requestedRange);

        CacheInteraction cacheInteraction;
        IEnumerable<RangeChunk<TRange, TData>>? fetchedChunks;
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
            // At least one segment hit: map segments to RangeData into a pooled buffer.
            // Pool rental: no heap allocation; returned in the finally block below.
            var rangeDataPool = ArrayPool<RangeData<TRange, TData, TDomain>>.Shared;
            var hittingRangeData = rangeDataPool.Rent(hittingSegments.Count);
            try
            {
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
                // so the LINQ chain is walked exactly once across both the probe and the fetch.
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
                    // the LINQ chain is never re-evaluated; FetchAsync walks it in one forward pass.
                    // Materialize once: chunks array is used both for RangeData mapping below
                    // and passed to CacheNormalizationRequest for the background path.
                    // .ToArray() uses SegmentedArrayBuilder internally — 1 allocation.
                    var chunksArray = (await _dataSource.FetchAsync(
                            PrependAndResume(gapsEnumerator.Current, gapsEnumerator), cancellationToken)
                        .ConfigureAwait(false)).ToArray();

                    // Build merged sources (hittingRangeData + chunkRangeData) in a pooled buffer.
                    // Upper bound: hittingCount segments + at most one RangeData per chunk.
                    var mergedPool = ArrayPool<RangeData<TRange, TData, TDomain>>.Shared;
                    var merged = mergedPool.Rent(hittingCount + chunksArray.Length);
                    try
                    {
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
                    }
                    finally
                    {
                        // clearArray: true — RangeData is a reference type; stale refs must not linger.
                        mergedPool.Return(merged, clearArray: true);
                    }

                    // Pass chunks array directly as IEnumerable — no wrapper needed.
                    fetchedChunks = chunksArray;
                }
            }
            finally
            {
                // clearArray: true — RangeData is a reference type; stale refs must not linger.
                rangeDataPool.Return(hittingRangeData, clearArray: true);
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
    /// Yields <paramref name="first"/> followed by the remaining elements of
    /// <paramref name="enumerator"/> (which must have already had <c>MoveNext()</c> called once
    /// and returned <see langword="true"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This allows the caller to use a single <see cref="IEnumerator{T}"/> for both an empty-check
    /// probe (<c>MoveNext()</c> returns <see langword="false"/> → Full Hit) and as the source for
    /// <c>FetchAsync</c> (Partial Hit) — without re-evaluating the upstream LINQ chain or
    /// allocating an intermediate array.
    /// </para>
    /// <para>
    /// The compiler generates a state-machine class for this iterator; that object is
    /// constructed when <see cref="IDataSource{TRange,TData}.FetchAsync(IEnumerable{Range{TRange}},CancellationToken)"/>
    /// calls <c>GetEnumerator()</c> on the returned sequence.
    /// </para>
    /// </remarks>
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
    /// <paramref name="hittingSegments"/>.
    /// </summary>
    /// <returns>
    /// A deferred <see cref="IEnumerable{T}"/> of uncovered sub-ranges. The caller obtains the
    /// enumerator directly via <c>GetEnumerator()</c> and probes with a single <c>MoveNext()</c>
    /// call — no array allocation. On Partial Hit, <see cref="PrependAndResume"/> resumes the
    /// same enumerator so the chain is walked exactly once in total.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Each iteration passes the current <c>remaining</c> sequence and the segment range to the
    /// static local <c>Subtract</c> — no closure is created, eliminating one heap allocation per
    /// hitting segment compared to an equivalent <c>SelectMany</c> lambda.
    /// </para>
    /// </remarks>
    private static IEnumerable<Range<TRange>> ComputeGaps(
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

        return remaining;

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
    /// Assembles result data from a contiguous slice of a <see cref="RangeData{TRangeType,TDataType,TRangeDomain}"/>
    /// buffer (cached segments and/or fetched chunks) clipped to <paramref name="requestedRange"/>.
    /// </summary>
    /// <param name="requestedRange">The range to assemble data for.</param>
    /// <param name="sources">
    /// Buffer containing domain-aware data sources in positions <c>[0..sourceCount)</c>. The buffer
    /// is typically a pooled <see cref="ArrayPool{T}"/> rental — only the first <paramref name="sourceCount"/>
    /// elements are valid; the rest must be ignored.
    /// </param>
    /// <param name="sourceCount">Number of valid entries at the start of <paramref name="sources"/>.</param>
    /// <returns>
    /// The assembled <see cref="ReadOnlyMemory{T}"/> and the actual available range
    /// (<see langword="null"/> when no source intersects <paramref name="requestedRange"/>).
    /// </returns>
    /// <remarks>
    /// <para>
    /// Each source is intersected with <paramref name="requestedRange"/> and sliced lazily in
    /// domain space via the <see cref="RangeData{TRangeType,TDataType,TRangeDomain}"/> indexer.
    /// </para>
    /// <para>
    /// Total length is computed from domain spans (no enumeration required), then a single
    /// result array is allocated and each slice is enumerated directly into it at the correct
    /// offset — one allocation, one pass per source, no intermediate arrays, no redundant copies.
    /// </para>
    /// <para>
    /// The internal <c>pieces</c> working buffer is rented from <see cref="ArrayPool{T}.Shared"/>
    /// and returned before this method exits — no <c>List&lt;T&gt;</c> allocation.
    /// </para>
    /// </remarks>
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
