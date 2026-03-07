using Intervals.NET.Caching.Extensions;
using Intervals.NET.Domain.Abstractions;
using Intervals.NET.Domain.Default.Numeric;
using Moq;

namespace Intervals.NET.Caching.SlidingWindow.Unit.Tests.Infrastructure.Extensions;

/// <summary>
/// Unit tests for IntervalsNetDomainExtensions that verify domain-agnostic extension methods
/// work correctly with both fixed-step and variable-step domains.
/// </summary>
public class IntervalsNetDomainExtensionsTests
{
    #region Span Method Tests

    [Fact]
    public void Span_WithFixedStepDomain_ReturnsCorrectStepCount()
    {
        // ARRANGE
        var domain = new IntegerFixedStepDomain();
        var range = Factories.Range.Closed<int>(10, 20);

        // ACT
        var span = range.Span(domain);

        // ASSERT
        Assert.Equal(11, span.Value); // [10, 20] inclusive = 11 steps
        Assert.True(span.IsFinite);
    }

    [Fact]
    public void Span_WithFixedStepDomain_SinglePoint_ReturnsOne()
    {
        // ARRANGE
        var domain = new IntegerFixedStepDomain();
        var range = Factories.Range.Closed<int>(5, 5);

        // ACT
        var span = range.Span(domain);

        // ASSERT
        Assert.Equal(1, span.Value); // Single point = 1 step
        Assert.True(span.IsFinite);
    }

    [Fact]
    public void Span_WithFixedStepDomain_LargeRange_ReturnsCorrectCount()
    {
        // ARRANGE
        var domain = new IntegerFixedStepDomain();
        var range = Factories.Range.Closed<int>(0, 100);

        // ACT
        var span = range.Span(domain);

        // ASSERT
        Assert.Equal(101, span.Value); // [0, 100] inclusive = 101 steps
        Assert.True(span.IsFinite);
    }

    [Fact]
    public void Span_WithVariableStepDomain_ReturnsCorrectStepCount()
    {
        // ARRANGE - Create a variable-step domain with custom steps
        var steps = new[] { 1, 2, 5, 10, 20, 50 };
        var domain = new IntegerVariableStepDomain(steps);
        var range = Factories.Range.Closed<int>(1, 20);

        // ACT
        var span = range.Span(domain);

        // ASSERT
        Assert.Equal(5, span.Value); // Steps: 1, 2, 5, 10, 20 = 5 steps
        Assert.True(span.IsFinite);
    }

    [Fact]
    public void Span_WithVariableStepDomain_PartialRange_ReturnsCorrectStepCount()
    {
        // ARRANGE
        var steps = new[] { 1, 2, 5, 10, 20, 50, 100 };
        var domain = new IntegerVariableStepDomain(steps);
        var range = Factories.Range.Closed<int>(5, 50);

        // ACT
        var span = range.Span(domain);

        // ASSERT
        Assert.Equal(4, span.Value); // Steps: 5, 10, 20, 50 = 4 steps
        Assert.True(span.IsFinite);
    }

    [Fact]
    public void Span_WithUnsupportedDomain_ThrowsNotSupportedException()
    {
        // ARRANGE - Create a mock domain that doesn't implement either interface
        var mockDomain = new Mock<IRangeDomain<int>>();
        var range = Factories.Range.Closed<int>(10, 20);

        // ACT & ASSERT
        var exception = Assert.Throws<NotSupportedException>(() => range.Span(mockDomain.Object));
        Assert.Contains("must implement either IFixedStepDomain<T> or IVariableStepDomain<T>", exception.Message);
    }

    #endregion

    #region Expand Method Tests

    [Fact]
    public void Expand_WithFixedStepDomain_ExpandsBothSides()
    {
        // ARRANGE
        var domain = new IntegerFixedStepDomain();
        var range = Factories.Range.Closed<int>(10, 20);

        // ACT
        var expanded = range.Expand(domain, left: 5, right: 3);

        // ASSERT
        Assert.Equal(5, expanded.Start.Value);  // 10 - 5 = 5
        Assert.Equal(23, expanded.End.Value);    // 20 + 3 = 23
    }

