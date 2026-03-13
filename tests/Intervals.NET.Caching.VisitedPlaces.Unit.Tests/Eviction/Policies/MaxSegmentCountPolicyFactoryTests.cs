using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Policies;

namespace Intervals.NET.Caching.VisitedPlaces.Unit.Tests.Eviction.Policies;

/// <summary>
/// Unit tests for the <see cref="MaxSegmentCountPolicy"/> static factory companion class.
/// Validates that <see cref="MaxSegmentCountPolicy.Create{TRange,TData}"/> correctly delegates
/// to the generic constructor and propagates its validation.
/// </summary>
public sealed class MaxSegmentCountPolicyFactoryTests
{
    #region Create — Valid Parameters

    [Fact]
    public void Create_WithValidMaxCount_ReturnsPolicyWithCorrectMaxCount()
    {
        // ARRANGE & ACT
        var policy = MaxSegmentCountPolicy.Create<int, int>(5);

        // ASSERT
        Assert.Equal(5, policy.MaxCount);
    }

    [Fact]
    public void Create_WithMaxCountOfOne_ReturnsValidPolicy()
    {
        // ARRANGE & ACT
        var exception = Record.Exception(() => MaxSegmentCountPolicy.Create<int, int>(1));

        // ASSERT
        Assert.Null(exception);
    }

    [Fact]
    public void Create_ReturnsCorrectType()
    {
        // ARRANGE & ACT
        var policy = MaxSegmentCountPolicy.Create<int, string>(10);

        // ASSERT
        Assert.IsType<MaxSegmentCountPolicy<int, string>>(policy);
    }

    #endregion

    #region Create — Invalid Parameters

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Create_WithMaxCountLessThanOne_ThrowsArgumentOutOfRangeException(int invalidMaxCount)
    {
        // ARRANGE & ACT
        var exception = Record.Exception(() => MaxSegmentCountPolicy.Create<int, int>(invalidMaxCount));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentOutOfRangeException>(exception);
    }

    #endregion
}
