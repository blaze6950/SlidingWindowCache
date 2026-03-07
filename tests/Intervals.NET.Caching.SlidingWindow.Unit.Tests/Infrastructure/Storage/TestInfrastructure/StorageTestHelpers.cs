using Intervals.NET.Data;
using Intervals.NET.Data.Extensions;
using Intervals.NET.Domain.Abstractions;
using Intervals.NET.Domain.Default.Numeric;

namespace Intervals.NET.Caching.SlidingWindow.Unit.Tests.Infrastructure.Storage.TestInfrastructure;

/// <summary>
/// Shared test helpers for storage implementation tests.
/// Provides factory methods for creating test data and assertion utilities.
/// </summary>
internal static class StorageTestHelpers
{
    /// <summary>
    /// Creates a fixed-step integer domain for testing.
    /// </summary>
    public static IntegerFixedStepDomain CreateFixedStepDomain() => new();

    /// <summary>
    /// Creates a closed range for testing.
    /// </summary>
    public static Range<int> CreateRange(int start, int end) =>
        Factories.Range.Closed<int>(start, end);

    /// <summary>
    /// Creates test range data with sequential integer values where value equals position.
    /// For range [start, end], generates data [start, start+1, start+2, ..., end].
    /// </summary>
    public static RangeData<int, int, IntegerFixedStepDomain> CreateRangeData(
        int start,
        int end,
        IntegerFixedStepDomain domain)
    {
        var range = CreateRange(start, end);
        var data = Enumerable.Range(start, end - start + 1).ToArray();
        return data.ToRangeData(range, domain);
    }

    /// <summary>
    /// Verifies that the provided data matches the expected range.
    /// For range [start, end], expects data [start, start+1, ..., end].
    /// </summary>
    public static void VerifyDataMatchesRange(ReadOnlyMemory<int> actualData, int expectedStart, int expectedEnd)
    {
        var expectedLength = expectedEnd - expectedStart + 1;
        Assert.Equal(expectedLength, actualData.Length);

        var span = actualData.Span;
        for (var i = 0; i < span.Length; i++)
        {
            Assert.Equal(expectedStart + i, span[i]);
        }
    }

    /// <summary>
    /// Verifies that ToRangeData() round-trips correctly by comparing ranges and data.
    /// </summary>
    public static void AssertRangeDataRoundTrip<TRange, TData, TDomain>(
        RangeData<TRange, TData, TDomain> original,
        RangeData<TRange, TData, TDomain> roundTripped)
        where TRange : IComparable<TRange>
        where TDomain : IRangeDomain<TRange>
    {
        // Verify ranges match
        Assert.Equal(original.Range.Start, roundTripped.Range.Start);
        Assert.Equal(original.Range.End, roundTripped.Range.End);
        Assert.Equal(original.Range.IsStartInclusive, roundTripped.Range.IsStartInclusive);
        Assert.Equal(original.Range.IsEndInclusive, roundTripped.Range.IsEndInclusive);

        // Verify data matches
        var originalArray = original.Data.ToArray();
        var roundTrippedArray = roundTripped.Data.ToArray();
        Assert.Equal(originalArray.Length, roundTrippedArray.Length);

        for (var i = 0; i < originalArray.Length; i++)
        {
            Assert.Equal(originalArray[i], roundTrippedArray[i]);
        }
    }
}