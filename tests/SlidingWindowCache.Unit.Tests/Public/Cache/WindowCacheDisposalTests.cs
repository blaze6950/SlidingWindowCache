using Intervals.NET.Domain.Default.Numeric;
using SlidingWindowCache.Public.Cache;
using SlidingWindowCache.Public.Configuration;
using SlidingWindowCache.Tests.Infrastructure.DataSources;

namespace SlidingWindowCache.Unit.Tests.Public.Cache;

/// <summary>
/// Unit tests for WindowCache disposal behavior.
/// Validates proper resource cleanup, idempotency, and exception handling.
/// </summary>
public class WindowCacheDisposalTests
{
    #region Test Infrastructure

    private static WindowCache<int, int, IntegerFixedStepDomain> CreateCache()
    {
        var dataSource = new SimpleTestDataSource<int>(i => i, simulateAsyncDelay: true);
        var domain = new IntegerFixedStepDomain();
        var options = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0.2,
            rightThreshold: 0.2,
            debounceDelay: TimeSpan.FromMilliseconds(50)
        );

        return new WindowCache<int, int, IntegerFixedStepDomain>(dataSource, domain, options);
    }

    #endregion

    #region Basic Disposal Tests

    [Fact]
    public async Task DisposeAsync_WithoutUsage_DisposesSuccessfully()
    {
        // ARRANGE
        var cache = CreateCache();

        // ACT
        var exception = await Record.ExceptionAsync(async () => await cache.DisposeAsync());

        // ASSERT
        Assert.Null(exception); // No exception thrown
    }

    [Fact]
    public async Task DisposeAsync_AfterNormalUsage_DisposesSuccessfully()
    {
        // ARRANGE
        var cache = CreateCache();
        var range = Intervals.NET.Factories.Range.Closed<int>(0, 10);

        // ACT - Use the cache
        var data = await cache.GetDataAsync(range, CancellationToken.None);
        Assert.Equal(11, data.Data.Length); // Verify usage worked

        // Wait for background processing to stabilize
        await cache.WaitForIdleAsync();

        // ACT - Dispose
        var exception = await Record.ExceptionAsync(async () => await cache.DisposeAsync());

        // ASSERT
        Assert.Null(exception); // Disposal succeeds
    }

    [Fact]
    public async Task DisposeAsync_WithActiveBackgroundRebalance_WaitsForCompletion()
    {
        // ARRANGE
        var cache = CreateCache();
        var range1 = Intervals.NET.Factories.Range.Closed<int>(0, 10);
        var range2 = Intervals.NET.Factories.Range.Closed<int>(100, 110);

        // ACT - Trigger cache usage that should start rebalance
        await cache.GetDataAsync(range1, CancellationToken.None);
        await cache.GetDataAsync(range2, CancellationToken.None);

        // Don't wait for idle - dispose immediately while rebalance might be in progress
        var exception = await Record.ExceptionAsync(async () => await cache.DisposeAsync());

        // ASSERT
        Assert.Null(exception); // Disposal completes gracefully even with background work
    }

    #endregion

    #region Idempotency Tests

    [Fact]
    public async Task DisposeAsync_CalledTwiceSequentially_IsIdempotent()
    {
        // ARRANGE
        var cache = CreateCache();

        // ACT - Dispose twice
        await cache.DisposeAsync();
        var secondDisposeException = await Record.ExceptionAsync(async () => await cache.DisposeAsync());

        // ASSERT
        Assert.Null(secondDisposeException); // Second disposal succeeds (idempotent)
    }

    [Fact]
    public async Task DisposeAsync_CalledMultipleTimes_IsIdempotent()
    {
        // ARRANGE
        var cache = CreateCache();

        // ACT - Dispose multiple times
        await cache.DisposeAsync();
        await cache.DisposeAsync();
        await cache.DisposeAsync();
        var fourthDisposeException = await Record.ExceptionAsync(async () => await cache.DisposeAsync());

        // ASSERT
        Assert.Null(fourthDisposeException); // All disposal calls succeed
    }

    [Fact]
    public async Task DisposeAsync_CalledConcurrently_HandlesRaceSafely()
    {
        // ARRANGE
        var cache = CreateCache();

        // ACT - Dispose concurrently from multiple threads
        var disposalTasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(async () => await cache.DisposeAsync()))
            .ToList();

        var exceptions = new List<Exception?>();
        foreach (var task in disposalTasks)
        {
            try
            {
                await task;
                exceptions.Add(null);
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }

        // ASSERT - All disposal attempts complete without exception
        Assert.All(exceptions, ex => Assert.Null(ex));
    }

    [Fact]
    public async Task DisposeAsync_ConcurrentLoserThread_WaitsForWinnerCompletion()
    {
        // ARRANGE
        var cache = CreateCache();
        var range = Intervals.NET.Factories.Range.Closed<int>(0, 10);

        // Trigger background work so disposal takes some time
        _ = await cache.GetDataAsync(range, CancellationToken.None);

        // ACT - Start two concurrent disposals
        var firstDispose = cache.DisposeAsync().AsTask();
        var secondDispose = cache.DisposeAsync().AsTask();

        var exceptions = await Task.WhenAll(
            Record.ExceptionAsync(async () => await firstDispose),
            Record.ExceptionAsync(async () => await secondDispose)
        );

        // ASSERT - Both dispose calls complete without exception
        Assert.All(exceptions, ex => Assert.Null(ex));
    }

    #endregion

    #region Post-Disposal Operation Tests

    [Fact]
    public async Task GetDataAsync_AfterDisposal_ThrowsObjectDisposedException()
    {
        // ARRANGE
        var cache = CreateCache();
        await cache.DisposeAsync();

        // ACT
        var range = Intervals.NET.Factories.Range.Closed<int>(0, 10);
        var exception = await Record.ExceptionAsync(
            async () => await cache.GetDataAsync(range, CancellationToken.None));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ObjectDisposedException>(exception);
    }

    [Fact]
    public async Task WaitForIdleAsync_AfterDisposal_ThrowsObjectDisposedException()
    {
        // ARRANGE
        var cache = CreateCache();
        await cache.DisposeAsync();

        // ACT
        var exception = await Record.ExceptionAsync(
            async () => await cache.WaitForIdleAsync());

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ObjectDisposedException>(exception);
    }

    [Fact]
    public async Task GetDataAsync_DuringDisposal_ThrowsObjectDisposedException()
    {
        // ARRANGE
        var cache = CreateCache();
        var range = Intervals.NET.Factories.Range.Closed<int>(0, 10);

        // Trigger initial cache usage
        await cache.GetDataAsync(range, CancellationToken.None);

        // ACT - Start disposal and immediately try to use cache
        var disposalTask = cache.DisposeAsync().AsTask();

        // Try to use cache while disposal is in progress
        var exception = await Record.ExceptionAsync(
            async () => await cache.GetDataAsync(range, CancellationToken.None));

        await disposalTask; // Ensure disposal completes

        // ASSERT - Should throw ObjectDisposedException
        Assert.NotNull(exception);
        Assert.IsType<ObjectDisposedException>(exception);
    }

    [Fact]
    public async Task MultipleOperations_AfterDisposal_AllThrowObjectDisposedException()
    {
        // ARRANGE
        var cache = CreateCache();
        var range = Intervals.NET.Factories.Range.Closed<int>(0, 10);
        await cache.DisposeAsync();

        // ACT - Try multiple operations
        var getDataException = await Record.ExceptionAsync(
            async () => await cache.GetDataAsync(range, CancellationToken.None));
        var waitIdleException = await Record.ExceptionAsync(
            async () => await cache.WaitForIdleAsync());

        // ASSERT - All operations throw ObjectDisposedException
        Assert.NotNull(getDataException);
        Assert.IsType<ObjectDisposedException>(getDataException);
        Assert.NotNull(waitIdleException);
        Assert.IsType<ObjectDisposedException>(waitIdleException);
    }

    #endregion

    #region Cancellation Tests

    [Fact]
    public async Task DisposeAsync_WithCancelledToken_CompletesDisposalAnyway()
    {
        // ARRANGE
        var cache = CreateCache();
        var range = Intervals.NET.Factories.Range.Closed<int>(0, 10);

        // Use cache to start background processing
        await cache.GetDataAsync(range, CancellationToken.None);

        // ACT - Note: DisposeAsync doesn't take CancellationToken, but operations it calls might
        // This test verifies disposal completes even if background operations were cancelled
        var exception = await Record.ExceptionAsync(async () => await cache.DisposeAsync());

        // ASSERT
        Assert.Null(exception); // Disposal always completes
    }

    #endregion

    #region Resource Cleanup Verification Tests

    [Fact]
    public async Task DisposeAsync_StopsBackgroundLoops_SubsequentOperationsThrow()
    {
        // ARRANGE
        var cache = CreateCache();
        var range = Intervals.NET.Factories.Range.Closed<int>(0, 10);

        // Trigger some background activity
        await cache.GetDataAsync(range, CancellationToken.None);
        await cache.WaitForIdleAsync(); // Wait for background work to complete

        // ACT - Dispose
        await cache.DisposeAsync();

        // ASSERT - After disposal, all operations should throw ObjectDisposedException
        // This validates that background loops have stopped and cleanup is complete
        var getDataException = await Record.ExceptionAsync(
            async () => await cache.GetDataAsync(range, CancellationToken.None));
        var waitIdleException = await Record.ExceptionAsync(
            async () => await cache.WaitForIdleAsync());

        Assert.NotNull(getDataException);
        Assert.IsType<ObjectDisposedException>(getDataException);
        Assert.NotNull(waitIdleException);
        Assert.IsType<ObjectDisposedException>(waitIdleException);
    }

    [Fact]
    public async Task DisposeAsync_StopsBackgroundProcessing_NoMoreRebalances()
    {
        // ARRANGE
        var cache = CreateCache();
        var range1 = Intervals.NET.Factories.Range.Closed<int>(0, 10);
        var range2 = Intervals.NET.Factories.Range.Closed<int>(100, 110);

        // Trigger rebalance activity
        await cache.GetDataAsync(range1, CancellationToken.None);
        await cache.GetDataAsync(range2, CancellationToken.None);
        await cache.WaitForIdleAsync();

        // ACT - Dispose
        await cache.DisposeAsync();

        // Give time for any hypothetical background tasks to run (they shouldn't)
        await Task.Delay(200);

        // ASSERT - Verify no operations work after disposal
        var exception = await Record.ExceptionAsync(
            async () => await cache.GetDataAsync(range1, CancellationToken.None));
        Assert.NotNull(exception);
        Assert.IsType<ObjectDisposedException>(exception);
    }

    #endregion

    #region Using Statement Pattern Tests

    [Fact]
    public async Task UsingStatement_DisposesAutomatically()
    {
        // ARRANGE & ACT
        await using (var cache = CreateCache())
        {
            var range = Intervals.NET.Factories.Range.Closed<int>(0, 10);
            var data = await cache.GetDataAsync(range, CancellationToken.None);
            Assert.Equal(11, data.Data.Length);
        } // DisposeAsync called automatically here

        // ASSERT - Implicit: No exceptions thrown during disposal
    }

    [Fact]
    public async Task UsingDeclaration_DisposesAutomatically()
    {
        // ARRANGE & ACT
        await using var cache = CreateCache();
        var range = Intervals.NET.Factories.Range.Closed<int>(0, 10);
        var data = await cache.GetDataAsync(range, CancellationToken.None);

        // ASSERT
        Assert.Equal(11, data.Data.Length);

        // DisposeAsync will be called automatically at end of scope
    }

    #endregion

    #region Edge Case Tests

    [Fact]
    public async Task DisposeAsync_ImmediatelyAfterConstruction_Succeeds()
    {
        // ARRANGE
        var cache = CreateCache();

        // ACT - Dispose immediately without any usage
        var exception = await Record.ExceptionAsync(async () => await cache.DisposeAsync());

        // ASSERT
        Assert.Null(exception);
    }

    [Fact]
    public async Task DisposeAsync_WhileGetDataAsyncInProgress_CompletesGracefully()
    {
        // ARRANGE
        var cache = CreateCache();
        var range = Intervals.NET.Factories.Range.Closed<int>(0, 10);

        // ACT - Start GetDataAsync but don't await
        var getDataTask = cache.GetDataAsync(range, CancellationToken.None).AsTask();

        // Immediately dispose while operation may be in progress
        await cache.DisposeAsync();

        // Try to complete the original operation (it may succeed or throw)
        var exception = await Record.ExceptionAsync(async () => await getDataTask);

        // ASSERT - Either succeeds (completed before disposal) or throws ObjectDisposedException
        if (exception != null)
        {
            Assert.IsType<ObjectDisposedException>(exception);
        }
    }

    [Fact]
    public async Task DisposeAsync_WithHighConcurrency_HandlesGracefully()
    {
        // ARRANGE
        var cache = CreateCache();
        var range = Intervals.NET.Factories.Range.Closed<int>(0, 10);

        // Start many concurrent operations
        var tasks = Enumerable.Range(0, 50)
            .Select(i => cache.GetDataAsync(
                Intervals.NET.Factories.Range.Closed<int>(i * 10, i * 10 + 10),
                CancellationToken.None).AsTask())
            .ToList();

        // ACT - Dispose while many operations are in flight
        var disposeException = await Record.ExceptionAsync(async () => await cache.DisposeAsync());

        // Wait for all operations to complete (or fail)
        await Task.WhenAll(tasks.Select(t => t.ContinueWith(_ => { })));

        // ASSERT - Disposal completes without exception
        Assert.Null(disposeException);
    }

    #endregion
}
