# Architecture — VisitedPlacesCache

VisitedPlaces-specific architectural details. Shared foundations — single-writer architecture, user-path-never-blocks, `AsyncActivityCounter`, work scheduler abstraction, disposal pattern, layered cache concept — are documented in `docs/shared/architecture.md`.

---

## Overview

`VisitedPlacesCache` is a range-based cache optimized for **random access** (non-contiguous, non-sequential requests). It models a user who returns to previously visited points — a map viewer panning across regions, a media scrubber jumping to arbitrary timestamps, or an analytics query hitting different time windows.

Unlike `SlidingWindowCache`, VPC:
- **Stores non-contiguous segments** — no contiguity requirement; gaps are valid cache state
- **Never prefetches** — fetches only what is strictly needed for the current request
- **Never merges segments** — each independently-fetched range remains a distinct segment
- **Processes every event** — no supersession; FIFO ordering preserves metadata accuracy

The library ships one NuGet package: **`Intervals.NET.Caching.VisitedPlaces`**. `Intervals.NET.Caching` is a non-packable shared foundation project (`<IsPackable>false</IsPackable>`) whose types — `IRangeCache`, `IDataSource`, `RangeResult`, `RangeChunk`, `CacheInteraction`, `LayeredRangeCache`, `RangeCacheDataSourceAdapter`, `LayeredRangeCacheBuilder`, `AsyncActivityCounter`, and the strong-consistency extension methods — are compiled directly into the `Intervals.NET.Caching.VisitedPlaces` assembly via `ProjectReference` with `PrivateAssets="all"`. It is never published as a standalone package.

---

## Segment Model

VPC maintains a collection of **non-contiguous segments** (`CachedSegments`). Each segment is a contiguous, independently-fetched range with its own data and eviction metadata.

Key structural rules:
- No two segments may share any discrete domain point (Invariant VPC.C.3)
- Segments are never merged, even if adjacent (Invariant VPC.C.2)
- The User Path assembles multi-segment responses in-memory; nothing is ever written back to storage from the User Path
- Eviction removes individual segments from the collection

**Contrast with SlidingWindowCache:** SWC maintains exactly one contiguous cached window and discards everything outside it on rebalance. VPC accumulates segments over time and uses eviction policies to enforce capacity limits.

---

## Threading Model

VPC has **two execution contexts** (User Thread and Background Storage Loop):

### Context 1 — User Thread (User Path)

Serves `GetDataAsync` calls. Responsibilities:

1. Read `CachedSegments` to identify coverage and compute true gaps
2. Fetch each gap synchronously from `IDataSource` (only what is needed)
3. Assemble the response in-memory (local to the user thread; no shared state written)
4. Publish a `CacheNormalizationRequest` (fire-and-forget) to the background queue
5. Return immediately — does not wait for background processing

The User Path is **strictly read-only** with respect to cache state (Invariant VPC.A.11). No eviction, no storage writes, no statistics updates occur on the user thread.

### Context 2 — Background Storage Loop

Single background task that dequeues `CacheNormalizationRequest`s in **strict FIFO order**. Responsibilities (four steps per event, Invariant VPC.B.3):

1. **Update metadata** — call `engine.UpdateMetadata(usedSegments)` → `selector.UpdateMetadata(...)`
2. **Store** — add fetched data as new segment(s); call `engine.InitializeSegment(segment)` per segment; call `storage.TryNormalize(out expiredSegments)` to flush the append buffer and discover TTL-expired segments
3. **Evaluate + execute eviction** — call `engine.EvaluateAndExecute(allSegments, justStored)`; only if new data was stored
4. **Post-removal** — call `storage.TryRemove(segment)` and `engine.OnSegmentRemoved(segment)` per evicted segment

**Single writer:** This is the sole context that mutates `CachedSegments`. There is no separate TTL Loop — TTL expiration is a timestamp check performed by the Background Path during `TryNormalize`.

