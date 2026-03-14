using Intervals.NET.Domain.Abstractions;
using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Caching.Layered;
using Intervals.NET.Caching.SlidingWindow.Public.Cache;
using Intervals.NET.Caching.SlidingWindow.Public.Configuration;
using Intervals.NET.Caching.SlidingWindow.Public.Extensions;
using Intervals.NET.Caching.SlidingWindow.Public.Instrumentation;
using Intervals.NET.Caching.SlidingWindow.Tests.Infrastructure.DataSources;
using Intervals.NET.Caching.SlidingWindow.Tests.Infrastructure.Helpers;

namespace Intervals.NET.Caching.SlidingWindow.Unit.Tests.Public.Cache;

/// <summary>
/// Unit tests for <see cref="LayeredRangeCacheBuilder{TRange,TData,TDomain}"/>.
/// Validates the builder API: construction via <see cref="SlidingWindowCacheBuilder.Layered{TRange,TData,TDomain}"/>,
/// layer addition (pre-built options and inline lambda), build validation, layer ordering,
/// and the resulting <see cref="LayeredRangeCache{TRange,TData,TDomain}"/>.
/// Uses <see cref="SimpleTestDataSource{TData}"/> as a lightweight real data source to avoid
/// mocking the complex <see cref="IDataSource{TRange,TData}"/> interface for these tests.
/// </summary>
public sealed class LayeredSlidingWindowCacheBuilderTests
{
    #region Test Infrastructure

    private static IntegerFixedStepDomain Domain => new();

    private static IDataSource<int, int> CreateDataSource()
        => new SimpleTestDataSource<int>(i => i);

    private static SlidingWindowCacheOptions DefaultOptions(
        UserCacheReadMode mode = UserCacheReadMode.Snapshot)
        => TestHelpers.CreateDefaultOptions(readMode: mode);

    #endregion

    #region SlidingWindowCacheBuilder.Layered() — Null Guard Tests

