using Intervals.NET;
using Intervals.NET.Data;
using Intervals.NET.Domain.Abstractions;

namespace Intervals.NET.Caching.Core.Rebalance.Intent;

/// <summary>
/// Represents the intent to rebalance the cache based on a requested range and the currently assembled range data.
/// </summary>
/// <param name="RequestedRange">
/// The range requested by the user that triggered the rebalance evaluation. This is the range for which the user is seeking data.
/// </param>
/// <param name="AssembledRangeData">
/// The current range of data available in the cache along with its associated data and domain information. This represents the state of the cache before any rebalance execution.
/// </param>
/// <typeparam name="TRange">
/// The type representing the range boundaries. Must implement <see cref="IComparable{T}"/> to allow for range comparisons and calculations.
/// </typeparam>
/// <typeparam name="TData">
/// The type of data being cached. This is the type of the elements stored within the ranges in the cache.
/// </typeparam>
/// <typeparam name="TDomain">
/// The type representing the domain of the ranges. Must implement <see cref="IRangeDomain{TRange}"/> to provide necessary domain-specific operations for range calculations and validations.
/// </typeparam>
internal record Intent<TRange, TData, TDomain>(
    Range<TRange> RequestedRange,
    RangeData<TRange, TData, TDomain> AssembledRangeData
)
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>;
