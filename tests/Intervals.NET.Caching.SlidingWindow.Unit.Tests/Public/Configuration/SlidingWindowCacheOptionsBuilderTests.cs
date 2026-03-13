using Intervals.NET.Caching.SlidingWindow.Public.Configuration;

namespace Intervals.NET.Caching.SlidingWindow.Unit.Tests.Public.Configuration;

/// <summary>
/// Unit tests for <see cref="SlidingWindowCacheOptionsBuilder"/> that verify fluent API,
/// default values, required-field enforcement, and <see cref="SlidingWindowCacheOptions"/> output.
/// </summary>
public class SlidingWindowCacheOptionsBuilderTests
{
    #region Build() — Required Fields Tests

    [Fact]
    public void Build_WithoutCacheSize_ThrowsInvalidOperationException()
    {
        // ARRANGE
        var builder = new SlidingWindowCacheOptionsBuilder();

        // ACT
        var exception = Record.Exception(() => builder.Build());

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<InvalidOperationException>(exception);
    }

    [Fact]
    public void Build_WithOnlyLeftCacheSize_ThrowsInvalidOperationException()
    {
        // ARRANGE
        var builder = new SlidingWindowCacheOptionsBuilder().WithLeftCacheSize(1.0);

        // ACT
        var exception = Record.Exception(() => builder.Build());

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<InvalidOperationException>(exception);
    }

    [Fact]
    public void Build_WithOnlyRightCacheSize_ThrowsInvalidOperationException()
    {
        // ARRANGE
        var builder = new SlidingWindowCacheOptionsBuilder().WithRightCacheSize(1.0);

        // ACT
        var exception = Record.Exception(() => builder.Build());

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<InvalidOperationException>(exception);
    }

    [Fact]
    public void Build_WithBothCacheSizesSet_DoesNotThrow()
    {
        // ARRANGE
        var builder = new SlidingWindowCacheOptionsBuilder()
            .WithLeftCacheSize(1.0)
            .WithRightCacheSize(2.0);

        // ACT
        var exception = Record.Exception(() => builder.Build());

        // ASSERT
        Assert.Null(exception);
    }

    [Fact]
    public void Build_WithSymmetricCacheSize_DoesNotThrow()
    {
        // ARRANGE
        var builder = new SlidingWindowCacheOptionsBuilder().WithCacheSize(1.5);

        // ACT
        var exception = Record.Exception(() => builder.Build());

        // ASSERT
        Assert.Null(exception);
    }

    [Fact]
    public void Build_WithAsymmetricCacheSize_DoesNotThrow()
    {
        // ARRANGE
        var builder = new SlidingWindowCacheOptionsBuilder().WithCacheSize(1.0, 2.0);

        // ACT
        var exception = Record.Exception(() => builder.Build());

        // ASSERT
        Assert.Null(exception);
    }

    #endregion

    #region WithLeftCacheSize / WithRightCacheSize Tests

    [Fact]
    public void Build_WithLeftAndRightCacheSize_SetsCorrectValues()
    {
        // ARRANGE & ACT
        var options = new SlidingWindowCacheOptionsBuilder()
            .WithLeftCacheSize(1.5)
            .WithRightCacheSize(3.0)
            .Build();

        // ASSERT
        Assert.Equal(1.5, options.LeftCacheSize);
        Assert.Equal(3.0, options.RightCacheSize);
    }

    [Fact]
    public void Build_WithSymmetricCacheSize_SetsBothSides()
    {
        // ARRANGE & ACT
        var options = new SlidingWindowCacheOptionsBuilder()
            .WithCacheSize(2.0)
            .Build();

        // ASSERT
        Assert.Equal(2.0, options.LeftCacheSize);
        Assert.Equal(2.0, options.RightCacheSize);
    }

    [Fact]
    public void Build_WithAsymmetricCacheSize_SetsBothSidesIndependently()
    {
        // ARRANGE & ACT
        var options = new SlidingWindowCacheOptionsBuilder()
            .WithCacheSize(0.5, 4.0)
            .Build();

        // ASSERT
        Assert.Equal(0.5, options.LeftCacheSize);
        Assert.Equal(4.0, options.RightCacheSize);
    }

    [Fact]
    public void Build_WithZeroCacheSize_DoesNotThrow()
    {
        // ARRANGE & ACT
        var exception = Record.Exception(() =>
            new SlidingWindowCacheOptionsBuilder().WithCacheSize(0.0).Build());

        // ASSERT
        Assert.Null(exception);
    }

    [Fact]
    public void Build_WithNegativeCacheSize_ThrowsArgumentOutOfRangeException()
    {
        // ARRANGE
        var builder = new SlidingWindowCacheOptionsBuilder().WithCacheSize(-1.0);

        // ACT
        var exception = Record.Exception(() => builder.Build());

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentOutOfRangeException>(exception);
    }

    [Fact]
    public void Build_WithNegativeLeftCacheSize_ThrowsArgumentOutOfRangeException()
    {
        // ARRANGE
        var builder = new SlidingWindowCacheOptionsBuilder()
            .WithLeftCacheSize(-0.5)
            .WithRightCacheSize(1.0);

        // ACT
        var exception = Record.Exception(() => builder.Build());

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentOutOfRangeException>(exception);
    }

