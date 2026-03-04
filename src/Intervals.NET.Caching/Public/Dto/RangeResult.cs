using Intervals.NET;

namespace Intervals.NET.Caching.Public.Dto;

/// <summary>
/// Represents the result of a cache data request, containing the actual available range, data,
/// and a description of how the request was fulfilled relative to the cache state.
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of cached data.</typeparam>
/// <param name="Range">
/// The actual range of data available.
/// Null if no data is available for the requested range (physical boundary miss).
/// May be a subset of the requested range if data is truncated at boundaries.
/// </param>
/// <param name="Data">
/// The data for the available range.
/// Empty if <paramref name="Range"/> is null.
/// </param>
/// <param name="CacheInteraction">
/// Describes how the request was fulfilled relative to the current cache state.
/// See <see cref="Dto.CacheInteraction"/> for the three possible values and their semantics.
/// This field is the foundation for the opt-in hybrid consistency mode:
/// <c>GetDataAndWaitOnMissAsync</c> awaits idle only when this is
/// <see cref="Dto.CacheInteraction.PartialHit"/> or <see cref="Dto.CacheInteraction.FullMiss"/>.
/// </param>
/// <remarks>
/// <para><strong>Range Semantics:</strong></para>
/// <para>Range = RequestedRange ∩ PhysicallyAvailableDataRange</para>
/// <para>When the data source has bounded data (e.g., a database with min/max IDs),
/// <paramref name="Range"/> indicates what portion of the request was actually available.</para>
/// <para><strong>Constructor Visibility:</strong></para>
/// <para>
/// The primary constructor is <c>internal</c>. <see cref="RangeResult{TRange,TData}"/> instances
/// are produced exclusively by <c>UserRequestHandler</c> and are consumed publicly. This prevents
/// external code from constructing results with inconsistent field combinations.
/// </para>
/// <para><strong>Example Usage:</strong></para>
/// <code>
/// var result = await cache.GetDataAsync(Range.Closed(50, 600), ct);
/// if (result.Range.HasValue)
/// {
///     Console.WriteLine($"Available: {result.Range.Value}");
///     Console.WriteLine($"Cache interaction: {result.CacheInteraction}");
///     ProcessData(result.Data);
/// }
/// else
/// {
///     Console.WriteLine("No data available for the requested range.");
/// }
/// </code>
/// </remarks>
public sealed record RangeResult<TRange, TData>
    where TRange : IComparable<TRange>
{
    /// <summary>
    /// Initializes a new <see cref="RangeResult{TRange,TData}"/>.
    /// </summary>
    /// <param name="range">The actual available range, or null for a physical boundary miss.</param>
    /// <param name="data">The data for the available range.</param>
    /// <param name="cacheInteraction">How the request was fulfilled relative to cache state.</param>
    internal RangeResult(Range<TRange>? range, ReadOnlyMemory<TData> data, CacheInteraction cacheInteraction)
    {
        Range = range;
        Data = data;
        CacheInteraction = cacheInteraction;
    }

    /// <summary>
    /// The actual range of data available.
    /// Null if no data is available for the requested range (physical boundary miss).
    /// May be a subset of the requested range if data is truncated at boundaries.
    /// </summary>
    public Range<TRange>? Range { get; internal init; }

    /// <summary>
    /// The data for the available range. Empty if <see cref="Range"/> is null.
    /// </summary>
    public ReadOnlyMemory<TData> Data { get; internal init; }

    /// <summary>
    /// Describes how this request was fulfilled relative to the current cache state.
    /// </summary>
    /// <remarks>
    /// Use this property to implement conditional consistency strategies.
    /// For example, <c>GetDataAndWaitOnMissAsync</c> awaits background rebalance completion
    /// only when this value is <see cref="Dto.CacheInteraction.PartialHit"/> or
    /// <see cref="Dto.CacheInteraction.FullMiss"/>, ensuring the cache is warm before returning.
    /// </remarks>
    public CacheInteraction CacheInteraction { get; internal init; }
}
