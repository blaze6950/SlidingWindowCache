using SlidingWindowCache.Core.State;

namespace SlidingWindowCache.Unit.Tests.Core.State;

/// <summary>
/// Unit tests for <see cref="RuntimeCacheOptions"/> that verify validation logic and property initialization.
/// </summary>
public class RuntimeCacheOptionsTests
{
    #region Constructor - Valid Parameters Tests

    [Fact]
    public void Constructor_WithValidParameters_InitializesAllProperties()
    {
        // ARRANGE & ACT
        var options = new RuntimeCacheOptions(
            leftCacheSize: 1.5,
            rightCacheSize: 2.0,
            leftThreshold: 0.3,
            rightThreshold: 0.4,
            debounceDelay: TimeSpan.FromMilliseconds(200)
        );

        // ASSERT
        Assert.Equal(1.5, options.LeftCacheSize);
        Assert.Equal(2.0, options.RightCacheSize);
        Assert.Equal(0.3, options.LeftThreshold);
        Assert.Equal(0.4, options.RightThreshold);
        Assert.Equal(TimeSpan.FromMilliseconds(200), options.DebounceDelay);
    }

    [Fact]
    public void Constructor_WithNullThresholds_IsValid()
    {
        // ARRANGE & ACT
        var options = new RuntimeCacheOptions(1.0, 1.0, null, null, TimeSpan.Zero);

        // ASSERT
        Assert.Null(options.LeftThreshold);
        Assert.Null(options.RightThreshold);
    }

    [Fact]
    public void Constructor_WithZeroCacheSizes_IsValid()
    {
        // ARRANGE & ACT
        var options = new RuntimeCacheOptions(0.0, 0.0, null, null, TimeSpan.Zero);

        // ASSERT
        Assert.Equal(0.0, options.LeftCacheSize);
        Assert.Equal(0.0, options.RightCacheSize);
    }

    [Fact]
    public void Constructor_WithZeroThresholds_IsValid()
    {
        // ARRANGE & ACT
        var options = new RuntimeCacheOptions(1.0, 1.0, 0.0, 0.0, TimeSpan.Zero);

        // ASSERT
        Assert.Equal(0.0, options.LeftThreshold);
        Assert.Equal(0.0, options.RightThreshold);
    }

    [Fact]
    public void Constructor_WithMaxThresholds_SummingToExactlyOne_IsValid()
    {
        // ARRANGE & ACT
        var options = new RuntimeCacheOptions(1.0, 1.0, 0.5, 0.5, TimeSpan.Zero);

        // ASSERT
        Assert.Equal(0.5, options.LeftThreshold);
        Assert.Equal(0.5, options.RightThreshold);
    }

    [Fact]
    public void Constructor_WithZeroDebounceDelay_IsValid()
    {
        // ARRANGE & ACT
        var options = new RuntimeCacheOptions(1.0, 1.0, null, null, TimeSpan.Zero);

        // ASSERT
        Assert.Equal(TimeSpan.Zero, options.DebounceDelay);
    }

    #endregion

    #region Constructor - Invalid LeftCacheSize Tests

    [Theory]
    [InlineData(-0.001)]
    [InlineData(-1.0)]
    [InlineData(double.MinValue)]
    public void Constructor_WithNegativeLeftCacheSize_ThrowsArgumentOutOfRangeException(double leftCacheSize)
    {
        // ARRANGE & ACT
        var exception = Record.Exception(() =>
            new RuntimeCacheOptions(leftCacheSize, 1.0, null, null, TimeSpan.Zero));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentOutOfRangeException>(exception);
        Assert.Contains("LeftCacheSize", exception.Message);
    }

    #endregion

    #region Constructor - Invalid RightCacheSize Tests

    [Theory]
    [InlineData(-0.001)]
    [InlineData(-1.0)]
    [InlineData(double.MinValue)]
    public void Constructor_WithNegativeRightCacheSize_ThrowsArgumentOutOfRangeException(double rightCacheSize)
    {
        // ARRANGE & ACT
        var exception = Record.Exception(() =>
            new RuntimeCacheOptions(1.0, rightCacheSize, null, null, TimeSpan.Zero));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentOutOfRangeException>(exception);
        Assert.Contains("RightCacheSize", exception.Message);
    }

    #endregion

    #region Constructor - Invalid LeftThreshold Tests

    [Theory]
    [InlineData(-0.001)]
    [InlineData(-1.0)]
    public void Constructor_WithNegativeLeftThreshold_ThrowsArgumentOutOfRangeException(double leftThreshold)
    {
        // ARRANGE & ACT
        var exception = Record.Exception(() =>
            new RuntimeCacheOptions(1.0, 1.0, leftThreshold, null, TimeSpan.Zero));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentOutOfRangeException>(exception);
        Assert.Contains("LeftThreshold", exception.Message);
    }

    [Theory]
    [InlineData(1.001)]
    [InlineData(2.0)]
    public void Constructor_WithLeftThresholdExceedingOne_ThrowsArgumentOutOfRangeException(double leftThreshold)
    {
        // ARRANGE & ACT
        var exception = Record.Exception(() =>
            new RuntimeCacheOptions(1.0, 1.0, leftThreshold, null, TimeSpan.Zero));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentOutOfRangeException>(exception);
        Assert.Contains("LeftThreshold", exception.Message);
    }

