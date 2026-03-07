using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Caching.SlidingWindow.Tests.Infrastructure.DataSources;
using Intervals.NET.Caching.SlidingWindow.Public.Cache;
using Intervals.NET.Caching.SlidingWindow.Public.Configuration;
using Intervals.NET.Caching.SlidingWindow.Public.Instrumentation;

namespace Intervals.NET.Caching.SlidingWindow.Integration.Tests;

/// <summary>
/// Tests that validate the EXACT ranges propagated to IDataSource in different cache scenarios.
/// These tests provide precise behavioral contracts ("alibi") proving the cache requests
/// correct ranges from the data source in every state transition.
///
/// <para><strong>Note:</strong> These are intentional white-box tests. They verify internal
/// range propagation details (e.g. exact segment boundaries sent to IDataSource) to guard
/// against regressions in the User Path and Rebalance Execution logic. This level of
/// specificity is deliberate — it documents and locks in the precise data-fetch contracts
/// that the rest of the architecture depends on.</para>
///
/// Scenarios covered:
/// - User Path: Cache miss (cold start)
/// - User Path: Cache hit (full cache coverage)
/// - User Path: Partial cache hit (left extension, right extension)
/// - Rebalance: After cold start
/// - Rebalance: With right-side expansion
/// - Rebalance: With left-side expansion
/// </summary>
public sealed class DataSourceRangePropagationTests : IAsyncDisposable
{
    private readonly IntegerFixedStepDomain _domain;
    private readonly SpyDataSource _dataSource;
    private SlidingWindowCache<int, int, IntegerFixedStepDomain>? _cache;
    private readonly EventCounterCacheDiagnostics _cacheDiagnostics;

    public DataSourceRangePropagationTests()
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

    private SlidingWindowCache<int, int, IntegerFixedStepDomain> CreateCache(SlidingWindowCacheOptions? options = null)
    {
        _cache = new SlidingWindowCache<int, int, IntegerFixedStepDomain>(
            _dataSource,
            _domain,
            options ?? new SlidingWindowCacheOptions(
                leftCacheSize: 1.0,
                rightCacheSize: 1.0,
                readMode: UserCacheReadMode.Snapshot,
                leftThreshold: 0.2,
                rightThreshold: 0.2,
                debounceDelay: TimeSpan.FromSeconds(1)
            ),
            _cacheDiagnostics
        );
        return _cache;
    }

    #region Cache Miss (Cold Start)

    [Fact]
    public async Task CacheMiss_ColdStart_PropagatesExactUserRange()
    {
        // ARRANGE
        var cache = CreateCache();
        var userRange = Factories.Range.Closed<int>(100, 110);

        // ACT
        var result = await cache.GetDataAsync(userRange, CancellationToken.None);

        // ASSERT - Data is correct
        Assert.Equal(11, result.Data.Length);
        Assert.Equal(100, result.Data.Span[0]);
        Assert.Equal(110, result.Data.Span[^1]);

        // ASSERT - IDataSource received exact user range on cold start
        var requestedRanges = _dataSource.GetAllRequestedRanges();
        Assert.Single(requestedRanges);

        var fetchedRange = requestedRanges.First();
        Assert.Equal(userRange, fetchedRange); // Exact match for cold start
    }

    [Fact]
    public async Task CacheMiss_ColdStart_LargeRange_PropagatesExactly()
    {
        // ARRANGE
        var cache = CreateCache();
        var userRange = Factories.Range.Closed<int>(0, 999);

        // ACT
        var result = await cache.GetDataAsync(userRange, CancellationToken.None);

        // ASSERT
        Assert.Equal(1000, result.Data.Length);

        // ASSERT - IDataSource received exact large range
        var requestedRanges = _dataSource.GetAllRequestedRanges();
        var fetchedRange = requestedRanges.SingleOrDefault();

        Assert.NotNull(requestedRanges);
        Assert.Equal(userRange, fetchedRange); // Exact match for large range
    }

    #endregion

    #region Cache Hit (Full Coverage)

