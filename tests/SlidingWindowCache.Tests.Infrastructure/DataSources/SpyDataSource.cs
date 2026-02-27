using System.Collections.Concurrent;
using Intervals.NET;
using SlidingWindowCache.Public;
using SlidingWindowCache.Public.Dto;

namespace SlidingWindowCache.Tests.Infrastructure.DataSources;

/// <summary>
/// A test spy/fake IDataSource implementation that records all fetch calls for verification.
/// Generates sequential integer data for requested ranges and tracks all interactions.
/// Thread-safe for concurrent test scenarios.
/// </summary>
public sealed class SpyDataSource : IDataSource<int, int>
{
    private readonly ConcurrentBag<Range<int>> _singleFetchCalls = new();
    private readonly ConcurrentBag<IEnumerable<Range<int>>> _batchFetchCalls = new();
    private int _totalFetchCount;

    /// <summary>
    /// Total number of fetch operations (single + batch).
    /// </summary>
    public int TotalFetchCount => _totalFetchCount;

    /// <summary>
    /// Resets all recorded calls.
    /// </summary>
    public void Reset()
    {
        _singleFetchCalls.Clear();
        _batchFetchCalls.Clear();
        Interlocked.Exchange(ref _totalFetchCount, 0);
    }

    /// <summary>
    /// Gets all ranges requested across both single and batch fetch calls.
    /// Flattens batch calls into individual ranges.
    /// </summary>
    public IReadOnlyCollection<Range<int>> GetAllRequestedRanges() =>
        _batchFetchCalls
            .SelectMany(b => b)
            .Concat(_singleFetchCalls)
            .ToList();

    /// <summary>
    /// Gets unique ranges requested (eliminates duplicates).
    /// Useful for verifying no redundant identical fetches occurred.
    /// </summary>
    public IReadOnlyCollection<Range<int>> GetUniqueRequestedRanges() =>
        GetAllRequestedRanges()
            .Distinct()
            .ToList();

    /// <summary>
    /// Verifies that the requested range covers at least the specified boundaries.
    /// Returns true if any requested range fully contains the target range.
    /// </summary>
    public bool WasRangeCovered(int start, int end)
    {
        foreach (var range in GetAllRequestedRanges())
        {
            var rangeStart = (int)range.Start;
            var rangeEnd = (int)range.End;

            // Check if this range fully covers [start, end]
            if (rangeStart <= start && rangeEnd >= end)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Asserts that a specific range was requested (boundary check).
    /// </summary>
    public void AssertRangeRequested(Range<int> range)
    {
        Assert.Contains(GetAllRequestedRanges(), r =>
            r.Start == range.Start &&
            r.End == range.End &&
            range.IsStartInclusive == r.IsStartInclusive &&
            range.IsEndInclusive == r.IsEndInclusive);
    }

    /// <summary>
    /// Fetches data for a single range and records the call.
    /// </summary>
    public Task<RangeChunk<int, int>> FetchAsync(Range<int> range, CancellationToken cancellationToken)
    {
        _singleFetchCalls.Add(range);
        Interlocked.Increment(ref _totalFetchCount);

        var data = DataGenerationHelpers.GenerateDataForRange(range);
        return Task.FromResult(new RangeChunk<int, int>(range, data));
    }

    /// <summary>
    /// Fetches data for multiple ranges and records the call.
    /// </summary>
    public async Task<IEnumerable<RangeChunk<int, int>>> FetchAsync(
        IEnumerable<Range<int>> ranges,
        CancellationToken cancellationToken)
    {
        var rangesList = ranges.ToList();
        _batchFetchCalls.Add(rangesList);
        Interlocked.Increment(ref _totalFetchCount);

        var chunks = new List<RangeChunk<int, int>>();
        foreach (var range in rangesList)
        {
            var data = DataGenerationHelpers.GenerateDataForRange(range);
            chunks.Add(new RangeChunk<int, int>(range, data));
        }

        return await Task.FromResult(chunks);
    }
}
