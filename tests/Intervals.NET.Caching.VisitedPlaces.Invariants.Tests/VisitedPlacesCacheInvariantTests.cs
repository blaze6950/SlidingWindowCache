using Intervals.NET.Caching.Dto;
using Intervals.NET.Caching.Extensions;
using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Policies;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Selectors;
using Intervals.NET.Caching.VisitedPlaces.Public.Cache;
using Intervals.NET.Caching.VisitedPlaces.Public.Configuration;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure.DataSources;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure.Helpers;

namespace Intervals.NET.Caching.VisitedPlaces.Invariants.Tests;

/// <summary>
/// Automated tests verifying system invariants of <c>VisitedPlacesCache</c>.
/// Each test is named after its invariant ID and description from
/// <c>docs/visited-places/invariants.md</c> and <c>docs/shared/invariants.md</c>.
/// 
/// This suite tests any invariant whose guarantees are observable through the public API,
/// regardless of its classification (Behavioral, Architectural, or Conceptual) in the
/// invariants documentation. The classification describes the nature of the invariant;
/// it does not restrict testability.
/// </summary>
public sealed class VisitedPlacesCacheInvariantTests : IAsyncDisposable
{
    private readonly IntegerFixedStepDomain _domain = new();
    private readonly EventCounterCacheDiagnostics _diagnostics = new();

    // Current cache tracked for disposal after each test.
    private IAsyncDisposable? _currentCache;

    public async ValueTask DisposeAsync()
    {
        if (_currentCache != null)
        {
            await _currentCache.DisposeAsync();
        }
    }

    // ============================================================
    // STORAGE STRATEGY TEST DATA
    // ============================================================

    public static IEnumerable<object[]> StorageStrategyTestData =>
    [
        [SnapshotAppendBufferStorageOptions<int, int>.Default],
        [LinkedListStrideIndexStorageOptions<int, int>.Default]
    ];

    // ============================================================
    // HELPERS
    // ============================================================

    private VisitedPlacesCache<int, int, IntegerFixedStepDomain> TrackCache(
        VisitedPlacesCache<int, int, IntegerFixedStepDomain> cache)
    {
        _currentCache = cache;
        return cache;
    }

    private VisitedPlacesCache<int, int, IntegerFixedStepDomain> CreateCache(
        StorageStrategyOptions<int, int>? strategy = null,
        int maxSegmentCount = 100) =>
        TrackCache(TestHelpers.CreateCacheWithSimpleSource(
            _domain, _diagnostics,
            TestHelpers.CreateDefaultOptions(strategy),
            maxSegmentCount));

    // ============================================================
    // VPC.A.3 — User Path Always Serves Requests
    // ============================================================

    /// <summary>
    /// Invariant VPC.A.3 [Behavioral]: The User Path always serves user requests regardless of
    /// the state of background processing.
    /// Verifies that GetDataAsync returns correct data even when the background loop is busy
    /// processing prior events.
    /// </summary>
    [Fact]
    public async Task Invariant_VPC_A_3_UserPathAlwaysServesRequests()
    {
        // ARRANGE
        var cache = CreateCache();

        // ACT — make several overlapping requests without waiting for idle
        var tasks = new List<Task<RangeResult<int, int>>>();
        for (var i = 0; i < 10; i++)
        {
            tasks.Add(cache.GetDataAsync(
                TestHelpers.CreateRange(i * 5, i * 5 + 4),
                CancellationToken.None).AsTask());
        }

        var results = await Task.WhenAll(tasks);

        // ASSERT — every request was served correctly with valid data
        for (var i = 0; i < results.Length; i++)
        {
            var range = TestHelpers.CreateRange(i * 5, i * 5 + 4);
            Assert.True(results[i].Data.Length > 0,
                $"Request {i} returned empty data — User Path must always serve requests");
            TestHelpers.AssertUserDataCorrect(results[i].Data, range);
        }

        // Wait for idle before dispose
        await cache.WaitForIdleAsync();
    }

    // ============================================================
    // VPC.A.4 — User Path Never Waits for Background Path
    // ============================================================

    /// <summary>
    /// Invariant VPC.A.4 [Behavioral]: GetDataAsync returns immediately after assembling data —
    /// it does not block on background storage, statistics updates, or eviction.
    /// Verifies that GetDataAsync completes promptly (well under the background processing timeout).
    /// </summary>
    [Fact]
    public async Task Invariant_VPC_A_4_UserPathNeverWaitsForBackground()
    {
        // ARRANGE
        var slowDataSource = new SlowDataSource(delay: TimeSpan.FromMilliseconds(200));
        var cache = TrackCache(TestHelpers.CreateCache(
            slowDataSource, _domain, TestHelpers.CreateDefaultOptions(), _diagnostics));

        var range = TestHelpers.CreateRange(0, 9);

        // ACT — call GetDataAsync and measure time; background loop may be slow, but user path must not wait
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await cache.GetDataAsync(range, CancellationToken.None);
        sw.Stop();

        // ASSERT — GetDataAsync should complete within reasonable time.
        // The data source takes 200ms and FetchAsync IS called on the User Path (VPC.A.8),
        // so GetDataAsync legitimately includes the data source delay.
        // What this test verifies is that GetDataAsync does NOT additionally wait for background
        // normalization, storage, or eviction — it returns as soon as data is assembled and
        // the CacheNormalizationRequest is enqueued.
        // The 750ms threshold accommodates the ~200ms FetchAsync delay plus execution overhead,
        // while catching any erroneous blocking on background processing.
        Assert.True(sw.ElapsedMilliseconds < 750,
            $"GetDataAsync took {sw.ElapsedMilliseconds}ms — User Path must not block on Background Path.");

        Assert.Equal(10, result.Data.Length);
        await cache.WaitForIdleAsync();
    }

    // ============================================================
    // VPC.A.9 — User Receives Data Exactly for RequestedRange
    // ============================================================

