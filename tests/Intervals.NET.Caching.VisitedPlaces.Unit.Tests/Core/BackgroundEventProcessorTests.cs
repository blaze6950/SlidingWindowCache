using Intervals.NET.Caching.Dto;
using Intervals.NET.Caching.VisitedPlaces.Core;
using Intervals.NET.Caching.VisitedPlaces.Core.Background;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Policies;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Selectors;
using Intervals.NET.Caching.VisitedPlaces.Infrastructure.Storage;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure.Helpers;
using Intervals.NET.Domain.Default.Numeric;

namespace Intervals.NET.Caching.VisitedPlaces.Unit.Tests.Core;

/// <summary>
/// Unit tests for <see cref="BackgroundEventProcessor{TRange,TData,TDomain}"/>.
/// Verifies the four-step Background Path sequence:
/// (1) statistics update, (2) store data, (3) evaluate eviction, (4) execute eviction.
/// </summary>
public sealed class BackgroundEventProcessorTests
{
    private readonly SnapshotAppendBufferStorage<int, int> _storage = new();
    private readonly EventCounterCacheDiagnostics _diagnostics = new();

    #region ProcessEventAsync — Step 1: Statistics Update

    [Fact]
    public async Task ProcessEventAsync_WithUsedSegments_UpdatesMetadata()
    {
        // ARRANGE
        var processor = CreateProcessor(maxSegmentCount: 100);
        var segment = AddToStorage(_storage, 0, 9);
        var beforeAccess = DateTime.UtcNow;

        var evt = CreateEvent(
            requestedRange: TestHelpers.CreateRange(0, 9),
            usedSegments: [segment],
            fetchedChunks: null);

        // ACT
        await processor.ProcessEventAsync(evt, CancellationToken.None);

        // ASSERT — LRU metadata updated (LastAccessedAt refreshed to >= beforeAccess)
        var meta = Assert.IsType<LruEvictionSelector<int, int>.LruMetadata>(segment.EvictionMetadata);
        Assert.True(meta.LastAccessedAt >= beforeAccess);
        Assert.Equal(1, _diagnostics.BackgroundStatisticsUpdated);
    }

    [Fact]
    public async Task ProcessEventAsync_WithNoUsedSegments_StillFiresStatisticsUpdatedDiagnostic()
    {
        // ARRANGE — full miss: no used segments, but fetched chunks present
        var processor = CreateProcessor(maxSegmentCount: 100);
        var chunk = CreateChunk(0, 9);

        var evt = CreateEvent(
            requestedRange: TestHelpers.CreateRange(0, 9),
            usedSegments: [],
            fetchedChunks: [chunk]);

        // ACT
        await processor.ProcessEventAsync(evt, CancellationToken.None);

        // ASSERT — statistics update still fires even with empty usedSegments
        Assert.Equal(1, _diagnostics.BackgroundStatisticsUpdated);
    }

    #endregion

    #region ProcessEventAsync — Step 2: Store Data

    [Fact]
    public async Task ProcessEventAsync_WithFetchedChunks_StoresSegmentAndFiresDiagnostic()
    {
        // ARRANGE
        var processor = CreateProcessor(maxSegmentCount: 100);
        var chunk = CreateChunk(0, 9);

        var evt = CreateEvent(
            requestedRange: TestHelpers.CreateRange(0, 9),
            usedSegments: [],
            fetchedChunks: [chunk]);

        // ACT
        await processor.ProcessEventAsync(evt, CancellationToken.None);

        // ASSERT — segment stored in storage
        Assert.Equal(1, _storage.Count);
        Assert.Equal(1, _diagnostics.BackgroundSegmentStored);
    }

    [Fact]
    public async Task ProcessEventAsync_WithMultipleFetchedChunks_StoresAllSegments()
    {
        // ARRANGE
        var processor = CreateProcessor(maxSegmentCount: 100);
        var chunk1 = CreateChunk(0, 9);
        var chunk2 = CreateChunk(20, 29);

        var evt = CreateEvent(
            requestedRange: TestHelpers.CreateRange(0, 29),
            usedSegments: [],
            fetchedChunks: [chunk1, chunk2]);

        // ACT
        await processor.ProcessEventAsync(evt, CancellationToken.None);

        // ASSERT
        Assert.Equal(2, _storage.Count);
        Assert.Equal(2, _diagnostics.BackgroundSegmentStored);
    }

