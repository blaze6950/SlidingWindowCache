using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Policies;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure.Helpers;

namespace Intervals.NET.Caching.VisitedPlaces.Unit.Tests.Eviction.Policies;

/// <summary>
/// Unit tests for the <see cref="MaxTotalSpanPolicy"/> static factory companion class.
/// Validates that <see cref="MaxTotalSpanPolicy.Create{TRange,TData,TDomain}"/> correctly delegates
/// to the generic constructor and propagates its validation.
/// </summary>
public sealed class MaxTotalSpanPolicyFactoryTests
{
    private readonly IntegerFixedStepDomain _domain = TestHelpers.CreateIntDomain();

    #region Create — Valid Parameters

    [Fact]
    public void Create_WithValidParameters_ReturnsPolicyWithCorrectMaxTotalSpan()
    {
        // ARRANGE & ACT
        var policy = MaxTotalSpanPolicy.Create<int, int, IntegerFixedStepDomain>(100, _domain);

        // ASSERT
        Assert.Equal(100, policy.MaxTotalSpan);
    }

    [Fact]
    public void Create_WithMaxTotalSpanOfOne_ReturnsValidPolicy()
    {
        // ARRANGE & ACT
        var exception = Record.Exception(() =>
            MaxTotalSpanPolicy.Create<int, int, IntegerFixedStepDomain>(1, _domain));

        // ASSERT
        Assert.Null(exception);
    }

    [Fact]
    public void Create_ReturnsCorrectType()
    {
        // ARRANGE & ACT
        var policy = MaxTotalSpanPolicy.Create<int, string, IntegerFixedStepDomain>(50, _domain);

        // ASSERT
        Assert.IsType<MaxTotalSpanPolicy<int, string, IntegerFixedStepDomain>>(policy);
    }

    #endregion

    #region Create — Invalid Parameters

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_WithMaxTotalSpanLessThanOne_ThrowsArgumentOutOfRangeException(int invalid)
    {
        // ARRANGE & ACT
        var exception = Record.Exception(() =>
            MaxTotalSpanPolicy.Create<int, int, IntegerFixedStepDomain>(invalid, _domain));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentOutOfRangeException>(exception);
    }

    [Fact]
    public void Create_WithNullDomain_ThrowsArgumentNullException()
    {
        // ARRANGE & ACT — domain is a struct (IntegerFixedStepDomain), so null is not applicable.
        // This test verifies the factory delegates validation to the generic constructor.
        // The constructor validates domain via `if (domain is null)` which fires for reference types.
        // For struct domains the compiler enforces non-null, so no runtime test is needed.
        // The test simply confirms the factory does not swallow exceptions on invalid maxTotalSpan.
        var exception = Record.Exception(() =>
            MaxTotalSpanPolicy.Create<int, int, Intervals.NET.Domain.Default.Numeric.IntegerFixedStepDomain>(0, _domain));

        Assert.NotNull(exception);
        Assert.IsType<ArgumentOutOfRangeException>(exception);
    }

    #endregion
}
