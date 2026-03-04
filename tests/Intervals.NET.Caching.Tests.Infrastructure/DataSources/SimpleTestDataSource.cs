using Intervals.NET;
using Intervals.NET.Caching.Public;
using Intervals.NET.Caching.Public.Dto;

namespace Intervals.NET.Caching.Tests.Infrastructure.DataSources;

/// <summary>
/// A minimal generic test data source that generates data for any requested range
/// using a caller-provided data generation function.
/// </summary>
/// <remarks>
/// This class exists to eliminate the near-identical private <c>TestDataSource</c>
/// inner classes that appear in multiple test files. Use this instead of per-file
/// private copies whenever the data-generation logic is range-boundary-driven and
/// does not need specific spy/fault injection behavior.
/// </remarks>
/// <typeparam name="TData">The type of data produced by this source.</typeparam>
public sealed class SimpleTestDataSource<TData> : IDataSource<int, TData>
{
    private readonly Func<int, TData> _valueFactory;
    private readonly bool _simulateAsyncDelay;

    /// <summary>
    /// Creates a new <see cref="SimpleTestDataSource{TData}"/> instance.
    /// </summary>
    /// <param name="valueFactory">
    /// Maps an integer position within the requested range to the data value at that position.
    /// Called once per element (each integer within the inclusive bounds of the range).
    /// </param>
    /// <param name="simulateAsyncDelay">
    /// When <see langword="true"/>, adds a 1 ms <see cref="Task.Delay"/> to simulate real async I/O.
    /// Defaults to <see langword="false"/>.
    /// </param>
    public SimpleTestDataSource(Func<int, TData> valueFactory, bool simulateAsyncDelay = false)
    {
        _valueFactory = valueFactory;
        _simulateAsyncDelay = simulateAsyncDelay;
    }

    /// <inheritdoc />
    public async Task<RangeChunk<int, TData>> FetchAsync(
        Range<int> requestedRange,
        CancellationToken cancellationToken)
    {
        if (_simulateAsyncDelay)
        {
            await Task.Delay(1, cancellationToken);
        }

        return new RangeChunk<int, TData>(requestedRange, GenerateData(requestedRange));
    }

    private List<TData> GenerateData(Range<int> range)
    {
        var data = new List<TData>();
        var start = (int)range.Start;
        var end = (int)range.End;

        switch (range)
        {
            case { IsStartInclusive: true, IsEndInclusive: true }:
                for (var i = start; i <= end; i++)
                    data.Add(_valueFactory(i));
                break;

            case { IsStartInclusive: true, IsEndInclusive: false }:
                for (var i = start; i < end; i++)
                    data.Add(_valueFactory(i));
                break;

            case { IsStartInclusive: false, IsEndInclusive: true }:
                for (var i = start + 1; i <= end; i++)
                    data.Add(_valueFactory(i));
                break;

            default:
                for (var i = start + 1; i < end; i++)
                    data.Add(_valueFactory(i));
                break;
        }

        return data;
    }
}
