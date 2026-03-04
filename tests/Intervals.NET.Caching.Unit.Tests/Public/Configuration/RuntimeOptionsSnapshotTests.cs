using Intervals.NET.Caching.Public.Configuration;

namespace Intervals.NET.Caching.Unit.Tests.Public.Configuration;

/// <summary>
/// Unit tests for <see cref="RuntimeOptionsSnapshot"/> that verify property initialization
/// and snapshot semantics.
/// </summary>
public class RuntimeOptionsSnapshotTests
{
    #region Constructor - Property Initialization Tests

    [Fact]
    public void Constructor_WithAllValues_InitializesAllProperties()
    {
        // ARRANGE & ACT
        var snapshot = new RuntimeOptionsSnapshot(
            leftCacheSize: 1.5,
            rightCacheSize: 2.0,
            leftThreshold: 0.3,
            rightThreshold: 0.4,
            debounceDelay: TimeSpan.FromMilliseconds(200)
        );

        // ASSERT
        Assert.Equal(1.5, snapshot.LeftCacheSize);
        Assert.Equal(2.0, snapshot.RightCacheSize);
        Assert.Equal(0.3, snapshot.LeftThreshold);
        Assert.Equal(0.4, snapshot.RightThreshold);
        Assert.Equal(TimeSpan.FromMilliseconds(200), snapshot.DebounceDelay);
    }

    [Fact]
    public void Constructor_WithNullThresholds_ThresholdsAreNull()
    {
        // ARRANGE & ACT
        var snapshot = new RuntimeOptionsSnapshot(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            leftThreshold: null,
            rightThreshold: null,
            debounceDelay: TimeSpan.Zero
        );

        // ASSERT
        Assert.Null(snapshot.LeftThreshold);
        Assert.Null(snapshot.RightThreshold);
    }

    [Fact]
    public void Constructor_WithZeroValues_InitializesZeroProperties()
    {
        // ARRANGE & ACT
        var snapshot = new RuntimeOptionsSnapshot(0.0, 0.0, 0.0, 0.0, TimeSpan.Zero);

        // ASSERT
        Assert.Equal(0.0, snapshot.LeftCacheSize);
        Assert.Equal(0.0, snapshot.RightCacheSize);
        Assert.Equal(0.0, snapshot.LeftThreshold);
        Assert.Equal(0.0, snapshot.RightThreshold);
        Assert.Equal(TimeSpan.Zero, snapshot.DebounceDelay);
    }

    [Fact]
    public void Constructor_WithOnlyLeftThreshold_RightThresholdIsNull()
    {
        // ARRANGE & ACT
        var snapshot = new RuntimeOptionsSnapshot(1.0, 1.0, 0.25, null, TimeSpan.Zero);

        // ASSERT
        Assert.Equal(0.25, snapshot.LeftThreshold);
        Assert.Null(snapshot.RightThreshold);
    }

    [Fact]
    public void Constructor_WithOnlyRightThreshold_LeftThresholdIsNull()
    {
        // ARRANGE & ACT
        var snapshot = new RuntimeOptionsSnapshot(1.0, 1.0, null, 0.25, TimeSpan.Zero);

        // ASSERT
        Assert.Null(snapshot.LeftThreshold);
        Assert.Equal(0.25, snapshot.RightThreshold);
    }

    [Fact]
    public void Constructor_WithLargeDebounceDelay_StoredCorrectly()
    {
        // ARRANGE & ACT
        var delay = TimeSpan.FromSeconds(30);
        var snapshot = new RuntimeOptionsSnapshot(1.0, 1.0, null, null, delay);

        // ASSERT
        Assert.Equal(delay, snapshot.DebounceDelay);
    }

    #endregion

    #region Property Immutability Tests

    [Fact]
    public void Properties_AreReadOnly_NoSetterAvailable()
    {
        // ASSERT — verify all properties are get-only (compile-time guarantee via type system)
        var type = typeof(RuntimeOptionsSnapshot);

        foreach (var prop in type.GetProperties())
        {
            Assert.True(prop.CanRead, $"Property '{prop.Name}' should be readable.");
            Assert.False(prop.CanWrite, $"Property '{prop.Name}' should NOT be writable.");
        }
    }

    #endregion

    #region Type Tests

    [Fact]
    public void RuntimeOptionsSnapshot_IsSealed()
    {
        // ASSERT — ensure no subclassing
        Assert.True(typeof(RuntimeOptionsSnapshot).IsSealed);
    }

    #endregion
}
