using Intervals.NET;
using Intervals.NET.Extensions;
using SlidingWindowCache.Public;
using SlidingWindowCache.Public.Dto;

namespace SlidingWindowCache.Integration.Tests.TestInfrastructure;

/// <summary>
/// A test IDataSource implementation that simulates a bounded data source with physical limits.
/// Only returns data for ranges within [MinId, MaxId] boundaries.
/// Used for testing boundary handling, partial fulfillment, and out-of-bounds scenarios.
/// </summary>
public sealed class BoundedDataSource : IDataSource<int, int>
{
    private const int MinId = 1000;
    private const int MaxId = 9999;

    /// <summary>
    /// Gets the minimum available ID (inclusive).
    /// </summary>
    public int MinimumId => MinId;

    /// <summary>
    /// Gets the maximum available ID (inclusive).
    /// </summary>
    public int MaximumId => MaxId;

    /// <summary>
    /// Fetches data for a single range, respecting physical boundaries.
    /// Returns only data within [MinId, MaxId].
    /// </summary>
    public Task<RangeChunk<int, int>> FetchAsync(Range<int> requested, CancellationToken cancellationToken)
    {
        // Define the physical boundary
        var availableRange = Intervals.NET.Factories.Range.Closed<int>(MinId, MaxId);

        // Compute intersection with requested range
        var fulfillable = requested.Intersect(availableRange);

        // No data available - completely out of bounds
        if (fulfillable == null)
        {
            return Task.FromResult(new RangeChunk<int, int>(
                null,  // Range must be null when no data is available (per IDataSource contract)
                Array.Empty<int>()
            ));
        }

        // Fetch available portion (non-null fulfillable)
        var data = GenerateDataForRange(fulfillable.Value);
        return Task.FromResult(new RangeChunk<int, int>(fulfillable.Value, data));
    }

    /// <summary>
    /// Fetches data for multiple ranges in batch.
    /// Each range respects physical boundaries independently.
    /// </summary>
    public async Task<IEnumerable<RangeChunk<int, int>>> FetchAsync(
        IEnumerable<Range<int>> ranges,
        CancellationToken cancellationToken)
    {
        var chunks = new List<RangeChunk<int, int>>();

        foreach (var range in ranges)
        {
            var chunk = await FetchAsync(range, cancellationToken);
            chunks.Add(chunk);
        }

        return chunks;
    }

    /// <summary>
    /// Generates sequential integer data for a range, respecting boundary inclusivity.
    /// </summary>
    private static List<int> GenerateDataForRange(Range<int> range)
    {
        var data = new List<int>();
        var start = (int)range.Start;
        var end = (int)range.End;

        switch (range)
        {
            case { IsStartInclusive: true, IsEndInclusive: true }:
                // [start, end]
                for (var i = start; i <= end; i++)
                {
                    data.Add(i);
                }
                break;

            case { IsStartInclusive: true, IsEndInclusive: false }:
                // [start, end)
                for (var i = start; i < end; i++)
                {
                    data.Add(i);
                }
                break;

            case { IsStartInclusive: false, IsEndInclusive: true }:
                // (start, end]
                for (var i = start + 1; i <= end; i++)
                {
                    data.Add(i);
                }
                break;

            default:
                // (start, end)
                for (var i = start + 1; i < end; i++)
                {
                    data.Add(i);
                }
                break;
        }

        return data;
    }
}