    [Fact]
    public void Layered_WithNullDataSource_ThrowsArgumentNullException()
    {
        // ACT
        var exception = Record.Exception(() =>
            SlidingWindowCacheBuilder.Layered<int, int, IntegerFixedStepDomain>(null!, Domain));

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
            SlidingWindowCacheBuilder.Layered<int, int, IRangeDomain<int>>(dataSource, null!));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentNullException>(exception);
        Assert.Contains("domain", ((ArgumentNullException)exception).ParamName);
    }

    [Fact]
    public void Layered_WithValidArguments_ReturnsBuilder()
    {
        // ACT
        var builder = SlidingWindowCacheBuilder.Layered(CreateDataSource(), Domain);

        // ASSERT
        Assert.NotNull(builder);
    }

    #endregion

    #region AddSlidingWindowLayer(SlidingWindowCacheOptions) Tests

    [Fact]
    public void AddSlidingWindowLayer_WithNullOptions_ThrowsArgumentNullException()
    {
        // ARRANGE
        var builder = SlidingWindowCacheBuilder.Layered(CreateDataSource(), Domain);

        // ACT
        var exception = Record.Exception(() => builder.AddSlidingWindowLayer((SlidingWindowCacheOptions)null!));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentNullException>(exception);
        Assert.Contains("options", ((ArgumentNullException)exception).ParamName);
    }

    [Fact]
    public void AddSlidingWindowLayer_ReturnsBuilderForFluentChaining()
    {
        // ARRANGE
        var builder = SlidingWindowCacheBuilder.Layered(CreateDataSource(), Domain);

        // ACT
        var returned = builder.AddSlidingWindowLayer(DefaultOptions());

        // ASSERT — same instance for fluent chaining
        Assert.Same(builder, returned);
    }

    [Fact]
    public void AddSlidingWindowLayer_MultipleCallsReturnSameBuilder()
    {
        // ARRANGE
        var builder = SlidingWindowCacheBuilder.Layered(CreateDataSource(), Domain);

        // ACT
        var b1 = builder.AddSlidingWindowLayer(DefaultOptions());
        var b2 = b1.AddSlidingWindowLayer(DefaultOptions());
        var b3 = b2.AddSlidingWindowLayer(DefaultOptions());

        // ASSERT
        Assert.Same(builder, b1);
        Assert.Same(builder, b2);
        Assert.Same(builder, b3);
    }

    [Fact]
    public void AddSlidingWindowLayer_AcceptsDiagnosticsParameter()
    {
        // ARRANGE
        var builder = SlidingWindowCacheBuilder.Layered(CreateDataSource(), Domain);
        var diagnostics = new EventCounterCacheDiagnostics();

        // ACT
        var exception = Record.Exception(() =>
            builder.AddSlidingWindowLayer(DefaultOptions(), diagnostics));

        // ASSERT
        Assert.Null(exception);
    }

    [Fact]
    public void AddSlidingWindowLayer_WithNullDiagnostics_DoesNotThrow()
    {
        // ARRANGE
        var builder = SlidingWindowCacheBuilder.Layered(CreateDataSource(), Domain);

        // ACT
        var exception = Record.Exception(() =>
            builder.AddSlidingWindowLayer(DefaultOptions(), null));

        // ASSERT
        Assert.Null(exception);
    }

    #endregion

    #region AddSlidingWindowLayer(Action<SlidingWindowCacheOptionsBuilder>) Tests

    [Fact]
    public void AddSlidingWindowLayer_WithNullDelegate_ThrowsArgumentNullException()
    {
        // ARRANGE
        var builder = SlidingWindowCacheBuilder.Layered(CreateDataSource(), Domain);

        // ACT
        var exception = Record.Exception(() =>
            builder.AddSlidingWindowLayer((Action<SlidingWindowCacheOptionsBuilder>)null!));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentNullException>(exception);
        Assert.Contains("configure", ((ArgumentNullException)exception).ParamName);
    }

    [Fact]
    public void AddSlidingWindowLayer_WithInlineDelegate_ReturnsBuilderForFluentChaining()
    {
        // ARRANGE
        var builder = SlidingWindowCacheBuilder.Layered(CreateDataSource(), Domain);

        // ACT
        var returned = builder.AddSlidingWindowLayer(o => o.WithCacheSize(1.0));

        // ASSERT
        Assert.Same(builder, returned);
    }

    [Fact]
    public void AddSlidingWindowLayer_WithInlineDelegateAndDiagnostics_DoesNotThrow()
    {
        // ARRANGE
        var builder = SlidingWindowCacheBuilder.Layered(CreateDataSource(), Domain);
        var diagnostics = new EventCounterCacheDiagnostics();

        // ACT
        var exception = Record.Exception(() =>
            builder.AddSlidingWindowLayer(o => o.WithCacheSize(1.0), diagnostics));

        // ASSERT
        Assert.Null(exception);
    }

    [Fact]
    public async Task AddSlidingWindowLayer_WithInlineDelegateMissingCacheSize_ThrowsInvalidOperationException()
    {
        // ARRANGE — delegate does not call WithCacheSize; Build() on the inner builder throws
        var builder = SlidingWindowCacheBuilder.Layered(CreateDataSource(), Domain)
            .AddSlidingWindowLayer(o => o.WithReadMode(UserCacheReadMode.Snapshot));

        // ACT — BuildAsync() on the LayeredRangeCacheBuilder triggers the options Build(), which throws
        var exception = await Record.ExceptionAsync(async () => await builder.BuildAsync());

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<InvalidOperationException>(exception);
    }

    [Fact]
    public async Task AddSlidingWindowLayer_InlineTwoLayers_CanFetchData()
    {
        // ARRANGE
        await using var cache = await SlidingWindowCacheBuilder.Layered(CreateDataSource(), Domain)
            .AddSlidingWindowLayer(o => o
                .WithCacheSize(2.0)
                .WithReadMode(UserCacheReadMode.CopyOnRead)
                .WithDebounceDelay(TimeSpan.FromMilliseconds(50)))
            .AddSlidingWindowLayer(o => o
                .WithCacheSize(0.5)
                .WithDebounceDelay(TimeSpan.FromMilliseconds(50)))
            .BuildAsync();

        var range = Factories.Range.Closed<int>(1, 10);

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
    public async Task Build_WithNoLayers_ThrowsInvalidOperationException()
    {
        // ARRANGE
        var builder = SlidingWindowCacheBuilder.Layered(CreateDataSource(), Domain);

        // ACT
        var exception = await Record.ExceptionAsync(async () => await builder.BuildAsync());

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<InvalidOperationException>(exception);
    }

    [Fact]
    public async Task Build_WithSingleLayer_ReturnsLayeredCacheWithOneLayer()
    {
        // ARRANGE
        var builder = SlidingWindowCacheBuilder.Layered(CreateDataSource(), Domain);

        // ACT
        await using var layered = (LayeredRangeCache<int, int, IntegerFixedStepDomain>)await builder
            .AddSlidingWindowLayer(DefaultOptions())
            .BuildAsync();

        // ASSERT
        Assert.Equal(1, layered.LayerCount);
    }

    [Fact]
    public async Task Build_WithTwoLayers_ReturnsLayeredCacheWithTwoLayers()
    {
        // ARRANGE
        var builder = SlidingWindowCacheBuilder.Layered(CreateDataSource(), Domain);

        // ACT
        await using var layered = (LayeredRangeCache<int, int, IntegerFixedStepDomain>)await builder
            .AddSlidingWindowLayer(new SlidingWindowCacheOptions(2.0, 2.0, UserCacheReadMode.CopyOnRead))
            .AddSlidingWindowLayer(new SlidingWindowCacheOptions(0.5, 0.5, UserCacheReadMode.Snapshot))
            .BuildAsync();

        // ASSERT
        Assert.Equal(2, layered.LayerCount);
    }

    [Fact]
    public async Task Build_WithThreeLayers_ReturnsLayeredCacheWithThreeLayers()
    {
        // ARRANGE
        var builder = SlidingWindowCacheBuilder.Layered(CreateDataSource(), Domain);

        // ACT
        await using var layered = (LayeredRangeCache<int, int, IntegerFixedStepDomain>)await builder
            .AddSlidingWindowLayer(new SlidingWindowCacheOptions(5.0, 5.0, UserCacheReadMode.CopyOnRead))
            .AddSlidingWindowLayer(new SlidingWindowCacheOptions(2.0, 2.0, UserCacheReadMode.CopyOnRead))
            .AddSlidingWindowLayer(new SlidingWindowCacheOptions(0.5, 0.5, UserCacheReadMode.Snapshot))
            .BuildAsync();

        // ASSERT
        Assert.Equal(3, layered.LayerCount);
    }

    [Fact]
    public async Task Build_ReturnsIRangeCacheImplementedByLayeredRangeCacheType()
    {
        // ARRANGE & ACT
        await using var cache = await SlidingWindowCacheBuilder.Layered(CreateDataSource(), Domain)
            .AddSlidingWindowLayer(DefaultOptions())
            .BuildAsync();

        // ASSERT — Build() returns IRangeCache<>; concrete type is LayeredRangeCache<>
        Assert.IsAssignableFrom<IRangeCache<int, int, IntegerFixedStepDomain>>(cache);
        Assert.IsType<LayeredRangeCache<int, int, IntegerFixedStepDomain>>(cache);
    }

    [Fact]
    public async Task Build_ReturnedCacheImplementsIRangeCache()
    {
        // ARRANGE & ACT
        await using var cache = await SlidingWindowCacheBuilder.Layered(CreateDataSource(), Domain)
            .AddSlidingWindowLayer(DefaultOptions())
            .BuildAsync();

        // ASSERT
        Assert.IsAssignableFrom<IRangeCache<int, int, IntegerFixedStepDomain>>(cache);
    }

    [Fact]
    public async Task Build_CannotBeCalledTwice_ThrowsInvalidOperationException()
    {
        // ARRANGE
        var builder = SlidingWindowCacheBuilder.Layered(CreateDataSource(), Domain)
            .AddSlidingWindowLayer(DefaultOptions());

        await using var cache1 = await builder.BuildAsync();

        // ACT — second call on the same builder instance must be rejected
        var exception = await Record.ExceptionAsync(async () => await builder.BuildAsync());

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<InvalidOperationException>(exception);
    }

    #endregion

    #region Layer Wiring Tests

    [Fact]
    public async Task Build_SingleLayer_CanFetchData()
    {
        // ARRANGE
        var options = new SlidingWindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot,
            debounceDelay: TimeSpan.FromMilliseconds(50));

        await using var cache = await SlidingWindowCacheBuilder.Layered(CreateDataSource(), Domain)
            .AddSlidingWindowLayer(options)
            .BuildAsync();

        var range = Factories.Range.Closed<int>(1, 10);

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
        var deepOptions = new SlidingWindowCacheOptions(
            leftCacheSize: 2.0,
            rightCacheSize: 2.0,
            readMode: UserCacheReadMode.CopyOnRead,
            debounceDelay: TimeSpan.FromMilliseconds(50));

        var userOptions = new SlidingWindowCacheOptions(
            leftCacheSize: 0.5,
            rightCacheSize: 0.5,
            readMode: UserCacheReadMode.Snapshot,
            debounceDelay: TimeSpan.FromMilliseconds(50));

        await using var cache = await SlidingWindowCacheBuilder.Layered(CreateDataSource(), Domain)
            .AddSlidingWindowLayer(deepOptions)
            .AddSlidingWindowLayer(userOptions)
            .BuildAsync();

        var range = Factories.Range.Closed<int>(100, 110);

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

        await using var cache = await SlidingWindowCacheBuilder.Layered(CreateDataSource(), Domain)
            .AddSlidingWindowLayer(new SlidingWindowCacheOptions(2.0, 2.0, UserCacheReadMode.CopyOnRead,
                debounceDelay: TimeSpan.FromMilliseconds(50)), deepDiagnostics)
            .AddSlidingWindowLayer(new SlidingWindowCacheOptions(0.5, 0.5, UserCacheReadMode.Snapshot,
                debounceDelay: TimeSpan.FromMilliseconds(50)), userDiagnostics)
            .BuildAsync();

        var range = Factories.Range.Closed<int>(1, 5);

        // ACT
        var exception = await Record.ExceptionAsync(
            async () => await cache.GetDataAsync(range, CancellationToken.None));

        // ASSERT
        Assert.Null(exception);
    }

    #endregion
}