    [Fact]
    public async Task ProcessEventAsync_WithNullFetchedChunks_DoesNotStoreAnySegment()
    {
        // ARRANGE — full cache hit: FetchedChunks is null
        var processor = CreateProcessor(maxSegmentCount: 100);
        var segment = AddToStorage(_storage, 0, 9);

        var evt = CreateEvent(
            requestedRange: TestHelpers.CreateRange(0, 9),
            usedSegments: [segment],
            fetchedChunks: null);

        // ACT
        await processor.ProcessEventAsync(evt, CancellationToken.None);

        // ASSERT — storage unchanged (still only the pre-existing segment)
        Assert.Equal(1, _storage.Count);
        Assert.Equal(0, _diagnostics.BackgroundSegmentStored);
    }

    [Fact]
    public async Task ProcessEventAsync_WithChunkWithNullRange_SkipsStoringThatChunk()
    {
        // ARRANGE — chunk with null Range means data is out of bounds
        var processor = CreateProcessor(maxSegmentCount: 100);
        var validChunk = CreateChunk(0, 9);
        var nullRangeChunk = new RangeChunk<int, int>(null, []);

        var evt = CreateEvent(
            requestedRange: TestHelpers.CreateRange(0, 9),
            usedSegments: [],
            fetchedChunks: [nullRangeChunk, validChunk]);

        // ACT
        await processor.ProcessEventAsync(evt, CancellationToken.None);

        // ASSERT — only the valid chunk is stored
        Assert.Equal(1, _storage.Count);
        Assert.Equal(1, _diagnostics.BackgroundSegmentStored);
    }

    #endregion

    #region ProcessEventAsync — Step 3: Evaluate Eviction

    [Fact]
    public async Task ProcessEventAsync_WhenStorageBelowLimit_DoesNotTriggerEviction()
    {
        // ARRANGE — limit is 5, only 1 stored → policy does not fire
        var processor = CreateProcessor(maxSegmentCount: 5);
        var chunk = CreateChunk(0, 9);

        var evt = CreateEvent(
            requestedRange: TestHelpers.CreateRange(0, 9),
            usedSegments: [],
            fetchedChunks: [chunk]);

        // ACT
        await processor.ProcessEventAsync(evt, CancellationToken.None);

        // ASSERT — evaluation ran but eviction was NOT triggered
        Assert.Equal(1, _diagnostics.EvictionEvaluated);
        Assert.Equal(0, _diagnostics.EvictionTriggered);
        Assert.Equal(0, _diagnostics.EvictionExecuted);
    }

    [Fact]
    public async Task ProcessEventAsync_WhenStorageExceedsLimit_TriggersEviction()
    {
        // ARRANGE — pre-populate storage with 2 segments, limit is 2; adding one more triggers eviction
        var processor = CreateProcessor(maxSegmentCount: 2);
        AddToStorage(_storage, 0, 9);
        AddToStorage(_storage, 20, 29);

        var chunk = CreateChunk(40, 49);  // This will push count to 3 > 2

        var evt = CreateEvent(
            requestedRange: TestHelpers.CreateRange(40, 49),
            usedSegments: [],
            fetchedChunks: [chunk]);

        // ACT
        await processor.ProcessEventAsync(evt, CancellationToken.None);

        // ASSERT — eviction triggered and executed
        Assert.Equal(1, _diagnostics.EvictionEvaluated);
        Assert.Equal(1, _diagnostics.EvictionTriggered);
        Assert.Equal(1, _diagnostics.EvictionExecuted);
        // Count should be back at 2 after eviction of 1 segment
        Assert.Equal(2, _storage.Count);
    }

    [Fact]
    public async Task ProcessEventAsync_WithNullFetchedChunks_SkipsEvictionEvaluation()
    {
        // ARRANGE — full cache hit: no new data stored → no eviction evaluation
        var processor = CreateProcessor(maxSegmentCount: 1);
        var segment = AddToStorage(_storage, 0, 9);

        var evt = CreateEvent(
            requestedRange: TestHelpers.CreateRange(0, 9),
            usedSegments: [segment],
            fetchedChunks: null);

        // ACT
        await processor.ProcessEventAsync(evt, CancellationToken.None);

        // ASSERT — steps 3 & 4 skipped entirely
        Assert.Equal(0, _diagnostics.EvictionEvaluated);
        Assert.Equal(0, _diagnostics.EvictionTriggered);
        Assert.Equal(0, _diagnostics.EvictionExecuted);
    }

