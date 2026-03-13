using Intervals.NET.Caching.Infrastructure.Diagnostics;
using Intervals.NET.Domain.Abstractions;
using Intervals.NET.Caching.Infrastructure.Scheduling.Base;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction;
using Intervals.NET.Caching.VisitedPlaces.Core.Ttl;
using Intervals.NET.Caching.VisitedPlaces.Infrastructure.Storage;
using Intervals.NET.Caching.VisitedPlaces.Public.Instrumentation;

namespace Intervals.NET.Caching.VisitedPlaces.Core.Background;

/// <summary>
/// Processes <see cref="CacheNormalizationRequest{TRange,TData}"/> items on the Background Storage Loop
/// (the single writer). Executes the four-step Background Path sequence per request:
/// (1) update metadata, (2) store fetched data, (3) evaluate eviction, (4) execute eviction.
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <typeparam name="TDomain">The range domain type; used by domain-aware eviction policies.</typeparam>
/// <remarks>
/// <para><strong>Execution Context:</strong> Background Storage Loop (single writer thread)</para>
/// <para><strong>Critical Contract — Background Path is the SINGLE WRITER for <c>Add</c> (Invariant VPC.A.10):</strong></para>
/// <para>
/// All <see cref="ISegmentStorage{TRange,TData}.Add"/> calls are made exclusively here.
/// <see cref="ISegmentStorage{TRange,TData}.TryRemove"/> may also be called concurrently by the
/// TTL actor; thread safety is guaranteed by <see cref="CachedSegment{TRange,TData}.TryMarkAsRemoved()"/>
/// (Interlocked.CompareExchange) and <see cref="EvictionEngine{TRange,TData}.OnSegmentRemoved"/>
/// using atomic operations internally.
/// Neither the User Path nor the <see cref="EvictionEngine{TRange,TData}"/> touches storage directly.
/// </para>
/// <para><strong>Four-step sequence per request (Invariant VPC.B.3):</strong></para>
/// <list type="number">
/// <item><description>
///   Metadata update — <see cref="EvictionEngine{TRange,TData}.UpdateMetadata"/> updates
///   selector metadata for segments that were read on the User Path (e.g., LRU timestamps).
/// </description></item>
/// <item><description>
///   Store data — each chunk in <see cref="CacheNormalizationRequest{TRange,TData}.FetchedChunks"/> with
///   a non-null Range is added to storage as a new <see cref="CachedSegment{TRange,TData}"/>,
///   followed immediately by <see cref="EvictionEngine{TRange,TData}.InitializeSegment"/> to
///   set up selector metadata and notify stateful policies. If TTL is enabled,
///   <see cref="TtlEngine{TRange,TData}.ScheduleExpirationAsync"/> is called to schedule expiration.
///   Skipped when <c>FetchedChunks</c> is null (full cache hit — zero allocations for the
///   just-stored list on the full-hit path via lazy initialisation).
/// </description></item>
/// <item><description>
///   Evaluate and execute eviction — <see cref="EvictionEngine{TRange,TData}.EvaluateAndExecute"/>
///   queries all policies and, if any constraint is exceeded, returns an <see cref="IEnumerable{T}"/>
///   of candidates yielded one at a time. Only runs when step 2 stored at least one segment.
/// </description></item>
/// <item><description>
///   Remove evicted segments — iterates the enumerable from step 3 and for each candidate
///   calls <see cref="ISegmentStorage{TRange,TData}.TryRemove"/>, which atomically claims
///   ownership via <see cref="CachedSegment{TRange,TData}.TryMarkAsRemoved()"/> internally and
///   returns <see langword="true"/> only for the first caller. For each segment this caller wins,
///   <see cref="EvictionEngine{TRange,TData}.OnSegmentRemoved"/> is called immediately
///   (per-segment — no intermediate list allocation), followed by
///   <see cref="IVisitedPlacesCacheDiagnostics.EvictionSegmentRemoved"/>.
///   After the loop completes, <see cref="IVisitedPlacesCacheDiagnostics.EvictionExecuted"/>
///   is fired once (only when at least one segment was successfully removed).
/// </description></item>
/// </list>
/// <para><strong>Activity counter (Invariant S.H.1):</strong></para>
/// <para>
/// The activity counter was incremented by the User Path before publishing the request.
/// It is decremented by <see cref="WorkSchedulerBase{TWorkItem}.ExecuteWorkItemCoreAsync"/>'s
/// <c>finally</c> block, NOT by this executor. This executor must not touch the counter.
/// </para>
/// <para><strong>Exception handling:</strong></para>
/// <para>
/// Exceptions are caught, reported via <see cref="ICacheDiagnostics.BackgroundOperationFailed"/>,
/// and swallowed so that the background loop survives individual request failures.
/// </para>
/// </remarks>
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
    /// <param name="storage">The segment storage (single writer for Add — only mutated here).</param>
    /// <param name="evictionEngine">
    /// The eviction engine facade; encapsulates selector metadata, policy evaluation,
    /// execution, and eviction diagnostics.
    /// </param>
    /// <param name="diagnostics">Diagnostics sink; must never throw.</param>
    /// <param name="ttlEngine">
    /// Optional TTL engine facade. When non-null, <see cref="TtlEngine{TRange,TData}.ScheduleExpirationAsync"/>
    /// is called for each stored segment immediately after storage. When null, TTL is disabled.
    /// </param>
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
    /// Executes a single <see cref="CacheNormalizationRequest{TRange,TData}"/> through the four-step sequence.
    /// </summary>
    /// <param name="request">The request to execute.</param>
    /// <param name="_">Unused cancellation token (CacheNormalizationRequests never cancel).</param>
    /// <returns>A <see cref="Task"/> that completes when execution is done.</returns>
    /// <remarks>
    /// <para>
    /// The activity counter is managed by the caller (<see cref="WorkSchedulerBase{TWorkItem}"/>),
    /// which decrements it in its own <c>finally</c> block after this method returns.
    /// This executor must NOT touch the activity counter.
    /// </para>
    /// <para>
    /// Note: <c>NormalizationRequestReceived()</c> is called by the scheduler adapter
    /// (<c>VisitedPlacesWorkSchedulerDiagnostics.WorkStarted()</c>) before this method is invoked.
    /// </para>
    /// </remarks>
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
                foreach (var chunk in request.FetchedChunks)
                {
                    if (!chunk.Range.HasValue)
                    {
                        continue;
                    }

                    // VPC.C.3: Enforce no-overlap invariant before storing. If a segment covering
                    // any part of this chunk's range already exists (e.g., from a concurrent
                    // in-flight request for the same range), skip storing to prevent duplicates.
                    var overlapping = _storage.FindIntersecting(chunk.Range.Value);
                    if (overlapping.Count > 0)
                    {
                        continue;
                    }

                    var data = new ReadOnlyMemory<TData>(chunk.Data.ToArray());
                    var segment = new CachedSegment<TRange, TData>(chunk.Range.Value, data);

                    _storage.Add(segment);
                    _evictionEngine.InitializeSegment(segment);
                    _diagnostics.BackgroundSegmentStored();

                    // TTL: if enabled, delegate scheduling to the engine facade.
                    if (_ttlEngine != null)
                    {
                        await _ttlEngine.ScheduleExpirationAsync(segment).ConfigureAwait(false);
                    }

                    (justStoredSegments ??= []).Add(segment);
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
}
