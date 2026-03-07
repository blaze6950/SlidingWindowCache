using Intervals.NET.Caching.VisitedPlaces.Core;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Pressure;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure.Helpers;

namespace Intervals.NET.Caching.VisitedPlaces.Unit.Tests.Eviction.Pressure;

/// <summary>
/// Unit tests for <see cref="SegmentCountPressure{TRange,TData}"/>.
/// Validates IsExceeded semantics and Reduce decrement behavior.
/// </summary>
public sealed class SegmentCountPressureTests
{
    #region IsExceeded Tests

    [Fact]
    public void IsExceeded_WhenCurrentCountAboveMax_ReturnsTrue()
    {
        // ARRANGE
        var pressure = new SegmentCountPressure<int, int>(currentCount: 5, maxCount: 3);

        // ACT & ASSERT
        Assert.True(pressure.IsExceeded);
    }

    [Fact]
    public void IsExceeded_WhenCurrentCountEqualsMax_ReturnsFalse()
    {
        // ARRANGE
        var pressure = new SegmentCountPressure<int, int>(currentCount: 3, maxCount: 3);

        // ACT & ASSERT
        Assert.False(pressure.IsExceeded);
    }

    [Fact]
    public void IsExceeded_WhenCurrentCountBelowMax_ReturnsFalse()
    {
        // ARRANGE
        var pressure = new SegmentCountPressure<int, int>(currentCount: 1, maxCount: 3);

        // ACT & ASSERT
        Assert.False(pressure.IsExceeded);
    }

    #endregion

    #region Reduce Tests

    [Fact]
    public void Reduce_DecrementsCurrentCount()
    {
        // ARRANGE — count=4, max=3 → exceeded
        var pressure = new SegmentCountPressure<int, int>(currentCount: 4, maxCount: 3);
        var segment = CreateSegment(0, 5);

        // ACT
        pressure.Reduce(segment);

        // ASSERT — count=3 → not exceeded
        Assert.False(pressure.IsExceeded);
    }

    [Fact]
    public void Reduce_MultipleCallsDecrementProgressively()
    {
        // ARRANGE — count=6, max=3 → need 3 reductions
        var pressure = new SegmentCountPressure<int, int>(currentCount: 6, maxCount: 3);
        var segment = CreateSegment(0, 5);

        // ACT & ASSERT
        pressure.Reduce(segment); // 5 > 3 → true
        Assert.True(pressure.IsExceeded);

        pressure.Reduce(segment); // 4 > 3 → true
        Assert.True(pressure.IsExceeded);

        pressure.Reduce(segment); // 3 <= 3 → false
        Assert.False(pressure.IsExceeded);
    }

    [Fact]
    public void Reduce_IsOrderIndependent_AnySegmentDecrementsSameAmount()
    {
        // ARRANGE
        var pressure = new SegmentCountPressure<int, int>(currentCount: 5, maxCount: 3);

        // Different-sized segments should all decrement by exactly 1
        var small = CreateSegment(0, 1);   // span 2
        var large = CreateSegment(0, 99);  // span 100

        // ACT
        pressure.Reduce(small); // 4 > 3
        Assert.True(pressure.IsExceeded);

        pressure.Reduce(large); // 3 <= 3
        Assert.False(pressure.IsExceeded);
    }

    #endregion

    #region Helpers

    private static CachedSegment<int, int> CreateSegment(int start, int end)
    {
        var range = TestHelpers.CreateRange(start, end);
        return new CachedSegment<int, int>(
            range,
            new ReadOnlyMemory<int>(new int[end - start + 1]),
            new SegmentStatistics(DateTime.UtcNow));
    }

    #endregion
}