    #endregion

    #region WithReadMode Tests

    [Fact]
    public void Build_DefaultReadMode_IsSnapshot()
    {
        // ARRANGE & ACT
        var options = new SlidingWindowCacheOptionsBuilder()
            .WithCacheSize(1.0)
            .Build();

        // ASSERT
        Assert.Equal(UserCacheReadMode.Snapshot, options.ReadMode);
    }

    [Fact]
    public void Build_WithReadModeCopyOnRead_SetsCopyOnRead()
    {
        // ARRANGE & ACT
        var options = new SlidingWindowCacheOptionsBuilder()
            .WithCacheSize(1.0)
            .WithReadMode(UserCacheReadMode.CopyOnRead)
            .Build();

        // ASSERT
        Assert.Equal(UserCacheReadMode.CopyOnRead, options.ReadMode);
    }

    [Fact]
    public void Build_WithReadModeSnapshot_SetsSnapshot()
    {
        // ARRANGE & ACT
        var options = new SlidingWindowCacheOptionsBuilder()
            .WithCacheSize(1.0)
            .WithReadMode(UserCacheReadMode.Snapshot)
            .Build();

        // ASSERT
        Assert.Equal(UserCacheReadMode.Snapshot, options.ReadMode);
    }

    #endregion

    #region WithThresholds Tests

    [Fact]
    public void Build_WithoutThresholds_ThresholdsAreNull()
    {
        // ARRANGE & ACT
        var options = new SlidingWindowCacheOptionsBuilder()
            .WithCacheSize(1.0)
            .Build();

        // ASSERT
        Assert.Null(options.LeftThreshold);
        Assert.Null(options.RightThreshold);
    }

    [Fact]
    public void Build_WithSymmetricThresholds_SetsBothSides()
    {
        // ARRANGE & ACT
        var options = new SlidingWindowCacheOptionsBuilder()
            .WithCacheSize(1.0)
            .WithThresholds(0.2)
            .Build();

        // ASSERT
        Assert.Equal(0.2, options.LeftThreshold);
        Assert.Equal(0.2, options.RightThreshold);
    }

    [Fact]
    public void Build_WithLeftThresholdOnly_SetsLeftAndRightIsNull()
    {
        // ARRANGE & ACT
        var options = new SlidingWindowCacheOptionsBuilder()
            .WithCacheSize(1.0)
            .WithLeftThreshold(0.3)
            .Build();

        // ASSERT
        Assert.Equal(0.3, options.LeftThreshold);
        Assert.Null(options.RightThreshold);
    }

    [Fact]
    public void Build_WithRightThresholdOnly_SetsRightAndLeftIsNull()
    {
        // ARRANGE & ACT
        var options = new SlidingWindowCacheOptionsBuilder()
            .WithCacheSize(1.0)
            .WithRightThreshold(0.25)
            .Build();

        // ASSERT
        Assert.Null(options.LeftThreshold);
        Assert.Equal(0.25, options.RightThreshold);
    }

    [Fact]
    public void Build_WithBothThresholdsIndependently_SetsBothCorrectly()
    {
        // ARRANGE & ACT
        var options = new SlidingWindowCacheOptionsBuilder()
            .WithCacheSize(1.0)
            .WithLeftThreshold(0.1)
            .WithRightThreshold(0.15)
            .Build();

        // ASSERT
        Assert.Equal(0.1, options.LeftThreshold);
        Assert.Equal(0.15, options.RightThreshold);
    }

    [Fact]
    public void Build_WithThresholdSumExceedingOne_ThrowsArgumentException()
    {
        // ARRANGE — 0.6 + 0.6 = 1.2 > 1.0
        var builder = new SlidingWindowCacheOptionsBuilder()
            .WithCacheSize(1.0)
            .WithThresholds(0.6);

        // ACT
        var exception = Record.Exception(() => builder.Build());

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentException>(exception);
    }

    [Fact]
    public void Build_WithZeroThresholds_SetsZero()
    {
        // ARRANGE & ACT
        var options = new SlidingWindowCacheOptionsBuilder()
            .WithCacheSize(1.0)
            .WithThresholds(0.0)
            .Build();

        // ASSERT
        Assert.Equal(0.0, options.LeftThreshold);
        Assert.Equal(0.0, options.RightThreshold);
    }

    #endregion

    #region WithDebounceDelay Tests

    [Fact]
    public void Build_WithDebounceDelay_SetsCorrectValue()
    {
        // ARRANGE & ACT
        var options = new SlidingWindowCacheOptionsBuilder()
            .WithCacheSize(1.0)
            .WithDebounceDelay(TimeSpan.FromMilliseconds(250))
            .Build();

        // ASSERT
        Assert.Equal(TimeSpan.FromMilliseconds(250), options.DebounceDelay);
    }

