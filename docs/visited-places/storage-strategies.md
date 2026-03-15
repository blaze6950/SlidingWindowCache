# Storage Strategies — VisitedPlaces Cache

This document describes the two storage strategies available for `VisitedPlacesCache`. These are internal implementation details — the public API and architectural invariants (see `docs/visited-places/invariants.md`) hold regardless of which strategy is selected.

---

## Overview

`VisitedPlacesCache` stores a collection of **non-contiguous, independently-sorted segments**. Two storage strategies are available, selectable at construction time:

1. **Snapshot + Append Buffer** (`SnapshotAppendBufferStorageOptions<TRange, TData>`) — default; optimized for smaller caches (<85KB total data)
2. **LinkedList + Stride Index** (`LinkedListStrideIndexStorageOptions<TRange, TData>`) — for larger caches where segment counts are high and traversal cost dominates

### Selecting a Strategy

Pass a typed options object to `WithStorageStrategy(...)` when building the cache:

```csharp
// Default strategy (Snapshot + Append Buffer, buffer size 8)
var options = new VisitedPlacesCacheOptions<int, MyData>();

// Explicit Snapshot + Append Buffer with custom buffer size
var options = new VisitedPlacesCacheOptions<int, MyData>(
    new SnapshotAppendBufferStorageOptions<int, MyData>(appendBufferSize: 16));

// LinkedList + Stride Index with default tuning
var options = new VisitedPlacesCacheOptions<int, MyData>(
    LinkedListStrideIndexStorageOptions<int, MyData>.Default);

// LinkedList + Stride Index with custom tuning
var options = new VisitedPlacesCacheOptions<int, MyData>(
    new LinkedListStrideIndexStorageOptions<int, MyData>(appendBufferSize: 16, stride: 8));
```

Or inline via the builder:

```csharp
await using var cache = VisitedPlacesCacheBuilder.For(dataSource, domain)
    .WithOptions(o => o.WithStorageStrategy(
        new LinkedListStrideIndexStorageOptions<int, MyData>(appendBufferSize: 8, stride: 16)))
    .WithEviction(policies: [...], selector: new LruEvictionSelector<int, MyData>())
    .Build();
```

