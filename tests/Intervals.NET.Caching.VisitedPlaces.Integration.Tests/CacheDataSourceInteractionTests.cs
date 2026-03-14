using Intervals.NET.Caching.Dto;
using Intervals.NET.Caching.Extensions;
using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Caching.VisitedPlaces.Public.Cache;
using Intervals.NET.Caching.VisitedPlaces.Public.Configuration;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure.DataSources;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure.Helpers;

namespace Intervals.NET.Caching.VisitedPlaces.Integration.Tests;

/// <summary>
/// Integration tests validating the interaction between VisitedPlacesCache and IDataSource.
/// Tests the full request/response cycle, diagnostics counters, and both storage strategies.
/// Uses <c>WaitForIdleAsync</c> to drive the cache to a deterministic state before assertions.
/// </summary>
public sealed class CacheDataSourceInteractionTests : IAsyncDisposable
{
    private readonly IntegerFixedStepDomain _domain = new();
    private readonly SpyDataSource _dataSource = new();
    private readonly EventCounterCacheDiagnostics _diagnostics = new();
    private VisitedPlacesCache<int, int, IntegerFixedStepDomain>? _cache;

    public async ValueTask DisposeAsync()
    {
        if (_cache != null)
        {
            await _cache.WaitForIdleAsync();
            await _cache.DisposeAsync();
        }

        _dataSource.Reset();
    }

    private VisitedPlacesCache<int, int, IntegerFixedStepDomain> CreateCache(
        StorageStrategyOptions<int, int>? strategy = null,
        int maxSegmentCount = 100)
    {
        _cache = TestHelpers.CreateCache(
            _dataSource,
            _domain,
            TestHelpers.CreateDefaultOptions(strategy),
            _diagnostics,
            maxSegmentCount);
        return _cache;
    }

    private static StorageStrategyOptions<int, int> CreateStrategyFromType(Type strategyType)
    {
        if (strategyType == typeof(SnapshotAppendBufferStorageOptions<int, int>))
        {
            return SnapshotAppendBufferStorageOptions<int, int>.Default;
        }

        if (strategyType == typeof(LinkedListStrideIndexStorageOptions<int, int>))
        {
            return LinkedListStrideIndexStorageOptions<int, int>.Default;
        }

        throw new ArgumentException($"Unknown strategy type: {strategyType}", nameof(strategyType));
    }

    // ============================================================
    // CACHE MISS SCENARIOS
    // ============================================================

    [Fact]
    public async Task FullMiss_ColdStart_FetchesFromDataSource()
    {
        // ARRANGE
        var cache = CreateCache();
        var range = TestHelpers.CreateRange(100, 110);

        // ACT
        var result = await cache.GetDataAndWaitForIdleAsync(range);

        // ASSERT — data source was called
        Assert.True(_dataSource.TotalFetchCount >= 1);
        Assert.True(_dataSource.WasRangeCovered(100, 110));
        Assert.Equal(CacheInteraction.FullMiss, result.CacheInteraction);
        Assert.Equal(11, result.Data.Length);
        Assert.Equal(100, result.Data.Span[0]);
        Assert.Equal(110, result.Data.Span[^1]);
    }

    [Fact]
    public async Task FullMiss_DiagnosticsCountersAreCorrect()
    {
        // ARRANGE
        var cache = CreateCache();

        // ACT
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(0, 9));

