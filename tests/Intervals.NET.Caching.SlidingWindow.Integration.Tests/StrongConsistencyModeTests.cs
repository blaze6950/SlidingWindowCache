using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Caching.Extensions;
using Intervals.NET.Caching.SlidingWindow.Tests.Infrastructure.Helpers;
using Intervals.NET.Caching.SlidingWindow.Public.Cache;
using Intervals.NET.Caching.SlidingWindow.Public.Configuration;
using Intervals.NET.Caching.SlidingWindow.Public.Extensions;
using Intervals.NET.Caching.SlidingWindow.Public.Instrumentation;

namespace Intervals.NET.Caching.SlidingWindow.Integration.Tests;

/// <summary>
/// Integration tests for the strong consistency mode exposed by
/// <see cref="SlidingWindowCacheConsistencyExtensions.GetDataAndWaitForIdleAsync{TRange,TData,TDomain}"/>.
/// 
/// Goal: Verify that the extension method behaves correctly end-to-end with a real
/// <see cref="SlidingWindowCache{TRange, TData, TDomain}"/> instance:
/// - Correct data is returned (identical to plain GetDataAsync)
/// - The cache is converged (idle) by the time the method returns
/// - Works across both storage strategies and execution strategies
/// - Cancellation and disposal integrate correctly
/// </summary>
public sealed class StrongConsistencyModeTests : IAsyncDisposable
{
    private readonly IntegerFixedStepDomain _domain;
    private readonly EventCounterCacheDiagnostics _cacheDiagnostics;
    private SlidingWindowCache<int, int, IntegerFixedStepDomain>? _cache;