    /// <summary>
    /// Invariant VPC.A.9 [Behavioral]: The user always receives data exactly corresponding to
    /// RequestedRange (Data.Length == range.Span(domain) and values match).
    /// </summary>
    [Theory]
    [MemberData(nameof(StorageStrategyTestData))]
    public async Task Invariant_VPC_A_9_UserAlwaysReceivesDataForRequestedRange(StorageStrategyOptions<int, int> strategy)
    {
        // ARRANGE
        var cache = CreateCache(strategy);

        // ACT & ASSERT — cold start (full miss)
        var range1 = TestHelpers.CreateRange(0, 9);
        var result1 = await cache.GetDataAndWaitForIdleAsync(range1);
        TestHelpers.AssertUserDataCorrect(result1.Data, range1);

        // ACT & ASSERT — full hit (cached)
        var result2 = await cache.GetDataAsync(range1, CancellationToken.None);
        TestHelpers.AssertUserDataCorrect(result2.Data, range1);

        // ACT & ASSERT — partial hit
        var range3 = TestHelpers.CreateRange(5, 14);
        var result3 = await cache.GetDataAsync(range3, CancellationToken.None);
        TestHelpers.AssertUserDataCorrect(result3.Data, range3);

        await cache.WaitForIdleAsync();
    }

    /// <summary>
    /// Invariant VPC.A.9a [Behavioral]: CacheInteraction accurately classifies each request.
    /// Cold start → FullMiss; second identical request → FullHit; partial overlap → PartialHit.
    /// </summary>
    [Theory]
    [MemberData(nameof(StorageStrategyTestData))]
    public async Task Invariant_VPC_A_9a_CacheInteractionClassifiedCorrectly(StorageStrategyOptions<int, int> strategy)
    {
        // ARRANGE
        var cache = CreateCache(strategy);
        var range = TestHelpers.CreateRange(0, 9);

        // ACT — full miss (cold start)
        var coldResult = await cache.GetDataAndWaitForIdleAsync(range);
        Assert.Equal(CacheInteraction.FullMiss, coldResult.CacheInteraction);

        // ACT — full hit
        var hitResult = await cache.GetDataAsync(range, CancellationToken.None);
        Assert.Equal(CacheInteraction.FullHit, hitResult.CacheInteraction);

        // ACT — partial hit: [0,9] is cached; request [5,14] overlaps but extends right
        var partialResult = await cache.GetDataAsync(
            TestHelpers.CreateRange(5, 14), CancellationToken.None);
        Assert.Equal(CacheInteraction.PartialHit, partialResult.CacheInteraction);

        await cache.WaitForIdleAsync();
    }

    // ============================================================
    // VPC.B.3 — Background Path Four-Step Sequence
    // ============================================================

    /// <summary>
    /// Invariant VPC.B.3 [Behavioral]: Each CacheNormalizationRequest is processed in the fixed sequence:
    /// (1) statistics update, (2) store data, (3) evaluate eviction, (4) execute eviction.
    /// Verified by checking that diagnostics counters fire in the correct quantities.
    /// </summary>
    [Fact]
    public async Task Invariant_VPC_B_3_BackgroundEventProcessedInFourStepSequence()
    {
        // ARRANGE
        var cache = CreateCache();

        // ACT — a full miss triggers a CacheNormalizationRequest with FetchedChunks
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(0, 9));