    [Fact]
    public void Expand_WithFixedStepDomain_ZeroExpansion_ReturnsSameRange()
    {
        // ARRANGE
        var domain = new IntegerFixedStepDomain();
        var range = Factories.Range.Closed<int>(10, 20);

        // ACT
        var expanded = range.Expand(domain, left: 0, right: 0);

        // ASSERT
        Assert.Equal(10, expanded.Start.Value);
        Assert.Equal(20, expanded.End.Value);
    }

    [Fact]
    public void Expand_WithFixedStepDomain_NegativeExpansion_Shrinks()
    {
        // ARRANGE
        var domain = new IntegerFixedStepDomain();
        var range = Factories.Range.Closed<int>(10, 30);

        // ACT
        var shrunk = range.Expand(domain, left: -2, right: -3);

        // ASSERT
        Assert.Equal(12, shrunk.Start.Value);  // 10 + 2 = 12
        Assert.Equal(27, shrunk.End.Value);    // 30 - 3 = 27
    }

    [Fact]
    public void Expand_WithFixedStepDomain_OnlyLeft_ExpandsLeftSide()
    {
        // ARRANGE
        var domain = new IntegerFixedStepDomain();
        var range = Factories.Range.Closed<int>(10, 20);

        // ACT
        var expanded = range.Expand(domain, left: 5, right: 0);

        // ASSERT
        Assert.Equal(5, expanded.Start.Value);
        Assert.Equal(20, expanded.End.Value);
    }

    [Fact]
    public void Expand_WithFixedStepDomain_OnlyRight_ExpandsRightSide()
    {
        // ARRANGE
        var domain = new IntegerFixedStepDomain();
        var range = Factories.Range.Closed<int>(10, 20);

        // ACT
        var expanded = range.Expand(domain, left: 0, right: 5);

        // ASSERT
        Assert.Equal(10, expanded.Start.Value);
        Assert.Equal(25, expanded.End.Value);
    }

    [Fact]
    public void Expand_WithVariableStepDomain_ExpandsCorrectly()
    {
        // ARRANGE - Create a variable-step domain with custom steps
        var steps = new[] { 1, 2, 5, 10, 20, 50, 100 };
        var domain = new IntegerVariableStepDomain(steps);
        var range = Factories.Range.Closed<int>(5, 20);

        // ACT - Expand by 1 step on each side
        var expanded = range.Expand(domain, left: 1, right: 1);

        // ASSERT
        // Left: 5 - 1 step = 2, Right: 20 + 1 step = 50
        Assert.Equal(2, expanded.Start.Value);
        Assert.Equal(50, expanded.End.Value);
    }

    [Fact]
    public void Expand_WithVariableStepDomain_MultipleSteps_ExpandsCorrectly()
    {
        // ARRANGE
        var steps = new[] { 1, 5, 10, 20, 50, 100 };
        var domain = new IntegerVariableStepDomain(steps);
        var range = Factories.Range.Closed<int>(10, 20);

        // ACT - Expand by 2 steps on left, 1 step on right
        var expanded = range.Expand(domain, left: 2, right: 1);

        // ASSERT
        // Left: 10 - 2 steps = 1, Right: 20 + 1 step = 50
        Assert.Equal(1, expanded.Start.Value);
        Assert.Equal(50, expanded.End.Value);
    }

    [Fact]
    public void Expand_WithUnsupportedDomain_ThrowsNotSupportedException()
    {
        // ARRANGE - Create a mock domain that doesn't implement either interface
        var mockDomain = new Mock<IRangeDomain<int>>();
        var range = Factories.Range.Closed<int>(10, 20);

        // ACT & ASSERT
        var exception = Assert.Throws<NotSupportedException>(() =>
            range.Expand(mockDomain.Object, left: 5, right: 5));
        Assert.Contains("must implement either IFixedStepDomain<T> or IVariableStepDomain<T>", exception.Message);
    }

    #endregion

    #region ExpandByRatio Method Tests

