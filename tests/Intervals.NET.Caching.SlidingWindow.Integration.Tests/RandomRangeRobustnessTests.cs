using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Domain.Extensions.Fixed;
using Intervals.NET.Caching.SlidingWindow.Tests.Infrastructure.DataSources;
using Intervals.NET.Caching.SlidingWindow.Public.Cache;
using Intervals.NET.Caching.SlidingWindow.Public.Configuration;
using Intervals.NET.Caching.SlidingWindow.Public.Instrumentation;

namespace Intervals.NET.Caching.SlidingWindow.Integration.Tests;

/// <summary>
/// Property-based robustness tests using randomized range requests.
/// Detects edge cases and invariant violations through many iterations.
/// Uses deterministic seed for reproducibility.
/// </summary>
public sealed class RandomRangeRobustnessTests : IAsyncDisposable
{
    private readonly IntegerFixedStepDomain _domain;
    private readonly SpyDataSource _dataSource;
    private readonly Random _random;
    private SlidingWindowCache<int, int, IntegerFixedStepDomain>? _cache;
    private readonly EventCounterCacheDiagnostics _cacheDiagnostics;

    private const int RandomSeed = 42;
    private const int MinRangeStart = -10000;
    private const int MaxRangeStart = 10000;
    private const int MinRangeLength = 1;
    private const int MaxRangeLength = 100;

    public RandomRangeRobustnessTests()
    {
        _domain = new IntegerFixedStepDomain();
        _dataSource = new SpyDataSource();
        _random = new Random(RandomSeed);
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

    private SlidingWindowCache<int, int, IntegerFixedStepDomain> CreateCache(SlidingWindowCacheOptions? options = null) =>
        _cache = new SlidingWindowCache<int, int, IntegerFixedStepDomain>(
            _dataSource,
            _domain,
            options ?? new SlidingWindowCacheOptions(
                leftCacheSize: 1.0,
                rightCacheSize: 1.0,
                readMode: UserCacheReadMode.Snapshot,
                leftThreshold: 0.2,
                rightThreshold: 0.2,
                debounceDelay: TimeSpan.FromMilliseconds(10)
            ),
            _cacheDiagnostics
        );

    private Range<int> GenerateRandomRange()
    {
        var start = _random.Next(MinRangeStart, MaxRangeStart);
        var length = _random.Next(MinRangeLength, MaxRangeLength);
        var end = start + length - 1;
        return Factories.Range.Closed<int>(start, end);
    }

    [Fact]
    public async Task RandomRanges_200Iterations_NoExceptions()
    {
        var cache = CreateCache();
        const int iterations = 200;

        for (var i = 0; i < iterations; i++)
        {
            var range = GenerateRandomRange();
            var result = await cache.GetDataAsync(range, CancellationToken.None);
            Assert.Equal((int)range.Span(_domain), result.Data.Length);
        }

        // ASSERT - Verify IDataSource was called and no malformed ranges requested
        Assert.True(_dataSource.TotalFetchCount > 0, "IDataSource should be called during random iterations");

        // Verify all requested ranges are valid
        var allRanges = _dataSource.GetAllRequestedRanges();
        Assert.All(allRanges, range =>
        {
            var start = (int)range.Start;
            var end = (int)range.End;
            Assert.True(start <= end, $"Invalid range: start ({start}) > end ({end})");
        });
    }

    [Fact]
    public async Task RandomRanges_DataContentAlwaysValid()
    {
        var cache = CreateCache();
        const int iterations = 150;

        for (var i = 0; i < iterations; i++)
        {
            var range = GenerateRandomRange();
            var result = await cache.GetDataAsync(range, CancellationToken.None);

            var start = (int)range.Start;
            var array = result.Data.ToArray(); // Convert to array to avoid ref struct in async

            for (var j = 0; j < array.Length; j++)
            {
                Assert.Equal(start + j, array[j]);
            }
        }
    }

    [Fact]
    public async Task RandomOverlappingRanges_NoExceptions()
    {
        var cache = CreateCache();
        const int iterations = 100;

        var baseStart = _random.Next(1000, 2000);
        var baseRange = Factories.Range.Closed<int>(baseStart, baseStart + 50);
        await cache.GetDataAsync(baseRange, CancellationToken.None);

        for (var i = 0; i < iterations; i++)
        {
            var overlapStart = baseStart + _random.Next(-25, 25);
            var overlapEnd = overlapStart + _random.Next(10, 40);
            var range = Factories.Range.Closed<int>(overlapStart, overlapEnd);

            var result = await cache.GetDataAsync(range, CancellationToken.None);
            Assert.Equal((int)range.Span(_domain), result.Data.Length);
        }
    }

    [Fact]
    public async Task RandomAccessSequence_ForwardBackward_StableOperation()
    {
        var cache = CreateCache();
        const int iterations = 150;
        var currentPosition = 5000;

        for (var i = 0; i < iterations; i++)
        {
            var direction = _random.Next(0, 2) == 0 ? -1 : 1;
            var step = _random.Next(5, 20);
            currentPosition += direction * step;

            var rangeLength = _random.Next(10, 30);
            var range = Factories.Range.Closed<int>(
                currentPosition,
                currentPosition + rangeLength - 1
            );

            var result = await cache.GetDataAsync(range, CancellationToken.None);
            var array = result.Data.ToArray();
            Assert.Equal(rangeLength, array.Length);
            Assert.Equal(currentPosition, array[0]);
        }
    }

    [Fact]
    public async Task StressCombination_MixedPatterns_500Iterations()
    {
        var cache = CreateCache(new SlidingWindowCacheOptions(
            leftCacheSize: 2.0,
            rightCacheSize: 2.0,
            readMode: UserCacheReadMode.CopyOnRead,
            leftThreshold: 0.25,
            rightThreshold: 0.25,
            debounceDelay: TimeSpan.FromMilliseconds(5)
        ));

        const int iterations = 500;

        for (var i = 0; i < iterations; i++)
        {
            Range<int> range;
            var pattern = _random.Next(0, 10);

            if (pattern < 5)
            {
                range = GenerateRandomRange();
            }
            else if (pattern < 8)
            {
                var start = i * 10;
                range = Factories.Range.Closed<int>(start, start + 20);
            }
            else
            {
                var start = (i - 1) * 10 + 5;
                range = Factories.Range.Closed<int>(start, start + 25);
            }

            var result = await cache.GetDataAsync(range, CancellationToken.None);
            Assert.Equal((int)range.Span(_domain), result.Data.Length);
        }

        // ASSERT - Comprehensive validation of IDataSource interactions
        var totalFetches = _dataSource.TotalFetchCount;
        Assert.True(totalFetches > 0, "IDataSource should be called during stress test");
        Assert.True(totalFetches < iterations * 3,
            $"Fetch count ({totalFetches}) should be reasonable for {iterations} mixed-pattern iterations");

        // Verify all ranges requested are valid
        var allRanges = _dataSource.GetAllRequestedRanges();
        Assert.NotEmpty(allRanges);
        Assert.All(allRanges, r =>
        {
            var start = (int)r.Start;
            var end = (int)r.End;
            Assert.True(start <= end, $"Invalid range detected: [{start}, {end}]");
        });

        // Verify no excessive redundant fetches
        var uniqueRanges = _dataSource.GetUniqueRequestedRanges();
        Assert.True(uniqueRanges.Count > 0, "Should have requested some unique ranges");
    }
}