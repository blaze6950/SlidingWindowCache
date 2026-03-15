using Intervals.NET.Caching.Extensions;
using Intervals.NET.Data;
using Intervals.NET.Data.Extensions;
using Intervals.NET.Domain.Abstractions;

namespace Intervals.NET.Caching.SlidingWindow.Infrastructure.Storage;

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
    // volatile: Rematerialize() (rebalance thread) and Read() (user thread) access this field
    // concurrently without a lock. volatile provides the acquire/release fence needed to ensure
    // the user thread always observes the latest array reference published by the rebalance thread.
    private volatile TData[] _storage = [];

    public SnapshotReadStorage(TDomain domain)
    {
        _domain = domain;
    }

    /// <inheritdoc />
    public Range<TRange> Range { get; private set; }

    /// <inheritdoc />
    public void Rematerialize(RangeData<TRange, TData, TDomain> rangeData)
    {
        // Always allocate a new array, even if the size is unchanged
        // This is the trade-off of the Snapshot mode
        //
        // Write ordering is intentional and critical for thread safety:
        //   1. Range is written first (plain store, no fence)
        //   2. _storage is written second as a volatile store (release fence)
        //
        // The volatile store on _storage acts as a release fence for ALL preceding stores,
        // including Range. The user thread's volatile read of _storage (in Read()) acts as
        // an acquire fence, guaranteeing it observes the Range value written before the
        // volatile store. This is correct and safe under .NET's memory model.
        Range = rangeData.Range;
        _storage = rangeData.Data.ToArray();
    }

    /// <inheritdoc />
    public ReadOnlyMemory<TData> Read(Range<TRange> range)
    {
        // Capture _storage once: this single volatile read provides the acquire fence that
        // guarantees all writes preceding Rematerialize()'s volatile store are visible —
        // including the Range write. Using 'storage' for all subsequent accesses avoids a
        // second volatile read that could see a different (newer) array than the Range value
        // captured on the same call, which would produce an inconsistent offset calculation.
        var storage = _storage;

        if (storage.Length == 0)
        {
            return ReadOnlyMemory<TData>.Empty;
        }

        // Calculate the offset and length for the requested range.
        // Note: if `range` extends outside the stored `Range`, `startOffset` or the derived
        // array slice may be out of bounds. The caller (UserRequestHandler) is responsible for
        // ensuring that only ranges fully contained within Range are passed here.
        var startOffset = _domain.Distance(Range.Start.Value, range.Start.Value);
        var length = (int)range.Span(_domain);

        // Return a view directly over the internal array - zero allocations
        return new ReadOnlyMemory<TData>(storage, (int)startOffset, length);
    }

    /// <inheritdoc />
    public RangeData<TRange, TData, TDomain> ToRangeData() => _storage.ToRangeData(Range, _domain);
}