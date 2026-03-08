using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Caching.VisitedPlaces.Core;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Policies;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Pressure;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure.Helpers;

namespace Intervals.NET.Caching.VisitedPlaces.Unit.Tests.Eviction.Policies;

/// <summary>
/// Unit tests for <see cref="MaxTotalSpanPolicy{TRange,TData,TDomain}"/>.
/// Validates constructor constraints, the O(1) Evaluate path (using cached running total),
/// stateful lifecycle via <see cref="IStatefulEvictionPolicy{TRange,TData}"/>,
/// and <see cref="MaxTotalSpanPolicy{TRange,TData,TDomain}.TotalSpanPressure"/> behavior.
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

    [Fact]
    public void Policy_ImplementsIStatefulEvictionPolicy()
    {
        // ARRANGE & ACT
        var policy = new MaxTotalSpanPolicy<int, int, IntegerFixedStepDomain>(10, _domain);

        // ASSERT — confirms the stateful contract is fulfilled
        Assert.IsAssignableFrom<IStatefulEvictionPolicy<int, int>>(policy);
    }

    #endregion

    #region Evaluate Tests — No Pressure (Constraint Not Violated)

    [Fact]
    public void Evaluate_WithNoSegmentsAdded_ReturnsNoPressure()
    {
        // ARRANGE — running total starts at 0
        var policy = new MaxTotalSpanPolicy<int, int, IntegerFixedStepDomain>(50, _domain);

        // ACT — no OnSegmentAdded calls; _totalSpan == 0 <= 50
        var pressure = policy.Evaluate([]);

        // ASSERT
        Assert.Same(NoPressure<int, int>.Instance, pressure);
    }

    [Fact]
    public void Evaluate_WhenTotalSpanBelowMax_ReturnsNoPressure()
    {
        // ARRANGE
        var policy = new MaxTotalSpanPolicy<int, int, IntegerFixedStepDomain>(50, _domain);
        var segment = CreateSegment(0, 9); // span 10

        policy.OnSegmentAdded(segment); // _totalSpan = 10 <= 50

        // ACT
        var pressure = policy.Evaluate([segment]);

        // ASSERT
        Assert.Same(NoPressure<int, int>.Instance, pressure);
    }

    [Fact]
    public void Evaluate_WhenTotalSpanEqualsMax_ReturnsNoPressure()
    {
        // ARRANGE
        var policy = new MaxTotalSpanPolicy<int, int, IntegerFixedStepDomain>(10, _domain);
        var segment = CreateSegment(0, 9); // span 10

        policy.OnSegmentAdded(segment); // _totalSpan = 10 == MaxTotalSpan

        // ACT
        var pressure = policy.Evaluate([segment]);

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
        var segment = CreateSegment(0, 9); // span 10

        policy.OnSegmentAdded(segment); // _totalSpan = 10 > 5

        // ACT
        var pressure = policy.Evaluate([segment]);

        // ASSERT
        Assert.True(pressure.IsExceeded);
        Assert.IsNotType<NoPressure<int, int>>(pressure);
    }

    [Fact]
    public void Evaluate_WithMultipleSegmentsTotalExceedsMax_ReturnsPressureWithIsExceededTrue()
    {
        // ARRANGE
        var policy = new MaxTotalSpanPolicy<int, int, IntegerFixedStepDomain>(15, _domain);
        var seg1 = CreateSegment(0, 9);   // span 10
        var seg2 = CreateSegment(20, 29); // span 10 → total 20 > 15

        policy.OnSegmentAdded(seg1);
        policy.OnSegmentAdded(seg2);

        // ACT
        var pressure = policy.Evaluate([seg1, seg2]);

        // ASSERT
        Assert.True(pressure.IsExceeded);
    }

    [Fact]
    public void Evaluate_WhenSingleSegmentExceedsMax_PressureSatisfiedAfterReducingThatSegment()
    {
        // ARRANGE
        var policy = new MaxTotalSpanPolicy<int, int, IntegerFixedStepDomain>(5, _domain);
        var segment = CreateSegment(0, 9); // span 10

        policy.OnSegmentAdded(segment); // _totalSpan = 10 > 5

        // ACT
        var pressure = policy.Evaluate([segment]);
        Assert.True(pressure.IsExceeded);

        // Reduce by removing the segment (span 10) → total 0 <= 5
        pressure.Reduce(segment);

        // ASSERT
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

        foreach (var seg in segments)
        {
            policy.OnSegmentAdded(seg);
        }

        // ACT
        var pressure = policy.Evaluate(segments);
        Assert.True(pressure.IsExceeded); // total=30 > 15

        // Remove first: total 30 - 10 = 20 > 15 → still exceeded
        pressure.Reduce(segments[0]);
        Assert.True(pressure.IsExceeded);

        // Remove second: total 20 - 10 = 10 <= 15 → satisfied
        pressure.Reduce(segments[1]);

        // ASSERT
        Assert.False(pressure.IsExceeded);
    }

    #endregion

    #region Stateful Lifecycle Tests (IStatefulEvictionPolicy)

    [Fact]
    public void OnSegmentAdded_IncreasesTotalSpan()
    {
        // ARRANGE
        var policy = new MaxTotalSpanPolicy<int, int, IntegerFixedStepDomain>(5, _domain);
        var seg = CreateSegment(0, 9); // span 10

        // Initially no pressure
        Assert.Same(NoPressure<int, int>.Instance, policy.Evaluate([]));

        // ACT
        policy.OnSegmentAdded(seg); // _totalSpan = 10 > 5

        // ASSERT — now exceeded
        Assert.True(policy.Evaluate([seg]).IsExceeded);
    }

    [Fact]
    public void OnSegmentRemoved_DecreasesTotalSpan()
    {
        // ARRANGE — add two segments; total span exceeds max; then remove one to go under
        var policy = new MaxTotalSpanPolicy<int, int, IntegerFixedStepDomain>(15, _domain);
        var seg1 = CreateSegment(0, 9);   // span 10
        var seg2 = CreateSegment(20, 29); // span 10 → total 20 > 15

        policy.OnSegmentAdded(seg1);
        policy.OnSegmentAdded(seg2);
        Assert.True(policy.Evaluate([seg1, seg2]).IsExceeded);

        // ACT
        policy.OnSegmentRemoved(seg2); // _totalSpan = 10 <= 15

        // ASSERT — no longer exceeded
        Assert.Same(NoPressure<int, int>.Instance, policy.Evaluate([seg1]));
    }

    [Fact]
    public void OnSegmentAdded_ThenOnSegmentRemoved_RestoresToOriginalTotal()
    {
        // ARRANGE
        var policy = new MaxTotalSpanPolicy<int, int, IntegerFixedStepDomain>(5, _domain);
        var seg = CreateSegment(0, 9); // span 10

        // ACT — add then remove the same segment
        policy.OnSegmentAdded(seg);
        Assert.True(policy.Evaluate([seg]).IsExceeded);

        policy.OnSegmentRemoved(seg);

        // ASSERT — total back to 0, no pressure
        Assert.Same(NoPressure<int, int>.Instance, policy.Evaluate([]));
    }

    [Fact]
    public void Evaluate_DoesNotUseAllSegmentsParameter_UsesRunningTotal()
    {
        // ARRANGE — policy has _totalSpan = 0 (no OnSegmentAdded called)
        // but we pass a non-empty segment list to Evaluate.
        // Evaluate must ignore the list and use the cached total.
        var policy = new MaxTotalSpanPolicy<int, int, IntegerFixedStepDomain>(5, _domain);
        var segment = CreateSegment(0, 9); // span 10 > 5

        // ACT — no OnSegmentAdded: _totalSpan remains 0 <= 5
        var pressure = policy.Evaluate([segment]);

        // ASSERT — NoPressure because _totalSpan=0, not because of the list content
        Assert.Same(NoPressure<int, int>.Instance, pressure);
    }

    [Fact]
    public void MultipleOnSegmentAdded_AccumulatesSpansCorrectly()
    {
        // ARRANGE
        var policy = new MaxTotalSpanPolicy<int, int, IntegerFixedStepDomain>(25, _domain);
        // Three segments: span 10 each → total 30 > 25
        var segs = new[]
        {
            CreateSegment(0, 9),   // span 10 → running total 10 (not exceeded)
            CreateSegment(20, 29), // span 10 → running total 20 (not exceeded)
            CreateSegment(40, 49), // span 10 → running total 30 (exceeded)
        };

        policy.OnSegmentAdded(segs[0]);
        Assert.Same(NoPressure<int, int>.Instance, policy.Evaluate([segs[0]]));

        policy.OnSegmentAdded(segs[1]);
        Assert.Same(NoPressure<int, int>.Instance, policy.Evaluate([segs[0], segs[1]]));

        // ACT — third segment pushes total over the limit
        policy.OnSegmentAdded(segs[2]);
        var pressure = policy.Evaluate(segs);

        // ASSERT
        Assert.True(pressure.IsExceeded);
    }

    #endregion

    #region Helpers

    private static CachedSegment<int, int> CreateSegment(int start, int end)
    {
        var range = TestHelpers.CreateRange(start, end);
        var len = end - start + 1;
        return new CachedSegment<int, int>(
            range,
            new ReadOnlyMemory<int>(new int[len]));
    }

    #endregion
}
