using Intervals.NET;
using Intervals.NET.Domain.Default.Numeric;
using SlidingWindowCache.Integration.Tests.TestInfrastructure;
using SlidingWindowCache.Infrastructure.Instrumentation;
using SlidingWindowCache.Public;
using SlidingWindowCache.Public.Configuration;

namespace SlidingWindowCache.Integration.Tests;

/// <summary>
/// Concurrency and stress stability tests for WindowCache.
/// Validates system stability under concurrent load and high volume requests.
/// 
/// Goal: Verify robustness under concurrent scenarios:
/// - No crashes or exceptions
/// - No deadlocks
/// - Valid data returned for all requests
/// - Avoids fragile timing assertions
/// </summary>
public sealed class ConcurrencyStabilityTests : IAsyncDisposable
{
    private readonly IntegerFixedStepDomain _domain;
    private readonly SpyDataSource _dataSource;
    private WindowCache<int, int, IntegerFixedStepDomain>? _cache;
    private readonly EventCounterCacheDiagnostics _cacheDiagnostics;

    public ConcurrencyStabilityTests()
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
        return _cache = new WindowCache<int, int, IntegerFixedStepDomain>(
            _dataSource,
            _domain,
            options ?? new WindowCacheOptions(
                leftCacheSize: 1.0,
                rightCacheSize: 1.0,
                readMode: UserCacheReadMode.Snapshot,
                leftThreshold: 0.2,
                rightThreshold: 0.2,
                debounceDelay: TimeSpan.FromMilliseconds(20)
            ),
            _cacheDiagnostics
        );
    }

    #region Basic Concurrency Tests

    [Fact]
    public async Task Concurrent_10SimultaneousRequests_AllSucceed()
    {
        // ARRANGE
        var cache = CreateCache();
        const int concurrentRequests = 10;

        // ACT - Execute requests concurrently
        var tasks = new List<Task<ReadOnlyMemory<int>>>();
        for (var i = 0; i < concurrentRequests; i++)
        {
            var start = i * 100;
            var range = Intervals.NET.Factories.Range.Closed<int>(start, start + 20);
            tasks.Add(cache.GetDataAsync(range, CancellationToken.None).AsTask());
        }

        var results = await Task.WhenAll(tasks);

        // ASSERT - All requests completed successfully
        Assert.Equal(concurrentRequests, results.Length);

        foreach (var data in results)
        {
            Assert.Equal(21, data.Length); // Each range has 21 elements
        }

        // ASSERT - IDataSource was called and handled concurrent requests
        Assert.True(_dataSource.TotalFetchCount > 0, "IDataSource should handle concurrent requests");

        // Verify all requested ranges are valid
        var allRanges = _dataSource.GetAllRequestedRanges();
        Assert.All(allRanges,
            range => { Assert.True((int)range.Start <= (int)range.End, "All concurrent ranges should be valid"); });
    }

    [Fact]
    public async Task Concurrent_SameRangeMultipleTimes_NoDeadlock()
    {
        // ARRANGE
        var cache = CreateCache();
        const int concurrentRequests = 20;
        var range = Intervals.NET.Factories.Range.Closed<int>(100, 120);

        // ACT - Many concurrent requests for same range
        var tasks = Enumerable.Range(0, concurrentRequests)
            .Select(_ => cache.GetDataAsync(range, CancellationToken.None).AsTask())
            .ToList();

        var results = await Task.WhenAll(tasks);

        // ASSERT - All completed, no deadlock
        Assert.Equal(concurrentRequests, results.Length);

        foreach (var result in results)
        {
            var array = result.ToArray();
            Assert.Equal(21, array.Length);
            Assert.Equal(100, array[0]);
            Assert.Equal(120, array[^1]);
        }
    }

    #endregion

    #region Overlapping Range Concurrency

    [Fact]
    public async Task Concurrent_OverlappingRanges_AllDataValid()
    {
        // ARRANGE
        var cache = CreateCache();
        const int concurrentRequests = 15;

        // ACT - Overlapping ranges around center point
        var tasks = new List<Task<ReadOnlyMemory<int>>>();
        for (var i = 0; i < concurrentRequests; i++)
        {
            var offset = i * 5;
            var range = Intervals.NET.Factories.Range.Closed<int>(100 + offset, 150 + offset);
            tasks.Add(cache.GetDataAsync(range, CancellationToken.None).AsTask());
        }

        var results = await Task.WhenAll(tasks);

        // ASSERT - Verify each result
        const int expected = 51; // [100+offset, 150+offset] = 51 elements
        for (var i = 0; i < results.Length; i++)
        {
            var offset = i * 5;
            var data = results[i];
            Assert.Equal(expected, data.Length);
            Assert.Equal(100 + offset, data.Span[0]);
        }
    }

    #endregion

    #region High Volume Stress Tests

    [Fact]
    public async Task HighVolume_100SequentialRequests_NoErrors()
    {
        // ARRANGE
        var cache = CreateCache();
        const int requestCount = 100;
        var exceptions = new List<Exception>();

        // ACT
        for (var i = 0; i < requestCount; i++)
        {
            try
            {
                var start = i * 10;
                var range = Intervals.NET.Factories.Range.Closed<int>(start, start + 15);
                var result = await cache.GetDataAsync(range, CancellationToken.None);

                Assert.Equal(16, result.Length);
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }

        // ASSERT
        Assert.Empty(exceptions);
    }

    [Fact]
    public async Task HighVolume_50ConcurrentBursts_SystemStable()
    {
        // ARRANGE
        var cache = CreateCache(new WindowCacheOptions(
            leftCacheSize: 1.5,
            rightCacheSize: 1.5,
            readMode: UserCacheReadMode.CopyOnRead,
            leftThreshold: 0.25,
            rightThreshold: 0.25,
            debounceDelay: TimeSpan.FromMilliseconds(10)
        ));

        const int burstSize = 50;

        // ACT - Launch many concurrent requests
        var tasks = new List<Task<ReadOnlyMemory<int>>>();
        for (var i = 0; i < burstSize; i++)
        {
            var start = (i % 10) * 50; // Create some overlap
            var range = Intervals.NET.Factories.Range.Closed<int>(start, start + 25);
            tasks.Add(cache.GetDataAsync(range, CancellationToken.None).AsTask());
        }

        var results = await Task.WhenAll(tasks);

        // ASSERT - All completed successfully
        Assert.Equal(burstSize, results.Length);
        Assert.All(results, r => Assert.Equal(26, r.Length));
    }

    #endregion

    #region Mixed Concurrent Operations

    [Fact]
    public async Task MixedConcurrent_RandomAndSequential_NoConflicts()
    {
        // ARRANGE
        var cache = CreateCache();
        var random = new Random(42);
        const int totalTasks = 40;

        // ACT - Mix of random and sequential requests
        var tasks = new List<Task<ReadOnlyMemory<int>>>();

        for (var i = 0; i < totalTasks; i++)
        {
            Range<int> range;

            if (i % 2 == 0)
            {
                // Sequential
                var start = i * 20;
                range = Intervals.NET.Factories.Range.Closed<int>(start, start + 30);
            }
            else
            {
                // Random
                var start = random.Next(0, 1000);
                range = Intervals.NET.Factories.Range.Closed<int>(start, start + 20);
            }

            tasks.Add(cache.GetDataAsync(range, CancellationToken.None).AsTask());
        }

        var results = await Task.WhenAll(tasks);

        // ASSERT
        Assert.Equal(totalTasks, results.Length);
        Assert.All(results, r => Assert.True(r.Length > 0));
    }

    #endregion

    #region Cancellation Under Load

    [Fact]
    public async Task CancellationUnderLoad_SystemStableWithCancellations()
    {
        // ARRANGE
        var cache = CreateCache();
        const int requestCount = 30;
        var ctsList = new List<CancellationTokenSource>();

        // ACT - Launch requests with delayed cancellations
        var tasks = new List<Task<bool>>();

        for (var i = 0; i < requestCount; i++)
        {
            var cts = new CancellationTokenSource();
            ctsList.Add(cts);

            var start = i * 10;
            var range = Intervals.NET.Factories.Range.Closed<int>(start, start + 15);

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await cache.GetDataAsync(range, cts.Token);
                    return true; // Success
                }
                catch (OperationCanceledException)
                {
                    return false; // Cancelled
                }
            }, CancellationToken.None));

            // Cancel some requests with delay
            if (i % 5 == 0)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(5, CancellationToken.None);
                    await cts.CancelAsync();
                }, CancellationToken.None);
            }
        }

        var results = await Task.WhenAll(tasks);

        // ASSERT - System handled mix gracefully (some succeeded, some may be cancelled)
        var successCount = results.Count(r => r);
        Assert.True(successCount > 0, "At least some requests should succeed");

        // Cleanup
        foreach (var cts in ctsList)
        {
            cts.Dispose();
        }
    }

    #endregion

    #region Rapid Fire Tests

    [Fact]
    public async Task RapidFire_100RequestsMinimalDelay_NoDeadlock()
    {
        // ARRANGE
        var cache = CreateCache(new WindowCacheOptions(
            leftCacheSize: 2.0,
            rightCacheSize: 2.0,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0.3,
            rightThreshold: 0.3,
            debounceDelay: TimeSpan.FromMilliseconds(5)
        ));

        const int requestCount = 100;

        // ACT - Rapid sequential requests
        for (var i = 0; i < requestCount; i++)
        {
            var start = (i % 20) * 10; // Create overlap pattern
            var range = Intervals.NET.Factories.Range.Closed<int>(start, start + 20);
            var result = await cache.GetDataAsync(range, CancellationToken.None);

            Assert.Equal(21, result.Length);
        }

        // ASSERT - Completed without deadlock
        Assert.True(true);
    }

    #endregion

    #region Data Integrity Under Concurrency

    [Fact]
    public async Task DataIntegrity_ConcurrentReads_AllDataCorrect()
    {
        // ARRANGE
        var cache = CreateCache();
        const int concurrentReaders = 25;
        var baseRange = Intervals.NET.Factories.Range.Closed<int>(500, 600);

        // Warm up cache
        await cache.GetDataAsync(baseRange, CancellationToken.None);
        await cache.WaitForIdleAsync();

        var initialFetchCount = _dataSource.TotalFetchCount;

        // ACT - Many concurrent reads of overlapping ranges
        var tasks = new List<Task<(int length, int firstValue, int expectedFirst)>>();

        for (var i = 0; i < concurrentReaders; i++)
        {
            var offset = i * 4;
            var expectedFirst = 500 + offset;
            tasks.Add(Task.Run(async () =>
            {
                var range = Intervals.NET.Factories.Range.Closed<int>(500 + offset, 550 + offset);
                var data = await cache.GetDataAsync(range, CancellationToken.None);
                return (data.Length, data.Span[0], expectedFirst);
            }));
        }

        var results = await Task.WhenAll(tasks);

        // ASSERT - No data corruption
        foreach (var (length, firstValue, expectedFirst) in results)
        {
            Assert.Equal(51, length);
            Assert.Equal(expectedFirst, firstValue);
        }

        // ASSERT - Concurrent reads should mostly hit cache after warmup
        var finalFetchCount = _dataSource.TotalFetchCount;
        Assert.True(finalFetchCount >= initialFetchCount, "May have additional fetches for range extensions");

        // Verify no malformed ranges during concurrent access
        var allRanges = _dataSource.GetAllRequestedRanges();
        Assert.All(allRanges,
            range =>
            {
                Assert.True((int)range.Start <= (int)range.End, "No data races should produce invalid ranges");
            });
    }

    #endregion

    #region Timeout Protection

    [Fact]
    public async Task TimeoutProtection_LongRunningTest_CompletesWithinReasonableTime()
    {
        // ARRANGE
        var cache = CreateCache();
        const int requestCount = 50;
        var timeout = TimeSpan.FromSeconds(30);

        // ACT
        using var cts = new CancellationTokenSource(timeout);
        var tasks = new List<Task>();

        for (var i = 0; i < requestCount; i++)
        {
            var start = i * 15;
            var range = Intervals.NET.Factories.Range.Closed<int>(start, start + 25);
            tasks.Add(cache.GetDataAsync(range, cts.Token).AsTask());
        }

        // ASSERT - Completes within timeout
        await Task.WhenAll(tasks);
        Assert.False(cts.Token.IsCancellationRequested, "Should complete before timeout");
    }

    #endregion
}