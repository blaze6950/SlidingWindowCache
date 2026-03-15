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
/// Unit tests for <see cref="CacheNormalizationExecutor{TRange,TData,TDomain}"/>.
/// Verifies the four-step Background Path sequence:
/// (1) statistics update, (2) store data, (3) evaluate eviction, (4) execute eviction.
/// </summary>
public sealed class CacheNormalizationExecutorTests
{
    private readonly SnapshotAppendBufferStorage<int, int> _storage = new();
    private readonly EventCounterCacheDiagnostics _diagnostics = new();

    #region ExecuteAsync — Step 1: Statistics Update

    [Fact]
    public async Task ExecuteAsync_WithUsedSegments_UpdatesMetadata()
    {
        // ARRANGE
        var executor = CreateExecutor(maxSegmentCount: 100);
        var segment = AddToStorage(_storage, 0, 9);
        var beforeAccess = DateTime.UtcNow;

        var request = CreateRequest(
            requestedRange: TestHelpers.CreateRange(0, 9),
            usedSegments: [segment],
            fetchedChunks: null);

        // ACT
        await executor.ExecuteAsync(request, CancellationToken.None);

        // ASSERT — LRU metadata updated (LastAccessedAt refreshed to >= beforeAccess)
        var meta = Assert.IsType<LruEvictionSelector<int, int>.LruMetadata>(segment.EvictionMetadata);
        Assert.True(meta.LastAccessedAt >= beforeAccess);
        Assert.Equal(1, _diagnostics.BackgroundStatisticsUpdated);
    }

    [Fact]
    public async Task ExecuteAsync_WithNoUsedSegments_StillFiresStatisticsUpdatedDiagnostic()
    {
        // ARRANGE — full miss: no used segments, but fetched chunks present
        var executor = CreateExecutor(maxSegmentCount: 100);
        var chunk = CreateChunk(0, 9);

        var request = CreateRequest(
            requestedRange: TestHelpers.CreateRange(0, 9),
            usedSegments: [],
            fetchedChunks: [chunk]);

        // ACT
        await executor.ExecuteAsync(request, CancellationToken.None);

        // ASSERT — statistics update still fires even with empty usedSegments
        Assert.Equal(1, _diagnostics.BackgroundStatisticsUpdated);
    }

    #endregion

    #region ExecuteAsync — Step 2: Store Data

    [Fact]
    public async Task ExecuteAsync_WithFetchedChunks_StoresSegmentAndFiresDiagnostic()
    {
        // ARRANGE
        var executor = CreateExecutor(maxSegmentCount: 100);
        var chunk = CreateChunk(0, 9);

        var request = CreateRequest(
            requestedRange: TestHelpers.CreateRange(0, 9),
            usedSegments: [],
            fetchedChunks: [chunk]);

        // ACT
        await executor.ExecuteAsync(request, CancellationToken.None);

        // ASSERT — segment stored in storage
        Assert.Equal(1, _storage.Count);
        Assert.Equal(1, _diagnostics.BackgroundSegmentStored);
    }

    [Fact]
    public async Task ExecuteAsync_WithMultipleFetchedChunks_StoresAllSegments()
    {
        // ARRANGE
        var executor = CreateExecutor(maxSegmentCount: 100);
        var chunk1 = CreateChunk(0, 9);
        var chunk2 = CreateChunk(20, 29);

        var request = CreateRequest(
            requestedRange: TestHelpers.CreateRange(0, 29),
            usedSegments: [],
            fetchedChunks: [chunk1, chunk2]);

        // ACT
        await executor.ExecuteAsync(request, CancellationToken.None);

        // ASSERT
        Assert.Equal(2, _storage.Count);
        Assert.Equal(2, _diagnostics.BackgroundSegmentStored);
    }

    [Fact]
    public async Task ExecuteAsync_WithNullFetchedChunks_DoesNotStoreAnySegment()
    {
        // ARRANGE — full cache hit: FetchedChunks is null
        var executor = CreateExecutor(maxSegmentCount: 100);
        var segment = AddToStorage(_storage, 0, 9);

        var request = CreateRequest(
            requestedRange: TestHelpers.CreateRange(0, 9),
            usedSegments: [segment],
            fetchedChunks: null);

        // ACT
        await executor.ExecuteAsync(request, CancellationToken.None);

        // ASSERT — storage unchanged (still only the pre-existing segment)
        Assert.Equal(1, _storage.Count);
        Assert.Equal(0, _diagnostics.BackgroundSegmentStored);
    }