    [Fact]
    public void Build_WithZeroDebounceDelay_SetsZero()
    {
        // ARRANGE & ACT
        var options = new SlidingWindowCacheOptionsBuilder()
            .WithCacheSize(1.0)
            .WithDebounceDelay(TimeSpan.Zero)
            .Build();

        // ASSERT
        Assert.Equal(TimeSpan.Zero, options.DebounceDelay);
    }

    [Fact]
    public void WithDebounceDelay_WithNegativeValue_ThrowsArgumentOutOfRangeException()
    {
        // ARRANGE
        var builder = new SlidingWindowCacheOptionsBuilder().WithCacheSize(1.0);

        // ACT
        var exception = Record.Exception(() => builder.WithDebounceDelay(TimeSpan.FromMilliseconds(-1)));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentOutOfRangeException>(exception);
    }

    #endregion

    #region WithRebalanceQueueCapacity Tests

    [Fact]
    public void Build_WithRebalanceQueueCapacity_SetsCorrectValue()
    {
        // ARRANGE & ACT
        var options = new SlidingWindowCacheOptionsBuilder()
            .WithCacheSize(1.0)
            .WithRebalanceQueueCapacity(10)
            .Build();

        // ASSERT
        Assert.Equal(10, options.RebalanceQueueCapacity);
    }

    [Fact]
    public void Build_WithoutRebalanceQueueCapacity_CapacityIsNull()
    {
        // ARRANGE & ACT
        var options = new SlidingWindowCacheOptionsBuilder()
            .WithCacheSize(1.0)
            .Build();

        // ASSERT
        Assert.Null(options.RebalanceQueueCapacity);
    }

    #endregion

    #region Fluent Chaining Tests

    [Fact]
    public void FluentMethods_ReturnSameBuilderInstance()
    {
        // ARRANGE
        var builder = new SlidingWindowCacheOptionsBuilder();

        // ACT & ASSERT — each method returns the same instance
        Assert.Same(builder, builder.WithLeftCacheSize(1.0));
        Assert.Same(builder, builder.WithRightCacheSize(1.0));
        Assert.Same(builder, builder.WithReadMode(UserCacheReadMode.Snapshot));
        Assert.Same(builder, builder.WithLeftThreshold(0.1));
        Assert.Same(builder, builder.WithRightThreshold(0.1));
        Assert.Same(builder, builder.WithDebounceDelay(TimeSpan.Zero));
        Assert.Same(builder, builder.WithRebalanceQueueCapacity(5));
    }

    [Fact]
    public void Build_FullFluentChain_ProducesCorrectOptions()
    {
        // ARRANGE & ACT
        var options = new SlidingWindowCacheOptionsBuilder()
            .WithCacheSize(1.5, 3.0)
            .WithReadMode(UserCacheReadMode.CopyOnRead)
            .WithLeftThreshold(0.1)
            .WithRightThreshold(0.15)
            .WithDebounceDelay(TimeSpan.FromMilliseconds(200))
            .WithRebalanceQueueCapacity(8)
            .Build();

        // ASSERT
        Assert.Equal(1.5, options.LeftCacheSize);
        Assert.Equal(3.0, options.RightCacheSize);
        Assert.Equal(UserCacheReadMode.CopyOnRead, options.ReadMode);
        Assert.Equal(0.1, options.LeftThreshold);
        Assert.Equal(0.15, options.RightThreshold);
        Assert.Equal(TimeSpan.FromMilliseconds(200), options.DebounceDelay);
        Assert.Equal(8, options.RebalanceQueueCapacity);
    }

    [Fact]
    public void Build_LatestCallWins_CacheSizeOverwrite()
    {
        // ARRANGE — set size twice; last call should win
        var options = new SlidingWindowCacheOptionsBuilder()
            .WithCacheSize(1.0)
            .WithCacheSize(5.0)
            .Build();

        // ASSERT
        Assert.Equal(5.0, options.LeftCacheSize);
        Assert.Equal(5.0, options.RightCacheSize);
    }

    [Fact]
    public void Build_WithCacheSizeAfterLeftRight_OverwritesBothSides()
    {
        // ARRANGE — WithCacheSize(double) after WithLeftCacheSize/WithRightCacheSize overwrites both
        var options = new SlidingWindowCacheOptionsBuilder()
            .WithLeftCacheSize(1.0)
            .WithRightCacheSize(2.0)
            .WithCacheSize(3.0)
            .Build();

        // ASSERT
        Assert.Equal(3.0, options.LeftCacheSize);
        Assert.Equal(3.0, options.RightCacheSize);
    }

    #endregion

    #region Type Tests

    [Fact]
    public void WindowCacheOptionsBuilder_IsSealed()
    {
        // ASSERT
        Assert.True(typeof(SlidingWindowCacheOptionsBuilder).IsSealed);
    }

    [Fact]
    public void WindowCacheOptionsBuilder_HasPublicParameterlessConstructor()
    {
        // ASSERT — verifies standalone usability
        var ctor = typeof(SlidingWindowCacheOptionsBuilder)
            .GetConstructor(Type.EmptyTypes);

        Assert.NotNull(ctor);
        Assert.True(ctor!.IsPublic);
    }

    #endregion
}
