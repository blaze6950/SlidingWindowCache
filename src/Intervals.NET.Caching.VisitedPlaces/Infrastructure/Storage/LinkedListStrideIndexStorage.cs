using Intervals.NET.Extensions;
using Intervals.NET.Caching.VisitedPlaces.Core;

namespace Intervals.NET.Caching.VisitedPlaces.Infrastructure.Storage;

/// <summary>
/// Segment storage backed by a sorted doubly-linked list with a volatile stride index for
/// accelerated range lookup. Optimised for larger caches (&gt;85 KB total data, &gt;50 segments)
/// where LOH pressure from large snapshot arrays must be avoided.
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <remarks>
/// <para><strong>Data Structure:</strong></para>
/// <list type="bullet">
/// <item><description><c>_list</c> — doubly-linked list sorted by segment range start; mutated on Background Path only</description></item>
/// <item><description><c>_strideIndex</c> — array of every Nth node ("stride anchors"); published via <c>Volatile.Write</c></description></item>
/// <item><description><c>_strideAppendBuffer</c> — fixed-size buffer collecting newly-added segments before stride normalization</description></item>
/// <item><description><c>_softDeleted</c> — set of logically-removed segments; physically unlinked during normalization</description></item>
/// </list>
/// <para><strong>RCU semantics (Invariant VPC.B.5):</strong>
/// User Path threads read a stable stride index via <c>Volatile.Read</c>. New stride index arrays
/// are published atomically via <c>Volatile.Write</c> during normalization.</para>
/// <para><strong>Threading:</strong>
/// <see cref="ISegmentStorage{TRange,TData}.FindIntersecting"/> is called on the User Path (concurrent reads safe).
/// All other methods are Background-Path-only (single writer).</para>
/// <para>Alignment: Invariants VPC.A.10, VPC.B.5, VPC.C.2, VPC.C.3, S.H.4.</para>
/// </remarks>
internal sealed class LinkedListStrideIndexStorage<TRange, TData> : ISegmentStorage<TRange, TData>
    where TRange : IComparable<TRange>
{
    private const int DefaultStride = 16;
    private const int StrideAppendBufferSize = 8;

    private readonly int _stride;

    // Sorted linked list — mutated on Background Path only.
    private readonly LinkedList<CachedSegment<TRange, TData>> _list = [];

    // Stride index: every Nth node in the sorted list as a navigation anchor.
    // Published atomically via Volatile.Write; read via Volatile.Read on the User Path.
    private CachedSegment<TRange, TData>[] _strideIndex = [];

    // Maps each segment to its linked list node for O(1) removal.
    // Maintained on Background Path only.
    private readonly Dictionary<CachedSegment<TRange, TData>, LinkedListNode<CachedSegment<TRange, TData>>>
        _nodeMap = new(ReferenceEqualityComparer.Instance);

    // Stride append buffer: newly-added segments not yet reflected in the stride index.
    private readonly CachedSegment<TRange, TData>[] _strideAppendBuffer =
        new CachedSegment<TRange, TData>[StrideAppendBufferSize];
    private int _strideAppendCount;

    // Soft-delete set: segments logically removed but not yet physically unlinked from _list.
    private readonly HashSet<CachedSegment<TRange, TData>> _softDeleted =
        new(ReferenceEqualityComparer.Instance);

    // Total count of live (non-deleted) segments.
    private int _count;

    /// <summary>
    /// Initializes a new <see cref="LinkedListStrideIndexStorage{TRange,TData}"/> with an
    /// optional stride value.
    /// </summary>
    /// <param name="stride">
    /// Distance between stride anchors (default 16). Must be &gt;= 1.
    /// </param>
    public LinkedListStrideIndexStorage(int stride = DefaultStride)
    {
        if (stride < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(stride),
                "Stride must be greater than or equal to 1.");
        }

        _stride = stride;
    }

    /// <inheritdoc/>
    public int Count => _count;

    /// <inheritdoc/>
    /// <remarks>
    /// <para><strong>Algorithm (O(log(n/N) + k + N + m)):</strong></para>
    /// <list type="number">
    /// <item><description>Acquire stable stride index via <c>Volatile.Read</c></description></item>
    /// <item><description>Binary-search stride index for the anchor just before <paramref name="range"/>.Start</description></item>
    /// <item><description>Walk the list forward from the anchor, collecting intersecting non-soft-deleted segments</description></item>
    /// <item><description>Linear-scan the stride append buffer for intersecting non-soft-deleted segments</description></item>
    /// </list>
    /// </remarks>
    public IReadOnlyList<CachedSegment<TRange, TData>> FindIntersecting(Range<TRange> range)
    {
        var strideIndex = Volatile.Read(ref _strideIndex);
        var softDeleted = _softDeleted; // Background Path only modifies; User Path only reads

        var results = new List<CachedSegment<TRange, TData>>();

        // Binary search stride index: find the last anchor whose Start <= range.End
        // (the anchor just before or at the query range).
        // We want the rightmost anchor whose Start.Value <= range.End.Value.
        LinkedListNode<CachedSegment<TRange, TData>>? startNode = null;

        if (strideIndex.Length > 0)
        {
            var lo = 0;
            var hi = strideIndex.Length - 1;

            // Find the rightmost anchor where Start.Value <= range.End.Value.
            // Because the stride index is sorted ascending by Start.Value, we binary-search for
            // the largest index where anchor.Start.Value <= range.End.Value.
            while (lo <= hi)
            {
                var mid = lo + (hi - lo) / 2;
                if (strideIndex[mid].Range.Start.Value.CompareTo(range.End.Value) <= 0)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            // hi is now the rightmost anchor with Start <= range.End.
            // Step back one more to ensure we start at or just before range.Start
            // (the anchor may cover part of range).
            var anchorIdx = hi > 0 ? hi - 1 : 0;
            if (hi >= 0)
            {
                // Look up the anchor segment in the node map to get the linked-list node.
                var anchorSeg = strideIndex[anchorIdx];
                if (_nodeMap.TryGetValue(anchorSeg, out var anchorNode))
                {
                    startNode = anchorNode;
                }
            }
        }

        // Walk linked list from the start node (or from head if no anchor found).
        var node = startNode ?? _list.First;

        while (node != null)
        {
            var seg = node.Value;

            // Short-circuit: if segment starts after range ends, no more candidates.
            if (seg.Range.Start.Value.CompareTo(range.End.Value) > 0)
            {
                break;
            }

            if (!softDeleted.Contains(seg) && seg.Range.Overlaps(range))
            {
                results.Add(seg);
            }

            node = node.Next;
        }

        // NOTE: The stride append buffer does NOT need to be scanned separately.
        // All segments added via Add() are inserted into _list immediately (InsertSorted).
        // The stride append buffer only tracks which list entries haven't been reflected
        // in the stride index yet — they are already covered by the list walk above.

        return results;
    }

    /// <inheritdoc/>
    public void Add(CachedSegment<TRange, TData> segment)
    {
        // Insert into sorted position in the linked list.
        InsertSorted(segment);

        // Write to stride append buffer.
        _strideAppendBuffer[_strideAppendCount] = segment;
        _strideAppendCount++;
        _count++;

        if (_strideAppendCount == StrideAppendBufferSize)
        {
            NormalizeStrideIndex();
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
        var results = new List<CachedSegment<TRange, TData>>(_count);

        var node = _list.First;
        while (node != null)
        {
            if (!_softDeleted.Contains(node.Value))
            {
                results.Add(node.Value);
            }
            node = node.Next;
        }

        // Also include segments currently in the stride append buffer that are not in the list yet.
        // Note: InsertSorted already adds to _list, so all segments are in _list. The stride
        // append buffer just tracks which are not yet reflected in the stride index.
        // GetAllSegments returns live list segments (already done above).

        return results;
    }

    /// <summary>
    /// Inserts a segment into the linked list in sorted order by range start value.
    /// Also registers the node in <see cref="_nodeMap"/> for O(1) lookup.
    /// </summary>
    private void InsertSorted(CachedSegment<TRange, TData> segment)
    {
        if (_list.Count == 0)
        {
            var node = _list.AddFirst(segment);
            _nodeMap[segment] = node;
            return;
        }

        // Use stride index to find a close insertion point (O(log(n/N)) search + O(N) walk).
        var strideIndex = Volatile.Read(ref _strideIndex);
        LinkedListNode<CachedSegment<TRange, TData>>? insertAfter = null;

        if (strideIndex.Length > 0)
        {
            // Binary search: find last anchor with Start.Value <= segment.Range.Start.Value.
            var lo = 0;
            var hi = strideIndex.Length - 1;
            while (lo <= hi)
            {
                var mid = lo + (hi - lo) / 2;
                if (strideIndex[mid].Range.Start.Value.CompareTo(segment.Range.Start.Value) <= 0)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            if (hi >= 0 && _nodeMap.TryGetValue(strideIndex[hi], out var anchorNode))
            {
                insertAfter = anchorNode;
            }
        }

        // Walk forward from anchor (or from head) to find insertion position.
        var current = insertAfter ?? _list.First;

        // If insertAfter is set, we start walking from that node.
        // Walk until we find the first node with Start > segment.Range.Start.
        if (insertAfter != null)
        {
            // Walk forward while next node starts before or at our value.
            while (current!.Next != null &&
                   current.Next.Value.Range.Start.Value.CompareTo(segment.Range.Start.Value) <= 0)
            {
                current = current.Next;
            }

            // Now insert after current.
            var newNode = _list.AddAfter(current, segment);
            _nodeMap[segment] = newNode;
        }
        else
        {
            // No anchor, walk from head.
            if (current != null &&
                current.Value.Range.Start.Value.CompareTo(segment.Range.Start.Value) > 0)
            {
                // Insert before the first node.
                var newNode = _list.AddBefore(current, segment);
                _nodeMap[segment] = newNode;
            }
            else
            {
                // Walk forward to find insertion position.
                while (current!.Next != null &&
                       current.Next.Value.Range.Start.Value.CompareTo(segment.Range.Start.Value) <= 0)
                {
                    current = current.Next;
                }

                var newNode = _list.AddAfter(current, segment);
                _nodeMap[segment] = newNode;
            }
        }
    }

    /// <summary>
    /// Rebuilds the stride index by walking the live linked list, collecting every Nth node
    /// as a stride anchor, physically removing soft-deleted nodes, and atomically publishing
    /// the new stride index via <c>Volatile.Write</c>.
    /// </summary>
    /// <remarks>
    /// <para><strong>Algorithm:</strong> O(n) list traversal + O(n/N) stride array allocation.</para>
    /// <para>Clears <c>_softDeleted</c>, resets <c>_strideAppendCount</c> to 0, physically unlinks
    /// soft-deleted nodes, and publishes the new stride index atomically.</para>
    /// </remarks>
    private void NormalizeStrideIndex()
    {
        // First pass: physically unlink soft-deleted nodes and compute live count.
        foreach (var seg in _softDeleted)
        {
            if (_nodeMap.TryGetValue(seg, out var node))
            {
                _list.Remove(node);
                _nodeMap.Remove(seg);
            }
        }

        _softDeleted.Clear();

        // Second pass: walk live list and collect every Nth node as a stride anchor.
        var liveCount = _list.Count;
        var anchorCount = liveCount == 0 ? 0 : (liveCount + _stride - 1) / _stride;
        var newStrideIndex = new CachedSegment<TRange, TData>[anchorCount];

        var current = _list.First;
        var nodeIdx = 0;
        var anchorIdx = 0;

        while (current != null)
        {
            if (nodeIdx % _stride == 0 && anchorIdx < anchorCount)
            {
                newStrideIndex[anchorIdx++] = current.Value;
            }

            current = current.Next;
            nodeIdx++;
        }

        // Reset stride append buffer.
        Array.Clear(_strideAppendBuffer, 0, StrideAppendBufferSize);
        _strideAppendCount = 0;

        // Atomically publish new stride index (release fence — User Path reads with acquire fence).
        Volatile.Write(ref _strideIndex, newStrideIndex);
    }
}
