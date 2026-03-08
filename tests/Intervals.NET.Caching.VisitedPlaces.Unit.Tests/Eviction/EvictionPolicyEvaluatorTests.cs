using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Caching.VisitedPlaces.Core;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Policies;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Pressure;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure.Helpers;

namespace Intervals.NET.Caching.VisitedPlaces.Unit.Tests.Eviction;

/// <summary>
/// Unit tests for <see cref="EvictionPolicyEvaluator{TRange,TData}"/>.
/// Validates constructor validation, stateful lifecycle forwarding to
/// <see cref="IStatefulEvictionPolicy{TRange,TData}"/> implementations,
/// pressure evaluation (single policy, multiple policies, composite), and the
/// <see cref="NoPressure{TRange,TData}.Instance"/> singleton return when no policy fires.
/// </summary>
public sealed class EvictionPolicyEvaluatorTests
{
    private readonly IntegerFixedStepDomain _domain = TestHelpers.CreateIntDomain();

    #region Constructor Tests

    [Fact]
    public void Constructor_WithNullPolicies_ThrowsArgumentNullException()
    {
        // ARRANGE & ACT
        var exception = Record.Exception(() =>
            new EvictionPolicyEvaluator<int, int>(null!));

        // ASSERT
        Assert.IsType<ArgumentNullException>(exception);
    }

    [Fact]
    public void Constructor_WithEmptyPolicies_DoesNotThrow()
    {
        // ARRANGE & ACT
        var exception = Record.Exception(() =>
            new EvictionPolicyEvaluator<int, int>([]));

        // ASSERT
        Assert.Null(exception);
    }

    #endregion

    #region Evaluate — No Pressure (NoPressure singleton)

    [Fact]
    public void Evaluate_WithNoPolicies_ReturnsNoPressureSingleton()
    {
        // ARRANGE
        var evaluator = new EvictionPolicyEvaluator<int, int>([]);

        // ACT
        var pressure = evaluator.Evaluate([]);

        // ASSERT — no eviction needed: singleton NoPressure, not exceeded
        Assert.IsType<NoPressure<int, int>>(pressure);
        Assert.False(pressure.IsExceeded);
    }

    [Fact]
    public void Evaluate_WhenNoPolicyFires_ReturnsNoPressureSingleton()
    {
        // ARRANGE — limit 10, only 3 segments stored
        var countPolicy = new MaxSegmentCountPolicy<int, int>(10);
        var evaluator = new EvictionPolicyEvaluator<int, int>([countPolicy]);
        var segments = CreateSegments(3);

        // ACT
        var pressure = evaluator.Evaluate(segments);

        // ASSERT
        Assert.IsType<NoPressure<int, int>>(pressure);
        Assert.False(pressure.IsExceeded);
    }

    #endregion

    #region Evaluate — Single Policy Fires

    [Fact]
    public void Evaluate_WhenSinglePolicyFires_ReturnsThatPressure()
    {
        // ARRANGE — max 2 segments; 3 stored → fires
        var countPolicy = new MaxSegmentCountPolicy<int, int>(2);
        var evaluator = new EvictionPolicyEvaluator<int, int>([countPolicy]);
        var segments = CreateSegments(3);

        // ACT
        var pressure = evaluator.Evaluate(segments);

        // ASSERT — pressure must be exceeded and not null
        Assert.NotNull(pressure);
        Assert.True(pressure.IsExceeded);
        // Must NOT be a CompositePressure when only one policy fires
        Assert.IsNotType<CompositePressure<int, int>>(pressure);
    }

    #endregion

    #region Evaluate — Multiple Policies Fire → CompositePressure

