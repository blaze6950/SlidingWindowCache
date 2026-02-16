using Intervals.NET;
using Intervals.NET.Data;
using Intervals.NET.Data.Extensions;
using Intervals.NET.Domain.Abstractions;
using SlidingWindowCache.Infrastructure.Extensions;
using SlidingWindowCache.Public.Configuration;

namespace SlidingWindowCache.Infrastructure.Storage;

/// <summary>
/// Snapshot read strategy that stores data in a contiguous array for zero-allocation reads.
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
internal sealed class SnapshotReadStorage<TRange, TData, TDomain> : ICacheStorage<TRange, TData, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private readonly TDomain _domain;
    private TData[] _storage = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="SnapshotReadStorage{TRange,TData,TDomain}"/> class.
    /// </summary>
    /// <param name="domain">
    /// The domain defining the range characteristics.
    /// </param>
    public SnapshotReadStorage(TDomain domain)
    {
        _domain = domain;
    }

    /// <inheritdoc />
    public UserCacheReadMode Mode => UserCacheReadMode.Snapshot;

    /// <inheritdoc />
    public Range<TRange> Range { get; private set; }

    /// <inheritdoc />
    public void Rematerialize(RangeData<TRange, TData, TDomain> rangeData)
    {
        // Always allocate a new array, even if the size is unchanged
        // This is the trade-off of the Snapshot mode
        Range = rangeData.Range;
        _storage = rangeData.Data.ToArray();
    }

    /// <inheritdoc />
    public ReadOnlyMemory<TData> Read(Range<TRange> range)
    {
        if (_storage.Length == 0)
        {
            return ReadOnlyMemory<TData>.Empty;
        }

        // Calculate the offset and length for the requested range
        var startOffset = _domain.Distance(Range.Start.Value, range.Start.Value);
        var length = (int)range.Span(_domain);

        // Return a view directly over the internal array - zero allocations
        return new ReadOnlyMemory<TData>(_storage, (int)startOffset, length);
    }

    /// <inheritdoc />
    public RangeData<TRange, TData, TDomain> ToRangeData() => _storage.ToRangeData(Range, _domain);
}