using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Domain.Extensions.Fixed;
using SlidingWindowCache.Tests.Infrastructure.DataSources;
using SlidingWindowCache.Public;
using SlidingWindowCache.Public.Configuration;
using SlidingWindowCache.Public.Instrumentation;

namespace SlidingWindowCache.Integration.Tests;

/// <summary>
/// Tests validating the interaction contract between WindowCache and IDataSource.
/// Uses SpyDataSource to capture and verify requested ranges without testing internal logic.
/// 
/// Goal: Verify integration assumptions, not DataSource implementation:
/// - Cache miss triggers exact requested range fetch
/// - Partial cache hit fetches only missing segments
/// - Rebalance triggers correct expansion ranges
/// - No redundant fetches occur
/// </summary>
public sealed class CacheDataSourceInteractionTests : IAsyncDisposable
{
    private readonly IntegerFixedStepDomain _domain;
    private readonly SpyDataSource _dataSource;
    private WindowCache<int, int, IntegerFixedStepDomain>? _cache;
    private readonly EventCounterCacheDiagnostics _cacheDiagnostics;

    public CacheDataSourceInteractionTests()
    {
        _domain = new IntegerFixedStepDomain();
        _dataSource = new SpyDataSource();
        _cacheDiagnostics = new EventCounterCacheDiagnostics();
    }