    [Fact]
    public void ExpandByRatio_WithFixedStepDomain_ExpandsBothSides()
    {
        // ARRANGE
        var domain = new IntegerFixedStepDomain();
        var range = Factories.Range.Closed<int>(10, 20); // Span = 11 steps

        // ACT - Expand by 50% on each side
        var expanded = range.ExpandByRatio(domain, leftRatio: 0.5, rightRatio: 0.5);

        // ASSERT
        // Span = 11, so 50% = 5.5 steps (rounds to 5 or 6 depending on implementation)
        // Left: 10 - ~5 = ~5, Right: 20 + ~5 = ~25
        Assert.True(expanded.Start.Value <= 5);
        Assert.True(expanded.End.Value >= 25);
        Assert.True(expanded.Start.Value < 10);
        Assert.True(expanded.End.Value > 20);
    }

    [Fact]
    public void ExpandByRatio_WithFixedStepDomain_ZeroRatio_ReturnsSameRange()
    {
        // ARRANGE
        var domain = new IntegerFixedStepDomain();
        var range = Factories.Range.Closed<int>(10, 20);

        // ACT
        var expanded = range.ExpandByRatio(domain, leftRatio: 0.0, rightRatio: 0.0);

        // ASSERT
        Assert.Equal(10, expanded.Start.Value);
        Assert.Equal(20, expanded.End.Value);
    }

    [Fact]
    public void ExpandByRatio_WithFixedStepDomain_NegativeRatio_Shrinks()
    {
        // ARRANGE
        var domain = new IntegerFixedStepDomain();
        var range = Factories.Range.Closed<int>(10, 30); // Span = 21 steps

        // ACT - Shrink by 20% on each side (negative ratio)
        var shrunk = range.ExpandByRatio(domain, leftRatio: -0.2, rightRatio: -0.2);

        // ASSERT
        // 20% of 21 = 4.2 steps (rounds to 4)
        // The range should be smaller
        Assert.True(shrunk.Start.Value >= 10);
        Assert.True(shrunk.End.Value <= 30);
        Assert.True(shrunk.Start.Value > 10 && shrunk.End.Value < 30); // Both sides must have changed
    }

    [Fact]
    public void ExpandByRatio_WithFixedStepDomain_OnlyLeftRatio_ExpandsLeftSide()
    {
        // ARRANGE
        var domain = new IntegerFixedStepDomain();
        var range = Factories.Range.Closed<int>(10, 20);

        // ACT
        var expanded = range.ExpandByRatio(domain, leftRatio: 1.0, rightRatio: 0.0);

        // ASSERT
        // Left expands by 100% of span (11 steps)
        Assert.True(expanded.Start.Value < 10);
        Assert.Equal(20, expanded.End.Value);
    }

    [Fact]
    public void ExpandByRatio_WithFixedStepDomain_OnlyRightRatio_ExpandsRightSide()
    {
        // ARRANGE
        var domain = new IntegerFixedStepDomain();
        var range = Factories.Range.Closed<int>(10, 20);

        // ACT
        var expanded = range.ExpandByRatio(domain, leftRatio: 0.0, rightRatio: 1.0);

        // ASSERT
        // Right expands by 100% of span (11 steps)
        Assert.Equal(10, expanded.Start.Value);
        Assert.True(expanded.End.Value > 20);
    }

    [Fact]
    public void ExpandByRatio_WithFixedStepDomain_LargeRatio_ExpandsSignificantly()
    {
        // ARRANGE
        var domain = new IntegerFixedStepDomain();
        var range = Factories.Range.Closed<int>(100, 110); // Span = 11 steps

        // ACT - Expand by 200% on each side
        var expanded = range.ExpandByRatio(domain, leftRatio: 2.0, rightRatio: 2.0);

        // ASSERT
        // 200% of 11 = 22 steps
        // The expansion should be substantial
        Assert.True(expanded.Start.Value < 100);
        Assert.True(expanded.End.Value > 110);
        Assert.True((100 - expanded.Start.Value) >= 20); // At least 20 steps left
        Assert.True((expanded.End.Value - 110) >= 20); // At least 20 steps right
    }

