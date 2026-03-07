using Intervals.NET.Caching.VisitedPlaces.Core;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Evaluators;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure.Helpers;

namespace Intervals.NET.Caching.VisitedPlaces.Unit.Tests.Eviction.Evaluators;

/// <summary>
/// Unit tests for <see cref="MaxSegmentCountEvaluator{TRange,TData}"/>.
/// </summary>
public sealed class MaxSegmentCountEvaluatorTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_WithValidMaxCount_SetsMaxCount()
    {
        // ARRANGE & ACT
        var evaluator = new MaxSegmentCountEvaluator<int, int>(5);

        // ASSERT
        Assert.Equal(5, evaluator.MaxCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Constructor_WithMaxCountLessThanOne_ThrowsArgumentOutOfRangeException(int invalidMaxCount)
    {
        // ARRANGE & ACT
        var exception = Record.Exception(() => new MaxSegmentCountEvaluator<int, int>(invalidMaxCount));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentOutOfRangeException>(exception);
    }

    [Fact]
    public void Constructor_WithMaxCountOfOne_IsValid()
    {
        // ARRANGE & ACT
        var exception = Record.Exception(() => new MaxSegmentCountEvaluator<int, int>(1));

        // ASSERT
        Assert.Null(exception);
    }

    #endregion

    #region ShouldEvict Tests

    [Fact]
    public void ShouldEvict_WhenCountBelowMax_ReturnsFalse()
    {
        // ARRANGE
        var evaluator = new MaxSegmentCountEvaluator<int, int>(3);
        var segments = CreateSegments(2);

        // ACT
        var result = evaluator.ShouldEvict(segments.Count, segments);

        // ASSERT
        Assert.False(result);
    }

    [Fact]
    public void ShouldEvict_WhenCountEqualsMax_ReturnsFalse()
    {
        // ARRANGE
        var evaluator = new MaxSegmentCountEvaluator<int, int>(3);
        var segments = CreateSegments(3);

        // ACT
        var result = evaluator.ShouldEvict(segments.Count, segments);

        // ASSERT
        Assert.False(result);
    }

    [Fact]
    public void ShouldEvict_WhenCountExceedsMax_ReturnsTrue()
    {
        // ARRANGE
        var evaluator = new MaxSegmentCountEvaluator<int, int>(3);
        var segments = CreateSegments(4);

        // ACT
        var result = evaluator.ShouldEvict(segments.Count, segments);

        // ASSERT
        Assert.True(result);
    }

    [Fact]
    public void ShouldEvict_WhenStorageEmpty_ReturnsFalse()
    {
        // ARRANGE
        var evaluator = new MaxSegmentCountEvaluator<int, int>(1);
        var segments = CreateSegments(0);

        // ACT
        var result = evaluator.ShouldEvict(segments.Count, segments);

        // ASSERT
        Assert.False(result);
    }

    #endregion

    #region ComputeRemovalCount Tests

    [Fact]
    public void ComputeRemovalCount_WhenCountAtMax_ReturnsZero()
    {
        // ARRANGE
        var evaluator = new MaxSegmentCountEvaluator<int, int>(3);
        var segments = CreateSegments(3);

        // ACT
        var count = evaluator.ComputeRemovalCount(segments.Count, segments);

        // ASSERT
        Assert.Equal(0, count);
    }

    [Fact]
    public void ComputeRemovalCount_WhenCountExceedsByOne_ReturnsOne()
    {
        // ARRANGE
        var evaluator = new MaxSegmentCountEvaluator<int, int>(3);
        var segments = CreateSegments(4);

        // ACT
        var count = evaluator.ComputeRemovalCount(segments.Count, segments);

        // ASSERT
        Assert.Equal(1, count);
    }

    [Fact]
    public void ComputeRemovalCount_WhenCountExceedsByMany_ReturnsExcess()
    {
        // ARRANGE
        var evaluator = new MaxSegmentCountEvaluator<int, int>(3);
        var segments = CreateSegments(7);

        // ACT
        var count = evaluator.ComputeRemovalCount(segments.Count, segments);

        // ASSERT
        Assert.Equal(4, count);
    }

    [Fact]
    public void ComputeRemovalCount_WhenStorageEmpty_ReturnsZero()
    {
        // ARRANGE
        var evaluator = new MaxSegmentCountEvaluator<int, int>(3);
        var segments = CreateSegments(0);

        // ACT
        var count = evaluator.ComputeRemovalCount(segments.Count, segments);

        // ASSERT
        Assert.Equal(0, count);
    }

    #endregion

    #region Helpers

    private static IReadOnlyList<CachedSegment<int, int>> CreateSegments(int count)
    {
        var result = new List<CachedSegment<int, int>>();
        for (var i = 0; i < count; i++)
        {
            var start = i * 10;
            var range = TestHelpers.CreateRange(start, start + 5);
            result.Add(new CachedSegment<int, int>(
                range,
                new ReadOnlyMemory<int>(new int[6]),
                new SegmentStatistics(DateTime.UtcNow)));
        }
        return result;
    }

    #endregion
}
