using Intervals.NET.Caching.Dto;

namespace Intervals.NET.Caching.Benchmarks.Infrastructure;

/// <summary>
/// Immutable, allocation-free IDataSource produced by SynchronousDataSource.Freeze().
/// FetchAsync returns Task.FromResult(cached) — zero allocation on the hot path.
/// Throws InvalidOperationException if a range was not learned during the learning pass.
/// </summary>
public sealed class FrozenDataSource : IDataSource<int, int>
{
    private readonly Dictionary<Range<int>, RangeChunk<int, int>> _cache;

    internal FrozenDataSource(Dictionary<Range<int>, RangeChunk<int, int>> cache)
    {
        _cache = cache;
    }

    /// <summary>
    /// Returns cached data for a previously-learned range with zero allocation.
    /// Throws <see cref="InvalidOperationException"/> if the range was not seen during the learning pass.
    /// </summary>
    public Task<RangeChunk<int, int>> FetchAsync(Range<int> range, CancellationToken cancellationToken)
    {
        if (!_cache.TryGetValue(range, out var cached))
        {
            throw new InvalidOperationException(
                $"FrozenDataSource: range [{range.Start.Value},{range.End.Value}] " +
                $"(IsStartInclusive={range.IsStartInclusive}, IsEndInclusive={range.IsEndInclusive}) " +
                $"was not seen during the learning pass. Ensure the learning pass exercises all benchmark code paths.");
        }

        return Task.FromResult(cached);
    }

    /// <summary>
    /// Returns cached data for all previously-learned ranges with zero allocation.
    /// Throws <see cref="InvalidOperationException"/> if any range was not seen during the learning pass.
    /// </summary>
    public Task<IEnumerable<RangeChunk<int, int>>> FetchAsync(
        IEnumerable<Range<int>> ranges,
        CancellationToken cancellationToken)
    {
        var chunks = ranges.Select(range =>
        {
            if (!_cache.TryGetValue(range, out var cached))
            {
                throw new InvalidOperationException(
                    $"FrozenDataSource: range [{range.Start.Value},{range.End.Value}] " +
                    $"(IsStartInclusive={range.IsStartInclusive}, IsEndInclusive={range.IsEndInclusive}) " +
                    $"was not seen during the learning pass. Ensure the learning pass exercises all benchmark code paths.");
            }

            return cached;
        });

        return Task.FromResult(chunks);
    }
}
