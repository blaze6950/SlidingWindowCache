using Intervals.NET.Domain.Abstractions;
using Intervals.NET.Domain.Default.Numeric;
using SlidingWindowCache.Public;
using SlidingWindowCache.Public.Cache;
using SlidingWindowCache.Public.Configuration;
using SlidingWindowCache.Public.Instrumentation;
using SlidingWindowCache.Tests.Infrastructure.DataSources;
using SlidingWindowCache.Tests.Infrastructure.Helpers;

namespace SlidingWindowCache.Unit.Tests.Public.Cache;

/// <summary>
/// Unit tests for <see cref="LayeredWindowCacheBuilder{TRange,TData,TDomain}"/>.
/// Validates the builder API: construction, layer addition, build validation,
/// layer ordering, and the resulting <see cref="LayeredWindowCache{TRange,TData,TDomain}"/>.
/// Uses <see cref="SimpleTestDataSource{TData}"/> as a lightweight real data source to avoid
/// mocking the complex <see cref="IDataSource{TRange,TData}"/> interface for these tests.
/// </summary>
public sealed class LayeredWindowCacheBuilderTests
{
    #region Test Infrastructure

    private static IntegerFixedStepDomain Domain => new();

    private static IDataSource<int, int> CreateDataSource()
        => new SimpleTestDataSource<int>(i => i);

    private static WindowCacheOptions DefaultOptions(
        UserCacheReadMode mode = UserCacheReadMode.Snapshot)
        => TestHelpers.CreateDefaultOptions(readMode: mode);

    #endregion

    #region Create() Tests

    [Fact]
    public void Create_WithNullDataSource_ThrowsArgumentNullException()
    {
        // ACT
        var exception = Record.Exception(() =>
            LayeredWindowCacheBuilder<int, int, IntegerFixedStepDomain>
                .Create(null!, Domain));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentNullException>(exception);
        Assert.Contains("dataSource", ((ArgumentNullException)exception).ParamName);
    }

    [Fact]
    public void Create_WithNullDomain_ThrowsArgumentNullException()
    {
        // ARRANGE — TDomain must be a reference type to accept null;
        // use IRangeDomain<int> as the type parameter (interface = reference type)
        var dataSource = CreateDataSource();

        // ACT
        var exception = Record.Exception(() =>
            LayeredWindowCacheBuilder<int, int, IRangeDomain<int>>
                .Create(dataSource, null!));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentNullException>(exception);
        Assert.Contains("domain", ((ArgumentNullException)exception).ParamName);
    }

    [Fact]
    public void Create_WithValidArguments_ReturnsBuilder()
    {
        // ACT
        var builder = LayeredWindowCacheBuilder<int, int, IntegerFixedStepDomain>
            .Create(CreateDataSource(), Domain);

        // ASSERT
        Assert.NotNull(builder);
    }

    #endregion

    #region AddLayer() Tests

    [Fact]
    public void AddLayer_WithNullOptions_ThrowsArgumentNullException()
    {
        // ARRANGE
        var builder = LayeredWindowCacheBuilder<int, int, IntegerFixedStepDomain>
            .Create(CreateDataSource(), Domain);

        // ACT
        var exception = Record.Exception(() => builder.AddLayer(null!));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentNullException>(exception);
        Assert.Contains("options", ((ArgumentNullException)exception).ParamName);
    }

    [Fact]
    public void AddLayer_ReturnsBuilderForFluentChaining()
    {
        // ARRANGE
        var builder = LayeredWindowCacheBuilder<int, int, IntegerFixedStepDomain>
            .Create(CreateDataSource(), Domain);

        // ACT
        var returned = builder.AddLayer(DefaultOptions());

        // ASSERT — same instance for fluent chaining
        Assert.Same(builder, returned);
    }

