using Intervals.NET.Domain.Abstractions;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction;
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
/// <typeparam name="TDomain">The range domain type; used by domain-aware eviction executors.</typeparam>
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
///   Statistics update — <see cref="IEvictionExecutor{TRange,TData}.UpdateStatistics"/> is called
///   with the segments that were read on the User Path.
/// </description></item>
/// <item><description>
///   Store data — each chunk in <see cref="BackgroundEvent{TRange,TData}.FetchedChunks"/> with
///   a non-null Range is added to storage as a new <see cref="CachedSegment{TRange,TData}"/>.
///   Skipped when <c>FetchedChunks</c> is null (full cache hit).
/// </description></item>
/// <item><description>
///   Evaluate eviction — all <see cref="IEvictionEvaluator{TRange,TData}"/> instances are queried.
///   Only runs when step 2 stored at least one segment.
/// </description></item>
/// <item><description>
///   Execute eviction — <see cref="IEvictionExecutor{TRange,TData}.SelectForEviction"/> is called
///   when at least one evaluator fired; the processor then removes the returned segments from storage
///   (Invariant VPC.E.2a).
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
    private readonly IReadOnlyList<IEvictionEvaluator<TRange, TData>> _evaluators;
    private readonly IEvictionExecutor<TRange, TData> _executor;
    private readonly ICacheDiagnostics _diagnostics;

    /// <summary>
    /// Initializes a new <see cref="BackgroundEventProcessor{TRange,TData,TDomain}"/>.
    /// </summary>
    /// <param name="storage">The segment storage (single writer — only mutated here).</param>
    /// <param name="evaluators">Eviction evaluators; checked after each storage step.</param>
    /// <param name="executor">Eviction executor; performs statistics updates and selects segments for eviction.</param>
    /// <param name="diagnostics">Diagnostics sink; must never throw.</param>
    public BackgroundEventProcessor(
        ISegmentStorage<TRange, TData> storage,
        IReadOnlyList<IEvictionEvaluator<TRange, TData>> evaluators,
        IEvictionExecutor<TRange, TData> executor,
        ICacheDiagnostics diagnostics)
    {
        _storage = storage;
        _evaluators = evaluators;
        _executor = executor;
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
            _executor.UpdateStatistics(backgroundEvent.UsedSegments, now);
            _diagnostics.BackgroundStatisticsUpdated();

            // Step 2: Store freshly fetched data (null FetchedChunks means full cache hit — skip).
            // TODO: just stored segment contains only the last stored segment within a single event proceesing, but the invariant mentioned that we have to prevent eviction of recently stored segment(S) cover all the stored segments within a single event processing.
            CachedSegment<TRange, TData>? justStored = null;

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

                    justStored = segment;
                }
            }

            // Steps 3 & 4: Evaluate and execute eviction only when new data was stored.
            if (justStored != null)
            {
                // Step 3: Evaluate — query all evaluators with current storage state.
                _diagnostics.EvictionEvaluated(); // evaluated in past simple - means it is done already, but we can see that this method is called BEFORE the actual aviction evaluation

                var allSegments = _storage.GetAllSegments();
                var count = _storage.Count;

                var firedEvaluators = new List<IEvictionEvaluator<TRange, TData>>();
                foreach (var evaluator in _evaluators)
                {
                    if (evaluator.ShouldEvict(count, allSegments))
                    {
                        firedEvaluators.Add(evaluator);
                    }
                }

                // Step 4: Execute eviction if any evaluator fired (Invariant VPC.E.2a).
                // The executor selects candidates; this processor removes them from storage.
                if (firedEvaluators.Count > 0)
                {
                    _diagnostics.EvictionTriggered();

                    var toRemove = _executor.SelectForEviction(allSegments, justStored, firedEvaluators);
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
}
