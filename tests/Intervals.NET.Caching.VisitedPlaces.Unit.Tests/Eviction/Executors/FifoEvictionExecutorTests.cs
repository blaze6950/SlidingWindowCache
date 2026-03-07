using Intervals.NET.Caching.VisitedPlaces.Core;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Evaluators;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Executors;
using Intervals.NET.Caching.VisitedPlaces.Infrastructure.Storage;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure.Helpers;

namespace Intervals.NET.Caching.VisitedPlaces.Unit.Tests.Eviction.Executors;

/// <summary>
/// Unit tests for <see cref="FifoEvictionExecutor{TRange,TData}"/>.
/// </summary>
public sealed class FifoEvictionExecutorTests
{
    private readonly FifoEvictionExecutor<int, int> _executor = new();

    #region UpdateStatistics Tests

    [Fact]
    public void UpdateStatistics_IncrementsHitCount()
    {
        // ARRANGE
        var segment = CreateSegment(0, 5, DateTime.UtcNow);
        var now = DateTime.UtcNow.AddSeconds(5);

        // ACT
        _executor.UpdateStatistics([segment], now);

        // ASSERT
        Assert.Equal(1, segment.Statistics.HitCount);
        Assert.Equal(now, segment.Statistics.LastAccessedAt);
    }

    #endregion

    #region SelectForEviction Tests

    [Fact]
    public void SelectForEviction_ReturnsOldestCreatedSegment()
    {
        // ARRANGE
        var storage = new SnapshotAppendBufferStorage<int, int>();
        var baseTime = DateTime.UtcNow.AddHours(-3);

        var oldest = CreateSegment(0, 5, baseTime);            // oldest CreatedAt
        var middle = CreateSegment(10, 15, baseTime.AddHours(1));
        var newest = CreateSegment(20, 25, baseTime.AddHours(2));

        storage.Add(oldest);
        storage.Add(middle);
        storage.Add(newest);

        var allSegments = storage.GetAllSegments();
        var evaluator = new MaxSegmentCountEvaluator<int, int>(2);

        // ACT
        var toRemove = _executor.SelectForEviction(allSegments, justStored: null, [evaluator]);
        foreach (var s in toRemove) storage.Remove(s);

        // ASSERT — oldest should be removed first
        var remaining = storage.GetAllSegments();
        Assert.DoesNotContain(oldest, remaining);
        Assert.Equal(2, storage.Count);
    }

    [Fact]
    public void SelectForEviction_RespectsJustStoredImmunity()
    {
        // ARRANGE — only segment is justStored
        var storage = new SnapshotAppendBufferStorage<int, int>();
        var justStored = CreateSegment(0, 5, DateTime.UtcNow);
        storage.Add(justStored);

        var allSegments = storage.GetAllSegments();
        var evaluator = new MaxSegmentCountEvaluator<int, int>(1);

        // ACT
        var toRemove = _executor.SelectForEviction(allSegments, justStored: justStored, [evaluator]);

        // ASSERT — no eviction (VPC.E.3a)
        Assert.Empty(toRemove);
        Assert.Equal(1, storage.Count);
    }

    [Fact]
    public void SelectForEviction_RemovesMultipleOldestSegments()
    {
        // ARRANGE
        var storage = new SnapshotAppendBufferStorage<int, int>();
        var baseTime = DateTime.UtcNow.AddHours(-4);

        var seg1 = CreateSegment(0, 5, baseTime);
        var seg2 = CreateSegment(10, 15, baseTime.AddHours(1));
        var seg3 = CreateSegment(20, 25, baseTime.AddHours(2));
        var justStored = CreateSegment(30, 35, baseTime.AddHours(3));

        storage.Add(seg1);
        storage.Add(seg2);
        storage.Add(seg3);
        storage.Add(justStored);

        var allSegments = storage.GetAllSegments();

        // MaxCount=1 → remove 3, but justStored is immune → removes seg1, seg2, seg3
        var evaluator = new MaxSegmentCountEvaluator<int, int>(1);

        // ACT
        var toRemove = _executor.SelectForEviction(allSegments, justStored: justStored, [evaluator]);
        foreach (var s in toRemove) storage.Remove(s);

        // ASSERT
        var remaining = storage.GetAllSegments();
        Assert.Contains(justStored, remaining);
        Assert.DoesNotContain(seg1, remaining);
        Assert.DoesNotContain(seg2, remaining);
        Assert.DoesNotContain(seg3, remaining);
    }

    #endregion

    #region Helpers

    private static CachedSegment<int, int> CreateSegment(int start, int end, DateTime createdAt)
    {
        var range = TestHelpers.CreateRange(start, end);
        var stats = new SegmentStatistics(createdAt);
        return new CachedSegment<int, int>(
            range,
            new ReadOnlyMemory<int>(new int[end - start + 1]),
            stats);
    }

    #endregion
}