    [Fact]
    public void Evaluate_WhenTwoPoliciesFire_ReturnsCompositePressure()
    {
        // ARRANGE — both policies fire: count (max 1) and span (max 5)
        var countPolicy = new MaxSegmentCountPolicy<int, int>(1);
        var spanPolicy = new MaxTotalSpanPolicy<int, int, IntegerFixedStepDomain>(5, _domain);
        var evaluator = new EvictionPolicyEvaluator<int, int>([countPolicy, spanPolicy]);

        var seg1 = CreateSegment(0, 9);   // span 10
        var seg2 = CreateSegment(20, 29); // span 10

        // Notify stateful policy of both segments
        evaluator.OnSegmentAdded(seg1);
        evaluator.OnSegmentAdded(seg2);

        var segments = new[] { seg1, seg2 }; // count=2>1; totalSpan=20>5

        // ACT
        var pressure = evaluator.Evaluate(segments);

        // ASSERT
        Assert.NotNull(pressure);
        Assert.True(pressure.IsExceeded);
        Assert.IsType<CompositePressure<int, int>>(pressure);
    }

    [Fact]
    public void Evaluate_WhenOnlyOnePolicyFiresAmongMany_ReturnsNonCompositePressure()
    {
        // ARRANGE — count (max 100) does NOT fire; span (max 5) DOES fire
        var countPolicy = new MaxSegmentCountPolicy<int, int>(100);
        var spanPolicy = new MaxTotalSpanPolicy<int, int, IntegerFixedStepDomain>(5, _domain);
        var evaluator = new EvictionPolicyEvaluator<int, int>([countPolicy, spanPolicy]);

        var seg = CreateSegment(0, 9); // span 10 > 5

        evaluator.OnSegmentAdded(seg);

        // ACT
        var pressure = evaluator.Evaluate([seg]);

        // ASSERT — one policy fired → single pressure (not composite)
        Assert.NotNull(pressure);
        Assert.True(pressure.IsExceeded);
        Assert.IsNotType<CompositePressure<int, int>>(pressure);
    }

    #endregion

    #region Lifecycle — OnSegmentAdded forwarded to stateful policies

    [Fact]
    public void OnSegmentAdded_ForwardsToStatefulPolicies()
    {
        // ARRANGE — stateful policy with max span 5; stateless count policy with max 100
        var spanPolicy = new MaxTotalSpanPolicy<int, int, IntegerFixedStepDomain>(5, _domain);
        var countPolicy = new MaxSegmentCountPolicy<int, int>(100);
        var evaluator = new EvictionPolicyEvaluator<int, int>([spanPolicy, countPolicy]);
        var seg = CreateSegment(0, 9); // span 10 > 5

        // Before add: spanPolicy._totalSpan=0 → no pressure
        Assert.False(evaluator.Evaluate([]).IsExceeded);

        // ACT
        evaluator.OnSegmentAdded(seg);

        // ASSERT — span policy now has _totalSpan=10 > 5 → fires
        var pressure = evaluator.Evaluate([seg]);
        Assert.NotNull(pressure);
        Assert.True(pressure.IsExceeded);
    }

    [Fact]
    public void OnSegmentAdded_DoesNotForwardToStatelessPolicies()
    {
        // ARRANGE — only a stateless count policy
        var countPolicy = new MaxSegmentCountPolicy<int, int>(10);
        var evaluator = new EvictionPolicyEvaluator<int, int>([countPolicy]);
        var seg = CreateSegment(0, 9);

        // ACT — OnSegmentAdded on a purely stateless policy must not throw or corrupt state
        var exception = Record.Exception(() => evaluator.OnSegmentAdded(seg));

        // ASSERT — no exception; evaluation uses allSegments.Count, still O(1)
        Assert.Null(exception);
    }

    #endregion

    #region Lifecycle — OnSegmentRemoved forwarded to stateful policies

    [Fact]
    public void OnSegmentRemoved_ForwardsToStatefulPolicies()
    {
        // ARRANGE — two segments push span over limit; removing one brings it under
        var spanPolicy = new MaxTotalSpanPolicy<int, int, IntegerFixedStepDomain>(15, _domain);
        var evaluator = new EvictionPolicyEvaluator<int, int>([spanPolicy]);
        var seg1 = CreateSegment(0, 9);   // span 10
        var seg2 = CreateSegment(20, 29); // span 10 → total 20 > 15

        evaluator.OnSegmentAdded(seg1);
        evaluator.OnSegmentAdded(seg2);
        Assert.True(evaluator.Evaluate([seg1, seg2]).IsExceeded);

        // ACT
        evaluator.OnSegmentRemoved(seg2); // total 10 <= 15

        // ASSERT — no longer exceeded
        Assert.False(evaluator.Evaluate([seg1]).IsExceeded);
    }

