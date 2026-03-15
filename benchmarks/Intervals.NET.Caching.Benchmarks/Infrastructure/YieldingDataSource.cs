using Intervals.NET.Caching.Dto;
using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Domain.Extensions.Fixed;

namespace Intervals.NET.Caching.Benchmarks.Infrastructure;

/// <summary>
/// Async-dispatching IDataSource for benchmark learning passes.
/// Identical to <see cref="SynchronousDataSource"/> but yields to the thread pool via
/// Task.Yield() before returning data, simulating the async dispatch cost of a real
/// I/O-bound data source. Call Freeze() after the learning pass to obtain a
/// FrozenYieldingDataSource and disable this instance.
/// </summary>
public sealed class YieldingDataSource : IDataSource<int, int>
{
    private readonly IntegerFixedStepDomain _domain;
    private Dictionary<Range<int>, RangeChunk<int, int>>? _cache = new();

    public YieldingDataSource(IntegerFixedStepDomain domain)
    {
        _domain = domain;
    }

    /// <summary>
    /// Transfers dictionary ownership to a new <see cref="FrozenYieldingDataSource"/> and
    /// disables this instance. Any FetchAsync call after Freeze() throws InvalidOperationException.
    /// </summary>
    public FrozenYieldingDataSource Freeze()
    {
        var cache = _cache ?? throw new InvalidOperationException(
            "YieldingDataSource has already been frozen.");
        _cache = null;
        return new FrozenYieldingDataSource(cache);
    }

    /// <summary>
    /// Fetches data for a single range, yielding to the thread pool before returning.
    /// Auto-caches result so subsequent calls for the same range only pay Task.Yield cost.
    /// </summary>
    public async Task<RangeChunk<int, int>> FetchAsync(Range<int> range, CancellationToken cancellationToken)
    {
        await Task.Yield();

        var cache = _cache ?? throw new InvalidOperationException(
            "YieldingDataSource has been frozen. Use the FrozenYieldingDataSource returned by Freeze().");

        if (!cache.TryGetValue(range, out var cached))
        {
            cached = new RangeChunk<int, int>(range, GenerateDataForRange(range).ToArray());
            cache[range] = cached;
        }

        return cached;
    }

    /// <summary>
    /// Fetches data for multiple ranges, yielding to the thread pool once before returning all chunks.
    /// Auto-caches results so subsequent calls for the same ranges only pay Task.Yield cost.
    /// </summary>
    public async Task<IEnumerable<RangeChunk<int, int>>> FetchAsync(
        IEnumerable<Range<int>> ranges,
        CancellationToken cancellationToken)
    {
        await Task.Yield();

        var cache = _cache ?? throw new InvalidOperationException(
            "YieldingDataSource has been frozen. Use the FrozenYieldingDataSource returned by Freeze().");

        var chunks = ranges.Select(range =>
        {
            if (!cache.TryGetValue(range, out var cached))
            {
                cached = new RangeChunk<int, int>(range, GenerateDataForRange(range).ToArray());
                cache[range] = cached;
            }

            return cached;
        });

        return chunks;
    }

    /// <summary>
    /// Generates deterministic data for a range: position i produces value i.
    /// </summary>
    private IEnumerable<int> GenerateDataForRange(Range<int> range)
    {
        var start = range.Start.Value;
        var count = (int)range.Span(_domain).Value;

        for (var i = 0; i < count; i++)
        {
            yield return start + i;
        }
    }
}
