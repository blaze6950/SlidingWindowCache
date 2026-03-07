using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Caching.VisitedPlaces.Core;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Policies;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Pressure;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Selectors;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure.Helpers;

namespace Intervals.NET.Caching.VisitedPlaces.Unit.Tests.Eviction;

/// <summary>
/// Unit tests for <see cref="EvictionExecutor{TRange,TData}"/>.
/// Validates the constraint satisfaction loop: immunity filtering, selector ordering,
/// and pressure-driven termination.
/// </summary>
public sealed class EvictionExecutorTests
{
    private readonly IntegerFixedStepDomain _domain = TestHelpers.CreateIntDomain();

    #region Execute — Basic Constraint Satisfaction

    [Fact]
    public void Execute_WithCountPressure_RemovesUntilSatisfied()
    {
        // ARRANGE — 4 segments, max 2 → need to remove 2
        var segments = CreateSegmentsWithAccess(4);
        var pressure = new SegmentCountPressure<int, int>(currentCount: 4, maxCount: 2);
        var executor = new EvictionExecutor<int, int>(new LruEvictionSelector<int, int>());

        // ACT
        var toRemove = executor.Execute(pressure, segments, justStoredSegments: []);

        // ASSERT — exactly 2 removed, pressure satisfied
        Assert.Equal(2, toRemove.Count);
        Assert.False(pressure.IsExceeded);
    }

    [Fact]
    public void Execute_WithCountPressureExceededByOne_RemovesExactlyOne()
    {
        // ARRANGE — 3 segments, max 2 → remove 1
        var segments = CreateSegmentsWithAccess(3);
        var pressure = new SegmentCountPressure<int, int>(currentCount: 3, maxCount: 2);
        var executor = new EvictionExecutor<int, int>(new LruEvictionSelector<int, int>());

        // ACT
        var toRemove = executor.Execute(pressure, segments, justStoredSegments: []);

        // ASSERT
        Assert.Single(toRemove);
        Assert.False(pressure.IsExceeded);
    }

    [Fact]
    public void Execute_WithTotalSpanPressure_RemovesUntilSpanSatisfied()
    {
        // ARRANGE — total span 30, max 15 → need to remove enough span
        var seg1 = CreateSegment(0, 9);   // span 10
        var seg2 = CreateSegment(20, 29); // span 10
        var seg3 = CreateSegment(40, 49); // span 10
        var segments = new List<CachedSegment<int, int>> { seg1, seg2, seg3 };

        var pressure = new TotalSpanPressure<int, int, IntegerFixedStepDomain>(
            currentTotalSpan: 30, maxTotalSpan: 15, domain: _domain);

        // Use LRU selector — all have same access time, so order is stable
        var executor = new EvictionExecutor<int, int>(new LruEvictionSelector<int, int>());

        // ACT
        var toRemove = executor.Execute(pressure, segments, justStoredSegments: []);

        // ASSERT — removed 2 segments (30 - 10 = 20 > 15, 20 - 10 = 10 <= 15)
        Assert.Equal(2, toRemove.Count);
        Assert.False(pressure.IsExceeded);
    }

    #endregion

    #region Execute — Selector Ordering Respected

    [Fact]
    public void Execute_WithLruSelector_RemovesLeastRecentlyUsedFirst()
    {
        // ARRANGE
        var baseTime = DateTime.UtcNow;
        var old = CreateSegmentWithLastAccess(0, 5, baseTime.AddHours(-2));
        var recent = CreateSegmentWithLastAccess(10, 15, baseTime);
        var segments = new List<CachedSegment<int, int>> { old, recent };

        var pressure = new SegmentCountPressure<int, int>(currentCount: 2, maxCount: 1);
        var executor = new EvictionExecutor<int, int>(new LruEvictionSelector<int, int>());

        // ACT
        var toRemove = executor.Execute(pressure, segments, justStoredSegments: []);

        // ASSERT — the old (LRU) segment is removed
        Assert.Single(toRemove);
        Assert.Same(old, toRemove[0]);
    }

