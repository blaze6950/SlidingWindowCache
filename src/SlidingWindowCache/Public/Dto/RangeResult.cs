using Intervals.NET;

namespace SlidingWindowCache.Public.Dto;

/// <summary>
/// Represents the result of a cache data request, containing the actual available range and data.
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of cached data.</typeparam>
/// <param name="Range">
/// The actual range of data available. 
/// Null if no data is available for the requested range.
/// May be a subset of the requested range if data is truncated at boundaries.
/// </param>
/// <param name="Data">
/// The data for the available range.
/// Empty if Range is null.
/// </param>
/// <remarks>
/// <para><strong>ActualRange Semantics:</strong></para>
/// <para>Range = RequestedRange ∩ PhysicallyAvailableDataRange</para>
/// <para>When DataSource has bounded data (e.g., database with min/max IDs), 
/// Range indicates what portion of the request was actually available.</para>
/// <para><strong>Example Usage:</strong></para>
/// <code>
/// var result = await cache.GetDataAsync(Range.Closed(50, 600), ct);
/// if (result.Range.HasValue)
/// {
///     Console.WriteLine($"Available: {result.Range.Value}");
///     ProcessData(result.Data);
/// }
/// else
/// {
///     Console.WriteLine("No data available");
/// }
/// </code>
/// </remarks>
public sealed record RangeResult<TRange, TData>(
    Range<TRange>? Range,
    ReadOnlyMemory<TData> Data
) where TRange : IComparable<TRange>;
