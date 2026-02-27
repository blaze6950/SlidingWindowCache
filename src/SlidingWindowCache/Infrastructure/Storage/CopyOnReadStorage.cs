using Intervals.NET;
using Intervals.NET.Data;
using Intervals.NET.Data.Extensions;
using Intervals.NET.Domain.Abstractions;
using Intervals.NET.Extensions;
using SlidingWindowCache.Infrastructure.Extensions;

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
/// <item><description><c>_activeStorage</c> - Serves data to <c>Read()</c> operations; never mutated during reads</description></item>
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
/// This ensures that active storage is never observed mid-swap by a concurrent <c>Read()</c> call,
/// preventing data races when range data is derived from the same storage (e.g., during cache expansion
/// per Invariant A.3.8).
/// </para>
/// <para><strong>Synchronization:</strong></para>
/// <para>
/// <c>Read()</c> and <c>Rematerialize()</c> share a single <c>_lock</c> object.
/// This is the accepted trade-off for buffer reuse: contention is bounded to the duration of a
/// single <c>Rematerialize()</c> call (a sub-millisecond linear copy), not the full rebalance cycle.
/// <c>ToRangeData()</c> is only called by the rebalance path (the same thread as <c>Rematerialize()</c>)
/// and is therefore not synchronized.
/// </para>
/// <para>
/// See Invariant A.2 for the conditional compliance note regarding this lock.
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
/// Each read operation acquires the lock, allocates a new array, and copies data from active storage
/// (copy-on-read semantics). This is a trade-off for cheaper rematerialization compared to Snapshot mode.
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

    // Shared lock: acquired by both Read() and Rematerialize() to prevent observation of mid-swap state.
    // ToRangeData() is not synchronized because it is only called from the rebalance path.
    private readonly object _lock = new();

    // Active storage: serves data to Read() operations; never mutated while _lock is held by Read()
    private List<TData> _activeStorage = [];

    // Staging buffer: write-only during Rematerialize(); reused across operations
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
    public Range<TRange> Range { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    /// <para><strong>Staging Buffer Rematerialization:</strong></para>
    /// <para>
    /// This method implements a dual-buffer pattern to satisfy Invariants A.3.8, B.11-12:
    /// </para>
    /// <list type="number">
    /// <item><description>Acquire <c>_lock</c> (shared with <c>Read()</c>)</description></item>
    /// <item><description>Clear staging buffer (preserves capacity for reuse)</description></item>
    /// <item><description>Enumerate range data into staging buffer (single-pass, no double enumeration)</description></item>
    /// <item><description>Swap buffers: staging becomes active, old active becomes staging</description></item>
    /// <item><description>Update <c>Range</c> to reflect new active storage</description></item>
    /// </list>
    /// <para>
    /// <strong>Why this pattern?</strong> When <paramref name="rangeData"/> contains data derived from
    /// the same storage (e.g., during cache expansion via LINQ operations like Concat/Union), direct
    /// mutation of active storage would corrupt the enumeration. The staging buffer ensures active
    /// storage remains unchanged during enumeration, satisfying Invariant A.3.9a (cache contiguity).
    /// </para>
    /// <para>
    /// <strong>Why the lock?</strong> The buffer swap consists of two separate field writes, which are
    /// not atomic at the CPU level. Without the lock, a concurrent <c>Read()</c> on the User thread could
    /// observe <c>_activeStorage</c> mid-swap (new list reference but stale <c>Range</c>, or vice versa),
    /// producing incorrect results. The lock eliminates this window. Contention is bounded to the duration
    /// of this method call, not the full rebalance cycle.
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
    /// Returns a <see cref="RangeData{TRange,TData,TDomain}"/> representing
    /// the current active storage. The returned data is a lazy enumerable over the active list.
    /// </para>
    /// <para>
    /// This method is only called from the rebalance path — the same thread that calls
    /// <c>Rematerialize()</c> — so it is not synchronized. It must not be called concurrently
    /// with <c>Rematerialize()</c>.
    /// </para>
    /// </remarks>
    public RangeData<TRange, TData, TDomain> ToRangeData() => _activeStorage.ToRangeData(Range, _domain);
}
