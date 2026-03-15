using System.Buffers;
using Intervals.NET.Extensions;
using Intervals.NET.Caching.VisitedPlaces.Core;

namespace Intervals.NET.Caching.VisitedPlaces.Infrastructure.Storage;

/// <summary>
/// Segment storage backed by a sorted doubly-linked list with a volatile stride index.
/// Optimised for larger caches (&gt;85 KB total data, &gt;50 segments).
/// See docs/visited-places/ for design details.
/// </summary>
internal sealed class LinkedListStrideIndexStorage<TRange, TData> : SegmentStorageBase<TRange, TData>
    where TRange : IComparable<TRange>
{
    private const int DefaultStride = 16;
    private const int DefaultAppendBufferSize = 8;

    private readonly int _stride;
    private readonly int _appendBufferSize;

    // Sorted linked list — mutated on Background Path only.
    private readonly LinkedList<CachedSegment<TRange, TData>> _list = [];

    // Synchronizes the linked-list walk (User Path) against node unlinking (Background Path).
    // The stride index binary search is lock-free; only the linked-list portion requires this lock.
    private readonly object _listSyncRoot = new();

    // Stride index: every Nth LinkedListNode in the sorted list as a navigation anchor.
    // Stores nodes directly — no separate segment-to-node map needed.
    // Published atomically via Volatile.Write; read via Volatile.Read on the User Path.
    private LinkedListNode<CachedSegment<TRange, TData>>[] _strideIndex = [];

    // Counter of segments added since the last stride normalization.
    // Normalization is triggered when this reaches _appendBufferSize.
    private int _addsSinceLastNormalization;

    /// <summary>
    /// Initializes a new <see cref="LinkedListStrideIndexStorage{TRange,TData}"/> with optional
    /// append buffer size and stride values.
    /// </summary>
    public LinkedListStrideIndexStorage(int appendBufferSize = DefaultAppendBufferSize, int stride = DefaultStride)
    {
        if (appendBufferSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(appendBufferSize),
                "AppendBufferSize must be greater than or equal to 1.");
        }

        if (stride < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(stride),
                "Stride must be greater than or equal to 1.");
        }

        _appendBufferSize = appendBufferSize;
        _stride = stride;
    }

    /// <inheritdoc/>
    public override IReadOnlyList<CachedSegment<TRange, TData>> FindIntersecting(Range<TRange> range)
    {
        var strideIndex = Volatile.Read(ref _strideIndex);

        // Lazy-init: only allocate the results list on the first actual match.
        // Full-Miss path (no intersecting segments) returns the static empty array — zero allocation.
        List<CachedSegment<TRange, TData>>? results = null;

        // Binary search: find the rightmost anchor whose Start <= range.Start.
        // No step-back needed: VPC.C.3 guarantees End[i] < Start[i+1] (strict inequality),
        // so all segments before anchor[hi] have End < anchor[hi].Start <= range.Start
        // and therefore cannot intersect the query range.
        // Uses Start.Value-based search (shared with SnapshotAppendBufferStorage via base class).
        LinkedListNode<CachedSegment<TRange, TData>>? startNode = null;

        if (strideIndex.Length > 0)
        {
            var hi = FindLastAtOrBefore(strideIndex, range.Start.Value, default(LinkedListNodeAccessor));

            var anchorIdx = Math.Max(0, hi);
            if (hi >= 0)
            {
                var anchorNode = strideIndex[anchorIdx];
                // Guard: node may have been physically unlinked since the old stride index was read.
                if (anchorNode.List != null)
                {
                    startNode = anchorNode;
                }
            }
        }

        // Walk linked list from the start node (or from head if no usable anchor found).
        // Held for the entire walk so that each per-node lock in NormalizeStrideIndex must wait
        // for this read to release before it can advance past any node — giving the User Path
        // priority over the Background Path's unlinking loop (C4, C5).
        lock (_listSyncRoot)
        {
            // Re-validate the anchor inside the lock (VPC.D.7 TOCTOU guard).
            // The outer anchorNode.List != null check (above) is a lock-free fast-path hint;
            // NormalizeStrideIndex Pass 2 can unlink the anchor between that check and here.
            // If the anchor was unlinked between the outer check and the lock acquisition,
            // node.Next is null after Remove(), so the walk would terminate immediately and
            // miss all segments — a false cache miss. Re-checking inside the lock eliminates
            // the race: if stale, fall back to _list.First for a full walk.
            if (startNode?.List == null)
            {
                startNode = null;
            }

            var node = startNode ?? _list.First;

            while (node != null)
            {
                var seg = node.Value;

                // Short-circuit: if segment starts after range ends, no more candidates.
                if (seg.Range.Start.Value.CompareTo(range.End.Value) > 0)
                {
                    break;
                }

                // Use IsRemoved flag as the primary soft-delete filter (no shared collection needed).
                if (!seg.IsRemoved && seg.Range.Overlaps(range))
                {
                    (results ??= []).Add(seg);
                }

                node = node.Next;
            }
        }

        // NOTE: All segments added via Add() are inserted into _list immediately (InsertSorted).
        // _addsSinceLastNormalization only tracks the normalization trigger — all live segments
        // are already in _list and covered by the walk above.

        return (IReadOnlyList<CachedSegment<TRange, TData>>?)results ?? [];
    }

    /// <inheritdoc/>
    public override void Add(CachedSegment<TRange, TData> segment)
    {
        // Insert into sorted position in the linked list.
        InsertSorted(segment);

        _addsSinceLastNormalization++;
        IncrementCount();

        if (_addsSinceLastNormalization == _appendBufferSize)
        {
            NormalizeStrideIndex();
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Inserts each segment via <see cref="InsertSorted"/> (O(log(n/N) + N) each), then runs a
    /// single <see cref="NormalizeStrideIndex"/> pass after all insertions.  Compared to calling
    /// <see cref="Add"/> in a loop, this defers stride-index rebuilds until all segments are in
    /// the list — reducing normalization passes from O(count/appendBufferSize) down to one.
    /// </remarks>
    public override void AddRange(CachedSegment<TRange, TData>[] segments)
    {
        if (segments.Length == 0)
        {
            return;
        }

        // Sort incoming segments so each InsertSorted call starts from a reasonably close anchor.
        segments.AsSpan().Sort(static (a, b) => a.Range.Start.Value.CompareTo(b.Range.Start.Value));

        foreach (var segment in segments)
        {
            InsertSorted(segment);
        }

        IncrementCount(segments.Length);

        // A single normalization after all insertions replaces the O(count/appendBufferSize)
        // normalizations that would occur when calling Add() in a loop. NormalizeStrideIndex also
        // resets _addsSinceLastNormalization = 0 in its finally block, so the next Add() call
        // starts a fresh normalization cycle.
        NormalizeStrideIndex();
    }

    /// <inheritdoc/>
    public override CachedSegment<TRange, TData>? TryGetRandomSegment()
    {
        if (_list.Count == 0)
        {
            return null;
        }

        for (var attempt = 0; attempt < RandomRetryLimit; attempt++)
        {
            CachedSegment<TRange, TData>? seg = null;
            var strideIndex = Volatile.Read(ref _strideIndex);

            if (strideIndex.Length > 0)
            {
                // Pick a random stride anchor index, then a random offset from 0 to stride-1
                // (or to list-end for the last anchor, which may have more than _stride nodes
                // when new segments have been appended after the last normalization).
                var anchorIdx = Random.Next(strideIndex.Length);
                var anchorNode = strideIndex[anchorIdx];

                // Guard: node may have been physically unlinked since the old stride index was read.
                if (anchorNode.List != null)
                {
                    // Determine the maximum reachable offset from this anchor.
                    // For interior anchors, offset is bounded by _stride (distance to next anchor).
                    // For the last anchor, we walk to the actual list end (may be > _stride when
                    // new segments have been appended since the last normalization).
                    int maxOffset;
                    if (anchorIdx < strideIndex.Length - 1)
                    {
                        maxOffset = _stride;
                    }
                    else
                    {
                        // Count nodes from this anchor to end of list.
                        maxOffset = 0;
                        var countNode = anchorNode;
                        while (countNode != null)
                        {
                            maxOffset++;
                            countNode = countNode.Next;
                        }
                    }

                    var offset = Random.Next(maxOffset);

                    var node = anchorNode;
                    for (var i = 0; i < offset && node.Next != null; i++)
                    {
                        node = node.Next;
                    }

                    seg = node.Value;
                }
            }
            else
            {
                // Stride index not yet built (all segments added but not yet normalized).
                // Fall back: linear walk with a random skip count.
                var listCount = _list.Count;
                var skip = Random.Next(listCount);
                var node = _list.First;

                for (var i = 0; i < skip && node != null; i++)
                {
                    node = node.Next;
                }

                seg = node?.Value;
            }

            if (seg is { IsRemoved: false })
            {
                return seg;
            }
        }

        return null;
    }

    /// <summary>
    /// Inserts a segment into the linked list in sorted order by range start.
    /// </summary>
    private void InsertSorted(CachedSegment<TRange, TData> segment)
    {
        if (_list.Count == 0)
        {
            _list.AddFirst(segment);
            return;
        }

        // Use stride index to find a close insertion point.
        var strideIndex = Volatile.Read(ref _strideIndex);
        LinkedListNode<CachedSegment<TRange, TData>>? insertAfter = null;

        if (strideIndex.Length > 0)
        {
            var hi = FindLastAtOrBefore(strideIndex, segment.Range.Start.Value, default(LinkedListNodeAccessor));

            if (hi >= 0)
            {
                var anchorNode = strideIndex[hi];
                // Guard: node may have been physically unlinked.
                if (anchorNode.List != null)
                {
                    insertAfter = anchorNode;
                }
            }
        }

        // Walk forward from anchor (or from head) to find insertion position.
        var current = insertAfter ?? _list.First;

        if (insertAfter != null)
        {
            // Walk forward while next node starts before or at our value.
            while (current!.Next != null &&
                   current.Next.Value.Range.Start.Value.CompareTo(segment.Range.Start.Value) <= 0)
            {
                current = current.Next;
            }

            _list.AddAfter(current, segment);
        }
        else
        {
            // No anchor, walk from head.
            if (current != null &&
                current.Value.Range.Start.Value.CompareTo(segment.Range.Start.Value) > 0)
            {
                // Insert before the first node.
                _list.AddBefore(current, segment);
            }
            else
            {
                // Walk forward to find insertion position.
                while (current!.Next != null &&
                       current.Next.Value.Range.Start.Value.CompareTo(segment.Range.Start.Value) <= 0)
                {
                    current = current.Next;
                }

                _list.AddAfter(current, segment);
            }
        }
    }

    /// <summary>
    /// Rebuilds the stride index from the live linked list and physically unlinks removed nodes.
    /// </summary>
    private void NormalizeStrideIndex()
    {
        // Upper bound on anchor count: ceil(liveCount / stride) ≤ ceil(listCount / stride).
        // Add 1 for safety against off-by-one when listCount is not a multiple of stride.
        var maxAnchors = (_list.Count / _stride) + 1;

        // Rent a buffer large enough to hold all possible anchors.
        // Returned immediately after we've copied into the right-sized published array.
        var anchorPool = ArrayPool<LinkedListNode<CachedSegment<TRange, TData>>>.Shared;
        var anchorBuffer = anchorPool.Rent(maxAnchors);
        var anchorCount = 0;

        try
        {
            // First pass: walk the full list (including removed nodes), collecting every Nth LIVE
            // node as a stride anchor. Removed nodes are skipped for anchor selection but are NOT
            // physically unlinked yet — their Next pointers must remain valid for any concurrent
            // User Path walk still using the old stride index.
            var liveNodeIdx = 0;

            var current = _list.First;
            while (current != null)
            {
                if (!current.Value.IsRemoved)
                {
                    if (liveNodeIdx % _stride == 0)
                    {
                        anchorBuffer[anchorCount++] = current;
                    }

                    liveNodeIdx++;
                }

                current = current.Next;
            }

            // Allocate the exact-sized published stride index and copy anchors into it.
            var newStrideIndex = new LinkedListNode<CachedSegment<TRange, TData>>[anchorCount];
            Array.Copy(anchorBuffer, newStrideIndex, anchorCount);

            // Atomically publish the new stride index (release fence).
            // From this point on, the User Path will use anchors that only reference live nodes.
            Interlocked.Exchange(ref _strideIndex, newStrideIndex);
        }
        finally
        {
            // Clear stale node references so they can be GC'd.
            anchorPool.Return(anchorBuffer, clearArray: true);
        }

        // Second pass: physically unlink removed nodes — per-node lock granularity.
        // For each node we briefly acquire _listSyncRoot to (a) read node.Next safely before
        // Remove() can null it out, and (b) call Remove() itself.
        // The User Path holds _listSyncRoot for its entire linked-list walk, so it will
        // block individual removal steps rather than the entire unlinking pass.
        // This lets reads and removals interleave at node granularity: a removal step waits
        // only for the current read to release the lock, executes one Remove(), then yields
        // the lock so the reader can continue to the next node.
        try
        {
            var node = _list.First;
            while (node != null)
            {
                LinkedListNode<CachedSegment<TRange, TData>>? next;
                lock (_listSyncRoot)
                {
                    next = node.Next;
                    if (node.Value.IsRemoved)
                    {
                        _list.Remove(node);
                    }
                }

                node = next;
            }
        }
        finally
        {
            // Reset the add counter — always runs, even if unlink loop throws.
            _addsSinceLastNormalization = 0;
        }
    }

    /// <summary>
    /// Zero-allocation accessor for extracting <c>Range.Start.Value</c> from a linked list node.
    /// </summary>
    private readonly struct LinkedListNodeAccessor
        : ISegmentAccessor<LinkedListNode<CachedSegment<TRange, TData>>>
    {
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public TRange GetStartValue(LinkedListNode<CachedSegment<TRange, TData>> element) =>
            element.Value.Range.Start.Value;
    }
}