    [Fact]
    public void OnSegmentRemoved_DoesNotForwardToStatelessPolicies()
    {
        // ARRANGE — stateless count policy
        var countPolicy = new MaxSegmentCountPolicy<int, int>(10);
        var evaluator = new EvictionPolicyEvaluator<int, int>([countPolicy]);
        var seg = CreateSegment(0, 9);

        // ACT — OnSegmentRemoved on a stateless policy must not throw
        var exception = Record.Exception(() => evaluator.OnSegmentRemoved(seg));

        // ASSERT
        Assert.Null(exception);
    }

    #endregion

    #region Lifecycle — Mixed stateful + stateless policies

    [Fact]
    public void MixedPolicies_StatefulReceivesLifecycle_StatelessDoesNot()
    {
        // ARRANGE — both a stateful span policy and a stateless count policy are registered
        var spanPolicy = new MaxTotalSpanPolicy<int, int, IntegerFixedStepDomain>(5, _domain);
        var countPolicy = new MaxSegmentCountPolicy<int, int>(100);
        var evaluator = new EvictionPolicyEvaluator<int, int>([spanPolicy, countPolicy]);

        var seg1 = CreateSegment(0, 9);  // span 10 > 5
        var seg2 = CreateSegment(20, 25); // span 6 > 5

        evaluator.OnSegmentAdded(seg1);
        evaluator.OnSegmentAdded(seg2);

        // Both added: span policy _totalSpan=16>5, count=2<=100
        var pressure = evaluator.Evaluate([seg1, seg2]);
        Assert.NotNull(pressure);
        Assert.True(pressure.IsExceeded);

        // Remove seg1: span total=6 still > 5 for span policy; count=1<=100
        evaluator.OnSegmentRemoved(seg1);
        pressure = evaluator.Evaluate([seg2]);
        Assert.NotNull(pressure);
        Assert.True(pressure.IsExceeded);

        // Remove seg2: span total=0 <= 5; count=0 <= 100
        evaluator.OnSegmentRemoved(seg2);
        var pressureAfter = evaluator.Evaluate([]);
        Assert.False(pressureAfter.IsExceeded);
    }

    #endregion

    #region Evaluate — CompositePressure Reduce propagates to all children

    [Fact]
    public void CompositePressure_Reduce_SatisfiesBothPolicies()
    {
        // ARRANGE — two policies both fire; reducing one segment satisfies both simultaneously
        var countPolicy = new MaxSegmentCountPolicy<int, int>(1); // max 1
        var spanPolicy = new MaxTotalSpanPolicy<int, int, IntegerFixedStepDomain>(5, _domain); // max span 5
        var evaluator = new EvictionPolicyEvaluator<int, int>([countPolicy, spanPolicy]);

        var seg1 = CreateSegment(0, 9);   // span 10 > 5
        var seg2 = CreateSegment(20, 29); // span 10

        evaluator.OnSegmentAdded(seg1);
        evaluator.OnSegmentAdded(seg2);
        // count=2>1, totalSpan=20>5 → both fire
        var segments = new[] { seg1, seg2 };
        var pressure = evaluator.Evaluate(segments);

        Assert.NotNull(pressure);
        Assert.IsType<CompositePressure<int, int>>(pressure);
        Assert.True(pressure.IsExceeded);

        // ACT — remove seg1: count goes to 1<=1; span goes to 10 still >5
        pressure.Reduce(seg1);
        Assert.True(pressure.IsExceeded); // span child still exceeded

        // Remove seg2: count goes to 0<=1; span goes to 0<=5
        pressure.Reduce(seg2);

        // ASSERT
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
            result.Add(CreateSegment(start, start + 5));
        }
        return result;
    }

    private static CachedSegment<int, int> CreateSegment(int start, int end)
    {
        var range = TestHelpers.CreateRange(start, end);
        return new CachedSegment<int, int>(
            range,
            new ReadOnlyMemory<int>(new int[end - start + 1]));
    }

    #endregion
}