**No supersession:** Every event is processed. VPC does not implement latest-intent-wins. This is required for metadata accuracy (e.g., LRU `LastAccessedAt` depends on every access being recorded in order — Invariant VPC.B.1a).

**No I/O:** The Background Storage Loop never calls `IDataSource`. Data is always delivered by the User Path's event payload.

---

## FIFO vs. Latest-Intent-Wins

| Property          | VisitedPlacesCache (VPC)         | SlidingWindowCache (SWC)            |
|-------------------|----------------------------------|-------------------------------------|
| Event processing  | FIFO — every event processed     | Latest-intent-wins (supersession)   |
| Burst behavior    | Events accumulate; all processed | Only the latest intent is executed  |
| Metadata accuracy | Every access recorded            | Intermediate accesses may be lost   |
| Background I/O    | None (User Path delivers data)   | Background fetches from IDataSource |
| Cache structure   | Non-contiguous segments          | Single contiguous window            |
| Eviction          | Pluggable policies + selectors   | Trim/reset on rebalance             |

**Why FIFO is required in VPC:** Eviction metadata depends on processing every access event in order. Under LRU, skipping an access event would mark a heavily-used segment as less recently accessed, causing it to be incorrectly evicted before a rarely-used segment. Supersession is safe in SWC because it manages geometry (not per-segment metadata) and discards intermediate access positions that the latest intent supersedes.

---

## Single-Writer Details

**Write ownership:** Only `CacheNormalizationExecutor` (Background Storage Loop) adds or removes segments from `CachedSegments`. TTL-driven removal also runs on the Background Storage Loop (via `TryNormalize`), so there is a single writer at all times.

**Read safety:** The User Path reads `CachedSegments` without locks because:
- Storage strategy transitions are atomic (snapshot swap or linked-list pointer update)
- No partial states are visible — a segment is either fully present (with valid data and metadata) or absent
- The Background Storage Loop is the sole writer; reads never contend with writes

**TTL coordination:** When a segment's TTL has expired, `FindIntersecting` filters it from results immediately (lazy expiration on read). The Background Path physically removes it during the next `TryNormalize` pass. If a segment is evicted by a capacity policy before `TryNormalize` discovers its TTL has expired, `TryRemove()` returns `false` for the second caller (no-op). See Invariant VPC.T.1.

---

## Eventual Consistency Model

Cache state converges asynchronously:

1. User Path returns correct data immediately (from cache or `IDataSource`) and classifies as `FullHit`, `PartialHit`, or `FullMiss`
2. User Path publishes a `CacheNormalizationRequest` (fire-and-forget)
3. Background Loop processes the event: updates metadata, stores new data, runs eviction
4. Cache converges to a state reflecting all past accesses and enforcing all capacity limits

**Key insight:** User always receives correct data regardless of background state. The cache is always in a valid (though possibly suboptimal) state from the user's perspective.

---

## Consistency Modes

Two opt-in consistency modes layer on top of eventual consistency:

| Mode     | Method                       | Waits for idle? | When to use                               |
|----------|------------------------------|-----------------|-------------------------------------------|
| Eventual | `GetDataAsync`               | Never           | Normal operation                          |
| Strong   | `GetDataAndWaitForIdleAsync` | Always          | Cold-start synchronization, test teardown |

**Serialized access requirement for Strong:** `GetDataAndWaitForIdleAsync` provides its warm-cache guarantee only under serialized (one-at-a-time) access. Under parallel callers, `WaitForIdleAsync`'s "was idle at some point" semantics (Invariant S.H.3) may return before all concurrent events are processed. The method is always safe (no deadlocks, no data corruption) but the guarantee degrades under parallelism. See Invariant VPC.D.5.

**Note:** VPC does not have a hybrid consistency mode (`GetDataAndWaitOnMissAsync`) because VPC does not have a "hit means cache is warm" semantic — a hit on one segment does not imply the cache is warm for adjacent ranges. Only strong consistency (`WaitForIdleAsync`) is meaningful in VPC.

