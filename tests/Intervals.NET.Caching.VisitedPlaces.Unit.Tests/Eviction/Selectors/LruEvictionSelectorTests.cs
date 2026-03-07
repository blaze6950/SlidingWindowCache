using Intervals.NET.Caching.VisitedPlaces.Core;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Selectors;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure.Helpers;

namespace Intervals.NET.Caching.VisitedPlaces.Unit.Tests.Eviction.Selectors;

/// <summary>
/// Unit tests for <see cref="LruEvictionSelector{TRange,TData}"/>.
/// Validates that candidates are ordered ascending by LastAccessedAt (LRU = least recently used first).
/// </summary>
public sealed class LruEvictionSelectorTests
{
    private readonly LruEvictionSelector<int, int> _selector = new();

    #region OrderCandidates Tests

    [Fact]
    public void OrderCandidates_ReturnsLeastRecentlyUsedFirst()
    {
        // ARRANGE
        var baseTime = DateTime.UtcNow;
        var old = CreateSegmentWithLastAccess(0, 5, baseTime.AddHours(-2));
        var recent = CreateSegmentWithLastAccess(10, 15, baseTime);

        // ACT
        var ordered = _selector.OrderCandidates([old, recent]);

        // ASSERT — old (least recently used) is first
        Assert.Equal(2, ordered.Count);
        Assert.Same(old, ordered[0]);
        Assert.Same(recent, ordered[1]);
    }

    [Fact]
    public void OrderCandidates_WithReversedInput_StillOrdersByLastAccessedAtAscending()
    {
        // ARRANGE — input in wrong order (recent first)
        var baseTime = DateTime.UtcNow;
        var old = CreateSegmentWithLastAccess(0, 5, baseTime.AddHours(-2));
        var recent = CreateSegmentWithLastAccess(10, 15, baseTime);

        // ACT
        var ordered = _selector.OrderCandidates([recent, old]);

        // ASSERT — corrected to ascending order
        Assert.Same(old, ordered[0]);
        Assert.Same(recent, ordered[1]);
    }

    [Fact]
    public void OrderCandidates_WithMultipleCandidates_OrdersAllCorrectly()
    {
        // ARRANGE
        var baseTime = DateTime.UtcNow.AddHours(-3);
        var seg1 = CreateSegmentWithLastAccess(0, 5, baseTime);                  // oldest access
        var seg2 = CreateSegmentWithLastAccess(10, 15, baseTime.AddHours(1));
        var seg3 = CreateSegmentWithLastAccess(20, 25, baseTime.AddHours(2));
        var seg4 = CreateSegmentWithLastAccess(30, 35, baseTime.AddHours(3));     // most recent

        // ACT
        var ordered = _selector.OrderCandidates([seg3, seg1, seg4, seg2]);

        // ASSERT — ascending by LastAccessedAt
        Assert.Same(seg1, ordered[0]);
        Assert.Same(seg2, ordered[1]);
        Assert.Same(seg3, ordered[2]);
        Assert.Same(seg4, ordered[3]);
    }

    [Fact]
    public void OrderCandidates_WithSingleCandidate_ReturnsSingleElement()
    {
        // ARRANGE
        var seg = CreateSegmentWithLastAccess(0, 5, DateTime.UtcNow);

        // ACT
        var ordered = _selector.OrderCandidates([seg]);

        // ASSERT
        Assert.Single(ordered);
        Assert.Same(seg, ordered[0]);
    }

    [Fact]
    public void OrderCandidates_WithEmptyList_ReturnsEmptyList()
    {
        // ARRANGE & ACT
        var ordered = _selector.OrderCandidates([]);

        // ASSERT
        Assert.Empty(ordered);
    }

    #endregion

    #region Helpers

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