    [Fact]
    public async Task ExecuteAsync_WithChunkWithNullRange_SkipsStoringThatChunk()
    {
        // ARRANGE — chunk with null Range means data is out of bounds
        var executor = CreateExecutor(maxSegmentCount: 100);
        var validChunk = CreateChunk(0, 9);
        var nullRangeChunk = new RangeChunk<int, int>(null, []);

        var request = CreateRequest(
            requestedRange: TestHelpers.CreateRange(0, 9),
            usedSegments: [],
            fetchedChunks: [nullRangeChunk, validChunk]);

        // ACT
        await executor.ExecuteAsync(request, CancellationToken.None);

        // ASSERT — only the valid chunk is stored
        Assert.Equal(1, _storage.Count);
        Assert.Equal(1, _diagnostics.BackgroundSegmentStored);
    }

    #endregion

    #region ExecuteAsync — Step 3: Evaluate Eviction

    [Fact]
    public async Task ExecuteAsync_WhenStorageBelowLimit_DoesNotTriggerEviction()
    {
        // ARRANGE — limit is 5, only 1 stored → policy does not fire
        var executor = CreateExecutor(maxSegmentCount: 5);
        var chunk = CreateChunk(0, 9);

        var request = CreateRequest(
            requestedRange: TestHelpers.CreateRange(0, 9),
            usedSegments: [],
            fetchedChunks: [chunk]);

        // ACT
        await executor.ExecuteAsync(request, CancellationToken.None);

        // ASSERT — evaluation ran but eviction was NOT triggered
        Assert.Equal(1, _diagnostics.EvictionEvaluated);
        Assert.Equal(0, _diagnostics.EvictionTriggered);
        Assert.Equal(0, _diagnostics.EvictionExecuted);
    }

    [Fact]
    public async Task ExecuteAsync_WhenStorageExceedsLimit_TriggersEviction()
    {
        // ARRANGE — pre-populate storage with 2 segments, limit is 2; adding one more triggers eviction
        var (executor, engine) = CreateExecutorWithEngine(maxSegmentCount: 2);
        AddPreexisting(engine, 0, 9);
        AddPreexisting(engine, 20, 29);

        var chunk = CreateChunk(40, 49);  // This will push count to 3 > 2

        var request = CreateRequest(
            requestedRange: TestHelpers.CreateRange(40, 49),
            usedSegments: [],
            fetchedChunks: [chunk]);

        // ACT
        await executor.ExecuteAsync(request, CancellationToken.None);

        // ASSERT — eviction triggered and executed
        Assert.Equal(1, _diagnostics.EvictionEvaluated);
        Assert.Equal(1, _diagnostics.EvictionTriggered);
        Assert.Equal(1, _diagnostics.EvictionExecuted);
        // Count should be back at 2 after eviction of 1 segment
        Assert.Equal(2, _storage.Count);
    }

    [Fact]
    public async Task ExecuteAsync_WithNullFetchedChunks_SkipsEvictionEvaluation()
    {
        // ARRANGE — full cache hit: no new data stored → no eviction evaluation
        var executor = CreateExecutor(maxSegmentCount: 1);
        var segment = AddToStorage(_storage, 0, 9);

        var request = CreateRequest(
            requestedRange: TestHelpers.CreateRange(0, 9),
            usedSegments: [segment],
            fetchedChunks: null);

        // ACT
        await executor.ExecuteAsync(request, CancellationToken.None);

        // ASSERT — steps 3 & 4 skipped entirely
        Assert.Equal(0, _diagnostics.EvictionEvaluated);
        Assert.Equal(0, _diagnostics.EvictionTriggered);
        Assert.Equal(0, _diagnostics.EvictionExecuted);
    }

    #endregion

    #region ExecuteAsync — Step 4: Eviction Execution