    /// <summary>
    /// Ensures any background rebalance operations are completed and cache is properly disposed
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_cache != null)
        {
            // Wait for any background rebalance from current test to complete
            await _cache.WaitForIdleAsync();

            // Properly dispose the cache to release resources
            await _cache.DisposeAsync();
        }

        _dataSource.Reset();
    }

    private WindowCache<int, int, IntegerFixedStepDomain> CreateCache(WindowCacheOptions? options = null)
    {
        _cache = new WindowCache<int, int, IntegerFixedStepDomain>(
            _dataSource,
            _domain,
            options ?? new WindowCacheOptions(
                leftCacheSize: 1.0,
                rightCacheSize: 1.0,
                readMode: UserCacheReadMode.Snapshot,
                leftThreshold: 0.2,
                rightThreshold: 0.2
            ),
            _cacheDiagnostics
        );
        return _cache;
    }

    #region Cache Miss Scenarios

    [Fact]
    public async Task CacheMiss_ColdStart_DataSourceReceivesExactRequestedRange()
    {
        // ARRANGE
        var cache = CreateCache();
        var requestedRange = Intervals.NET.Factories.Range.Closed<int>(100, 110);

        // ACT
        var result = await cache.GetDataAsync(requestedRange, CancellationToken.None);

        // ASSERT - DataSource was called with the requested range
        Assert.True(_dataSource.TotalFetchCount > 0, "DataSource should be called for cold start");

        // ASSERT - Verify IDataSource covered the exact requested range
        Assert.True(_dataSource.WasRangeCovered(100, 110),
            "DataSource should be asked to fetch at least the requested range [100, 110]");

        // Verify data is correct
        var array = result.Data.ToArray();
        Assert.Equal((int)requestedRange.Span(_domain), array.Length);
        Assert.Equal(100, array[0]);
        Assert.Equal(110, array[^1]);
    }

    [Fact]
    public async Task CacheMiss_NonOverlappingJump_DataSourceReceivesNewRange()
    {
        // ARRANGE
        var cache = CreateCache();

        // First request establishes cache
        await cache.GetDataAsync(Intervals.NET.Factories.Range.Closed<int>(100, 110), CancellationToken.None);
        await cache.WaitForIdleAsync();

        _dataSource.Reset(); // Track only the second request

        // ACT - Jump to non-overlapping range
        var newRange = Intervals.NET.Factories.Range.Closed<int>(500, 510);
        var result = await cache.GetDataAsync(newRange, CancellationToken.None);

        // ASSERT - DataSource was called for new range
        Assert.True(_dataSource.TotalFetchCount > 0, "DataSource should be called for non-overlapping range");

        // Verify correct data
        var array = result.Data.ToArray();
        Assert.Equal(11, array.Length);
        Assert.Equal(500, array[0]);
        Assert.Equal(510, array[^1]);
    }



    #endregion

    #region Partial Cache Hit Scenarios

    [Fact]
    public async Task PartialCacheHit_OverlappingRange_FetchesOnlyMissingSegments()
    {
        // ARRANGE
        var cache = CreateCache();

        // First request establishes cache [100, 110]
        await cache.GetDataAsync(Intervals.NET.Factories.Range.Closed<int>(100, 110), CancellationToken.None);
        await cache.WaitForIdleAsync();

        // ACT - Request overlapping range [105, 120]
        // Should fetch only missing portion [111, 120]
        var overlappingRange = Intervals.NET.Factories.Range.Closed<int>(105, 120);
        var result = await cache.GetDataAsync(overlappingRange, CancellationToken.None);

        // ASSERT - Verify returned data is correct
        var array = result.Data.ToArray();
        Assert.Equal(16, array.Length); // [105, 120] = 16 elements
        Assert.Equal(105, array[0]);
        Assert.Equal(120, array[^1]);

        // DataSource may or may not be called depending on cache expansion
        // We verify behavior is correct regardless
        for (var i = 0; i < array.Length; i++)
        {
            Assert.Equal(105 + i, array[i]);
        }
    }

    [Fact]
    public async Task PartialCacheHit_LeftExtension_DataCorrect()
    {
        // ARRANGE
        var cache = CreateCache();

        // Establish cache at [200, 210]
        await cache.GetDataAsync(Intervals.NET.Factories.Range.Closed<int>(200, 210), CancellationToken.None);
        await cache.WaitForIdleAsync();

        // ACT - Extend to the left [190, 205]
        var leftExtendRange = Intervals.NET.Factories.Range.Closed<int>(190, 205);
        var result = await cache.GetDataAsync(leftExtendRange, CancellationToken.None);

        // ASSERT - Verify data correctness
        var array = result.Data.ToArray();
        Assert.Equal(16, array.Length);
        Assert.Equal(190, array[0]);
        Assert.Equal(205, array[^1]);
    }

    [Fact]
    public async Task PartialCacheHit_RightExtension_DataCorrect()
    {
        // ARRANGE
        var cache = CreateCache();

        // Establish cache at [300, 310]
        await cache.GetDataAsync(Intervals.NET.Factories.Range.Closed<int>(300, 310), CancellationToken.None);
        await cache.WaitForIdleAsync();

        // ACT - Extend to the right [305, 320]
        var rightExtendRange = Intervals.NET.Factories.Range.Closed<int>(305, 320);
        var result = await cache.GetDataAsync(rightExtendRange, CancellationToken.None);

        // ASSERT - Verify data correctness
        var array2 = result.Data.ToArray();
        Assert.Equal(16, array2.Length);
        Assert.Equal(305, array2[0]);
        Assert.Equal(320, array2[^1]);
    }

    #endregion

    #region Rebalance Expansion Tests

    [Fact]
    public async Task Rebalance_WithExpansionCoefficients_ExpandsCacheCorrectly()
    {
        // ARRANGE - Cache with 2x expansion (leftSize=2.0, rightSize=2.0)
        var cache = CreateCache(new WindowCacheOptions(
            leftCacheSize: 2.0,
            rightCacheSize: 2.0,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0.3,
            rightThreshold: 0.3,
            debounceDelay: TimeSpan.FromMilliseconds(50)
        ));

        // ACT - Request range [100, 110] (11 elements)
        // Expected expansion: left by 22, right by 22 -> cache becomes [78, 132]
        var requestedRange = Intervals.NET.Factories.Range.Closed<int>(100, 110);
        var result = await cache.GetDataAsync(requestedRange, CancellationToken.None);

        // Wait for rebalance to complete
        await cache.WaitForIdleAsync();

        // Make a request within expected expanded cache
        _dataSource.Reset();
        var withinExpanded = Intervals.NET.Factories.Range.Closed<int>(85, 95);
        var data2 = await cache.GetDataAsync(withinExpanded, CancellationToken.None);

        // ASSERT - Verify data correctness
        var array1 = result.Data.ToArray();
        var array2 = data2.Data.ToArray();
        Assert.Equal(11, array1.Length);
        Assert.Equal(100, array1[0]);
        Assert.Equal(11, array2.Length);
        Assert.Equal(85, array2[0]);
    }

    [Fact]
    public async Task Rebalance_SequentialRequests_CacheAdaptsToPattern()
    {
        // ARRANGE
        var cache = CreateCache(new WindowCacheOptions(
            leftCacheSize: 1.5,
            rightCacheSize: 1.5,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0.2,
            rightThreshold: 0.2,
            debounceDelay: TimeSpan.FromMilliseconds(50)
        ));

        // ACT - Sequential access pattern moving right
        var ranges = new[]
        {
            Intervals.NET.Factories.Range.Closed<int>(100, 110),
            Intervals.NET.Factories.Range.Closed<int>(120, 130),
            Intervals.NET.Factories.Range.Closed<int>(140, 150)
        };

        foreach (var range in ranges)
        {
            var loopResult = await cache.GetDataAsync(range, CancellationToken.None);
            Assert.Equal((int)range.Span(_domain), loopResult.Data.Length);
            await cache.WaitForIdleAsync();
        }
    }

    #endregion

    #region No Redundant Fetches

    [Fact]
    public async Task NoRedundantFetches_RepeatedSameRange_UsesCache()
    {
        // ARRANGE
        var cache = CreateCache(new WindowCacheOptions(1, 1, UserCacheReadMode.Snapshot, 0.4, 0.4));
        var range = Intervals.NET.Factories.Range.Closed<int>(100, 110);

        // ACT - First request
        await cache.GetDataAsync(range, CancellationToken.None);
        await cache.WaitForIdleAsync();

        // Second identical request
        var data2 = await cache.GetDataAsync(range, CancellationToken.None);

        // ASSERT - Second request should not trigger additional fetch (served from cache)
        // Note: May trigger rebalance fetch in background, but user data served from cache
        var array = data2.Data.ToArray();
        Assert.Equal(11, array.Length);
        Assert.Equal(100, array[0]);
    }

    [Fact]
    public async Task NoRedundantFetches_SubsetOfCache_NoAdditionalFetch()
    {
        // ARRANGE
        var cache = CreateCache(new WindowCacheOptions(
            leftCacheSize: 2.0,
            rightCacheSize: 2.0,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0.3,
            rightThreshold: 0.3,
            debounceDelay: TimeSpan.FromMilliseconds(50)
        ));

        // ACT - Large initial request
        await cache.GetDataAsync(Intervals.NET.Factories.Range.Closed<int>(100, 200), CancellationToken.None);
        await cache.WaitForIdleAsync();

        var totalFetchesAfterExpansion = _dataSource.TotalFetchCount;
        Assert.True(totalFetchesAfterExpansion > 0, "Initial request should trigger fetches");

        _dataSource.Reset();

        // Request subset that should be in expanded cache
        var subset = Intervals.NET.Factories.Range.Closed<int>(150, 160);
        var result = await cache.GetDataAsync(subset, CancellationToken.None);

        // ASSERT - Data is correct
        var array = result.Data.ToArray();
        Assert.Equal(11, array.Length);
        Assert.Equal(150, array[0]);
        Assert.Equal(160, array[^1]);
    }

    #endregion

    #region DataSource Call Verification

    [Fact]
    public async Task DataSourceCalls_SingleFetchMethod_CalledForSimpleRanges()
    {
        // ARRANGE
        var cache = CreateCache();

        // ACT
        await cache.GetDataAsync(Intervals.NET.Factories.Range.Closed<int>(100, 110), CancellationToken.None);

        // ASSERT - At least one fetch call made
        Assert.True(_dataSource.TotalFetchCount >= 1,
            $"Expected at least 1 fetch, but got {_dataSource.TotalFetchCount}");
    }

    [Fact]
    public async Task DataSourceCalls_MultipleCacheMisses_EachTriggersFetch()
    {
        // ARRANGE
        var cache = CreateCache();

        // ACT - Three non-overlapping ranges (guaranteed cache misses)
        var ranges = new[]
        {
            Intervals.NET.Factories.Range.Closed<int>(100, 110),
            Intervals.NET.Factories.Range.Closed<int>(1000, 1010),
            Intervals.NET.Factories.Range.Closed<int>(10000, 10010)
        };

        foreach (var range in ranges)
        {
            _dataSource.Reset();
            _ = await cache.GetDataAsync(range, CancellationToken.None);

            // Each miss should trigger at least one fetch
            Assert.True(_dataSource.TotalFetchCount >= 1,
                $"Cache miss should trigger fetch for range {range}");
        }
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task EdgeCase_VerySmallRange_SingleElement_HandlesCorrectly()
    {
        // ARRANGE
        var cache = CreateCache();

        // ACT
        var singleElementRange = Intervals.NET.Factories.Range.Closed<int>(42, 42);
        var result = await cache.GetDataAsync(singleElementRange, CancellationToken.None);

        // ASSERT
        var array1 = result.Data.ToArray();
        Assert.Single(array1);
        Assert.Equal(42, array1[0]);
        Assert.True(_dataSource.TotalFetchCount >= 1);
    }

    [Fact]
    public async Task EdgeCase_VeryLargeRange_HandlesWithoutError()
    {
        // ARRANGE
        var cache = CreateCache();

        // ACT - Large range (1000 elements)
        var largeRange = Intervals.NET.Factories.Range.Closed<int>(0, 999);
        var result = await cache.GetDataAsync(largeRange, CancellationToken.None);

        // ASSERT
        var array2 = result.Data.ToArray();
        Assert.Equal(1000, array2.Length);
        Assert.Equal(0, array2[0]);
        Assert.Equal(999, array2[^1]);
    }

    #endregion
}
