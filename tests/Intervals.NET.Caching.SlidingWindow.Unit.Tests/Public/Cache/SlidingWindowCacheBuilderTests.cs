using Intervals.NET.Domain.Abstractions;
using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Caching.Layered;
using Intervals.NET.Caching.SlidingWindow.Public;
using Intervals.NET.Caching.SlidingWindow.Public.Cache;
using Intervals.NET.Caching.SlidingWindow.Public.Configuration;
using Intervals.NET.Caching.SlidingWindow.Public.Instrumentation;
using Intervals.NET.Caching.SlidingWindow.Tests.Infrastructure.DataSources;
using Intervals.NET.Caching.SlidingWindow.Tests.Infrastructure.Helpers;

namespace Intervals.NET.Caching.SlidingWindow.Unit.Tests.Public.Cache;

/// <summary>
/// Unit tests for <see cref="SlidingWindowCacheBuilder"/> (static entry point) and
/// <see cref="SlidingWindowCacheBuilder{TRange,TData,TDomain}"/> (single-cache builder).
/// Validates construction, null-guard enforcement, options configuration (pre-built and inline),
/// diagnostics wiring, and the resulting <see cref="ISlidingWindowCache{TRange,TData,TDomain}"/>.
/// Uses <see cref="SimpleTestDataSource{TData}"/> to avoid mocking the complex
/// <see cref="IDataSource{TRange,TData}"/> interface for these tests.
/// </summary>
public sealed class SlidingWindowCacheBuilderTests
{
    #region Test Infrastructure

    private static IntegerFixedStepDomain Domain => new();

    private static IDataSource<int, int> CreateDataSource()
        => new SimpleTestDataSource<int>(i => i);

    private static SlidingWindowCacheOptions DefaultOptions(
        UserCacheReadMode mode = UserCacheReadMode.Snapshot)
        => TestHelpers.CreateDefaultOptions(readMode: mode);

    #endregion

    #region SlidingWindowCacheBuilder.For() — Null Guard Tests

    [Fact]
    public void For_WithNullDataSource_ThrowsArgumentNullException()
    {
        // ACT
        var exception = Record.Exception(() =>
            SlidingWindowCacheBuilder.For<int, int, IntegerFixedStepDomain>(null!, Domain));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentNullException>(exception);
        Assert.Contains("dataSource", ((ArgumentNullException)exception).ParamName);
    }

    [Fact]
    public void For_WithNullDomain_ThrowsArgumentNullException()
    {
        // ARRANGE — use a reference-type TDomain to allow null
        var dataSource = CreateDataSource();

        // ACT
        var exception = Record.Exception(() =>
            SlidingWindowCacheBuilder.For<int, int, IRangeDomain<int>>(dataSource, null!));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentNullException>(exception);
        Assert.Contains("domain", ((ArgumentNullException)exception).ParamName);
    }

    [Fact]
    public void For_WithValidArguments_ReturnsBuilder()
    {
        // ACT
        var builder = SlidingWindowCacheBuilder.For(CreateDataSource(), Domain);

        // ASSERT
        Assert.NotNull(builder);
    }

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
        // ARRANGE — use a reference-type TDomain to allow null
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
    public void Layered_WithValidArguments_ReturnsLayeredBuilder()
    {
        // ACT
        var builder = SlidingWindowCacheBuilder.Layered(CreateDataSource(), Domain);

        // ASSERT
        Assert.NotNull(builder);
        Assert.IsType<LayeredRangeCacheBuilder<int, int, IntegerFixedStepDomain>>(builder);
    }

    #endregion

    #region WithOptions(SlidingWindowCacheOptions) Tests

    [Fact]
    public void WithOptions_WithNullOptions_ThrowsArgumentNullException()
    {
        // ARRANGE
        var builder = SlidingWindowCacheBuilder.For(CreateDataSource(), Domain);

        // ACT
        var exception = Record.Exception(() => builder.WithOptions((SlidingWindowCacheOptions)null!));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentNullException>(exception);
        Assert.Contains("options", ((ArgumentNullException)exception).ParamName);
    }

