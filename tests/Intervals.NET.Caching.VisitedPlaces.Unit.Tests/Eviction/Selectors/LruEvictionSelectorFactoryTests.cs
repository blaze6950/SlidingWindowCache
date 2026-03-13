using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Selectors;
using Intervals.NET.Caching.VisitedPlaces.Public.Configuration;

namespace Intervals.NET.Caching.VisitedPlaces.Unit.Tests.Eviction.Selectors;

/// <summary>
/// Unit tests for the <see cref="LruEvictionSelector"/> static factory companion class.
/// Validates that <see cref="LruEvictionSelector.Create{TRange,TData}"/> returns an instance
/// of the correct type with default and custom parameters.
/// </summary>
public sealed class LruEvictionSelectorFactoryTests
{
    #region Create — Default Parameters

    [Fact]
    public void Create_WithNoArguments_ReturnsLruEvictionSelector()
    {
        // ARRANGE & ACT
        var selector = LruEvictionSelector.Create<int, int>();

        // ASSERT
        Assert.IsType<LruEvictionSelector<int, int>>(selector);
    }

    [Fact]
    public void Create_WithNoArguments_DoesNotThrow()
    {
        // ARRANGE & ACT
        var exception = Record.Exception(() => LruEvictionSelector.Create<int, int>());

        // ASSERT
        Assert.Null(exception);
    }

    #endregion

    #region Create — Custom Parameters

    [Fact]
    public void Create_WithCustomSamplingOptions_ReturnsInstance()
    {
        // ARRANGE
        var samplingOptions = new EvictionSamplingOptions(sampleSize: 64);

        // ACT
        var selector = LruEvictionSelector.Create<int, int>(samplingOptions);

        // ASSERT
        Assert.IsType<LruEvictionSelector<int, int>>(selector);
    }

    [Fact]
    public void Create_WithCustomTimeProvider_ReturnsInstance()
    {
        // ARRANGE
        var timeProvider = TimeProvider.System;

        // ACT
        var selector = LruEvictionSelector.Create<int, int>(timeProvider: timeProvider);

        // ASSERT
        Assert.IsType<LruEvictionSelector<int, int>>(selector);
    }

    [Fact]
    public void Create_WithBothCustomParameters_ReturnsInstance()
    {
        // ARRANGE
        var samplingOptions = new EvictionSamplingOptions(sampleSize: 16);

        // ACT
        var selector = LruEvictionSelector.Create<int, string>(samplingOptions, TimeProvider.System);

        // ASSERT
        Assert.IsType<LruEvictionSelector<int, string>>(selector);
    }

    #endregion
}