    [Fact]
    public void AddLayer_MultipleCallsReturnSameBuilder()
    {
        // ARRANGE
        var builder = LayeredWindowCacheBuilder<int, int, IntegerFixedStepDomain>
            .Create(CreateDataSource(), Domain);

        // ACT
        var b1 = builder.AddLayer(DefaultOptions());
        var b2 = b1.AddLayer(DefaultOptions());
        var b3 = b2.AddLayer(DefaultOptions());

        // ASSERT
        Assert.Same(builder, b1);
        Assert.Same(builder, b2);
        Assert.Same(builder, b3);
    }

    [Fact]
    public void AddLayer_AcceptsDiagnosticsParameter()
    {
        // ARRANGE
        var builder = LayeredWindowCacheBuilder<int, int, IntegerFixedStepDomain>
            .Create(CreateDataSource(), Domain);
        var diagnostics = new EventCounterCacheDiagnostics();

        // ACT
        var exception = Record.Exception(() =>
            builder.AddLayer(DefaultOptions(), diagnostics));

        // ASSERT
        Assert.Null(exception);
    }

    [Fact]
    public void AddLayer_WithNullDiagnostics_DoesNotThrow()
    {
        // ARRANGE
        var builder = LayeredWindowCacheBuilder<int, int, IntegerFixedStepDomain>
            .Create(CreateDataSource(), Domain);

        // ACT
        var exception = Record.Exception(() =>
            builder.AddLayer(DefaultOptions(), null));

        // ASSERT
        Assert.Null(exception);
    }

    #endregion

    #region Build() Tests

    [Fact]
    public void Build_WithNoLayers_ThrowsInvalidOperationException()
    {
        // ARRANGE
        var builder = LayeredWindowCacheBuilder<int, int, IntegerFixedStepDomain>
            .Create(CreateDataSource(), Domain);

        // ACT
        var exception = Record.Exception(() => builder.Build());

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<InvalidOperationException>(exception);
    }

    [Fact]
    public async Task Build_WithSingleLayer_ReturnsLayeredCacheWithOneLayer()
    {
        // ARRANGE
        var builder = LayeredWindowCacheBuilder<int, int, IntegerFixedStepDomain>
            .Create(CreateDataSource(), Domain);

        // ACT
        await using var cache = builder
            .AddLayer(DefaultOptions())
            .Build();

        // ASSERT
        Assert.Equal(1, cache.LayerCount);
    }

    [Fact]
    public async Task Build_WithTwoLayers_ReturnsLayeredCacheWithTwoLayers()
    {
        // ARRANGE
        var builder = LayeredWindowCacheBuilder<int, int, IntegerFixedStepDomain>
            .Create(CreateDataSource(), Domain);

        // ACT
        await using var cache = builder
            .AddLayer(new WindowCacheOptions(2.0, 2.0, UserCacheReadMode.CopyOnRead))
            .AddLayer(new WindowCacheOptions(0.5, 0.5, UserCacheReadMode.Snapshot))
            .Build();

        // ASSERT
        Assert.Equal(2, cache.LayerCount);
    }

    [Fact]
    public async Task Build_WithThreeLayers_ReturnsLayeredCacheWithThreeLayers()
    {
        // ARRANGE
        var builder = LayeredWindowCacheBuilder<int, int, IntegerFixedStepDomain>
            .Create(CreateDataSource(), Domain);

        // ACT
        await using var cache = builder
            .AddLayer(new WindowCacheOptions(5.0, 5.0, UserCacheReadMode.CopyOnRead))
            .AddLayer(new WindowCacheOptions(2.0, 2.0, UserCacheReadMode.CopyOnRead))
            .AddLayer(new WindowCacheOptions(0.5, 0.5, UserCacheReadMode.Snapshot))
            .Build();

        // ASSERT
        Assert.Equal(3, cache.LayerCount);
    }

    [Fact]
    public async Task Build_ReturnsLayeredWindowCacheType()
    {
        // ARRANGE & ACT
        await using var cache = LayeredWindowCacheBuilder<int, int, IntegerFixedStepDomain>
            .Create(CreateDataSource(), Domain)
            .AddLayer(DefaultOptions())
            .Build();

        // ASSERT
        Assert.IsType<LayeredWindowCache<int, int, IntegerFixedStepDomain>>(cache);
    }

