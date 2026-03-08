using Intervals.NET.Caching.VisitedPlaces.Core;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Selectors;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure.Helpers;

namespace Intervals.NET.Caching.VisitedPlaces.Unit.Tests.Eviction.Selectors;

/// <summary>
/// Unit tests for <see cref="FifoEvictionSelector{TRange,TData}"/>.
/// Validates that <see cref="IEvictionSelector{TRange,TData}.TrySelectCandidate"/> returns the
/// oldest created segment (oldest <c>CreatedAt</c>) from the sample.
/// All datasets are small (≤ SampleSize = 32), so sampling is exhaustive and deterministic.
/// </summary>
public sealed class FifoEvictionSelectorTests
{
    private static readonly IReadOnlySet<CachedSegment<int, int>> NoImmune =
        new HashSet<CachedSegment<int, int>>();

    private readonly FifoEvictionSelector<int, int> _selector = new();

    #region TrySelectCandidate — Returns FIFO Candidate

    [Fact]
    public void TrySelectCandidate_ReturnsTrueAndSelectsOldestCreated()
    {
        // ARRANGE
        var baseTime = DateTime.UtcNow.AddHours(-3);
        var oldest = CreateSegment(0, 5, baseTime);
        var newest = CreateSegment(10, 15, baseTime.AddHours(2));

        // ACT
        var result = _selector.TrySelectCandidate([oldest, newest], NoImmune, out var candidate);

        // ASSERT — oldest (FIFO) is selected
        Assert.True(result);
        Assert.Same(oldest, candidate);
    }

    [Fact]
    public void TrySelectCandidate_WithReversedInput_StillSelectsOldestCreated()
    {
        // ARRANGE — input in reverse order (newest first)
        var baseTime = DateTime.UtcNow.AddHours(-3);
        var oldest = CreateSegment(0, 5, baseTime);
        var newest = CreateSegment(10, 15, baseTime.AddHours(2));

        // ACT
        var result = _selector.TrySelectCandidate([newest, oldest], NoImmune, out var candidate);

        // ASSERT — still selects the oldest regardless of input order
        Assert.True(result);
        Assert.Same(oldest, candidate);
    }

    [Fact]
    public void TrySelectCandidate_WithMultipleCandidates_SelectsOldestCreated()
    {
        // ARRANGE
        var baseTime = DateTime.UtcNow.AddHours(-4);
        var seg1 = CreateSegment(0, 5, baseTime);                   // oldest
        var seg2 = CreateSegment(10, 15, baseTime.AddHours(1));
        var seg3 = CreateSegment(20, 25, baseTime.AddHours(2));
        var seg4 = CreateSegment(30, 35, baseTime.AddHours(3));     // newest

        // ACT
        var result = _selector.TrySelectCandidate([seg3, seg1, seg4, seg2], NoImmune, out var candidate);

        // ASSERT — seg1 has oldest CreatedAt → selected by FIFO
        Assert.True(result);
        Assert.Same(seg1, candidate);
    }

    [Fact]
    public void TrySelectCandidate_WithSingleCandidate_ReturnsThatCandidate()
    {
        // ARRANGE
        var seg = CreateSegment(0, 5, DateTime.UtcNow);

        // ACT
        var result = _selector.TrySelectCandidate([seg], NoImmune, out var candidate);

        // ASSERT
        Assert.True(result);
        Assert.Same(seg, candidate);
    }

    [Fact]
    public void TrySelectCandidate_WithEmptyList_ReturnsFalse()
    {
        // ARRANGE & ACT
        var result = _selector.TrySelectCandidate(
            new List<CachedSegment<int, int>>(), NoImmune, out _);

        // ASSERT
        Assert.False(result);
    }

    #endregion

    #region TrySelectCandidate — Immunity

    [Fact]
    public void TrySelectCandidate_WhenOldestIsImmune_SelectsNextOldest()
    {
        // ARRANGE
        var baseTime = DateTime.UtcNow.AddHours(-3);
        var oldest = CreateSegment(0, 5, baseTime);           // FIFO — immune
        var newest = CreateSegment(10, 15, baseTime.AddHours(2));

        var immune = new HashSet<CachedSegment<int, int>> { oldest };

        // ACT
        var result = _selector.TrySelectCandidate([oldest, newest], immune, out var candidate);

        // ASSERT — oldest is immune, so next oldest (newest) is selected
        Assert.True(result);
        Assert.Same(newest, candidate);
    }

    [Fact]
    public void TrySelectCandidate_WhenAllCandidatesAreImmune_ReturnsFalse()
    {
        // ARRANGE
        var seg = CreateSegment(0, 5, DateTime.UtcNow);
        var immune = new HashSet<CachedSegment<int, int>> { seg };

        // ACT
        var result = _selector.TrySelectCandidate([seg], immune, out _);

        // ASSERT
        Assert.False(result);
    }

    #endregion

    #region InitializeMetadata / UpdateMetadata

    [Fact]
    public void InitializeMetadata_SetsCreatedAt()
    {
        // ARRANGE
        var segment = CreateSegmentRaw(0, 5);
        var now = DateTime.UtcNow;

        // ACT
        _selector.InitializeMetadata(segment, now);

        // ASSERT
        var meta = Assert.IsType<FifoEvictionSelector<int, int>.FifoMetadata>(segment.EvictionMetadata);
        Assert.Equal(now, meta.CreatedAt);
    }

    [Fact]
    public void UpdateMetadata_IsNoOp_DoesNotChangeCreatedAt()
    {
        // ARRANGE — FIFO metadata is immutable; UpdateMetadata should not change CreatedAt
        var originalTime = DateTime.UtcNow.AddHours(-1);
        var segment = CreateSegment(0, 5, originalTime);
        var laterTime = DateTime.UtcNow;

        // ACT
        _selector.UpdateMetadata([segment], laterTime);

        // ASSERT — CreatedAt unchanged (FIFO is immutable after initialization)
        var meta = Assert.IsType<FifoEvictionSelector<int, int>.FifoMetadata>(segment.EvictionMetadata);
        Assert.Equal(originalTime, meta.CreatedAt);
    }

    #endregion

    #region Helpers

    private static CachedSegment<int, int> CreateSegment(int start, int end, DateTime createdAt)
    {
        var segment = CreateSegmentRaw(start, end);
        segment.EvictionMetadata = new FifoEvictionSelector<int, int>.FifoMetadata(createdAt);
        return segment;
    }

    private static CachedSegment<int, int> CreateSegmentRaw(int start, int end)
    {
        var range = TestHelpers.CreateRange(start, end);
        return new CachedSegment<int, int>(
            range,
            new ReadOnlyMemory<int>(new int[end - start + 1]));
    }

    #endregion
}
