using System.Buffers;
using Intervals.NET.Extensions;
using Intervals.NET.Caching.VisitedPlaces.Core;

namespace Intervals.NET.Caching.VisitedPlaces.Infrastructure.Storage;

/// <summary>
/// Segment storage backed by a sorted doubly-linked list with a volatile stride index.
/// Optimised for larger caches (&gt;85 KB total data, &gt;50 segments).
/// See docs/visited-places/ for design details.
/// </summary>
/// <remarks>
/// This class implements only the data-structure mechanics of the linked-list + stride-index
/// pattern. All invariant enforcement (VPC.C.3 overlap check, VPC.T.1 idempotent removal,
/// normalization threshold check, retry/filter for random sampling) is handled by the base
/// class <see cref="SegmentStorageBase{TRange,TData}"/>.
/// </remarks>
internal sealed class LinkedListStrideIndexStorage<TRange, TData> : SegmentStorageBase<TRange, TData>
    where TRange : IComparable<TRange>
{
    private const int DefaultStride = 16;
    private const int DefaultAppendBufferSize = 8;

    private readonly int _stride;
    private readonly int _appendBufferSize;
    private readonly TimeProvider _timeProvider;

    // Sorted linked list — mutated on Background Path only.
    private readonly LinkedList<CachedSegment<TRange, TData>> _list = [];

    // Guards structural pointer mutations (AddFirst/AddAfter/AddBefore/Remove) against
    // concurrent User Path reads of the same Next/Previous pointers inside FindIntersecting.
    //
    // Lock scope rule:
    //   - Background Path: hold the lock ONLY during the _list.Add*/Remove() call itself
    //     (the structural pointer update). Position-finding walks (node.Next reads) are done
    //     outside the lock — safe because InsertSorted and NormalizeStrideIndex run exclusively
    //     on the Background Path, so no concurrent structural mutation can occur during those
    //     reads.
    //   - User Path (FindIntersecting): hold the lock for the ENTIRE linked-list walk, so that
    //     no removal can null out node.Next mid-traversal.
    //
    // All other _list accesses (_list.Count, _list.First, node.Next reads in SampleRandomCore,
    // NormalizeStrideIndex Pass 1, and the position-finding loops in InsertSorted) are Background-
    // Path-only and therefore do not need synchronization — there is only one writer.
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
    /// append buffer size, stride, and time provider values.
    /// </summary>
    public LinkedListStrideIndexStorage(
        int appendBufferSize = DefaultAppendBufferSize,
        int stride = DefaultStride,
        TimeProvider? timeProvider = null)
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
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    // -------------------------------------------------------------------------
    // FindIntersecting (abstract in base; scan is tightly coupled to list + stride structure)
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public override IReadOnlyList<CachedSegment<TRange, TData>> FindIntersecting(Range<TRange> range)
    {
        var strideIndex = Volatile.Read(ref _strideIndex);

        // Pre-compute the current UTC ticks once for all expiry checks in this call.
        var utcNowTicks = _timeProvider.GetUtcNow().UtcTicks;

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

                // Filter out removed and TTL-expired segments (lazy expiration on read).
                if (!seg.IsRemoved && !seg.IsExpired(utcNowTicks) && seg.Range.Overlaps(range))
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

    // -------------------------------------------------------------------------
    // Abstract primitive implementations (data-structure mechanics only)
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    /// <remarks>
    /// Inserts the segment into the linked list in sorted order and increments
    /// <c>_addsSinceLastNormalization</c>.
    /// VPC.C.3 overlap check is handled by <see cref="SegmentStorageBase{TRange,TData}.TryAdd"/>.
    /// </remarks>
    protected override void AddCore(CachedSegment<TRange, TData> segment)
    {
        InsertSorted(segment);
        _addsSinceLastNormalization++;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// Inserts each validated sorted segment into the linked list and increments
    /// <c>_addsSinceLastNormalization</c>. The stride index is NOT rebuilt here.
    /// VPC.C.3 overlap check is handled by <see cref="SegmentStorageBase{TRange,TData}.TryAddRange"/>.
    /// </para>
    /// <para>
    /// ⚠ DO NOT call <see cref="NormalizeStrideIndex"/> inside this method.
    /// <see cref="AddRangeCore"/> is called from <see cref="SegmentStorageBase{TRange,TData}.TryAddRange"/>,
    /// which returns to <c>CacheNormalizationExecutor.StoreBulk</c>. Immediately after,
    /// the executor calls <see cref="TryNormalize"/> — the correct place for normalization
    /// and TTL discovery. Calling <see cref="NormalizeStrideIndex"/> here would:
    /// <list type="bullet">
    ///   <item>Discard TTL-expired segments (the <c>out</c> expired list is inaccessible to the
    ///   executor, so <c>OnSegmentRemoved</c> / <c>TtlSegmentExpired</c> diagnostics never fire).</item>
    ///   <item>Reset <c>_addsSinceLastNormalization</c> to zero, causing the executor's subsequent
    ///   <see cref="TryNormalize"/> call to always skip (threshold never reached), permanently
    ///   preempting the normal normalization cadence.</item>
    /// </list>
    /// The stride index will be slightly stale until <see cref="TryNormalize"/> runs, but all
    /// newly-inserted segments are immediately live in <c>_list</c> and will be found by
    /// <see cref="FindIntersecting"/> regardless of index staleness.
    /// </para>
    /// </remarks>
    protected override void AddRangeCore(CachedSegment<TRange, TData>[] segments)
    {
        foreach (var segment in segments)
        {
            InsertSorted(segment);
            _addsSinceLastNormalization++;
        }

        // !!! Intentionally no NormalizeStrideIndex call here — see XML doc above for the full
        // explanation. The executor's TryNormalize call handles normalization and TTL discovery.
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Picks a random segment from the linked list using the stride index when available,
    /// or falls back to a linear walk when the stride index has not yet been built.
    /// Returns <see langword="null"/> when the list is empty. Dead-segment filtering is handled
    /// by <see cref="SegmentStorageBase{TRange,TData}.TryGetRandomSegment"/>.
    /// </remarks>
    protected override CachedSegment<TRange, TData>? SampleRandomCore()
    {
        if (_list.Count == 0)
        {
            return null;
        }

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

                return node.Value;
            }
        }

        // Stride index not yet built (all segments added but not yet normalized).
        // Fall back: linear walk with a random skip count.
        {
            var listCount = _list.Count;
            var skip = Random.Next(listCount);
            var node = _list.First;

            for (var i = 0; i < skip && node != null; i++)
            {
                node = node.Next;
            }

            return node?.Value;
        }
    }

    /// <inheritdoc/>
    protected override bool ShouldNormalize() => _addsSinceLastNormalization >= _appendBufferSize;

    /// <inheritdoc/>
    /// <remarks>
    /// Rebuilds the stride index from the live linked list, physically unlinks removed nodes,
    /// and discovers TTL-expired segments. Expired segments are marked removed via
    /// <see cref="SegmentStorageBase{TRange,TData}.TryRemove"/> and collected in
    /// <paramref name="expired"/> for the executor to process.
    /// Resets <c>_addsSinceLastNormalization</c> to zero in a <c>finally</c> block.
    /// </remarks>
    protected override void NormalizeCore(
        long utcNowTicks,
        ref List<CachedSegment<TRange, TData>>? expired)
    {
        NormalizeStrideIndex(utcNowTicks, ref expired);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// No-op: <see cref="NormalizeCore"/> delegates to <see cref="NormalizeStrideIndex(long, ref List{CachedSegment{TRange, TData}}?)"/>,
    /// which resets <c>_addsSinceLastNormalization</c> to zero in its own <c>finally</c> block.
    /// The base class calls this after <see cref="NormalizeCore"/> returns; for this strategy
    /// the reset is already done.
    /// </remarks>
    protected override void ResetNormalizationCounter()
    {
        // Reset is performed inside NormalizeStrideIndex's finally block.
        // Nothing to do here.
    }

    /// <inheritdoc/>
    protected override long GetUtcNowTicks() => _timeProvider.GetUtcNow().UtcTicks;

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Inserts a segment into the linked list in sorted order by range start.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Synchronization rule</b> (see also <c>_listSyncRoot</c> field comment):
    /// <c>_listSyncRoot</c> is held only for the structural <c>_list.Add*</c> call — the moment
    /// that rewrites <c>Next</c>/<c>Previous</c> pointers. <c>FindIntersecting</c> on the User
    /// Path holds <c>_listSyncRoot</c> for its entire walk, so those pointer writes must be
    /// atomic with respect to any concurrent read.
    /// </para>
    /// <para>
    /// The position-finding walk (reading <c>node.Next</c> before the lock) does NOT require
    /// synchronization: <c>InsertSorted</c> runs exclusively on the Background Path. No
    /// concurrent <c>InsertSorted</c> or <c>AddRangeCore</c> call exists, so no structural
    /// mutation can race with this walk.
    /// </para>
    /// </remarks>
    private void InsertSorted(CachedSegment<TRange, TData> segment)
    {
        if (_list.Count == 0)
        {
            lock (_listSyncRoot)
            {
                _list.AddFirst(segment);
            }

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
        // This read-only walk does not require the lock — we are the sole writer.
        var current = insertAfter ?? _list.First;

        if (insertAfter != null)
        {
            // Walk forward while next node starts before or at our value.
            while (current!.Next != null &&
                   current.Next.Value.Range.Start.Value.CompareTo(segment.Range.Start.Value) <= 0)
            {
                current = current.Next;
            }

            // Acquire lock only for the structural mutation (pointer update).
            lock (_listSyncRoot)
            {
                _list.AddAfter(current, segment);
            }
        }
        else
        {
            // No anchor, walk from head.
            if (current != null &&
                current.Value.Range.Start.Value.CompareTo(segment.Range.Start.Value) > 0)
            {
                // Insert before the first node.
                lock (_listSyncRoot)
                {
                    _list.AddBefore(current, segment);
                }
            }
            else
            {
                // Walk forward to find insertion position.
                while (current!.Next != null &&
                       current.Next.Value.Range.Start.Value.CompareTo(segment.Range.Start.Value) <= 0)
                {
                    current = current.Next;
                }

                // Acquire lock only for the structural mutation (pointer update).
                lock (_listSyncRoot)
                {
                    _list.AddAfter(current, segment);
                }
            }
        }
    }

    /// <summary>
    /// Rebuilds the stride index from the live linked list, physically unlinks removed nodes,
    /// and discovers TTL-expired segments. Expired segments are returned via
    /// <paramref name="expired"/> so the executor can update policy aggregates.
    /// Resets <c>_addsSinceLastNormalization</c> to zero in a <c>finally</c> block.
    /// </summary>
    private void NormalizeStrideIndex(
        long utcNowTicks,
        ref List<CachedSegment<TRange, TData>>? expired)
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
            // TTL-expired segments are discovered and marked removed here so they are excluded
            // from the new stride index.
            var liveNodeIdx = 0;

            var current = _list.First;
            while (current != null)
            {
                var seg = current.Value;

                if (seg.IsExpired(utcNowTicks) && TryRemove(seg))
                {
                    (expired ??= []).Add(seg);
                }

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
