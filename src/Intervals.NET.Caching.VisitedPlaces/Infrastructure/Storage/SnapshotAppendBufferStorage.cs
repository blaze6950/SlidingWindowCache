using Intervals.NET.Extensions;
using Intervals.NET.Caching.VisitedPlaces.Core;

namespace Intervals.NET.Caching.VisitedPlaces.Infrastructure.Storage;

/// <summary>
/// Segment storage backed by a volatile snapshot array and a small fixed-size append buffer.
/// Optimised for small caches (&lt;85 KB total data, &lt;~50 segments) with high read-to-write ratios.
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <remarks>
/// <para><strong>Data Structure:</strong></para>
/// <list type="bullet">
/// <item><description><c>_snapshot</c> — sorted array of live segments; published via <c>Volatile.Write</c> (User Path)</description></item>
/// <item><description><c>_appendBuffer</c> — fixed-size buffer for recently-added segments (Background Path only)</description></item>
/// </list>
/// <para><strong>Soft-delete via <see cref="CachedSegment{TRange,TData}.IsRemoved"/>:</strong></para>
/// <para>
/// Rather than maintaining a separate <c>_softDeleted</c> collection (which would require
/// synchronization between the Background Path and the TTL thread), this implementation
/// delegates soft-delete tracking entirely to <see cref="CachedSegment{TRange,TData}.IsRemoved"/>.
/// The flag is set atomically by <see cref="CachedSegment{TRange,TData}.TryMarkAsRemoved"/> and
/// never reset, so it is safe to read from any thread without a lock.
    /// All read paths (<see cref="FindIntersecting"/>, <see cref="TryGetRandomSegment"/>,
    /// <see cref="Normalize"/>) simply skip segments whose <c>IsRemoved</c> flag is set.
