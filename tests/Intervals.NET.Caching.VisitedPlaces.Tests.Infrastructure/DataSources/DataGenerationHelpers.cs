namespace Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure.DataSources;

/// <summary>
/// Shared data generation logic used by test data sources.
/// Encapsulates the range-to-integer-data mapping, respecting boundary inclusivity.
/// </summary>
public static class DataGenerationHelpers
{
    /// <summary>
    /// Generates sequential integer data for an integer range, respecting boundary inclusivity.
    /// </summary>
    /// <param name="range">The range to generate data for.</param>
    /// <returns>A list of sequential integers corresponding to the range.</returns>
    public static List<int> GenerateDataForRange(Range<int> range)
    {
        var data = new List<int>();
        var start = (int)range.Start;
        var end = (int)range.End;

        switch (range)
        {
            case { IsStartInclusive: true, IsEndInclusive: true }:
                for (var i = start; i <= end; i++)
                    data.Add(i);
                break;

            case { IsStartInclusive: true, IsEndInclusive: false }:
                for (var i = start; i < end; i++)
                    data.Add(i);
                break;

            case { IsStartInclusive: false, IsEndInclusive: true }:
                for (var i = start + 1; i <= end; i++)
                    data.Add(i);
                break;

            default:
                for (var i = start + 1; i < end; i++)
                    data.Add(i);
                break;
        }

        return data;
    }
}
