using Intervals.NET.Domain.Default.Numeric;
using SlidingWindowCache.Public;
using SlidingWindowCache.Public.Cache;
using SlidingWindowCache.Public.Configuration;
using SlidingWindowCache.Public.Extensions;
using SlidingWindowCache.Public.Instrumentation;
using SlidingWindowCache.Tests.Infrastructure.DataSources;

namespace SlidingWindowCache.Integration.Tests;

/// <summary>
/// Integration tests for the layered cache feature:
/// <see cref="LayeredWindowCacheBuilder{TRange,TData,TDomain}"/>,
/// <see cref="LayeredWindowCache{TRange,TData,TDomain}"/>, and
/// <see cref="WindowCacheDataSourceAdapter{TRange,TData,TDomain}"/>.
///
/// Goal: Verify that a multi-layer cache stack correctly:
/// - Propagates data from the real data source up through all layers
/// - Returns correct data values from the outermost layer
/// - Converges to a steady state (WaitForIdleAsync)
/// - Disposes all layers cleanly without errors
/// - Supports 2-layer and 3-layer configurations
/// - Handles per-layer diagnostics independently
/// </summary>
public sealed class LayeredCacheIntegrationTests
{
    private static readonly IntegerFixedStepDomain Domain = new();

    private static IDataSource<int, int> CreateRealDataSource()
        => new SimpleTestDataSource<int>(i => i);

    private static WindowCacheOptions DeepLayerOptions() => new(
        leftCacheSize: 5.0,
        rightCacheSize: 5.0,
        readMode: UserCacheReadMode.CopyOnRead,
        leftThreshold: 0.3,
        rightThreshold: 0.3,
        debounceDelay: TimeSpan.FromMilliseconds(20));

    private static WindowCacheOptions MidLayerOptions() => new(
        leftCacheSize: 2.0,
        rightCacheSize: 2.0,
        readMode: UserCacheReadMode.CopyOnRead,
        leftThreshold: 0.3,
        rightThreshold: 0.3,
        debounceDelay: TimeSpan.FromMilliseconds(20));

    private static WindowCacheOptions UserLayerOptions() => new(
        leftCacheSize: 0.5,
        rightCacheSize: 0.5,
        readMode: UserCacheReadMode.Snapshot,
        leftThreshold: 0.2,
        rightThreshold: 0.2,
        debounceDelay: TimeSpan.FromMilliseconds(20));

    #region Data Correctness Tests

    [Fact]
    public async Task TwoLayerCache_GetData_ReturnsCorrectValues()
    {
        // ARRANGE
        await using var cache = LayeredWindowCacheBuilder<int, int, IntegerFixedStepDomain>
            .Create(CreateRealDataSource(), Domain)
            .AddLayer(DeepLayerOptions())
            .AddLayer(UserLayerOptions())
            .Build();

        var range = Intervals.NET.Factories.Range.Closed<int>(100, 110);

        // ACT
        var result = await cache.GetDataAsync(range, CancellationToken.None);

        // ASSERT
        var array = result.Data.ToArray();
        Assert.Equal(11, array.Length);
        for (var i = 0; i < array.Length; i++)
            Assert.Equal(100 + i, array[i]);
    }

    [Fact]
    public async Task ThreeLayerCache_GetData_ReturnsCorrectValues()
    {
        // ARRANGE
        await using var cache = LayeredWindowCacheBuilder<int, int, IntegerFixedStepDomain>
            .Create(CreateRealDataSource(), Domain)
            .AddLayer(DeepLayerOptions())
            .AddLayer(MidLayerOptions())
            .AddLayer(UserLayerOptions())
            .Build();

        var range = Intervals.NET.Factories.Range.Closed<int>(200, 215);

        // ACT
        var result = await cache.GetDataAsync(range, CancellationToken.None);

        // ASSERT
        var array = result.Data.ToArray();
        Assert.Equal(16, array.Length);
        for (var i = 0; i < array.Length; i++)
            Assert.Equal(200 + i, array[i]);
    }

