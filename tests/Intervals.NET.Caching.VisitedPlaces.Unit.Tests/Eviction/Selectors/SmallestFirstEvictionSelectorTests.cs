using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Caching.VisitedPlaces.Core;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Selectors;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure.Helpers;

namespace Intervals.NET.Caching.VisitedPlaces.Unit.Tests.Eviction.Selectors;

/// <summary>
/// Unit tests for <see cref="SmallestFirstEvictionSelector{TRange,TData,TDomain}"/>.
/// Validates that <see cref="IEvictionSelector{TRange,TData}.TrySelectCandidate"/> returns the
/// segment with the smallest span from the sample.
/// All datasets are small (≤ SampleSize = 32), so sampling is exhaustive and deterministic.
/// </summary>
public sealed class SmallestFirstEvictionSelectorTests
{
    private static readonly IReadOnlySet<CachedSegment<int, int>> NoImmune =
        new HashSet<CachedSegment<int, int>>();

    private readonly IntegerFixedStepDomain _domain = TestHelpers.CreateIntDomain();

    #region Constructor Tests

    [Fact]
    public void Constructor_WithValidDomain_DoesNotThrow()
    {
        // ARRANGE & ACT
        var exception = Record.Exception(() =>
            new SmallestFirstEvictionSelector<int, int, IntegerFixedStepDomain>(_domain));

        // ASSERT
        Assert.Null(exception);
    }

    #endregion

    #region InitializeMetadata Tests

    [Fact]
    public void InitializeMetadata_SetsSpanOnEvictionMetadata()
    {
        // ARRANGE
        var selector = new SmallestFirstEvictionSelector<int, int, IntegerFixedStepDomain>(_domain);
        var segment = CreateSegmentRaw(10, 19); // span = 10

        // ACT
        selector.InitializeMetadata(segment, DateTime.UtcNow);

        // ASSERT
        var meta = Assert.IsType<SmallestFirstEvictionSelector<int, int, IntegerFixedStepDomain>.SmallestFirstMetadata>(
            segment.EvictionMetadata);
        Assert.Equal(10L, meta.Span);
    }

    [Fact]
    public void InitializeMetadata_OnSegmentWithExistingMetadata_OverwritesMetadata()
    {
        // ARRANGE
        var selector = new SmallestFirstEvictionSelector<int, int, IntegerFixedStepDomain>(_domain);
        var segment = CreateSegmentRaw(0, 4); // span = 5
        selector.InitializeMetadata(segment, DateTime.UtcNow);

        // ACT — re-initialize (e.g., segment re-stored after selector swap)
        selector.InitializeMetadata(segment, DateTime.UtcNow);

        // ASSERT — still correct metadata, not stale
        var meta = Assert.IsType<SmallestFirstEvictionSelector<int, int, IntegerFixedStepDomain>.SmallestFirstMetadata>(
            segment.EvictionMetadata);
        Assert.Equal(5L, meta.Span);
    }

    #endregion

    #region TrySelectCandidate — Returns Smallest-Span Candidate

    [Fact]
    public void TrySelectCandidate_ReturnsTrueAndSelectsSmallestSpan()
    {
        // ARRANGE
        var selector = new SmallestFirstEvictionSelector<int, int, IntegerFixedStepDomain>(_domain);

        var small = CreateSegment(selector, 0, 2);    // span 3
        var large = CreateSegment(selector, 20, 29);  // span 10

        // ACT
        var result = selector.TrySelectCandidate([small, large], NoImmune, out var candidate);

        // ASSERT — smallest span is selected
        Assert.True(result);
        Assert.Same(small, candidate);
    }

    [Fact]
    public void TrySelectCandidate_WithReversedInput_StillSelectsSmallestSpan()
    {
        // ARRANGE
        var selector = new SmallestFirstEvictionSelector<int, int, IntegerFixedStepDomain>(_domain);

        var small = CreateSegment(selector, 0, 2);    // span 3
        var large = CreateSegment(selector, 20, 29);  // span 10

        // ACT
        var result = selector.TrySelectCandidate([large, small], NoImmune, out var candidate);

        // ASSERT — regardless of input order, smallest is found
        Assert.True(result);
        Assert.Same(small, candidate);
    }

