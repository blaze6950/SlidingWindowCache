using Intervals.NET;
using Intervals.NET.Caching.Public;
using Intervals.NET.Caching.Public.Dto;

namespace Intervals.NET.Caching.Tests.Infrastructure.DataSources;

/// <summary>
/// A configurable IDataSource that delegates fetch calls through a user-supplied callback,
/// allowing individual tests to inject faults (exceptions) or control returned data on a per-call basis.
/// Intended for exception-handling tests only. For boundary/null-Range scenarios use BoundedDataSource.
/// </summary>
/// <typeparam name="TRange">The range boundary type.</typeparam>
/// <typeparam name="TData">The data type.</typeparam>
public sealed class FaultyDataSource<TRange, TData> : IDataSource<TRange, TData>
    where TRange : IComparable<TRange>
{
    private readonly Func<Range<TRange>, IReadOnlyList<TData>> _fetchSingleRange;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    /// <param name="fetchSingleRange">
    /// Callback invoked for every single-range fetch. May throw to simulate failures,
    /// or return any <see cref="IReadOnlyList{T}"/> to control the returned data.
    /// The <see cref="RangeChunk{TRange,TData}.Range"/> in the result is always set to
    /// the requested range — this class does not support returning a null Range.
    /// </param>
    public FaultyDataSource(Func<Range<TRange>, IReadOnlyList<TData>> fetchSingleRange)
    {
        _fetchSingleRange = fetchSingleRange;
    }

    /// <inheritdoc />
    public Task<RangeChunk<TRange, TData>> FetchAsync(Range<TRange> range, CancellationToken cancellationToken)
    {
        var data = _fetchSingleRange(range);
        return Task.FromResult(new RangeChunk<TRange, TData>(range, data));
    }

    /// <inheritdoc />
    public Task<IEnumerable<RangeChunk<TRange, TData>>> FetchAsync(
        IEnumerable<Range<TRange>> ranges,
        CancellationToken cancellationToken)
    {
        var chunks = new List<RangeChunk<TRange, TData>>();
        foreach (var range in ranges)
        {
            var data = _fetchSingleRange(range);
            chunks.Add(new RangeChunk<TRange, TData>(range, data));
        }

        return Task.FromResult<IEnumerable<RangeChunk<TRange, TData>>>(chunks);
    }

    /// <summary>
    /// Generates sequential string items ("Item-N") for a closed integer range.
    /// Convenience helper for tests using <c>IDataSource&lt;int, string&gt;</c>.
    /// </summary>
    public static IReadOnlyList<string> GenerateStringData(Range<int> range)
    {
        var data = new List<string>();
        for (var i = range.Start.Value; i <= range.End.Value; i++)
        {
            data.Add($"Item-{i}");
        }

        return data;
    }
}
