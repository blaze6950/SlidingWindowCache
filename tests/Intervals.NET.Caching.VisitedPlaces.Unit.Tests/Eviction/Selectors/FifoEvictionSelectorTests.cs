using Intervals.NET.Caching.VisitedPlaces.Core;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Selectors;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure.Helpers;

namespace Intervals.NET.Caching.VisitedPlaces.Unit.Tests.Eviction.Selectors;

/// <summary>
/// Unit tests for <see cref="FifoEvictionSelector{TRange,TData}"/>.
/// Validates that candidates are ordered ascending by CreatedAt (FIFO = oldest created first).
/// </summary>
public sealed class FifoEvictionSelectorTests
{
    private readonly FifoEvictionSelector<int, int> _selector = new();

    #region OrderCandidates Tests

    [Fact]
    public void OrderCandidates_ReturnsOldestCreatedFirst()
    {
        // ARRANGE
        var baseTime = DateTime.UtcNow.AddHours(-3);
        var oldest = CreateSegment(0, 5, baseTime);
        var newest = CreateSegment(10, 15, baseTime.AddHours(2));

        // ACT
        var ordered = _selector.OrderCandidates([oldest, newest]);

        // ASSERT
        Assert.Equal(2, ordered.Count);
        Assert.Same(oldest, ordered[0]);
        Assert.Same(newest, ordered[1]);
    }

    [Fact]
    public void OrderCandidates_WithReversedInput_StillOrdersByCreatedAtAscending()
    {
        // ARRANGE
        var baseTime = DateTime.UtcNow.AddHours(-3);
        var oldest = CreateSegment(0, 5, baseTime);
        var newest = CreateSegment(10, 15, baseTime.AddHours(2));

        // ACT
        var ordered = _selector.OrderCandidates([newest, oldest]);

        // ASSERT
        Assert.Same(oldest, ordered[0]);
        Assert.Same(newest, ordered[1]);
    }

    [Fact]
    public void OrderCandidates_WithMultipleCandidates_OrdersAllCorrectly()
    {
        // ARRANGE
        var baseTime = DateTime.UtcNow.AddHours(-4);
        var seg1 = CreateSegment(0, 5, baseTime);                   // oldest
        var seg2 = CreateSegment(10, 15, baseTime.AddHours(1));
        var seg3 = CreateSegment(20, 25, baseTime.AddHours(2));
        var seg4 = CreateSegment(30, 35, baseTime.AddHours(3));      // newest

        // ACT
        var ordered = _selector.OrderCandidates([seg3, seg1, seg4, seg2]);

        // ASSERT
        Assert.Same(seg1, ordered[0]);
        Assert.Same(seg2, ordered[1]);
        Assert.Same(seg3, ordered[2]);
        Assert.Same(seg4, ordered[3]);
    }

    [Fact]
    public void OrderCandidates_WithSingleCandidate_ReturnsSingleElement()
    {
        // ARRANGE
        var seg = CreateSegment(0, 5, DateTime.UtcNow);

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

    private static CachedSegment<int, int> CreateSegment(int start, int end, DateTime createdAt)
    {
        var range = TestHelpers.CreateRange(start, end);
        var segment = new CachedSegment<int, int>(
            range,
            new ReadOnlyMemory<int>(new int[end - start + 1]));
        segment.EvictionMetadata = new FifoEvictionSelector<int, int>.FifoMetadata(createdAt);
        return segment;
    }

    #endregion
}
