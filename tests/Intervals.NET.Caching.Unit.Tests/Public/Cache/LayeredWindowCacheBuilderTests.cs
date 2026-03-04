using Intervals.NET.Domain.Abstractions;
using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Caching.Public;
using Intervals.NET.Caching.Public.Cache;
using Intervals.NET.Caching.Public.Configuration;
using Intervals.NET.Caching.Public.Instrumentation;
using Intervals.NET.Caching.Tests.Infrastructure.DataSources;
using Intervals.NET.Caching.Tests.Infrastructure.Helpers;

namespace Intervals.NET.Caching.Unit.Tests.Public.Cache;

/// <summary>
/// Unit tests for <see cref="LayeredWindowCacheBuilder{TRange,TData,TDomain}"/>.
/// Validates the builder API: construction via <see cref="WindowCacheBuilder.Layered{TRange,TData,TDomain}"/>,
/// layer addition (pre-built options and inline lambda), build validation, layer ordering,
/// and the resulting <see cref="LayeredWindowCache{TRange,TData,TDomain}"/>.
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

    #region WindowCacheBuilder.Layered() — Null Guard Tests

    [Fact]
    public void Layered_WithNullDataSource_ThrowsArgumentNullException()
    {
        // ACT
        var exception = Record.Exception(() =>
            WindowCacheBuilder.Layered<int, int, IntegerFixedStepDomain>(null!, Domain));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentNullException>(exception);
        Assert.Contains("dataSource", ((ArgumentNullException)exception).ParamName);
    }

    [Fact]
    public void Layered_WithNullDomain_ThrowsArgumentNullException()
    {
        // ARRANGE — TDomain must be a reference type to accept null;
        // use IRangeDomain<int> as the type parameter (interface = reference type)
        var dataSource = CreateDataSource();

        // ACT
        var exception = Record.Exception(() =>
            WindowCacheBuilder.Layered<int, int, IRangeDomain<int>>(dataSource, null!));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentNullException>(exception);
        Assert.Contains("domain", ((ArgumentNullException)exception).ParamName);
    }

    [Fact]
    public void Layered_WithValidArguments_ReturnsBuilder()
    {
        // ACT
        var builder = WindowCacheBuilder.Layered(CreateDataSource(), Domain);

        // ASSERT
        Assert.NotNull(builder);
    }

    #endregion

    #region AddLayer(WindowCacheOptions) Tests

    [Fact]
    public void AddLayer_WithNullOptions_ThrowsArgumentNullException()
    {
        // ARRANGE
        var builder = WindowCacheBuilder.Layered(CreateDataSource(), Domain);

        // ACT
        var exception = Record.Exception(() => builder.AddLayer((WindowCacheOptions)null!));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentNullException>(exception);
        Assert.Contains("options", ((ArgumentNullException)exception).ParamName);
    }

    [Fact]
    public void AddLayer_ReturnsBuilderForFluentChaining()
    {
        // ARRANGE
        var builder = WindowCacheBuilder.Layered(CreateDataSource(), Domain);

        // ACT
        var returned = builder.AddLayer(DefaultOptions());

        // ASSERT — same instance for fluent chaining
        Assert.Same(builder, returned);
    }

    [Fact]
    public void AddLayer_MultipleCallsReturnSameBuilder()
    {
        // ARRANGE
        var builder = WindowCacheBuilder.Layered(CreateDataSource(), Domain);

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
        var builder = WindowCacheBuilder.Layered(CreateDataSource(), Domain);
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
        var builder = WindowCacheBuilder.Layered(CreateDataSource(), Domain);

        // ACT
        var exception = Record.Exception(() =>
            builder.AddLayer(DefaultOptions(), null));

        // ASSERT
        Assert.Null(exception);
    }

    #endregion

    #region AddLayer(Action<WindowCacheOptionsBuilder>) Tests

    [Fact]
    public void AddLayer_WithNullDelegate_ThrowsArgumentNullException()
    {
        // ARRANGE
        var builder = WindowCacheBuilder.Layered(CreateDataSource(), Domain);

        // ACT
        var exception = Record.Exception(() =>
            builder.AddLayer((Action<WindowCacheOptionsBuilder>)null!));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentNullException>(exception);
        Assert.Contains("configure", ((ArgumentNullException)exception).ParamName);
    }

    [Fact]
    public void AddLayer_WithInlineDelegate_ReturnsBuilderForFluentChaining()
    {
        // ARRANGE
        var builder = WindowCacheBuilder.Layered(CreateDataSource(), Domain);

        // ACT
        var returned = builder.AddLayer(o => o.WithCacheSize(1.0));

        // ASSERT
        Assert.Same(builder, returned);
    }

    [Fact]
    public void AddLayer_WithInlineDelegateAndDiagnostics_DoesNotThrow()
    {
        // ARRANGE
        var builder = WindowCacheBuilder.Layered(CreateDataSource(), Domain);
        var diagnostics = new EventCounterCacheDiagnostics();

        // ACT
        var exception = Record.Exception(() =>
            builder.AddLayer(o => o.WithCacheSize(1.0), diagnostics));

        // ASSERT
        Assert.Null(exception);
    }

    [Fact]
    public void AddLayer_WithInlineDelegateMissingCacheSize_ThrowsInvalidOperationException()
    {
        // ARRANGE — delegate does not call WithCacheSize; Build() on the inner builder throws
        var builder = WindowCacheBuilder.Layered(CreateDataSource(), Domain)
            .AddLayer(o => o.WithReadMode(UserCacheReadMode.Snapshot));

        // ACT — Build() on the LayeredWindowCacheBuilder triggers the options Build(), which throws
        var exception = Record.Exception(() => builder.Build());

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<InvalidOperationException>(exception);
    }

    [Fact]
    public async Task AddLayer_InlineTwoLayers_CanFetchData()
    {
        // ARRANGE
        await using var cache = WindowCacheBuilder.Layered(CreateDataSource(), Domain)
            .AddLayer(o => o
                .WithCacheSize(2.0)
                .WithReadMode(UserCacheReadMode.CopyOnRead)
                .WithDebounceDelay(TimeSpan.FromMilliseconds(50)))
            .AddLayer(o => o
                .WithCacheSize(0.5)
                .WithDebounceDelay(TimeSpan.FromMilliseconds(50)))
            .Build();

        var range = Intervals.NET.Factories.Range.Closed<int>(1, 10);

        // ACT
        var result = await cache.GetDataAsync(range, CancellationToken.None);

        // ASSERT
        Assert.NotNull(result);
        Assert.True(result.Range.HasValue);
        Assert.Equal(10, result.Data.Length);
    }

    #endregion

    #region Build() Tests

    [Fact]
    public void Build_WithNoLayers_ThrowsInvalidOperationException()
    {
        // ARRANGE
        var builder = WindowCacheBuilder.Layered(CreateDataSource(), Domain);

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
        var builder = WindowCacheBuilder.Layered(CreateDataSource(), Domain);

        // ACT
        await using var layered = (LayeredWindowCache<int, int, IntegerFixedStepDomain>)builder
            .AddLayer(DefaultOptions())
            .Build();

        // ASSERT
        Assert.Equal(1, layered.LayerCount);
    }

    [Fact]
    public async Task Build_WithTwoLayers_ReturnsLayeredCacheWithTwoLayers()
    {
        // ARRANGE
        var builder = WindowCacheBuilder.Layered(CreateDataSource(), Domain);

        // ACT
        await using var layered = (LayeredWindowCache<int, int, IntegerFixedStepDomain>)builder
            .AddLayer(new WindowCacheOptions(2.0, 2.0, UserCacheReadMode.CopyOnRead))
            .AddLayer(new WindowCacheOptions(0.5, 0.5, UserCacheReadMode.Snapshot))
            .Build();

        // ASSERT
        Assert.Equal(2, layered.LayerCount);
    }

    [Fact]
    public async Task Build_WithThreeLayers_ReturnsLayeredCacheWithThreeLayers()
    {
        // ARRANGE
        var builder = WindowCacheBuilder.Layered(CreateDataSource(), Domain);

        // ACT
        await using var layered = (LayeredWindowCache<int, int, IntegerFixedStepDomain>)builder
            .AddLayer(new WindowCacheOptions(5.0, 5.0, UserCacheReadMode.CopyOnRead))
            .AddLayer(new WindowCacheOptions(2.0, 2.0, UserCacheReadMode.CopyOnRead))
            .AddLayer(new WindowCacheOptions(0.5, 0.5, UserCacheReadMode.Snapshot))
            .Build();

        // ASSERT
        Assert.Equal(3, layered.LayerCount);
    }

    [Fact]
    public async Task Build_ReturnsIWindowCacheImplementedByLayeredWindowCacheType()
    {
        // ARRANGE & ACT
        await using var cache = WindowCacheBuilder.Layered(CreateDataSource(), Domain)
            .AddLayer(DefaultOptions())
            .Build();

        // ASSERT — Build() returns IWindowCache<>; concrete type is LayeredWindowCache<>
        Assert.IsAssignableFrom<IWindowCache<int, int, IntegerFixedStepDomain>>(cache);
        Assert.IsType<LayeredWindowCache<int, int, IntegerFixedStepDomain>>(cache);
    }

    [Fact]
    public async Task Build_ReturnedCacheImplementsIWindowCache()
    {
        // ARRANGE & ACT
        await using var cache = WindowCacheBuilder.Layered(CreateDataSource(), Domain)
            .AddLayer(DefaultOptions())
            .Build();

        // ASSERT
        Assert.IsAssignableFrom<IWindowCache<int, int, IntegerFixedStepDomain>>(cache);
    }

    [Fact]
    public async Task Build_CanBeCalledMultipleTimes_ReturnsDifferentInstances()
    {
        // ARRANGE
        var builder = WindowCacheBuilder.Layered(CreateDataSource(), Domain)
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

        await using var cache = WindowCacheBuilder.Layered(CreateDataSource(), Domain)
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

        await using var cache = WindowCacheBuilder.Layered(CreateDataSource(), Domain)
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

        await using var cache = WindowCacheBuilder.Layered(CreateDataSource(), Domain)
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