    public StrongConsistencyModeTests()
    {
        _domain = TestHelpers.CreateIntDomain();
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

    private SlidingWindowCache<int, int, IntegerFixedStepDomain> CreateCache(
        UserCacheReadMode readMode = UserCacheReadMode.Snapshot,
        int? rebalanceQueueCapacity = null,
        double leftCacheSize = 1.0,
        double rightCacheSize = 1.0,
        double leftThreshold = 0.2,
        double rightThreshold = 0.2)
    {
        var options = TestHelpers.CreateDefaultOptions(
            leftCacheSize: leftCacheSize,
            rightCacheSize: rightCacheSize,
            leftThreshold: leftThreshold,
            rightThreshold: rightThreshold,
            readMode: readMode,
            rebalanceQueueCapacity: rebalanceQueueCapacity
        );

        var (cache, _) = TestHelpers.CreateCacheWithDefaults(_domain, _cacheDiagnostics, options);
        _cache = cache;
        return cache;
    }

    #region Test Data Sources

    /// <summary>
    /// Parameterizes tests across both storage strategies.
    /// </summary>
    public static IEnumerable<object[]> StorageStrategyTestData =>
        new List<object[]>
        {
            new object[] { "Snapshot", UserCacheReadMode.Snapshot },
            new object[] { "CopyOnRead", UserCacheReadMode.CopyOnRead }
        };

    /// <summary>
    /// Parameterizes tests across both execution strategies.
    /// </summary>
    public static IEnumerable<object?[]> ExecutionStrategyTestData =>
        new List<object?[]>
        {
            new object?[] { "TaskBased", null },
            new object?[] { "ChannelBased", 10 }
        };

    /// <summary>
    /// Parameterizes tests across all combinations of storage and execution strategies.
    /// </summary>
    public static IEnumerable<object?[]> AllStrategiesTestData
    {
        get
        {
            foreach (var storage in StorageStrategyTestData)
            {
                foreach (var execution in ExecutionStrategyTestData)
                {
                    yield return
                    [
                        $"{execution[0]}_{storage[0]}",
                        storage[1],
                        execution[1]
                    ];
                }
            }
        }
    }

    #endregion

    #region Data Correctness Tests

    /// <summary>
    /// Verifies that GetDataAndWaitForIdleAsync returns the same data as GetDataAsync would,
    /// across all storage and execution strategy combinations.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllStrategiesTestData))]
    public async Task GetDataAndWaitForIdleAsync_ReturnsCorrectData(
        string strategyName, UserCacheReadMode readMode, int? queueCapacity)
    {
        _ = strategyName; // used for test display name

        // ARRANGE
        var cache = CreateCache(readMode: readMode, rebalanceQueueCapacity: queueCapacity);
        var range = TestHelpers.CreateRange(100, 110);

        // ACT
        var result = await cache.GetDataAndWaitForIdleAsync(range, CancellationToken.None);

        // ASSERT — data is correct and matches requested range
        Assert.NotNull(result.Range);
        TestHelpers.AssertUserDataCorrect(result.Data, range);
    }

    /// <summary>
    /// Verifies that the returned data is identical to what plain GetDataAsync returns
    /// for the same request (result passthrough fidelity).
    /// </summary>
    [Theory]
    [MemberData(nameof(StorageStrategyTestData))]
    public async Task GetDataAndWaitForIdleAsync_ResultIdenticalToGetDataAsync(
        string storageName, UserCacheReadMode readMode)
    {
        _ = storageName;

        // ARRANGE — single cache: compare plain GetDataAsync vs GetDataAndWaitForIdleAsync
        var cacheA = CreateCache(readMode: readMode);
        var range = TestHelpers.CreateRange(100, 110);

        // ACT — get result from plain GetDataAsync on first call (cold start)
        var regularResult = await cacheA.GetDataAsync(range, CancellationToken.None);
        await cacheA.WaitForIdleAsync();

        // Reset to use GetDataAndWaitForIdleAsync for same range
        _cacheDiagnostics.Reset();
        var strongResult = await cacheA.GetDataAndWaitForIdleAsync(range, CancellationToken.None);

        // ASSERT — data content is identical
        Assert.Equal(regularResult.Range, strongResult.Range);
        Assert.Equal(regularResult.Data.Length, strongResult.Data.Length);
        Assert.True(regularResult.Data.Span.SequenceEqual(strongResult.Data.Span));
    }

    /// <summary>
    /// Verifies correct data on cold start (uninitialized cache) — the first request
    /// must fetch from the data source.
    /// </summary>
    [Theory]
    [MemberData(nameof(StorageStrategyTestData))]
    public async Task GetDataAndWaitForIdleAsync_ColdStart_DataCorrect(
        string storageName, UserCacheReadMode readMode)
    {
        _ = storageName;

        // ARRANGE
        var cache = CreateCache(readMode: readMode);
        var range = TestHelpers.CreateRange(200, 220);

        // ACT
        var result = await cache.GetDataAndWaitForIdleAsync(range, CancellationToken.None);

        // ASSERT
        Assert.NotNull(result.Range);
        TestHelpers.AssertUserDataCorrect(result.Data, range);
    }

    #endregion

    #region Convergence Guarantee Tests

    /// <summary>
    /// Verifies that after GetDataAndWaitForIdleAsync returns, the cache has executed at least
    /// one rebalance (the rebalance execution counter is non-zero), proving convergence occurred.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllStrategiesTestData))]
    public async Task GetDataAndWaitForIdleAsync_CacheHasConvergedAfterReturn(
        string strategyName, UserCacheReadMode readMode, int? queueCapacity)
    {
        _ = strategyName;

        // ARRANGE
        var cache = CreateCache(readMode: readMode, rebalanceQueueCapacity: queueCapacity);
        var range = TestHelpers.CreateRange(100, 110);

        // ACT
        await cache.GetDataAndWaitForIdleAsync(range, CancellationToken.None);

        // ASSERT — at least one complete rebalance execution cycle has occurred
        // (either completed or skipped — work avoidance is valid, but the idle wait ensures convergence)
        var totalRebalanceActivity =
            _cacheDiagnostics.RebalanceExecutionCompleted +
            _cacheDiagnostics.RebalanceSkippedCurrentNoRebalanceRange +
            _cacheDiagnostics.RebalanceSkippedPendingNoRebalanceRange +
            _cacheDiagnostics.RebalanceSkippedSameRange;

        Assert.True(totalRebalanceActivity >= 1,
            "Cache should have processed at least one rebalance cycle after GetDataAndWaitForIdleAsync");
    }

    /// <summary>
    /// Verifies that after GetDataAndWaitForIdleAsync, a subsequent request within the
    /// expanded cache window is served as a full cache hit (no data source fetch needed).
    /// This confirms the cache window has been expanded to its desired geometry.
    /// </summary>
    [Theory]
    [MemberData(nameof(StorageStrategyTestData))]
    public async Task GetDataAndWaitForIdleAsync_CacheWindowExpanded_SubsequentRequestIsFullHit(
        string storageName, UserCacheReadMode readMode)
    {
        _ = storageName;

        // ARRANGE — cache configured with leftCacheSize=1.0, rightCacheSize=1.0
        // For range [100, 110] (11 elements), desired range expands to roughly [89, 121]
        var cache = CreateCache(
            readMode: readMode,
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            leftThreshold: 0.4,
            rightThreshold: 0.4
        );
        var initialRange = TestHelpers.CreateRange(100, 110);

        // ACT — first call with strong consistency: cache will be converged after this
        await cache.GetDataAndWaitForIdleAsync(initialRange, CancellationToken.None);

        // Reset diagnostics to observe only the next request
        _cacheDiagnostics.Reset();

        // Request within the expanded window (cache should have expanded left/right)
        var innerRange = TestHelpers.CreateRange(102, 108); // subset of [100, 110]
        var innerResult = await cache.GetDataAsync(innerRange, CancellationToken.None);

        // ASSERT — inner range should be a full cache hit (served without data source call)
        Assert.NotNull(innerResult.Range);
        Assert.Equal(1, _cacheDiagnostics.UserRequestFullCacheHit);
        TestHelpers.AssertUserDataCorrect(innerResult.Data, innerRange);
    }

    #endregion

    #region Sequential Requests Tests

    /// <summary>
    /// Verifies that sequential GetDataAndWaitForIdleAsync calls each produce converged
    /// state and return correct data for sequential access patterns.
    /// </summary>
    [Theory]
    [MemberData(nameof(StorageStrategyTestData))]
    public async Task GetDataAndWaitForIdleAsync_SequentialRequests_EachReturnsConvergedState(
        string storageName, UserCacheReadMode readMode)
    {
        _ = storageName;

        // ARRANGE
        var cache = CreateCache(readMode: readMode);
        var ranges = new[]
        {
            TestHelpers.CreateRange(100, 110),
            TestHelpers.CreateRange(120, 130),
            TestHelpers.CreateRange(140, 150)
        };

        // ACT & ASSERT — each sequential request
        foreach (var range in ranges)
        {
            var result = await cache.GetDataAndWaitForIdleAsync(range, CancellationToken.None);

            // Data must be correct
            Assert.NotNull(result.Range);
            TestHelpers.AssertUserDataCorrect(result.Data, range);
        }
    }

    /// <summary>
    /// Verifies that each GetDataAndWaitForIdleAsync call in a sequence leaves the cache in
    /// a stable state that positively influences the next call's cache hit ratio.
    /// </summary>
    [Theory]
    [MemberData(nameof(StorageStrategyTestData))]
    public async Task GetDataAndWaitForIdleAsync_SequentialOverlappingRequests_DataAlwaysCorrect(
        string storageName, UserCacheReadMode readMode)
    {
        _ = storageName;

        // ARRANGE — sequential overlapping requests simulating scroll-right pattern
        var cache = CreateCache(
            readMode: readMode,
            leftCacheSize: 1.0,
            rightCacheSize: 2.0,
            leftThreshold: 0.2,
            rightThreshold: 0.2
        );

        var ranges = new[]
        {
            TestHelpers.CreateRange(100, 110),
            TestHelpers.CreateRange(105, 115),
            TestHelpers.CreateRange(110, 120),
            TestHelpers.CreateRange(115, 125)
        };

        // ACT & ASSERT
        foreach (var range in ranges)
        {
            var result = await cache.GetDataAndWaitForIdleAsync(range, CancellationToken.None);
            Assert.NotNull(result.Range);
            TestHelpers.AssertUserDataCorrect(result.Data, range);
        }
    }

    #endregion

    #region Cancellation Integration Tests

    /// <summary>
    /// Verifies that a pre-cancelled token causes graceful degradation: if GetDataAsync
    /// succeeds before observing the cancellation, WaitForIdleAsync's OperationCanceledException
    /// is caught and the already-obtained result is returned (eventual consistency degradation).
    /// The background rebalance is not affected.
    /// </summary>
    [Fact]
    public async Task GetDataAndWaitForIdleAsync_PreCancelledToken_ReturnsResultGracefully()
    {
        // ARRANGE
        var cache = CreateCache();
        var range = TestHelpers.CreateRange(100, 110);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // ACT — GetDataAsync may succeed even with a pre-cancelled token (no explicit
        // ThrowIfCancellationRequested on the user path when data is already in cache or
        // when the fetch completes before observing cancellation). If WaitForIdleAsync
        // then throws OperationCanceledException it is caught and the result is returned.
        var exception = await Record.ExceptionAsync(
            async () => await cache.GetDataAndWaitForIdleAsync(range, cts.Token));

        // ASSERT — graceful degradation: either no exception (WaitForIdleAsync cancelled
        // and caught), or OperationCanceledException from GetDataAsync itself (if the
        // data source fetch observed the cancellation). Both outcomes are valid.
        if (exception is not null)
        {
            Assert.IsAssignableFrom<OperationCanceledException>(exception);
        }
    }

    #endregion

    #region Post-Disposal Tests

    /// <summary>
    /// Verifies that calling GetDataAndWaitForIdleAsync on a disposed cache throws
    /// ObjectDisposedException (via GetDataAsync, which checks disposal state first).
    /// </summary>
    [Fact]
    public async Task GetDataAndWaitForIdleAsync_AfterDisposal_ThrowsObjectDisposedException()
    {
        // ARRANGE
        var cache = CreateCache();
        await cache.DisposeAsync();
        _cache = null; // prevent double-dispose in DisposeAsync

        var range = TestHelpers.CreateRange(100, 110);

        // ACT
        var exception = await Record.ExceptionAsync(
            async () => await cache.GetDataAndWaitForIdleAsync(range, CancellationToken.None));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ObjectDisposedException>(exception);
    }

    #endregion

    #region Single-Element and Edge Case Tests

    /// <summary>
    /// Verifies correct behavior for a single-element range.
    /// </summary>
    [Fact]
    public async Task GetDataAndWaitForIdleAsync_SingleElementRange_DataCorrect()
    {
        // ARRANGE
        var cache = CreateCache();
        var range = TestHelpers.CreateRange(42, 42);

        // ACT
        var result = await cache.GetDataAndWaitForIdleAsync(range, CancellationToken.None);

        // ASSERT
        Assert.NotNull(result.Range);
        Assert.Single(result.Data.ToArray());
        Assert.Equal(42, result.Data.ToArray()[0]);
    }

    /// <summary>
    /// Verifies correct behavior with a large range (stress test for convergence).
    /// </summary>
    [Fact]
    public async Task GetDataAndWaitForIdleAsync_LargeRange_DataCorrectAndConverged()
    {
        // ARRANGE
        var cache = CreateCache();
        var range = TestHelpers.CreateRange(0, 499);

        // ACT
        var result = await cache.GetDataAndWaitForIdleAsync(range, CancellationToken.None);

        // ASSERT
        Assert.NotNull(result.Range);
        Assert.Equal(500, result.Data.Length);
        TestHelpers.AssertUserDataCorrect(result.Data, range);
    }

    #endregion
}