    [Fact]
    public async Task Build_ReturnedCacheImplementsIWindowCache()
    {
        // ARRANGE & ACT
        await using var cache = LayeredWindowCacheBuilder<int, int, IntegerFixedStepDomain>
            .Create(CreateDataSource(), Domain)
            .AddLayer(DefaultOptions())
            .Build();

        // ASSERT
        Assert.IsAssignableFrom<IWindowCache<int, int, IntegerFixedStepDomain>>(cache);
    }

    [Fact]
    public async Task Build_CanBeCalledMultipleTimes_ReturnsDifferentInstances()
    {
        // ARRANGE
        var builder = LayeredWindowCacheBuilder<int, int, IntegerFixedStepDomain>
            .Create(CreateDataSource(), Domain)
            .AddLayer(DefaultOptions());

        // ACT
        await using var cache1 = builder.Build();
        await using var cache2 = builder.Build();

        // ASSERT — each build creates a new set of independent cache instances
        Assert.NotSame(cache1, cache2);
    }

    #endregion

    #region Layer Wiring Tests

    [Fact]
    public async Task Build_SingleLayer_CanFetchData()
    {
        // ARRANGE
        var options = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot,
            debounceDelay: TimeSpan.FromMilliseconds(50));

        await using var cache = LayeredWindowCacheBuilder<int, int, IntegerFixedStepDomain>
            .Create(CreateDataSource(), Domain)
            .AddLayer(options)
            .Build();

        var range = Intervals.NET.Factories.Range.Closed<int>(1, 10);

        // ACT
        var result = await cache.GetDataAsync(range, CancellationToken.None);

        // ASSERT
        Assert.NotNull(result);
        Assert.True(result.Range.HasValue);
        Assert.Equal(10, result.Data.Length);
    }

    [Fact]
    public async Task Build_TwoLayers_CanFetchData()
    {
        // ARRANGE
        var deepOptions = new WindowCacheOptions(
            leftCacheSize: 2.0,
            rightCacheSize: 2.0,
            readMode: UserCacheReadMode.CopyOnRead,
            debounceDelay: TimeSpan.FromMilliseconds(50));

        var userOptions = new WindowCacheOptions(
            leftCacheSize: 0.5,
            rightCacheSize: 0.5,
            readMode: UserCacheReadMode.Snapshot,
            debounceDelay: TimeSpan.FromMilliseconds(50));

        await using var cache = LayeredWindowCacheBuilder<int, int, IntegerFixedStepDomain>
            .Create(CreateDataSource(), Domain)
            .AddLayer(deepOptions)
            .AddLayer(userOptions)
            .Build();

        var range = Intervals.NET.Factories.Range.Closed<int>(100, 110);

        // ACT
        var result = await cache.GetDataAsync(range, CancellationToken.None);

        // ASSERT
        Assert.NotNull(result);
        Assert.True(result.Range.HasValue);
        Assert.Equal(11, result.Data.Length);
    }

    [Fact]
    public async Task Build_WithPerLayerDiagnostics_DoesNotThrowOnFetch()
    {
        // ARRANGE
        var deepDiagnostics = new EventCounterCacheDiagnostics();
        var userDiagnostics = new EventCounterCacheDiagnostics();

        await using var cache = LayeredWindowCacheBuilder<int, int, IntegerFixedStepDomain>
            .Create(CreateDataSource(), Domain)
            .AddLayer(new WindowCacheOptions(2.0, 2.0, UserCacheReadMode.CopyOnRead,
                debounceDelay: TimeSpan.FromMilliseconds(50)), deepDiagnostics)
            .AddLayer(new WindowCacheOptions(0.5, 0.5, UserCacheReadMode.Snapshot,
                debounceDelay: TimeSpan.FromMilliseconds(50)), userDiagnostics)
            .Build();

        var range = Intervals.NET.Factories.Range.Closed<int>(1, 5);

        // ACT
        var exception = await Record.ExceptionAsync(
            async () => await cache.GetDataAsync(range, CancellationToken.None));

        // ASSERT
        Assert.Null(exception);
    }

    #endregion
}
