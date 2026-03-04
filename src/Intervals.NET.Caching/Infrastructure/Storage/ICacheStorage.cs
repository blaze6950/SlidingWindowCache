using Intervals.NET;
using Intervals.NET.Data;
using Intervals.NET.Domain.Abstractions;

namespace Intervals.NET.Caching.Infrastructure.Storage;

/// <summary>
/// Internal strategy interface for handling user cache read operations.
/// </summary>
/// <typeparam name="TRange">
/// The type representing the range boundaries. Must implement <see cref="IComparable{T}"/>.
/// </typeparam>
/// <typeparam name="TData">
/// The type of data being cached.
/// </typeparam>
/// <typeparam name="TDomain">
/// The type representing the domain of the ranges. Must implement <see cref="IRangeDomain{TRange}"/>.
/// </typeparam>
/// <remarks>
/// This interface is an implementation detail of the window cache.
/// It represents behavior over internal state, not a public service.
/// </remarks>
internal interface ICacheStorage<TRange, TData, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    /// <summary>
    /// Gets the current range of data stored in internal storage.
    /// </summary>
    Range<TRange> Range { get; }

    /// <summary>
    /// Rematerializes internal storage from the provided range data.
    /// </summary>
    /// <param name="rangeData">
    /// The range data to materialize into internal storage.
    /// </param>
    /// <remarks>
    /// This method is called during cache initialization and rebalancing.
    /// All elements from the range data are rewritten into internal storage.
    /// </remarks>
    void Rematerialize(RangeData<TRange, TData, TDomain> rangeData);

    /// <summary>
    /// Reads data for the specified range from internal storage.
    /// </summary>
    /// <param name="range">
    /// The range for which to retrieve data.
    /// </param>
    /// <returns>
    /// A <see cref="ReadOnlyMemory{T}"/> containing the data for the specified range.
    /// </returns>
    /// <remarks>
    /// The behavior of this method depends on the strategy:
    /// - Snapshot: Returns a view directly over internal array (zero allocations).
    /// - CopyOnRead: Allocates a new array and copies the requested data.
    /// </remarks>
    ReadOnlyMemory<TData> Read(Range<TRange> range);

    /// <summary>
    /// Converts the current internal storage state into a <see cref="RangeData{TRange,TData,TDomain}"/> representation.
    /// </summary>
    /// <returns>
    /// A <see cref="RangeData{TRange,TData,TDomain}"/> representing the current state of internal storage.
    /// </returns>
    RangeData<TRange, TData, TDomain> ToRangeData();
}