    [Fact]
    public void Execute_WithFifoSelector_RemovesOldestCreatedFirst()
    {
        // ARRANGE
        var baseTime = DateTime.UtcNow;
        var oldest = CreateSegmentWithCreatedAt(0, 5, baseTime.AddHours(-2));
        var newest = CreateSegmentWithCreatedAt(10, 15, baseTime);
        var segments = new List<CachedSegment<int, int>> { oldest, newest };

        var pressure = new SegmentCountPressure<int, int>(currentCount: 2, maxCount: 1);
        var executor = new EvictionExecutor<int, int>(new FifoEvictionSelector<int, int>());

        // ACT
        var toRemove = executor.Execute(pressure, segments, justStoredSegments: []);

        // ASSERT — the oldest (FIFO) segment is removed
        Assert.Single(toRemove);
        Assert.Same(oldest, toRemove[0]);
    }

    [Fact]
    public void Execute_WithSmallestFirstSelector_RemovesSmallestSpanFirst()
    {
        // ARRANGE
        var small = CreateSegment(0, 2);    // span 3
        var large = CreateSegment(20, 29);  // span 10
        var segments = new List<CachedSegment<int, int>> { small, large };

        var pressure = new SegmentCountPressure<int, int>(currentCount: 2, maxCount: 1);
        var selector = new SmallestFirstEvictionSelector<int, int, IntegerFixedStepDomain>(_domain);
        var executor = new EvictionExecutor<int, int>(selector);

        // ACT
        var toRemove = executor.Execute(pressure, segments, justStoredSegments: []);

        // ASSERT — smallest span removed
        Assert.Single(toRemove);
        Assert.Same(small, toRemove[0]);
    }

    #endregion

    #region Execute — Just-Stored Immunity (Invariant VPC.E.3)

    [Fact]
    public void Execute_JustStoredSegmentIsImmune_RemovedFromCandidates()
    {
        // ARRANGE — 2 segments, 1 is justStored
        var old = CreateSegmentWithLastAccess(0, 5, DateTime.UtcNow.AddHours(-2));
        var justStored = CreateSegmentWithLastAccess(10, 15, DateTime.UtcNow);
        var segments = new List<CachedSegment<int, int>> { old, justStored };

        var pressure = new SegmentCountPressure<int, int>(currentCount: 2, maxCount: 1);
        var executor = new EvictionExecutor<int, int>(new LruEvictionSelector<int, int>());

        // ACT
        var toRemove = executor.Execute(pressure, segments, justStoredSegments: [justStored]);

        // ASSERT — old is removed, justStored is immune
        Assert.Single(toRemove);
        Assert.Same(old, toRemove[0]);
        Assert.DoesNotContain(justStored, toRemove);
    }

    [Fact]
    public void Execute_AllSegmentsAreJustStored_ReturnsEmptyList()
    {
        // ARRANGE — all immune (Invariant VPC.E.3a)
        var seg = CreateSegment(0, 5);
        var segments = new List<CachedSegment<int, int>> { seg };

        var pressure = new SegmentCountPressure<int, int>(currentCount: 2, maxCount: 1);
        var executor = new EvictionExecutor<int, int>(new LruEvictionSelector<int, int>());

        // ACT
        var toRemove = executor.Execute(pressure, segments, justStoredSegments: [seg]);

        // ASSERT — no eviction possible
        Assert.Empty(toRemove);
    }

    [Fact]
    public void Execute_MultipleJustStoredSegments_AllFilteredFromCandidates()
    {
        // ARRANGE — 4 segments, 2 are justStored
        var baseTime = DateTime.UtcNow;
        var old1 = CreateSegmentWithLastAccess(0, 5, baseTime.AddHours(-2));
        var old2 = CreateSegmentWithLastAccess(10, 15, baseTime.AddHours(-1));
        var just1 = CreateSegmentWithLastAccess(20, 25, baseTime);
        var just2 = CreateSegmentWithLastAccess(30, 35, baseTime);
        var segments = new List<CachedSegment<int, int>> { old1, old2, just1, just2 };

        var pressure = new SegmentCountPressure<int, int>(currentCount: 4, maxCount: 2);
        var executor = new EvictionExecutor<int, int>(new LruEvictionSelector<int, int>());

        // ACT
        var toRemove = executor.Execute(pressure, segments, justStoredSegments: [just1, just2]);

        // ASSERT — old1 and old2 removed, just1 and just2 immune
        Assert.Equal(2, toRemove.Count);
        Assert.Contains(old1, toRemove);
        Assert.Contains(old2, toRemove);
        Assert.DoesNotContain(just1, toRemove);
        Assert.DoesNotContain(just2, toRemove);
    }