    [Fact]
    public void WithOptions_WithValidOptions_ReturnsBuilderForFluentChaining()
    {
        // ARRANGE
        var builder = SlidingWindowCacheBuilder.For(CreateDataSource(), Domain);

        // ACT
        var returned = builder.WithOptions(DefaultOptions());

        // ASSERT — same instance for fluent chaining
        Assert.Same(builder, returned);
    }

    #endregion

    #region WithOptions(Action<SlidingWindowCacheOptionsBuilder>) Tests

    [Fact]
    public void WithOptions_WithNullDelegate_ThrowsArgumentNullException()
    {
        // ARRANGE
        var builder = SlidingWindowCacheBuilder.For(CreateDataSource(), Domain);

        // ACT
        var exception = Record.Exception(() =>
            builder.WithOptions((Action<SlidingWindowCacheOptionsBuilder>)null!));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentNullException>(exception);
        Assert.Contains("configure", ((ArgumentNullException)exception).ParamName);
    }

    [Fact]
    public void WithOptions_WithInlineDelegate_ReturnsBuilderForFluentChaining()
    {
        // ARRANGE
        var builder = SlidingWindowCacheBuilder.For(CreateDataSource(), Domain);

        // ACT
        var returned = builder.WithOptions(o => o.WithCacheSize(1.0));

        // ASSERT
        Assert.Same(builder, returned);
    }

    [Fact]
    public void WithOptions_WithInlineDelegateMissingCacheSize_ThrowsInvalidOperationException()
    {
        // ARRANGE — configure delegate that does not set cache size
        var builder = SlidingWindowCacheBuilder.For(CreateDataSource(), Domain)
            .WithOptions(o => o.WithReadMode(UserCacheReadMode.Snapshot));

        // ACT — Build() internally calls delegate's Build(), which throws
        var exception = Record.Exception(() => builder.Build());

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<InvalidOperationException>(exception);
    }

    #endregion

    #region WithDiagnostics Tests

    [Fact]
    public void WithDiagnostics_WithNullDiagnostics_ThrowsArgumentNullException()
    {
        // ARRANGE
        var builder = SlidingWindowCacheBuilder.For(CreateDataSource(), Domain);

        // ACT
        var exception = Record.Exception(() => builder.WithDiagnostics(null!));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentNullException>(exception);
        Assert.Contains("diagnostics", ((ArgumentNullException)exception).ParamName);
    }

    [Fact]
    public void WithDiagnostics_WithValidDiagnostics_ReturnsBuilderForFluentChaining()
    {
        // ARRANGE
        var builder = SlidingWindowCacheBuilder.For(CreateDataSource(), Domain);
        var diagnostics = new EventCounterCacheDiagnostics();

        // ACT
        var returned = builder.WithDiagnostics(diagnostics);

        // ASSERT
        Assert.Same(builder, returned);
    }

    [Fact]
    public void WithDiagnostics_WithoutCallingIt_DoesNotThrowOnBuild()
    {
        // ARRANGE — diagnostics is optional; NoOpDiagnostics.Instance should be used
        var builder = SlidingWindowCacheBuilder.For(CreateDataSource(), Domain)
            .WithOptions(DefaultOptions());

        // ACT
        var exception = Record.Exception(() => builder.Build());

        // ASSERT
        Assert.Null(exception);
    }

    #endregion

    #region Build() Tests

    [Fact]
    public void Build_WithoutOptions_ThrowsInvalidOperationException()
    {
        // ARRANGE
        var builder = SlidingWindowCacheBuilder.For(CreateDataSource(), Domain);

        // ACT
        var exception = Record.Exception(() => builder.Build());

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<InvalidOperationException>(exception);
    }

    [Fact]
    public void Build_WithPreBuiltOptions_ReturnsNonNull()
    {
        // ARRANGE & ACT
        var cache = SlidingWindowCacheBuilder.For(CreateDataSource(), Domain)
            .WithOptions(DefaultOptions())
            .Build();

        // ASSERT
        Assert.NotNull(cache);
    }

