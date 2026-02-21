using Intervals.NET;
using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Domain.Extensions.Fixed;
using SlidingWindowCache.Public;
using SlidingWindowCache.Public.Configuration;

namespace SlidingWindowCache.Integration.Tests;

/// <summary>
/// Integration tests verifying the execution strategy selection based on WindowCacheOptions.RebalanceQueueCapacity.
/// Tests that both task-based (unbounded) and channel-based (bounded) strategies work correctly.
/// </summary>
public class ExecutionStrategySelectionTests
{
    #region Test Data Source

    private class TestDataSource : IDataSource<int, string>
    {
        public Task<IEnumerable<string>> FetchAsync(
            Range<int> range,
            CancellationToken cancellationToken)
        {
            var data = new List<string>();
            var start = (int)range.Start;
            var end = (int)range.End;
            
            // Generate data respecting range inclusivity (assuming closed ranges for simplicity)
            for (int i = start; i <= end; i++)
            {
                data.Add($"Item_{i}");
            }
            return Task.FromResult<IEnumerable<string>>(data);
        }
    }

    #endregion

    #region Task-Based Strategy Tests (Unbounded - Default)

    [Fact]
    public async Task WindowCache_WithNullCapacity_UsesTaskBasedStrategy()
    {
        // ARRANGE
        var dataSource = new TestDataSource();
        var domain = new IntegerFixedStepDomain();
        var options = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 2.0,
            readMode: UserCacheReadMode.Snapshot,
            rebalanceQueueCapacity: null  // Task-based strategy
        );

        await using var cache = new WindowCache<int, string, IntegerFixedStepDomain>(
            dataSource,
            domain,
            options
        );

        // ACT
        var result = await cache.GetDataAsync(Intervals.NET.Factories.Range.Closed<int>(10, 20), CancellationToken.None);