    [Fact]
    public void Execute_WithSmallestFirstSelector_JustStoredSmallSkipsToNextSmallest()
    {
        // ARRANGE — smallest is justStored (immune), should select next smallest
        var small = CreateSegment(0, 1);    // span 2 — justStored
        var medium = CreateSegment(10, 14); // span 5
        var large = CreateSegment(20, 29);  // span 10
        var segments = new List<CachedSegment<int, int>> { small, medium, large };

        var pressure = new SegmentCountPressure<int, int>(currentCount: 3, maxCount: 2);
        var selector = new SmallestFirstEvictionSelector<int, int, IntegerFixedStepDomain>(_domain);
        var executor = new EvictionExecutor<int, int>(selector);

        // ACT
        var toRemove = executor.Execute(pressure, segments, justStoredSegments: [small]);

        // ASSERT — medium removed (next smallest after immune small)
        Assert.Single(toRemove);
        Assert.Same(medium, toRemove[0]);
    }

    #endregion

    #region Execute — Composite Pressure

    [Fact]
    public void Execute_WithCompositePressure_RemovesUntilAllSatisfied()
    {
        // ARRANGE — count pressure (4>2) + another count pressure (4>3)
        // The stricter constraint (max 2) governs: need to remove 2
        var segments = CreateSegmentsWithAccess(4);
        var p1 = new SegmentCountPressure<int, int>(currentCount: 4, maxCount: 2); // need 2 removals
        var p2 = new SegmentCountPressure<int, int>(currentCount: 4, maxCount: 3); // need 1 removal
        var composite = new CompositePressure<int, int>([p1, p2]);
        var executor = new EvictionExecutor<int, int>(new LruEvictionSelector<int, int>());

        // ACT
        var toRemove = executor.Execute(composite, segments, justStoredSegments: []);

        // ASSERT — 2 removed (satisfies both: 2<=2 and 2<=3)
        Assert.Equal(2, toRemove.Count);
        Assert.False(composite.IsExceeded);
    }

    #endregion

    #region Execute — Candidates Exhausted Before Satisfaction

    [Fact]
    public void Execute_WhenCandidatesExhaustedBeforeSatisfaction_ReturnsAllCandidates()
    {
        // ARRANGE — pressure requires removing 3, but only 2 non-immune candidates
        var old1 = CreateSegmentWithLastAccess(0, 5, DateTime.UtcNow.AddHours(-2));
        var old2 = CreateSegmentWithLastAccess(10, 15, DateTime.UtcNow.AddHours(-1));
        var justStored = CreateSegment(20, 25); // immune
        var segments = new List<CachedSegment<int, int>> { old1, old2, justStored };

        // Need to remove 3 (count=4, max=1) but only 2 eligible
        var pressure = new SegmentCountPressure<int, int>(currentCount: 4, maxCount: 1);
        var executor = new EvictionExecutor<int, int>(new LruEvictionSelector<int, int>());

        // ACT
        var toRemove = executor.Execute(pressure, segments, justStoredSegments: [justStored]);

        // ASSERT — all eligible candidates removed (even though pressure still exceeded)
        Assert.Equal(2, toRemove.Count);
        Assert.Contains(old1, toRemove);
        Assert.Contains(old2, toRemove);
        // Pressure may still be exceeded — that's acceptable (exhausted candidates)
    }

    #endregion

    #region Execute — The Core Architectural Fix (TotalSpan + Selector Mismatch)

