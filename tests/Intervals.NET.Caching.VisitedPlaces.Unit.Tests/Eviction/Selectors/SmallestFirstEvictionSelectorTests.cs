using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Caching.VisitedPlaces.Core;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Selectors;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure.Helpers;

namespace Intervals.NET.Caching.VisitedPlaces.Unit.Tests.Eviction.Selectors;

/// <summary>
/// Unit tests for <see cref="SmallestFirstEvictionSelector{TRange,TData,TDomain}"/>.
/// Validates that candidates are ordered ascending by span (smallest span first).
/// </summary>
public sealed class SmallestFirstEvictionSelectorTests
{
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

    #region OrderCandidates Tests

    [Fact]
    public void OrderCandidates_ReturnsSmallestSpanFirst()
    {
        // ARRANGE
        var selector = new SmallestFirstEvictionSelector<int, int, IntegerFixedStepDomain>(_domain);

        var small = CreateSegment(0, 2);    // span 3
        var medium = CreateSegment(10, 15); // span 6
        var large = CreateSegment(20, 29);  // span 10

        // ACT
        var ordered = selector.OrderCandidates([large, small, medium]);

        // ASSERT — ascending by span
        Assert.Same(small, ordered[0]);
        Assert.Same(medium, ordered[1]);
        Assert.Same(large, ordered[2]);
    }

    [Fact]
    public void OrderCandidates_WithAlreadySortedInput_PreservesOrder()
    {
        // ARRANGE
        var selector = new SmallestFirstEvictionSelector<int, int, IntegerFixedStepDomain>(_domain);

        var small = CreateSegment(0, 2);    // span 3
        var medium = CreateSegment(10, 15); // span 6
        var large = CreateSegment(20, 29);  // span 10

        // ACT
        var ordered = selector.OrderCandidates([small, medium, large]);

        // ASSERT
        Assert.Same(small, ordered[0]);
        Assert.Same(medium, ordered[1]);
        Assert.Same(large, ordered[2]);
    }

    [Fact]
    public void OrderCandidates_WithSingleCandidate_ReturnsSingleElement()
    {
        // ARRANGE
        var selector = new SmallestFirstEvictionSelector<int, int, IntegerFixedStepDomain>(_domain);
        var seg = CreateSegment(0, 5);

        // ACT
        var ordered = selector.OrderCandidates([seg]);

        // ASSERT
        Assert.Single(ordered);
        Assert.Same(seg, ordered[0]);
    }

    [Fact]
    public void OrderCandidates_WithEmptyList_ReturnsEmptyList()
    {
        // ARRANGE
        var selector = new SmallestFirstEvictionSelector<int, int, IntegerFixedStepDomain>(_domain);

        // ACT
        var ordered = selector.OrderCandidates([]);

        // ASSERT
        Assert.Empty(ordered);
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

    #endregion
}
