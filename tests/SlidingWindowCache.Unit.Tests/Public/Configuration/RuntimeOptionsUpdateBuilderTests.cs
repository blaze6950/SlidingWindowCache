using SlidingWindowCache.Core.State;
using SlidingWindowCache.Public.Configuration;

namespace SlidingWindowCache.Unit.Tests.Public.Configuration;

/// <summary>
/// Unit tests for <see cref="RuntimeOptionsUpdateBuilder"/> verifying fluent API,
/// partial-update semantics, and threshold clear/set distinctions.
/// </summary>
public class RuntimeOptionsUpdateBuilderTests
{
    private static RuntimeCacheOptions BaseOptions() => new(1.0, 2.0, 0.1, 0.2, TimeSpan.FromMilliseconds(50));

    #region Builder Method Tests — WithLeftCacheSize

    [Fact]
    public void ApplyTo_WithLeftCacheSizeSet_ChangesOnlyLeftCacheSize()
    {
        var builder = new RuntimeOptionsUpdateBuilder();
        builder.WithLeftCacheSize(5.0);

        var result = builder.ApplyTo(BaseOptions());

        Assert.Equal(5.0, result.LeftCacheSize);
        Assert.Equal(2.0, result.RightCacheSize);     // unchanged
        Assert.Equal(0.1, result.LeftThreshold);      // unchanged
        Assert.Equal(0.2, result.RightThreshold);     // unchanged
        Assert.Equal(TimeSpan.FromMilliseconds(50), result.DebounceDelay); // unchanged
    }

    #endregion

    #region Builder Method Tests — WithRightCacheSize

    [Fact]
    public void ApplyTo_WithRightCacheSizeSet_ChangesOnlyRightCacheSize()
    {
        var builder = new RuntimeOptionsUpdateBuilder();
        builder.WithRightCacheSize(7.0);

        var result = builder.ApplyTo(BaseOptions());

        Assert.Equal(1.0, result.LeftCacheSize);      // unchanged
        Assert.Equal(7.0, result.RightCacheSize);
        Assert.Equal(0.1, result.LeftThreshold);      // unchanged
        Assert.Equal(0.2, result.RightThreshold);     // unchanged
        Assert.Equal(TimeSpan.FromMilliseconds(50), result.DebounceDelay); // unchanged
    }

    #endregion

    #region Builder Method Tests — WithLeftThreshold / ClearLeftThreshold

    [Fact]
    public void ApplyTo_WithLeftThresholdSet_UpdatesLeftThreshold()
    {
        var builder = new RuntimeOptionsUpdateBuilder();
        builder.WithLeftThreshold(0.3);

        var result = builder.ApplyTo(BaseOptions());

        Assert.Equal(0.3, result.LeftThreshold);
        Assert.Equal(0.2, result.RightThreshold); // unchanged
    }

    [Fact]
    public void ApplyTo_ClearLeftThreshold_SetsLeftThresholdToNull()
    {
        var builder = new RuntimeOptionsUpdateBuilder();
        builder.ClearLeftThreshold();

        var result = builder.ApplyTo(BaseOptions());

        Assert.Null(result.LeftThreshold);
        Assert.Equal(0.2, result.RightThreshold); // unchanged
    }

    [Fact]
    public void ApplyTo_LeftThresholdNotSet_KeepsCurrentValue()
    {
        // No threshold method called on builder
        var builder = new RuntimeOptionsUpdateBuilder();
        builder.WithLeftCacheSize(3.0); // only set left cache size

        var result = builder.ApplyTo(BaseOptions());

        Assert.Equal(0.1, result.LeftThreshold); // unchanged from base
    }

    #endregion

    #region Builder Method Tests — WithRightThreshold / ClearRightThreshold

    [Fact]
    public void ApplyTo_WithRightThresholdSet_UpdatesRightThreshold()
    {
        var builder = new RuntimeOptionsUpdateBuilder();
        builder.WithRightThreshold(0.35);

        var result = builder.ApplyTo(BaseOptions());

        Assert.Equal(0.35, result.RightThreshold);
        Assert.Equal(0.1, result.LeftThreshold); // unchanged
    }