    [Fact]
    public async Task ExecuteAsync_Eviction_JustStoredSegmentIsImmune()
    {
        // ARRANGE — only 1 slot allowed; the just-stored segment should survive
        var (executor, engine) = CreateExecutorWithEngine(maxSegmentCount: 1);
        var oldSeg = AddPreexisting(engine, 0, 9);

        var chunk = CreateChunk(20, 29);  // will be stored → count=2 > 1 → eviction

        var request = CreateRequest(
            requestedRange: TestHelpers.CreateRange(20, 29),
            usedSegments: [],
            fetchedChunks: [chunk]);

        // ACT
        await executor.ExecuteAsync(request, CancellationToken.None);

        // ASSERT — the old segment was evicted (not the just-stored one)
        Assert.Equal(1, _storage.Count);
        // Old segment [0,9] must be gone
        Assert.Empty(_storage.FindIntersecting(TestHelpers.CreateRange(0, 9)));
        // Just-stored segment [20,29] must still be present
        Assert.Single(_storage.FindIntersecting(TestHelpers.CreateRange(20, 29)));
    }

    #endregion

    #region ExecuteAsync — Diagnostics Lifecycle

    [Fact]
    public async Task ExecuteAsync_Always_FiresNormalizationRequestProcessed()
    {
        // ARRANGE
        var executor = CreateExecutor(maxSegmentCount: 100);

        var request = CreateRequest(
            requestedRange: TestHelpers.CreateRange(0, 9),
            usedSegments: [],
            fetchedChunks: null);

        // ACT
        await executor.ExecuteAsync(request, CancellationToken.None);

        // ASSERT
        Assert.Equal(1, _diagnostics.NormalizationRequestProcessed);
    }

    [Fact]
    public async Task ExecuteAsync_MultipleRequests_AccumulatesDiagnostics()
    {
        // ARRANGE
        var executor = CreateExecutor(maxSegmentCount: 100);

        var request1 = CreateRequest(TestHelpers.CreateRange(0, 9), [], [CreateChunk(0, 9)]);
        var request2 = CreateRequest(TestHelpers.CreateRange(20, 29), [], [CreateChunk(20, 29)]);

        // ACT
        await executor.ExecuteAsync(request1, CancellationToken.None);
        await executor.ExecuteAsync(request2, CancellationToken.None);

        // ASSERT
        Assert.Equal(2, _diagnostics.NormalizationRequestProcessed);
        Assert.Equal(2, _diagnostics.BackgroundStatisticsUpdated);
        Assert.Equal(2, _diagnostics.BackgroundSegmentStored);
        Assert.Equal(2, _storage.Count);
    }

    #endregion

    #region ExecuteAsync — Exception Handling

    [Fact]
    public async Task ExecuteAsync_WhenSelectorThrows_SwallowsExceptionAndFiresFailedDiagnostic()
    {
        // ARRANGE — use a throwing selector to simulate a fault during eviction
        var throwingSelector = new ThrowingEvictionSelector();
        var evictionEngine = new EvictionEngine<int, int>(
            [new MaxSegmentCountPolicy<int, int>(1)],
            throwingSelector,
            _diagnostics);
        var executor = new CacheNormalizationExecutor<int, int, IntegerFixedStepDomain>(
            _storage,
            evictionEngine,
            _diagnostics);

        // Pre-populate so eviction is triggered (count > 1 after storing).
        // Must notify the engine so MaxSegmentCountPolicy._count is accurate.
        var preexisting = AddToStorage(_storage, 0, 9);
        evictionEngine.InitializeSegment(preexisting);

        var chunk = CreateChunk(20, 29);

        var request = CreateRequest(
            requestedRange: TestHelpers.CreateRange(20, 29),
            usedSegments: [],
            fetchedChunks: [chunk]);

        // ACT
        var ex = await Record.ExceptionAsync(() =>
            executor.ExecuteAsync(request, CancellationToken.None));

        // ASSERT — no exception propagated; failed diagnostic incremented
        Assert.Null(ex);
        Assert.Equal(1, _diagnostics.BackgroundOperationFailed);
        Assert.Equal(0, _diagnostics.NormalizationRequestProcessed);
    }

