using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Caching.Layered;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Policies;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Selectors;
using Intervals.NET.Caching.VisitedPlaces.Public.Cache;
using Intervals.NET.Caching.VisitedPlaces.Public.Configuration;
using Intervals.NET.Caching.VisitedPlaces.Public.Extensions;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure.DataSources;

namespace Intervals.NET.Caching.VisitedPlaces.Unit.Tests.Public.Extensions;

/// <summary>
/// Unit tests for <see cref="VisitedPlacesLayerExtensions"/> — all four overloads of
/// <c>AddVisitedPlacesLayer</c>. Validates null-guard enforcement and that layers are added to the stack.
/// </summary>
public sealed class VisitedPlacesLayerExtensionsTests
{
    #region Test Infrastructure

    private static IntegerFixedStepDomain Domain => new();

    private static IDataSource<int, int> CreateDataSource() => new SimpleTestDataSource();

    private static LayeredRangeCacheBuilder<int, int, IntegerFixedStepDomain> CreateLayeredBuilder() =>
        VisitedPlacesCacheBuilder.Layered(CreateDataSource(), Domain);

    private static IReadOnlyList<IEvictionPolicy<int, int>> DefaultPolicies() =>
        [new MaxSegmentCountPolicy<int, int>(100)];

    private static IEvictionSelector<int, int> DefaultSelector() => new LruEvictionSelector<int, int>();

    private static void ConfigureEviction(EvictionConfigBuilder<int, int> b) =>
        b.AddPolicy(new MaxSegmentCountPolicy<int, int>(100))
         .WithSelector(new LruEvictionSelector<int, int>());

    #endregion

    #region Overload 1: policies + selector + options (pre-built) Tests

    [Fact]
    public void AddVisitedPlacesLayer_Overload1_WithValidArguments_ReturnsSameBuilder()
    {
        // ARRANGE
        var builder = CreateLayeredBuilder();

        // ACT
        var returned = builder.AddVisitedPlacesLayer(DefaultPolicies(), DefaultSelector());

        // ASSERT
        Assert.Same(builder, returned);
    }

    [Fact]
    public void AddVisitedPlacesLayer_Overload1_WithNullPolicies_ThrowsArgumentNullException()
    {
        // ARRANGE
        var builder = CreateLayeredBuilder();

        // ACT
        var exception = Record.Exception(
            () => builder.AddVisitedPlacesLayer(
                (IReadOnlyList<IEvictionPolicy<int, int>>)null!,
                DefaultSelector()));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentNullException>(exception);
    }

    [Fact]
    public void AddVisitedPlacesLayer_Overload1_WithEmptyPolicies_ThrowsArgumentException()
    {
        // ARRANGE
        var builder = CreateLayeredBuilder();

        // ACT
        var exception = Record.Exception(
            () => builder.AddVisitedPlacesLayer(
                Array.Empty<IEvictionPolicy<int, int>>(),
                DefaultSelector()));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentException>(exception);
    }

    [Fact]
    public void AddVisitedPlacesLayer_Overload1_WithNullSelector_ThrowsArgumentNullException()
    {
        // ARRANGE
        var builder = CreateLayeredBuilder();

        // ACT
        var exception = Record.Exception(
            () => builder.AddVisitedPlacesLayer(DefaultPolicies(), (IEvictionSelector<int, int>)null!));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentNullException>(exception);
    }

    [Fact]
    public void AddVisitedPlacesLayer_Overload1_WithNullOptions_UsesDefaults()
    {
        // ARRANGE
        var builder = CreateLayeredBuilder();

        // ACT — null options should use defaults (no exception)
        var exception = Record.Exception(
            () => builder.AddVisitedPlacesLayer(DefaultPolicies(), DefaultSelector(), options: null));

        // ASSERT
        Assert.Null(exception);
    }

    #endregion

    #region Overload 2: policies + selector + configure (inline options) Tests

    [Fact]
    public void AddVisitedPlacesLayer_Overload2_WithValidArguments_ReturnsSameBuilder()
    {
        // ARRANGE
        var builder = CreateLayeredBuilder();

        // ACT
        var returned = builder.AddVisitedPlacesLayer(
            DefaultPolicies(),
            DefaultSelector(),
            b => b.WithEventChannelCapacity(64));

        // ASSERT
        Assert.Same(builder, returned);
    }

    [Fact]
    public void AddVisitedPlacesLayer_Overload2_WithNullPolicies_ThrowsArgumentNullException()
    {
        // ARRANGE
        var builder = CreateLayeredBuilder();

        // ACT
        var exception = Record.Exception(
            () => builder.AddVisitedPlacesLayer(
                (IReadOnlyList<IEvictionPolicy<int, int>>)null!,
                DefaultSelector(),
                b => b.WithEventChannelCapacity(64)));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentNullException>(exception);
    }

