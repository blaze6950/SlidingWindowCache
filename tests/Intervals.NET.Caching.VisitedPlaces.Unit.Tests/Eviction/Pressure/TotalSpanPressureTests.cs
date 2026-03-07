using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Caching.VisitedPlaces.Core;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Pressure;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure.Helpers;

namespace Intervals.NET.Caching.VisitedPlaces.Unit.Tests.Eviction.Pressure;

/// <summary>
/// Unit tests for <see cref="TotalSpanPressure{TRange,TData,TDomain}"/>.
/// Validates IsExceeded semantics and Reduce behavior that subtracts actual segment span.
/// </summary>
public sealed class TotalSpanPressureTests
{
    private readonly IntegerFixedStepDomain _domain = TestHelpers.CreateIntDomain();

    #region IsExceeded Tests

    [Fact]
    public void IsExceeded_WhenTotalSpanAboveMax_ReturnsTrue()
    {
        // ARRANGE
        var pressure = new TotalSpanPressure<int, int, IntegerFixedStepDomain>(
            currentTotalSpan: 20, maxTotalSpan: 15, domain: _domain);

        // ACT & ASSERT
        Assert.True(pressure.IsExceeded);
    }

    [Fact]
    public void IsExceeded_WhenTotalSpanEqualsMax_ReturnsFalse()
    {
        // ARRANGE
        var pressure = new TotalSpanPressure<int, int, IntegerFixedStepDomain>(
            currentTotalSpan: 15, maxTotalSpan: 15, domain: _domain);

        // ACT & ASSERT
        Assert.False(pressure.IsExceeded);
    }

    [Fact]
    public void IsExceeded_WhenTotalSpanBelowMax_ReturnsFalse()
    {
        // ARRANGE
        var pressure = new TotalSpanPressure<int, int, IntegerFixedStepDomain>(
            currentTotalSpan: 5, maxTotalSpan: 15, domain: _domain);

        // ACT & ASSERT
        Assert.False(pressure.IsExceeded);
    }

    #endregion

    #region Reduce Tests

    [Fact]
    public void Reduce_SubtractsSegmentSpanFromTotal()
    {
        // ARRANGE — total=20, max=15 → exceeded
        var pressure = new TotalSpanPressure<int, int, IntegerFixedStepDomain>(
            currentTotalSpan: 20, maxTotalSpan: 15, domain: _domain);

        // Segment [0,9] = span 10
        var segment = CreateSegment(0, 9);

        // ACT — reduce by span 10 → total=10 <= 15
        pressure.Reduce(segment);

        // ASSERT
        Assert.False(pressure.IsExceeded);
    }

    [Fact]
    public void Reduce_IsSpanDependent_SmallSegmentReducesLess()
    {
        // ARRANGE — total=20, max=15 → excess 5
        var pressure = new TotalSpanPressure<int, int, IntegerFixedStepDomain>(
            currentTotalSpan: 20, maxTotalSpan: 15, domain: _domain);

        // Small segment [0,2] = span 3 → total=17 > 15 still exceeded
        var smallSegment = CreateSegment(0, 2);

        // ACT
        pressure.Reduce(smallSegment);

        // ASSERT — 20 - 3 = 17 > 15 → still exceeded
        Assert.True(pressure.IsExceeded);
    }

    [Fact]
    public void Reduce_MultipleCallsSubtractProgressively()
    {
        // ARRANGE — total=30, max=15 → need to reduce by > 15
        var pressure = new TotalSpanPressure<int, int, IntegerFixedStepDomain>(
            currentTotalSpan: 30, maxTotalSpan: 15, domain: _domain);

        var seg1 = CreateSegment(0, 9);   // span 10
        var seg2 = CreateSegment(20, 29); // span 10

        // ACT & ASSERT
        pressure.Reduce(seg1); // 30 - 10 = 20 > 15 → still exceeded
        Assert.True(pressure.IsExceeded);

        pressure.Reduce(seg2); // 20 - 10 = 10 <= 15 → satisfied
        Assert.False(pressure.IsExceeded);
    }

    [Fact]
    public void Reduce_UnlikeCountPressure_DifferentSegmentsReduceDifferentAmounts()
    {
        // ARRANGE — total=25, max=15 → need to reduce by > 10
        var pressure = new TotalSpanPressure<int, int, IntegerFixedStepDomain>(
            currentTotalSpan: 25, maxTotalSpan: 15, domain: _domain);

        // Small segment [0,2] = span 3 → total=22 (still exceeded)
        // Large segment [10,19] = span 10 → total=12 (satisfied)
        var small = CreateSegment(0, 2);
        var large = CreateSegment(10, 19);

        // ACT
        pressure.Reduce(small); // 25 - 3 = 22 > 15
        Assert.True(pressure.IsExceeded);

        pressure.Reduce(large); // 22 - 10 = 12 <= 15
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