    [Fact]
    public async Task ExecuteAsync_WhenStorageThrows_SwallowsExceptionAndFiresFailedDiagnostic()
    {
        // ARRANGE — use a throwing storage to simulate a storage fault
        var throwingStorage = new ThrowingSegmentStorage();
        var evictionEngine = new EvictionEngine<int, int>(
            [new MaxSegmentCountPolicy<int, int>(100)],
            new LruEvictionSelector<int, int>(),
            _diagnostics);
        var executor = new CacheNormalizationExecutor<int, int, IntegerFixedStepDomain>(
            throwingStorage,
            evictionEngine,
            _diagnostics);

        var chunk = CreateChunk(0, 9);
        var request = CreateRequest(
            requestedRange: TestHelpers.CreateRange(0, 9),
            usedSegments: [],
            fetchedChunks: [chunk]);

        // ACT
        var ex = await Record.ExceptionAsync(() =>
            executor.ExecuteAsync(request, CancellationToken.None));

        // ASSERT
        Assert.Null(ex);
        Assert.Equal(1, _diagnostics.BackgroundOperationFailed);
        Assert.Equal(0, _diagnostics.NormalizationRequestProcessed);
    }

    #endregion

    #region ExecuteAsync — Bulk Storage Path

    [Fact]
    public async Task ExecuteAsync_WithTwoFetchedChunks_TakesBulkPath_StoresAllSegments()
    {
        // ARRANGE — 2 chunks triggers the bulk path (FetchedChunks.Count > 1)
        var executor = CreateExecutor(maxSegmentCount: 100);
        var chunk1 = CreateChunk(0, 9);
        var chunk2 = CreateChunk(20, 29);

        var request = CreateRequest(
            requestedRange: TestHelpers.CreateRange(0, 29),
            usedSegments: [],
            fetchedChunks: [chunk1, chunk2]);

        // ACT
        await executor.ExecuteAsync(request, CancellationToken.None);

        // ASSERT — both segments stored and diagnostics reflect 2 stores
        Assert.Equal(2, _storage.Count);
        Assert.Equal(2, _diagnostics.BackgroundSegmentStored);
        Assert.Single(_storage.FindIntersecting(TestHelpers.CreateRange(0, 9)));
        Assert.Single(_storage.FindIntersecting(TestHelpers.CreateRange(20, 29)));
    }

    [Fact]
    public async Task ExecuteAsync_WithManyFetchedChunks_BulkPath_AllSegmentsStoredAndFindable()
    {
        // ARRANGE — 5 chunks: typical variable-span partial-hit with multiple gaps
        var executor = CreateExecutor(maxSegmentCount: 100);
        var chunks = new[]
        {
            CreateChunk(0, 9),
            CreateChunk(20, 29),
            CreateChunk(40, 49),
            CreateChunk(60, 69),
            CreateChunk(80, 89),
        };

        var request = CreateRequest(
            requestedRange: TestHelpers.CreateRange(0, 89),
            usedSegments: [],
            fetchedChunks: chunks);

        // ACT
        await executor.ExecuteAsync(request, CancellationToken.None);

        // ASSERT — all 5 segments stored and individually findable
        Assert.Equal(5, _storage.Count);
        Assert.Equal(5, _diagnostics.BackgroundSegmentStored);
        Assert.Single(_storage.FindIntersecting(TestHelpers.CreateRange(0, 9)));
        Assert.Single(_storage.FindIntersecting(TestHelpers.CreateRange(20, 29)));
        Assert.Single(_storage.FindIntersecting(TestHelpers.CreateRange(40, 49)));
        Assert.Single(_storage.FindIntersecting(TestHelpers.CreateRange(60, 69)));
        Assert.Single(_storage.FindIntersecting(TestHelpers.CreateRange(80, 89)));
    }

    [Fact]
    public async Task ExecuteAsync_BulkPath_EvictionStillTriggeredCorrectly()
    {
        // ARRANGE — maxSegmentCount=3, pre-populate with 2, then bulk-add 2 more → count=4 > 3 → eviction
        var (executor, engine) = CreateExecutorWithEngine(maxSegmentCount: 3);
        AddPreexisting(engine, 0, 9);
        AddPreexisting(engine, 20, 29);

        var request = CreateRequest(
            requestedRange: TestHelpers.CreateRange(40, 69),
            usedSegments: [],
            fetchedChunks: [CreateChunk(40, 49), CreateChunk(60, 69)]);

        // ACT
        await executor.ExecuteAsync(request, CancellationToken.None);

        // ASSERT — eviction triggered once (count went from 2→4, one eviction brings it to 3)
        Assert.Equal(1, _diagnostics.EvictionEvaluated);
        Assert.Equal(1, _diagnostics.EvictionTriggered);
        Assert.Equal(1, _diagnostics.EvictionExecuted);
        Assert.Equal(3, _storage.Count);
    }

