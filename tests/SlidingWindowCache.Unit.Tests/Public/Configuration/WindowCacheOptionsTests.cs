using SlidingWindowCache.Public.Configuration;

namespace SlidingWindowCache.Unit.Tests.Public.Configuration;

/// <summary>
/// Unit tests for WindowCacheOptions that verify validation logic, property initialization,
/// and edge cases for cache configuration.
/// </summary>
public class WindowCacheOptionsTests
{
    #region Constructor - Valid Parameters Tests

    [Fact]
    public void Constructor_WithValidParameters_InitializesAllProperties()
    {
        // ARRANGE & ACT
        var options = new WindowCacheOptions(
            leftCacheSize: 1.5,
            rightCacheSize: 2.0,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0.3,
            rightThreshold: 0.4,
            debounceDelay: TimeSpan.FromMilliseconds(200)
        );

        // ASSERT
        Assert.Equal(1.5, options.LeftCacheSize);
        Assert.Equal(2.0, options.RightCacheSize);
        Assert.Equal(UserCacheReadMode.Snapshot, options.ReadMode);
        Assert.Equal(0.3, options.LeftThreshold);
        Assert.Equal(0.4, options.RightThreshold);
        Assert.Equal(TimeSpan.FromMilliseconds(200), options.DebounceDelay);
    }

    [Fact]
    public void Constructor_WithMinimalParameters_UsesDefaults()
    {
        // ARRANGE & ACT
        var options = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot
        );