    [Fact]
    public void ApplyTo_ClearRightThreshold_SetsRightThresholdToNull()
    {
        var builder = new RuntimeOptionsUpdateBuilder();
        builder.ClearRightThreshold();

        var result = builder.ApplyTo(BaseOptions());

        Assert.Null(result.RightThreshold);
        Assert.Equal(0.1, result.LeftThreshold); // unchanged
    }

    [Fact]
    public void ApplyTo_RightThresholdNotSet_KeepsCurrentValue()
    {
        var builder = new RuntimeOptionsUpdateBuilder();
        // Only change debounce
        builder.WithDebounceDelay(TimeSpan.Zero);

        var result = builder.ApplyTo(BaseOptions());

        Assert.Equal(0.2, result.RightThreshold); // unchanged from base
    }

    #endregion

    #region Builder Method Tests — WithDebounceDelay

    [Fact]
    public void ApplyTo_WithDebounceDelaySet_ChangesOnlyDebounceDelay()
    {
        var builder = new RuntimeOptionsUpdateBuilder();
        builder.WithDebounceDelay(TimeSpan.FromSeconds(1));

        var result = builder.ApplyTo(BaseOptions());

        Assert.Equal(TimeSpan.FromSeconds(1), result.DebounceDelay);
        Assert.Equal(1.0, result.LeftCacheSize);  // unchanged
        Assert.Equal(2.0, result.RightCacheSize); // unchanged
    }

    #endregion

    #region Builder Fluent Chaining Tests

    [Fact]
    public void ApplyTo_FluentChain_AppliesAllChanges()
    {
        var base_ = new RuntimeCacheOptions(1.0, 1.0, null, null, TimeSpan.Zero);
        var builder = new RuntimeOptionsUpdateBuilder();
        builder
            .WithLeftCacheSize(2.0)
            .WithRightCacheSize(3.0)
            .WithLeftThreshold(0.1)
            .WithRightThreshold(0.15)
            .WithDebounceDelay(TimeSpan.FromMilliseconds(75));

        var result = builder.ApplyTo(base_);

        Assert.Equal(2.0, result.LeftCacheSize);
        Assert.Equal(3.0, result.RightCacheSize);
        Assert.Equal(0.1, result.LeftThreshold);
        Assert.Equal(0.15, result.RightThreshold);
        Assert.Equal(TimeSpan.FromMilliseconds(75), result.DebounceDelay);
    }

    [Fact]
    public void ApplyTo_EmptyBuilder_ReturnsSnapshotWithAllCurrentValues()
    {
        var base_ = BaseOptions();
        var builder = new RuntimeOptionsUpdateBuilder();

        var result = builder.ApplyTo(base_);

        Assert.Equal(base_.LeftCacheSize, result.LeftCacheSize);
        Assert.Equal(base_.RightCacheSize, result.RightCacheSize);
        Assert.Equal(base_.LeftThreshold, result.LeftThreshold);
        Assert.Equal(base_.RightThreshold, result.RightThreshold);
        Assert.Equal(base_.DebounceDelay, result.DebounceDelay);
    }

    #endregion

    #region Builder Validation via ApplyTo Tests

    [Fact]
    public void ApplyTo_WithInvalidMergedCacheSize_ThrowsArgumentOutOfRangeException()
    {
        var builder = new RuntimeOptionsUpdateBuilder();
        builder.WithLeftCacheSize(-1.0);

        var exception = Record.Exception(() => builder.ApplyTo(BaseOptions()));

        Assert.NotNull(exception);
        Assert.IsType<ArgumentOutOfRangeException>(exception);
    }

    [Fact]
    public void ApplyTo_WithThresholdSumExceedingOne_ThrowsArgumentException()
    {
        // Base has leftThreshold=0.1; set right=0.95 → sum=1.05
        var builder = new RuntimeOptionsUpdateBuilder();
        builder.WithRightThreshold(0.95);

        var exception = Record.Exception(() => builder.ApplyTo(BaseOptions()));

        Assert.NotNull(exception);
        Assert.IsType<ArgumentException>(exception);
    }

    [Fact]
    public void WithDebounceDelay_WithNegativeValue_ThrowsArgumentOutOfRangeException()
    {
        // ARRANGE
        var builder = new RuntimeOptionsUpdateBuilder();

        // ACT
        var exception = Record.Exception(() => builder.WithDebounceDelay(TimeSpan.FromMilliseconds(-1)));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentOutOfRangeException>(exception);
    }

    #endregion
}
