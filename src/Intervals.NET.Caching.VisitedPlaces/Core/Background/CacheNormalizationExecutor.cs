using Intervals.NET.Domain.Abstractions;
using Intervals.NET.Caching.Dto;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction;
using Intervals.NET.Caching.VisitedPlaces.Core.Ttl;
using Intervals.NET.Caching.VisitedPlaces.Infrastructure.Storage;
using Intervals.NET.Caching.VisitedPlaces.Public.Instrumentation;

namespace Intervals.NET.Caching.VisitedPlaces.Core.Background;

/// <summary>
/// Processes cache normalization requests on the Background Storage Loop (single writer).
/// See docs/visited-places/ for design details.
/// </summary>
internal sealed class CacheNormalizationExecutor<TRange, TData, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private readonly ISegmentStorage<TRange, TData> _storage;
    private readonly EvictionEngine<TRange, TData> _evictionEngine;
    private readonly IVisitedPlacesCacheDiagnostics _diagnostics;
    private readonly TtlEngine<TRange, TData>? _ttlEngine;

    /// <summary>
    /// Initializes a new <see cref="CacheNormalizationExecutor{TRange,TData,TDomain}"/>.
    /// </summary>
    public CacheNormalizationExecutor(
        ISegmentStorage<TRange, TData> storage,
        EvictionEngine<TRange, TData> evictionEngine,
        IVisitedPlacesCacheDiagnostics diagnostics,
        TtlEngine<TRange, TData>? ttlEngine = null)
    {
        _storage = storage;
        _evictionEngine = evictionEngine;
        _diagnostics = diagnostics;
        _ttlEngine = ttlEngine;
    }

    /// <summary>
    /// Executes a single cache normalization request through the four-step sequence.
    /// </summary>
    public async Task ExecuteAsync(CacheNormalizationRequest<TRange, TData> request, CancellationToken _)
    {
        try
        {
            // Step 1: Update selector metadata for segments read on the User Path.
            _evictionEngine.UpdateMetadata(request.UsedSegments);
            _diagnostics.BackgroundStatisticsUpdated();

            // Step 2: Store freshly fetched data (null FetchedChunks means full cache hit — skip).
            // Track ALL segments stored in this request cycle for just-stored immunity (Invariant VPC.E.3).
            // Lazy-init: list is only allocated when at least one segment is actually stored,
            // so the full-hit path (FetchedChunks == null) pays zero allocation here.
            List<CachedSegment<TRange, TData>>? justStoredSegments = null;

            if (request.FetchedChunks != null)
            {
                // Choose between bulk and single-add paths based on chunk count.
                //
                // Constant-span access patterns (each request fetches at most one range) never
                // benefit from bulk storage: there is at most one gap per request, so the
                // single-add path is used.
                //
                // Variable-span access patterns can produce many gaps in a single request
                // (one per cached sub-range not covering the requested span). With the
                // single-add path each chunk triggers a normalization every AppendBufferSize
                // additions — O(gaps/bufferSize) normalizations, each rebuilding an
                // increasingly large data structure: O(gaps x totalSegments) overall.
                // The bulk path reduces this to a single O(totalSegments) normalization.
                if (request.FetchedChunks.Count > 1)
                {
                    justStoredSegments = await StoreBulkAsync(request.FetchedChunks).ConfigureAwait(false);
                }
                else
                {
                    justStoredSegments = await StoreSingleAsync(request.FetchedChunks[0]).ConfigureAwait(false);
                }
            }

            // Steps 3 & 4: Evaluate and execute eviction only when new data was stored.
            if (justStoredSegments != null)
            {
                // Step 3+4: Evaluate policies and iterate candidates to remove (Invariant VPC.E.2a).
                // The selector samples directly from its injected storage.
                // EvictionEvaluated and EvictionTriggered diagnostics are fired by the engine.
                // EvictionExecuted is fired here after the full enumeration completes.
                var evicted = false;
                foreach (var segment in _evictionEngine.EvaluateAndExecute(justStoredSegments))
                {
                    if (!_storage.TryRemove(segment))
                    {
                        continue; // TTL actor already claimed this segment — skip.
                    }

                    _evictionEngine.OnSegmentRemoved(segment);
                    _diagnostics.EvictionSegmentRemoved();
                    evicted = true;
                }

                if (evicted)
                {
                    _diagnostics.EvictionExecuted();
                }
            }

            _diagnostics.NormalizationRequestProcessed();
        }
        catch (OperationCanceledException)
        {
            // Cancellation (e.g. from TtlEngine disposal CTS) must propagate so the
            // scheduler's execution pipeline can fire WorkCancelled instead of WorkFailed.
            throw;
        }
        catch (Exception ex)
        {
            _diagnostics.BackgroundOperationFailed(ex);
            // Swallow: the background loop must survive individual request failures.
        }
    }

    /// <summary>
    /// Stores a single chunk via <see cref="ISegmentStorage{TRange,TData}.Add"/>.
    /// Used when exactly one chunk was fetched (constant-span or single-gap requests).
    /// Returns a single-element list if the chunk was stored, or <see langword="null"/> if it
    /// had no valid range or overlapped an existing segment.
    /// </summary>
    private async Task<List<CachedSegment<TRange, TData>>?> StoreSingleAsync(
        RangeChunk<TRange, TData> chunk)
    {
        if (!chunk.Range.HasValue)
        {
            return null;
        }

        // VPC.C.3: skip if an overlapping segment already exists in storage.
        var overlapping = _storage.FindIntersecting(chunk.Range.Value);
        if (overlapping.Count > 0)
        {
            return null;
        }

        var data = new ReadOnlyMemory<TData>(chunk.Data.ToArray());
        var segment = new CachedSegment<TRange, TData>(chunk.Range.Value, data);

        _storage.Add(segment);
        _evictionEngine.InitializeSegment(segment);
        _diagnostics.BackgroundSegmentStored();

        if (_ttlEngine != null)
        {
            await _ttlEngine.ScheduleExpirationAsync(segment).ConfigureAwait(false);
        }

        return [segment];
    }

    /// <summary>
    /// Validates all chunks, builds the segment array, stores them in a single bulk call via
    /// <see cref="ISegmentStorage{TRange,TData}.AddRange"/>, then initialises metadata and
    /// schedules TTL for each. Used when there are two or more fetched chunks.
    /// Returns the list of stored segments, or <see langword="null"/> if none were stored.
    /// </summary>
    private async Task<List<CachedSegment<TRange, TData>>?> StoreBulkAsync(
        IReadOnlyList<RangeChunk<TRange, TData>> chunks)
    {
        // ValidateChunks is a lazy enumerator — materialise to an array before calling AddRange
        // so all overlap checks are done against the pre-bulk-add storage state (single-writer
        // guarantee means no concurrent writes can occur between the checks and the bulk add).
        var validated = ValidateChunks(chunks).ToArray();

        if (validated.Length == 0)
        {
            return null;
        }

        // Bulk-add: a single normalization pass for all incoming segments.
        _storage.AddRange(validated);

        // Metadata init and TTL scheduling have no dependency on storage internals —
        // they operate only on the segment objects themselves.
        var justStored = new List<CachedSegment<TRange, TData>>(validated.Length);
        foreach (var segment in validated)
        {
            _evictionEngine.InitializeSegment(segment);
            _diagnostics.BackgroundSegmentStored();

            if (_ttlEngine != null)
            {
                await _ttlEngine.ScheduleExpirationAsync(segment).ConfigureAwait(false);
            }

            justStored.Add(segment);
        }

        return justStored;
    }

    /// <summary>
    /// Lazy enumerator that yields a <see cref="CachedSegment{TRange,TData}"/> for each chunk
    /// that has a valid range and does not overlap an existing segment in storage (VPC.C.3).
    /// Materialise with <c>.ToArray()</c> before the bulk add so all checks run against the
    /// consistent pre-add storage state.
    /// </summary>
    private IEnumerable<CachedSegment<TRange, TData>> ValidateChunks(
        IReadOnlyList<RangeChunk<TRange, TData>> chunks)
    {
        foreach (var chunk in chunks)
        {
            if (!chunk.Range.HasValue)
            {
                continue;
            }

            var overlapping = _storage.FindIntersecting(chunk.Range.Value);
            if (overlapping.Count > 0)
            {
                continue;
            }

            var data = new ReadOnlyMemory<TData>(chunk.Data.ToArray());
            yield return new CachedSegment<TRange, TData>(chunk.Range.Value, data);
        }
    }
}
