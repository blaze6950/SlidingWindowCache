using Intervals.NET.Domain.Default.Numeric;
using SlidingWindowCache.Tests.Infrastructure.DataSources;
using SlidingWindowCache.Public;
using SlidingWindowCache.Public.Cache;
using SlidingWindowCache.Public.Configuration;
using SlidingWindowCache.Public.Instrumentation;

namespace SlidingWindowCache.Integration.Tests;

/// <summary>
/// Tests that validate boundary handling when the data source has physical limits.
/// Uses BoundedDataSource (MinId=1000, MaxId=9999) to simulate a database with bounded records.
/// 
/// Scenarios covered:
/// - User Path: Physical data miss, partial hit, full hit
/// - Rebalance Path: Physical data miss, partial miss, full hit
/// </summary>
public sealed class BoundaryHandlingTests : IAsyncDisposable
{
    private readonly IntegerFixedStepDomain _domain;
    private readonly BoundedDataSource _dataSource;
    private WindowCache<int, int, IntegerFixedStepDomain>? _cache;
    private readonly EventCounterCacheDiagnostics _cacheDiagnostics;

    public BoundaryHandlingTests()
    {
        _domain = new IntegerFixedStepDomain();
        _dataSource = new BoundedDataSource();
        _cacheDiagnostics = new EventCounterCacheDiagnostics();
    }

    public async ValueTask DisposeAsync()
    {
        if (_cache != null)
        {
            await _cache.WaitForIdleAsync();
            await _cache.DisposeAsync();
        }
    }

    #region User Path - Boundary Handling

    [Fact]
    public async Task UserPath_PhysicalDataMiss_ReturnsNullRange()
    {
        // ARRANGE - Bounded data source with data in [1000, 9999]
        var cache = CreateCache();

        // Request completely below physical bounds
        var requestBelowBounds = Intervals.NET.Factories.Range.Closed<int>(0, 999);

        // ACT
        var result = await cache.GetDataAsync(requestBelowBounds, CancellationToken.None);

        // ASSERT - Range is null, data is empty
        Assert.Null(result.Range);
        Assert.True(result.Data.IsEmpty);
        Assert.Equal(0, result.Data.Length);
    }

    [Fact]
    public async Task UserPath_PhysicalDataMiss_AboveBounds_ReturnsNullRange()
    {
        // ARRANGE
        var cache = CreateCache();

        // Request completely above physical bounds
        var requestAboveBounds = Intervals.NET.Factories.Range.Closed<int>(10000, 11000);

        // ACT
        var result = await cache.GetDataAsync(requestAboveBounds, CancellationToken.None);

        // ASSERT - Range is null, data is empty
        Assert.Null(result.Range);
        Assert.True(result.Data.IsEmpty);
        Assert.Equal(0, result.Data.Length);
    }

    [Fact]
    public async Task UserPath_PartialHit_LowerBoundaryTruncation_ReturnsTruncatedRange()
    {
        // ARRANGE - Data available in [1000, 9999]
        var cache = CreateCache();

        // Request [500, 1500] - overlaps lower boundary
        // Expected: [1000, 1500] (truncated at lower boundary)
        var requestedRange = Intervals.NET.Factories.Range.Closed<int>(500, 1500);

        // ACT
        var result = await cache.GetDataAsync(requestedRange, CancellationToken.None);

        // ASSERT - Range is truncated to [1000, 1500]
        Assert.NotNull(result.Range);
        var expectedRange = Intervals.NET.Factories.Range.Closed<int>(1000, 1500);
        Assert.Equal(expectedRange, result.Range);

        // Data should contain 501 elements [1000..1500]
        Assert.Equal(501, result.Data.Length);
        Assert.Equal(1000, result.Data.Span[0]);
        Assert.Equal(1500, result.Data.Span[500]);
    }

