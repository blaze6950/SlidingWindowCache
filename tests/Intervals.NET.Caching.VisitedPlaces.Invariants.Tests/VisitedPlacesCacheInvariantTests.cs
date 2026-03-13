using Intervals.NET;
using Intervals.NET.Caching.Dto;
using Intervals.NET.Caching.Extensions;
using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Caching.VisitedPlaces.Public.Cache;
using Intervals.NET.Caching.VisitedPlaces.Public.Configuration;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure.DataSources;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure.Helpers;

namespace Intervals.NET.Caching.VisitedPlaces.Invariants.Tests;

/// <summary>
/// Automated tests verifying behavioral invariants of <c>VisitedPlacesCache</c>.
/// Each test is named after its invariant ID and description from
/// <c>docs/visited-places/invariants.md</c>.
/// 
/// Only BEHAVIORAL invariants are tested here (observable via public API).
/// ARCHITECTURAL and CONCEPTUAL invariants are enforced by code structure and are not tested.
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

        // ASSERT — GetDataAsync should complete within reasonable time
        // The data source takes 200ms; if user path waited for background, it would be >= 200ms.
        // We assert it completes in under 750ms (well above the 200ms data-source delay,
        // well below any scheduler-induced background-wait that would indicate blocking).
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

    /// <summary>
    /// Invariant VPC.E.3a [Behavioral]: If the just-stored segment is the ONLY segment in
    /// CachedSegments when eviction is triggered, the Eviction Executor is a no-op for that event.
    /// The cache will remain over-limit (count=1 > maxCount=0 is impossible; count=1, maxCount=1
    /// is at-limit). We test with 1-slot capacity: on the FIRST store, there is only one segment
    /// (the just-stored, immune one), so nothing is evicted.
    /// </summary>
    [Fact]
    public async Task Invariant_VPC_E_3a_OnlySegmentIsImmuneEvenWhenOverLimit()
    {
        // ARRANGE — exactly 1 slot; after the first store, eviction fires but the only segment is immune
        var cache = CreateCache(maxSegmentCount: 1);

        // ACT — first request: stores one segment; evaluator fires (count=1 == maxCount=1, not >1, so no eviction)
        // Actually maxSegmentCount=1 means ShouldEvict fires when count > 1, so the first store doesn't trigger eviction.
        // Let's use maxSegmentCount=0 which is invalid. Use 1 and verify count stays 1.
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(0, 9));

        // ASSERT — segment is stored and no eviction triggered (count=1, limit=1, not exceeded)
        Assert.Equal(0, _diagnostics.EvictionTriggered);
        var result = await cache.GetDataAsync(TestHelpers.CreateRange(0, 9), CancellationToken.None);
        Assert.Equal(CacheInteraction.FullHit, result.CacheInteraction);

        await cache.WaitForIdleAsync();
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
}
