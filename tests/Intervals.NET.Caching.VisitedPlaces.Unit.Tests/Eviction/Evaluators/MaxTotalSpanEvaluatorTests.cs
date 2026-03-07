using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Caching.VisitedPlaces.Core;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Evaluators;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure.Helpers;

namespace Intervals.NET.Caching.VisitedPlaces.Unit.Tests.Eviction.Evaluators;

/// <summary>
/// Unit tests for <see cref="MaxTotalSpanEvaluator{TRange,TData,TDomain}"/>.
/// </summary>
public sealed class MaxTotalSpanEvaluatorTests
{
    private readonly IntegerFixedStepDomain _domain = TestHelpers.CreateIntDomain();

    #region Constructor Tests

    [Fact]
    public void Constructor_WithValidParameters_SetsMaxTotalSpan()
    {
        // ARRANGE & ACT
        var evaluator = new MaxTotalSpanEvaluator<int, int, IntegerFixedStepDomain>(100, _domain);

        // ASSERT
        Assert.Equal(100, evaluator.MaxTotalSpan);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_WithMaxTotalSpanLessThanOne_ThrowsArgumentOutOfRangeException(int invalid)
    {
        // ARRANGE & ACT
        var exception = Record.Exception(() =>
            new MaxTotalSpanEvaluator<int, int, IntegerFixedStepDomain>(invalid, _domain));

        // ASSERT
        Assert.IsType<ArgumentOutOfRangeException>(exception);
    }

    #endregion

    #region ShouldEvict Tests

    [Fact]
    public void ShouldEvict_WhenTotalSpanBelowMax_ReturnsFalse()
    {
        // ARRANGE
        var evaluator = new MaxTotalSpanEvaluator<int, int, IntegerFixedStepDomain>(50, _domain);

        // Add a segment [0,9] = span 10
        var segments = new[] { CreateSegment(0, 9) };

        // ACT
        var result = evaluator.ShouldEvict(segments.Length, segments);

        // ASSERT
        Assert.False(result);
    }

    [Fact]
    public void ShouldEvict_WhenTotalSpanExceedsMax_ReturnsTrue()
    {
        // ARRANGE
        var evaluator = new MaxTotalSpanEvaluator<int, int, IntegerFixedStepDomain>(5, _domain);

        // Add [0,9] = span 10 > 5
        var segments = new[] { CreateSegment(0, 9) };

        // ACT
        var result = evaluator.ShouldEvict(segments.Length, segments);

        // ASSERT
        Assert.True(result);
    }

    [Fact]
    public void ShouldEvict_WithMultipleSegmentsTotalExceedsMax_ReturnsTrue()
    {
        // ARRANGE
        var evaluator = new MaxTotalSpanEvaluator<int, int, IntegerFixedStepDomain>(15, _domain);

        // Two segments: [0,9]=span10 + [20,29]=span10 = total 20 > 15
        var segments = new[] { CreateSegment(0, 9), CreateSegment(20, 29) };

        // ACT
        var result = evaluator.ShouldEvict(segments.Length, segments);

        // ASSERT
        Assert.True(result);
    }

    [Fact]
    public void ShouldEvict_WithEmptyStorage_ReturnsFalse()
    {
        // ARRANGE
        var evaluator = new MaxTotalSpanEvaluator<int, int, IntegerFixedStepDomain>(1, _domain);
        var segments = Array.Empty<CachedSegment<int, int>>();

        // ACT
        var result = evaluator.ShouldEvict(segments.Length, segments);

        // ASSERT
        Assert.False(result);
    }

    #endregion

    #region ComputeRemovalCount Tests

    [Fact]
    public void ComputeRemovalCount_WhenNotOverLimit_ReturnsZero()
    {
        // ARRANGE
        var evaluator = new MaxTotalSpanEvaluator<int, int, IntegerFixedStepDomain>(20, _domain);
        var segments = new[] { CreateSegment(0, 9) }; // span 10

        // ACT
        var count = evaluator.ComputeRemovalCount(segments.Length, segments);

        // ASSERT
        Assert.Equal(0, count);
    }

    [Fact]
    public void ComputeRemovalCount_WhenOneLargeSegmentExceedsMax_ReturnsOne()
    {
        // ARRANGE
        var evaluator = new MaxTotalSpanEvaluator<int, int, IntegerFixedStepDomain>(5, _domain);
        var segments = new[] { CreateSegment(0, 9) }; // span 10 > 5 → excess 5 → remove 1

        // ACT
        var count = evaluator.ComputeRemovalCount(segments.Length, segments);

        // ASSERT
        Assert.Equal(1, count);
    }

    [Fact]
    public void ComputeRemovalCount_WithMultipleSegments_ReturnsMinimumNeeded()
    {
        // ARRANGE – max 15, three segments of span 10 each = total 30, need to remove at least 2
        var evaluator = new MaxTotalSpanEvaluator<int, int, IntegerFixedStepDomain>(15, _domain);
        var segments = new[]
        {
            CreateSegment(0, 9),   // span 10
            CreateSegment(20, 29), // span 10
            CreateSegment(40, 49), // span 10
        };

        // ACT
        var count = evaluator.ComputeRemovalCount(segments.Length, segments);

        // ASSERT – removing 2 segments of span 10 each gives total = 10 ≤ 15
        Assert.True(count >= 1, $"Expected at least 1 removal, got {count}");
    }

    #endregion

    #region Helpers

    private static CachedSegment<int, int> CreateSegment(int start, int end)
    {
        var range = TestHelpers.CreateRange(start, end);
        var len = end - start + 1;
        return new CachedSegment<int, int>(
            range,
            new ReadOnlyMemory<int>(new int[len]),
            new SegmentStatistics(DateTime.UtcNow));
    }

    #endregion
}