    [Fact]
    public async Task CacheHit_FullCoverage_NoAdditionalFetch()
    {
        // ARRANGE - Cache with large expansion to ensure second request is fully covered
        var cache = CreateCache(new SlidingWindowCacheOptions(
            leftCacheSize: 3.0,
            rightCacheSize: 3.0,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0.3,
            rightThreshold: 0.3
        ));

        // First request: [100, 120] will expand to approximately [37, 183] with 3x coefficient
        await cache.GetDataAsync(Factories.Range.Closed<int>(100, 120), CancellationToken.None);
        await cache.WaitForIdleAsync();

        _dataSource.Reset();

        // ACT - Request subset that should be fully cached: [110, 115]
        var subsetRange = Factories.Range.Closed<int>(110, 115);
        var result = await cache.GetDataAsync(subsetRange, CancellationToken.None);

        // ASSERT - Data is correct
        Assert.Equal(6, result.Data.Length);
        Assert.Equal(110, result.Data.Span[0]);

        // ASSERT - No additional fetch should occur (cache hit)
        var newFetches = _dataSource.GetAllRequestedRanges();
        Assert.Empty(newFetches); // Perfect cache hit!
    }

    #endregion

    #region Partial Cache Hit - Right Extension

    [Fact]
    public async Task PartialCacheHit_RightExtension_FetchesOnlyMissingSegment()
    {
        // ARRANGE
        var cache = CreateCache(new SlidingWindowCacheOptions(
            leftCacheSize: 1,
            rightCacheSize: 1,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0,
            rightThreshold: 0
        ));

        // First request establishes cache [200, 210] - 11 items, cache after rebalance [189, 221]
        await cache.GetDataAsync(Factories.Range.Closed<int>(200, 210), CancellationToken.None);
        await cache.WaitForIdleAsync();

        _dataSource.Reset();

        // ACT - Extend to right [220, 230] - overlaps existing [189, 221]
        var rightExtension = Factories.Range.Closed<int>(220, 230);
        var result = await cache.GetDataAsync(rightExtension, CancellationToken.None);

        // ASSERT - Data is correct
        Assert.Equal(11, result.Data.Length);
        Assert.Equal(220, result.Data.Span[0]);
        Assert.Equal(230, result.Data.Span[^1]);

        // ASSERT - IDataSource should fetch only missing right segment (221, 230]
        _dataSource.AssertRangeRequested(Factories.Range.OpenClosed<int>(221, 230));
    }

    #endregion

    #region Partial Cache Hit - Left Extension

    [Fact]
    public async Task PartialCacheHit_LeftExtension_FetchesOnlyMissingSegment()
    {
        // ARRANGE - Cache WITHOUT expansion
        var cache = CreateCache(new SlidingWindowCacheOptions(
            leftCacheSize: 1,
            rightCacheSize: 1,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0,
            rightThreshold: 0
        ));

        // First request establishes cache [300, 310] - 11 items, cache after rebalance [289, 321]
        await cache.GetDataAsync(Factories.Range.Closed<int>(300, 310), CancellationToken.None);
        await cache.WaitForIdleAsync();

        _dataSource.Reset();

        // ACT - Extend to left [280, 290] - overlaps existing [289, 321]
        var leftExtension = Factories.Range.Closed<int>(280, 290);
        var result = await cache.GetDataAsync(leftExtension, CancellationToken.None);

        // ASSERT - Data is correct
        Assert.Equal(11, result.Data.Length);
        Assert.Equal(280, result.Data.Span[0]);
        Assert.Equal(290, result.Data.Span[^1]);

        // ASSERT - IDataSource should fetch only missing left segment [280, 289)
        _dataSource.AssertRangeRequested(Factories.Range.ClosedOpen(280, 289));
    }

    #endregion

    #region Rebalance After Cold Start

    [Fact]
    public async Task Rebalance_ColdStart_ExpandsSymmetrically()
    {
        // ARRANGE
        var cache = CreateCache(new SlidingWindowCacheOptions(
            leftCacheSize: 1,
            rightCacheSize: 1,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0,
            rightThreshold: 0
        ));

        // ACT - Request [100, 110] - 11 items, cache after rebalance [89, 121]
        await cache.GetDataAsync(Factories.Range.Closed<int>(100, 110), CancellationToken.None);
        await cache.WaitForIdleAsync();

        // ASSERT - Should fetch initial user range and rebalance expansions
        var allRanges = _dataSource.GetAllRequestedRanges();
        Assert.Equal(3, allRanges.Count); // Initial fetch + 2 expansions

        // First fetch should be the user range
        _dataSource.AssertRangeRequested(Factories.Range.Closed<int>(100, 110));

        // Rebalance should expand symmetrically
        // Left expansion: 11 * 1 = 11, so [89, 100)
        _dataSource.AssertRangeRequested(Factories.Range.ClosedOpen(89, 100));

        // Right expansion: 11 * 1.0 = 11, so (110, 121]
        _dataSource.AssertRangeRequested(Factories.Range.OpenClosed<int>(110, 121));
    }