    [Fact]
    public async Task UserPath_PartialHit_UpperBoundaryTruncation_ReturnsTruncatedRange()
    {
        // ARRANGE - Data available in [1000, 9999]
        var cache = CreateCache();

        // Request [9500, 10500] - overlaps upper boundary
        // Expected: [9500, 9999] (truncated at upper boundary)
        var requestedRange = Intervals.NET.Factories.Range.Closed<int>(9500, 10500);

        // ACT
        var result = await cache.GetDataAsync(requestedRange, CancellationToken.None);

        // ASSERT - Range is truncated to [9500, 9999]
        Assert.NotNull(result.Range);
        var expectedRange = Intervals.NET.Factories.Range.Closed<int>(9500, 9999);
        Assert.Equal(expectedRange, result.Range);

        // Data should contain 500 elements [9500..9999]
        Assert.Equal(500, result.Data.Length);
        Assert.Equal(9500, result.Data.Span[0]);
        Assert.Equal(9999, result.Data.Span[499]);
    }

    [Fact]
    public async Task UserPath_FullHit_WithinBounds_ReturnsFullRange()
    {
        // ARRANGE - Data available in [1000, 9999]
        var cache = CreateCache();

        // Request [2000, 3000] - completely within bounds
        var requestedRange = Intervals.NET.Factories.Range.Closed<int>(2000, 3000);

        // ACT
        var result = await cache.GetDataAsync(requestedRange, CancellationToken.None);

        // ASSERT - Full requested range returned
        Assert.NotNull(result.Range);
        Assert.Equal(requestedRange, result.Range);

        // Data should contain 1001 elements [2000..3000]
        Assert.Equal(1001, result.Data.Length);
        Assert.Equal(2000, result.Data.Span[0]);
        Assert.Equal(3000, result.Data.Span[1000]);
    }

    [Fact]
    public async Task UserPath_FullHit_AtExactBoundaries_ReturnsFullRange()
    {
        // ARRANGE
        var cache = CreateCache();

        // Request exactly at physical boundaries [1000, 9999]
        var requestedRange = Intervals.NET.Factories.Range.Closed<int>(1000, 9999);

        // ACT
        var result = await cache.GetDataAsync(requestedRange, CancellationToken.None);

        // ASSERT - Full range at exact boundaries
        Assert.NotNull(result.Range);
        Assert.Equal(requestedRange, result.Range);

        // Data should contain 9000 elements [1000..9999]
        Assert.Equal(9000, result.Data.Length);
        Assert.Equal(1000, result.Data.Span[0]);
        Assert.Equal(9999, result.Data.Span[8999]);
    }

    /// <summary>
    /// When a request is completely outside the physical bounds of the data source,
    /// the user path must:
    ///   - Return RangeResult with null Range and empty Data (full vacuum)
    ///   - Count the request as served (UserRequestServed == 1) — the request completed without exception
    ///   - NOT publish a rebalance intent (RebalanceIntentPublished == 0) — no meaningful data hit to signal
    /// 
    /// This validates the boundary between "request completed" and "intent published":
    /// UserRequestServed fires whenever !exceptionOccurred, even on full vacuum.
    /// Intent is only published when assembledData is not null (physical data hit occurred).
    /// </summary>
    [Fact]
    public async Task UserPath_PhysicalDataMiss_CountsAsServed_ButDoesNotPublishIntent()
    {
        // ARRANGE - Bounded data source has data only in [1000, 9999]
        var cache = CreateCache();

        // Request completely below physical bounds (full vacuum — no data whatsoever)
        var requestBelowBounds = Intervals.NET.Factories.Range.Closed<int>(0, 999);

        // ACT
        var result = await cache.GetDataAsync(requestBelowBounds, CancellationToken.None);

        // ASSERT - No data returned (full vacuum)
        Assert.Null(result.Range);
        Assert.True(result.Data.IsEmpty);

        // ASSERT - Request was completed without exception → counts as served
        Assert.Equal(1, _cacheDiagnostics.UserRequestServed);

        // ASSERT - No physical data hit → no rebalance intent published
        Assert.Equal(0, _cacheDiagnostics.RebalanceIntentPublished);
    }