    #endregion

    #region Constructor - Invalid RightThreshold Tests

    [Theory]
    [InlineData(-0.001)]
    [InlineData(-1.0)]
    public void Constructor_WithNegativeRightThreshold_ThrowsArgumentOutOfRangeException(double rightThreshold)
    {
        // ARRANGE & ACT
        var exception = Record.Exception(() =>
            new RuntimeCacheOptions(1.0, 1.0, null, rightThreshold, TimeSpan.Zero));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentOutOfRangeException>(exception);
        Assert.Contains("RightThreshold", exception.Message);
    }

    [Theory]
    [InlineData(1.001)]
    [InlineData(2.0)]
    public void Constructor_WithRightThresholdExceedingOne_ThrowsArgumentOutOfRangeException(double rightThreshold)
    {
        // ARRANGE & ACT
        var exception = Record.Exception(() =>
            new RuntimeCacheOptions(1.0, 1.0, null, rightThreshold, TimeSpan.Zero));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentOutOfRangeException>(exception);
        Assert.Contains("RightThreshold", exception.Message);
    }

    #endregion

    #region ToSnapshot Tests

    [Fact]
    public void ToSnapshot_ReturnsSnapshotWithMatchingValues()
    {
        // ARRANGE
        var options = new RuntimeCacheOptions(
            leftCacheSize: 1.5,
            rightCacheSize: 2.0,
            leftThreshold: 0.3,
            rightThreshold: 0.4,
            debounceDelay: TimeSpan.FromMilliseconds(200)
        );

        // ACT
        var snapshot = options.ToSnapshot();

        // ASSERT
        Assert.Equal(1.5, snapshot.LeftCacheSize);
        Assert.Equal(2.0, snapshot.RightCacheSize);
        Assert.Equal(0.3, snapshot.LeftThreshold);
        Assert.Equal(0.4, snapshot.RightThreshold);
        Assert.Equal(TimeSpan.FromMilliseconds(200), snapshot.DebounceDelay);
    }

    [Fact]
    public void ToSnapshot_WithNullThresholds_ReturnsSnapshotWithNullThresholds()
    {
        // ARRANGE
        var options = new RuntimeCacheOptions(1.0, 1.0, null, null, TimeSpan.Zero);

        // ACT
        var snapshot = options.ToSnapshot();

        // ASSERT
        Assert.Null(snapshot.LeftThreshold);
        Assert.Null(snapshot.RightThreshold);
    }

    [Fact]
    public void ToSnapshot_CalledTwice_ReturnsTwoIndependentInstances()
    {
        // ARRANGE
        var options = new RuntimeCacheOptions(1.0, 1.0, null, null, TimeSpan.Zero);

        // ACT
        var snapshot1 = options.ToSnapshot();
        var snapshot2 = options.ToSnapshot();

        // ASSERT — each call returns a new object
        Assert.NotSame(snapshot1, snapshot2);
    }

    [Fact]
    public void ToSnapshot_WithZeroValues_ReturnsSnapshotWithZeroValues()
    {
        // ARRANGE
        var options = new RuntimeCacheOptions(0.0, 0.0, 0.0, 0.0, TimeSpan.Zero);

        // ACT
        var snapshot = options.ToSnapshot();

        // ASSERT
        Assert.Equal(0.0, snapshot.LeftCacheSize);
        Assert.Equal(0.0, snapshot.RightCacheSize);
        Assert.Equal(0.0, snapshot.LeftThreshold);
        Assert.Equal(0.0, snapshot.RightThreshold);
        Assert.Equal(TimeSpan.Zero, snapshot.DebounceDelay);
    }

    #endregion

    #region Constructor - Invalid Threshold Sum Tests

    [Theory]
    [InlineData(0.6, 0.5)]
    [InlineData(0.5, 0.6)]
    [InlineData(1.0, 0.001)]
    [InlineData(0.001, 1.0)]
    public void Constructor_WithThresholdSumExceedingOne_ThrowsArgumentException(
        double leftThreshold, double rightThreshold)
    {
        // ARRANGE & ACT
        var exception = Record.Exception(() =>
            new RuntimeCacheOptions(1.0, 1.0, leftThreshold, rightThreshold, TimeSpan.Zero));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentException>(exception);
        Assert.Contains("sum", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Constructor_WithOnlyLeftThreshold_DoesNotValidateSum()
    {
        // ARRANGE & ACT — only one threshold; no sum validation applies
        var exception = Record.Exception(() =>
            new RuntimeCacheOptions(1.0, 1.0, 0.99, null, TimeSpan.Zero));

        // ASSERT
        Assert.Null(exception);
    }

    [Fact]
    public void Constructor_WithOnlyRightThreshold_DoesNotValidateSum()
    {
        // ARRANGE & ACT
        var exception = Record.Exception(() =>
            new RuntimeCacheOptions(1.0, 1.0, null, 0.99, TimeSpan.Zero));

        // ASSERT
        Assert.Null(exception);
    }

    #endregion
}
