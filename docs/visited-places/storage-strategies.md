# Storage Strategies — VisitedPlaces Cache

This document describes the two MVP storage strategies available for `VisitedPlacesCache`. These are internal implementation details — the public API and architectural invariants (see `docs/visited-places/invariants.md`) hold regardless of which strategy is selected.

---

## Overview

`VisitedPlacesCache` stores a collection of **non-contiguous, independently-sorted segments**. Two storage strategies are available, selectable at construction time:

1. **Snapshot + Append Buffer** — default; optimized for smaller caches (<85KB total data)
2. **LinkedList + Stride Index** — for larger caches where segment counts are high and traversal cost dominates

Both strategies expose the same internal interface:
- **`FindIntersecting(RequestedRange)`** — returns all segments whose ranges intersect `RequestedRange` (User Path, read-only)
- **`Add(Segment)`** — adds a new segment (Background Path, write-only)
- **`Remove(Segment)`** — removes a segment, typically during eviction (Background Path, write-only)

---

## Key Design Constraints

Both strategies are designed around VPC's two-thread model:

- **User Path** reads are concurrent with each other (multiple threads may call `FindIntersecting` simultaneously)
- **Background Path** writes are exclusive: only one background thread ever writes (single-writer guarantee)
- **RCU semantics** (Read-Copy-Update): reads operate on a stable snapshot; the background thread builds a new snapshot and publishes it atomically via `Volatile.Write`

**Soft delete** is used by both MVP strategies as an internal optimization: segments marked for eviction are logically removed immediately (invisible to reads) but physically removed during the next normalization pass. This allows the background thread to batch physical removal work rather than doing it inline during eviction.

**Append buffer** is used by both MVP strategies: new segments are written to a small fixed-size buffer rather than immediately integrated into the main sorted structure. The main structure is rebuilt ("normalized") when the buffer becomes full. This amortizes the cost of maintaining sort order.

---

## Strategy 1 — Snapshot + Append Buffer (Default)

### When to Use

- Total cached data < 85KB (avoids Large Object Heap pressure)
- Segment count typically low (< ~50 segments)
- Read-to-write ratio is high (few evictions, many reads)

### Data Structure

```
SnapshotAppendBufferStorage
├── _snapshot: Segment[]             (sorted by range start; read via Volatile.Read)
├── _appendBuffer: Segment[N]        (fixed-size; new segments written here)
├── _appendCount: int                (count of valid entries in append buffer)
└── _softDeleteMask: bool[*]         (marks deleted segments; cleared on normalization)
```

### Read Path (User Thread)

1. `Volatile.Read(_snapshot)` — acquire a stable reference to the current snapshot array
2. Binary search on `_snapshot` to find the first segment whose end ≥ `RequestedRange.Start`
3. Linear scan forward through `_snapshot` collecting all segments that intersect `RequestedRange` (short-circuit when segment start > `RequestedRange.End`)
4. Linear scan through `_appendBuffer[0.._appendCount]` collecting intersecting segments
5. Filter out soft-deleted entries from both scans
6. Return all collected intersecting segments

**Read cost**: O(log n + k + m) where n = snapshot size, k = matching segments, m = append buffer size

**Allocation**: Zero (returns references to existing segment objects; does not copy data)

### Write Path (Background Thread)

**Add segment:**
1. Write new segment into `_appendBuffer[_appendCount]`
2. Increment `_appendCount`
3. If `_appendCount == N` (buffer full): **normalize** (see below)

**Remove segment (soft delete):**
1. Mark the segment's slot in `_softDeleteMask` as `true`
2. No immediate structural change

**Normalize:**
1. Allocate a new `Segment[]` of size `(_snapshot.Length - softDeleteCount + _appendCount)`
2. Merge `_snapshot` (excluding soft-deleted entries) and `_appendBuffer[0.._appendCount]` into the new array via merge-sort
3. Reset `_softDeleteMask` (all `false`)
4. Reset `_appendCount = 0`
5. `Volatile.Write(_snapshot, newArray)` — atomically publish the new snapshot

**Normalization cost**: O(n log n) where n = total segment count (or O(n + m) with merge-sort since both inputs are sorted)

