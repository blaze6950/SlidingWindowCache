using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Selectors;
using Intervals.NET.Caching.VisitedPlaces.Public.Configuration;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure.Helpers;

namespace Intervals.NET.Caching.VisitedPlaces.Unit.Tests.Eviction.Selectors;

/// <summary>
/// Unit tests for the <see cref="SmallestFirstEvictionSelector"/> static factory companion class.
/// Validates that <see cref="SmallestFirstEvictionSelector.Create{TRange,TData,TDomain}"/> returns
/// an instance of the correct type and propagates constructor validation.
/// </summary>
public sealed class SmallestFirstEvictionSelectorFactoryTests
{
    private readonly IntegerFixedStepDomain _domain = TestHelpers.CreateIntDomain();

    #region Create — Valid Parameters

    [Fact]
    public void Create_WithDomainOnly_ReturnsSmallestFirstEvictionSelector()
    {
        // ARRANGE & ACT
        var selector = SmallestFirstEvictionSelector.Create<int, int, IntegerFixedStepDomain>(_domain);

        // ASSERT
        Assert.IsType<SmallestFirstEvictionSelector<int, int, IntegerFixedStepDomain>>(selector);
    }

    [Fact]
    public void Create_WithDomainOnly_DoesNotThrow()
    {
        // ARRANGE & ACT
        var exception = Record.Exception(() =>
            SmallestFirstEvictionSelector.Create<int, int, IntegerFixedStepDomain>(_domain));

        // ASSERT
        Assert.Null(exception);
    }

    [Fact]
    public void Create_WithCustomSamplingOptions_ReturnsInstance()
    {
        // ARRANGE
        var samplingOptions = new EvictionSamplingOptions(sampleSize: 16);

        // ACT
        var selector = SmallestFirstEvictionSelector.Create<int, int, IntegerFixedStepDomain>(
            _domain, samplingOptions);

        // ASSERT
        Assert.IsType<SmallestFirstEvictionSelector<int, int, IntegerFixedStepDomain>>(selector);
    }

    #endregion

    #region Create — Invalid Parameters

    [Fact]
    public void Create_WithInvalidSamplingOptions_ThrowsArgumentOutOfRangeException()
    {
        // ARRANGE — domain is a struct so null cannot be passed; validate via invalid sampling options instead
        // (SampleSize < 1 throws ArgumentOutOfRangeException)
        var exception = Record.Exception(() =>
            SmallestFirstEvictionSelector.Create<int, int, Intervals.NET.Domain.Default.Numeric.IntegerFixedStepDomain>(
                _domain,
                new Intervals.NET.Caching.VisitedPlaces.Public.Configuration.EvictionSamplingOptions(0)));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentOutOfRangeException>(exception);
    }

    #endregion
}
