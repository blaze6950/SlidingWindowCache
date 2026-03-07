using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Caching.VisitedPlaces.Core;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Policies;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Pressure;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure.Helpers;

namespace Intervals.NET.Caching.VisitedPlaces.Unit.Tests.Eviction.Policies;

/// <summary>
/// Unit tests for <see cref="MaxTotalSpanPolicy{TRange,TData,TDomain}"/>.
/// Validates constructor constraints, NoPressure return on non-violation,
/// and TotalSpanPressure return on violation.
/// </summary>
public sealed class MaxTotalSpanPolicyTests
{
    private readonly IntegerFixedStepDomain _domain = TestHelpers.CreateIntDomain();

    #region Constructor Tests

    [Fact]
    public void Constructor_WithValidParameters_SetsMaxTotalSpan()
    {
        // ARRANGE & ACT
        var policy = new MaxTotalSpanPolicy<int, int, IntegerFixedStepDomain>(100, _domain);

        // ASSERT
        Assert.Equal(100, policy.MaxTotalSpan);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_WithMaxTotalSpanLessThanOne_ThrowsArgumentOutOfRangeException(int invalid)
    {
        // ARRANGE & ACT
        var exception = Record.Exception(() =>
            new MaxTotalSpanPolicy<int, int, IntegerFixedStepDomain>(invalid, _domain));

        // ASSERT
        Assert.IsType<ArgumentOutOfRangeException>(exception);
    }

    #endregion

    #region Evaluate Tests — No Pressure (Constraint Not Violated)

    [Fact]
    public void Evaluate_WhenTotalSpanBelowMax_ReturnsNoPressure()
    {
        // ARRANGE
        var policy = new MaxTotalSpanPolicy<int, int, IntegerFixedStepDomain>(50, _domain);
        var segments = new[] { CreateSegment(0, 9) }; // span 10 <= 50

        // ACT
        var pressure = policy.Evaluate(segments);

        // ASSERT
        Assert.Same(NoPressure<int, int>.Instance, pressure);
    }

    [Fact]
    public void Evaluate_WhenTotalSpanEqualsMax_ReturnsNoPressure()
    {
        // ARRANGE
        var policy = new MaxTotalSpanPolicy<int, int, IntegerFixedStepDomain>(10, _domain);
        var segments = new[] { CreateSegment(0, 9) }; // span 10 == 10

        // ACT
        var pressure = policy.Evaluate(segments);

        // ASSERT
        Assert.Same(NoPressure<int, int>.Instance, pressure);
    }

    [Fact]
    public void Evaluate_WithEmptyStorage_ReturnsNoPressure()
    {
        // ARRANGE
        var policy = new MaxTotalSpanPolicy<int, int, IntegerFixedStepDomain>(1, _domain);
        var segments = Array.Empty<CachedSegment<int, int>>();

        // ACT
        var pressure = policy.Evaluate(segments);

        // ASSERT
        Assert.Same(NoPressure<int, int>.Instance, pressure);
    }

    #endregion

    #region Evaluate Tests — Pressure Produced (Constraint Violated)

    [Fact]
    public void Evaluate_WhenTotalSpanExceedsMax_ReturnsPressureWithIsExceededTrue()
    {
        // ARRANGE
        var policy = new MaxTotalSpanPolicy<int, int, IntegerFixedStepDomain>(5, _domain);
        var segments = new[] { CreateSegment(0, 9) }; // span 10 > 5

        // ACT
        var pressure = policy.Evaluate(segments);

        // ASSERT
        Assert.True(pressure.IsExceeded);
        Assert.IsNotType<NoPressure<int, int>>(pressure);
    }

    [Fact]
    public void Evaluate_WithMultipleSegmentsTotalExceedsMax_ReturnsPressureWithIsExceededTrue()
    {
        // ARRANGE
        var policy = new MaxTotalSpanPolicy<int, int, IntegerFixedStepDomain>(15, _domain);
        // [0,9]=span10 + [20,29]=span10 = total 20 > 15
        var segments = new[] { CreateSegment(0, 9), CreateSegment(20, 29) };

        // ACT
        var pressure = policy.Evaluate(segments);

        // ASSERT
        Assert.True(pressure.IsExceeded);
    }

    [Fact]
    public void Evaluate_WhenSingleSegmentExceedsMax_PressureSatisfiedAfterReducingThatSegment()
    {
        // ARRANGE
        var policy = new MaxTotalSpanPolicy<int, int, IntegerFixedStepDomain>(5, _domain);
        var segments = new[] { CreateSegment(0, 9) }; // span 10 > 5

        // ACT
        var pressure = policy.Evaluate(segments);

        // ASSERT — exceeded before reduction
        Assert.True(pressure.IsExceeded);

        // Reduce by removing the segment (span 10) → total 0 <= 5
        pressure.Reduce(segments[0]);
        Assert.False(pressure.IsExceeded);
    }

    [Fact]
    public void Evaluate_WithMultipleSegments_PressureSatisfiedAfterEnoughReduces()
    {
        // ARRANGE — max 15, three segments of span 10 each = total 30
        var policy = new MaxTotalSpanPolicy<int, int, IntegerFixedStepDomain>(15, _domain);
        var segments = new[]
        {
            CreateSegment(0, 9),   // span 10
            CreateSegment(20, 29), // span 10
            CreateSegment(40, 49), // span 10
        };

        // ACT
        var pressure = policy.Evaluate(segments);

        // ASSERT — total=30 > 15, need to remove enough to get to <= 15
        Assert.True(pressure.IsExceeded);

        // Remove first: total 30 - 10 = 20 > 15 → still exceeded
        pressure.Reduce(segments[0]);
        Assert.True(pressure.IsExceeded);

        // Remove second: total 20 - 10 = 10 <= 15 → satisfied
        pressure.Reduce(segments[1]);
        Assert.False(pressure.IsExceeded);
    }

    #endregion

    #region Helpers

    private static CachedSegment<int, int> CreateSegment(int start, int end)
    {
        var range = TestHelpers.CreateRange(start, end);
        var len = end - start + 1;
        return new CachedSegment<int, int>(
            range,
            new ReadOnlyMemory<int>(new int[len]),
            new SegmentStatistics(DateTime.UtcNow));
    }

    #endregion
}