    [Fact]
    public void AddVisitedPlacesLayer_Overload2_WithNullSelector_ThrowsArgumentNullException()
    {
        // ARRANGE
        var builder = CreateLayeredBuilder();

        // ACT
        var exception = Record.Exception(
            () => builder.AddVisitedPlacesLayer(
                DefaultPolicies(),
                (IEvictionSelector<int, int>)null!,
                b => b.WithEventChannelCapacity(64)));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentNullException>(exception);
    }

    [Fact]
    public void AddVisitedPlacesLayer_Overload2_WithNullConfigure_ThrowsArgumentNullException()
    {
        // ARRANGE
        var builder = CreateLayeredBuilder();

        // ACT
        var exception = Record.Exception(
            () => builder.AddVisitedPlacesLayer(
                DefaultPolicies(),
                DefaultSelector(),
                (Action<VisitedPlacesCacheOptionsBuilder<int, int>>)null!));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentNullException>(exception);
    }

    #endregion

    #region Overload 3: configureEviction + options (pre-built) Tests

    [Fact]
    public void AddVisitedPlacesLayer_Overload3_WithValidArguments_ReturnsSameBuilder()
    {
        // ARRANGE
        var builder = CreateLayeredBuilder();

        // ACT
        var returned = builder.AddVisitedPlacesLayer(ConfigureEviction);

        // ASSERT
        Assert.Same(builder, returned);
    }

    [Fact]
    public void AddVisitedPlacesLayer_Overload3_WithNullConfigureEviction_ThrowsArgumentNullException()
    {
        // ARRANGE
        var builder = CreateLayeredBuilder();

        // ACT
        var exception = Record.Exception(
            () => builder.AddVisitedPlacesLayer(
                (Action<EvictionConfigBuilder<int, int>>)null!));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentNullException>(exception);
    }

    [Fact]
    public void AddVisitedPlacesLayer_Overload3_WithNullOptions_UsesDefaults()
    {
        // ARRANGE
        var builder = CreateLayeredBuilder();

        // ACT — null options should not throw
        var exception = Record.Exception(
            () => builder.AddVisitedPlacesLayer(ConfigureEviction, options: null));

        // ASSERT
        Assert.Null(exception);
    }

    [Fact]
    public async Task AddVisitedPlacesLayer_Overload3_IncompleteEviction_ThrowsInvalidOperationExceptionOnBuild()
    {
        // ARRANGE — delegate adds no selector; EvictionConfigBuilder.Build() throws at BuildAsync() time
        var builder = CreateLayeredBuilder()
            .AddVisitedPlacesLayer(
                b => b.AddPolicy(new MaxSegmentCountPolicy<int, int>(10)));

        // ACT — AddVisitedPlacesLayer just registers the factory; the exception is deferred to BuildAsync()
        var exception = await Record.ExceptionAsync(async () => await builder.BuildAsync());

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<InvalidOperationException>(exception);
    }

    #endregion

    #region Overload 4: configureEviction + configure (inline options) Tests

    [Fact]
    public void AddVisitedPlacesLayer_Overload4_WithValidArguments_ReturnsSameBuilder()
    {
        // ARRANGE
        var builder = CreateLayeredBuilder();

        // ACT
        var returned = builder.AddVisitedPlacesLayer(
            ConfigureEviction,
            b => b.WithEventChannelCapacity(64));

        // ASSERT
        Assert.Same(builder, returned);
    }

    [Fact]
    public void AddVisitedPlacesLayer_Overload4_WithNullConfigureEviction_ThrowsArgumentNullException()
    {
        // ARRANGE
        var builder = CreateLayeredBuilder();

        // ACT
        var exception = Record.Exception(
            () => builder.AddVisitedPlacesLayer(
                (Action<EvictionConfigBuilder<int, int>>)null!,
                b => b.WithEventChannelCapacity(64)));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentNullException>(exception);
    }

    [Fact]
    public void AddVisitedPlacesLayer_Overload4_WithNullConfigure_ThrowsArgumentNullException()
    {
        // ARRANGE
        var builder = CreateLayeredBuilder();

        // ACT
        var exception = Record.Exception(
            () => builder.AddVisitedPlacesLayer(
                ConfigureEviction,
                (Action<VisitedPlacesCacheOptionsBuilder<int, int>>)null!));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentNullException>(exception);
    }

    #endregion
}