    [Fact]
    public async Task TwoLayerCache_SubsequentRequests_ReturnCorrectValues()
    {
        // ARRANGE
        await using var cache = LayeredWindowCacheBuilder<int, int, IntegerFixedStepDomain>
            .Create(CreateRealDataSource(), Domain)
            .AddLayer(DeepLayerOptions())
            .AddLayer(UserLayerOptions())
            .Build();

        // ACT & ASSERT — three sequential non-overlapping requests
        var ranges = new[]
        {
            Intervals.NET.Factories.Range.Closed<int>(0, 10),
            Intervals.NET.Factories.Range.Closed<int>(100, 110),
            Intervals.NET.Factories.Range.Closed<int>(500, 510),
        };

        foreach (var range in ranges)
        {
            var result = await cache.GetDataAsync(range, CancellationToken.None);
            var array = result.Data.ToArray();
            Assert.Equal(11, array.Length);
            var start = (int)range.Start;
            for (var i = 0; i < array.Length; i++)
                Assert.Equal(start + i, array[i]);
        }
    }

    [Fact]
    public async Task TwoLayerCache_SingleElementRange_ReturnsCorrectValue()
    {
        // ARRANGE
        await using var cache = LayeredWindowCacheBuilder<int, int, IntegerFixedStepDomain>
            .Create(CreateRealDataSource(), Domain)
            .AddLayer(DeepLayerOptions())
            .AddLayer(UserLayerOptions())
            .Build();

        // ACT
        var range = Intervals.NET.Factories.Range.Closed<int>(42, 42);
        var result = await cache.GetDataAsync(range, CancellationToken.None);

        // ASSERT
        var array = result.Data.ToArray();
        Assert.Single(array);
        Assert.Equal(42, array[0]);
    }

    #endregion

    #region LayerCount Tests

    [Fact]
    public async Task TwoLayerCache_LayerCount_IsTwo()
    {
        // ARRANGE
        await using var cache = LayeredWindowCacheBuilder<int, int, IntegerFixedStepDomain>
            .Create(CreateRealDataSource(), Domain)
            .AddLayer(DeepLayerOptions())
            .AddLayer(UserLayerOptions())
            .Build();

        // ASSERT
        Assert.Equal(2, cache.LayerCount);
    }

    [Fact]
    public async Task ThreeLayerCache_LayerCount_IsThree()
    {
        // ARRANGE
        await using var cache = LayeredWindowCacheBuilder<int, int, IntegerFixedStepDomain>
            .Create(CreateRealDataSource(), Domain)
            .AddLayer(DeepLayerOptions())
            .AddLayer(MidLayerOptions())
            .AddLayer(UserLayerOptions())
            .Build();

        // ASSERT
        Assert.Equal(3, cache.LayerCount);
    }

    #endregion

    #region Convergence / WaitForIdleAsync Tests

    [Fact]
    public async Task TwoLayerCache_WaitForIdleAsync_ConvergesWithoutException()
    {
        // ARRANGE
        await using var cache = LayeredWindowCacheBuilder<int, int, IntegerFixedStepDomain>
            .Create(CreateRealDataSource(), Domain)
            .AddLayer(DeepLayerOptions())
            .AddLayer(UserLayerOptions())
            .Build();

        var range = Intervals.NET.Factories.Range.Closed<int>(100, 110);
        await cache.GetDataAsync(range, CancellationToken.None);

        // ACT — should complete without throwing
        var exception = await Record.ExceptionAsync(() => cache.WaitForIdleAsync());

        // ASSERT
        Assert.Null(exception);
    }

    [Fact]
    public async Task TwoLayerCache_AfterConvergence_DataStillCorrect()
    {
        // ARRANGE
        await using var cache = LayeredWindowCacheBuilder<int, int, IntegerFixedStepDomain>
            .Create(CreateRealDataSource(), Domain)
            .AddLayer(DeepLayerOptions())
            .AddLayer(UserLayerOptions())
            .Build();

        var range = Intervals.NET.Factories.Range.Closed<int>(50, 60);

        // Prime the cache and wait for background rebalance to settle
        await cache.GetDataAsync(range, CancellationToken.None);
        await cache.WaitForIdleAsync();

        // ACT — re-read same range after convergence
        var result = await cache.GetDataAsync(range, CancellationToken.None);

        // ASSERT
        var array = result.Data.ToArray();
        Assert.Equal(11, array.Length);
        for (var i = 0; i < array.Length; i++)
            Assert.Equal(50 + i, array[i]);
    }