    #endregion

    #region Rebalance with Right-Side Expansion

    [Fact]
    public async Task Rebalance_RightMovement_ExpandsRightSide()
    {
        // ARRANGE
        var cache = CreateCache(new SlidingWindowCacheOptions(
            leftCacheSize: 1,
            rightCacheSize: 1,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0,
            rightThreshold: 0
        ));

        // Establish initial cache at [100, 110] - 11 items, cache after rebalance [89, 121]
        await cache.GetDataAsync(Factories.Range.Closed<int>(100, 110), CancellationToken.None);
        await cache.WaitForIdleAsync();

        _dataSource.Reset();

        // ACT - Move right to [120, 130] - 11 items, overlaps existing [89, 121]
        var rightRange = Factories.Range.Closed<int>(120, 130);
        await cache.GetDataAsync(rightRange, CancellationToken.None);
        await cache.WaitForIdleAsync();

        // ASSERT
        // First fetch should be the missing segment
        _dataSource.AssertRangeRequested(Factories.Range.OpenClosed<int>(121, 130));

        // Rebalance may trigger right expansion
        // Expected right expansion: 11 * 1 = 11, so (130, 141]
        _dataSource.AssertRangeRequested(Factories.Range.OpenClosed<int>(130, 141));
    }

    #endregion

    #region Rebalance with Left-Side Expansion

    [Fact]
    public async Task Rebalance_LeftMovement_ExpandsLeftSide()
    {
        // ARRANGE
        var cache = CreateCache(new SlidingWindowCacheOptions(
            leftCacheSize: 1,
            rightCacheSize: 1,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0,
            rightThreshold: 0
        ));

        // Establish initial cache at [200, 210] - 11 items, cache after rebalance [189, 221]
        await cache.GetDataAsync(Factories.Range.Closed<int>(200, 210), CancellationToken.None);
        await cache.WaitForIdleAsync();

        _dataSource.Reset();

        // ACT - Move left to [180, 190] - 11 items, overlaps existing [189, 221]
        var leftRange = Factories.Range.Closed<int>(180, 190);
        await cache.GetDataAsync(leftRange, CancellationToken.None);
        await cache.WaitForIdleAsync();

        // ASSERT - Should fetch the new range
        var requestedRanges = _dataSource.GetAllRequestedRanges();
        Assert.NotEmpty(requestedRanges);

        // First fetch should be the missing segment
        _dataSource.AssertRangeRequested(Factories.Range.ClosedOpen(180, 189));

        // Rebalance may trigger left expansion
        // Expected left expansion: 11 * 1 = 11, so [169, 180)
        _dataSource.AssertRangeRequested(Factories.Range.ClosedOpen(169, 180));
    }

    #endregion

    #region Partial Overlap Scenarios

    [Fact]
    public async Task PartialOverlap_BothSides_FetchesBothMissingSegments()
    {
        // ARRANGE - No expansion for predictable behavior
        var cache = CreateCache(new SlidingWindowCacheOptions(
            leftCacheSize: 1,
            rightCacheSize: 1,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0,
            rightThreshold: 0
        ));

        // Establish cache [100, 110] - 11 items, cache after rebalance [89, 121]
        await cache.GetDataAsync(Factories.Range.Closed<int>(100, 110), CancellationToken.None);
        await cache.WaitForIdleAsync();

        _dataSource.Reset();

        // ACT - Request [80, 130] which extends both left and right
        var extendedRange = Factories.Range.Closed<int>(80, 130);
        var result = await cache.GetDataAsync(extendedRange, CancellationToken.None);

        // ASSERT - Data is correct
        Assert.Equal(51, result.Data.Length);
        Assert.Equal(80, result.Data.Span[0]);
        Assert.Equal(130, result.Data.Span[^1]);

        // ASSERT - Should fetch both missing segments
        // Left segment [80, 89) and right segment (121, 130]
        // May be fetched as 2 separate ranges or 1 consolidated range
        var requestedRanges = _dataSource.GetAllRequestedRanges();
        Assert.Equal(2, requestedRanges.Count); // Expecting 2 separate fetches for left and right missing segments
        _dataSource.AssertRangeRequested(Factories.Range.ClosedOpen(80, 89));
        _dataSource.AssertRangeRequested(Factories.Range.OpenClosed<int>(121, 130));
    }

