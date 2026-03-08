using Intervals.NET.Caching.VisitedPlaces.Core;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Selectors;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure.Helpers;

namespace Intervals.NET.Caching.VisitedPlaces.Unit.Tests.Eviction.Selectors;

/// <summary>
/// Unit tests for <see cref="LruEvictionSelector{TRange,TData}"/>.
/// Validates that <see cref="IEvictionSelector{TRange,TData}.TrySelectCandidate"/> returns the
/// least recently used segment (oldest <c>LastAccessedAt</c>) from the sample.
/// All datasets are small (≤ SampleSize = 32), so sampling is exhaustive and deterministic.
/// </summary>
public sealed class LruEvictionSelectorTests
{
    private static readonly IReadOnlySet<CachedSegment<int, int>> NoImmune =
        new HashSet<CachedSegment<int, int>>();

    private readonly LruEvictionSelector<int, int> _selector = new();

    #region TrySelectCandidate — Returns LRU Candidate

    [Fact]
    public void TrySelectCandidate_ReturnsTrueAndSelectsLeastRecentlyUsed()
    {
        // ARRANGE
        var baseTime = DateTime.UtcNow;
        var old = CreateSegmentWithLastAccess(0, 5, baseTime.AddHours(-2));
        var recent = CreateSegmentWithLastAccess(10, 15, baseTime);

        // ACT
        var result = _selector.TrySelectCandidate([old, recent], NoImmune, out var candidate);

        // ASSERT — old (least recently used) is selected
        Assert.True(result);
        Assert.Same(old, candidate);
    }

    [Fact]
    public void TrySelectCandidate_WithReversedInput_StillSelectsLeastRecentlyUsed()
    {
        // ARRANGE — input in reverse order (recent first)
        var baseTime = DateTime.UtcNow;
        var old = CreateSegmentWithLastAccess(0, 5, baseTime.AddHours(-2));
        var recent = CreateSegmentWithLastAccess(10, 15, baseTime);

        // ACT
        var result = _selector.TrySelectCandidate([recent, old], NoImmune, out var candidate);

        // ASSERT — still selects the LRU regardless of input order
        Assert.True(result);
        Assert.Same(old, candidate);
    }

    [Fact]
    public void TrySelectCandidate_WithMultipleCandidates_SelectsOldestAccess()
    {
        // ARRANGE
        var baseTime = DateTime.UtcNow.AddHours(-3);
        var seg1 = CreateSegmentWithLastAccess(0, 5, baseTime);                  // oldest access
        var seg2 = CreateSegmentWithLastAccess(10, 15, baseTime.AddHours(1));
        var seg3 = CreateSegmentWithLastAccess(20, 25, baseTime.AddHours(2));
        var seg4 = CreateSegmentWithLastAccess(30, 35, baseTime.AddHours(3));    // most recent

        // ACT
        var result = _selector.TrySelectCandidate([seg3, seg1, seg4, seg2], NoImmune, out var candidate);

        // ASSERT — seg1 has oldest LastAccessedAt → selected by LRU
        Assert.True(result);
        Assert.Same(seg1, candidate);
    }

    [Fact]
    public void TrySelectCandidate_WithSingleCandidate_ReturnsThatCandidate()
    {
        // ARRANGE
        var seg = CreateSegmentWithLastAccess(0, 5, DateTime.UtcNow);

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
            new List<CachedSegment<int, int>>(), NoImmune, out var candidate);

        // ASSERT
        Assert.False(result);
    }

    #endregion

    #region TrySelectCandidate — Immunity

    [Fact]
    public void TrySelectCandidate_WhenLruCandidateIsImmune_SelectsNextLru()
    {
        // ARRANGE
        var baseTime = DateTime.UtcNow;
        var old = CreateSegmentWithLastAccess(0, 5, baseTime.AddHours(-2));      // LRU — immune
        var recent = CreateSegmentWithLastAccess(10, 15, baseTime);

        var immune = new HashSet<CachedSegment<int, int>> { old };

        // ACT
        var result = _selector.TrySelectCandidate([old, recent], immune, out var candidate);

        // ASSERT — old is immune, so next LRU (recent) is selected
        Assert.True(result);
        Assert.Same(recent, candidate);
    }

    [Fact]
    public void TrySelectCandidate_WhenAllCandidatesAreImmune_ReturnsFalse()
    {
        // ARRANGE
        var seg = CreateSegmentWithLastAccess(0, 5, DateTime.UtcNow);
        var immune = new HashSet<CachedSegment<int, int>> { seg };

        // ACT
        var result = _selector.TrySelectCandidate([seg], immune, out _);

        // ASSERT
        Assert.False(result);
    }

    #endregion

    #region InitializeMetadata / UpdateMetadata

    [Fact]
    public void InitializeMetadata_SetsLastAccessedAt()
    {
        // ARRANGE
        var segment = CreateSegmentRaw(0, 5);
        var now = DateTime.UtcNow;

        // ACT
        _selector.InitializeMetadata(segment, now);

        // ASSERT
        var meta = Assert.IsType<LruEvictionSelector<int, int>.LruMetadata>(segment.EvictionMetadata);
        Assert.Equal(now, meta.LastAccessedAt);
    }

    [Fact]
    public void UpdateMetadata_RefreshesLastAccessedAt()
    {
        // ARRANGE
        var segment = CreateSegmentWithLastAccess(0, 5, DateTime.UtcNow.AddHours(-1));
        var newTime = DateTime.UtcNow;

        // ACT
        _selector.UpdateMetadata([segment], newTime);

        // ASSERT
        var meta = Assert.IsType<LruEvictionSelector<int, int>.LruMetadata>(segment.EvictionMetadata);
        Assert.Equal(newTime, meta.LastAccessedAt);
    }

    [Fact]
    public void UpdateMetadata_WithNullMetadata_LazilyInitializesMetadata()
    {
        // ARRANGE — segment has no metadata yet
        var segment = CreateSegmentRaw(0, 5);
        var now = DateTime.UtcNow;

        // ACT
        _selector.UpdateMetadata([segment], now);

        // ASSERT — metadata lazily created
        var meta = Assert.IsType<LruEvictionSelector<int, int>.LruMetadata>(segment.EvictionMetadata);
        Assert.Equal(now, meta.LastAccessedAt);
    }

    #endregion

    #region Helpers

    private static CachedSegment<int, int> CreateSegmentWithLastAccess(int start, int end, DateTime lastAccess)
    {
        var segment = CreateSegmentRaw(start, end);
        segment.EvictionMetadata = new LruEvictionSelector<int, int>.LruMetadata(lastAccess);
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