    [Fact]
    public void TrySelectCandidate_WithMultipleCandidates_SelectsSmallestSpan()
    {
        // ARRANGE
        var selector = new SmallestFirstEvictionSelector<int, int, IntegerFixedStepDomain>(_domain);

        var small = CreateSegment(selector, 0, 2);    // span 3
        var medium = CreateSegment(selector, 10, 15); // span 6
        var large = CreateSegment(selector, 20, 29);  // span 10

        // ACT
        var result = selector.TrySelectCandidate([large, small, medium], NoImmune, out var candidate);

        // ASSERT — smallest span wins
        Assert.True(result);
        Assert.Same(small, candidate);
    }

    [Fact]
    public void TrySelectCandidate_WithSingleCandidate_ReturnsThatCandidate()
    {
        // ARRANGE
        var selector = new SmallestFirstEvictionSelector<int, int, IntegerFixedStepDomain>(_domain);
        var seg = CreateSegment(selector, 0, 5);

        // ACT
        var result = selector.TrySelectCandidate([seg], NoImmune, out var candidate);

        // ASSERT
        Assert.True(result);
        Assert.Same(seg, candidate);
    }

    [Fact]
    public void TrySelectCandidate_WithEmptyList_ReturnsFalse()
    {
        // ARRANGE
        var selector = new SmallestFirstEvictionSelector<int, int, IntegerFixedStepDomain>(_domain);

        // ACT
        var result = selector.TrySelectCandidate(
            new List<CachedSegment<int, int>>(), NoImmune, out _);

        // ASSERT
        Assert.False(result);
    }

    [Fact]
    public void TrySelectCandidate_WithNoMetadata_FallsBackToLiveSpanComputation()
    {
        // ARRANGE — segments without InitializeMetadata called (metadata = null)
        var selector = new SmallestFirstEvictionSelector<int, int, IntegerFixedStepDomain>(_domain);
        var small = CreateSegmentRaw(0, 2);    // span 3
        var large = CreateSegmentRaw(20, 29);  // span 10

        // ACT — fallback path uses live Range.Span(domain) computation
        var result = selector.TrySelectCandidate([large, small], NoImmune, out var candidate);

        // ASSERT — fallback still selects the smallest span
        Assert.True(result);
        Assert.Same(small, candidate);
    }

    #endregion

    #region TrySelectCandidate — Immunity

    [Fact]
    public void TrySelectCandidate_WhenSmallestIsImmune_SelectsNextSmallest()
    {
        // ARRANGE
        var selector = new SmallestFirstEvictionSelector<int, int, IntegerFixedStepDomain>(_domain);

        var small = CreateSegment(selector, 0, 2);    // span 3 — immune
        var medium = CreateSegment(selector, 10, 15); // span 6
        var large = CreateSegment(selector, 20, 29);  // span 10

        var immune = new HashSet<CachedSegment<int, int>> { small };

        // ACT
        var result = selector.TrySelectCandidate([small, medium, large], immune, out var candidate);

        // ASSERT — small is immune, so medium (next smallest) is selected
        Assert.True(result);
        Assert.Same(medium, candidate);
    }

    [Fact]
    public void TrySelectCandidate_WhenAllCandidatesAreImmune_ReturnsFalse()
    {
        // ARRANGE
        var selector = new SmallestFirstEvictionSelector<int, int, IntegerFixedStepDomain>(_domain);
        var seg = CreateSegment(selector, 0, 5);
        var immune = new HashSet<CachedSegment<int, int>> { seg };

        // ACT
        var result = selector.TrySelectCandidate([seg], immune, out _);

        // ASSERT
        Assert.False(result);
    }

    #endregion

    #region Helpers

    private static CachedSegment<int, int> CreateSegment(
        SmallestFirstEvictionSelector<int, int, IntegerFixedStepDomain> selector,
        int start, int end)
    {
        var segment = CreateSegmentRaw(start, end);
        selector.InitializeMetadata(segment, DateTime.UtcNow);
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