    #endregion

    #region Non-Overlapping Jump

    [Fact]
    public async Task NonOverlappingJump_FetchesEntireNewRange()
    {
        // ARRANGE
        var cache = CreateCache();

        // Establish cache at [100, 110]
        await cache.GetDataAsync(Factories.Range.Closed<int>(100, 110), CancellationToken.None);
        await cache.WaitForIdleAsync();

        _dataSource.Reset();

        // ACT - Jump to non-overlapping [500, 510]
        var jumpRange = Factories.Range.Closed<int>(500, 510);
        var result = await cache.GetDataAsync(jumpRange, CancellationToken.None);

        // ASSERT - Data is correct
        Assert.Equal(11, result.Data.Length);
        Assert.Equal(500, result.Data.Span[0]);
        Assert.Equal(510, result.Data.Span[^1]);

        // ASSERT - Should fetch entire new range
        _dataSource.AssertRangeRequested(Factories.Range.Closed<int>(500, 510));
    }

    #endregion

    #region Edge Case: Adjacent Ranges

    [Fact]
    public async Task AdjacentRanges_RightAdjacent_FetchesExactNewSegment()
    {
        // ARRANGE - No expansion
        var cache = CreateCache(new SlidingWindowCacheOptions(
            leftCacheSize: 0.0,
            rightCacheSize: 0.0,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0.0,
            rightThreshold: 0.0,
            debounceDelay: TimeSpan.FromMilliseconds(50)
        ));

        // Establish cache [100, 110]
        await cache.GetDataAsync(Factories.Range.Closed<int>(100, 110), CancellationToken.None);
        await cache.WaitForIdleAsync();

        _dataSource.Reset();

        // ACT - Request adjacent right range [111, 120]
        var adjacentRange = Factories.Range.Closed<int>(111, 120);
        var result = await cache.GetDataAsync(adjacentRange, CancellationToken.None);

        // ASSERT - Data is correct
        Assert.Equal(10, result.Data.Length);
        Assert.Equal(111, result.Data.Span[0]);
        Assert.Equal(120, result.Data.Span[^1]);

        // ASSERT - Should fetch only the new adjacent segment
        var requestedRanges = _dataSource.GetAllRequestedRanges();
        Assert.Single(requestedRanges);

        var fetchedRange = requestedRanges.First();
        Assert.Equal(111, (int)fetchedRange.Start);
        Assert.Equal(120, (int)fetchedRange.End);
    }

    [Fact]
    public async Task AdjacentRanges_LeftAdjacent_FetchesExactNewSegment()
    {
        // ARRANGE - No expansion
        var cache = CreateCache(new SlidingWindowCacheOptions(
            leftCacheSize: 0.0,
            rightCacheSize: 0.0,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0.0,
            rightThreshold: 0.0,
            debounceDelay: TimeSpan.FromMilliseconds(50)
        ));

        // Establish cache [100, 110]
        await cache.GetDataAsync(Factories.Range.Closed<int>(100, 110), CancellationToken.None);
        await cache.WaitForIdleAsync();

        _dataSource.Reset();

        // ACT - Request adjacent left range [90, 99]
        var adjacentRange = Factories.Range.Closed<int>(90, 99);
        var result = await cache.GetDataAsync(adjacentRange, CancellationToken.None);

        // ASSERT - Data is correct
        Assert.Equal(10, result.Data.Length);
        Assert.Equal(90, result.Data.Span[0]);
        Assert.Equal(99, result.Data.Span[^1]);

        // ASSERT - Should fetch only the new adjacent segment
        var requestedRanges = _dataSource.GetAllRequestedRanges();
        Assert.Single(requestedRanges);

        var fetchedRange = requestedRanges.First();
        Assert.Equal(90, (int)fetchedRange.Start);
        Assert.Equal(99, (int)fetchedRange.End);
    }

    #endregion
}