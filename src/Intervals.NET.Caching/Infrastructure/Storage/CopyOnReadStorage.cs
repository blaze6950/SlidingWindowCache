using Intervals.NET;
using Intervals.NET.Data;
using Intervals.NET.Data.Extensions;
using Intervals.NET.Domain.Abstractions;
using Intervals.NET.Extensions;
using Intervals.NET.Caching.Infrastructure.Extensions;

namespace Intervals.NET.Caching.Infrastructure.Storage;

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
/// <item><description><c>_activeStorage</c> - Serves data to <c>Read()</c> and <c>ToRangeData()</c>; never mutated during those calls</description></item>
/// <item><description><c>_stagingBuffer</c> - Write-only during rematerialization; reused across operations</description></item>
/// </list>
/// <para><strong>Rematerialization Process:</strong></para>
/// <list type="number">
/// <item><description>Acquire <c>_lock</c></description></item>
/// <item><description>Clear staging buffer (preserves capacity)</description></item>
/// <item><description>Enumerate incoming range data into staging buffer (single-pass)</description></item>
/// <item><description>Swap staging buffer with active storage</description></item>
/// <item><description>Update <c>Range</c> to reflect new active storage</description></item>
/// <item><description>Release <c>_lock</c></description></item>
/// </list>
/// <para>
/// This ensures that active storage is never observed mid-swap by a concurrent <c>Read()</c> or
/// <c>ToRangeData()</c> call, preventing data races when range data is derived from the same storage
/// (e.g., during cache expansion per Invariant A.12).
/// </para>
/// <para><strong>Synchronization:</strong></para>
/// <para>
/// <c>Read()</c>, <c>Rematerialize()</c>, and <c>ToRangeData()</c> share a single <c>_lock</c>
/// object.
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>Rematerialize()</c> holds the lock only for the two-field swap and <c>Range</c> update
/// (bounded to two field writes and a property assignment — sub-microsecond). The enumeration
/// into the staging buffer happens <em>before</em> the lock is acquired.
/// </description></item>
/// <item><description>
/// <c>Read()</c> holds the lock for the duration of the array copy (O(n), bounded by cache size).
/// </description></item>
/// <item><description>
/// <c>ToRangeData()</c> is called from the user path and holds the lock while copying
/// <c>_activeStorage</c> to an immutable array snapshot. This ensures the returned
/// <see cref="RangeData{TRange,TData,TDomain}"/> captures a consistent
/// (<c>_activeStorage</c>, <c>Range</c>) pair and is decoupled from buffer reuse: a subsequent
/// <c>Rematerialize()</c> that swaps and clears the old active buffer cannot corrupt or
/// truncate data that is still referenced by an outstanding lazy enumerable.
/// </description></item>
/// </list>
/// <para>
    /// See Invariant A.4 for the conditional compliance note regarding this lock.
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
/// Both <c>Read()</c> and <c>ToRangeData()</c> acquire the lock, allocate a new array, and copy
/// data from active storage (copy-on-read semantics). This is a trade-off for cheaper
/// rematerialization compared to Snapshot mode.
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

    // Shared lock: acquired by Read(), Rematerialize(), and ToRangeData() to prevent observation of
    // mid-swap state and to ensure each caller captures a consistent (_activeStorage, Range) pair.
    private readonly object _lock = new();

    // Active storage: serves data to Read() and ToRangeData() operations; never mutated while _lock is held
    // volatile is NOT needed: Read(), ToRangeData(), and the swap in Rematerialize() access this field
    // exclusively under _lock, which provides full acquire/release fence semantics.
    private List<TData> _activeStorage = [];

    // Staging buffer: write-only during Rematerialize(); reused across operations
    // This buffer may grow but never shrinks, amortizing allocation cost
    // volatile is NOT needed: _stagingBuffer is only accessed by the rebalance thread outside the lock,
    // and inside _lock during the swap — it never crosses thread boundaries directly.
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
    public Range<TRange> Range { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    /// <para><strong>Staging Buffer Rematerialization:</strong></para>
    /// <para>
    /// This method implements a dual-buffer pattern to satisfy Invariants A.12, B.1-2:
    /// </para>
    /// <list type="number">
    /// <item><description>Acquire <c>_lock</c> (shared with <c>Read()</c> and <c>ToRangeData()</c>)</description></item>
    /// <item><description>Clear staging buffer (preserves capacity for reuse)</description></item>
    /// <item><description>Enumerate range data into staging buffer (single-pass, no double enumeration)</description></item>
    /// <item><description>Swap buffers: staging becomes active, old active becomes staging</description></item>
    /// <item><description>Update <c>Range</c> to reflect new active storage</description></item>
    /// </list>
    /// <para>
    /// <strong>Why this pattern?</strong> When <paramref name="rangeData"/> contains data derived from
    /// the same storage (e.g., during cache expansion via LINQ operations like Concat/Union), direct
    /// mutation of active storage would corrupt the enumeration. The staging buffer ensures active
    /// storage remains unchanged during enumeration, satisfying Invariant A.12b (cache contiguity).
    /// </para>
    /// <para>
    /// <strong>Why the lock?</strong> The buffer swap consists of two separate field writes, which are
    /// not atomic at the CPU level. Without the lock, a concurrent <c>Read()</c> or <c>ToRangeData()</c>
    /// on the User thread could observe <c>_activeStorage</c> mid-swap (new list reference but stale
    /// <c>Range</c>, or vice versa), producing incorrect results. The lock eliminates this window.
    /// Contention is bounded to the duration of this method call, not the full rebalance cycle.
    /// </para>
    /// <para>
    /// <strong>Memory efficiency:</strong> The staging buffer reuses capacity across rematerializations,
    /// avoiding repeated allocations for large sliding windows. The buffer may grow but never shrinks,
    /// amortizing allocation cost over time.
    /// </para>
    /// </remarks>
    public void Rematerialize(RangeData<TRange, TData, TDomain> rangeData)
    {
        // Enumerate incoming data BEFORE acquiring the lock.
        // rangeData.Data may be a lazy LINQ chain over _activeStorage (e.g., during cache expansion).
        // Holding the lock during enumeration would block concurrent Read() calls for the full
        // enumeration duration. Instead, we materialize into a local staging buffer first, then
        // acquire the lock only for the fast swap operation.
        _stagingBuffer.Clear();                        // Preserves capacity
        _stagingBuffer.AddRange(rangeData.Data);       // Single-pass enumeration outside the lock

        lock (_lock)
        {
            // Swap buffers: staging (now filled) becomes active; old active becomes staging for next use.
            // Range update is inside the lock so Read() always observes a consistent (list, Range) pair.
            // There is no case when during Read the read buffer is changed due to lock.
            (_activeStorage, _stagingBuffer) = (_stagingBuffer, _activeStorage);
            Range = rangeData.Range;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para><strong>Copy-on-Read Semantics:</strong></para>
    /// <para>
    /// Each read acquires <c>_lock</c>, allocates a new array, and copies the requested data from
    /// active storage. The lock prevents observing active storage mid-swap during a concurrent
    /// <c>Rematerialize()</c> call, ensuring the returned data is always consistent with <c>Range</c>.
    /// </para>
    /// <para>
    /// This is the trade-off for cheaper rematerialization: reads are more expensive (lock + alloc + copy),
    /// but rematerialization avoids allocating a new backing array each time.
    /// </para>
    /// </remarks>
    public ReadOnlyMemory<TData> Read(Range<TRange> range)
    {
        lock (_lock)
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
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Acquires <c>_lock</c> and captures an immutable array snapshot of <c>_activeStorage</c>
    /// together with the current <c>Range</c>, returning a fully materialized
    /// <see cref="RangeData{TRange,TData,TDomain}"/> backed by that snapshot.
    /// </para>
    /// <para>
    /// <strong>Why synchronized?</strong> This method is called from the <em>user path</em>
    /// (e.g., <c>UserRequestHandler</c>) concurrently with <c>Rematerialize()</c> on the rebalance
    /// thread. Without the lock, two distinct races are possible:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <strong>Non-atomic pair read</strong>: a concurrent buffer swap could complete between the
    /// read of <c>_activeStorage</c> and the read of <c>Range</c>, pairing the new list with the
    /// old range (or vice versa), violating the <see cref="RangeData{TRange,TData,TDomain}"/>
    /// contract that the range length must match the data count.
    /// </description></item>
    /// <item><description>
    /// <strong>Dangling lazy reference</strong>: a lazy <c>IEnumerable</c> over the live
    /// <c>_activeStorage</c> list is published as an <c>Intent</c> and later enumerated on the
    /// rebalance thread. A subsequent <c>Rematerialize()</c> swaps that list to
    /// <c>_stagingBuffer</c> and immediately clears it via <c>_stagingBuffer.Clear()</c>
    /// (line 151), corrupting or emptying the data under the still-live enumerable.
    /// </description></item>
    /// </list>
    /// <para>
    /// The lock eliminates both races. The <c>.ToArray()</c> copy decouples the returned
    /// <see cref="RangeData{TRange,TData,TDomain}"/> from the mutable buffer lifecycle:
    /// once the snapshot array is created, no future <c>Rematerialize()</c> can affect it.
    /// </para>
    /// <para>
    /// <strong>Cost:</strong> O(n) time and O(n) allocation (n = number of cached elements),
    /// identical to <c>Read()</c>. This is the accepted trade-off: <c>ToRangeData()</c> is called
    /// at most once per user request, so the amortized impact on throughput is negligible.
    /// </para>
    /// </remarks>
    public RangeData<TRange, TData, TDomain> ToRangeData()
    {
        lock (_lock)
        {
            return _activeStorage.ToArray().ToRangeData(Range, _domain);
        }
    }
}