    [Fact]
    public void Execute_TotalSpanPressureWithLruSelector_CorrectlySatisfiesRegardlessOfOrder()
    {
        // ARRANGE — This is the scenario the old architecture got wrong:
        // MaxTotalSpanEvaluator used a greedy largest-first count estimate,
        // but the executor used LRU order. The new model tracks actual span removal.
        var baseTime = DateTime.UtcNow;

        // LRU order will evict oldest-accessed first (small, medium, large)
        // But the span constraint needs sufficient total span removed
        var small = CreateSegmentWithLastAccess(0, 2, baseTime.AddHours(-3));    // span 3, oldest
        var medium = CreateSegmentWithLastAccess(10, 15, baseTime.AddHours(-2)); // span 6
        var large = CreateSegmentWithLastAccess(20, 29, baseTime.AddHours(-1));  // span 10, newest

        var segments = new List<CachedSegment<int, int>> { small, medium, large };

        // Total span = 3+6+10 = 19, max = 10 → need to reduce by > 9
        // LRU order: small(3) then medium(6) = total removed 9 → 19-9=10 <= 10 → satisfied after 2
        // Old greedy estimate (largest-first): large(10) alone covers 9 → estimate=1, but LRU removes small first!
        var pressure = new TotalSpanPressure<int, int, IntegerFixedStepDomain>(
            currentTotalSpan: 19, maxTotalSpan: 10, domain: _domain);

        var executor = new EvictionExecutor<int, int>(new LruEvictionSelector<int, int>());

        // ACT
        var toRemove = executor.Execute(pressure, segments, justStoredSegments: []);

        // ASSERT — correctly removes 2 segments (small + medium) to satisfy constraint
        Assert.Equal(2, toRemove.Count);
        Assert.Same(small, toRemove[0]);   // LRU: oldest accessed first
        Assert.Same(medium, toRemove[1]);
        Assert.False(pressure.IsExceeded); // Constraint actually satisfied!
    }

    #endregion

    #region Execute — Empty Input

    [Fact]
    public void Execute_WithNoSegments_ReturnsEmptyList()
    {
        // ARRANGE
        var segments = new List<CachedSegment<int, int>>();
        var pressure = new SegmentCountPressure<int, int>(currentCount: 1, maxCount: 0);
        var executor = new EvictionExecutor<int, int>(new LruEvictionSelector<int, int>());

        // ACT
        var toRemove = executor.Execute(pressure, segments, justStoredSegments: []);

        // ASSERT
        Assert.Empty(toRemove);
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

    private static CachedSegment<int, int> CreateSegmentWithLastAccess(int start, int end, DateTime lastAccess)
    {
        var range = TestHelpers.CreateRange(start, end);
        var segment = new CachedSegment<int, int>(
            range,
            new ReadOnlyMemory<int>(new int[end - start + 1]));
        segment.EvictionMetadata = new LruEvictionSelector<int, int>.LruMetadata(lastAccess);
        return segment;
    }

    private static CachedSegment<int, int> CreateSegmentWithCreatedAt(int start, int end, DateTime createdAt)
    {
        var range = TestHelpers.CreateRange(start, end);
        var segment = new CachedSegment<int, int>(
            range,
            new ReadOnlyMemory<int>(new int[end - start + 1]));
        segment.EvictionMetadata = new FifoEvictionSelector<int, int>.FifoMetadata(createdAt);
        return segment;
    }

    /// <summary>
    /// Creates N segments with distinct access times (oldest first) for predictable LRU ordering.
    /// </summary>
    private static IReadOnlyList<CachedSegment<int, int>> CreateSegmentsWithAccess(int count)
    {
        var baseTime = DateTime.UtcNow.AddHours(-count);
        var result = new List<CachedSegment<int, int>>();
        for (var i = 0; i < count; i++)
        {
            var start = i * 10;
            var range = TestHelpers.CreateRange(start, start + 5);
            var segment = new CachedSegment<int, int>(
                range,
                new ReadOnlyMemory<int>(new int[6]));
            segment.EvictionMetadata = new LruEvictionSelector<int, int>.LruMetadata(baseTime.AddHours(i));
            result.Add(segment);
        }
        return result;
    }

    #endregion
}