        // ASSERT
        Assert.Equal(1.0, options.LeftCacheSize);
        Assert.Equal(1.0, options.RightCacheSize);
        Assert.Equal(UserCacheReadMode.Snapshot, options.ReadMode);
        Assert.Null(options.LeftThreshold);
        Assert.Null(options.RightThreshold);
        Assert.Equal(TimeSpan.FromMilliseconds(100), options.DebounceDelay); // Default
    }

    [Fact]
    public void Constructor_WithZeroCacheSizes_IsValid()
    {
        // ARRANGE & ACT
        var options = new WindowCacheOptions(
            leftCacheSize: 0.0,
            rightCacheSize: 0.0,
            readMode: UserCacheReadMode.Snapshot
        );

        // ASSERT
        Assert.Equal(0.0, options.LeftCacheSize);
        Assert.Equal(0.0, options.RightCacheSize);
    }

    [Fact]
    public void Constructor_WithZeroThresholds_IsValid()
    {
        // ARRANGE & ACT
        var options = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0.0,
            rightThreshold: 0.0
        );

        // ASSERT
        Assert.Equal(0.0, options.LeftThreshold);
        Assert.Equal(0.0, options.RightThreshold);
    }

    [Fact]
    public void Constructor_WithNullThresholds_SetsThresholdsToNull()
    {
        // ARRANGE & ACT
        var options = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: null,
            rightThreshold: null
        );

        // ASSERT
        Assert.Null(options.LeftThreshold);
        Assert.Null(options.RightThreshold);
    }

    [Fact]
    public void Constructor_WithOnlyLeftThreshold_IsValid()
    {
        // ARRANGE & ACT
        var options = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0.2,
            rightThreshold: null
        );

        // ASSERT
        Assert.Equal(0.2, options.LeftThreshold);
        Assert.Null(options.RightThreshold);
    }

    [Fact]
    public void Constructor_WithOnlyRightThreshold_IsValid()
    {
        // ARRANGE & ACT
        var options = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: null,
            rightThreshold: 0.2
        );

        // ASSERT
        Assert.Null(options.LeftThreshold);
        Assert.Equal(0.2, options.RightThreshold);
    }

    [Fact]
    public void Constructor_WithLargeCacheSizes_IsValid()
    {
        // ARRANGE & ACT
        var options = new WindowCacheOptions(
            leftCacheSize: 100.0,
            rightCacheSize: 200.0,
            readMode: UserCacheReadMode.Snapshot
        );

        // ASSERT
        Assert.Equal(100.0, options.LeftCacheSize);
        Assert.Equal(200.0, options.RightCacheSize);
    }

    [Fact]
    public void Constructor_WithLargeThresholds_IsValid()
    {
        // ARRANGE & ACT - Large individual thresholds are valid if sum <= 1.0
        var options = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0.49,
            rightThreshold: 0.5  // Sum = 0.99 (valid)
        );

        // ASSERT
        Assert.Equal(0.49, options.LeftThreshold);
        Assert.Equal(0.5, options.RightThreshold);
    }

    [Fact]
    public void Constructor_WithVerySmallDebounceDelay_IsValid()
    {
        // ARRANGE & ACT
        var options = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot,
            debounceDelay: TimeSpan.FromMilliseconds(1)
        );

        // ASSERT
        Assert.Equal(TimeSpan.FromMilliseconds(1), options.DebounceDelay);
    }

    [Fact]
    public void Constructor_WithVeryLargeDebounceDelay_IsValid()
    {
        // ARRANGE & ACT
        var options = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot,
            debounceDelay: TimeSpan.FromSeconds(10)
        );

        // ASSERT
        Assert.Equal(TimeSpan.FromSeconds(10), options.DebounceDelay);
    }

    [Fact]
    public void Constructor_WithSnapshotReadMode_SetsCorrectly()
    {
        // ARRANGE & ACT
        var options = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot
        );

        // ASSERT
        Assert.Equal(UserCacheReadMode.Snapshot, options.ReadMode);
    }

    [Fact]
    public void Constructor_WithCopyOnReadMode_SetsCorrectly()
    {
        // ARRANGE & ACT
        var options = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.CopyOnRead
        );

        // ASSERT
        Assert.Equal(UserCacheReadMode.CopyOnRead, options.ReadMode);
    }

    #endregion

    #region Constructor - Validation Tests

    [Fact]
    public void Constructor_WithNegativeLeftCacheSize_ThrowsArgumentOutOfRangeException()
    {
        // ARRANGE, ACT & ASSERT
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WindowCacheOptions(
                leftCacheSize: -1.0,
                rightCacheSize: 1.0,
                readMode: UserCacheReadMode.Snapshot
            )
        );

        Assert.Equal("leftCacheSize", exception.ParamName);
        Assert.Contains("LeftCacheSize must be greater than or equal to 0", exception.Message);
    }

    [Fact]
    public void Constructor_WithNegativeRightCacheSize_ThrowsArgumentOutOfRangeException()
    {
        // ARRANGE, ACT & ASSERT
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WindowCacheOptions(
                leftCacheSize: 1.0,
                rightCacheSize: -1.0,
                readMode: UserCacheReadMode.Snapshot
            )
        );

        Assert.Equal("rightCacheSize", exception.ParamName);
        Assert.Contains("RightCacheSize must be greater than or equal to 0", exception.Message);
    }

    [Fact]
    public void Constructor_WithNegativeLeftThreshold_ThrowsArgumentOutOfRangeException()
    {
        // ARRANGE, ACT & ASSERT
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WindowCacheOptions(
                leftCacheSize: 1.0,
                rightCacheSize: 1.0,
                readMode: UserCacheReadMode.Snapshot,
                leftThreshold: -0.1
            )
        );

        Assert.Equal("leftThreshold", exception.ParamName);
        Assert.Contains("LeftThreshold must be greater than or equal to 0", exception.Message);
    }

    [Fact]
    public void Constructor_WithNegativeRightThreshold_ThrowsArgumentOutOfRangeException()
    {
        // ARRANGE, ACT & ASSERT
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WindowCacheOptions(
                leftCacheSize: 1.0,
                rightCacheSize: 1.0,
                readMode: UserCacheReadMode.Snapshot,
                rightThreshold: -0.1
            )
        );

        Assert.Equal("rightThreshold", exception.ParamName);
        Assert.Contains("RightThreshold must be greater than or equal to 0", exception.Message);
    }

    [Fact]
    public void Constructor_WithVerySmallNegativeLeftCacheSize_ThrowsArgumentOutOfRangeException()
    {
        // ARRANGE, ACT & ASSERT
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WindowCacheOptions(
                leftCacheSize: -0.001,
                rightCacheSize: 1.0,
                readMode: UserCacheReadMode.Snapshot
            )
        );

        Assert.Equal("leftCacheSize", exception.ParamName);
    }

    [Fact]
    public void Constructor_WithVerySmallNegativeRightCacheSize_ThrowsArgumentOutOfRangeException()
    {
        // ARRANGE, ACT & ASSERT
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WindowCacheOptions(
                leftCacheSize: 1.0,
                rightCacheSize: -0.001,
                readMode: UserCacheReadMode.Snapshot
            )
        );

        Assert.Equal("rightCacheSize", exception.ParamName);
    }

    #endregion

    #region Constructor - Threshold Sum Validation Tests

    [Fact]
    public void Constructor_WithThresholdSumExceedingOne_ThrowsArgumentException()
    {
        // ARRANGE, ACT & ASSERT
        var exception = Record.Exception(() =>
            new WindowCacheOptions(
                leftCacheSize: 1.0,
                rightCacheSize: 1.0,
                readMode: UserCacheReadMode.Snapshot,
                leftThreshold: 0.6,
                rightThreshold: 0.5  // Sum = 1.1
            )
        );

        Assert.NotNull(exception);
        Assert.IsType<ArgumentException>(exception);
        var argException = (ArgumentException)exception;
        Assert.Contains("sum", argException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1.1", argException.Message); // Actual sum in message
        Assert.Contains("exceed 1.0", argException.Message);
    }

    [Fact]
    public void Constructor_WithThresholdSumEqualToOne_IsValid()
    {
        // ARRANGE & ACT
        var options = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0.5,
            rightThreshold: 0.5  // Sum = 1.0 (boundary case - valid)
        );

        // ASSERT
        Assert.Equal(0.5, options.LeftThreshold);
        Assert.Equal(0.5, options.RightThreshold);
    }

    [Fact]
    public void Constructor_WithThresholdSumJustBelowOne_IsValid()
    {
        // ARRANGE & ACT
        var options = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0.49,
            rightThreshold: 0.5  // Sum = 0.99
        );

        // ASSERT
        Assert.Equal(0.49, options.LeftThreshold);
        Assert.Equal(0.5, options.RightThreshold);
    }

    [Fact]
    public void Constructor_WithBothThresholdsOne_ThrowsArgumentException()
    {
        // ARRANGE, ACT & ASSERT
        var exception = Record.Exception(() =>
            new WindowCacheOptions(
                leftCacheSize: 1.0,
                rightCacheSize: 1.0,
                readMode: UserCacheReadMode.Snapshot,
                leftThreshold: 1.0,
                rightThreshold: 1.0  // Sum = 2.0
            )
        );

        Assert.NotNull(exception);
        Assert.IsType<ArgumentException>(exception);
        var argException = (ArgumentException)exception;
        Assert.Contains("sum", argException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2.0", argException.Message); // Actual sum
    }

    [Fact]
    public void Constructor_WithOnlyLeftThresholdEqualToOne_IsValid()
    {
        // ARRANGE & ACT - Only one threshold, even if 1.0, is valid (sum check only applies when both are set)
        var options = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 1.0,
            rightThreshold: null  // Sum check doesn't apply
        );

        // ASSERT
        Assert.Equal(1.0, options.LeftThreshold);
        Assert.Null(options.RightThreshold);
    }

    [Fact]
    public void Constructor_WithOnlyRightThresholdEqualToOne_IsValid()
    {
        // ARRANGE & ACT
        var options = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: null,
            rightThreshold: 1.0  // Sum check doesn't apply
        );

        // ASSERT
        Assert.Null(options.LeftThreshold);
        Assert.Equal(1.0, options.RightThreshold);
    }

    [Fact]
    public void Constructor_WithHighButValidThresholdSum_IsValid()
    {
        // ARRANGE & ACT
        var options = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0.45,
            rightThreshold: 0.45  // Sum = 0.9 (high but valid)
        );

        // ASSERT
        Assert.Equal(0.45, options.LeftThreshold);
        Assert.Equal(0.45, options.RightThreshold);
    }

    [Fact]
    public void Constructor_WithSlightlyExceedingThresholdSum_ThrowsArgumentException()
    {
        // ARRANGE, ACT & ASSERT
        var exception = Record.Exception(() =>
            new WindowCacheOptions(
                leftCacheSize: 1.0,
                rightCacheSize: 1.0,
                readMode: UserCacheReadMode.Snapshot,
                leftThreshold: 0.50001,
                rightThreshold: 0.5  // Sum = 1.00001 (just over)
            )
        );

        Assert.NotNull(exception);
        Assert.IsType<ArgumentException>(exception);
    }

    #endregion

    #region Record Equality Tests

    [Fact]
    public void RecordEquality_WithSameValues_AreEqual()
    {
        // ARRANGE
        var options1 = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 2.0,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0.3,
            rightThreshold: 0.4,
            debounceDelay: TimeSpan.FromMilliseconds(100)
        );

        var options2 = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 2.0,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0.3,
            rightThreshold: 0.4,
            debounceDelay: TimeSpan.FromMilliseconds(100)
        );

        // ACT & ASSERT
        Assert.Equal(options1, options2);
        Assert.True(options1 == options2);
        Assert.False(options1 != options2);
    }

    [Fact]
    public void RecordEquality_WithDifferentLeftCacheSize_AreNotEqual()
    {
        // ARRANGE
        var options1 = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot
        );

        var options2 = new WindowCacheOptions(
            leftCacheSize: 2.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot
        );

        // ACT & ASSERT
        Assert.NotEqual(options1, options2);
        Assert.False(options1 == options2);
        Assert.True(options1 != options2);
    }

    [Fact]
    public void RecordEquality_WithDifferentRightCacheSize_AreNotEqual()
    {
        // ARRANGE
        var options1 = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot
        );

        var options2 = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 2.0,
            readMode: UserCacheReadMode.Snapshot
        );

        // ACT & ASSERT
        Assert.NotEqual(options1, options2);
    }

    [Fact]
    public void RecordEquality_WithDifferentReadMode_AreNotEqual()
    {
        // ARRANGE
        var options1 = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot
        );

        var options2 = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.CopyOnRead
        );

        // ACT & ASSERT
        Assert.NotEqual(options1, options2);
    }

    [Fact]
    public void RecordEquality_WithDifferentThresholds_AreNotEqual()
    {
        // ARRANGE
        var options1 = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0.2
        );

        var options2 = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0.3
        );

        // ACT & ASSERT
        Assert.NotEqual(options1, options2);
    }

    [Fact]
    public void RecordEquality_WithDifferentRebalanceQueueCapacity_AreNotEqual()
    {
        // ARRANGE
        var options1 = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot,
            rebalanceQueueCapacity: null
        );

        var options2 = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot,
            rebalanceQueueCapacity: 5
        );

        // ACT & ASSERT
        Assert.NotEqual(options1, options2);
        Assert.False(options1 == options2);
        Assert.True(options1 != options2);
    }

    [Fact]
    public void RecordEquality_WithDifferentDebounceDelay_AreNotEqual()
    {
        // ARRANGE
        var options1 = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot,
            debounceDelay: TimeSpan.FromMilliseconds(100)
        );

        var options2 = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot,
            debounceDelay: TimeSpan.FromMilliseconds(200)
        );

        // ACT & ASSERT
        Assert.NotEqual(options1, options2);
    }

    [Fact]
    public void GetHashCode_WithSameValues_ReturnsSameHashCode()
    {
        // ARRANGE
        var options1 = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 2.0,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0.3,
            rightThreshold: 0.4
        );

        var options2 = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 2.0,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0.3,
            rightThreshold: 0.4
        );

        // ACT & ASSERT
        Assert.Equal(options1.GetHashCode(), options2.GetHashCode());
    }

    #endregion

    #region Edge Cases and Boundary Tests

    [Fact]
    public void Constructor_WithBothCacheSizesZero_IsValid()
    {
        // ARRANGE & ACT
        var options = new WindowCacheOptions(
            leftCacheSize: 0.0,
            rightCacheSize: 0.0,
            readMode: UserCacheReadMode.Snapshot
        );

        // ASSERT
        Assert.Equal(0.0, options.LeftCacheSize);
        Assert.Equal(0.0, options.RightCacheSize);
    }

    [Fact]
    public void Constructor_WithBothThresholdsNull_IsValid()
    {
        // ARRANGE & ACT
        var options = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: null,
            rightThreshold: null
        );

        // ASSERT
        Assert.Null(options.LeftThreshold);
        Assert.Null(options.RightThreshold);
    }

    [Fact]
    public void Constructor_WithZeroDebounceDelay_IsValid()
    {
        // ARRANGE & ACT
        var options = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot,
            debounceDelay: TimeSpan.Zero
        );

        // ASSERT
        Assert.Equal(TimeSpan.Zero, options.DebounceDelay);
    }

    [Fact]
    public void Constructor_WithNullDebounceDelay_UsesDefault()
    {
        // ARRANGE & ACT
        var options = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot,
            debounceDelay: null
        );

        // ASSERT
        Assert.Equal(TimeSpan.FromMilliseconds(100), options.DebounceDelay);
    }

    [Fact]
    public void Constructor_WithVeryLargeCacheSizes_IsValid()
    {
        // ARRANGE & ACT
        var options = new WindowCacheOptions(
            leftCacheSize: double.MaxValue,
            rightCacheSize: double.MaxValue,
            readMode: UserCacheReadMode.Snapshot
        );

        // ASSERT
        Assert.Equal(double.MaxValue, options.LeftCacheSize);
        Assert.Equal(double.MaxValue, options.RightCacheSize);
    }

    [Fact]
    public void Constructor_WithVerySmallPositiveValues_IsValid()
    {
        // ARRANGE & ACT
        var options = new WindowCacheOptions(
            leftCacheSize: 0.0001,
            rightCacheSize: 0.0001,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0.0001,
            rightThreshold: 0.0001
        );

        // ASSERT
        Assert.Equal(0.0001, options.LeftCacheSize);
        Assert.Equal(0.0001, options.RightCacheSize);
        Assert.Equal(0.0001, options.LeftThreshold);
        Assert.Equal(0.0001, options.RightThreshold);
    }

    [Fact]
    public void Constructor_WithLeftThresholdAboveOne_ThrowsArgumentOutOfRangeException()
    {
        // ARRANGE, ACT & ASSERT
        var exception = Record.Exception(() => new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 1.01,
            rightThreshold: null
        ));

        Assert.NotNull(exception);
        Assert.IsType<ArgumentOutOfRangeException>(exception);
        var argException = (ArgumentOutOfRangeException)exception;
        Assert.Equal("leftThreshold", argException.ParamName);
    }

    [Fact]
    public void Constructor_WithRightThresholdAboveOne_ThrowsArgumentOutOfRangeException()
    {
        // ARRANGE, ACT & ASSERT
        var exception = Record.Exception(() => new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: null,
            rightThreshold: 1.01
        ));

        Assert.NotNull(exception);
        Assert.IsType<ArgumentOutOfRangeException>(exception);
        var argException = (ArgumentOutOfRangeException)exception;
        Assert.Equal("rightThreshold", argException.ParamName);
    }

    #endregion

    #region Documentation and Usage Scenario Tests

    [Fact]
    public void Constructor_TypicalCacheScenario_WorksAsExpected()
    {
        // ARRANGE & ACT - Typical sliding window cache with symmetric caching
        var options = new WindowCacheOptions(
            leftCacheSize: 1.0,  // Cache same size as requested range on left
            rightCacheSize: 1.0, // Cache same size as requested range on right
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0.2,  // Rebalance when 20% of cache remains
            rightThreshold: 0.2,
            debounceDelay: TimeSpan.FromMilliseconds(50)
        );

        // ASSERT
        Assert.Equal(1.0, options.LeftCacheSize);
        Assert.Equal(1.0, options.RightCacheSize);
        Assert.Equal(0.2, options.LeftThreshold);
        Assert.Equal(0.2, options.RightThreshold);
    }

    [Fact]
    public void Constructor_ForwardOnlyScenario_WorksAsExpected()
    {
        // ARRANGE & ACT - Optimized for forward-only access (e.g., video streaming)
        var options = new WindowCacheOptions(
            leftCacheSize: 0.0,  // No left cache needed
            rightCacheSize: 2.0, // Large right cache for forward access
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: null,
            rightThreshold: 0.3
        );

        // ASSERT
        Assert.Equal(0.0, options.LeftCacheSize);
        Assert.Equal(2.0, options.RightCacheSize);
        Assert.Null(options.LeftThreshold);
        Assert.Equal(0.3, options.RightThreshold);
    }

    [Fact]
    public void Constructor_MinimalRebalanceScenario_WorksAsExpected()
    {
        // ARRANGE & ACT - Disable automatic rebalancing
        var options = new WindowCacheOptions(
            leftCacheSize: 0.5,
            rightCacheSize: 0.5,
            readMode: UserCacheReadMode.CopyOnRead,
            leftThreshold: null,  // Disable left threshold
            rightThreshold: null  // Disable right threshold
        );

        // ASSERT
        Assert.Null(options.LeftThreshold);
        Assert.Null(options.RightThreshold);
    }

    #endregion

    #region Constructor - RebalanceQueueCapacity Tests

    [Fact]
    public void Constructor_WithNullRebalanceQueueCapacity_UsesUnboundedStrategy()
    {
        // ARRANGE & ACT
        var options = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 2.0,
            readMode: UserCacheReadMode.Snapshot,
            rebalanceQueueCapacity: null
        );

        // ASSERT
        Assert.Null(options.RebalanceQueueCapacity);
    }

    [Fact]
    public void Constructor_WithValidRebalanceQueueCapacity_UsesBoundedStrategy()
    {
        // ARRANGE & ACT
        var options = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 2.0,
            readMode: UserCacheReadMode.Snapshot,
            rebalanceQueueCapacity: 10
        );

        // ASSERT
        Assert.Equal(10, options.RebalanceQueueCapacity);
    }

    [Fact]
    public void Constructor_WithRebalanceQueueCapacityOne_IsValid()
    {
        // ARRANGE & ACT
        var options = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 2.0,
            readMode: UserCacheReadMode.Snapshot,
            rebalanceQueueCapacity: 1
        );

        // ASSERT
        Assert.Equal(1, options.RebalanceQueueCapacity);
    }

    [Fact]
    public void Constructor_WithRebalanceQueueCapacityZero_ThrowsArgumentOutOfRangeException()
    {
        // ARRANGE & ACT & ASSERT
        var exception = Record.Exception(() => new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 2.0,
            readMode: UserCacheReadMode.Snapshot,
            rebalanceQueueCapacity: 0
        ));

        Assert.NotNull(exception);
        Assert.IsType<ArgumentOutOfRangeException>(exception);
        var argException = (ArgumentOutOfRangeException)exception;
        Assert.Equal("rebalanceQueueCapacity", argException.ParamName);
    }

    [Fact]
    public void Constructor_WithNegativeRebalanceQueueCapacity_ThrowsArgumentOutOfRangeException()
    {
        // ARRANGE & ACT & ASSERT
        var exception = Record.Exception(() => new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 2.0,
            readMode: UserCacheReadMode.Snapshot,
            rebalanceQueueCapacity: -5
        ));

        Assert.NotNull(exception);
        Assert.IsType<ArgumentOutOfRangeException>(exception);
        var argException = (ArgumentOutOfRangeException)exception;
        Assert.Equal("rebalanceQueueCapacity", argException.ParamName);
    }

    [Fact]
    public void Constructor_WithDefaultParameters_RebalanceQueueCapacityIsNull()
    {
        // ARRANGE & ACT - Test that default is null (unbounded strategy)
        var options = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 2.0,
            readMode: UserCacheReadMode.Snapshot
        );

        // ASSERT
        Assert.Null(options.RebalanceQueueCapacity);
    }

    #endregion
}