    [Fact]
    public void ExpandByRatio_WithVariableStepDomain_ExpandsCorrectly()
    {
        // ARRANGE
        var steps = new[] { 1, 2, 5, 10, 15, 20, 25, 30, 40, 50, 100, 200 };
        var domain = new IntegerVariableStepDomain(steps);
        var range = Factories.Range.Closed<int>(10, 30); // Span = 5 steps (10, 15, 20, 25, 30)

        // ACT - Expand by 50% on each side (2 steps on each side)
        var expanded = range.ExpandByRatio(domain, leftRatio: 0.5, rightRatio: 0.5);

        // ASSERT
        // Original range covers steps: 10, 15, 20, 25, 30 (5 steps)
        // Expanding by 50% should add ~2-3 steps on each side
        Assert.True(expanded.Start.Value < 10);
        Assert.True(expanded.End.Value > 30);
    }

    [Fact]
    public void ExpandByRatio_WithUnsupportedDomain_ThrowsNotSupportedException()
    {
        // ARRANGE - Create a mock domain that doesn't implement either interface
        var mockDomain = new Mock<IRangeDomain<int>>();
        var range = Factories.Range.Closed<int>(10, 20);

        // ACT & ASSERT
        var exception = Assert.Throws<NotSupportedException>(() =>
            range.ExpandByRatio(mockDomain.Object, leftRatio: 0.5, rightRatio: 0.5));
        Assert.Contains("must implement either IFixedStepDomain<T> or IVariableStepDomain<T>", exception.Message);
    }

    #endregion

    #region Integration Tests - Multiple Operations

    [Fact]
    public void MultipleOperations_Span_Then_Expand_WorksTogether()
    {
        // ARRANGE
        var domain = new IntegerFixedStepDomain();
        var originalRange = Factories.Range.Closed<int>(10, 20);

        // ACT
        var originalSpan = originalRange.Span(domain);
        var expanded = originalRange.Expand(domain, left: 5, right: 5);
        var expandedSpan = expanded.Span(domain);

        // ASSERT
        Assert.Equal(11, originalSpan.Value);  // Original: [10, 20] = 11 steps
        Assert.Equal(21, expandedSpan.Value);  // Expanded: [5, 25] = 21 steps
        Assert.Equal(originalSpan.Value + 10, expandedSpan.Value); // Added 5 steps on each side
    }

    [Fact]
    public void MultipleOperations_ExpandByRatio_Then_Span_WorksTogether()
    {
        // ARRANGE
        var domain = new IntegerFixedStepDomain();
        var range = Factories.Range.Closed<int>(100, 110); // Span = 11 steps

        // ACT
        var expanded = range.ExpandByRatio(domain, leftRatio: 1.0, rightRatio: 1.0);
        var expandedSpan = expanded.Span(domain);

        // ASSERT
        // Expanding by 100% on each side should roughly triple the span
        Assert.True(expandedSpan.Value > 11); // Must be larger
        Assert.True(expandedSpan.Value >= 30); // Should be approximately 33 (11 + 11 + 11)
    }

    [Fact]
    public void MultipleOperations_ChainedExpansions_WorkCorrectly()
    {
        // ARRANGE
        var domain = new IntegerFixedStepDomain();
        var range = Factories.Range.Closed<int>(50, 60); // Span = 11 steps

        // ACT - Chain multiple expansions
        var firstExpansion = range.Expand(domain, left: 2, right: 2);
        var secondExpansion = firstExpansion.Expand(domain, left: 3, right: 3);

        // ASSERT
        Assert.Equal(48, firstExpansion.Start.Value);  // 50 - 2 = 48
        Assert.Equal(62, firstExpansion.End.Value);    // 60 + 2 = 62
        Assert.Equal(45, secondExpansion.Start.Value); // 48 - 3 = 45
        Assert.Equal(65, secondExpansion.End.Value);   // 62 + 3 = 65
    }

    #endregion
}
