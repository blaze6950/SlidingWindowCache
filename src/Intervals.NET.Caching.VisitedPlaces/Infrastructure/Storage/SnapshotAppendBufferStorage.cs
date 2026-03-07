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
/// <item><description><c>_snapshot</c> — sorted array of segments; read via <c>Volatile.Read</c> (User Path)</description></item>
/// <item><description><c>_appendBuffer</c> — fixed-size buffer for recently-added segments</description></item>
/// <item><description><c>_softDeleted</c> — set of segments logically removed but not yet physically purged</description></item>
/// </list>
/// <para><strong>RCU semantics (Invariant VPC.B.5):</strong>
/// User Path threads read a stable snapshot via <c>Volatile.Read</c>. New snapshots are published
/// atomically via <c>Volatile.Write</c> during normalization.</para>
/// <para><strong>Threading:</strong>
/// <see cref="ISegmentStorage{TRange,TData}.FindIntersecting"/> is called on the User Path (concurrent reads safe).
/// All other methods are Background-Path-only (single writer).</para>
/// <para>Alignment: Invariants VPC.A.10, VPC.B.5, VPC.C.2, VPC.C.3, S.H.4.</para>
/// </remarks>
internal sealed class SnapshotAppendBufferStorage<TRange, TData> : ISegmentStorage<TRange, TData>
    where TRange : IComparable<TRange>
{
    private const int AppendBufferSize = 8;

    // Sorted snapshot — published atomically via Volatile.Write on normalization.
    // User Path reads via Volatile.Read.
    private CachedSegment<TRange, TData>[] _snapshot = [];

    // Small fixed-size append buffer for recently-added segments (Background Path only).
    private readonly CachedSegment<TRange, TData>[] _appendBuffer = new CachedSegment<TRange, TData>[AppendBufferSize];
    private int _appendCount;

    // Soft-delete set: segments logically removed but not yet physically purged.
    // Maintained on Background Path only; filtered out during User Path reads via snapshot.
    // The snapshot itself never contains soft-deleted entries after normalization.
    // Between normalizations, soft-deleted snapshot entries are tracked here.
    private readonly HashSet<CachedSegment<TRange, TData>> _softDeleted = new(ReferenceEqualityComparer.Instance);

    // Total count of live (non-deleted) segments.
    private int _count;

    /// <inheritdoc/>
    public int Count => _count;

    /// <inheritdoc/>
    /// <remarks>
    /// <para><strong>Algorithm (O(log n + k + m)):</strong></para>
    /// <list type="number">
    /// <item><description>Acquire stable snapshot via <c>Volatile.Read</c></description></item>
    /// <item><description>Binary-search snapshot for first entry whose range end &gt;= <paramref name="range"/>.Start</description></item>
    /// <item><description>Linear-scan forward collecting intersecting, non-soft-deleted entries</description></item>
    /// <item><description>Linear-scan append buffer for intersecting, non-soft-deleted entries</description></item>
    /// </list>
    /// </remarks>
    public IReadOnlyList<CachedSegment<TRange, TData>> FindIntersecting(Range<TRange> range)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        var softDeleted = _softDeleted; // Background Path only modifies this; User Path only reads

        var results = new List<CachedSegment<TRange, TData>>();

        // Binary search: find first candidate in snapshot
        var lo = 0;
        var hi = snapshot.Length - 1;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            // A segment intersects range if segment.Range.End.Value >= range.Start.Value
            // We want the first segment where End.Value >= range.Start.Value
            if (snapshot[mid].Range.End.Value.CompareTo(range.Start.Value) < 0)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        // Linear scan from lo forward
        for (var i = lo; i < snapshot.Length; i++)
        {
            var seg = snapshot[i];
            // Short-circuit: if segment starts after range ends, no more candidates
            if (seg.Range.Start.Value.CompareTo(range.End.Value) > 0)
            {
                break;
            }

            if (!softDeleted.Contains(seg) && seg.Range.Overlaps(range))
            {
                results.Add(seg);
            }
        }

        // Scan append buffer (unsorted, small)
        var appendCount = _appendCount; // safe: Background Path writes this; User Path reads it
        for (var i = 0; i < appendCount; i++)
        {
            var seg = _appendBuffer[i];
            if (!softDeleted.Contains(seg) && seg.Range.Overlaps(range))
            {
                results.Add(seg);
            }
        }

        return results;
    }

    /// <inheritdoc/>
    public void Add(CachedSegment<TRange, TData> segment)
    {
        _appendBuffer[_appendCount] = segment;
        _appendCount++;
        _count++;

        if (_appendCount == AppendBufferSize)
        {
            Normalize();
        }
    }

    /// <inheritdoc/>
    public void Remove(CachedSegment<TRange, TData> segment)
    {
        _softDeleted.Add(segment);
        _count--;
    }

    /// <inheritdoc/>
    public IReadOnlyList<CachedSegment<TRange, TData>> GetAllSegments()
    {
        var snapshot = Volatile.Read(ref _snapshot);
        var results = new List<CachedSegment<TRange, TData>>(snapshot.Length + _appendCount);

        foreach (var seg in snapshot)
        {
            if (!_softDeleted.Contains(seg))
            {
                results.Add(seg);
            }
        }

        for (var i = 0; i < _appendCount; i++)
        {
            var seg = _appendBuffer[i];
            if (!_softDeleted.Contains(seg))
            {
                results.Add(seg);
            }
        }

        return results;
    }

    /// <summary>
    /// Rebuilds the sorted snapshot by merging the current snapshot (excluding soft-deleted
    /// entries) with all append buffer entries, then atomically publishes the new snapshot.
    /// </summary>
    /// <remarks>
    /// <para><strong>Algorithm:</strong> O(n + m) merge of two sorted sequences (snapshot sorted,
    /// append buffer unsorted — sort append buffer entries first).</para>
    /// <para>Clears <c>_softDeleted</c>, resets <c>_appendCount</c> to 0, and publishes via
    /// <c>Volatile.Write</c> so User Path threads atomically see the new snapshot.</para>
    /// </remarks>
    private void Normalize()
    {
        var snapshot = Volatile.Read(ref _snapshot);

        // Collect live snapshot entries
        var liveSnapshot = new List<CachedSegment<TRange, TData>>(snapshot.Length);
        foreach (var seg in snapshot)
        {
            if (!_softDeleted.Contains(seg))
            {
                liveSnapshot.Add(seg);
            }
        }

        // Collect live append buffer entries and sort them
        var appendEntries = new List<CachedSegment<TRange, TData>>(_appendCount);
        for (var i = 0; i < _appendCount; i++)
        {
            var seg = _appendBuffer[i];
            if (!_softDeleted.Contains(seg))
            {
                appendEntries.Add(seg);
            }
        }
        appendEntries.Sort(static (a, b) => a.Range.Start.Value.CompareTo(b.Range.Start.Value));

        // Merge two sorted sequences
        var merged = MergeSorted(liveSnapshot, appendEntries);

        // Reset append buffer and soft-delete set
        _softDeleted.Clear();
        _appendCount = 0;
        // Clear stale references in append buffer
        Array.Clear(_appendBuffer, 0, AppendBufferSize);

        // Atomically publish the new snapshot (release fence — User Path reads with acquire fence)
        Volatile.Write(ref _snapshot, merged);
    }

    private static CachedSegment<TRange, TData>[] MergeSorted(
        List<CachedSegment<TRange, TData>> left,
        List<CachedSegment<TRange, TData>> right)
    {
        var result = new CachedSegment<TRange, TData>[left.Count + right.Count];
        int i = 0, j = 0, k = 0;

        while (i < left.Count && j < right.Count)
        {
            var cmp = left[i].Range.Start.Value.CompareTo(right[j].Range.Start.Value);
            if (cmp <= 0)
            {
                result[k++] = left[i++];
            }
            else
            {
                result[k++] = right[j++];
            }
        }

        while (i < left.Count) result[k++] = left[i++];
        while (j < right.Count) result[k++] = right[j++];

        return result;
    }
}
