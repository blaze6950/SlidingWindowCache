namespace Intervals.NET.Caching.Dto;

/// <summary>
/// Represents a chunk of data associated with a specific range, returned by <see cref="IDataSource{TRange,TData}"/>.
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of data elements.</typeparam>
/// <param name="Range">
/// The range of data in this chunk.
/// Null if no data is available for the requested range (e.g., out of physical bounds).
/// When non-null, the Data enumerable MUST contain exactly Range.Span elements.
/// </param>
/// <param name="Data">
/// The data elements for the range.
/// Empty sequence when Range is null.
/// </param>
/// <remarks>
/// <para><strong>IDataSource Contract:</strong></para>
/// <para>Implementations MUST return null Range when no data is available
/// (e.g., requested range beyond physical database boundaries, time-series temporal limits).</para>
/// <para>Implementations MUST NOT throw exceptions for out-of-bounds requests.</para>
/// <para><strong>Example - Bounded Database:</strong></para>
/// <code>
/// // Database with records ID 100-500
/// // Request [50..150]  > Return RangeChunk([100..150], 51 records)
/// // Request [600..700] > Return RangeChunk(null, empty list)
/// </code>
/// </remarks>
public sealed record RangeChunk<TRange, TData>(Range<TRange>? Range, IEnumerable<TData> Data)
    where TRange : IComparable<TRange>;