**RCU safety**: User Path threads that read `_snapshot` via `Volatile.Read` before normalization continue to see the old, valid snapshot until their read completes. The new snapshot is published atomically; no intermediate state is ever visible.

### Memory Behavior

- `_snapshot` is replaced on every normalization (exact-size allocation)
- Arrays < 85KB go to the Small Object Heap (generational GC, compactable)
- Arrays ≥ 85KB go to the Large Object Heap — avoid with this strategy for large caches
- Append buffer is fixed-size and reused across normalizations (no allocation per add)
- Soft-delete mask is same size as snapshot, reallocated on normalization

### Alignment with Invariants

| Invariant                          | How enforced                                                                              |
|------------------------------------|-------------------------------------------------------------------------------------------|
| VPC.C.2 — No merging               | Normalization merges array positions, not segment data or statistics                      |
| VPC.C.3 — No overlapping segments  | Invariant maintained at insertion time (implementation responsibility)                    |
| VPC.B.5 — Atomic state transitions | `Volatile.Write(_snapshot, ...)` — single-word publish; old snapshot valid until replaced |
| VPC.A.10 — User Path is read-only  | `FindIntersecting` reads only; all writes in normalize/add/remove are background-only     |
| S.H.4 — Lock-free                  | `Volatile.Read/Write` only; no locks                                                      |

---

## Strategy 2 — LinkedList + Stride Index

### When to Use

- Total cached data > 85KB
- Segment count is high (>50–100 segments)
- Eviction frequency is high (stride index makes removal cheaper than full array rebuild)

### Data Structure

```
LinkedListStrideIndexStorage
├── _list: DoublyLinkedList<Segment>     (sorted by range start; single-writer)
├── _strideIndex: Segment[]              (array of every Nth node = "stride anchors")
├── _strideAppendBuffer: Segment[M]      (new stride anchors, appended before normalization)
├── _strideAppendCount: int
└── _softDeleteMask: bool[*]             (marks deleted nodes across list + stride index)
```

**Stride**: A configurable integer N (e.g., N=16) defining how often a stride anchor is placed. A stride anchor is a reference to the Nth, 2Nth, 3Nth... node in the sorted linked list.

### Read Path (User Thread)

1. `Volatile.Read(_strideIndex)` — acquire stable reference to the current stride index
2. Binary search on `_strideIndex` to find the stride anchor just before `RequestedRange.Start`
3. From the anchor node, linear scan forward through `_list` collecting all intersecting segments (short-circuit when node start > `RequestedRange.End`)
4. Linear scan through `_strideAppendBuffer[0.._strideAppendCount]` — these are the most-recently-added segments not yet in the main list
5. Filter out soft-deleted entries
6. Return all collected intersecting segments

**Read cost**: O(log(n/N) + k + N + m) where n = total segments, N = stride, k = matching segments, m = stride append buffer size

**Read cost vs Snapshot strategy**: For large n (many segments), the stride-indexed search eliminates the O(log n) binary search over a large array and replaces it with O(log(n/N)) on a smaller stride index + O(N) local scan. For small n, Snapshot is typically faster.

### Write Path (Background Thread)

**Add segment:**
1. Insert new node into `_list` in sorted position (O(log(n/N) + N) using stride to find insertion point)
2. Write reference to `_strideAppendBuffer[_strideAppendCount]`
3. Increment `_strideAppendCount`
4. If `_strideAppendCount == M` (stride buffer full): **normalize stride index** (see below)

**Remove segment (soft delete):**
1. Mark the segment's node in `_softDeleteMask` as `true`
2. No immediate structural change to the list or stride index

**Normalize stride index:**
1. Allocate a new `Segment[]` of size `ceil(nonDeletedListCount / N)`
2. Walk `_list` from head to tail (excluding soft-deleted nodes), collecting every Nth node as a stride anchor
3. Reset `_strideAppendBuffer` (clear count)
4. Reset all soft-delete bits for stride-index entries (physical removal of deleted nodes from `_list` also happens here)
5. `Volatile.Write(_strideIndex, newArray)` — atomically publish the new stride index