        // ASSERT
        Assert.Equal(11, result.Length);
        Assert.Equal("Item_10", result.Span[0]);
        Assert.Equal("Item_20", result.Span[10]);
    }

    [Fact]
    public async Task WindowCache_WithDefaultParameters_UsesTaskBasedStrategy()
    {
        // ARRANGE
        var dataSource = new TestDataSource();
        var domain = new IntegerFixedStepDomain();
        var options = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 2.0,
            readMode: UserCacheReadMode.Snapshot
            // rebalanceQueueCapacity not specified - defaults to null
        );

        await using var cache = new WindowCache<int, string, IntegerFixedStepDomain>(
            dataSource,
            domain,
            options
        );

        // ACT
        var result = await cache.GetDataAsync(Intervals.NET.Factories.Range.Closed<int>(0, 10), CancellationToken.None);

        // ASSERT
        Assert.Equal(11, result.Length);
        Assert.Equal("Item_0", result.Span[0]);
        Assert.Equal("Item_10", result.Span[10]);
    }

    [Fact]
    public async Task TaskBasedStrategy_UnderLoad_MaintainsSerialExecution()
    {
        // ARRANGE
        var dataSource = new TestDataSource();
        var domain = new IntegerFixedStepDomain();
        var options = new WindowCacheOptions(
            leftCacheSize: 0.5,
            rightCacheSize: 0.5,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0.1,
            rightThreshold: 0.1,
            debounceDelay: TimeSpan.FromMilliseconds(10),
            rebalanceQueueCapacity: null  // Task-based strategy
        );

        await using var cache = new WindowCache<int, string, IntegerFixedStepDomain>(
            dataSource,
            domain,
            options
        );

        // ACT - Rapid sequential requests (should trigger multiple rebalances)
        var tasks = new List<Task<ReadOnlyMemory<string>>>();
        for (int i = 0; i < 10; i++)
        {
            int start = i * 10;
            int end = start + 10;
            tasks.Add(cache.GetDataAsync(Intervals.NET.Factories.Range.Closed<int>(start, end), CancellationToken.None).AsTask());
        }

        var results = await Task.WhenAll(tasks);

        // ASSERT - All requests should complete successfully
        Assert.Equal(10, results.Length);
        foreach (var result in results)
        {
            Assert.Equal(11, result.Length);
        }

        // Wait for idle to ensure all background work completes
        await cache.WaitForIdleAsync(CancellationToken.None);
    }

    #endregion

    #region Channel-Based Strategy Tests (Bounded)

    [Fact]
    public async Task WindowCache_WithBoundedCapacity_UsesChannelBasedStrategy()
    {
        // ARRANGE
        var dataSource = new TestDataSource();
        var domain = new IntegerFixedStepDomain();
        var options = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 2.0,
            readMode: UserCacheReadMode.Snapshot,
            rebalanceQueueCapacity: 5  // Channel-based strategy with capacity 5
        );

        await using var cache = new WindowCache<int, string, IntegerFixedStepDomain>(
            dataSource,
            domain,
            options
        );

        // ACT
        var result = await cache.GetDataAsync(Intervals.NET.Factories.Range.Closed<int>(100, 110), CancellationToken.None);

        // ASSERT
        Assert.Equal(11, result.Length);
        Assert.Equal("Item_100", result.Span[0]);
        Assert.Equal("Item_110", result.Span[10]);
    }

    [Fact]
    public async Task ChannelBasedStrategy_UnderLoad_MaintainsSerialExecution()
    {
        // ARRANGE
        var dataSource = new TestDataSource();
        var domain = new IntegerFixedStepDomain();
        var options = new WindowCacheOptions(
            leftCacheSize: 0.5,
            rightCacheSize: 0.5,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0.1,
            rightThreshold: 0.1,
            debounceDelay: TimeSpan.FromMilliseconds(10),
            rebalanceQueueCapacity: 3  // Small capacity for backpressure testing
        );

        await using var cache = new WindowCache<int, string, IntegerFixedStepDomain>(
            dataSource,
            domain,
            options
        );

        // ACT - Rapid sequential requests (may experience backpressure)
        var tasks = new List<Task<ReadOnlyMemory<string>>>();
        for (int i = 0; i < 10; i++)
        {
            int start = i * 10;
            int end = start + 10;
            tasks.Add(cache.GetDataAsync(Intervals.NET.Factories.Range.Closed<int>(start, end), CancellationToken.None).AsTask());
        }

        var results = await Task.WhenAll(tasks);

        // ASSERT - All requests should complete successfully despite backpressure
        Assert.Equal(10, results.Length);
        foreach (var result in results)
        {
            Assert.Equal(11, result.Length);
        }

        // Wait for idle to ensure all background work completes
        await cache.WaitForIdleAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ChannelBasedStrategy_WithCapacityOne_WorksCorrectly()
    {
        // ARRANGE - Minimum capacity (strictest backpressure)
        var dataSource = new TestDataSource();
        var domain = new IntegerFixedStepDomain();
        var options = new WindowCacheOptions(
            leftCacheSize: 0.5,
            rightCacheSize: 0.5,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0.2,
            rightThreshold: 0.2,
            debounceDelay: TimeSpan.FromMilliseconds(5),
            rebalanceQueueCapacity: 1  // Capacity of 1
        );

        await using var cache = new WindowCache<int, string, IntegerFixedStepDomain>(
            dataSource,
            domain,
            options
        );

        // ACT - Multiple requests with strict queuing
        var result1 = await cache.GetDataAsync(Intervals.NET.Factories.Range.Closed<int>(0, 10), CancellationToken.None);
        var result2 = await cache.GetDataAsync(Intervals.NET.Factories.Range.Closed<int>(20, 30), CancellationToken.None);
        var result3 = await cache.GetDataAsync(Intervals.NET.Factories.Range.Closed<int>(40, 50), CancellationToken.None);

        // ASSERT
        Assert.Equal(11, result1.Length);
        Assert.Equal(11, result2.Length);
        Assert.Equal(11, result3.Length);

        // Wait for idle
        await cache.WaitForIdleAsync(CancellationToken.None);
    }

    #endregion

    #region Disposal Tests (Both Strategies)

    [Fact]
    public async Task TaskBasedStrategy_DisposalCompletesGracefully()
    {
        // ARRANGE
        var dataSource = new TestDataSource();
        var domain = new IntegerFixedStepDomain();
        var options = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 2.0,
            readMode: UserCacheReadMode.Snapshot,
            rebalanceQueueCapacity: null  // Task-based
        );

        var cache = new WindowCache<int, string, IntegerFixedStepDomain>(
            dataSource,
            domain,
            options
        );

        // ACT - Use cache then dispose
        await cache.GetDataAsync(Intervals.NET.Factories.Range.Closed<int>(0, 10), CancellationToken.None);
        await cache.DisposeAsync();

        // ASSERT - Should throw ObjectDisposedException after disposal
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            await cache.GetDataAsync(Intervals.NET.Factories.Range.Closed<int>(0, 10), CancellationToken.None);
        });
    }

    [Fact]
    public async Task ChannelBasedStrategy_DisposalCompletesGracefully()
    {
        // ARRANGE
        var dataSource = new TestDataSource();
        var domain = new IntegerFixedStepDomain();
        var options = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 2.0,
            readMode: UserCacheReadMode.Snapshot,
            rebalanceQueueCapacity: 5  // Channel-based
        );

        var cache = new WindowCache<int, string, IntegerFixedStepDomain>(
            dataSource,
            domain,
            options
        );

        // ACT - Use cache then dispose
        await cache.GetDataAsync(Intervals.NET.Factories.Range.Closed<int>(0, 10), CancellationToken.None);
        await cache.DisposeAsync();

        // ASSERT - Should throw ObjectDisposedException after disposal
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            await cache.GetDataAsync(Intervals.NET.Factories.Range.Closed<int>(0, 10), CancellationToken.None);
        });
    }

    #endregion
}