    #endregion

    #region ProcessEventAsync — Step 4: Eviction Execution

    [Fact]
    public async Task ProcessEventAsync_Eviction_JustStoredSegmentIsImmune()
    {
        // ARRANGE — only 1 slot allowed; the just-stored segment should survive
        var processor = CreateProcessor(maxSegmentCount: 1);
        var oldSeg = AddToStorage(_storage, 0, 9);

        var chunk = CreateChunk(20, 29);  // will be stored → count=2 > 1 → eviction

        var evt = CreateEvent(
            requestedRange: TestHelpers.CreateRange(20, 29),
            usedSegments: [],
            fetchedChunks: [chunk]);

        // ACT
        await processor.ProcessEventAsync(evt, CancellationToken.None);

        // ASSERT — the old segment was evicted (not the just-stored one)
        Assert.Equal(1, _storage.Count);
        var remaining = _storage.GetAllSegments();
        Assert.DoesNotContain(oldSeg, remaining);
        // The just-stored segment (range [20,29]) should still be there
        Assert.Single(remaining);
        Assert.Equal(20, (int)remaining[0].Range.Start);
    }

    #endregion

    #region ProcessEventAsync — Diagnostics Lifecycle

    [Fact]
    public async Task ProcessEventAsync_Always_FiresBackgroundEventProcessed()
    {
        // ARRANGE
        var processor = CreateProcessor(maxSegmentCount: 100);

        var evt = CreateEvent(
            requestedRange: TestHelpers.CreateRange(0, 9),
            usedSegments: [],
            fetchedChunks: null);

        // ACT
        await processor.ProcessEventAsync(evt, CancellationToken.None);

        // ASSERT
        Assert.Equal(1, _diagnostics.BackgroundEventProcessed);
    }

    [Fact]
    public async Task ProcessEventAsync_MultipleEvents_AccumulatesDiagnostics()
    {
        // ARRANGE
        var processor = CreateProcessor(maxSegmentCount: 100);

        var evt1 = CreateEvent(TestHelpers.CreateRange(0, 9), [], [CreateChunk(0, 9)]);
        var evt2 = CreateEvent(TestHelpers.CreateRange(20, 29), [], [CreateChunk(20, 29)]);

        // ACT
        await processor.ProcessEventAsync(evt1, CancellationToken.None);
        await processor.ProcessEventAsync(evt2, CancellationToken.None);

        // ASSERT
        Assert.Equal(2, _diagnostics.BackgroundEventProcessed);
        Assert.Equal(2, _diagnostics.BackgroundStatisticsUpdated);
        Assert.Equal(2, _diagnostics.BackgroundSegmentStored);
        Assert.Equal(2, _storage.Count);
    }

    #endregion

    #region ProcessEventAsync — Exception Handling

    [Fact]
    public async Task ProcessEventAsync_WhenSelectorThrows_SwallowsExceptionAndFiresFailedDiagnostic()
    {
        // ARRANGE — use a throwing selector to simulate a fault during eviction
        var throwingSelector = new ThrowingEvictionSelector();
        var processor = new BackgroundEventProcessor<int, int, IntegerFixedStepDomain>(
            _storage,
            policies: [new MaxSegmentCountPolicy<int, int>(1)],
            selector: throwingSelector,
            diagnostics: _diagnostics);

        // Pre-populate so eviction is triggered (count > 1 after storing)
        AddToStorage(_storage, 0, 9);
        var chunk = CreateChunk(20, 29);

        var evt = CreateEvent(
            requestedRange: TestHelpers.CreateRange(20, 29),
            usedSegments: [],
            fetchedChunks: [chunk]);

        // ACT
        var ex = await Record.ExceptionAsync(() =>
            processor.ProcessEventAsync(evt, CancellationToken.None));

        // ASSERT — no exception propagated; failed diagnostic incremented
        Assert.Null(ex);
        Assert.Equal(1, _diagnostics.BackgroundEventProcessingFailed);
        Assert.Equal(0, _diagnostics.BackgroundEventProcessed);
    }

