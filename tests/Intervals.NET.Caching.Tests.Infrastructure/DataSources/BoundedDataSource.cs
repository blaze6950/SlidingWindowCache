using Intervals.NET;
using Intervals.NET.Extensions;
using Intervals.NET.Caching.Public;
using Intervals.NET.Caching.Public.Dto;

namespace Intervals.NET.Caching.Tests.Infrastructure.DataSources;

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
        var data = DataGenerationHelpers.GenerateDataForRange(fulfillable.Value);
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
}