    [Fact]
    public async Task ExecuteAsync_BulkPath_WhenStorageThrows_SwallowsExceptionAndFiresFailedDiagnostic()
    {
        // ARRANGE — ThrowingSegmentStorage.AddRange throws; verify Background Path swallows it
        var throwingStorage = new ThrowingSegmentStorage();
        var evictionEngine = new EvictionEngine<int, int>(
            [new MaxSegmentCountPolicy<int, int>(100)],
            new LruEvictionSelector<int, int>(),
            _diagnostics);
        var executor = new CacheNormalizationExecutor<int, int, IntegerFixedStepDomain>(
            throwingStorage,
            evictionEngine,
            _diagnostics);

        var request = CreateRequest(
            requestedRange: TestHelpers.CreateRange(0, 29),
            usedSegments: [],
            fetchedChunks: [CreateChunk(0, 9), CreateChunk(20, 29)]);

        // ACT
        var ex = await Record.ExceptionAsync(() =>
            executor.ExecuteAsync(request, CancellationToken.None));

        // ASSERT — no exception propagated; failed diagnostic incremented
        Assert.Null(ex);
        Assert.Equal(1, _diagnostics.BackgroundOperationFailed);
        Assert.Equal(0, _diagnostics.NormalizationRequestProcessed);
    }

    #endregion

    #region Helpers — Factories

    private (CacheNormalizationExecutor<int, int, IntegerFixedStepDomain> Executor,
             EvictionEngine<int, int> Engine)
        CreateExecutorWithEngine(int maxSegmentCount)
    {
        var selector = new LruEvictionSelector<int, int>();
        ((IStorageAwareEvictionSelector<int, int>)selector).Initialize(_storage);

        var evictionEngine = new EvictionEngine<int, int>(
            [new MaxSegmentCountPolicy<int, int>(maxSegmentCount)],
            selector,
            _diagnostics);

        var executor = new CacheNormalizationExecutor<int, int, IntegerFixedStepDomain>(
            _storage,
            evictionEngine,
            _diagnostics);

        return (executor, evictionEngine);
    }

    private CacheNormalizationExecutor<int, int, IntegerFixedStepDomain> CreateExecutor(
        int maxSegmentCount) => CreateExecutorWithEngine(maxSegmentCount).Executor;

    /// <summary>
    /// Adds a segment to both <see cref="_storage"/> and the eviction engine's policy tracking
    /// (simulates a segment that was stored in a prior event cycle).
    /// </summary>
    private CachedSegment<int, int> AddPreexisting(
        EvictionEngine<int, int> engine,
        int start,
        int end)
    {
        var seg = AddToStorage(_storage, start, end);
        engine.InitializeSegment(seg);
        return seg;
    }

    private static CacheNormalizationRequest<int, int> CreateRequest(
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
    /// An eviction selector that throws on <see cref="IEvictionSelector{TRange,TData}.TrySelectCandidate"/>
    /// to test exception handling.
    /// </summary>
    private sealed class ThrowingEvictionSelector : IEvictionSelector<int, int>
    {
        public void InitializeMetadata(CachedSegment<int, int> segment) { }

        public void UpdateMetadata(IReadOnlyList<CachedSegment<int, int>> usedSegments) { }

        public bool TrySelectCandidate(
            IReadOnlySet<CachedSegment<int, int>> immuneSegments,
            out CachedSegment<int, int> candidate) =>
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

        public void AddRange(CachedSegment<int, int>[] segments) =>
            throw new InvalidOperationException("Simulated storage failure.");

        public bool TryRemove(CachedSegment<int, int> segment) => false;

        public CachedSegment<int, int>? TryGetRandomSegment() => null;
    }

    #endregion
}
