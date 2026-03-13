using System.Buffers;
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
/// <item><description><c>_strideIndex</c> — array of every Nth <see cref="LinkedListNode{T}"/> ("stride anchors"); published via <c>Volatile.Write</c></description></item>
/// <item><description><c>_addsSinceLastNormalization</c> — counter of segments added since the last stride normalization; triggers normalization when it reaches the append buffer size threshold</description></item>
/// </list>
/// <para><strong>Soft-delete via <see cref="CachedSegment{TRange,TData}.IsRemoved"/>:</strong></para>
/// <para>
/// Rather than maintaining a separate <c>_softDeleted</c> collection, this implementation uses
/// <see cref="CachedSegment{TRange,TData}.IsRemoved"/> as the primary soft-delete filter.
/// The flag is set atomically by <see cref="CachedSegment{TRange,TData}.MarkAsRemoved"/>.
/// Removed nodes are physically unlinked from <c>_list</c> during <see cref="NormalizeStrideIndex"/>,
/// but only AFTER the new stride index is published (to preserve list integrity for any
/// concurrent User Path walk still using the old stride index).
/// All read paths skip segments whose <c>IsRemoved</c> flag is set without needing a shared collection.
/// </para>
/// <para><strong>No <c>_nodeMap</c>:</strong></para>
/// <para>
/// The stride index stores <see cref="LinkedListNode{T}"/> references directly, eliminating the
/// need for a separate segment-to-node dictionary. Callers use <c>anchorNode.List != null</c>
/// to verify the node is still linked before walking from it.
/// </para>
/// <para><strong>RCU semantics (Invariant VPC.B.5):</strong>
/// User Path threads read a stable stride index via <c>Volatile.Read</c>. New stride index arrays
/// are published atomically via <c>Volatile.Write</c> during normalization.</para>
/// <para><strong>Threading:</strong>
/// <see cref="ISegmentStorage{TRange,TData}.FindIntersecting"/> is called on the User Path (concurrent reads safe).
/// All other methods are Background-Path-only (single writer).</para>
/// <para>Alignment: Invariants VPC.A.10, VPC.B.5, VPC.C.2, VPC.C.3, S.H.4.</para>
/// </remarks>
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
    /// <param name="appendBufferSize">
    /// Number of segments added before stride index normalization is triggered.
    /// Must be &gt;= 1. Default: 8.
    /// </param>
    /// <param name="stride">
    /// Distance between stride anchors (default 16). Must be &gt;= 1.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="appendBufferSize"/> or <paramref name="stride"/> is less than 1.
    /// </exception>
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
    /// <remarks>
    /// <para><strong>Algorithm (O(log(n/N) + k + N)):</strong></para>
    /// <list type="number">
    /// <item><description>Acquire stable stride index via <c>Volatile.Read</c></description></item>
    /// <item><description>Binary-search stride index for the rightmost anchor whose <c>Start &lt;= range.Start</c>
    ///   via <see cref="SegmentStorageBase{TRange,TData}.FindLastAtOrBefore{TElement,TAccessor}"/> (Start.Value-based,
    ///   shared with <see cref="SnapshotAppendBufferStorage{TRange,TData}"/>). No step-back needed:
    ///   Invariant VPC.C.3 guarantees <c>End[i] &lt; Start[i+1]</c> (strict), so every segment before
    ///   the anchor has <c>End &lt; anchor.Start &lt;= range.Start</c> and cannot intersect the query.</description></item>
    /// <item><description>Walk the list forward from the anchor node, collecting intersecting non-removed segments</description></item>
    /// </list>
    /// <para><strong>Allocation:</strong> The result list is lazily allocated — Full-Miss returns
    /// the static empty array singleton with zero heap allocation.</para>
    /// </remarks>
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
        // Lock protects against concurrent node unlinking in NormalizeStrideIndex:
        // - Prevents _list.First from being mutated while we read it (C4)
        // - Prevents node.Next from being set to null by Remove() during our walk (C5)
        // The entire walk is under one lock acquisition for efficiency — the Background Path
        // waits for the read to finish rather than racing node-by-node.
        lock (_listSyncRoot)
        {
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
    /// <para><strong>Algorithm:</strong></para>
    /// <list type="number">
    /// <item><description>
    ///   If <c>_strideIndex</c> is non-empty, pick a random anchor index and a random offset
    ///   within the stride gap, then walk forward from the anchor node to the selected node — O(stride).
    /// </description></item>
    /// <item><description>
    ///   If <c>_strideIndex</c> is empty but <c>_list</c> is non-empty (segments were added but
    ///   stride normalization has not yet run), fall back to a linear walk from <c>_list.First</c>
    ///   with a random skip count bounded by <c>_list.Count</c>.
    /// </description></item>
    /// <item><description>
    ///   If the selected segment is soft-deleted, retry (bounded by <c>RandomRetryLimit</c>).
    /// </description></item>
    /// </list>
    /// </remarks>
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
    /// Inserts a segment into the linked list in sorted order by range start value,
    /// using the stride index for an O(log(n/N)) anchor lookup followed by an O(N) walk.
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
    /// Rebuilds the stride index by walking the live linked list, collecting every Nth live
    /// node as a stride anchor, atomically publishing the new stride index via
    /// <c>Volatile.Write</c>, and only then physically unlinking removed nodes from the list.
    /// </summary>
    /// <remarks>
    /// <para><strong>Algorithm:</strong> O(n) list traversal + O(n/N) stride array allocation.</para>
    /// <para>
    /// Resets <c>_addsSinceLastNormalization</c> to 0 and publishes the new stride index atomically.
    /// Removed segments are physically unlinked from <c>_list</c> after the new stride index
    /// is published, reclaiming memory.
    /// </para>
    /// <para><strong>Order matters for thread safety (Invariant VPC.B.5):</strong></para>
    /// <para>
    /// The new stride index is built and published BEFORE dead nodes are physically unlinked.
    /// Dead nodes are then unlinked under <c>_listSyncRoot</c>, which is the same lock held
    /// by the User Path during its entire linked-list walk in <see cref="FindIntersecting"/>.
    /// This guarantees that no User Path walk can observe a node whose <c>Next</c> pointer was
    /// set to <see langword="null"/> by <c>LinkedList.Remove()</c> mid-walk.
    /// </para>
    /// <para><strong>Allocation:</strong> Uses an <see cref="ArrayPool{T}"/> rental as the
    /// anchor accumulation buffer (returned immediately after the right-sized index array is
    /// constructed), eliminating the intermediate <c>List&lt;T&gt;</c> and its <c>ToArray()</c>
    /// copy. The only heap allocation is the published stride index array itself (unavoidable).</para>
    /// </remarks>
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

        // Second pass: physically unlink removed nodes under lock.
        // The User Path holds the same lock during its entire linked-list walk, so this
        // unlinking pass waits until any in-progress read completes, then runs uninterrupted.
        // This eliminates the race where Remove() sets node.Next to null while a User Path
        // thread is walking through that node.
        lock (_listSyncRoot)
        {
            var node = _list.First;
            while (node != null)
            {
                var next = node.Next;
                if (node.Value.IsRemoved)
                {
                    _list.Remove(node);
                }

                node = next;
            }
        }

        // Reset the add counter.
        _addsSinceLastNormalization = 0;
    }

    /// <summary>
    /// Zero-allocation accessor that extracts <c>Range.Start.Value</c> from a
    /// <see cref="LinkedListNode{T}"/> whose value is a <see cref="CachedSegment{TRange,TData}"/>,
    /// for use with <see cref="SegmentStorageBase{TRange,TData}.FindLastAtOrBefore{TElement,TAccessor}"/>.
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