        // ASSERT — all four steps executed
        // Step 1: statistics updated
        Assert.Equal(1, _diagnostics.BackgroundStatisticsUpdated);
        // Step 2: segment stored
        Assert.Equal(1, _diagnostics.BackgroundSegmentStored);
        // Step 3: eviction evaluated (because new data was stored)
        Assert.Equal(1, _diagnostics.EvictionEvaluated);
        // Step 4: eviction NOT triggered (only 1 segment, limit is 100)
        Assert.Equal(0, _diagnostics.EvictionTriggered);
        // Lifecycle: event processed
        Assert.Equal(1, _diagnostics.NormalizationRequestProcessed);
    }

    /// <summary>
    /// Invariant VPC.B.3b [Behavioral]: Eviction evaluation only occurs after a storage step.
    /// A full cache hit (FetchedChunks == null) must NOT trigger eviction evaluation.
    /// </summary>
    [Fact]
    public async Task Invariant_VPC_B_3b_EvictionNotEvaluatedForFullCacheHit()
    {
        // ARRANGE
        var cache = CreateCache();

        // Warm up: store one segment
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(0, 9));
        _diagnostics.Reset();

        // ACT — full cache hit: FetchedChunks is null → no storage step → no eviction evaluation
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(0, 9));

        // ASSERT — no storage, no eviction steps
        Assert.Equal(0, _diagnostics.BackgroundSegmentStored);
        Assert.Equal(0, _diagnostics.EvictionEvaluated);
        Assert.Equal(0, _diagnostics.EvictionTriggered);
        // But statistics update still fires (step 1 always runs)
        Assert.Equal(1, _diagnostics.BackgroundStatisticsUpdated);
    }

    // ============================================================
    // VPC.C.1 — Non-Contiguous Storage (Gaps Permitted)
    // ============================================================

    /// <summary>
    /// Invariant VPC.C.1 [Behavioral]: CachedSegments is a collection of non-contiguous segments.
    /// Gaps between segments are explicitly permitted. Two non-overlapping requests create two
    /// distinct segments — the cache does not require contiguity.
    /// </summary>
    [Fact]
    public async Task Invariant_VPC_C_1_NonContiguousSegmentsArePermitted()
    {
        // ARRANGE
        var cache = CreateCache();

        // ACT — request two non-overlapping ranges with a gap in between
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(0, 9));
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(100, 109));

        // ASSERT — both segments stored; there is a gap [10,99] which is valid
        Assert.True(_diagnostics.BackgroundSegmentStored >= 2,
            "Both non-overlapping segments should be stored independently.");

        // Verify the data in each independent segment is correct
        var result1 = await cache.GetDataAsync(TestHelpers.CreateRange(0, 9), CancellationToken.None);
        var result2 = await cache.GetDataAsync(TestHelpers.CreateRange(100, 109), CancellationToken.None);
        Assert.Equal(CacheInteraction.FullHit, result1.CacheInteraction);
        Assert.Equal(CacheInteraction.FullHit, result2.CacheInteraction);

        // Gap range must be a full miss (the cache did NOT fill the gap automatically)
        var gapResult = await cache.GetDataAsync(TestHelpers.CreateRange(50, 59), CancellationToken.None);
        Assert.Equal(CacheInteraction.FullMiss, gapResult.CacheInteraction);

        await cache.WaitForIdleAsync();
    }

    // ============================================================
    // VPC.C.2 — Segments Never Merge
    // ============================================================

    /// <summary>
    /// Invariant VPC.C.2 [Architectural]: Segments are never merged, even if two segments are
    /// adjacent (consecutive in the domain with no gap between them).
    /// Verifies that two adjacent ranges [0,9] and [10,19] remain as two distinct segments
    /// after background processing — the cache does not coalesce them.
    /// </summary>
    [Theory]
    [MemberData(nameof(StorageStrategyTestData))]
    public async Task Invariant_VPC_C_2_AdjacentSegmentsNeverMerge(StorageStrategyOptions<int, int> strategy)
    {
        // ARRANGE
        var cache = CreateCache(strategy);

        // ACT — store two adjacent ranges: [0,9] and [10,19] (no gap, no overlap)
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(0, 9));
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(10, 19));

        // ASSERT — exactly 2 segments stored (not merged into 1)
        Assert.Equal(2, _diagnostics.BackgroundSegmentStored);

        // Both original ranges are still individually a FullHit
        var result1 = await cache.GetDataAsync(TestHelpers.CreateRange(0, 9), CancellationToken.None);
        var result2 = await cache.GetDataAsync(TestHelpers.CreateRange(10, 19), CancellationToken.None);
        Assert.Equal(CacheInteraction.FullHit, result1.CacheInteraction);
        Assert.Equal(CacheInteraction.FullHit, result2.CacheInteraction);
        TestHelpers.AssertUserDataCorrect(result1.Data, TestHelpers.CreateRange(0, 9));
        TestHelpers.AssertUserDataCorrect(result2.Data, TestHelpers.CreateRange(10, 19));

        // The combined range [0,19] is also a FullHit (assembled from 2 segments, VPC.C.4)
        var combinedResult = await cache.GetDataAsync(TestHelpers.CreateRange(0, 19), CancellationToken.None);
        Assert.Equal(CacheInteraction.FullHit, combinedResult.CacheInteraction);
        TestHelpers.AssertUserDataCorrect(combinedResult.Data, TestHelpers.CreateRange(0, 19));

        await cache.WaitForIdleAsync();
    }

    // ============================================================
    // VPC.C.3 — Segment Non-Overlap
    // ============================================================

    /// <summary>
    /// Invariant VPC.C.3 [Architectural]: No two segments may share any discrete domain point.
    /// When a partial-hit request overlaps an existing segment, only the gap (uncovered sub-range)
    /// is fetched and stored — the existing segment is not duplicated or extended.
    /// Verifies via SpyDataSource that only the gap range is fetched from the data source.
    /// </summary>
    [Theory]
    [MemberData(nameof(StorageStrategyTestData))]
    public async Task Invariant_VPC_C_3_OverlappingRequestFetchesOnlyGap(StorageStrategyOptions<int, int> strategy)
    {
        // ARRANGE
        var spy = new SpyDataSource();
        var cache = TrackCache(TestHelpers.CreateCache(
            spy, _domain, TestHelpers.CreateDefaultOptions(strategy), _diagnostics));

        // ACT — cache [0,9], then request [5,14] (overlaps [5,9], gap is [10,14])
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(0, 9));
        spy.Reset();
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(5, 14));

        // ASSERT — only the gap [10,14] was fetched (not [5,14] or [0,14])
        Assert.Equal(1, spy.TotalFetchCount);
        var fetchedRanges = spy.GetAllRequestedRanges().ToList();
        Assert.Single(fetchedRanges);
        Assert.True(spy.WasRangeCovered(10, 14),
            "Only the gap [10,14] should have been fetched, not the overlapping portion.");

        // The original segment [0,9] and the new gap segment [10,14] are both stored
        Assert.Equal(2, _diagnostics.BackgroundSegmentStored);

        // Data correctness across both segments
        TestHelpers.AssertUserDataCorrect(
            (await cache.GetDataAsync(TestHelpers.CreateRange(0, 14), CancellationToken.None)).Data,
            TestHelpers.CreateRange(0, 14));

        await cache.WaitForIdleAsync();
    }

    // ============================================================
    // VPC.C.4 — Multi-Segment Assembly for FullHit
    // ============================================================

    /// <summary>
    /// Invariant VPC.C.4 [Architectural]: The User Path assembles data from all contributing
    /// segments when their union covers RequestedRange. If the union of two or more segments
    /// spans RequestedRange with no gaps, CacheInteraction == FullHit.
    /// Verifies that a request spanning two non-adjacent cached segments (with a filled gap)
    /// returns a FullHit with correctly assembled data.
    /// </summary>
    [Theory]
    [MemberData(nameof(StorageStrategyTestData))]
    public async Task Invariant_VPC_C_4_MultiSegmentAssemblyProducesFullHit(StorageStrategyOptions<int, int> strategy)
    {
        // ARRANGE
        var cache = CreateCache(strategy);

        // Cache three separate segments: [0,9], [10,19], [20,29]
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(0, 9));
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(10, 19));
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(20, 29));

        // ACT — request [0,29]: spans all three segments with no gaps
        var result = await cache.GetDataAsync(TestHelpers.CreateRange(0, 29), CancellationToken.None);

        // ASSERT — FullHit (assembled from 3 segments) with correct data
        Assert.Equal(CacheInteraction.FullHit, result.CacheInteraction);
        TestHelpers.AssertUserDataCorrect(result.Data, TestHelpers.CreateRange(0, 29));

        await cache.WaitForIdleAsync();
    }

    // ============================================================
    // VPC.E.3 — Just-Stored Segment Immunity
    // ============================================================

    /// <summary>
    /// Invariant VPC.E.3 [Behavioral]: The just-stored segment is immune from eviction in the
    /// same background event processing step in which it was stored.
    /// Even when the cache is at capacity (maxSegmentCount=1), the newly stored segment survives
    /// and is served as a FullHit on the next request.
    /// </summary>
    [Fact]
    public async Task Invariant_VPC_E_3_JustStoredSegmentIsImmuneFromEviction()
    {
        // ARRANGE — maxSegmentCount=1: eviction will fire on every new segment
        var cache = CreateCache(maxSegmentCount: 1);

        // ACT — store first segment
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(0, 9));

        // ACT — store second segment (forces eviction; first is evicted, second is immune)
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(100, 109));

        // ASSERT — eviction was triggered
        TestHelpers.AssertEvictionTriggered(_diagnostics);

        // ASSERT — the second (just-stored) segment is available as a full hit
        var result = await cache.GetDataAsync(TestHelpers.CreateRange(100, 109), CancellationToken.None);
        Assert.Equal(CacheInteraction.FullHit, result.CacheInteraction);
        TestHelpers.AssertUserDataCorrect(result.Data, TestHelpers.CreateRange(100, 109));

        await cache.WaitForIdleAsync();
    }

    // ============================================================
    // VPC.D.1 — Concurrent Access Safety
    // ============================================================

    /// <summary>
    /// Invariant VPC.D.1 [Architectural]: Multiple concurrent user threads may simultaneously
    /// read from CachedSegments without corruption. The single-writer model ensures no
    /// write-write or read-write races on cache state.
    /// Verifies that rapid concurrent GetDataAsync calls for overlapping ranges produce
    /// correct data with no exceptions or background failures.
    /// </summary>
    [Theory]
    [MemberData(nameof(StorageStrategyTestData))]
    public async Task Invariant_VPC_D_1_ConcurrentAccessDoesNotCorruptState(StorageStrategyOptions<int, int> strategy)
    {
        // ARRANGE
        var cache = CreateCache(strategy);

        // ACT — fire 20 concurrent requests with overlapping ranges
        var tasks = new List<Task<RangeResult<int, int>>>();
        for (var i = 0; i < 20; i++)
        {
            var start = (i % 5) * 10; // ranges: [0,9], [10,19], [20,29], [30,39], [40,49] (cycling)
            tasks.Add(cache.GetDataAsync(
                TestHelpers.CreateRange(start, start + 9),
                CancellationToken.None).AsTask());
        }

        var results = await Task.WhenAll(tasks);

        // ASSERT — every request returned valid data with no corruption
        for (var i = 0; i < results.Length; i++)
        {
            var start = (i % 5) * 10;
            var range = TestHelpers.CreateRange(start, start + 9);
            Assert.Equal(10, results[i].Data.Length);
            TestHelpers.AssertUserDataCorrect(results[i].Data, range);
        }

        // Wait for all background processing to settle
        await cache.WaitForIdleAsync();

        // ASSERT — no background failures occurred
        TestHelpers.AssertNoBackgroundFailures(_diagnostics);
        TestHelpers.AssertBackgroundLifecycleIntegrity(_diagnostics);
    }

    // ============================================================
    // VPC.F.1 — Data Source Called Only for Gaps
    // ============================================================

    /// <summary>
    /// Invariant VPC.F.1 [Behavioral]: IDataSource.FetchAsync is called only for true gaps —
    /// sub-ranges of RequestedRange not covered by any segment in CachedSegments.
    /// After caching [0,9], a request for [0,9] must not call the data source again.
    /// </summary>
    [Fact]
    public async Task Invariant_VPC_F_1_DataSourceCalledOnlyForGaps()
    {
        // ARRANGE
        var spy = new SpyDataSource();
        var cache = TrackCache(TestHelpers.CreateCache(
            spy, _domain, TestHelpers.CreateDefaultOptions(), _diagnostics));

        // ACT — warm up
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(0, 9));
        var fetchCountAfterWarmUp = spy.TotalFetchCount;
        Assert.True(fetchCountAfterWarmUp >= 1, "Data source should be called on cold start.");

        // ACT — repeat identical request: should be a full hit, no data source call
        spy.Reset();
        var hitResult = await cache.GetDataAsync(TestHelpers.CreateRange(0, 9), CancellationToken.None);
        Assert.Equal(CacheInteraction.FullHit, hitResult.CacheInteraction);
        Assert.Equal(0, spy.TotalFetchCount);

        await cache.WaitForIdleAsync();
    }

    /// <summary>
    /// Invariant VPC.F.1 [Architectural] — enhanced: On a partial hit, the data source is called
    /// only for the gap sub-ranges, not for the entire RequestedRange.
    /// Caches [0,9] and [20,29], then requests [0,29]. The only gap is [10,19] — the data source
    /// must be called exactly once for that gap, not for [0,29].
    /// </summary>
    [Fact]
    public async Task Invariant_VPC_F_1_PartialHitFetchesOnlyGapRanges()
    {
        // ARRANGE
        var spy = new SpyDataSource();
        var cache = TrackCache(TestHelpers.CreateCache(
            spy, _domain, TestHelpers.CreateDefaultOptions(), _diagnostics));

        // Warm up: cache [0,9] and [20,29] with a gap at [10,19]
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(0, 9));
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(20, 29));
        spy.Reset();

        // ACT — request [0,29]: partial hit — [0,9] and [20,29] are cached, [10,19] is the gap
        var result = await cache.GetDataAsync(TestHelpers.CreateRange(0, 29), CancellationToken.None);

        // ASSERT — partial hit with correct data
        Assert.Equal(CacheInteraction.PartialHit, result.CacheInteraction);
        TestHelpers.AssertUserDataCorrect(result.Data, TestHelpers.CreateRange(0, 29));

        // ASSERT — only the gap [10,19] was fetched from the data source
        Assert.Equal(1, spy.TotalFetchCount);
        Assert.True(spy.WasRangeCovered(10, 19),
            "Data source should have been called only for gap [10,19].");

        await cache.WaitForIdleAsync();
    }

    // ============================================================
    // VPC.S.H — Diagnostics Lifecycle Integrity
    // ============================================================

    /// <summary>
    /// Shared Invariant S.H [Behavioral]: Background event lifecycle is consistent.
    /// Received == Processed + Failed (no events lost or double-counted).
    /// </summary>
    [Theory]
    [MemberData(nameof(StorageStrategyTestData))]
    public async Task Invariant_VPC_S_H_BackgroundEventLifecycleConsistency(StorageStrategyOptions<int, int> strategy)
    {
        // ARRANGE
        var cache = CreateCache(strategy);

        // ACT — several requests covering all three interaction types
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(0, 9));     // FullMiss
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(0, 9));     // FullHit
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(5, 14));    // PartialHit
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(100, 109)); // FullMiss

        // ASSERT
        TestHelpers.AssertBackgroundLifecycleIntegrity(_diagnostics);
        TestHelpers.AssertNoBackgroundFailures(_diagnostics);
    }

    // ============================================================
    // VPC.S.J — Disposal
    // ============================================================

    /// <summary>
    /// Shared Invariant S.J [Behavioral]: After disposal, GetDataAsync throws ObjectDisposedException.
    /// </summary>
    [Fact]
    public async Task Invariant_VPC_S_J_GetDataAsyncAfterDispose_ThrowsObjectDisposedException()
    {
        // ARRANGE
        var cache = CreateCache();
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(0, 9));
        await cache.DisposeAsync();

        // ACT
        var exception = await Record.ExceptionAsync(() =>
            cache.GetDataAsync(TestHelpers.CreateRange(0, 9), CancellationToken.None).AsTask());

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ObjectDisposedException>(exception);
    }

    /// <summary>
    /// Shared Invariant S.J [Behavioral]: DisposeAsync is idempotent — calling it multiple times
    /// does not throw.
    /// </summary>
    [Fact]
    public async Task Invariant_VPC_S_J_DisposeAsyncIsIdempotent()
    {
        // ARRANGE
        var cache = CreateCache();
        await cache.DisposeAsync();

        // ACT — second dispose
        var exception = await Record.ExceptionAsync(() => cache.DisposeAsync().AsTask());

        // ASSERT
        Assert.Null(exception);
    }

    // ============================================================
    // BOTH STORAGE STRATEGIES — FULL BEHAVIORAL EQUIVALENCE
    // ============================================================

    /// <summary>
    /// Both storage strategies must produce identical observable behavior.
    /// Verifies that the choice of storage strategy is transparent to the user.
    /// </summary>
    [Theory]
    [MemberData(nameof(StorageStrategyTestData))]
    public async Task Invariant_VPC_BothStrategies_BehaviorallyEquivalent(StorageStrategyOptions<int, int> strategy)
    {
        // ARRANGE
        var cache = CreateCache(strategy);
        var ranges = new[]
        {
            TestHelpers.CreateRange(0, 9),
            TestHelpers.CreateRange(50, 59),
            TestHelpers.CreateRange(100, 109)
        };

        // ACT & ASSERT — each range is a full miss on first access
        foreach (var range in ranges)
        {
            var result = await cache.GetDataAndWaitForIdleAsync(range);
            Assert.Equal(CacheInteraction.FullMiss, result.CacheInteraction);
            TestHelpers.AssertUserDataCorrect(result.Data, range);
        }

        // ACT & ASSERT — each range is a full hit on second access
        foreach (var range in ranges)
        {
            var result = await cache.GetDataAndWaitForIdleAsync(range);
            Assert.Equal(CacheInteraction.FullHit, result.CacheInteraction);
            TestHelpers.AssertUserDataCorrect(result.Data, range);
        }
    }

    // ============================================================
    // VPC.T.1 — TTL Expiration Is Idempotent
    // ============================================================

    /// <summary>
    /// Invariant VPC.T.1 [Behavioral]: TTL expiration is idempotent.
    /// A segment that has already been evicted by the eviction policy before its TTL fires
    /// must not be double-removed or cause any error.
    /// </summary>
    [Fact]
    public async Task Invariant_VPC_T_1_TtlExpirationIsIdempotent()
    {
        // ARRANGE — MaxSegmentCount(1): second request evicts first; first segment's TTL fires later
        var options = new VisitedPlacesCacheOptions<int, int>(
            eventChannelCapacity: 128,
            segmentTtl: TimeSpan.FromMilliseconds(150));
        var cache = TrackCache(TestHelpers.CreateCacheWithSimpleSource(_domain, _diagnostics, options, maxSegmentCount: 1));

        // ACT — store segment A, then B (B evicts A); then wait for A's TTL to fire
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(0, 9));
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(20, 29));

        Assert.Equal(2, _diagnostics.BackgroundSegmentStored);
        Assert.Equal(1, _diagnostics.EvictionTriggered);

        // Wait for both TTL work items to fire (one is a no-op because segment was already evicted)
        await Task.Delay(500);

        // ASSERT — only one TTL expiration diagnostic fires (the no-op branch is silent), zero background failures
        Assert.Equal(1, _diagnostics.TtlSegmentExpired);
        Assert.Equal(0, _diagnostics.BackgroundOperationFailed);
    }

    // ============================================================
    // VPC.T.2 — TTL Does Not Block User Path
    // ============================================================

    /// <summary>
    /// Invariant VPC.T.2 [Behavioral]: The TTL background actor never blocks user requests.
    /// Even when TTL is configured with a very short value, user-facing GetDataAsync returns
    /// promptly (no deadlock or starvation from TTL processing).
    /// </summary>
    [Fact]
    public async Task Invariant_VPC_T_2_TtlDoesNotBlockUserPath()
    {
        // ARRANGE — very short TTL (1 ms); many requests in quick succession
        var options = new VisitedPlacesCacheOptions<int, int>(
            eventChannelCapacity: 128,
            segmentTtl: TimeSpan.FromMilliseconds(1));
        var cache = TrackCache(TestHelpers.CreateCacheWithSimpleSource(_domain, _diagnostics, options));

        var ranges = Enumerable.Range(0, 10)
            .Select(i => TestHelpers.CreateRange(i * 10, i * 10 + 9))
            .ToArray();

        // ACT — issue all requests; each should complete quickly without blocking on TTL
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var range in ranges)
        {
            await cache.GetDataAsync(range, CancellationToken.None);
        }
        sw.Stop();

        // ASSERT — all 10 requests completed well within 2 seconds (TTL doesn't block them)
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2),
            $"User path was blocked: elapsed={sw.Elapsed.TotalMilliseconds:F0}ms");
        Assert.Equal(0, _diagnostics.BackgroundOperationFailed);
    }

    // ============================================================
    // S.R.1 — Infinite Range Rejected at Entry Point
    // ============================================================

    /// <summary>
    /// Invariant S.R.1 [Behavioral]: GetDataAsync rejects unbounded ranges by throwing
    /// <see cref="ArgumentException"/> before any cache logic executes.
    /// </summary>
    [Fact]
    public async Task Invariant_VPC_S_R_1_UnboundedRangeThrowsArgumentException()
    {
        // ARRANGE
        var cache = CreateCache();
        var infiniteRange = Factories.Range.Closed(RangeValue<int>.NegativeInfinity, RangeValue<int>.PositiveInfinity);

        // ACT
        var exception = await Record.ExceptionAsync(() =>
            cache.GetDataAsync(infiniteRange, CancellationToken.None).AsTask());

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentException>(exception);
    }

    // ============================================================
    // VPC.F.2 — Bounded Source: null Range Means No Segment Stored
    // ============================================================

    /// <summary>
    /// Invariant VPC.F.2 [Behavioral]: When <c>IDataSource.FetchAsync</c> returns a <c>RangeChunk</c>
    /// with a null <c>Range</c>, the cache treats it as "no data available" and does NOT store
    /// a segment for that gap. The background lifecycle counter still increments correctly.
    /// </summary>
    [Fact]
    public async Task Invariant_VPC_F_2_NullRangeChunk_NoSegmentStored()
    {
        // ARRANGE — BoundedDataSource only serves [1000, 9999]; request below that returns null Range
        var boundedSource = new BoundedDataSource();
        var cache = TrackCache(TestHelpers.CreateCache(
            boundedSource, _domain, TestHelpers.CreateDefaultOptions(), _diagnostics));

        // ACT — request entirely out of bounds (below MinId)
        var outOfBoundsRange = TestHelpers.CreateRange(0, 9);
        var result = await cache.GetDataAndWaitForIdleAsync(outOfBoundsRange);

        // ASSERT — no segment was stored (null Range chunk → no storage step)
        Assert.Equal(0, _diagnostics.BackgroundSegmentStored);

        // The request was still served (classified as FullMiss) and lifecycle is consistent
        Assert.Equal(CacheInteraction.FullMiss, result.CacheInteraction);
        TestHelpers.AssertBackgroundLifecycleIntegrity(_diagnostics);
    }

    /// <summary>
    /// Invariant VPC.F.2 [Behavioral]: When the data source returns a range smaller than requested
    /// (partial fulfilment), the cache stores only what was returned — it does NOT use the requested range.
    /// A subsequent request for the same original range will be a PartialHit or FullMiss (not FullHit).
    /// </summary>
    [Fact]
    public async Task Invariant_VPC_F_2_PartialFulfillment_CachesOnlyActualReturnedRange()
    {
        // ARRANGE — BoundedDataSource serves [1000, 9999]; request crossing the lower boundary
        var boundedSource = new BoundedDataSource();
        var cache = TrackCache(TestHelpers.CreateCache(
            boundedSource, _domain, TestHelpers.CreateDefaultOptions(), _diagnostics));

        // ACT — request [990, 1009]: only [1000, 1009] is within the boundary
        var crossBoundaryRange = TestHelpers.CreateRange(990, 1009);
        var result = await cache.GetDataAndWaitForIdleAsync(crossBoundaryRange);

        // ASSERT — one segment stored (only the fulfillable part [1000, 1009])
        Assert.Equal(1, _diagnostics.BackgroundSegmentStored);

        // The portion [1000, 1009] is now a FullHit; re-requesting it doesn't call the source
        var innerResult = await cache.GetDataAsync(
            TestHelpers.CreateRange(1000, 1009), CancellationToken.None);
        Assert.Equal(CacheInteraction.FullHit, innerResult.CacheInteraction);
        Assert.Equal(10, innerResult.Data.Length);
        Assert.Equal(1000, innerResult.Data.Span[0]);

        await cache.WaitForIdleAsync();
    }

    // ============================================================
    // VPC.F.4 — CancellationToken Propagated to FetchAsync
    // ============================================================

    /// <summary>
    /// Invariant VPC.F.4 [Behavioral]: The <c>CancellationToken</c> passed to <c>GetDataAsync</c>
    /// is forwarded to <c>IDataSource.FetchAsync</c>. Cancelling the token before the fetch
    /// completes causes <c>GetDataAsync</c> to throw <c>OperationCanceledException</c>.
    /// </summary>
    [Fact]
    public async Task Invariant_VPC_F_4_CancellationToken_PropagatedToFetchAsync()
    {
        // ARRANGE — use a data source that delays fetch so we can cancel mid-flight
        var delaySource = new CancellableDelayDataSource(delay: TimeSpan.FromMilliseconds(500));
        var cache = TrackCache(TestHelpers.CreateCache(
            delaySource, _domain, TestHelpers.CreateDefaultOptions(), _diagnostics));

        using var cts = new CancellationTokenSource();

        // Cancel after a short delay so the fetch is in-flight
        _ = Task.Run(async () =>
        {
            await Task.Delay(50, CancellationToken.None);
            await cts.CancelAsync();
        }, CancellationToken.None);

        // ACT
        var exception = await Record.ExceptionAsync(() =>
            cache.GetDataAsync(TestHelpers.CreateRange(0, 9), cts.Token).AsTask());

        // ASSERT — cancellation propagated to the data source
        Assert.NotNull(exception);
        Assert.IsAssignableFrom<OperationCanceledException>(exception);

        await cache.WaitForIdleAsync();
    }

    // ============================================================
    // VPC.E.1a — OR-Combined Policies: Any Exceeded Triggers Eviction
    // ============================================================

    /// <summary>
    /// Invariant VPC.E.1a [Behavioral]: Eviction is triggered when ANY configured policy is exceeded
    /// (OR-combination). A single <c>MaxSegmentCountPolicy(1)</c> alone is sufficient to trigger
    /// eviction when a second segment is stored — no other policy is required.
    /// </summary>
    [Fact]
    public async Task Invariant_VPC_E_1a_AnyPolicyExceeded_TriggersEviction()
    {
        // ARRANGE — a single MaxSegmentCountPolicy(1) plus a permissive MaxSegmentCountPolicy(100).
        // Only the first policy can be exceeded. Eviction fires if either is exceeded (OR logic).
        var policies = new IEvictionPolicy<int, int>[]
        {
            new MaxSegmentCountPolicy<int, int>(1),
            new MaxSegmentCountPolicy<int, int>(100)
        };
        var selector = new LruEvictionSelector<int, int>();
        var cache = TrackCache(new VisitedPlacesCache<int, int, IntegerFixedStepDomain>(
            new SimpleTestDataSource(), _domain, TestHelpers.CreateDefaultOptions(),
            policies, selector, _diagnostics));

        // ACT — store two segments: first at capacity (count=1 → eviction fires at second)
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(0, 9));
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(100, 109));

        // ASSERT — eviction triggered (MaxSegmentCountPolicy(1) was exceeded)
        Assert.True(_diagnostics.EvictionTriggered >= 1,
            "Eviction must fire when any policy is exceeded (OR logic).");

        // Second segment (just-stored) must survive (VPC.E.3 immunity)
        var result = await cache.GetDataAsync(TestHelpers.CreateRange(100, 109), CancellationToken.None);
        Assert.Equal(CacheInteraction.FullHit, result.CacheInteraction);

        await cache.WaitForIdleAsync();
    }

    // ============================================================
    // VPC.E.3a — Only Segment at Capacity: Eviction Is a No-Op
    // ============================================================

    /// <summary>
    /// Invariant VPC.E.3a [Behavioral]: When eviction is triggered but the just-stored segment is
    /// the only segment in the cache, the eviction loop finds no eligible candidates (all are immune)
    /// and becomes a no-op. The segment survives and is immediately accessible.
    /// </summary>
    [Fact]
    public async Task Invariant_VPC_E_3a_OnlySegmentAtCapacity_EvictionIsNoOp()
    {
        // ARRANGE — maxSegmentCount=1; first store immediately hits capacity.
        // The just-stored segment is the ONLY segment AND it is immune — eviction loop is a no-op.
        var cache = CreateCache(maxSegmentCount: 1);

        // ACT — store first (and only) segment
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(0, 9));

        // ASSERT — eviction was evaluated (policy is exceeded: count 1 >= limit 1) ...
        Assert.Equal(1, _diagnostics.EvictionEvaluated);
        // ... but NO segment was removed (just-stored segment is immune)
        Assert.Equal(0, _diagnostics.EvictionSegmentRemoved);

        // The only segment is still accessible as a FullHit
        var result = await cache.GetDataAsync(TestHelpers.CreateRange(0, 9), CancellationToken.None);
        Assert.Equal(CacheInteraction.FullHit, result.CacheInteraction);
        TestHelpers.AssertUserDataCorrect(result.Data, TestHelpers.CreateRange(0, 9));

        await cache.WaitForIdleAsync();
    }

    // ============================================================
    // VPC.T.3 — Disposal Cancels Pending TTL Work Items
    // ============================================================

    /// <summary>
    /// Invariant VPC.T.3 [Behavioral]: Pending TTL work items are cancelled when the cache is disposed.
    /// No TTL-related background failures should occur after disposal.
    /// </summary>
    [Fact]
    public async Task Invariant_VPC_T_3_Disposal_CancelsPendingTtlWorkItems()
    {
        // ARRANGE — very long TTL so the work item will definitely still be pending at disposal time
        var options = new VisitedPlacesCacheOptions<int, int>(
            eventChannelCapacity: 128,
            segmentTtl: TimeSpan.FromHours(1));
        var cache = TrackCache(TestHelpers.CreateCacheWithSimpleSource(_domain, _diagnostics, options));

        // ACT — store a segment (schedules a TTL work item with a 1-hour delay)
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(0, 9));
        Assert.Equal(1, _diagnostics.TtlWorkItemScheduled);

        // Dispose immediately — the pending Task.Delay for 1 hour must be cancelled
        await cache.DisposeAsync();

        // Brief wait to allow any would-be TTL activity to surface (should be silent)
        await Task.Delay(100);

        // ASSERT — no TTL expiration (the delay was cancelled) and no background failures
        Assert.Equal(0, _diagnostics.TtlSegmentExpired);
        Assert.Equal(0, _diagnostics.BackgroundOperationFailed);
    }

    // ============================================================
    // VPC.C.7 — Snapshot Normalization Correctness
    // ============================================================

    /// <summary>
    /// Invariant VPC.C.7 [Behavioral]: <c>SnapshotAppendBufferStorage</c> normalizes atomically.
    /// After the append buffer is flushed into the snapshot (at buffer capacity), all previously
    /// added segments remain accessible — none are lost during the normalization pass.
    /// </summary>
    [Fact]
    public async Task Invariant_VPC_C_7_SnapshotNormalization_AllSegmentsRetainedAfterFlush()
    {
        // ARRANGE — use AppendBufferSize=3 to trigger normalization after every 3 additions.
        // Storing 9 non-overlapping segments forces 3 normalization passes.
        var storageOptions = new SnapshotAppendBufferStorageOptions<int, int>(appendBufferSize: 3);
        var cache = CreateCache(storageOptions, maxSegmentCount: 100);

        var ranges = Enumerable.Range(0, 9)
            .Select(i => TestHelpers.CreateRange(i * 20, i * 20 + 9))
            .ToArray();

        // ACT — store all segments sequentially, waiting for each to be processed
        foreach (var range in ranges)
        {
            await cache.GetDataAndWaitForIdleAsync(range);
        }

        // ASSERT — all 9 segments were stored
        Assert.Equal(9, _diagnostics.BackgroundSegmentStored);

        // All 9 segments are still accessible as FullHits (normalization didn't lose any)
        foreach (var range in ranges)
        {
            var result = await cache.GetDataAsync(range, CancellationToken.None);
            Assert.Equal(CacheInteraction.FullHit, result.CacheInteraction);
            TestHelpers.AssertUserDataCorrect(result.Data, range);
        }

        await cache.WaitForIdleAsync();
    }

    // ============================================================
    // VPC.A.9b — DataSourceFetchGap Diagnostic
    // ============================================================

    /// <summary>
    /// Invariant VPC.A.9b [Behavioral]: The <c>DataSourceFetchGap</c> diagnostic fires exactly once
    /// per gap fetch. A full miss fires once; a partial hit fires once per distinct gap;
    /// a full hit fires zero times.
    /// </summary>
    [Fact]
    public async Task Invariant_VPC_A_9b_DataSourceFetchGap_FiredOncePerGap()
    {
        // ARRANGE
        var spy = new SpyDataSource();
        var cache = TrackCache(TestHelpers.CreateCache(
            spy, _domain, TestHelpers.CreateDefaultOptions(), _diagnostics));

        // ACT — full miss: 1 gap fetch
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(0, 9));
        Assert.Equal(1, _diagnostics.DataSourceFetchGap);

        // ACT — full hit: 0 gap fetches
        _diagnostics.Reset();
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(0, 9));
        Assert.Equal(0, _diagnostics.DataSourceFetchGap);

        // ACT — partial hit: [0,9] cached; request [5,14] has one gap [10,14] → 1 gap fetch
        _diagnostics.Reset();
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(5, 14));
        Assert.Equal(1, _diagnostics.DataSourceFetchGap);

        // ACT — two-gap partial hit: [0,9] and [20,29] cached; [0,29] has one gap [10,19] → 1 fetch
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(20, 29));
        _diagnostics.Reset();
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(0, 29));
        Assert.Equal(1, _diagnostics.DataSourceFetchGap);

        await cache.WaitForIdleAsync();
    }

    // ============================================================
    // VPC.B.1 — Strict FIFO Event Ordering
    // ============================================================

    /// <summary>
    /// Invariant VPC.B.1 [Architectural]: Every <c>CacheNormalizationRequest</c> is processed
    /// in strict FIFO order — no request is superseded, skipped, or discarded.
    /// Verifies that after N sequential full-miss requests, all N normalization requests are
    /// received AND processed, and all N segments are present in the cache (as FullHits).
    /// If any event were superseded (as in SWC's latest-intent-wins model), some segments
    /// would be missing from cache and subsequent full-hit reads would fail.
    /// </summary>
    [Theory]
    [MemberData(nameof(StorageStrategyTestData))]
    public async Task Invariant_VPC_B_1_StrictFifoOrdering_AllRequestsProcessed(
        StorageStrategyOptions<int, int> strategy)
    {
        #region Arrange

        const int requestCount = 10;

        // Create non-overlapping ranges so each request produces exactly one new segment.
        // Stride of 20 guarantees no adjacency merging.
        var ranges = Enumerable.Range(0, requestCount)
            .Select(i => TestHelpers.CreateRange(i * 20, i * 20 + 9))
            .ToArray();

        var cache = CreateCache(strategy, maxSegmentCount: requestCount + 10);

        #endregion

        #region Act

        // Issue all requests sequentially, waiting for idle after each one so that
        // segments are stored before the next request.
        // This ensures NormalizationRequestReceived == requestCount at the end.
        foreach (var range in ranges)
        {
            await cache.GetDataAndWaitForIdleAsync(range);
        }

        #endregion

        #region Assert

        // VPC.B.1: every request received must have been processed — no events discarded.
        Assert.Equal(requestCount, _diagnostics.NormalizationRequestReceived);
        Assert.Equal(requestCount, _diagnostics.NormalizationRequestProcessed);

        // All requestCount segments must be stored — no segment was superseded.
        Assert.Equal(requestCount, _diagnostics.BackgroundSegmentStored);

        // Re-read all ranges: every one must be a FullHit, proving the segment was stored and is
        // retrievable — this would fail if any event had been dropped or processed out of order.
        foreach (var range in ranges)
        {
            var result = await cache.GetDataAsync(range, CancellationToken.None);
            Assert.Equal(CacheInteraction.FullHit, result.CacheInteraction);
            TestHelpers.AssertUserDataCorrect(result.Data, range);
        }

        // No background failures.
        Assert.Equal(0, _diagnostics.BackgroundOperationFailed);

        #endregion
    }

    // ============================================================
    // TEST DOUBLES
    // ============================================================

    /// <summary>
    /// A data source that introduces a delay to simulate slow I/O.
    /// Used to verify that GetDataAsync does not block on the background path.
    /// </summary>
    private sealed class SlowDataSource : IDataSource<int, int>
    {
        private readonly TimeSpan _delay;

        public SlowDataSource(TimeSpan delay) => _delay = delay;

        public async Task<RangeChunk<int, int>> FetchAsync(Range<int> range, CancellationToken cancellationToken)
        {
            await Task.Delay(_delay, cancellationToken);
            var data = DataGenerationHelpers.GenerateDataForRange(range);
            return new RangeChunk<int, int>(range, data);
        }
    }

    /// <summary>
    /// A data source that delays fetches and respects cancellation.
    /// Used to verify that the CancellationToken is propagated to FetchAsync.
    /// </summary>
    private sealed class CancellableDelayDataSource : IDataSource<int, int>
    {
        private readonly TimeSpan _delay;

        public CancellableDelayDataSource(TimeSpan delay) => _delay = delay;

        public async Task<RangeChunk<int, int>> FetchAsync(Range<int> range, CancellationToken cancellationToken)
        {
            await Task.Delay(_delay, cancellationToken);
            var data = DataGenerationHelpers.GenerateDataForRange(range);
            return new RangeChunk<int, int>(range, data);
        }
    }
}
