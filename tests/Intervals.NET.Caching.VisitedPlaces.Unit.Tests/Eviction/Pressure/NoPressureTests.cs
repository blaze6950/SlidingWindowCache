using Intervals.NET.Caching.VisitedPlaces.Core;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Pressure;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure.Helpers;

namespace Intervals.NET.Caching.VisitedPlaces.Unit.Tests.Eviction.Pressure;

/// <summary>
/// Unit tests for <see cref="NoPressure{TRange,TData}"/>.
/// Validates singleton semantics, IsExceeded always false, and Reduce no-op.
/// </summary>
public sealed class NoPressureTests
{
    #region Singleton Tests

    [Fact]
    public void Instance_ReturnsSameReference()
    {
        // ARRANGE & ACT
        var instance1 = NoPressure<int, int>.Instance;
        var instance2 = NoPressure<int, int>.Instance;

        // ASSERT
        Assert.Same(instance1, instance2);
    }

    #endregion

    #region IsExceeded Tests

    [Fact]
    public void IsExceeded_AlwaysReturnsFalse()
    {
        // ARRANGE
        var pressure = NoPressure<int, int>.Instance;

        // ACT & ASSERT
        Assert.False(pressure.IsExceeded);
    }

    #endregion

    #region Reduce Tests

    [Fact]
    public void Reduce_IsNoOp_IsExceededRemainsFalse()
    {
        // ARRANGE
        var pressure = NoPressure<int, int>.Instance;
        var segment = CreateSegment(0, 5);

        // ACT
        pressure.Reduce(segment);

        // ASSERT — still false after reduction
        Assert.False(pressure.IsExceeded);
    }

    [Fact]
    public void Reduce_MultipleCalls_DoesNotThrow()
    {
        // ARRANGE
        var pressure = NoPressure<int, int>.Instance;
        var segment = CreateSegment(0, 5);

        // ACT
        var exception = Record.Exception(() =>
        {
            pressure.Reduce(segment);
            pressure.Reduce(segment);
            pressure.Reduce(segment);
        });

        // ASSERT
        Assert.Null(exception);
        Assert.False(pressure.IsExceeded);
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