    [Fact]
    public async Task ProcessEventAsync_WhenStorageThrows_SwallowsExceptionAndFiresFailedDiagnostic()
    {
        // ARRANGE — use a throwing storage to simulate a storage fault
        var throwingStorage = new ThrowingSegmentStorage();
        var processor = new BackgroundEventProcessor<int, int, IntegerFixedStepDomain>(
            throwingStorage,
            policies: [new MaxSegmentCountPolicy<int, int>(100)],
            selector: new LruEvictionSelector<int, int>(),
            diagnostics: _diagnostics);

        var chunk = CreateChunk(0, 9);
        var evt = CreateEvent(
            requestedRange: TestHelpers.CreateRange(0, 9),
            usedSegments: [],
            fetchedChunks: [chunk]);

        // ACT
        var ex = await Record.ExceptionAsync(() =>
            processor.ProcessEventAsync(evt, CancellationToken.None));

        // ASSERT
        Assert.Null(ex);
        Assert.Equal(1, _diagnostics.BackgroundEventProcessingFailed);
        Assert.Equal(0, _diagnostics.BackgroundEventProcessed);
    }

    #endregion

    #region Helpers — Factories

    private BackgroundEventProcessor<int, int, IntegerFixedStepDomain> CreateProcessor(
        int maxSegmentCount)
    {
        IReadOnlyList<IEvictionPolicy<int, int>> policies =
            [new MaxSegmentCountPolicy<int, int>(maxSegmentCount)];
        IEvictionSelector<int, int> selector = new LruEvictionSelector<int, int>();

        return new BackgroundEventProcessor<int, int, IntegerFixedStepDomain>(
            _storage,
            policies,
            selector,
            _diagnostics);
    }

    private static BackgroundEvent<int, int> CreateEvent(
        Range<int> requestedRange,
        IReadOnlyList<CachedSegment<int, int>> usedSegments,
        IReadOnlyList<RangeChunk<int, int>>? fetchedChunks) =>
        new(requestedRange, usedSegments, fetchedChunks);

    private static RangeChunk<int, int> CreateChunk(int start, int end)
    {
        var range = TestHelpers.CreateRange(start, end);
        var data = Enumerable.Range(start, end - start + 1);
        return new RangeChunk<int, int>(range, data);
    }

    private static CachedSegment<int, int> AddToStorage(
        SnapshotAppendBufferStorage<int, int> storage,
        int start,
        int end)
    {
        var range = TestHelpers.CreateRange(start, end);
        var segment = new CachedSegment<int, int>(
            range,
            new ReadOnlyMemory<int>(new int[end - start + 1]));
        storage.Add(segment);
        return segment;
    }

    #endregion

    #region Test Doubles

    /// <summary>
    /// An eviction selector that throws on <see cref="OrderCandidates"/> to test exception handling.
    /// </summary>
    private sealed class ThrowingEvictionSelector : IEvictionSelector<int, int>
    {
        public void InitializeMetadata(CachedSegment<int, int> segment, DateTime now) { }

        public void UpdateMetadata(IReadOnlyList<CachedSegment<int, int>> usedSegments, DateTime now) { }

        public IReadOnlyList<CachedSegment<int, int>> OrderCandidates(
            IReadOnlyList<CachedSegment<int, int>> candidates) =>
            throw new InvalidOperationException("Simulated selector failure.");
    }

    /// <summary>
    /// A segment storage that throws on <see cref="Add"/> to test exception handling.
    /// </summary>
    private sealed class ThrowingSegmentStorage : ISegmentStorage<int, int>
    {
        public int Count => 0;

        public IReadOnlyList<CachedSegment<int, int>> FindIntersecting(Range<int> range) => [];

        public void Add(CachedSegment<int, int> segment) =>
            throw new InvalidOperationException("Simulated storage failure.");

        public void Remove(CachedSegment<int, int> segment) { }

        public IReadOnlyList<CachedSegment<int, int>> GetAllSegments() => [];
    }

    #endregion
}