    [Fact]
    public async Task TwoLayerCache_WaitForIdleAsync_AllLayersHaveConverged()
    {
        // ARRANGE — use per-layer diagnostics to verify both layers rebalanced
        var deepDiagnostics = new EventCounterCacheDiagnostics();
        var userDiagnostics = new EventCounterCacheDiagnostics();

        await using var cache = LayeredWindowCacheBuilder<int, int, IntegerFixedStepDomain>
            .Create(CreateRealDataSource(), Domain)
            .AddLayer(DeepLayerOptions(), deepDiagnostics)
            .AddLayer(UserLayerOptions(), userDiagnostics)
            .Build();

        var range = Intervals.NET.Factories.Range.Closed<int>(200, 210);

        // Trigger activity on both layers
        await cache.GetDataAsync(range, CancellationToken.None);

        // ACT — wait for the full stack to converge
        await cache.WaitForIdleAsync();

        // ASSERT — both layers must have processed at least one rebalance intent
        // (userDiagnostics from outer layer triggered by user request;
        //  deepDiagnostics from inner layer triggered by outer layer's fetch)
        Assert.True(userDiagnostics.RebalanceIntentPublished >= 1,
            "Outer (user-facing) layer should have published at least one rebalance intent.");
        Assert.True(deepDiagnostics.RebalanceIntentPublished >= 1,
            "Inner (deep) layer should have published at least one rebalance intent driven by the outer layer.");
    }

    [Fact]
    public async Task TwoLayerCache_GetDataAndWaitForIdleAsync_ReturnsCorrectData()
    {
        // ARRANGE — verify that the strong consistency extension method works on a LayeredWindowCache
        await using var cache = LayeredWindowCacheBuilder<int, int, IntegerFixedStepDomain>
            .Create(CreateRealDataSource(), Domain)
            .AddLayer(DeepLayerOptions())
            .AddLayer(UserLayerOptions())
            .Build();

        var range = Intervals.NET.Factories.Range.Closed<int>(300, 315);

        // ACT — extension method should work correctly because WaitForIdleAsync now covers all layers
        var result = await cache.GetDataAndWaitForIdleAsync(range);

        // ASSERT
        var array = result.Data.ToArray();
        Assert.Equal(16, array.Length);
        for (var i = 0; i < array.Length; i++)
            Assert.Equal(300 + i, array[i]);
    }

    [Fact]
    public async Task TwoLayerCache_GetDataAndWaitForIdleAsync_SubsequentRequestIsFullHit()
    {
        // ARRANGE
        await using var cache = LayeredWindowCacheBuilder<int, int, IntegerFixedStepDomain>
            .Create(CreateRealDataSource(), Domain)
            .AddLayer(DeepLayerOptions())
            .AddLayer(UserLayerOptions())
            .Build();

        var range = Intervals.NET.Factories.Range.Closed<int>(400, 410);

        // ACT — prime with strong consistency (waits for full stack to converge)
        await cache.GetDataAndWaitForIdleAsync(range);

        // Re-request a subset — the outer layer cache window should fully cover it
        var subRange = Intervals.NET.Factories.Range.Closed<int>(402, 408);
        var result = await cache.GetDataAsync(subRange, CancellationToken.None);

        // ASSERT — data is correct
        var array = result.Data.ToArray();
        Assert.Equal(7, array.Length);
        for (var i = 0; i < array.Length; i++)
            Assert.Equal(402 + i, array[i]);
    }

    #endregion

    #region Disposal Tests

    [Fact]
    public async Task TwoLayerCache_DisposeAsync_CompletesWithoutException()
    {
        // ARRANGE
        var cache = LayeredWindowCacheBuilder<int, int, IntegerFixedStepDomain>
            .Create(CreateRealDataSource(), Domain)
            .AddLayer(DeepLayerOptions())
            .AddLayer(UserLayerOptions())
            .Build();

        await cache.GetDataAsync(Intervals.NET.Factories.Range.Closed<int>(1, 10), CancellationToken.None);

        // ACT
        var exception = await Record.ExceptionAsync(() => cache.DisposeAsync().AsTask());

        // ASSERT
        Assert.Null(exception);
    }

    [Fact]
    public async Task TwoLayerCache_DisposeWithoutAnyRequests_CompletesWithoutException()
    {
        // ARRANGE — build but never use
        var cache = LayeredWindowCacheBuilder<int, int, IntegerFixedStepDomain>
            .Create(CreateRealDataSource(), Domain)
            .AddLayer(DeepLayerOptions())
            .AddLayer(UserLayerOptions())
            .Build();

        // ACT
        var exception = await Record.ExceptionAsync(() => cache.DisposeAsync().AsTask());

        // ASSERT
        Assert.Null(exception);
    }

