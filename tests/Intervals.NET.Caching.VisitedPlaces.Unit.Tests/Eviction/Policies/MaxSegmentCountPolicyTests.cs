using Intervals.NET.Caching.VisitedPlaces.Core;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Policies;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Pressure;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure.Helpers;

namespace Intervals.NET.Caching.VisitedPlaces.Unit.Tests.Eviction.Policies;

/// <summary>
/// Unit tests for <see cref="MaxSegmentCountPolicy{TRange,TData}"/>.
/// Validates constructor constraints, NoPressure return on non-violation,
/// and SegmentCountPressure return on violation.
/// </summary>
public sealed class MaxSegmentCountPolicyTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_WithValidMaxCount_SetsMaxCount()
    {
        // ARRANGE & ACT
        var policy = new MaxSegmentCountPolicy<int, int>(5);

        // ASSERT
        Assert.Equal(5, policy.MaxCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Constructor_WithMaxCountLessThanOne_ThrowsArgumentOutOfRangeException(int invalidMaxCount)
    {
        // ARRANGE & ACT
        var exception = Record.Exception(() => new MaxSegmentCountPolicy<int, int>(invalidMaxCount));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentOutOfRangeException>(exception);
    }

    [Fact]
    public void Constructor_WithMaxCountOfOne_IsValid()
    {
        // ARRANGE & ACT
        var exception = Record.Exception(() => new MaxSegmentCountPolicy<int, int>(1));

        // ASSERT
        Assert.Null(exception);
    }

    #endregion

    #region Evaluate Tests — No Pressure (Constraint Not Violated)

    [Fact]
    public void Evaluate_WhenCountBelowMax_ReturnsNoPressure()
    {
        // ARRANGE
        var policy = new MaxSegmentCountPolicy<int, int>(3);
        var segments = CreateSegments(2);

        // ACT
        var pressure = policy.Evaluate(segments);

        // ASSERT
        Assert.Same(NoPressure<int, int>.Instance, pressure);
    }

    [Fact]
    public void Evaluate_WhenCountEqualsMax_ReturnsNoPressure()
    {
        // ARRANGE
        var policy = new MaxSegmentCountPolicy<int, int>(3);
        var segments = CreateSegments(3);

        // ACT
        var pressure = policy.Evaluate(segments);

        // ASSERT
        Assert.Same(NoPressure<int, int>.Instance, pressure);
    }

    [Fact]
    public void Evaluate_WhenStorageEmpty_ReturnsNoPressure()
    {
        // ARRANGE
        var policy = new MaxSegmentCountPolicy<int, int>(1);
        var segments = CreateSegments(0);

        // ACT
        var pressure = policy.Evaluate(segments);

        // ASSERT
        Assert.Same(NoPressure<int, int>.Instance, pressure);
    }

    #endregion

    #region Evaluate Tests — Pressure Produced (Constraint Violated)

    [Fact]
    public void Evaluate_WhenCountExceedsMax_ReturnsPressureWithIsExceededTrue()
    {
        // ARRANGE
        var policy = new MaxSegmentCountPolicy<int, int>(3);
        var segments = CreateSegments(4);

        // ACT
        var pressure = policy.Evaluate(segments);

        // ASSERT
        Assert.True(pressure.IsExceeded);
        Assert.IsNotType<NoPressure<int, int>>(pressure);
    }

    [Fact]
    public void Evaluate_WhenCountExceedsByOne_PressureSatisfiedAfterOneReduce()
    {
        // ARRANGE
        var policy = new MaxSegmentCountPolicy<int, int>(3);
        var segments = CreateSegments(4);

        // ACT
        var pressure = policy.Evaluate(segments);

        // ASSERT — pressure is exceeded before reduction
        Assert.True(pressure.IsExceeded);

        // Reduce once — should satisfy (4 - 1 = 3 <= 3)
        pressure.Reduce(segments[0]);
        Assert.False(pressure.IsExceeded);
    }

    [Fact]
    public void Evaluate_WhenCountExceedsByMany_PressureSatisfiedAfterEnoughReduces()
    {
        // ARRANGE
        var policy = new MaxSegmentCountPolicy<int, int>(3);
        var segments = CreateSegments(7);

        // ACT
        var pressure = policy.Evaluate(segments);

        // ASSERT — need 4 reductions (7 - 4 = 3 <= 3)
        Assert.True(pressure.IsExceeded);

        for (var i = 0; i < 3; i++)
        {
            pressure.Reduce(segments[i]);
            Assert.True(pressure.IsExceeded, $"Should still be exceeded after {i + 1} reduction(s)");
        }

        pressure.Reduce(segments[3]);
        Assert.False(pressure.IsExceeded);
    }

    #endregion

    #region Helpers

    private static IReadOnlyList<CachedSegment<int, int>> CreateSegments(int count)
    {
        var result = new List<CachedSegment<int, int>>();
        for (var i = 0; i < count; i++)
        {
            var start = i * 10;
            var range = TestHelpers.CreateRange(start, start + 5);
            result.Add(new CachedSegment<int, int>(
                range,
                new ReadOnlyMemory<int>(new int[6])));
        }
        return result;
    }

    #endregion
}
