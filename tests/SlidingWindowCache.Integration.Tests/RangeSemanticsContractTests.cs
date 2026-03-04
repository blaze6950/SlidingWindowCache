using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Domain.Extensions.Fixed;
using SlidingWindowCache.Tests.Infrastructure.DataSources;
using SlidingWindowCache.Public;
using SlidingWindowCache.Public.Cache;
using SlidingWindowCache.Public.Configuration;
using SlidingWindowCache.Public.Instrumentation;

namespace SlidingWindowCache.Integration.Tests;

/// <summary>
/// Tests that validate SlidingWindowCache assumptions about range semantics and behavior.
/// These tests focus on observable contract validation rather than internal implementation.
/// 
/// Goal: Verify that range operations behave as expected regarding:
/// - Inclusivity and boundary correctness
/// - Returned data length matching requested range span
/// - Behavior with infinite boundaries
/// - Span consistency after expansions
/// </summary>
public sealed class RangeSemanticsContractTests : IAsyncDisposable
{
    private readonly IntegerFixedStepDomain _domain;
    private readonly SpyDataSource _dataSource;
    private WindowCache<int, int, IntegerFixedStepDomain>? _cache;
    private readonly EventCounterCacheDiagnostics _cacheDiagnostics;

    public RangeSemanticsContractTests()
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
                rightThreshold: 0.2,
                debounceDelay: TimeSpan.FromMilliseconds(50)
            ),
            _cacheDiagnostics
        );
        return _cache;
    }

    #region Finite Range Tests

    [Fact]
    public async Task FiniteRange_ClosedBoundaries_ReturnsCorrectLength()
    {
        // ARRANGE
        var cache = CreateCache();
        var range = Intervals.NET.Factories.Range.Closed<int>(100, 110);

        // ACT
        var result = await cache.GetDataAsync(range, CancellationToken.None);

        // ASSERT - Validate memory length matches range span
        var expectedLength = (int)range.Span(_domain);
        Assert.Equal(expectedLength, result.Data.Length);
        Assert.Equal(11, result.Data.Length); // [100, 110] inclusive = 11 elements

        // ASSERT - Validate IDataSource was called with correct range
        Assert.True(_dataSource.TotalFetchCount > 0, "DataSource should be called for cold start");
        Assert.True(_dataSource.WasRangeCovered(100, 110), "DataSource should cover requested range [100, 110]");
    }

    [Fact]
    public async Task FiniteRange_BoundaryAlignment_ReturnsCorrectValues()
    {
        // ARRANGE
        var cache = CreateCache();
        var range = Intervals.NET.Factories.Range.Closed<int>(50, 55);

        // ACT
        var result = await cache.GetDataAsync(range, CancellationToken.None);

        // ASSERT - Validate boundary values are correct
        var array = result.Data.ToArray();
        Assert.Equal(50, array[0]); // First element matches start
        Assert.Equal(55, array[^1]); // Last element matches end
        Assert.True(array.SequenceEqual([50, 51, 52, 53, 54, 55]));
    }

    [Fact]
    public async Task FiniteRange_MultipleRequests_ConsistentLengths()
    {
        // ARRANGE
        var cache = CreateCache();
        var ranges = new[]
        {
            Intervals.NET.Factories.Range.Closed<int>(10, 20), // 11 elements
            Intervals.NET.Factories.Range.Closed<int>(100, 199), // 100 elements
            Intervals.NET.Factories.Range.Closed<int>(500, 501) // 2 elements
        };

        // ACT & ASSERT
        foreach (var range in ranges)
        {
            var loopResult = await cache.GetDataAsync(range, CancellationToken.None);
            var expectedLength = (int)range.Span(_domain);
            Assert.Equal(expectedLength, loopResult.Data.Length);
        }
    }

    [Fact]
    public async Task FiniteRange_SingleElementRange_ReturnsOneElement()
    {
        // ARRANGE
        var cache = CreateCache();
        var range = Intervals.NET.Factories.Range.Closed<int>(42, 42);

        // ACT
        var result = await cache.GetDataAsync(range, CancellationToken.None);

        // ASSERT
        var array = result.Data.ToArray();
        Assert.Single(array);
        Assert.Equal(42, array[0]);
    }

    [Fact]
    public async Task FiniteRange_DataContentMatchesRange_SequentialValues()
    {
        // ARRANGE
        var cache = CreateCache();
        var range = Intervals.NET.Factories.Range.Closed<int>(1000, 1010);

        // ACT
        var result = await cache.GetDataAsync(range, CancellationToken.None);

        // ASSERT - Verify sequential data from start to end
        var array = result.Data.ToArray();
        for (var i = 0; i < array.Length; i++)
        {
            Assert.Equal(1000 + i, array[i]);
        }
    }

    #endregion

    #region Infinite Boundary Tests

    [Fact]
    public async Task InfiniteBoundary_LeftInfinite_CacheHandlesGracefully()
    {
        // ARRANGE
        var cache = CreateCache();

        // Note: IntegerFixedStepDomain uses int.MinValue for negative infinity
        // We test behavior with very large ranges but finite boundaries
        var range = Intervals.NET.Factories.Range.Closed<int>(int.MinValue + 1000, int.MinValue + 1100);

        // ACT
        var result = await cache.GetDataAsync(range, CancellationToken.None);

        // ASSERT - No exceptions, correct length
        var expectedLength = (int)range.Span(_domain);
        Assert.Equal(expectedLength, result.Data.Length);
    }

    [Fact]
    public async Task InfiniteBoundary_RightInfinite_CacheHandlesGracefully()
    {
        // ARRANGE
        var cache = CreateCache();

        // Note: IntegerFixedStepDomain uses int.MaxValue for positive infinity
        var range = Intervals.NET.Factories.Range.Closed<int>(int.MaxValue - 1100, int.MaxValue - 1000);

        // ACT
        var result = await cache.GetDataAsync(range, CancellationToken.None);

        // ASSERT - No exceptions, correct length
        var expectedLength = (int)range.Span(_domain);
        Assert.Equal(expectedLength, result.Data.Length);
    }

    #endregion

    #region Span Consistency After Expansions

    [Fact]
    public async Task SpanConsistency_AfterCacheExpansion_LengthStillCorrect()
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

        // ACT - First request establishes cache with expansion
        var range1 = Intervals.NET.Factories.Range.Closed<int>(100, 110);
        var data1 = await cache.GetDataAsync(range1, CancellationToken.None);

        // Wait for background rebalance to complete
        await cache.WaitForIdleAsync();

        // Second request should hit expanded cache
        var range2 = Intervals.NET.Factories.Range.Closed<int>(105, 115);
        var data2 = await cache.GetDataAsync(range2, CancellationToken.None);

        // ASSERT - Both requests return correct lengths despite cache expansion
        Assert.Equal((int)range1.Span(_domain), data1.Data.Length);
        Assert.Equal((int)range2.Span(_domain), data2.Data.Length);
    }

    [Fact]
    public async Task SpanConsistency_OverlappingRanges_EachReturnsCorrectLength()
    {
        // ARRANGE
        var cache = CreateCache();
        var ranges = new[]
        {
            Intervals.NET.Factories.Range.Closed<int>(100, 120),
            Intervals.NET.Factories.Range.Closed<int>(110, 130),
            Intervals.NET.Factories.Range.Closed<int>(115, 125)
        };

        // ACT & ASSERT - Each overlapping range returns exact length
        foreach (var range in ranges)
        {
            var loopResult = await cache.GetDataAsync(range, CancellationToken.None);
            Assert.Equal((int)range.Span(_domain), loopResult.Data.Length);
        }
    }

    #endregion

    #region Exception Handling

    [Fact]
    public async Task ExceptionHandling_CacheDoesNotThrow_UnlessDataSourceThrows()
    {
        // ARRANGE
        var cache = CreateCache();
        var validRanges = new[]
        {
            Intervals.NET.Factories.Range.Closed<int>(0, 10),
            Intervals.NET.Factories.Range.Closed<int>(1000, 2000),
            Intervals.NET.Factories.Range.Closed<int>(50, 51)
        };

        // ACT & ASSERT - No exceptions for valid ranges
        foreach (var range in validRanges)
        {
            var exception = await Record.ExceptionAsync(async () =>
                await cache.GetDataAsync(range, CancellationToken.None));

            Assert.Null(exception);
        }
    }

    #endregion

    #region Boundary Edge Cases

    [Fact]
    public async Task BoundaryEdgeCase_ZeroCrossingRange_HandlesCorrectly()
    {
        // ARRANGE
        var cache = CreateCache();
        var range = Intervals.NET.Factories.Range.Closed<int>(-10, 10);

        // ACT
        var result = await cache.GetDataAsync(range, CancellationToken.None);

        // ASSERT
        var array = result.Data.ToArray();
        Assert.Equal(21, array.Length); // -10 to 10 inclusive
        Assert.Equal(-10, array[0]);
        Assert.Equal(0, array[10]);
        Assert.Equal(10, array[20]);
    }

    [Fact]
    public async Task BoundaryEdgeCase_NegativeRange_ReturnsCorrectData()
    {
        // ARRANGE
        var cache = CreateCache();
        var range = Intervals.NET.Factories.Range.Closed<int>(-100, -90);

        // ACT
        var result = await cache.GetDataAsync(range, CancellationToken.None);

        // ASSERT
        var array = result.Data.ToArray();
        Assert.Equal(11, array.Length);
        Assert.Equal(-100, array[0]);
        Assert.Equal(-90, array[^1]);

        // ASSERT - IDataSource handled negative range correctly
        Assert.True(_dataSource.WasRangeCovered(-100, -90),
            "DataSource should cover negative range [-100, -90]");
        Assert.True(_dataSource.TotalFetchCount > 0, "DataSource should be called");
    }

    #endregion
}