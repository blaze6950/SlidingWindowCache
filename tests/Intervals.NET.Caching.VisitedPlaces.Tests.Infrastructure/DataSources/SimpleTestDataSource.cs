using Intervals.NET.Caching.Dto;

namespace Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure.DataSources;

/// <summary>
/// A minimal generic test data source that generates integer data for any requested range
/// using sequential values matching the range boundaries.
/// </summary>
/// <remarks>
/// Use this instead of per-file private data source classes whenever the data-generation
/// logic is range-boundary-driven and does not require spy or fault-injection behavior.
/// </remarks>
public sealed class SimpleTestDataSource : IDataSource<int, int>
{
    private readonly bool _simulateAsyncDelay;

    /// <summary>
    /// Creates a new <see cref="SimpleTestDataSource"/> instance.
    /// </summary>
    /// <param name="simulateAsyncDelay">
    /// When <see langword="true"/>, adds a 1 ms <see cref="Task.Delay"/> to simulate real async I/O.
    /// Defaults to <see langword="false"/>.
    /// </param>
    public SimpleTestDataSource(bool simulateAsyncDelay = false)
    {
        _simulateAsyncDelay = simulateAsyncDelay;
    }

    /// <inheritdoc />
    public async Task<RangeChunk<int, int>> FetchAsync(
        Range<int> requestedRange,
        CancellationToken cancellationToken)
    {
        if (_simulateAsyncDelay)
        {
            await Task.Delay(1, cancellationToken);
        }

        var data = DataGenerationHelpers.GenerateDataForRange(requestedRange);
        return new RangeChunk<int, int>(requestedRange, data);
    }
}
