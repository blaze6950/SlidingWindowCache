using Intervals.NET;
using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Domain.Extensions.Fixed;
using SlidingWindowCache.Public;
using SlidingWindowCache.Public.Dto;

namespace SlidingWindowCache.Benchmarks.Infrastructure;

/// <summary>
/// Zero-latency synchronous IDataSource for isolating rebalance and cache mutation costs.
/// Returns data immediately without Task.Delay or I/O simulation.
/// Designed for RebalanceCostBenchmarks to measure pure cache mechanics without data source interference.
/// </summary>
public sealed class SynchronousDataSource : IDataSource<int, int>
{
    private readonly IntegerFixedStepDomain _domain;

    public SynchronousDataSource(IntegerFixedStepDomain domain)
    {
        _domain = domain;
    }

    /// <summary>
    /// Fetches data for a single range with zero latency.
    /// Data generation: Returns the integer value at each position in the range.
    /// </summary>
    public Task<RangeChunk<int, int>> FetchAsync(Range<int> range, CancellationToken cancellationToken) =>
        Task.FromResult(new RangeChunk<int, int>(range, GenerateDataForRange(range)));

    /// <summary>
    /// Fetches data for multiple ranges with zero latency.
    /// </summary>
    public Task<IEnumerable<RangeChunk<int, int>>> FetchAsync(
        IEnumerable<Range<int>> ranges,
        CancellationToken cancellationToken)
    {
        // Synchronous generation for all chunks
        var chunks = ranges.Select(range => new RangeChunk<int, int>(
            range,
            GenerateDataForRange(range)
        ));

        return Task.FromResult(chunks);
    }

    /// <summary>
    /// Generates deterministic data for a range.
    /// Each position i in the range produces value i.
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