    #endregion

    #region Rebalance Path - Boundary Handling

    [Fact]
    public async Task RebalancePath_PhysicalDataMiss_CacheContainsOnlyAvailableData()
    {
        // ARRANGE - Data available in [1000, 9999]
        // Configure cache with large left coefficient to trigger rebalance below bounds
        var cache = CreateCacheWithLeftExpansion();

        // Initial request at [1100, 1200] - rebalance will try to expand left beyond bounds
        var initialRequest = Intervals.NET.Factories.Range.Closed<int>(1100, 1200);

        // ACT
        var result = await cache.GetDataAsync(initialRequest, CancellationToken.None);
        await cache.WaitForIdleAsync(); // Wait for rebalance to complete

        // ASSERT - User got requested data
        Assert.NotNull(result.Range);
        Assert.Equal(initialRequest, result.Range);
        Assert.Equal(101, result.Data.Length);

        // After rebalance, cache should only contain data from [1000, ...] (not below)
        // Subsequent request below 1000 should still return null
        var belowBoundsRequest = Intervals.NET.Factories.Range.Closed<int>(900, 950);
        var belowResult = await cache.GetDataAsync(belowBoundsRequest, CancellationToken.None);

        Assert.Null(belowResult.Range);
        Assert.True(belowResult.Data.IsEmpty);
    }

    [Fact]
    public async Task RebalancePath_PartialMiss_LowerBoundary_CacheExpandsToLimit()
    {
        // ARRANGE - Configure cache to expand left significantly
        var cache = CreateCacheWithLeftExpansion();

        // Request near lower boundary - rebalance will hit physical limit
        var requestNearBoundary = Intervals.NET.Factories.Range.Closed<int>(1050, 1150);

        // ACT
        var result = await cache.GetDataAsync(requestNearBoundary, CancellationToken.None);
        await cache.WaitForIdleAsync();

        // ASSERT - User got requested data
        Assert.NotNull(result.Range);
        Assert.Equal(requestNearBoundary, result.Range);

        // Cache should have expanded left to physical boundary (1000)
        // Verify by requesting data at the boundary
        var boundaryRequest = Intervals.NET.Factories.Range.Closed<int>(1000, 1010);
        var boundaryResult = await cache.GetDataAsync(boundaryRequest, CancellationToken.None);

        Assert.NotNull(boundaryResult.Range);
        Assert.Equal(boundaryRequest, boundaryResult.Range);
        Assert.Equal(11, boundaryResult.Data.Length);
        Assert.Equal(1000, boundaryResult.Data.Span[0]);
    }

    [Fact]
    public async Task RebalancePath_PartialMiss_UpperBoundary_CacheExpandsToLimit()
    {
        // ARRANGE - Configure cache to expand right significantly
        var cache = CreateCacheWithRightExpansion();

        // Request near upper boundary - rebalance will hit physical limit
        var requestNearBoundary = Intervals.NET.Factories.Range.Closed<int>(9850, 9950);

        // ACT
        var result = await cache.GetDataAsync(requestNearBoundary, CancellationToken.None);
        await cache.WaitForIdleAsync();

        // ASSERT - User got requested data
        Assert.NotNull(result.Range);
        Assert.Equal(requestNearBoundary, result.Range);

        // Cache should have expanded right to physical boundary (9999)
        // Verify by requesting data at the boundary
        var boundaryRequest = Intervals.NET.Factories.Range.Closed<int>(9990, 9999);
        var boundaryResult = await cache.GetDataAsync(boundaryRequest, CancellationToken.None);

        Assert.NotNull(boundaryResult.Range);
        Assert.Equal(boundaryRequest, boundaryResult.Range);
        Assert.Equal(10, boundaryResult.Data.Length);
        Assert.Equal(9990, boundaryResult.Data.Span[0]);
        Assert.Equal(9999, boundaryResult.Data.Span[9]);
    }

