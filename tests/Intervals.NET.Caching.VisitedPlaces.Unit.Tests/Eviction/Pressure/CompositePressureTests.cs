using Intervals.NET.Caching.VisitedPlaces.Core;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Pressure;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure.Helpers;

namespace Intervals.NET.Caching.VisitedPlaces.Unit.Tests.Eviction.Pressure;

/// <summary>
/// Unit tests for <see cref="CompositePressure{TRange,TData}"/>.
/// Validates OR semantics for IsExceeded and Reduce propagation to all children.
/// </summary>
public sealed class CompositePressureTests
{
    #region IsExceeded — OR Semantics Tests

    [Fact]
    public void IsExceeded_WhenAllChildrenExceeded_ReturnsTrue()
    {
        // ARRANGE
        var p1 = new SegmentCountPressure<int, int>(currentCount: 5, maxCount: 3); // exceeded
        var p2 = new SegmentCountPressure<int, int>(currentCount: 4, maxCount: 2); // exceeded
        var composite = new CompositePressure<int, int>([p1, p2]);

        // ACT & ASSERT
        Assert.True(composite.IsExceeded);
    }

    [Fact]
    public void IsExceeded_WhenOneChildExceeded_ReturnsTrue()
    {
        // ARRANGE
        var exceeded = new SegmentCountPressure<int, int>(currentCount: 5, maxCount: 3); // exceeded
        var satisfied = new SegmentCountPressure<int, int>(currentCount: 2, maxCount: 3); // not exceeded
        var composite = new CompositePressure<int, int>([exceeded, satisfied]);

        // ACT & ASSERT
        Assert.True(composite.IsExceeded);
    }

    [Fact]
    public void IsExceeded_WhenNoChildrenExceeded_ReturnsFalse()
    {
        // ARRANGE
        var p1 = new SegmentCountPressure<int, int>(currentCount: 2, maxCount: 3); // not exceeded
        var p2 = new SegmentCountPressure<int, int>(currentCount: 1, maxCount: 3); // not exceeded
        var composite = new CompositePressure<int, int>([p1, p2]);

        // ACT & ASSERT
        Assert.False(composite.IsExceeded);
    }

    #endregion

    #region Reduce Propagation Tests

    [Fact]
    public void Reduce_ForwardsToAllChildren()
    {
        // ARRANGE — both exceeded: p1(4>3), p2(5>3)
        var p1 = new SegmentCountPressure<int, int>(currentCount: 4, maxCount: 3); // 1 over
        var p2 = new SegmentCountPressure<int, int>(currentCount: 5, maxCount: 3); // 2 over
        var composite = new CompositePressure<int, int>([p1, p2]);
        var segment = CreateSegment(0, 5);

        // ACT — reduce once
        composite.Reduce(segment);

        // ASSERT — p1 satisfied (3<=3), p2 still exceeded (4>3) → composite still exceeded
        Assert.False(p1.IsExceeded);
        Assert.True(p2.IsExceeded);
        Assert.True(composite.IsExceeded);
    }

    [Fact]
    public void Reduce_UntilAllSatisfied_CompositeBecomesFalse()
    {
        // ARRANGE — p1(4>3), p2(5>3)
        var p1 = new SegmentCountPressure<int, int>(currentCount: 4, maxCount: 3);
        var p2 = new SegmentCountPressure<int, int>(currentCount: 5, maxCount: 3);
        var composite = new CompositePressure<int, int>([p1, p2]);
        var segment = CreateSegment(0, 5);

        // ACT — reduce twice
        composite.Reduce(segment); // p1: 3<=3 (sat), p2: 4>3 (exc)
        Assert.True(composite.IsExceeded);

        composite.Reduce(segment); // p1: 2<=3 (sat), p2: 3<=3 (sat)
        Assert.False(composite.IsExceeded);
    }

    #endregion

    #region Mixed Pressure Type Tests

    [Fact]
    public void Reduce_WithMixedPressureTypes_BothTrackedCorrectly()
    {
        // ARRANGE — count pressure + NoPressure (already satisfied)
        var countPressure = new SegmentCountPressure<int, int>(currentCount: 4, maxCount: 3);
        var noPressure = NoPressure<int, int>.Instance;
        var composite = new CompositePressure<int, int>([countPressure, noPressure]);
        var segment = CreateSegment(0, 5);

        // ACT & ASSERT — composite exceeded because countPressure is exceeded
        Assert.True(composite.IsExceeded);

        composite.Reduce(segment); // count: 3<=3 → satisfied
        Assert.False(composite.IsExceeded);
    }

    #endregion

    #region Helpers

    private static CachedSegment<int, int> CreateSegment(int start, int end)
    {
        var range = TestHelpers.CreateRange(start, end);
        return new CachedSegment<int, int>(
            range,
            new ReadOnlyMemory<int>(new int[end - start + 1]));
    }

    #endregion
}
