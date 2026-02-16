using Intervals.NET;
using Intervals.NET.Data;
using Intervals.NET.Data.Extensions;
using Intervals.NET.Domain.Abstractions;
using Intervals.NET.Extensions;
using SlidingWindowCache.Infrastructure.Extensions;
using SlidingWindowCache.Public.Configuration;

namespace SlidingWindowCache.Infrastructure.Storage;

/// <summary>
/// CopyOnRead strategy that stores data using a dual-buffer (staging buffer) pattern.
/// Uses two internal lists: one active storage for reads, one staging buffer for rematerialization.
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
/// <para><strong>Dual-Buffer Staging Pattern:</strong></para>
/// <para>
/// This storage maintains two internal lists:
/// </para>
/// <list type="bullet">
/// <item><description><c>_activeStorage</c> - Immutable during reads, used for serving data</description></item>
/// <item><description><c>_stagingBuffer</c> - Write-only during rematerialization, reused across operations</description></item>
/// </list>
/// <para><strong>Rematerialization Process:</strong></para>
/// <list type="number">
/// <item><description>Clear staging buffer (preserves capacity)</description></item>
/// <item><description>Enumerate incoming range data into staging buffer (single-pass)</description></item>
/// <item><description>Atomically swap staging buffer with active storage</description></item>
/// </list>
/// <para>
/// This ensures that active storage is never mutated during enumeration, preventing correctness issues
/// when range data is derived from the same storage (e.g., during cache expansion per Invariant A.3.8).
/// </para>
/// <para><strong>Memory Behavior:</strong></para>
/// <list type="bullet">
/// <item><description>Staging buffer may grow but never shrinks</description></item>
/// <item><description>Avoids repeated allocations by reusing capacity</description></item>
/// <item><description>No temporary arrays beyond the two buffers</description></item>
/// <item><description>Predictable allocation behavior for large sliding windows</description></item>
/// </list>
/// <para><strong>Read Behavior:</strong></para>
/// <para>
/// Each read operation allocates a new array and copies data from active storage (copy-on-read semantics).
/// This is a trade-off for cheaper rematerialization compared to Snapshot mode.
/// </para>
/// <para><strong>When to Use:</strong></para>
/// <list type="bullet">
/// <item><description>Large sliding windows with frequent rematerialization</description></item>
/// <item><description>Infrequent reads relative to rematerialization</description></item>
/// <item><description>Scenarios where backing memory reuse is valuable</description></item>
/// <item><description>Multi-level cache composition (background layer feeding snapshot-based cache)</description></item>
/// </list>
/// </remarks>
internal sealed class CopyOnReadStorage<TRange, TData, TDomain> : ICacheStorage<TRange, TData, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private readonly TDomain _domain;

    // Active storage: immutable during reads, serves data to Read() operations
    private List<TData> _activeStorage = [];

    // Staging buffer: write-only during rematerialization, reused across operations
    // This buffer may grow but never shrinks, amortizing allocation cost
    private List<TData> _stagingBuffer = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="CopyOnReadStorage{TRange,TData,TDomain}"/> class.
    /// </summary>
    /// <param name="domain">
    /// The domain defining the range characteristics.
    /// </param>
    public CopyOnReadStorage(TDomain domain)
    {
        _domain = domain;
    }

    /// <inheritdoc />
    public UserCacheReadMode Mode => UserCacheReadMode.CopyOnRead;

    /// <inheritdoc />
    public Range<TRange> Range { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    /// <para><strong>Staging Buffer Rematerialization:</strong></para>
    /// <para>
    /// This method implements a dual-buffer pattern to satisfy Invariants A.3.8, B.11-12:
    /// </para>
    /// <list type="number">
    /// <item><description>Clear staging buffer (preserves capacity for reuse)</description></item>
    /// <item><description>Enumerate range data into staging buffer (single-pass, no double enumeration)</description></item>
    /// <item><description>Atomically swap buffers: staging becomes active, old active becomes staging</description></item>
    /// </list>
    /// <para>
    /// <strong>Why this pattern?</strong> When <paramref name="rangeData"/> contains data derived from
    /// the same storage (e.g., during cache expansion via LINQ operations like Concat/Union), direct
    /// mutation of active storage would corrupt the enumeration. The staging buffer ensures active
    /// storage remains immutable during enumeration, satisfying Invariant A.3.9a (cache contiguity).
    /// </para>
    /// <para>
    /// <strong>Memory efficiency:</strong> The staging buffer reuses capacity across rematerializations,
    /// avoiding repeated allocations for large sliding windows. The buffer may grow but never shrinks,
    /// amortizing allocation cost over time.
    /// </para>
    /// </remarks>
    public void Rematerialize(RangeData<TRange, TData, TDomain> rangeData)
    {
        // Clear staging buffer (preserves capacity for reuse)
        _stagingBuffer.Clear();

        // Single-pass enumeration: materialize incoming range data into staging buffer
        // This is safe even if rangeData.Data is based on _activeStorage (e.g., LINQ chains during expansion)
        // because we never mutate _activeStorage during enumeration
        _stagingBuffer.AddRange(rangeData.Data);

        // Atomically swap buffers: staging becomes active, old active becomes staging for next use
        // This swap is the only point where active storage is replaced, satisfying Invariant B.12 (atomic changes)
        (_activeStorage, _stagingBuffer) = (_stagingBuffer, _activeStorage);

        // Update range to reflect new active storage (part of atomic change)
        Range = rangeData.Range;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para><strong>Copy-on-Read Semantics:</strong></para>
    /// <para>
    /// Each read allocates a new array and copies the requested data from active storage.
    /// This is the trade-off for cheaper rematerialization: reads are more expensive,
    /// but rematerialization avoids allocating a new backing array each time.
    /// </para>
    /// <para>
    /// Active storage is immutable during this operation, ensuring correctness within
    /// the single-consumer model (Invariant A.1-1: no concurrent execution).
    /// </para>
    /// </remarks>
    public ReadOnlyMemory<TData> Read(Range<TRange> range)
    {
        if (_activeStorage.Count == 0)
        {
            return ReadOnlyMemory<TData>.Empty;
        }

        // Validate that the requested range is within the stored range
        if (!Range.Contains(range))
        {
            throw new ArgumentOutOfRangeException(nameof(range),
                $"Requested range {range} is not contained within the cached range {Range}");
        }

        // Calculate the offset and length for the requested range
        var startOffset = _domain.Distance(Range.Start.Value, range.Start.Value);
        var length = (int)range.Span(_domain);

        // Validate bounds before accessing storage
        if (startOffset < 0 || length < 0 || (int)startOffset + length > _activeStorage.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(range),
                $"Calculated offset {startOffset} and length {length} exceed storage bounds (storage count: {_activeStorage.Count})");
        }

        // Allocate a new array and copy the requested data (copy-on-read semantics)
        var result = new TData[length];
        for (var i = 0; i < length; i++)
        {
            result[i] = _activeStorage[(int)startOffset + i];
        }

        return new ReadOnlyMemory<TData>(result);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Returns a <see cref="RangeData{TRangeType,TDataType,TRangeDomain}"/> representing
    /// the current active storage. The returned data is a lazy enumerable over the active list.
    /// </para>
    /// <para>
    /// This method is safe because active storage is immutable during reads and only replaced
    /// atomically during rematerialization (Invariant B.12).
    /// </para>
    /// </remarks>
    public RangeData<TRange, TData, TDomain> ToRangeData() => _activeStorage.ToRangeData(Range, _domain);
}