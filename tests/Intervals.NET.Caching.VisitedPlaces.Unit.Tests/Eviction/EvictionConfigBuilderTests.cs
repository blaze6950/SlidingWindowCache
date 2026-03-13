using Intervals.NET.Caching.VisitedPlaces.Core.Eviction;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Policies;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Selectors;
using Intervals.NET.Caching.VisitedPlaces.Public.Cache;
using Intervals.NET.Caching.VisitedPlaces.Public.Configuration;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure.Helpers;

namespace Intervals.NET.Caching.VisitedPlaces.Unit.Tests.Eviction;

/// <summary>
/// Unit tests for <see cref="EvictionConfigBuilder{TRange,TData}"/>.
/// Validates <see cref="EvictionConfigBuilder{TRange,TData}.AddPolicy"/>,
/// <see cref="EvictionConfigBuilder{TRange,TData}.WithSelector"/>,
/// and the internal <c>Build</c> method via the public builder integration.
/// </summary>
public sealed class EvictionConfigBuilderTests
{
    #region AddPolicy

    [Fact]
    public void AddPolicy_WithNullPolicy_ThrowsArgumentNullException()
    {
        // ARRANGE
        var builder = new EvictionConfigBuilder<int, int>();

        // ACT
        var exception = Record.Exception(() =>
            builder.AddPolicy(null!));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentNullException>(exception);
    }

    [Fact]
    public void AddPolicy_ReturnsSameBuilderInstance_ForChaining()
    {
        // ARRANGE
        var builder = new EvictionConfigBuilder<int, int>();
        var policy = new MaxSegmentCountPolicy<int, int>(10);

        // ACT
        var returned = builder.AddPolicy(policy);

        // ASSERT
        Assert.Same(builder, returned);
    }

    [Fact]
    public void AddPolicy_CanAddMultiplePolicies()
    {
        // ARRANGE
        var domain = TestHelpers.CreateIntDomain();
        var builder = new EvictionConfigBuilder<int, int>();

        // ACT — two policies, no exception
        var exception = Record.Exception(() =>
        {
            builder
                .AddPolicy(new MaxSegmentCountPolicy<int, int>(10))
                .AddPolicy(MaxTotalSpanPolicy.Create<int, int, Intervals.NET.Domain.Default.Numeric.IntegerFixedStepDomain>(100, domain))
                .WithSelector(LruEvictionSelector.Create<int, int>());
        });

        // ASSERT
        Assert.Null(exception);
    }

    #endregion

    #region WithSelector

    [Fact]
    public void WithSelector_WithNullSelector_ThrowsArgumentNullException()
    {
        // ARRANGE
        var builder = new EvictionConfigBuilder<int, int>();

        // ACT
        var exception = Record.Exception(() =>
            builder.WithSelector(null!));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentNullException>(exception);
    }

    [Fact]
    public void WithSelector_ReturnsSameBuilderInstance_ForChaining()
    {
        // ARRANGE
        var builder = new EvictionConfigBuilder<int, int>();
        var selector = new LruEvictionSelector<int, int>();

        // ACT
        var returned = builder.WithSelector(selector);

        // ASSERT
        Assert.Same(builder, returned);
    }

    #endregion

    #region Build — via VisitedPlacesCacheBuilder.WithEviction delegate overload

    [Fact]
    public void WithEviction_WithValidConfig_BuildsSuccessfully()
    {
        // ARRANGE
        var domain = TestHelpers.CreateIntDomain();
        var dataSource = TestHelpers.CreateMockDataSource().Object;

        // ACT — uses the Action<EvictionConfigBuilder> overload
        var exception = Record.Exception(() =>
            VisitedPlacesCacheBuilder
                .For(dataSource, domain)
                .WithOptions(o => o.WithSegmentTtl(TimeSpan.FromMinutes(5)))
                .WithEviction(e => e
                    .AddPolicy(MaxSegmentCountPolicy.Create<int, int>(50))
                    .WithSelector(LruEvictionSelector.Create<int, int>()))
                .Build()
                .DisposeAsync());

        // ASSERT
        Assert.Null(exception);
    }

    [Fact]
    public void WithEviction_WithNullDelegate_ThrowsArgumentNullException()
    {
        // ARRANGE
        var domain = TestHelpers.CreateIntDomain();
        var dataSource = TestHelpers.CreateMockDataSource().Object;

        // ACT
        var exception = Record.Exception(() =>
            VisitedPlacesCacheBuilder
                .For(dataSource, domain)
                .WithOptions(o => { })
                .WithEviction((Action<EvictionConfigBuilder<int, int>>)null!));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentNullException>(exception);
    }

    [Fact]
    public void WithEviction_WithNoPoliciesAdded_ThrowsInvalidOperationException()
    {
        // ARRANGE
        var domain = TestHelpers.CreateIntDomain();
        var dataSource = TestHelpers.CreateMockDataSource().Object;

        // ACT
        var exception = Record.Exception(() =>
            VisitedPlacesCacheBuilder
                .For(dataSource, domain)
                .WithOptions(o => { })
                .WithEviction(e => e.WithSelector(LruEvictionSelector.Create<int, int>()))
                .Build());

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<InvalidOperationException>(exception);
    }

    [Fact]
    public void WithEviction_WithNoSelectorSet_ThrowsInvalidOperationException()
    {
        // ARRANGE
        var domain = TestHelpers.CreateIntDomain();
        var dataSource = TestHelpers.CreateMockDataSource().Object;

        // ACT
        var exception = Record.Exception(() =>
            VisitedPlacesCacheBuilder
                .For(dataSource, domain)
                .WithOptions(o => { })
                .WithEviction(e => e.AddPolicy(MaxSegmentCountPolicy.Create<int, int>(10)))
                .Build());

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<InvalidOperationException>(exception);
    }

    #endregion

    #region Fluent chaining — AddPolicy + WithSelector together

    [Fact]
    public void FluentChain_AddPolicyAndWithSelector_DoNotThrow()
    {
        // ARRANGE
        var builder = new EvictionConfigBuilder<int, int>();

        // ACT
        var exception = Record.Exception(() =>
            builder
                .AddPolicy(MaxSegmentCountPolicy.Create<int, int>(10))
                .WithSelector(FifoEvictionSelector.Create<int, int>()));

        // ASSERT
        Assert.Null(exception);
    }

    #endregion
}
