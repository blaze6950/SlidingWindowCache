using Intervals.NET.Caching.VisitedPlaces.Core;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Evaluators;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Executors;
using Intervals.NET.Caching.VisitedPlaces.Infrastructure.Storage;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure.Helpers;

namespace Intervals.NET.Caching.VisitedPlaces.Unit.Tests.Eviction.Executors;

/// <summary>
/// Unit tests for <see cref="LruEvictionExecutor{TRange,TData}"/>.
/// </summary>
public sealed class LruEvictionExecutorTests
{
    private readonly LruEvictionExecutor<int, int> _executor = new();

    #region UpdateStatistics Tests

    [Fact]
    public void UpdateStatistics_WithSingleSegment_IncrementsHitCountAndSetsLastAccessedAt()
    {
        // ARRANGE
        var segment = CreateSegment(0, 5);
        var before = DateTime.UtcNow;

        // ACT
        _executor.UpdateStatistics([segment], before.AddSeconds(1));

        // ASSERT
        Assert.Equal(1, segment.Statistics.HitCount);
        Assert.Equal(before.AddSeconds(1), segment.Statistics.LastAccessedAt);
    }

    [Fact]
    public void UpdateStatistics_WithMultipleSegments_UpdatesAll()
    {
        // ARRANGE
        var s1 = CreateSegment(0, 5);
        var s2 = CreateSegment(10, 15);
        var now = DateTime.UtcNow;

        // ACT
        _executor.UpdateStatistics([s1, s2], now);

        // ASSERT
        Assert.Equal(1, s1.Statistics.HitCount);
        Assert.Equal(1, s2.Statistics.HitCount);
    }

    [Fact]
    public void UpdateStatistics_WithEmptyList_DoesNotThrow()
    {
        // ARRANGE & ACT
        var exception = Record.Exception(() =>
            _executor.UpdateStatistics([], DateTime.UtcNow));

        // ASSERT
        Assert.Null(exception);
    }

    #endregion

    #region SelectForEviction Tests

    [Fact]
    public void SelectForEviction_ReturnsLeastRecentlyUsedSegment()
    {
        // ARRANGE
        var storage = new SnapshotAppendBufferStorage<int, int>();
        var old = CreateSegmentWithLastAccess(0, 5, DateTime.UtcNow.AddHours(-2));
        var recent = CreateSegmentWithLastAccess(10, 15, DateTime.UtcNow);

        storage.Add(old);
        storage.Add(recent);

        var allSegments = storage.GetAllSegments();
        var evaluator = new MaxSegmentCountEvaluator<int, int>(1);

        // ACT
        var toRemove = _executor.SelectForEviction(allSegments, justStored: null, [evaluator]);
        foreach (var s in toRemove) storage.Remove(s);

        // ASSERT
        Assert.Equal(1, storage.Count);
        var remaining = storage.GetAllSegments();
        Assert.DoesNotContain(old, remaining);
        Assert.Contains(recent, remaining);
    }

    [Fact]
    public void SelectForEviction_RespectsJustStoredImmunity()
    {
        // ARRANGE — only segment is justStored, so no eviction possible (VPC.E.3a)
        var storage = new SnapshotAppendBufferStorage<int, int>();
        var justStored = CreateSegment(0, 5);
        storage.Add(justStored);

        var allSegments = storage.GetAllSegments();
        var evaluator = new MaxSegmentCountEvaluator<int, int>(1);

        // ACT
        var toRemove = _executor.SelectForEviction(allSegments, justStored: justStored, [evaluator]);

        // ASSERT — nothing selected for eviction
        Assert.Empty(toRemove);
        Assert.Equal(1, storage.Count);
    }

    [Fact]
    public void SelectForEviction_WithMultipleCandidates_RemovesCorrectCount()
    {
        // ARRANGE
        var storage = new SnapshotAppendBufferStorage<int, int>();
        var baseTime = DateTime.UtcNow.AddHours(-3);

        // Add 4 segments with different access times
        var seg1 = CreateSegmentWithLastAccess(0, 5, baseTime);
        var seg2 = CreateSegmentWithLastAccess(10, 15, baseTime.AddHours(1));
        var seg3 = CreateSegmentWithLastAccess(20, 25, baseTime.AddHours(2));
        var seg4 = CreateSegmentWithLastAccess(30, 35, baseTime.AddHours(3)); // justStored

        storage.Add(seg1);
        storage.Add(seg2);
        storage.Add(seg3);
        storage.Add(seg4);

        var allSegments = storage.GetAllSegments();

        // MaxCount=2, justStored=seg4 → should select 2 oldest (seg1, seg2)
        var evaluator = new MaxSegmentCountEvaluator<int, int>(2);

        // ACT
        var toRemove = _executor.SelectForEviction(allSegments, justStored: seg4, [evaluator]);
        foreach (var s in toRemove) storage.Remove(s);

        // ASSERT
        Assert.Equal(2, storage.Count);
        var remaining = storage.GetAllSegments();
        Assert.DoesNotContain(seg1, remaining);
        Assert.DoesNotContain(seg2, remaining);
        Assert.Contains(seg3, remaining);
        Assert.Contains(seg4, remaining);
    }

    // Note: SelectForEviction is only called by BackgroundEventProcessor when at least one evaluator
    // has fired (Invariant VPC.E.2a). Calling it with an empty firedEvaluators list is not a supported
    // scenario; no test is provided for this case.

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

    private static CachedSegment<int, int> CreateSegmentWithLastAccess(int start, int end, DateTime lastAccess)
    {
        var range = TestHelpers.CreateRange(start, end);
        var stats = new SegmentStatistics(lastAccess);
        return new CachedSegment<int, int>(
            range,
            new ReadOnlyMemory<int>(new int[end - start + 1]),
            stats);
    }

    #endregion
}
