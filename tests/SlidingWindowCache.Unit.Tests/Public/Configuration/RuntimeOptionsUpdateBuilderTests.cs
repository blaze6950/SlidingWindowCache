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

    // Helper to create an internal builder via WindowCache (public API) isn't needed here
    // because we can test ApplyTo directly via internal access.
    // However, RuntimeOptionsUpdateBuilder's ctor is internal, so we access it via WindowCache.
    // Instead we test the builder indirectly through WindowCache.UpdateRuntimeOptions.
    // For pure unit tests of the builder logic, we use InternalsVisibleTo.

    // Since RuntimeOptionsUpdateBuilder constructor is internal but the tests are in a separate
    // assembly, we verify builder behaviour through ApplyTo by instantiating via reflection,
    // or test the full behaviour through WindowCache integration tests.
    // Here we use the WindowCache public API to exercise each builder method.

    #region Builder Method Tests — WithLeftCacheSize

    [Fact]
    public void ApplyTo_WithLeftCacheSizeSet_ChangesOnlyLeftCacheSize()
    {
        // We test ApplyTo indirectly: create builder through internal constructor via reflection.
        var builder = CreateBuilder();
        builder.WithLeftCacheSize(5.0);

        var result = InvokeApplyTo(builder, BaseOptions());

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
        var builder = CreateBuilder();
        builder.WithRightCacheSize(7.0);

        var result = InvokeApplyTo(builder, BaseOptions());

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
        var builder = CreateBuilder();
        builder.WithLeftThreshold(0.3);

        var result = InvokeApplyTo(builder, BaseOptions());

        Assert.Equal(0.3, result.LeftThreshold);
        Assert.Equal(0.2, result.RightThreshold); // unchanged
    }

    [Fact]
    public void ApplyTo_ClearLeftThreshold_SetsLeftThresholdToNull()
    {
        var builder = CreateBuilder();
        builder.ClearLeftThreshold();

        var result = InvokeApplyTo(builder, BaseOptions());

        Assert.Null(result.LeftThreshold);
        Assert.Equal(0.2, result.RightThreshold); // unchanged
    }

    [Fact]
    public void ApplyTo_LeftThresholdNotSet_KeepsCurrentValue()
    {
        // No threshold method called on builder
        var builder = CreateBuilder();
        builder.WithLeftCacheSize(3.0); // only set left cache size

        var result = InvokeApplyTo(builder, BaseOptions());

        Assert.Equal(0.1, result.LeftThreshold); // unchanged from base
    }

    #endregion

    #region Builder Method Tests — WithRightThreshold / ClearRightThreshold

    [Fact]
    public void ApplyTo_WithRightThresholdSet_UpdatesRightThreshold()
    {
        var builder = CreateBuilder();
        builder.WithRightThreshold(0.35);

        var result = InvokeApplyTo(builder, BaseOptions());

        Assert.Equal(0.35, result.RightThreshold);
        Assert.Equal(0.1, result.LeftThreshold); // unchanged
    }

    [Fact]
    public void ApplyTo_ClearRightThreshold_SetsRightThresholdToNull()
    {
        var builder = CreateBuilder();
        builder.ClearRightThreshold();

        var result = InvokeApplyTo(builder, BaseOptions());

        Assert.Null(result.RightThreshold);
        Assert.Equal(0.1, result.LeftThreshold); // unchanged
    }

    [Fact]
    public void ApplyTo_RightThresholdNotSet_KeepsCurrentValue()
    {
        var builder = CreateBuilder();
        // Only change debounce
        builder.WithDebounceDelay(TimeSpan.Zero);

        var result = InvokeApplyTo(builder, BaseOptions());

        Assert.Equal(0.2, result.RightThreshold); // unchanged from base
    }

    #endregion

    #region Builder Method Tests — WithDebounceDelay

    [Fact]
    public void ApplyTo_WithDebounceDelaySet_ChangesOnlyDebounceDelay()
    {
        var builder = CreateBuilder();
        builder.WithDebounceDelay(TimeSpan.FromSeconds(1));

        var result = InvokeApplyTo(builder, BaseOptions());

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
        var builder = CreateBuilder();
        builder
            .WithLeftCacheSize(2.0)
            .WithRightCacheSize(3.0)
            .WithLeftThreshold(0.1)
            .WithRightThreshold(0.15)
            .WithDebounceDelay(TimeSpan.FromMilliseconds(75));

        var result = InvokeApplyTo(builder, base_);

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
        var builder = CreateBuilder();

        var result = InvokeApplyTo(builder, base_);

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
        var builder = CreateBuilder();
        builder.WithLeftCacheSize(-1.0);

        var exception = Record.Exception(() => InvokeApplyTo(builder, BaseOptions()));

        Assert.NotNull(exception);
        Assert.IsType<ArgumentOutOfRangeException>(exception);
    }

    [Fact]
    public void ApplyTo_WithThresholdSumExceedingOne_ThrowsArgumentException()
    {
        // Base has leftThreshold=0.1; set right=0.95 → sum=1.05
        var builder = CreateBuilder();
        builder.WithRightThreshold(0.95);

        var exception = Record.Exception(() => InvokeApplyTo(builder, BaseOptions()));

        Assert.NotNull(exception);
        Assert.IsType<ArgumentException>(exception);
    }

    #endregion

    // Helpers — create builder via internal constructor using reflection,
    // and invoke the internal ApplyTo method.
    private static RuntimeOptionsUpdateBuilder CreateBuilder()
    {
        var ctor = typeof(RuntimeOptionsUpdateBuilder)
            .GetConstructor(
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                null, Type.EmptyTypes, null)!;
        return (RuntimeOptionsUpdateBuilder)ctor.Invoke(null);
    }

    private static RuntimeCacheOptions InvokeApplyTo(
        RuntimeOptionsUpdateBuilder builder,
        RuntimeCacheOptions current)
    {
        var method = typeof(RuntimeOptionsUpdateBuilder)
            .GetMethod("ApplyTo",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        try
        {
            return (RuntimeCacheOptions)method.Invoke(builder, [current])!;
        }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw; // unreachable, satisfies compiler
        }
    }
}