    [Fact]
    public async Task RebalancePath_FullHit_WithinBounds_CacheExpandsNormally()
    {
        // ARRANGE - Data source has data in [1000, 9999]
        var cache = CreateCache();

        // Request well within bounds - rebalance should succeed fully
        var requestInMiddle = Intervals.NET.Factories.Range.Closed<int>(5000, 5100);

        // ACT
        var result = await cache.GetDataAsync(requestInMiddle, CancellationToken.None);
        await cache.WaitForIdleAsync();

        // ASSERT - User got requested data
        Assert.NotNull(result.Range);
        Assert.Equal(requestInMiddle, result.Range);

        // Rebalance expanded cache in both directions (no physical limits hit)
        // Verify cache contains expanded data on both sides
        var leftExpanded = Intervals.NET.Factories.Range.Closed<int>(4900, 4950);
        var leftResult = await cache.GetDataAsync(leftExpanded, CancellationToken.None);

        Assert.NotNull(leftResult.Range);
        Assert.Equal(leftExpanded, leftResult.Range);

        var rightExpanded = Intervals.NET.Factories.Range.Closed<int>(5150, 5200);
        var rightResult = await cache.GetDataAsync(rightExpanded, CancellationToken.None);

        Assert.NotNull(rightResult.Range);
        Assert.Equal(rightExpanded, rightResult.Range);
    }

    [Fact]
    public async Task RebalancePath_CompleteDataMiss_IncrementsDataSegmentUnavailable()
    {
        // ARRANGE - Configure cache to expand far beyond physical bounds
        var cache = CreateCacheWithLeftExpansion();
        _cacheDiagnostics.Reset();

        // Request at exact lower boundary to create an out-of-bounds missing segment
        var initialRequest = Intervals.NET.Factories.Range.Closed<int>(1000, 1010);

        // ACT
        await cache.GetDataAsync(initialRequest, CancellationToken.None);
        await cache.WaitForIdleAsync();

        // ASSERT - At least one segment should be reported as unavailable
        Assert.True(_cacheDiagnostics.DataSegmentUnavailable >= 1,
            "Expected DataSegmentUnavailable to be recorded when rebalance requests out-of-bounds data.");
    }

    #endregion

    #region Helper Methods

    private WindowCache<int, int, IntegerFixedStepDomain> CreateCache()
    {
        var options = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0.2,
            rightThreshold: 0.2,
            debounceDelay: TimeSpan.FromMilliseconds(10)
        );

        _cache = new WindowCache<int, int, IntegerFixedStepDomain>(
            _dataSource,
            _domain,
            options,
            _cacheDiagnostics
        );

        return _cache;
    }

    private WindowCache<int, int, IntegerFixedStepDomain> CreateCacheWithLeftExpansion()
    {
        var options = new WindowCacheOptions(
            leftCacheSize: 3.0,  // Large left expansion
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0.2,
            rightThreshold: 0.2,
            debounceDelay: TimeSpan.FromMilliseconds(10)
        );

        _cache = new WindowCache<int, int, IntegerFixedStepDomain>(
            _dataSource,
            _domain,
            options,
            _cacheDiagnostics
        );

        return _cache;
    }

    private WindowCache<int, int, IntegerFixedStepDomain> CreateCacheWithRightExpansion()
    {
        var options = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 3.0,  // Large right expansion
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0.2,
            rightThreshold: 0.2,
            debounceDelay: TimeSpan.FromMilliseconds(10)
        );

        _cache = new WindowCache<int, int, IntegerFixedStepDomain>(
            _dataSource,
            _domain,
            options,
            _cacheDiagnostics
        );

        return _cache;
    }

    #endregion
}
