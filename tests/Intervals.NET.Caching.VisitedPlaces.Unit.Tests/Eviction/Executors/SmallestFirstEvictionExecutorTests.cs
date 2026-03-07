using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Caching.VisitedPlaces.Core;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Evaluators;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Executors;
using Intervals.NET.Caching.VisitedPlaces.Infrastructure.Storage;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure.Helpers;

namespace Intervals.NET.Caching.VisitedPlaces.Unit.Tests.Eviction.Executors;

/// <summary>
/// Unit tests for <see cref="SmallestFirstEvictionExecutor{TRange,TData,TDomain}"/>.
/// </summary>
public sealed class SmallestFirstEvictionExecutorTests
{
    private readonly IntegerFixedStepDomain _domain = TestHelpers.CreateIntDomain();

    #region Constructor Tests

    [Fact]
    public void Constructor_WithValidDomain_DoesNotThrow()
    {
        // ARRANGE & ACT
        var exception = Record.Exception(() =>
            new SmallestFirstEvictionExecutor<int, int, IntegerFixedStepDomain>(_domain));

        // ASSERT
        Assert.Null(exception);
    }

    #endregion

    #region SelectForEviction Tests

    [Fact]
    public void SelectForEviction_ReturnsSmallestSegmentFirst()
    {
        // ARRANGE
        var executor = new SmallestFirstEvictionExecutor<int, int, IntegerFixedStepDomain>(_domain);
        var storage = new SnapshotAppendBufferStorage<int, int>();

        // Segments of different spans
        var small = CreateSegment(0, 2);    // span 3
        var medium = CreateSegment(10, 15); // span 6
        var large = CreateSegment(20, 29);  // span 10

        storage.Add(small);
        storage.Add(medium);
        storage.Add(large);

        var allSegments = storage.GetAllSegments();
        var evaluator = new MaxSegmentCountEvaluator<int, int>(2);

        // ACT
        var toRemove = executor.SelectForEviction(allSegments, justStored: null, [evaluator]);
        foreach (var s in toRemove) storage.Remove(s);

        // ASSERT — smallest (span 3) removed
        var remaining = storage.GetAllSegments();
        Assert.DoesNotContain(small, remaining);
        Assert.Equal(2, storage.Count);
    }

    [Fact]
    public void SelectForEviction_RespectsJustStoredImmunity()
    {
        // ARRANGE
        var executor = new SmallestFirstEvictionExecutor<int, int, IntegerFixedStepDomain>(_domain);
        var storage = new SnapshotAppendBufferStorage<int, int>();

        // Only the justStored segment exists
        var justStored = CreateSegment(0, 5);
        storage.Add(justStored);

        var allSegments = storage.GetAllSegments();
        var evaluator = new MaxSegmentCountEvaluator<int, int>(1);

        // ACT
        var toRemove = executor.SelectForEviction(allSegments, justStored: justStored, [evaluator]);

        // ASSERT — no-op (VPC.E.3a)
        Assert.Empty(toRemove);
        Assert.Equal(1, storage.Count);
    }

    [Fact]
    public void SelectForEviction_WithJustStoredSmall_ReturnsNextSmallest()
    {
        // ARRANGE
        var executor = new SmallestFirstEvictionExecutor<int, int, IntegerFixedStepDomain>(_domain);
        var storage = new SnapshotAppendBufferStorage<int, int>();

        var small = CreateSegment(0, 1);    // span 2 — justStored (immune)
        var medium = CreateSegment(10, 14); // span 5
        var large = CreateSegment(20, 29);  // span 10

        storage.Add(small);
        storage.Add(medium);
        storage.Add(large);

        var allSegments = storage.GetAllSegments();
        var evaluator = new MaxSegmentCountEvaluator<int, int>(2);

        // ACT — justStored=small is immune, so medium (next smallest) should be selected
        var toRemove = executor.SelectForEviction(allSegments, justStored: small, [evaluator]);
        foreach (var s in toRemove) storage.Remove(s);

        // ASSERT
        var remaining = storage.GetAllSegments();
        Assert.DoesNotContain(medium, remaining);
        Assert.Contains(small, remaining);
        Assert.Contains(large, remaining);
    }

    #endregion

    #region UpdateStatistics Tests

    [Fact]
    public void UpdateStatistics_IncrementsHitCount()
    {
        // ARRANGE
        var executor = new SmallestFirstEvictionExecutor<int, int, IntegerFixedStepDomain>(_domain);
        var segment = CreateSegment(0, 9);

        // ACT
        executor.UpdateStatistics([segment], DateTime.UtcNow);

        // ASSERT
        Assert.Equal(1, segment.Statistics.HitCount);
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
