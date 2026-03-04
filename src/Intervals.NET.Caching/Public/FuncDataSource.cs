using Intervals.NET;
using Intervals.NET.Caching.Public.Dto;

namespace Intervals.NET.Caching.Public;

/// <summary>
/// An <see cref="IDataSource{TRange,TData}"/> implementation that delegates
/// <see cref="FetchAsync(Range{TRange}, CancellationToken)"/> to a caller-supplied
/// asynchronous function, enabling data sources to be created inline without
/// defining a dedicated class.
/// </summary>
/// <typeparam name="TRange">
/// The type representing range boundaries. Must implement <see cref="IComparable{T}"/>.
/// </typeparam>
/// <typeparam name="TData">
/// The type of data being fetched.
/// </typeparam>
/// <remarks>
/// <para><strong>Purpose:</strong></para>
/// <para>
/// Use <see cref="FuncDataSource{TRange,TData}"/> when the fetch logic is simple enough
/// to express as a lambda or method reference and a full <see cref="IDataSource{TRange,TData}"/>
/// subclass would add unnecessary ceremony.
/// </para>
/// <para><strong>Batch Fetching:</strong></para>
/// <para>
/// The batch <c>FetchAsync</c> overload is not overridden here; it falls through to the
/// <see cref="IDataSource{TRange,TData}"/> default implementation, which parallelizes
/// calls to the single-range delegate via <c>Task.WhenAll</c>.
/// </para>
/// <para><strong>Example — unbounded integer source:</strong></para>
/// <code>
/// IDataSource&lt;int, string&gt; source = new FuncDataSource&lt;int, string&gt;(
///     async (range, ct) =>
///     {
///         var data = await myService.QueryAsync(range, ct);
///         return new RangeChunk&lt;int, string&gt;(range, data);
///     });
/// </code>
/// <para><strong>Example — bounded source with null-range contract:</strong></para>
/// <code>
/// IDataSource&lt;int, string&gt; bounded = new FuncDataSource&lt;int, string&gt;(
///     async (range, ct) =>
///     {
///         var available = range.Intersect(Range.Closed(minId, maxId));
///         if (available is null)
///             return new RangeChunk&lt;int, string&gt;(null, []);
///
///         var data = await myService.QueryAsync(available, ct);
///         return new RangeChunk&lt;int, string&gt;(available, data);
///     });
/// </code>
/// </remarks>
public sealed class FuncDataSource<TRange, TData> : IDataSource<TRange, TData>
    where TRange : IComparable<TRange>
{
    private readonly Func<Range<TRange>, CancellationToken, Task<RangeChunk<TRange, TData>>> _fetchFunc;

    /// <summary>
    /// Initializes a new <see cref="FuncDataSource{TRange,TData}"/> with the specified fetch delegate.
    /// </summary>
    /// <param name="fetchFunc">
    /// The asynchronous function invoked for every single-range fetch. Must not be <see langword="null"/>.
    /// The function receives the requested <see cref="Range{TRange}"/> and a
    /// <see cref="CancellationToken"/>, and must return a <see cref="RangeChunk{TRange,TData}"/>
    /// that satisfies the <see cref="IDataSource{TRange,TData}"/> boundary contract.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="fetchFunc"/> is <see langword="null"/>.
    /// </exception>
    public FuncDataSource(
        Func<Range<TRange>, CancellationToken, Task<RangeChunk<TRange, TData>>> fetchFunc)
    {
        ArgumentNullException.ThrowIfNull(fetchFunc);
        _fetchFunc = fetchFunc;
    }

    /// <inheritdoc />
    public Task<RangeChunk<TRange, TData>> FetchAsync(
        Range<TRange> range,
        CancellationToken cancellationToken)
        => _fetchFunc(range, cancellationToken);
}
