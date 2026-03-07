using Intervals.NET.Domain.Abstractions;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Pressure;
using Intervals.NET.Caching.VisitedPlaces.Infrastructure.Storage;
using Intervals.NET.Caching.VisitedPlaces.Public.Instrumentation;

namespace Intervals.NET.Caching.VisitedPlaces.Core.Background;

/// <summary>
/// Processes <see cref="BackgroundEvent{TRange,TData}"/> items on the Background Storage Loop
/// (the single writer). Executes the four-step Background Path sequence per event:
/// (1) update statistics, (2) store fetched data, (3) evaluate eviction, (4) execute eviction.
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <typeparam name="TDomain">The range domain type; used by domain-aware eviction policies.</typeparam>
/// <remarks>
/// <para><strong>Execution Context:</strong> Background Storage Loop (single writer thread)</para>
/// <para><strong>Critical Contract — Background Path is the SINGLE WRITER (Invariant VPC.A.10):</strong></para>
/// <para>
/// All mutations to <see cref="ISegmentStorage{TRange,TData}"/> are made exclusively here.
/// The User Path never mutates storage.
/// </para>
/// <para><strong>Four-step sequence per event (Invariant VPC.B.3):</strong></para>
/// <list type="number">
/// <item><description>
///   Statistics update — per-segment statistics (<c>HitCount</c>, <c>LastAccessedAt</c>) are
///   updated for segments that were read on the User Path. This is an orthogonal concern
///   owned directly by the processor (not by any eviction component).
/// </description></item>
/// <item><description>
///   Store data — each chunk in <see cref="BackgroundEvent{TRange,TData}.FetchedChunks"/> with
///   a non-null Range is added to storage as a new <see cref="CachedSegment{TRange,TData}"/>.
///   Skipped when <c>FetchedChunks</c> is null (full cache hit).
/// </description></item>
/// <item><description>
///   Evaluate eviction — all <see cref="IEvictionPolicy{TRange,TData}"/> instances are queried.
///   Each returns an <see cref="IEvictionPressure{TRange,TData}"/>. Pressures with
///   <c>IsExceeded = true</c> are collected into a <see cref="CompositePressure{TRange,TData}"/>.
///   Only runs when step 2 stored at least one segment.
/// </description></item>
/// <item><description>
///   Execute eviction — <see cref="EvictionExecutor{TRange,TData}.Execute"/> is called
///   with the composite pressure; it removes segments in selector order until all pressures
///   are satisfied (Invariant VPC.E.2a). The processor then removes the returned segments
///   from storage.
/// </description></item>
/// </list>
/// <para><strong>Activity counter (Invariant S.H.1):</strong></para>
/// <para>
/// The activity counter was incremented by the User Path before publishing the event.
/// It is decremented by <see cref="WorkSchedulerBase{TWorkItem}.ExecuteWorkItemCoreAsync"/>'s
/// <c>finally</c> block, NOT by this processor. This processor must not touch the counter.
/// </para>
/// <para><strong>Exception handling:</strong></para>
/// <para>
/// Exceptions are caught, reported via <see cref="ICacheDiagnostics.BackgroundEventProcessingFailed"/>,
/// and swallowed so that the background loop survives individual event failures.
/// </para>
/// </remarks>
internal sealed class BackgroundEventProcessor<TRange, TData, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private readonly ISegmentStorage<TRange, TData> _storage;
    private readonly IReadOnlyList<IEvictionPolicy<TRange, TData>> _policies;
    private readonly EvictionExecutor<TRange, TData> _executor;
    private readonly ICacheDiagnostics _diagnostics;

    /// <summary>
    /// Initializes a new <see cref="BackgroundEventProcessor{TRange,TData,TDomain}"/>.
    /// </summary>
    /// <param name="storage">The segment storage (single writer — only mutated here).</param>
    /// <param name="policies">Eviction policies; checked after each storage step.</param>
    /// <param name="selector">Eviction selector; determines candidate ordering for the executor.</param>
    /// <param name="diagnostics">Diagnostics sink; must never throw.</param>
    public BackgroundEventProcessor(
        ISegmentStorage<TRange, TData> storage,
        IReadOnlyList<IEvictionPolicy<TRange, TData>> policies,
        IEvictionSelector<TRange, TData> selector,
        ICacheDiagnostics diagnostics)
    {
        _storage = storage;
        _policies = policies;
        _executor = new EvictionExecutor<TRange, TData>(selector);
        _diagnostics = diagnostics;
    }

    /// <summary>
    /// Processes a single <see cref="BackgroundEvent{TRange,TData}"/> through the four-step sequence.
    /// </summary>
    /// <param name="backgroundEvent">The event to process.</param>
    /// <param name="_">Unused cancellation token (BackgroundEvents never cancel).</param>
    /// <returns>A <see cref="Task"/> that completes when processing is done.</returns>
    /// <remarks>
    /// <para>
    /// The activity counter is managed by the caller (<see cref="WorkSchedulerBase{TWorkItem}"/>),
    /// which decrements it in its own <c>finally</c> block after this method returns.
    /// This processor must NOT touch the activity counter.
    /// </para>
    /// <para>
    /// Note: <c>BackgroundEventReceived()</c> is called by the scheduler adapter
    /// (<c>VisitedPlacesWorkSchedulerDiagnostics.WorkStarted()</c>) before this method is invoked.
    /// </para>
    /// </remarks>
    public Task ProcessEventAsync(BackgroundEvent<TRange, TData> backgroundEvent, CancellationToken _)
    {
        try
        {
            var now = DateTime.UtcNow;

            // Step 1: Update statistics for segments read on the User Path.
            // This is an orthogonal concern: HitCount++ and LastAccessedAt = now for each used segment.
            // Owned directly by the processor (not by any eviction component).
            UpdateStatistics(backgroundEvent.UsedSegments, now);
            _diagnostics.BackgroundStatisticsUpdated();

            // Step 2: Store freshly fetched data (null FetchedChunks means full cache hit — skip).
            // Track ALL segments stored in this event cycle for just-stored immunity (Invariant VPC.E.3).
            var justStoredSegments = new List<CachedSegment<TRange, TData>>();

            if (backgroundEvent.FetchedChunks != null)
            {
                foreach (var chunk in backgroundEvent.FetchedChunks)
                {
                    if (!chunk.Range.HasValue)
                    {
                        continue;
                    }

                    var data = new ReadOnlyMemory<TData>(chunk.Data.ToArray());
                    var segment = new CachedSegment<TRange, TData>(
                        chunk.Range.Value,
                        data,
                        new SegmentStatistics(now));

                    _storage.Add(segment);
                    _diagnostics.BackgroundSegmentStored();

                    justStoredSegments.Add(segment);
                }
            }

            // Steps 3 & 4: Evaluate and execute eviction only when new data was stored.
            if (justStoredSegments.Count > 0)
            {
                // Step 3: Evaluate — query all policies and collect exceeded pressures.
                var allSegments = _storage.GetAllSegments();

                var exceededPressures = new List<IEvictionPressure<TRange, TData>>();
                foreach (var policy in _policies)
                {
                    var pressure = policy.Evaluate(allSegments);
                    if (pressure.IsExceeded)
                    {
                        exceededPressures.Add(pressure);
                    }
                }

                _diagnostics.EvictionEvaluated();

                // Step 4: Execute eviction if any policy produced an exceeded pressure (Invariant VPC.E.2a).
                if (exceededPressures.Count > 0)
                {
                    _diagnostics.EvictionTriggered();

                    // Build composite pressure for multi-policy satisfaction.
                    IEvictionPressure<TRange, TData> compositePressure = exceededPressures.Count == 1
                        ? exceededPressures[0]
                        : new CompositePressure<TRange, TData>(exceededPressures.ToArray());

                    var toRemove = _executor.Execute(compositePressure, allSegments, justStoredSegments);
                    foreach (var segment in toRemove)
                    {
                        _storage.Remove(segment);
                    }

                    _diagnostics.EvictionExecuted();
                }
            }

            _diagnostics.BackgroundEventProcessed();
        }
        catch (Exception ex)
        {
            _diagnostics.BackgroundEventProcessingFailed(ex);
            // Swallow: the background loop must survive individual event failures.
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Updates per-segment statistics for all segments in <paramref name="usedSegments"/>.
    /// </summary>
    /// <param name="usedSegments">The segments that were accessed by the User Path.</param>
    /// <param name="now">The current timestamp to assign to <c>LastAccessedAt</c>.</param>
    /// <remarks>
    /// <para>
    /// For each segment in <paramref name="usedSegments"/>:
    /// <list type="bullet">
    /// <item><description><c>HitCount</c> is incremented (Invariant VPC.E.4b)</description></item>
    /// <item><description><c>LastAccessedAt</c> is set to <paramref name="now"/> (Invariant VPC.E.4b)</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// This logic was previously duplicated across all three executor implementations
    /// (<c>LruEvictionExecutor</c>, <c>FifoEvictionExecutor</c>, <c>SmallestFirstEvictionExecutor</c>).
    /// It is an orthogonal concern that does not belong on candidate selectors.
    /// </para>
    /// </remarks>
    private static void UpdateStatistics(IReadOnlyList<CachedSegment<TRange, TData>> usedSegments, DateTime now)
    {
        foreach (var segment in usedSegments)
        {
            segment.Statistics.HitCount++;
            segment.Statistics.LastAccessedAt = now;
        }
    }
}