        // ASSERT
        Assert.Equal(1, _diagnostics.UserRequestServed);
        Assert.Equal(1, _diagnostics.UserRequestFullCacheMiss);
        Assert.Equal(0, _diagnostics.UserRequestFullCacheHit);
        Assert.Equal(0, _diagnostics.UserRequestPartialCacheHit);
        Assert.Equal(1, _diagnostics.NormalizationRequestProcessed);
        Assert.True(_diagnostics.BackgroundSegmentStored >= 1);
    }

    // ============================================================
    // CACHE HIT SCENARIOS
    // ============================================================

    [Fact]
    public async Task FullHit_AfterCaching_DoesNotCallDataSource()
    {
        // ARRANGE
        var cache = CreateCache();

        // Warm up cache
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(0, 9));
        _dataSource.Reset();
        _diagnostics.Reset();

        // ACT — same range again; should be a full hit
        var result = await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(0, 9));

        // ASSERT
        Assert.Equal(CacheInteraction.FullHit, result.CacheInteraction);
        Assert.Equal(0, _dataSource.TotalFetchCount);
        Assert.Equal(1, _diagnostics.UserRequestFullCacheHit);
        Assert.Equal(10, result.Data.Length);
    }

    [Fact]
    public async Task FullHit_DataIsCorrect()
    {
        // ARRANGE
        var cache = CreateCache();
        var range = TestHelpers.CreateRange(50, 60);

        await cache.GetDataAndWaitForIdleAsync(range);

        // ACT — second request should be a full hit
        var result = await cache.GetDataAndWaitForIdleAsync(range);

        // ASSERT
        Assert.Equal(CacheInteraction.FullHit, result.CacheInteraction);
        TestHelpers.AssertUserDataCorrect(result.Data, range);
    }

    // ============================================================
    // PARTIAL HIT SCENARIOS
    // ============================================================

    [Fact]
    public async Task PartialHit_OverlappingRange_FetchesOnlyMissingPart()
    {
        // ARRANGE
        var cache = CreateCache();

        // Cache [0, 9]
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(0, 9));
        _dataSource.Reset();

        // ACT — request [5, 14]: overlaps cached [0,9] on the right
        var result = await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(5, 14));

        // ASSERT
        Assert.Equal(CacheInteraction.PartialHit, result.CacheInteraction);
        Assert.True(_dataSource.TotalFetchCount >= 1, "Should fetch missing portion [10,14]");
        Assert.Equal(10, result.Data.Length);
        TestHelpers.AssertUserDataCorrect(result.Data, TestHelpers.CreateRange(5, 14));
    }

    [Fact]
    public async Task PartialHit_DiagnosticsCountersAreCorrect()
    {
        // ARRANGE
        var cache = CreateCache();

        // Cache [0, 9]
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(0, 9));
        _diagnostics.Reset();

        // ACT — request [5, 14]
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(5, 14));

        // ASSERT
        Assert.Equal(1, _diagnostics.UserRequestPartialCacheHit);
        Assert.Equal(0, _diagnostics.UserRequestFullCacheHit);
        Assert.Equal(0, _diagnostics.UserRequestFullCacheMiss);
    }

    // ============================================================
    // MULTIPLE SEQUENTIAL REQUESTS
    // ============================================================

    [Fact]
    public async Task MultipleRequests_NonOverlapping_AllServedCorrectly()
    {
        // ARRANGE
        var cache = CreateCache();
        var ranges = new[]
        {
            TestHelpers.CreateRange(0, 9),
            TestHelpers.CreateRange(100, 109),
            TestHelpers.CreateRange(1000, 1009)
        };

        // ACT & ASSERT — each request should be a full miss and return correct data
        foreach (var range in ranges)
        {
            var result = await cache.GetDataAndWaitForIdleAsync(range);
            Assert.Equal(10, result.Data.Length);
            TestHelpers.AssertUserDataCorrect(result.Data, range);
        }
    }

    [Fact]
    public async Task MultipleRequests_Repeated_UseCachedData()
    {
        // ARRANGE
        var cache = CreateCache();
        var range = TestHelpers.CreateRange(200, 210);

        // Warm up
        await cache.GetDataAndWaitForIdleAsync(range);
        _diagnostics.Reset();

        // ACT — repeat 3 times; all should be full hits
        for (var i = 0; i < 3; i++)
        {
            var result = await cache.GetDataAndWaitForIdleAsync(range);
            Assert.Equal(CacheInteraction.FullHit, result.CacheInteraction);
        }

        // ASSERT
        Assert.Equal(3, _diagnostics.UserRequestFullCacheHit);
        Assert.Equal(0, _diagnostics.UserRequestFullCacheMiss);
    }

    // ============================================================
    // EVICTION INTEGRATION
    // ============================================================

    [Fact]
    public async Task Eviction_WhenMaxSegmentsExceeded_SegmentsAreEvicted()
    {
        // ARRANGE — maxSegmentCount=2 forces eviction after 3 stores
        var cache = CreateCache(maxSegmentCount: 2);

        // Store 3 non-overlapping segments (each triggers a background event)
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(0, 9));
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(100, 109));
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(200, 209));

        // ASSERT — eviction triggered at least once
        TestHelpers.AssertEvictionTriggered(_diagnostics);
    }

    // ============================================================
    // BOTH STORAGE STRATEGIES
    // ============================================================

    [Theory]
    [InlineData(typeof(SnapshotAppendBufferStorageOptions<int, int>))]
    [InlineData(typeof(LinkedListStrideIndexStorageOptions<int, int>))]
    public async Task BothStorageStrategies_FullCycle_DataCorrect(Type strategyType)
    {
        // ARRANGE
        var strategy = CreateStrategyFromType(strategyType);
        var cache = CreateCache(strategy);
        var range = TestHelpers.CreateRange(0, 9);

        // ACT
        var firstResult = await cache.GetDataAndWaitForIdleAsync(range);
        var secondResult = await cache.GetDataAndWaitForIdleAsync(range);

        // ASSERT
        Assert.Equal(CacheInteraction.FullMiss, firstResult.CacheInteraction);
        Assert.Equal(CacheInteraction.FullHit, secondResult.CacheInteraction);
        TestHelpers.AssertUserDataCorrect(firstResult.Data, range);
        TestHelpers.AssertUserDataCorrect(secondResult.Data, range);
    }

    [Theory]
    [InlineData(typeof(SnapshotAppendBufferStorageOptions<int, int>))]
    [InlineData(typeof(LinkedListStrideIndexStorageOptions<int, int>))]
    public async Task BothStorageStrategies_ManySegments_AllFoundCorrectly(Type strategyType)
    {
        // ARRANGE
        var strategy = CreateStrategyFromType(strategyType);
        var cache = CreateCache(strategy, maxSegmentCount: 100);

        // ACT — store 12 non-overlapping segments to force normalization in both strategies
        for (var i = 0; i < 12; i++)
        {
            await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(i * 20, i * 20 + 9));
        }

        // Now request each range again — all should be full hits
        for (var i = 0; i < 12; i++)
        {
            var range = TestHelpers.CreateRange(i * 20, i * 20 + 9);
            var result = await cache.GetDataAndWaitForIdleAsync(range);
            Assert.Equal(CacheInteraction.FullHit, result.CacheInteraction);
        }
    }

    // ============================================================
    // DIAGNOSTICS LIFECYCLE INTEGRITY
    // ============================================================

    [Fact]
    public async Task DiagnosticsLifecycle_Received_EqualsProcessedPlusFailed()
    {
        // ARRANGE
        var cache = CreateCache();

        // ACT — several requests
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(0, 9));
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(0, 9));
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(50, 59));

        // ASSERT
        TestHelpers.AssertBackgroundLifecycleIntegrity(_diagnostics);
        TestHelpers.AssertNoBackgroundFailures(_diagnostics);
    }

}