/// </para>
/// <para><strong>RCU semantics (Invariant VPC.B.5):</strong>
/// User Path threads read a stable snapshot via <c>Volatile.Read</c>. New snapshots are published
/// atomically via <c>Volatile.Write</c> during normalization.</para>
/// <para><strong>Threading:</strong>
/// <see cref="ISegmentStorage{TRange,TData}.FindIntersecting"/> is called on the User Path (concurrent reads safe).
/// All other methods are Background-Path-only (single writer).</para>
/// <para>Alignment: Invariants VPC.A.10, VPC.B.5, VPC.C.2, VPC.C.3, S.H.4.</para>
/// </remarks>
internal sealed class SnapshotAppendBufferStorage<TRange, TData> : SegmentStorageBase<TRange, TData>
    where TRange : IComparable<TRange>
{
    private readonly int _appendBufferSize;

    // Sorted snapshot — published atomically via Volatile.Write on normalization.
    // User Path reads via Volatile.Read.
    private CachedSegment<TRange, TData>[] _snapshot = [];

    // Small fixed-size append buffer for recently-added segments (Background Path only).
    // Size is determined by the appendBufferSize constructor parameter.
    private readonly CachedSegment<TRange, TData>[] _appendBuffer;
    private int _appendCount;

    /// <summary>
    /// Initializes a new <see cref="SnapshotAppendBufferStorage{TRange,TData}"/> with the
    /// specified append buffer size.
    /// </summary>
    /// <param name="appendBufferSize">
    /// Number of segments the append buffer can hold before normalization is triggered.
    /// Must be &gt;= 1. Default: 8.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="appendBufferSize"/> is less than 1.
    /// </exception>
    internal SnapshotAppendBufferStorage(int appendBufferSize = 8)
    {
        if (appendBufferSize < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(appendBufferSize),
                "AppendBufferSize must be greater than or equal to 1.");
        }

        _appendBufferSize = appendBufferSize;
        _appendBuffer = new CachedSegment<TRange, TData>[appendBufferSize];
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para><strong>Algorithm (O(log n + k)):</strong></para>
    /// <list type="number">
    /// <item><description>Acquire stable snapshot via <c>Volatile.Read</c></description></item>
    /// <item><description>Binary-search snapshot for the rightmost entry whose <c>Start &lt;= range.Start</c>
    ///   via <see cref="SegmentStorageBase{TRange,TData}.FindLastAtOrBefore{TElement,TAccessor}"/> (Start.Value-based,
    ///   shared with <see cref="LinkedListStrideIndexStorage{TRange,TData}"/>). No step-back needed:
    ///   Invariant VPC.C.3 guarantees <c>End[i] &lt; Start[i+1]</c>, so all earlier segments have
    ///   <c>End &lt; range.Start</c> and cannot intersect.</description></item>
    /// <item><description>Linear scan forward collecting intersecting non-removed segments;
    ///   short-circuit when <c>segment.Start &gt; range.End</c></description></item>
    /// <item><description>Linear scan of append buffer (unsorted, small)</description></item>
    /// </list>
    /// <para><strong>Allocation:</strong> The result list is lazily allocated — Full-Miss returns
    /// the static empty array singleton with zero heap allocation.</para>
    /// </remarks>
    public override IReadOnlyList<CachedSegment<TRange, TData>> FindIntersecting(Range<TRange> range)
    {
        var snapshot = Volatile.Read(ref _snapshot);

        // Lazy-init: only allocate the results list on the first actual match.
        // Full-Miss path (no intersecting segments) returns the static empty array — zero allocation.
        List<CachedSegment<TRange, TData>>? results = null;

        // Binary search: find the rightmost snapshot entry whose Start <= range.Start.
        // That entry is itself the earliest possible intersector: because segments are
        // non-overlapping and sorted by Start (Invariant VPC.C.3), every earlier segment
        // has End < Start[hi] <= range.Start and therefore cannot intersect.
        // No step-back needed — unlike the stride strategy, every element is directly indexed.
        var hi = snapshot.Length > 0
            ? FindLastAtOrBefore(snapshot, range.Start.Value, default(DirectAccessor))
            : -1;

        // Start scanning from hi (the rightmost segment whose Start <= range.Start).
        // If hi == -1 all segments start after range.Start; begin from 0 in case some
        // still have Start <= range.End (i.e. the query range starts before all segments).
        var scanStart = Math.Max(0, hi);

        // Linear scan from scanStart forward
        for (var i = scanStart; i < snapshot.Length; i++)
        {
            var seg = snapshot[i];
            // Short-circuit: if segment starts after range ends, no more candidates
            if (seg.Range.Start.Value.CompareTo(range.End.Value) > 0)
            {
                break;
            }

            // Use IsRemoved flag as the primary soft-delete filter (no shared collection needed).
            if (!seg.IsRemoved && seg.Range.Overlaps(range))
            {
                (results ??= []).Add(seg);
            }
        }

        // Scan append buffer (unsorted, small)
        var appendCount = _appendCount; // safe: Background Path writes this; User Path reads it
        for (var i = 0; i < appendCount; i++)
        {
            var seg = _appendBuffer[i];
            if (!seg.IsRemoved && seg.Range.Overlaps(range))
            {
                (results ??= []).Add(seg);
            }
        }

        return (IReadOnlyList<CachedSegment<TRange, TData>>?)results ?? [];
    }

    /// <inheritdoc/>
    public override void Add(CachedSegment<TRange, TData> segment)
    {
        _appendBuffer[_appendCount] = segment;
        _appendCount++;
        IncrementCount();

        if (_appendCount == _appendBufferSize)
        {
            Normalize();
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para><strong>Algorithm (O(1) per attempt, bounded retries):</strong></para>
    /// <list type="number">
    /// <item><description>Compute the live pool size: <c>snapshot.Length + _appendCount</c>.</description></item>
    /// <item><description>Pick a random index in that range. Indices in <c>[0, snapshot.Length)</c>
    ///   map to snapshot entries; indices in <c>[snapshot.Length, pool)</c> map to append buffer entries.</description></item>
    /// <item><description>If the selected segment is soft-deleted, retry (bounded by <c>RandomRetryLimit</c>).</description></item>
    /// </list>
    /// </remarks>
    public override CachedSegment<TRange, TData>? TryGetRandomSegment()
    {
        var snapshot = Volatile.Read(ref _snapshot);
        var pool = snapshot.Length + _appendCount;

        if (pool == 0)
        {
            return null;
        }

        for (var attempt = 0; attempt < RandomRetryLimit; attempt++)
        {
            var index = Random.Next(pool);
            CachedSegment<TRange, TData> seg;

            if (index < snapshot.Length)
            {
                seg = snapshot[index];
            }
            else
            {
                seg = _appendBuffer[index - snapshot.Length];
            }

            if (!seg.IsRemoved)
            {
                return seg;
            }
        }

        return null;
    }

    /// <summary>
    /// Rebuilds the sorted snapshot by merging the current snapshot (excluding removed
    /// entries) with all live append buffer entries, then atomically publishes the new snapshot.
    /// </summary>
    /// <remarks>
    /// <para><strong>Algorithm:</strong> O(n + m) merge of two sorted sequences (snapshot sorted,
    /// append buffer sorted in-place on the private backing array).</para>
    /// <para>Resets <c>_appendCount</c> to 0 and publishes via <c>Volatile.Write</c> so User
    /// Path threads atomically see the new snapshot. Removed segments (whose
    /// <see cref="CachedSegment{TRange,TData}.IsRemoved"/> flag is set) are excluded from the
    /// new snapshot and are physically dropped from memory.</para>
    /// <para><strong>Allocation:</strong> No intermediate <c>List&lt;T&gt;</c> allocations.
    /// The append buffer is sorted in-place (Background Path owns it exclusively).
    /// The only allocation is the new merged snapshot array (unavoidable — published to User Path).</para>
    /// </remarks>
    private void Normalize()
    {
        var snapshot = Volatile.Read(ref _snapshot);

        // Count live snapshot entries (skip removed segments) without allocating a List.
        var liveSnapshotCount = 0;
        for (var i = 0; i < snapshot.Length; i++)
        {
            var seg = snapshot[i];
            if (!seg.IsRemoved)
            {
                liveSnapshotCount++;
            }
        }

        // Sort the append buffer in-place (Background Path owns _appendBuffer exclusively).
        // MemoryExtensions.Sort operates on a Span — zero allocation.
        _appendBuffer.AsSpan(0, _appendCount).Sort(
            static (a, b) => a.Range.Start.Value.CompareTo(b.Range.Start.Value));

        // Count live append buffer entries after sorting.
        var liveAppendCount = 0;
        for (var i = 0; i < _appendCount; i++)
        {
            if (!_appendBuffer[i].IsRemoved)
            {
                liveAppendCount++;
            }
        }

        // Merge two sorted sequences directly into the output array — one allocation.
        var merged = MergeSorted(snapshot, liveSnapshotCount, _appendBuffer, _appendCount, liveAppendCount);

        // Reset append buffer
        _appendCount = 0;
        // Clear stale references in append buffer
        Array.Clear(_appendBuffer, 0, _appendBufferSize);

        // Atomically publish the new snapshot (release fence — User Path reads with acquire fence)
        Volatile.Write(ref _snapshot, merged);
    }

    private static CachedSegment<TRange, TData>[] MergeSorted(
        CachedSegment<TRange, TData>[] left,
        int liveLeftCount,
        CachedSegment<TRange, TData>[] right,
        int rightLength,
        int liveRightCount)
    {
        var result = new CachedSegment<TRange, TData>[liveLeftCount + liveRightCount];
        int i = 0, j = 0, k = 0;

        // Advance i to the next live left entry.
        while (i < left.Length && left[i].IsRemoved)
        {
            i++;
        }

        // Advance j to the next live right entry.
        while (j < rightLength && right[j].IsRemoved)
        {
            j++;
        }

        while (i < left.Length && j < rightLength)
        {
            var cmp = left[i].Range.Start.Value.CompareTo(right[j].Range.Start.Value);
            if (cmp <= 0)
            {
                result[k++] = left[i++];
                while (i < left.Length && left[i].IsRemoved)
                {
                    i++;
                }
            }
            else
            {
                result[k++] = right[j++];
                while (j < rightLength && right[j].IsRemoved)
                {
                    j++;
                }
            }
        }

        while (i < left.Length)
        {
            if (!left[i].IsRemoved)
            {
                result[k++] = left[i];
            }

            i++;
        }

        while (j < rightLength)
        {
            if (!right[j].IsRemoved)
            {
                result[k++] = right[j];
            }

            j++;
        }

        return result;
    }

    /// <summary>
    /// Zero-allocation accessor that extracts <c>Range.Start.Value</c> from a
    /// <see cref="CachedSegment{TRange,TData}"/> element for use with
    /// <see cref="SegmentStorageBase{TRange,TData}.FindLastAtOrBefore{TElement,TAccessor}"/>.
    /// </summary>
    private readonly struct DirectAccessor : ISegmentAccessor<CachedSegment<TRange, TData>>
    {
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public TRange GetStartValue(CachedSegment<TRange, TData> element) =>
            element.Range.Start.Value;
    }
}