**Normalization cost**: O(n) list traversal + O(n/N) for new stride array allocation

**Physical removal**: Soft-deleted nodes are physically unlinked from `_list` during stride normalization. Between normalizations, they remain in the list but are skipped during scans via the soft-delete mask.

### Memory Behavior

- `_list` nodes are individually allocated (generational GC; no LOH pressure regardless of total size)
- `_strideIndex` is a small array (n/N entries) — minimal LOH risk
- Stride append buffer is fixed-size and reused (no per-add allocation)
- Avoids the "one giant array" pattern that causes LOH pressure in the Snapshot strategy

### RCU Semantics

Same as Strategy 1: User Path threads read via `Volatile.Read(_strideIndex)`. The linked list itself is read directly (nodes are stable; soft-deleted nodes are simply skipped). The stride index snapshot is rebuilt and published atomically.

### Alignment with Invariants

| Invariant                          | How enforced                                                                    |
|------------------------------------|---------------------------------------------------------------------------------|
| VPC.C.2 — No merging               | Insert adds a new independent node; no existing node data is modified           |
| VPC.C.3 — No overlapping segments  | Invariant maintained at insertion time                                          |
| VPC.B.5 — Atomic state transitions | `Volatile.Write(_strideIndex, ...)` — stride index snapshot atomically replaced |
| VPC.A.10 — User Path is read-only  | `FindIntersecting` reads only; all structural mutations are background-only     |

---

## Strategy Comparison

| Aspect                          | Snapshot + Append Buffer        | LinkedList + Stride Index         |
|---------------------------------|---------------------------------|-----------------------------------|
| **Read cost**                   | O(log n + k + m)                | O(log(n/N) + k + N + m)           |
| **Write cost (add)**            | O(1) amortized (to buffer)      | O(log(n/N) + N)                   |
| **Normalization cost**          | O(n log n) or O(n+m)            | O(n)                              |
| **Eviction cost (soft delete)** | O(1)                            | O(1)                              |
| **Memory pattern**              | One sorted array per snapshot   | Linked list + small stride array  |
| **LOH risk**                    | High for large n                | Low (no single large array)       |
| **Best for**                    | Small caches, < 85KB total data | Large caches, high segment counts |
| **Segment count sweet spot**    | < ~50 segments                  | > ~50–100 segments                |

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

If unsure: start with **Snapshot + Append Buffer**. Profile and switch to **LinkedList + Stride Index** if:
- LOH collections appear in GC metrics
- Segment count grows beyond ~100
- Normalization cost becomes visible in profiling

---

## Implementation Notes

### Soft Delete: Internal Optimization Only

Soft delete is an implementation detail of both MVP strategies. It is NOT an architectural invariant. Future storage strategies (e.g., skip list, B+ tree) may use immediate physical removal instead. External code must never observe or depend on the soft-deleted-but-not-yet-removed state of a segment.

From the User Path's perspective, a segment is either present (returned by `FindIntersecting`) or absent. Soft-deleted segments are filtered out during scans and are never returned to the User Path.

### Append Buffer: Internal Optimization Only

The append buffer is an internal optimization to defer sort-order maintenance. It is NOT an architectural concept shared across components. The distinction between "in the main structure" and "in the append buffer" is invisible outside the storage implementation.

### Non-Merging Invariant

Neither strategy ever merges two segments into one. When `Normalization` is mentioned above, it refers to rebuilding the sorted array or stride index — not merging segment data. Each segment created by the Background Path (from a `BackgroundEvent.FetchedData` entry) retains its own identity, statistics, and position in the collection for its entire lifetime.

---

## See Also

- `docs/visited-places/invariants.md` — VPC.C (segment storage invariants), VPC.D (concurrency invariants)
- `docs/visited-places/actors.md` — Segment Storage actor responsibilities
- `docs/visited-places/scenarios.md` — storage behavior in context of B2 (store no eviction), B4 (multi-gap)
- `docs/visited-places/eviction.md` — how eviction interacts with storage (soft delete, segment removal)
- `docs/shared/glossary.md` — RCU, WaitForIdleAsync, CacheInteraction terms