Both strategies expose the same internal interface:
- **`FindIntersecting(RequestedRange)`** — returns all segments whose ranges intersect `RequestedRange` (User Path, read-only)
- **`TryAdd(Segment)`** — adds a single new segment if no overlap exists (Background Path, write-only); returns `true` if stored, `false` if skipped due to VPC.C.3
- **`TryAddRange(Segment[])`** — adds multiple segments, skipping any that overlap an existing segment; returns only the stored subset (Background Path, write-only; see [Bulk Storage: TryAddRange](#bulk-storage-tryaddrange) below)
- **`TryRemove(Segment)`** — removes a segment if not already removed (idempotent), typically during eviction (Background Path, write-only); returns `true` if actually removed

---

## Bulk Storage: TryAddRange

### Why TryAddRange Exists

When a user requests a **variable-span range** that partially hits the cache, the User Path computes all uncovered gaps and fetches them from `IDataSource`. If there are N gap sub-ranges, the `CacheNormalizationRequest` carries N fetched chunks.

**Constant-span workloads (e.g., sequential sliding-window reads)** typically produce 0 or 1 gap at most — `TryAdd()` is sufficient.

**Variable-span workloads (e.g., random-access, wide range queries)** can produce 2–100+ gaps in a single request. Without `TryAddRange`, the Background Path would call `TryAdd()` N times. For `SnapshotAppendBufferStorage` this means:

- N `TryAdd()` calls → potentially N normalization passes
- Each normalization pass is O(n + m) where n = current snapshot size, m = buffer size
- Total cost: **O(N × n)** — quadratic in the number of gaps for large caches

`TryAddRange(Segment[])` eliminates this by merging all incoming segments in **a single structural update**:

| FetchedChunks count | Path used       | Normalization passes | Cost           |
|---------------------|-----------------|----------------------|----------------|
| 0 or 1              | `TryAdd()`      | At most 1            | O(n + m)       |
| > 1                 | `TryAddRange()` | Exactly 1            | O(n + N log N) |

The branching logic lives in `CacheNormalizationExecutor.StoreBulkAsync` — it dispatches to `TryAddRange` when `FetchedChunks.Count > 1`, and to `TryAdd` otherwise. `TryGetNonEnumeratedCount()` is used for the branch check since `FetchedChunks` is typed as `IEnumerable<RangeChunk<TRange, TData>>`.

### Contract

- Input may be a non-empty array of `CachedSegment` instances in any order — `SegmentStorageBase` sorts before validation
- Overlap detection against already-stored segments is performed by `SegmentStorageBase` (enforcing VPC.C.3): any segment that overlaps an existing one is silently skipped. **Intra-batch overlap between incoming segments is not detected** — because validation runs against live storage and all incoming segments are validated before any are inserted, two incoming segments that overlap each other will both pass the `FindIntersecting` check if no pre-existing segment covers their range. This is a deliberate trade-off: sorted, non-overlapping inputs (the common case from gap computation) are handled correctly; unexpected intra-batch overlaps from callers are the caller's responsibility
- The return value is the subset of input segments that were actually stored (may be empty if all overlapped)
- An empty input array is a legal no-op (returns an empty array)
- Like `TryAdd()`, `TryAddRange()` is exclusive to the Background Path (single-writer guarantee, VPC.A.1)

---

## Key Design Constraints

Both strategies are designed around VPC's two-thread model:

- **User Path** reads are concurrent with each other (multiple threads may call `FindIntersecting` simultaneously)
- **Background Path** writes are exclusive: only one background thread ever writes (single-writer guarantee)
- **RCU semantics** (Read-Copy-Update): reads operate on a stable snapshot; the background thread builds a new snapshot and publishes it atomically via `Volatile.Write`

**Logical removal** is used by both storage strategies as an internal optimization: a removed segment is marked via `CachedSegment.IsRemoved` (set via `Volatile.Write`, with idempotent removal enforced by `SegmentStorageBase.TryRemove`) so it is immediately invisible to reads, but its node/slot is only physically removed during the next normalization pass. This allows the background thread to batch physical removal work rather than doing it inline during eviction.

**Append buffer** is used by both storage strategies: new segments are written to a small fixed-size buffer (Snapshot strategy) or counted toward a threshold (LinkedList strategy) rather than immediately integrated into the main sorted structure. The main structure is rebuilt ("normalized") when the threshold is reached. Normalization is **not triggered by `TryAdd` itself** — the executor calls `TryNormalize` explicitly after each storage step. The buffer size is configurable via `AppendBufferSize` on each options object (default: 8).

---

## Strategy 1 — Snapshot + Append Buffer (Default)

### When to Use

- Total cached data < 85KB (avoids Large Object Heap pressure)
- Segment count typically low (< ~50 segments)
- Read-to-write ratio is high (few evictions, many reads)

### Tuning: `AppendBufferSize`

Controls the number of segments accumulated in the append buffer before a normalization pass is triggered.

| `AppendBufferSize` | Effect                                                                                                              |
|--------------------|---------------------------------------------------------------------------------------------------------------------|
| **Smaller**        | Normalizes more frequently — snapshot is more up-to-date, but CPU cost (merge) is paid more often per segment added |
| **Larger**         | Normalizes less frequently — lower amortized CPU cost, but snapshot may lag newly added segments longer             |
| **Default (8)**    | Appropriate for most workloads. Only tune under profiling.                                                          |

### Data Structure

```
SnapshotAppendBufferStorage
├── _snapshot: Segment[]             (sorted by range start; read via Volatile.Read)
├── _appendBuffer: Segment[N]        (fixed-size N = AppendBufferSize; new segments written here)
└── _appendCount: int                (count of valid entries in append buffer)
```

> Logical removal is tracked via `CachedSegment.IsRemoved` (an `int` field on each segment, set via `Volatile.Write`). No separate mask array is maintained; all reads filter out segments where `IsRemoved == true`.

### Read Path (User Thread)

1. `Volatile.Read(_snapshot)` — acquire a stable reference to the current snapshot array
2. Binary search on `_snapshot` to find the rightmost segment whose start ≤ `RequestedRange.Start` (via shared `FindLastAtOrBefore` — see [Algorithm Detail](#findintersecting-algorithm-detail) below)
3. Linear scan forward through `_snapshot` collecting all segments that intersect `RequestedRange`; short-circuit when segment start > `RequestedRange.End`; skip soft-deleted entries inline
4. Linear scan through `_appendBuffer[0.._appendCount]` collecting intersecting segments (unsorted, small)
5. Return all collected intersecting segments

**Read cost**: O(log n + k + m) where n = snapshot size, k = matching segments, m = append buffer size

**Allocation**: Zero (returns references to existing segment objects; does not copy data)

### Write Path (Background Thread)

**Add segment (`TryAdd`):** *(VPC.C.3 check owned by `SegmentStorageBase.TryAdd`; `SnapshotAppendBufferStorage` implements `AddCore`)*
1. `SegmentStorageBase.TryAdd` calls `FindIntersecting` on the current snapshot + append buffer — if any existing segment overlaps, return `false` (skip)
2. `AddCore`: write new segment into `_appendBuffer[_appendCount]`; increment `_appendCount`
3. Return `true`
4. Normalization is NOT triggered here — the executor calls `TryNormalize` explicitly after the storage step

**Remove segment (logical removal):**
1. `SegmentStorageBase.TryRemove(segment)` checks `segment.IsRemoved`; if already removed, returns `false` (no-op)
2. Otherwise calls `segment.MarkAsRemoved()` (`Volatile.Write`) and decrements `_count`; returns `true`
3. No immediate structural change to snapshot or append buffer

**TryNormalize (called by executor after each storage step):**
1. Check threshold: if `_appendCount < AppendBufferSize`, return `false` (no-op)
2. Otherwise, run `Normalize()`:
   1. Count live segments in a first pass to size the output array
   2. Discover TTL-expired segments: call `TryRemove(seg)` on expired entries; collect them in the `expiredSegments` out list
   3. Merge `_snapshot` (excluding `IsRemoved`) and `_appendBuffer[0.._appendCount]` into the new array via merge-sort; re-check `IsRemoved` inline during the merge
   4. Under `_normalizeLock`: atomically publish the new snapshot and reset `_appendCount = 0`
   5. Leave `_appendBuffer` contents in place (see below)
3. Return `true` and the `expiredSegments` list (may be null if none expired)

**Normalization cost**: O(n + m) merge of two sorted sequences (snapshot already sorted; append buffer sorted before merge)

**Why `_appendBuffer` is not cleared after normalization:** A `FindIntersecting` call that captured `appendCount > 0` before the lock update is still iterating `_appendBuffer` lock-free when `Normalize` completes. Calling `Array.Clear` on the shared buffer at that point nulls out slots the reader is actively dereferencing, causing a `NullReferenceException`. Stale references left in the buffer are harmless: readers entering after the lock update capture `appendCount = 0` and skip the buffer scan entirely; subsequent `TryAdd()` calls overwrite each slot before making it visible to readers.

**RCU safety**: User Path threads that captured `_snapshot` and `_appendCount` under `_normalizeLock` before normalization continue to operate on a consistent pre-normalization view until their read completes. No intermediate state is ever visible.

### TryAddRange Write Path (Background Thread)

`TryAddRange(segments[])` is used when `FetchedChunks.Count > 1` (multi-gap partial hit). The base class `SegmentStorageBase` owns the validation loop; `SnapshotAppendBufferStorage` implements only the `AddRangeCore` primitive that merges the validated batch into the snapshot:

**Base class (`SegmentStorageBase.TryAddRange`):**
1. If `segments` is empty: return an empty array (no-op)
2. Sort `segments` in-place by range start (incoming order is not guaranteed)
3. For each segment, call `FindIntersecting` against the live snapshot + append buffer — collect only non-overlapping segments into a list
4. If no segments passed validation: return an empty array (no-op)
5. Call `AddRangeCore(validatedArray)` — delegates to the concrete strategy
6. Increment `_count` by the number of stored segments
7. Return the stored segments array

**`SnapshotAppendBufferStorage.AddRangeCore` (the strategy's primitive):**
1. Count live entries in `_snapshot` (first pass)
2. Merge sorted `_snapshot` (excluding `IsRemoved`) and the validated+sorted segments via `MergeSorted`
3. Publish via `Interlocked.Exchange(_snapshot, mergedArray)` — **NOT under `_normalizeLock`** (see note below)

**Why `_normalizeLock` is NOT used in `AddRangeCore`:** The lock guards the `(_snapshot, _appendCount)` pair atomically. `AddRangeCore` does NOT modify `_appendCount`, so the pair invariant (readers must see a consistent count alongside the snapshot they're reading) is preserved. The append buffer contents are entirely ignored by `AddRangeCore` — they remain valid for any concurrent `FindIntersecting` call that is currently scanning them, and will be drained naturally by the next `Normalize()` call. `Interlocked.Exchange` provides the required acquire/release fence for the snapshot swap.

**Why the append buffer is bypassed (not drained):** Draining the buffer into the merge would require acquiring `_normalizeLock` to guarantee atomicity of the `(_snapshot, _appendCount)` update — introducing unnecessary contention. Buffer segments are always visible to `FindIntersecting` via its independent buffer scan regardless of whether a merge has occurred. Bypassing the buffer is correct, cheaper, and requires no coordination with any concurrent reader.

### Memory Behavior

- `_snapshot` is replaced on every normalization (exact-size allocation)
- Arrays < 85KB go to the Small Object Heap (generational GC, compactable)
- Arrays ≥ 85KB go to the Large Object Heap — avoid with this strategy for large caches
- Append buffer is fixed-size (`AppendBufferSize` entries) and reused across normalizations (no allocation per add)

### Alignment with Invariants

| Invariant                          | How enforced                                                                                                                    |
|------------------------------------|---------------------------------------------------------------------------------------------------------------------------------|
| VPC.C.2 — No merging               | Normalization merges array positions, not segment data or statistics                                                            |
| VPC.C.3 — No overlapping segments  | `SegmentStorageBase.TryAdd`/`TryAddRange` call `FindIntersecting` before inserting; any overlapping segment is silently skipped |
| VPC.B.5 — Atomic state transitions | `Volatile.Write(_snapshot, ...)` — single-word publish; old snapshot valid until replaced                                       |
| VPC.A.10 — User Path is read-only  | `FindIntersecting` reads only; all writes in normalize/add/remove are background-only                                           |
| S.H.4 — Lock-free                  | `Volatile.Read/Write` only; no locks                                                                                            |

---

## Strategy 2 — LinkedList + Stride Index

### When to Use

- Total cached data > 85KB
- Segment count is high (>50–100 segments)
- Eviction frequency is high (stride index makes removal cheaper than full array rebuild)

### Tuning: `AppendBufferSize` and `Stride`

**`AppendBufferSize`** controls how many segments are added before the stride index is rebuilt:

| `AppendBufferSize` | Effect                                                                                                                                                                     |
|--------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Smaller**        | Stride index rebuilt more frequently — index stays more up-to-date, but O(n) normalization cost is paid more often                                                         |
| **Larger**         | Stride index rebuilt less often — lower amortized CPU cost; new segments are still in the linked list and always found by `FindIntersecting` regardless of index staleness |
| **Default (8)**    | Appropriate for most workloads. Only tune under profiling.                                                                                                                 |

**`Stride`** controls the density of the stride index:

| `Stride`         | Effect                                                                                               |
|------------------|------------------------------------------------------------------------------------------------------|
| **Smaller**      | Denser index — faster lookup (shorter local list walk from anchor), more memory for the stride array |
| **Larger**       | Sparser index — slower lookup (longer local list walk), less memory; diminishing returns beyond ~32  |
| **Default (16)** | Balanced default. Tune based on typical segment count and read/write ratio.                          |

### Data Structure

```
LinkedListStrideIndexStorage
├── _list: DoublyLinkedList<Segment>           (sorted by range start; single-writer)
├── _strideIndex: LinkedListNode<Segment>[]    (every Nth live node = "stride anchors"; published via Volatile.Write)
└── _addsSinceLastNormalization: int           (counter; triggers stride rebuild at AppendBufferSize threshold)
```

> Logical removal is tracked via `CachedSegment.IsRemoved` (an `int` field on each segment, set via `Volatile.Write`). No separate mask array is maintained; all reads and stride-index walks filter out segments where `IsRemoved == true`. Physical unlinking of removed nodes from `_list` happens during stride normalization.

**No `_nodeMap`:** The stride index stores `LinkedListNode<T>` references directly, eliminating the need for a separate segment-to-node dictionary. Callers use `anchorNode.List != null` to verify the node is still linked before walking from it.

**Stride**: A configurable integer N (default N=16) defining how often a stride anchor is placed. A stride anchor is a reference to the 1st, (N+1)th, (2N+1)th... live node in the sorted linked list.

### Read Path (User Thread)

1. `Volatile.Read(_strideIndex)` — acquire stable reference to the current stride index
2. Binary search on `_strideIndex` to find the rightmost stride anchor whose start ≤ `RequestedRange.Start` (via shared `FindLastAtOrBefore`). No step-back needed: Invariant VPC.C.3 (`End[i] < Start[i+1]`, strict) ensures all segments before the anchor have `End < range.Start` and cannot intersect (see [Algorithm Detail](#findintersecting-algorithm-detail) below)
3. From the anchor node, linear scan forward through `_list` collecting all intersecting segments; short-circuit when node start > `RequestedRange.End`; skip soft-deleted entries inline
4. Return all collected intersecting segments

> All segments are inserted directly into `_list` via `InsertSorted` when added. There is no separate append buffer for `FindIntersecting` to scan — the linked list walk covers all segments regardless of whether the stride index has been rebuilt since they were added.

**Read cost**: O(log(n/N) + k + N) where n = total segments, N = stride, k = matching segments

**Read cost vs Snapshot strategy**: For large n, the stride-indexed search replaces O(log n) binary search on a large array with O(log(n/N)) on the smaller stride index + O(N) local list walk from the anchor. For small n, Snapshot is typically faster.

### Write Path (Background Thread)

**Add segment (`TryAdd`):** *(VPC.C.3 check owned by `SegmentStorageBase.TryAdd`; `LinkedListStrideIndexStorage` implements `AddCore`)*
1. `SegmentStorageBase.TryAdd` calls `FindIntersecting` on the current linked list (via stride index) — if any existing segment overlaps, return `false` (skip)
2. `AddCore`: insert new segment into `_list` at the correct sorted position via `InsertSorted` (uses stride index for O(log(n/N)) anchor lookup + O(N) local walk); increment `_addsSinceLastNormalization`
3. Return `true`
4. Normalization is NOT triggered here — the executor calls `TryNormalize` explicitly after the storage step

**Remove segment (logical removal):**
1. `SegmentStorageBase.TryRemove(segment)` checks `segment.IsRemoved`; if already removed, returns `false` (no-op)
2. Otherwise calls `segment.MarkAsRemoved()` (`Volatile.Write`) and decrements `_count`; returns `true`
3. No immediate structural change to the list or stride index

**TryNormalize (called by executor after each storage step):**
1. Check threshold: if `_addsSinceLastNormalization < AppendBufferSize`, return `false` (no-op)
2. Otherwise, run `NormalizeStrideIndex()` (see below)
3. Return `true` and the `expiredSegments` list (may be null if none expired)

**NormalizeStrideIndex (two-pass for RCU safety):**

Pass 1 — build new stride index:
1. Walk `_list` from head to tail
2. Discover TTL-expired segments: call `TryRemove(seg)` on expired entries; collect them in the `expiredSegments` out list
3. For each **live** node (skip `IsRemoved` nodes without unlinking them): if this is the Nth live node seen, add it to the new stride anchor array
4. Publish new stride index: `Interlocked.Exchange(_strideIndex, newArray)` (release fence)

Pass 2 — physical cleanup (safe only after new index is live):
5. Walk `_list` again; physically unlink every `IsRemoved` node
6. Reset `_addsSinceLastNormalization = 0`

> **Why two passes?** Any User Path thread that read the *old* stride index before the swap may still be walking through `_list` using old anchor nodes as starting points. Those old anchors may point to nodes that are about to be physically removed. If we unlinked removed nodes *before* publishing the new index, a concurrent walk starting from a stale anchor could follow a node whose `Next` pointer was already set to `null` by physical removal, truncating the walk prematurely and missing live segments. Publishing first ensures all walkers using old anchors will complete correctly before those nodes disappear.

**Per-node lock granularity during physical cleanup:** Dead nodes are unlinked one at a time, each under a brief `_listSyncRoot` acquisition: both `node.Next` capture and `_list.Remove(node)` execute inside the same per-node lock block, so the walk variable `next` is captured before `Remove()` can null out the pointer. The User Path (`FindIntersecting`) holds `_listSyncRoot` for its entire linked-list walk, so reads and removals interleave at node granularity: each removal step waits only for the current read to release the lock, then executes one `Remove()`, then yields so the reader can continue. This gives the User Path priority without blocking either path wholesale.

**ArrayPool rental for anchor accumulation:** `NormalizeStrideIndex` uses an `ArrayPool<T>` rental as the anchor accumulation buffer (returned immediately after the right-sized index array is constructed), eliminating the intermediate `List<T>` and its `ToArray()` copy. The only heap allocation is the published stride index array itself (unavoidable).

**Normalization cost**: O(n) list traversal (two passes) + O(n/N) for new stride array allocation

### TryAddRange Write Path (Background Thread)

`TryAddRange(segments[])` is used when `FetchedChunks.Count > 1` (multi-gap partial hit). The base class `SegmentStorageBase` owns the validation loop; `LinkedListStrideIndexStorage` implements only the `AddRangeCore` primitive that inserts the validated batch and rebuilds the stride index once:

**Base class (`SegmentStorageBase.TryAddRange`):**
1. If `segments` is empty: return an empty array (no-op)
2. Sort `segments` in-place by range start (incoming order is not guaranteed)
3. For each segment, call `FindIntersecting` against the current linked list — collect only non-overlapping segments into a list
4. If no segments passed validation: return an empty array (no-op)
5. Call `AddRangeCore(validatedArray)` — delegates to the concrete strategy
6. Increment `_count` by the number of stored segments
7. Return the stored segments array

**`LinkedListStrideIndexStorage.AddRangeCore` (the strategy's primitive):**
1. For each validated segment: call `InsertSorted` to insert into `_list` and increment `_addsSinceLastNormalization`
2. Return — normalization is **not** triggered here (see note below)

**Why `AddRangeCore` must NOT call `NormalizeStrideIndex` directly:** `AddRangeCore` is called from `SegmentStorageBase.TryAddRange`, which returns immediately to the executor. The executor then calls `TryNormalize` — the only path where TTL-expired segments are discovered and returned to the caller so that `OnSegmentRemoved` / `TtlSegmentExpired` diagnostics fire. Calling `NormalizeStrideIndex` inside `AddRangeCore` would:
- Discard the expired-segments list (`out _` — inaccessible to the executor), silently breaking eviction policy aggregates and diagnostics.
- Reset `_addsSinceLastNormalization = 0`, causing the executor's `TryNormalize` to always see `ShouldNormalize() == false` and skip, permanently preempting the normalization cadence.

The stride index will be stale until the executor's `TryNormalize` fires, but all newly-inserted segments are immediately live in `_list` and are found by `FindIntersecting` regardless of index staleness.

### Random Segment Sampling and Eviction Bias

Eviction selectors call `TryGetRandomSegment()` to obtain candidates. In `LinkedListStrideIndexStorage` this method:

1. Picks a random stride anchor index from `_strideIndex`
2. Picks a random offset within that anchor's stride gap (up to `_stride` nodes)
3. Walks forward from the anchor to the selected node

This produces **approximately** uniform selection, not perfectly uniform:

- Each of the `n/N` anchors is equally likely to be chosen in step 1
- For interior anchors, the reachable gap is exactly `_stride` nodes — selection within the gap is uniform
- For the **last anchor**, the gap may contain **more than `_stride` nodes** if segments have been added since the last normalization. Those extra nodes (in the "append tail") are reachable only from the last anchor, so they are slightly under-represented compared to nodes reachable from earlier anchors

**Why this is acceptable:**

This is a deliberate O(stride) performance trade-off. True uniform selection would require counting all live nodes first — O(n). Eviction selectors sample multiple candidates (`EvictionSamplingOptions.SampleSize`) and pick the worst of the sample; a slight positional bias in individual draws has negligible impact on overall eviction quality. The bias diminishes toward zero as the normalization cadence (`AppendBufferSize`) is tuned smaller relative to `stride`.

**When it matters:**

- Very small caches (< 10 segments): bias may be more noticeable; consider using `SnapshotAppendBufferStorage` instead
- After a burst of rapid adds before normalization: the append tail temporarily grows; effect disappears after the next normalization pass

### Memory Behavior

- `_list` nodes are individually allocated (generational GC; no LOH pressure regardless of total size)
- `_strideIndex` is a small array (n/N entries) — minimal LOH risk
- Avoids the "one giant array" pattern that causes LOH pressure in the Snapshot strategy

### RCU Semantics

Same as Strategy 1: User Path threads read via `Volatile.Read(_strideIndex)`. The linked list itself is read directly (nodes are stable; soft-deleted nodes are simply skipped). The stride index snapshot is rebuilt and published atomically. Physical removal of dead nodes only happens after the new stride index is live, preserving `Next` pointer integrity for any concurrent walk still using the old index.

### Alignment with Invariants

| Invariant                          | How enforced                                                                                                                    |
|------------------------------------|---------------------------------------------------------------------------------------------------------------------------------|
| VPC.C.2 — No merging               | Insert adds a new independent node; no existing node data is modified                                                           |
| VPC.C.3 — No overlapping segments  | `SegmentStorageBase.TryAdd`/`TryAddRange` call `FindIntersecting` before inserting; any overlapping segment is silently skipped |
| VPC.B.5 — Atomic state transitions | `Interlocked.Exchange(_strideIndex, ...)` — stride index atomically replaced; physical removal deferred until after publish     |
| VPC.A.10 — User Path is read-only  | `FindIntersecting` reads only; all structural mutations are background-only                                                     |

---

## Strategy Comparison

| Aspect                              | Snapshot + Append Buffer        | LinkedList + Stride Index         |
|-------------------------------------|---------------------------------|-----------------------------------|
| **Read cost**                       | O(log n + k + m)                | O(log(n/N) + k + N)               |
| **Write cost (add)**                | O(1) amortized (to buffer)      | O(log(n/N) + N)                   |
| **Normalization cost**              | O(n + m)                        | O(n)                              |
| **Eviction cost (logical removal)** | O(1)                            | O(1)                              |
| **Memory pattern**                  | One sorted array per snapshot   | Linked list + small stride array  |
| **LOH risk**                        | High for large n                | Low (no single large array)       |
| **Best for**                        | Small caches, < 85KB total data | Large caches, high segment counts |
| **Segment count sweet spot**        | < ~50 segments                  | > ~50–100 segments                |

---

## FindIntersecting Algorithm Detail

Both strategies share the same binary search primitive and the same forward-scan + short-circuit pattern.
The key difference is *what* the binary search operates on (flat array vs sparse stride anchors).
Neither strategy needs a step-back after the search — Invariant VPC.C.3 (`End[i] < Start[i+1]`, strict)
guarantees that all elements before the binary-search result have `End < range.Start` and cannot
intersect the query range.

### Shared Binary Search: `FindLastAtOrBefore(array, value)`

**Goal**: find the rightmost element in a sorted array where `Start.Value <= value`. Returns that
index, or `-1` if no element qualifies.

```
Example: 8 segments sorted by Start.Value, searching for value = 50

Index:   0      1      2      3      4      5      6      7
Start: [ 10 ] [ 20 ] [ 30 ] [ 40 ] [ 60 ] [ 70 ] [ 80 ] [ 90 ]
       <=50   <=50   <=50   <=50    >50    >50    >50    >50
        \_______________________/   \_______________________/
           qualify (Start<=50)            don't qualify

Answer: index 3  (rightmost where Start <= 50)
```

**Iteration trace** — `lo` and `hi` are the active search window:

```
Iteration 1:   lo=0, hi=7
               mid = 0 + ( 7 - 0 ) / 2 = 3
               Start[3] = 40 <= 50?  YES  →  lo = mid + 1 = 4

                  lo=0                                             hi=7
                   |                                                |
                 [ 10 ] [ 20 ] [ 30 ] [ 40 ] [ 60 ] [ 70 ] [ 80 ] [ 90 ]
                                       ^^^^
                                       mid=3, qualifies → lo moves right

Iteration 2:   lo=4, hi=7
               mid = 4 + ( 7 - 4 ) / 2 = 5
               Start[5] = 70 <= 50?  NO   →  hi = mid - 1 = 4

                                              lo=4                 hi=7
                                               |                    |
                 [ 10 ] [ 20 ] [ 30 ] [ 40 ] [ 60 ] [ 70 ] [ 80 ] [ 90 ]
                                              ^^^^
                                              mid=5, doesn't qualify → hi moves left

Iteration 3:   lo=4, hi=4
               mid = 4 + ( 4 - 4 ) / 2 = 4
               Start[4] = 60 <= 50?  NO   →  hi = mid - 1 = 3

                                           lo=4  hi=4
                                              |  |
                 [ 10 ] [ 20 ] [ 30 ] [ 40 ] [ 60 ] [ 70 ] [ 80 ] [ 90 ]
                                              ^^^^
                                              mid=4, doesn't qualify → hi moves left

Loop ends: lo = 4 > hi = 3  →  return hi = 3  ✓
```

**Invariant maintained throughout**: everything at index < lo qualifies (Start <= value);
everything at index > hi does not qualify (Start > value). When the loop exits, `hi` is
the rightmost qualifying index (or -1 if lo never advanced past 0).

---

### Strategy 1 — Snapshot: no step-back needed

`FindIntersecting` calls `FindLastAtOrBefore(snapshot, range.Start.Value)`.

Because every element is directly indexed and segments are **non-overlapping** (Invariant VPC.C.3),
ends are also monotonically ordered: `End[i] < Start[i+1]`. This means every element before `hi`
has `End < Start[hi] <= range.Start` and can never intersect the query range.
`hi` itself is the earliest possible intersector — no step-back is needed.

```
Example: snapshot has 5 segments; query range = [50, 120]

Index:   0          1          2          3          4
      [10──25]   [30──55]   [60──75]   [80──95]  [110──130]
                     ↑ range.Start = 50

FindLastAtOrBefore(snapshot, 50) → hi = 1   (Start[1] = 30, rightmost where Start <= 50)

scanStart = Math.Max(0, hi) = 1         ← start here, no step-back

Scan forward from index 1:
  i=1: [30──55]  →  Start=30 <= 120, Overlaps [50,120]?  YES  ✓  (End=55 >= 50)
  i=2: [60──75]  →  Start=60 <= 120, Overlaps [50,120]?  YES  ✓
  i=3: [80──95]  →  Start=80 <= 120, Overlaps [50,120]?  YES  ✓
  i=4: [110──130]→  Start=110 <= 120, Overlaps [50,120]?  YES  ✓
  (end of snapshot)

Why i = 0 is correctly skipped:
  Invariant VPC.C.3: End[0] = 25 < Start[1] = 30 <= range.Start = 50
  So [10──25] provably cannot reach range.Start. Starting at hi is exact.
```

**Edge cases:**

```
hi = -1  →  all segments start after range.Start
            scanStart = Math.Max(0, -1) = 0
            scan from 0; segments may still intersect if Start <= range.End

hi = 0   →  only segment[0] qualifies
            scanStart = 0; scan from segment[0]

hi = n-1 →  all segments start at or before range.Start
            scanStart = n-1; scan from last qualifying segment forward
```

---

### Strategy 2 — Stride Index: no step-back needed

`FindIntersecting` calls `FindLastAtOrBefore(strideIndex, range.Start.Value)`, then uses
`anchorIdx = Math.Max(0, hi)` — identical reasoning to Strategy 1.

The stride index is **sparse** (every Nth live node), but the no-step-back proof is the same:

```
Proof (applies to both strategies):

  Let anchor[hi] = the rightmost stride anchor where anchor[hi].Start <= range.Start.
  Let X = any segment before anchor[hi] in the linked list.

  By sorted order:        X.Start < anchor[hi].Start
  By VPC.C.3 (strict):   X.End   < Start_of_next_segment_after_X
  Transitively:           X.End   < ... < anchor[hi].Start
  By binary search:       anchor[hi].Start <= range.Start
  Therefore:              X.End   < range.Start

  X cannot intersect [range.Start, range.End].  QED.
```

This holds regardless of whether X is a stride anchor or an unindexed node between anchors —
VPC.C.3's strict inequality propagates through the entire sorted chain.

```
Example: 12 nodes in linked list, stride = 4, query range = [42, 80]

Linked list (sorted by Start, ends respect End[i] < Start[i+1]):
   1      2      3      4      5      6      7      8      9      10     11     12
  [A]────[B]────[C]────[D]────[E]────[F]────[G]────[H]────[I]────[J]────[K]────[L]
  10─11  15─16  20─21  25─26  30─31  35─36  40─41  45─46  50─51  55─56  60─61  65─66

Stride index (every 4th live node):
  anchor[0] = node 1 (A)  (Start=10)
  anchor[1] = node 5 (E)  (Start=30)
  anchor[2] = node 9 (I)  (Start=50)

FindLastAtOrBefore(strideIndex, range.Start=42) → hi = 1
  (anchor[1].Start=30 <= 42;  anchor[2].Start=50 > 42)

anchorIdx = Math.Max(0, hi) = 1  →  start walk from anchor[1] = node E

Why starting from anchor[1] is safe:
  Nodes A, B, C, D are before anchor[1] and unreachable by forward walk from E.
  But by VPC.C.3: D.End=26 < E.Start=30 <= range.Start=42.
  D.End < range.Start, so D cannot intersect [42, 80].
  Same reasoning applies to C, B, A.

Walk forward from anchor[1] = node E:
  E  (30─31): Start=30 <= 80, Overlaps [42,80]?  NO  (End=31 < 42)
  F  (35─36): NO  (End=36 < 42)
  G  (40─41): NO  (End=41 < 42)
  H  (45─46): Start=45 <= 80, Overlaps [42,80]?  YES  ✓
  I  (50─51): YES  ✓
  J  (55─56): YES  ✓
  K  (60─61): YES  ✓
  L  (65─66): YES  ✓
  (end of list)
```

**Edge cases:**

```
hi = -1         →  all anchors start after range.Start; startNode = null
                   walk from _list.First (full list walk)

hi = 0          →  anchorIdx = Math.Max(0, 0) = 0
                   walk from anchor[0]

anchor unlinked →  outer anchorNode.List == null guard fires before lock acquisition
                   (fast-path hint — avoids acquiring the lock unnecessarily)
                   AND inner startNode?.List == null re-check fires inside the lock
                   (VPC.D.7 TOCTOU guard — eliminates race between the two checks)
                   fall back to _list.First
```

---

### Zero-Allocation Accessor Design

Both strategies use the same `FindLastAtOrBefore` method despite operating on different element
types. The element types differ in how the `Start.Value` key is extracted:

```
CachedSegment<TRange,TData>[]                 →  element.Range.Start.Value
LinkedListNode<CachedSegment<TRange,TData>>[] →  element.Value.Range.Start.Value
                                                        ^^^^^^
                                                  one extra indirection
```

A delegate or virtual method would allocate on every call — unacceptable on the User Path hot
path. Instead, the accessor is a **zero-size struct** implementing a protected interface. The JIT
specialises the generic instantiation and inlines the key extraction to a single field load:

```
interface ISegmentAccessor<TElement> {        ← protected in SegmentStorageBase
    TRange GetStartValue(TElement element);
}

struct DirectAccessor         : ISegmentAccessor<CachedSegment<>>
    → element.Range.Start.Value               ← private nested struct in SnapshotAppendBufferStorage

struct LinkedListNodeAccessor : ISegmentAccessor<LinkedListNode<CachedSegment<>>>
    → element.Value.Range.Start.Value         ← private nested struct in LinkedListStrideIndexStorage

FindLastAtOrBefore<TElement, TAccessor>(array, value, accessor = default)
                             ^^^^^^^^^
                             struct constraint → JIT specialises, inlines GetStartValue
                             no heap allocation, no virtual dispatch
```

Each accessor is a private nested `readonly struct` inside the concrete strategy that owns it.
`ISegmentAccessor<TElement>` is the only accessor-related type in `SegmentStorageBase` — the
interface contract is shared, the implementations are not. Adding a new storage strategy means
adding a new nested accessor struct in that strategy's file, with no changes to the base class.

Callers pass `default(DirectAccessor)` or `default(LinkedListNodeAccessor)` — a zero-byte value
that carries no state and costs nothing at runtime.

---

## Decision Matrix

### Choose **Snapshot + Append Buffer** if:

1. Total cached data is **small** (< 85KB)
2. Segment count is **low** (< 50)
3. Reads are **much more frequent** than segment additions or evictions
4. Access pattern is **read-heavy with infrequent eviction**

### Choose **LinkedList + Stride Index** if:

1. Total cached data is **large** (> 85KB)
2. Segment count is **high** (> 100)
3. Eviction frequency is **high** (many segments added and removed frequently)
4. LOH pressure is a concern for the application's GC profile

### Default

If unsure: start with **Snapshot + Append Buffer** (`SnapshotAppendBufferStorageOptions<TRange, TData>.Default`). Profile and switch to **LinkedList + Stride Index** if:
- LOH collections appear in GC metrics
- Segment count grows beyond ~100
- Normalization cost becomes visible in profiling

---

## Implementation Notes

### Thread-Safe Segment Count

Both strategies expose a `Count` property that is read by the `MaxSegmentCountPolicy` on the Background Storage Loop. With the passive TTL design, all mutations (`_count` increments and decrements) run exclusively on the Background Storage Loop — there is no separate TTL thread updating the count concurrently. The `_count` field uses plain `++`/`--` increments protected by the single-writer guarantee rather than `Interlocked` operations.

### Logical Removal: Internal Optimization Only

Logical removal (via `CachedSegment.IsRemoved`) is an implementation detail of both storage strategies. It is NOT an architectural invariant. Future storage strategies (e.g., skip list, B+ tree) may use immediate physical removal instead. External code must never observe or depend on the logically-removed-but-not-yet-unlinked state of a segment.

From the User Path's perspective, a segment is either present (returned by `FindIntersecting`) or absent. Logically-removed segments are filtered out during scans and are never returned to the User Path.

### Append Buffer: Internal Optimization Only

The append buffer is an internal optimization to defer sort-order maintenance. It is NOT an architectural concept shared across components. The distinction between "in the main structure" and "in the append buffer" is invisible outside the storage implementation. The `AppendBufferSize` tuning parameter on each options class controls this threshold.

### Non-Merging Invariant

Neither strategy ever merges two segments into one. When "normalization" is mentioned above, it refers to rebuilding the sorted array or stride index — not merging segment data. Each segment created by the Background Path (from a `CacheNormalizationRequest.FetchedChunks` entry) retains its own identity, statistics, and position in the collection for its entire lifetime.

---

## See Also

- `docs/visited-places/invariants.md` — VPC.C (segment storage invariants), VPC.D (concurrency invariants)
- `docs/visited-places/actors.md` — Segment Storage actor responsibilities
- `docs/visited-places/scenarios.md` — storage behavior in context of B2 (store no eviction), B4 (multi-gap)
- `docs/visited-places/eviction.md` — how eviction interacts with storage (soft delete, segment removal)
- `docs/shared/glossary.md` — RCU, WaitForIdleAsync, CacheInteraction terms
