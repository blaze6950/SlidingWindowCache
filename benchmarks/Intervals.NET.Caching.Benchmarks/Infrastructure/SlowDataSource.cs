using Intervals.NET;
using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Caching.Public;
using Intervals.NET.Caching.Public.Dto;

namespace Intervals.NET.Caching.Benchmarks.Infrastructure;

/// <summary>
/// Configurable-latency IDataSource for testing execution strategy behavior with realistic I/O delays.
/// Simulates network/database/external API latency using Task.Delay.
/// Designed for ExecutionStrategyBenchmarks to measure cancellation, backpressure, and burst handling.
/// </summary>
public sealed class SlowDataSource : IDataSource<int, int>
{
    private readonly IntegerFixedStepDomain _domain;
    private readonly TimeSpan _latency;

    /// <summary>
    /// Initializes a new instance of SlowDataSource with configurable latency.
    /// </summary>
    /// <param name="domain">The integer domain for range calculations.</param>
    /// <param name="latency">The simulated I/O latency per fetch operation.</param>
    public SlowDataSource(IntegerFixedStepDomain domain, TimeSpan latency)
    {
        _domain = domain;
        _latency = latency;
    }

    /// <summary>
    /// Fetches data for a single range with simulated latency.
    /// Respects cancellation token to allow early exit during debounce or execution cancellation.
    /// </summary>
    public async Task<RangeChunk<int, int>> FetchAsync(Range<int> range, CancellationToken cancellationToken)
    {
        // Simulate I/O latency (network/database delay)
        // This delay is cancellable, allowing execution strategies to abort obsolete fetches
        await Task.Delay(_latency, cancellationToken).ConfigureAwait(false);

        // Generate data after delay completes
        return new RangeChunk<int, int>(range, GenerateDataForRange(range).ToList());
    }

    /// <summary>
    /// Fetches data for multiple ranges with simulated latency per range.
    /// Each range fetch includes the full latency delay to simulate realistic multi-gap scenarios.
    /// </summary>
    public async Task<IEnumerable<RangeChunk<int, int>>> FetchAsync(
        IEnumerable<Range<int>> ranges,
        CancellationToken cancellationToken)
    {
        var chunks = new List<RangeChunk<int, int>>();

        foreach (var range in ranges)
        {
            // Simulate I/O latency per range (cancellable)
            await Task.Delay(_latency, cancellationToken).ConfigureAwait(false);

            chunks.Add(new RangeChunk<int, int>(
                range,
                GenerateDataForRange(range).ToList()
            ));
        }

        return chunks;
    }

    /// <summary>
    /// Generates deterministic data for a range, respecting boundary inclusivity.
    /// Each position i in the range produces value i.
    /// Uses pattern matching to handle all 4 combinations of inclusive/exclusive boundaries.
    /// </summary>
    private IEnumerable<int> GenerateDataForRange(Range<int> range)
    {
        var start = (int)range.Start;
        var end = (int)range.End;

        switch (range)
        {
            case { IsStartInclusive: true, IsEndInclusive: true }:
                // [start, end]
                for (var i = start; i <= end; i++)
                    yield return i;
                break;

            case { IsStartInclusive: true, IsEndInclusive: false }:
                // [start, end)
                for (var i = start; i < end; i++)
                    yield return i;
                break;

            case { IsStartInclusive: false, IsEndInclusive: true }:
                // (start, end]
                for (var i = start + 1; i <= end; i++)
                    yield return i;
                break;

            default:
                // (start, end)
                for (var i = start + 1; i < end; i++)
                    yield return i;
                break;
        }
    }
}