    [Fact]
    public void Build_WithInlineOptions_ReturnsNonNull()
    {
        // ARRANGE & ACT
        var cache = SlidingWindowCacheBuilder.For(CreateDataSource(), Domain)
            .WithOptions(o => o.WithCacheSize(1.0))
            .Build();

        // ASSERT
        Assert.NotNull(cache);
    }

    [Fact]
    public async Task Build_ReturnsWindowCacheType()
    {
        // ARRANGE & ACT
        await using var cache = SlidingWindowCacheBuilder.For(CreateDataSource(), Domain)
            .WithOptions(DefaultOptions())
            .Build();

        // ASSERT
        Assert.IsType<SlidingWindowCache<int, int, IntegerFixedStepDomain>>(cache);
    }

    [Fact]
    public async Task Build_ReturnedCacheImplementsIWindowCache()
    {
        // ARRANGE & ACT
        await using var cache = SlidingWindowCacheBuilder.For(CreateDataSource(), Domain)
            .WithOptions(DefaultOptions())
            .Build();

        // ASSERT
        Assert.IsAssignableFrom<ISlidingWindowCache<int, int, IntegerFixedStepDomain>>(cache);
    }

    [Fact]
    public async Task Build_CalledTwice_ThrowsInvalidOperationException()
    {
        // ARRANGE
        var builder = SlidingWindowCacheBuilder.For(CreateDataSource(), Domain)
            .WithOptions(DefaultOptions());

        await using var cache1 = builder.Build(); // first call succeeds

        // ACT — second call should throw
        var exception = Record.Exception(() => builder.Build());

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<InvalidOperationException>(exception);
    }

    #endregion

    #region End-to-End Tests

    [Fact]
    public async Task Build_WithPreBuiltOptions_CanFetchData()
    {
        // ARRANGE
        var options = new SlidingWindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot,
            debounceDelay: TimeSpan.FromMilliseconds(50));

        await using var cache = SlidingWindowCacheBuilder.For(CreateDataSource(), Domain)
            .WithOptions(options)
            .Build();

        var range = Factories.Range.Closed<int>(1, 10);

        // ACT
        var result = await cache.GetDataAsync(range, CancellationToken.None);

        // ASSERT
        Assert.NotNull(result);
        Assert.True(result.Range.HasValue);
        Assert.Equal(10, result.Data.Length);
    }

    [Fact]
    public async Task Build_WithInlineOptions_CanFetchData()
    {
        // ARRANGE
        await using var cache = SlidingWindowCacheBuilder.For(CreateDataSource(), Domain)
            .WithOptions(o => o
                .WithCacheSize(1.0)
                .WithReadMode(UserCacheReadMode.Snapshot)
                .WithDebounceDelay(TimeSpan.FromMilliseconds(50)))
            .Build();

        var range = Factories.Range.Closed<int>(50, 60);

        // ACT
        var result = await cache.GetDataAsync(range, CancellationToken.None);

        // ASSERT
        Assert.NotNull(result);
        Assert.True(result.Range.HasValue);
        Assert.Equal(11, result.Data.Length);
    }

    [Fact]
    public async Task Build_WithDiagnostics_DiagnosticsReceiveEvents()
    {
        // ARRANGE
        var diagnostics = new EventCounterCacheDiagnostics();

        await using var cache = SlidingWindowCacheBuilder.For(CreateDataSource(), Domain)
            .WithOptions(DefaultOptions())
            .WithDiagnostics(diagnostics)
            .Build();

        var range = Factories.Range.Closed<int>(1, 10);

        // ACT
        await cache.GetDataAsync(range, CancellationToken.None);
        await cache.WaitForIdleAsync();

        // ASSERT — at least one rebalance intent must have been published
        Assert.True(diagnostics.RebalanceIntentPublished >= 1,
            "Diagnostics should have received at least one rebalance intent event.");
    }

    #endregion
}
