using Intervals.NET.Caching.Core.State;

namespace Intervals.NET.Caching.Unit.Tests.Core.State;

/// <summary>
/// Unit tests for <see cref="RuntimeOptionsValidator"/> that verify all shared validation rules
/// are enforced correctly for both cache sizes and thresholds.
/// </summary>
public class RuntimeOptionsValidatorTests
{
    #region Valid Parameters Tests

    [Fact]
    public void Validate_WithAllValidParameters_DoesNotThrow()
    {
        // ARRANGE & ACT
        var exception = Record.Exception(() =>
            RuntimeOptionsValidator.ValidateCacheSizesAndThresholds(1.0, 2.0, 0.2, 0.3));

        // ASSERT
        Assert.Null(exception);
    }

    [Fact]
    public void Validate_WithZeroCacheSizes_DoesNotThrow()
    {
        // ARRANGE & ACT
        var exception = Record.Exception(() =>
            RuntimeOptionsValidator.ValidateCacheSizesAndThresholds(0.0, 0.0, null, null));

        // ASSERT
        Assert.Null(exception);
    }

    [Fact]
    public void Validate_WithNullThresholds_DoesNotThrow()
    {
        // ARRANGE & ACT
        var exception = Record.Exception(() =>
            RuntimeOptionsValidator.ValidateCacheSizesAndThresholds(1.0, 1.0, null, null));

        // ASSERT
        Assert.Null(exception);
    }

    [Fact]
    public void Validate_WithThresholdsSummingToExactlyOne_DoesNotThrow()
    {
        // ARRANGE & ACT
        var exception = Record.Exception(() =>
            RuntimeOptionsValidator.ValidateCacheSizesAndThresholds(1.0, 1.0, 0.5, 0.5));

        // ASSERT
        Assert.Null(exception);
    }

    [Fact]
    public void Validate_WithZeroThresholds_DoesNotThrow()
    {
        // ARRANGE & ACT
        var exception = Record.Exception(() =>
            RuntimeOptionsValidator.ValidateCacheSizesAndThresholds(1.0, 1.0, 0.0, 0.0));

        // ASSERT
        Assert.Null(exception);
    }

    [Fact]
    public void Validate_WithMaxThresholdValues_DoesNotThrow()
    {
        // ARRANGE & ACT
        var exception = Record.Exception(() =>
            RuntimeOptionsValidator.ValidateCacheSizesAndThresholds(1.0, 1.0, 1.0, null));

        // ASSERT
        Assert.Null(exception);
    }

    #endregion

    #region LeftCacheSize Validation Tests

    [Fact]
    public void Validate_WithNegativeLeftCacheSize_ThrowsArgumentOutOfRangeException()
    {
        // ACT
        var exception = Record.Exception(() =>
            RuntimeOptionsValidator.ValidateCacheSizesAndThresholds(-0.1, 1.0, null, null));

        // ASSERT
        var ex = Assert.IsType<ArgumentOutOfRangeException>(exception);
        Assert.Equal("leftCacheSize", ex.ParamName);
        Assert.Contains("LeftCacheSize must be greater than or equal to 0.", ex.Message);
    }

    #endregion

    #region RightCacheSize Validation Tests

    [Fact]
    public void Validate_WithNegativeRightCacheSize_ThrowsArgumentOutOfRangeException()
    {
        // ACT
        var exception = Record.Exception(() =>
            RuntimeOptionsValidator.ValidateCacheSizesAndThresholds(1.0, -0.1, null, null));

        // ASSERT
        var ex = Assert.IsType<ArgumentOutOfRangeException>(exception);
        Assert.Equal("rightCacheSize", ex.ParamName);
        Assert.Contains("RightCacheSize must be greater than or equal to 0.", ex.Message);
    }

    #endregion

    #region LeftThreshold Validation Tests

    [Fact]
    public void Validate_WithNegativeLeftThreshold_ThrowsArgumentOutOfRangeException()
    {
        // ACT
        var exception = Record.Exception(() =>
            RuntimeOptionsValidator.ValidateCacheSizesAndThresholds(1.0, 1.0, -0.1, null));

        // ASSERT
        var ex = Assert.IsType<ArgumentOutOfRangeException>(exception);
        Assert.Equal("leftThreshold", ex.ParamName);
        Assert.Contains("LeftThreshold must be greater than or equal to 0.", ex.Message);
    }

    [Fact]
    public void Validate_WithLeftThresholdExceedingOne_ThrowsArgumentOutOfRangeException()
    {
        // ACT
        var exception = Record.Exception(() =>
            RuntimeOptionsValidator.ValidateCacheSizesAndThresholds(1.0, 1.0, 1.1, null));

        // ASSERT
        var ex = Assert.IsType<ArgumentOutOfRangeException>(exception);
        Assert.Equal("leftThreshold", ex.ParamName);
        Assert.Contains("LeftThreshold must not exceed 1.0.", ex.Message);
    }

    #endregion

    #region RightThreshold Validation Tests

    [Fact]
    public void Validate_WithNegativeRightThreshold_ThrowsArgumentOutOfRangeException()
    {
        // ACT
        var exception = Record.Exception(() =>
            RuntimeOptionsValidator.ValidateCacheSizesAndThresholds(1.0, 1.0, null, -0.1));

        // ASSERT
        var ex = Assert.IsType<ArgumentOutOfRangeException>(exception);
        Assert.Equal("rightThreshold", ex.ParamName);
        Assert.Contains("RightThreshold must be greater than or equal to 0.", ex.Message);
    }

    [Fact]
    public void Validate_WithRightThresholdExceedingOne_ThrowsArgumentOutOfRangeException()
    {
        // ACT
        var exception = Record.Exception(() =>
            RuntimeOptionsValidator.ValidateCacheSizesAndThresholds(1.0, 1.0, null, 1.1));

        // ASSERT
        var ex = Assert.IsType<ArgumentOutOfRangeException>(exception);
        Assert.Equal("rightThreshold", ex.ParamName);
        Assert.Contains("RightThreshold must not exceed 1.0.", ex.Message);
    }

    #endregion

    #region Threshold Sum Validation Tests

    [Fact]
    public void Validate_WithThresholdSumExceedingOne_ThrowsArgumentException()
    {
        // ACT
        var exception = Record.Exception(() =>
            RuntimeOptionsValidator.ValidateCacheSizesAndThresholds(1.0, 1.0, 0.6, 0.5));

        // ASSERT
        var ex = Assert.IsType<ArgumentException>(exception);
        Assert.Contains("sum", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_WithOnlyLeftThresholdSet_NoSumValidation()
    {
        // ARRANGE & ACT — single threshold at 1.0 is valid; sum check only applies when both are non-null
        var exception = Record.Exception(() =>
            RuntimeOptionsValidator.ValidateCacheSizesAndThresholds(1.0, 1.0, 1.0, null));

        // ASSERT
        Assert.Null(exception);
    }

    [Fact]
    public void Validate_WithOnlyRightThresholdSet_NoSumValidation()
    {
        // ARRANGE & ACT
        var exception = Record.Exception(() =>
            RuntimeOptionsValidator.ValidateCacheSizesAndThresholds(1.0, 1.0, null, 1.0));

        // ASSERT
        Assert.Null(exception);
    }

    #endregion
}