---

## Disposal Architecture

`VisitedPlacesCache` implements `IAsyncDisposable`. Disposal uses a three-state, lock-free pattern:

```
0 = Active → 1 = Disposing → 2 = Disposed

Transitions:
  0→1: First DisposeAsync() call wins via Interlocked.CompareExchange
  1→2: Disposal sequence completes

Concurrent calls:
  First (0→1): Performs actual disposal
  Concurrent (1): Spin-wait until TCS is published, then await it
  Subsequent (2): Return immediately (idempotent)
```

**Disposal sequence:**

```
VisitedPlacesCache.DisposeAsync()
  └─> UserRequestHandler.DisposeAsync()
      └─> ISerialWorkScheduler.DisposeAsync()
          ├─> Unbounded: await task chain completion
          └─> Bounded: complete channel writer + await loop
```

The normalization scheduler is drained to completion before disposal returns. Because there is no separate TTL Loop, no additional teardown is required — all background activity halts when the scheduler is drained.

Post-disposal: all public methods throw `ObjectDisposedException` (checked via `Volatile.Read(ref _disposeState) != 0`).

See `docs/shared/invariants.md` group S.J for formal disposal invariants.

---

## Multi-Layer Caches

`VisitedPlacesCache` is designed to participate as a layer in a mixed-type layered cache stack — not as a standalone outer cache, but as a deep inner buffer that absorbs random-access misses from outer `SlidingWindowCache` layers.

**Typical role:** VPC as the innermost layer (L3 random-access absorber) with one or more SWC layers above it as sequential buffers. This arrangement lets the outer SWC layers handle sequential-access bursts efficiently while VPC accumulates and retains data across non-contiguous access patterns.

**Example — three-layer mixed stack** (see `README.md` for the full code example):

```
User request
   ↓
SlidingWindowCache (L1, small 0.5-unit window, user-facing, Snapshot)
   ↓ miss
SlidingWindowCache (L2, large 10-unit buffer, CopyOnRead)
   ↓ miss
VisitedPlacesCache (L3, random-access absorber, MaxSegmentCount=200, LRU)
   ↓ miss
IDataSource (real data source)
```

Key types in `Intervals.NET.Caching`:
- **`RangeCacheDataSourceAdapter`** — adapts any `IRangeCache` as an `IDataSource`
- **`LayeredRangeCacheBuilder`** — wires layers via `AddVisitedPlacesLayer(...)` and `AddSlidingWindowLayer(...)` extension methods; returns a `LayeredRangeCache`
- **`LayeredRangeCache`** — delegates `GetDataAsync` to the outermost layer; awaits all layers outermost-first on `WaitForIdleAsync`

### Cascading Miss

When L1 misses a range, it fetches from L2's `GetDataAsync`. L2's User Path either hits its own segments or fetches from L3/`IDataSource`. Each miss publishes a `CacheNormalizationRequest` on the respective layer's Background Loop.

**No burst resistance:** Unlike SWC, VPC does not suppress intermediate requests. A burst of L1 misses in the same range triggers one L2 miss per L1 miss. Mitigation: use sufficient L2 capacity so L1 misses amortize over many L2 hits.

---

## See Also

- `docs/shared/architecture.md` — shared principles: single-writer, user-path-never-blocks, `AsyncActivityCounter`, disposal
- `docs/visited-places/invariants.md` — formal invariant groups VPC.A–VPC.T
- `docs/visited-places/actors.md` — actor catalog and execution context summary
- `docs/visited-places/scenarios.md` — temporal scenario walkthroughs
- `docs/visited-places/eviction.md` — eviction architecture (policy-pressure-selector model)
- `docs/visited-places/storage-strategies.md` — storage strategy internals
- `docs/visited-places/components/overview.md` — component catalog and source file map
