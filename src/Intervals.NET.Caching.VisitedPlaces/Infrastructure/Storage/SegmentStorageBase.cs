using Intervals.NET.Caching.VisitedPlaces.Core;

namespace Intervals.NET.Caching.VisitedPlaces.Infrastructure.Storage;

/// <summary>
/// Abstract base class for <see cref="ISegmentStorage{TRange,TData}"/> implementations,
/// consolidating the shared concurrency primitives and invariant logic that is identical
/// across all storage strategies.
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <remarks>
/// <para><strong>Shared Responsibilities:</strong></para>
/// <list type="bullet">
/// <item><description><see cref="Count"/> — live segment count via <c>Volatile.Read</c></description></item>
/// <item><description><see cref="TryRemove"/> — soft-delete via
///   <see cref="CachedSegment{TRange,TData}.TryMarkAsRemoved"/> with <c>Interlocked.Decrement</c>
///   on the live count</description></item>
/// <item><description><see cref="IncrementCount"/> — protected helper for subclass <c>Add</c> methods</description></item>
/// <item><description><see cref="Random"/> — per-instance <see cref="System.Random"/> for
///   <see cref="ISegmentStorage{TRange,TData}.TryGetRandomSegment"/> (Background Path only, no sync needed)</description></item>
/// <item><description><see cref="FindLastAtOrBefore{TElement,TAccessor}"/> — shared zero-allocation binary search
///   used by all strategies; each strategy provides its own <see cref="ISegmentAccessor{TElement}"/> implementation
///   as a private nested struct</description></item>
/// </list>
/// <para><strong>Threading Contract for <c>_count</c>:</strong></para>
/// <para>
/// <c>_count</c> is decremented via <c>Interlocked.Decrement</c> — safe from both the Background
/// Path (eviction) and the TTL thread. It is incremented via <c>Interlocked.Increment</c> through
/// <see cref="IncrementCount"/>, which is Background-Path-only.
/// <see cref="Count"/> reads via <c>Volatile.Read</c> for acquire-fence visibility.
/// </para>
/// <para>Alignment: Invariants VPC.A.10, VPC.B.5, VPC.C.2, S.H.4.</para>
/// </remarks>
internal abstract class SegmentStorageBase<TRange, TData> : ISegmentStorage<TRange, TData>
    where TRange : IComparable<TRange>
{
    /// <summary>
    /// Maximum number of retry attempts when sampling a random live segment
    /// before giving up. Used when all candidates within the retry budget are soft-deleted.
    /// </summary>
    protected const int RandomRetryLimit = 8;

    /// <summary>
    /// Per-instance random number generator for <see cref="TryGetRandomSegment"/>.
    /// Background-Path-only — no synchronization required.
    /// </summary>
    protected readonly Random Random = new();

    // Total count of live (non-removed) segments.
    // Decremented by TryRemove (which may be called from the TTL thread) via Interlocked.Decrement.
    // Incremented only on the Background Path via Interlocked.Increment (through IncrementCount).
    private int _count;

    /// <inheritdoc/>
    public int Count => Volatile.Read(ref _count);

    /// <inheritdoc/>
    public abstract IReadOnlyList<CachedSegment<TRange, TData>> FindIntersecting(Range<TRange> range);

    /// <inheritdoc/>
    public abstract void Add(CachedSegment<TRange, TData> segment);

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// Calls <see cref="CachedSegment{TRange,TData}.TryMarkAsRemoved"/> to atomically transition
    /// the segment to the removed state. If this is the first removal of the segment (the flag
    /// was not already set), the live count is decremented and <see langword="true"/> is returned.
    /// Subsequent calls for the same segment are no-ops (idempotent) and return
    /// <see langword="false"/>.
    /// </para>
    /// <para>
    /// The segment remains physically present in the underlying data structure until the next
    /// normalization pass. All read paths skip it immediately via the
    /// <see cref="CachedSegment{TRange,TData}.IsRemoved"/> flag.
    /// </para>
    /// <para><strong>Thread safety:</strong> Safe to call concurrently from the Background Path
    /// (eviction) and the TTL thread. <see cref="CachedSegment{TRange,TData}.TryMarkAsRemoved"/>
    /// uses <c>Interlocked.CompareExchange</c>; the live count uses <c>Interlocked.Decrement</c>.
    /// </para>
    /// </remarks>
    public bool TryRemove(CachedSegment<TRange, TData> segment)
    {
        if (segment.TryMarkAsRemoved())
        {
            Interlocked.Decrement(ref _count);
            return true;
        }

        return false;
    }

    /// <inheritdoc/>
    public abstract CachedSegment<TRange, TData>? TryGetRandomSegment();

    /// <summary>
    /// Atomically increments the live segment count.
    /// Called by subclass <see cref="Add"/> implementations after a segment has been
    /// successfully inserted into the underlying data structure.
    /// </summary>
    /// <remarks>
    /// <para><strong>Execution Context:</strong> Background Path only (single writer).</para>
    /// </remarks>
    protected void IncrementCount()
    {
        Interlocked.Increment(ref _count);
    }

    // -------------------------------------------------------------------------
    // Shared binary search infrastructure
    // -------------------------------------------------------------------------

    /// <summary>
    /// Zero-allocation accessor abstraction used by <see cref="FindLastAtOrBefore{TElement,TAccessor}"/>
    /// to extract the <c>Range.Start.Value</c> key from an array element without delegate allocation.
    /// Implement as a <see langword="readonly struct"/> nested inside the concrete storage class so
    /// the JIT specialises and inlines the call, and so the implementation stays co-located with
    /// the strategy that owns it.
    /// </summary>
    /// <typeparam name="TElement">The array element type.</typeparam>
    protected interface ISegmentAccessor<in TElement>
    {
        /// <summary>Returns the <c>Range.Start.Value</c> of <paramref name="element"/>.</summary>
        TRange GetStartValue(TElement element);
    }

    /// <summary>
    /// Binary-searches <paramref name="array"/> for the rightmost element whose
    /// <c>Range.Start.Value</c> is less than or equal to <paramref name="value"/>.
    /// </summary>
    /// <typeparam name="TElement">Array element type.</typeparam>
    /// <typeparam name="TAccessor">
    /// A <see langword="struct"/> implementing <see cref="ISegmentAccessor{TElement}"/>.
    /// Passed as a value type so the JIT specialises and inlines the key extraction — no
    /// delegate allocation, no virtual dispatch on the User Path hot path.
    /// Each concrete storage strategy defines its own <typeparamref name="TAccessor"/> as a
    /// private nested <see langword="readonly struct"/>.
    /// </typeparam>
    /// <param name="array">The sorted array to search (must be non-empty).</param>
    /// <param name="value">The upper-bound value to compare each element's start against.</param>
    /// <param name="accessor">The accessor instance (zero-size struct; use <c>default</c>).</param>
    /// <returns>
    /// The index of the rightmost element where <c>Start.Value &lt;= value</c>,
    /// or <c>-1</c> if every element has a start greater than <paramref name="value"/>.
    /// </returns>
    /// <remarks>
    /// <para><strong>Invariant:</strong> <paramref name="array"/> must be sorted ascending by
    /// <c>Range.Start.Value</c> (guaranteed by Invariant VPC.C.3 — segments store no shared
    /// discrete points and are stored in order).</para>
    /// <para><strong>Complexity:</strong> O(log n).</para>
    /// </remarks>
    protected static int FindLastAtOrBefore<TElement, TAccessor>(
        TElement[] array,
        TRange value,
        TAccessor accessor = default)
        where TAccessor : struct, ISegmentAccessor<TElement>
    {
        var lo = 0;
        var hi = array.Length - 1;

        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (accessor.GetStartValue(array[mid]).CompareTo(value) <= 0)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        // hi is the rightmost index where Start.Value <= value, or -1 if none.
        return hi;
    }
}
