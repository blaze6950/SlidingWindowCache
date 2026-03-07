using System.Collections.Concurrent;
using Intervals.NET.Caching.Dto;

namespace Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure.DataSources;

/// <summary>
/// A test spy data source that records all fetch calls and generates sequential integer data.
/// Thread-safe for concurrent test scenarios.
/// </summary>
public sealed class SpyDataSource : IDataSource<int, int>
{
    private readonly ConcurrentBag<Range<int>> _fetchCalls = [];
    private int _totalFetchCount;

    /// <summary>Total number of fetch operations performed.</summary>
    public int TotalFetchCount => Volatile.Read(ref _totalFetchCount);

    /// <summary>
    /// Resets all recorded calls and the fetch count.
    /// </summary>
    public void Reset()
    {
        _fetchCalls.Clear();
        Interlocked.Exchange(ref _totalFetchCount, 0);
    }

    /// <summary>
    /// Gets all ranges that were fetched.
    /// </summary>
    public IReadOnlyCollection<Range<int>> GetAllRequestedRanges() =>
        _fetchCalls.ToList();

    /// <summary>
    /// Returns <see langword="true"/> if a fetch call was made for a range that covers [start, end].
    /// </summary>
    public bool WasRangeCovered(int start, int end)
    {
        foreach (var range in _fetchCalls)
        {
            var rangeStart = (int)range.Start;
            var rangeEnd = (int)range.End;

            if (rangeStart <= start && rangeEnd >= end)
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc/>
    public Task<RangeChunk<int, int>> FetchAsync(Range<int> range, CancellationToken cancellationToken)
    {
        _fetchCalls.Add(range);
        Interlocked.Increment(ref _totalFetchCount);

        var data = DataGenerationHelpers.GenerateDataForRange(range);
        return Task.FromResult(new RangeChunk<int, int>(range, data));
    }
}
