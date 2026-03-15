using Intervals.NET.Caching.Dto;

namespace Intervals.NET.Caching.Benchmarks.Infrastructure;

/// <summary>
/// Immutable, Task.Yield()-dispatching IDataSource produced by YieldingDataSource.Freeze().
/// Identical to <see cref="FrozenDataSource"/> but includes await Task.Yield() before
/// each lookup, isolating the async dispatch cost without allocation noise.
/// Throws InvalidOperationException if a range was not learned during the learning pass.
/// </summary>
public sealed class FrozenYieldingDataSource : IDataSource<int, int>
{
    private readonly Dictionary<Range<int>, RangeChunk<int, int>> _cache;

    internal FrozenYieldingDataSource(Dictionary<Range<int>, RangeChunk<int, int>> cache)
    {
        _cache = cache;
    }

    /// <summary>
    /// Yields to the thread pool then returns cached data for a previously-learned range.
    /// Throws <see cref="InvalidOperationException"/> if the range was not seen during the learning pass.
    /// </summary>
    public async Task<RangeChunk<int, int>> FetchAsync(Range<int> range, CancellationToken cancellationToken)
    {
        await Task.Yield();

        if (!_cache.TryGetValue(range, out var cached))
        {
            throw new InvalidOperationException(
                $"FrozenYieldingDataSource: range [{range.Start.Value},{range.End.Value}] " +
                $"(IsStartInclusive={range.IsStartInclusive}, IsEndInclusive={range.IsEndInclusive}) " +
                $"was not seen during the learning pass. Ensure the learning pass exercises all benchmark code paths.");
        }

        return cached;
    }

    /// <summary>
    /// Yields to the thread pool once then returns cached data for all previously-learned ranges.
    /// Throws <see cref="InvalidOperationException"/> if any range was not seen during the learning pass.
    /// </summary>
    public async Task<IEnumerable<RangeChunk<int, int>>> FetchAsync(
        IEnumerable<Range<int>> ranges,
        CancellationToken cancellationToken)
    {
        await Task.Yield();

        var chunks = ranges.Select(range =>
        {
            if (!_cache.TryGetValue(range, out var cached))
            {
                throw new InvalidOperationException(
                    $"FrozenYieldingDataSource: range [{range.Start.Value},{range.End.Value}] " +
                    $"(IsStartInclusive={range.IsStartInclusive}, IsEndInclusive={range.IsEndInclusive}) " +
                    $"was not seen during the learning pass. Ensure the learning pass exercises all benchmark code paths.");
            }

            return cached;
        });

        return chunks;
    }
}