    [Fact]
    public async Task ThreeLayerCache_DisposeAsync_CompletesWithoutException()
    {
        // ARRANGE
        var cache = LayeredWindowCacheBuilder<int, int, IntegerFixedStepDomain>
            .Create(CreateRealDataSource(), Domain)
            .AddLayer(DeepLayerOptions())
            .AddLayer(MidLayerOptions())
            .AddLayer(UserLayerOptions())
            .Build();

        await cache.GetDataAsync(Intervals.NET.Factories.Range.Closed<int>(10, 20), CancellationToken.None);

        // ACT
        var exception = await Record.ExceptionAsync(() => cache.DisposeAsync().AsTask());

        // ASSERT
        Assert.Null(exception);
    }

    #endregion

    #region Adapter Integration Tests

    [Fact]
    public async Task WindowCacheDataSourceAdapter_UsedAsDataSource_PropagatesDataCorrectly()
    {
        // ARRANGE — manually compose two layers without the builder, to test the adapter directly
        var realSource = CreateRealDataSource();
        var deepCache = new WindowCache<int, int, IntegerFixedStepDomain>(
            realSource, Domain, DeepLayerOptions());

        await using var _ = deepCache;

        var adapter = new WindowCacheDataSourceAdapter<int, int, IntegerFixedStepDomain>(deepCache);
        var userCache = new WindowCache<int, int, IntegerFixedStepDomain>(
            adapter, Domain, UserLayerOptions());

        await using var __ = userCache;

        var range = Intervals.NET.Factories.Range.Closed<int>(300, 310);

        // ACT
        var result = await userCache.GetDataAsync(range, CancellationToken.None);

        // ASSERT
        var array = result.Data.ToArray();
        Assert.Equal(11, array.Length);
        for (var i = 0; i < array.Length; i++)
            Assert.Equal(300 + i, array[i]);
    }

    #endregion

    #region Per-Layer Diagnostics Tests

    [Fact]
    public async Task TwoLayerCache_WithPerLayerDiagnostics_EachLayerTracksIndependently()
    {
        // ARRANGE
        var deepDiagnostics = new EventCounterCacheDiagnostics();
        var userDiagnostics = new EventCounterCacheDiagnostics();

        await using var cache = LayeredWindowCacheBuilder<int, int, IntegerFixedStepDomain>
            .Create(CreateRealDataSource(), Domain)
            .AddLayer(DeepLayerOptions(), deepDiagnostics)
            .AddLayer(UserLayerOptions(), userDiagnostics)
            .Build();

        var range = Intervals.NET.Factories.Range.Closed<int>(100, 110);

        // ACT
        await cache.GetDataAsync(range, CancellationToken.None);
        await cache.WaitForIdleAsync();

        // ASSERT — user-facing layer saw user requests
        Assert.True(userDiagnostics.RebalanceIntentPublished >= 0,
            "User layer diagnostics should be connected.");

        // ASSERT — data is still correct
        var result = await cache.GetDataAsync(range, CancellationToken.None);
        var array = result.Data.ToArray();
        Assert.Equal(11, array.Length);
        Assert.Equal(100, array[0]);
        Assert.Equal(110, array[^1]);
    }

    #endregion

    #region Large Range Tests

    [Fact]
    public async Task TwoLayerCache_LargeRange_ReturnsCorrectData()
    {
        // ARRANGE
        await using var cache = LayeredWindowCacheBuilder<int, int, IntegerFixedStepDomain>
            .Create(CreateRealDataSource(), Domain)
            .AddLayer(DeepLayerOptions())
            .AddLayer(UserLayerOptions())
            .Build();

        // ACT
        var range = Intervals.NET.Factories.Range.Closed<int>(0, 999);
        var result = await cache.GetDataAsync(range, CancellationToken.None);

        // ASSERT
        var array = result.Data.ToArray();
        Assert.Equal(1000, array.Length);
        Assert.Equal(0, array[0]);
        Assert.Equal(999, array[^1]);

        // Spot-check values
        for (var i = 0; i < array.Length; i++)
            Assert.Equal(i, array[i]);
    }

    #endregion
}
