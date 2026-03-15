# Scenarios — VisitedPlaces Cache

This document describes the temporal behavior of `VisitedPlacesCache`: what happens over time when user requests occur, background events are processed, and eviction runs.

Canonical term definitions: `docs/visited-places/glossary.md`. Formal invariants: `docs/visited-places/invariants.md`.

---

## Motivation

Component maps describe "what exists"; scenarios describe "what happens". Scenarios are the fastest way to debug behavior because they connect public API calls to background convergence.

---

## Design

Scenarios are grouped by path:

1. **User Path** (user thread)
2. **Background Path** (background storage loop)
3. **Eviction**
4. **Concurrency**
5. **TTL**

---

## Request Lifecycle Overview

The following diagram shows the full flow of a single `GetDataAsync` call — from the user thread through to background convergence. Scenarios I–V each describe one or more segments of this flow in detail.

```
User Thread
───────────────────────────────────────────────────────────────────────────────
  GetDataAsync(range)
       │
       ├─ Find intersecting segments in CachedSegments (read-only)
       │
       ├─ [FullHit]      All data found in cache ──────────────────────────────┐
       │                                                                       │
       ├─ [PartialHit]   Fetch gap sub-ranges from IDataSource (sync) ─────────┤
       │                                                                       │
       └─ [FullMiss]     Fetch entire range from IDataSource (sync) ───────────┤
                                                                               │
       Assemble and return RangeResult to user                                 │
                                                                               │
       Publish CacheNormalizationRequest { UsedSegments, FetchedData? } ───────┘
       (fire-and-forget; user thread returns immediately)

Background Storage Loop                                             [FIFO queue]
────────────────────────────────────────────────────────────────────────────────
  Dequeue CacheNormalizationRequest
       │
       ├─ engine.UpdateMetadata(UsedSegments)    [always; no-op when empty]
       │
       ├─ [FetchedData != null]
       │    ├─ storage.Store(newSegment)
       │    ├─ engine.InitializeSegment(newSegment)
       │    └─ engine.EvaluateAndExecute(allSegments, justStoredSegments)
       │         ├─ [no policy fires]  → done
       │         └─ [policy fires]
       │              ├─ build immune set (justStoredSegments)
       │              ├─ loop: TrySelectCandidate → pressure.Reduce(candidate)
       │              │        until all constraints satisfied
       │              └─ storage.Remove(evicted); engine.OnSegmentRemoved(evicted)
       │
       └─ [FetchedData == null]  → done (stats-only event; no eviction)

TTL Loop (only when SegmentTtl is configured)         [fire-and-forget per segment]
───────────────────────────────────────────────────────────────────────────────
  After storage.Store(segment):
       Schedule TtlExpirationWorkItem → Task.Delay(SegmentTtl)
            │
            └─ On delay fire: segment.MarkAsRemoved()
                 ├─ [returns true]  → storage.Remove; engine.OnSegmentRemoved; TtlSegmentExpired
                 └─ [returns false] → segment already removed by eviction; no-op
```

**Reading the scenarios**: Each scenario in sections I–V corresponds to one or more steps in this diagram. Scenarios U1–U5 focus on the user thread portion; B1–B5 focus on the background storage loop; E1–E6 focus on the `EvaluateAndExecute` branch; T1–T3 focus on the TTL loop.

---

## I. User Path Scenarios

### U1 — Cold Cache Request (Empty Cache)

**Preconditions**:
- `CachedSegments == empty`

**Action Sequence**:
1. User requests `RequestedRange`
2. User Path checks `CachedSegments` — no segment covers any part of `RequestedRange`
3. User Path fetches `RequestedRange` from `IDataSource` synchronously (unavoidable — user request must be served immediately)
4. Data is returned to the user — `RangeResult.CacheInteraction == FullMiss`
5. A `CacheNormalizationRequest` is published (fire-and-forget): `{ UsedSegments: [], FetchedData: <fetched range data>, RequestedRange }`
6. Background Path stores the fetched data as a new `Segment` in `CachedSegments`

**Note**: The User Path does not store data itself. Cache writes are exclusively the responsibility of the Background Path (Single-Writer rule, Invariant VPC.A.1).

---

### U2 — Full Cache Hit (Single Segment)

**Preconditions**:
- `CachedSegments` contains at least one segment `S` where `S.Range.Contains(RequestedRange) == true`

**Action Sequence**:
1. User requests `RequestedRange`
2. User Path finds `S` via binary search (or stride index + linear scan, strategy-dependent)
3. Subrange is read from `S.Data`
4. Data is returned to the user — `RangeResult.CacheInteraction == FullHit`
5. A `CacheNormalizationRequest` is published: `{ UsedSegments: [S], FetchedData: null, RequestedRange }`
6. Background Path calls `engine.UpdateMetadata([S])` → `selector.UpdateMetadata(...)` — e.g., LRU selector updates `S.LruMetadata.LastAccessedAt`

**Note**: No `IDataSource` call is made. No eviction is triggered on stats-only events (eviction is only evaluated after new data is stored).

---

### U3 — Full Cache Hit (Multi-Segment Assembly)

**Preconditions**:
- No single segment in `CachedSegments` contains `RequestedRange`
- The union of two or more segments in `CachedSegments` fully covers `RequestedRange` with no gaps

**Action Sequence**:
1. User requests `RequestedRange`
2. User Path identifies all segments whose ranges intersect `RequestedRange`
3. User Path verifies that the union of intersecting segments covers `RequestedRange` completely (no gaps within `RequestedRange`)
4. Relevant subranges are read from each contributing segment and assembled in-memory
5. Data is returned to the user — `RangeResult.CacheInteraction == FullHit`
6. A `CacheNormalizationRequest` is published: `{ UsedSegments: [S₁, S₂, ...], FetchedData: null, RequestedRange }`
7. Background Path calls `engine.UpdateMetadata([S₁, S₂, ...])` → `selector.UpdateMetadata(...)` for each contributing segment

**Note**: Multi-segment assembly is a core VPC capability. The assembled data is never stored as a merged segment (merging is not performed). Each source segment remains independent in `CachedSegments`.

---

### U4 — Partial Cache Hit (Gap Fetch)

**Preconditions**:
- Some portion of `RequestedRange` is covered by one or more segments in `CachedSegments`
- At least one sub-range within `RequestedRange` is NOT covered by any cached segment (a true gap)

**Action Sequence**:
1. User requests `RequestedRange`
2. User Path identifies all cached segments intersecting `RequestedRange` and computes the uncovered sub-ranges (gaps)
3. Each gap sub-range is synchronously fetched from `IDataSource`
4. Cached data (from existing segments) and newly fetched data (from gaps) are assembled in-memory
5. Data is returned to the user — `RangeResult.CacheInteraction == PartialHit`
6. A `CacheNormalizationRequest` is published: `{ UsedSegments: [S₁, ...], FetchedData: <gap data>, RequestedRange }`
7. Background Path updates statistics for used segments AND stores gap data as new segment(s)

**Note**: The User Path performs only the minimum fetches needed to serve `RequestedRange`. In-memory assembly is local only — no cache writes occur on the user thread.

**Consistency note**: `GetDataAndWaitForIdleAsync` will call `WaitForIdleAsync` after this scenario, waiting for background storage and statistics updates to complete.

---

### U5 — Full Cache Miss (No Overlap)

**Preconditions**:
- No segment in `CachedSegments` intersects `RequestedRange`

**Action Sequence**:
1. User requests `RequestedRange`
2. User Path finds no intersecting segments
3. `RequestedRange` is synchronously fetched from `IDataSource`
4. Data is returned to the user — `RangeResult.CacheInteraction == FullMiss`
5. A `CacheNormalizationRequest` is published: `{ UsedSegments: [], FetchedData: <fetched range data>, RequestedRange }`
6. Background Path stores fetched data as a new `Segment` in `CachedSegments`

**Key difference from SWC**: Unlike SlidingWindowCache, VPC does NOT discard existing cached segments on a full miss. Existing segments remain intact; only the new data for `RequestedRange` is added. There is no contiguity requirement enforcing a full cache reset.

**Consistency note**: `GetDataAndWaitForIdleAsync` will call `WaitForIdleAsync` after this scenario, waiting for background storage to complete.

---

## II. Background Path Scenarios

**Core principle**: The Background Path is the sole writer of cache state. It processes `CacheNormalizationRequest`s in strict FIFO order (no supersession). Each request triggers four steps: (1) metadata update, (2) storage, (3) eviction evaluation + execution, (4) post-removal. See `docs/visited-places/architecture.md` — Threading Model, Context 2 for the authoritative description.

---

### B1 — Stats-Only Event (Full Hit)

**Preconditions**:
- Event has `UsedSegments: [S₁, ...]`, `FetchedData: null`

**Sequence**:
1. Background Path dequeues the event
2. `engine.UpdateMetadata([S₁, ...])` → `selector.UpdateMetadata(...)` — selector updates metadata for each used segment
   - LRU: sets `LruMetadata.LastAccessedAt` to current time on each
   - FIFO / SmallestFirst: no-op
3. No storage step (no new data)
4. No eviction evaluation (eviction is only triggered after storage)

**Rationale**: Eviction should not be triggered by reads alone. Triggering on reads could cause thrashing in heavily-accessed caches that never add new data.

---

### B2 — Store New Segment (No Eviction Triggered)

**Preconditions**:
- Event has `FetchedData: <range data>` (may or may not have `UsedSegments`)
- No Eviction Policy fires after storage

**Sequence**:
1. Background Path dequeues the event
2. If `UsedSegments` is non-empty: `engine.UpdateMetadata(usedSegments)` → `selector.UpdateMetadata(...)`
3. Store `FetchedData` as a new `Segment` in `CachedSegments`
   - Segment is added in sorted order (or appended to the strategy's append buffer)
   - `engine.InitializeSegment(segment)` — e.g., `LruMetadata { LastAccessedAt = <now> }`, `FifoMetadata { CreatedAt = <now> }`, `SmallestFirstMetadata { Span = <computed> }`, etc.
4. `engine.EvaluateAndExecute(allSegments, justStored)` — no policy constraint exceeded; returns empty list
5. Processing complete; cache now has one additional segment

**Note**: The just-stored segment always has **immunity** — it is never eligible for eviction in the same processing step in which it was stored (Invariant VPC.E.3).

---

### B3 — Store New Segment (Eviction Triggered)

**Preconditions**:
- Event has `FetchedData: <range data>`
- At least one Eviction Policy fires after storage (e.g., segment count exceeds limit)

**Sequence**:
1. Background Path dequeues the event
2. If `UsedSegments` is non-empty: `engine.UpdateMetadata(usedSegments)` → `selector.UpdateMetadata(...)`
3. Store `FetchedData` as a new `Segment` in `CachedSegments`; `engine.InitializeSegment(segment)` attaches fresh metadata and notifies stateful policies
4. `engine.EvaluateAndExecute(allSegments, justStored)` — at least one policy fires:
   - Executor builds immune set from `justStoredSegments`
   - Executor loops: `selector.TrySelectCandidate(allSegments, immune, out candidate)` → `pressure.Reduce(candidate)` until satisfied
   - Engine returns `toRemove` list
5. Processor removes evicted segments from storage; calls `engine.OnSegmentRemoved(segment)` per removed segment
6. Cache returns to within-policy state

**Note**: Multiple policies may fire simultaneously. The Eviction Executor runs once per event (not once per fired policy), using `CompositePressure` to satisfy all constraints simultaneously.

---

### B4 — Multi-Gap Event (Partial Hit with Multiple Fetched Ranges)

**Preconditions**:
- User Path fetched multiple disjoint gap ranges from `IDataSource` to serve a `PartialHit`
- Event has `UsedSegments: [S₁, ...]` and `FetchedData: <multiple gap ranges>`
- `FetchedChunks.Count > 1` (two or more gap chunks in the request)

**Sequence**:
1. Background Path dequeues the event
2. Update metadata for used segments: `engine.UpdateMetadata(usedSegments)`
3. `CacheNormalizationExecutor` detects `FetchedChunks.Count > 1` and dispatches to `StoreBulkAsync`:
   - Validate and wrap all fetched chunks into `CachedSegment` instances (`ValidateChunks`)
   - Call `storage.AddRange(segments[])` — all N gap segments inserted in a single structural update
   - For each stored segment: `engine.InitializeSegment(segment)` — attaches fresh metadata and notifies stateful policies
4. `engine.EvaluateAndExecute(allSegments, justStoredSegments)` — `justStoredSegments` contains **all** segments from the bulk store; all are immune from eviction in this cycle (see VPC.E.3)
5. If any policy fires: processor removes returned segments; calls `engine.OnSegmentRemoved(segment)` per removed segment

**Why `AddRange` instead of N × `Add`:** For `SnapshotAppendBufferStorage`, N calls to `Add()` can trigger up to ⌈N/AppendBufferSize⌉ normalization passes, each O(n) — quadratic total cost for large caches with many gaps. `AddRange` performs a single O(n + N log N) structural update regardless of N. See `docs/visited-places/storage-strategies.md` — Bulk Storage: AddRange.

**Note**: Gaps are stored as distinct segments. Segments are never merged, even when adjacent. Each independently-fetched sub-range occupies its own entry in `CachedSegments`. This preserves independent statistics per fetched unit.

---

### B5 — FIFO Event Processing Order

**Situation**:
- User requests U₁, U₂, U₃ in rapid sequence, each publishing events E₁, E₂, E₃

**Sequence**:
1. E₁ is dequeued and fully processed (stats + storage + eviction if needed)
2. E₂ is dequeued and fully processed
3. E₃ is dequeued and fully processed

**Key difference from SWC**: There is no "latest wins" supersession. Every event is processed. E₂ cannot skip E₁, and E₃ cannot skip E₂. The Background Path provides a total ordering over all cache mutations.

**Rationale**: See `docs/visited-places/architecture.md` — FIFO vs. Latest-Intent-Wins.

---

## III. Eviction Scenarios

### E1 — Policy Fires: Max Segment Count Exceeded

**Configuration**:
- Policy: `MaxSegmentCountPolicy(maxCount: 10)`
- Selector strategy: LRU

**Sequence**:
1. Background Path stores a new segment, bringing total count to 11
2. `engine.EvaluateAndExecute`: `MaxSegmentCountPolicy` fires (`CachedSegments.Count (11) > maxCount (10)`)
3. Eviction Engine + LRU Selector:
   - Executor builds immune set (the just-stored segment)
   - LRU Selector samples O(SampleSize) eligible segments; selects the one with the smallest `LruMetadata.LastAccessedAt`
   - Executor calls `pressure.Reduce(candidate)`; `SegmentCountPressure.IsExceeded` becomes `false`
4. Processor removes the selected segment from storage; `engine.OnSegmentRemoved(candidate)`
5. Total segment count returns to 10

**Post-condition**: All remaining segments are valid cache entries with up-to-date metadata.

---

### E2 — Multiple Policies, One Fires

**Configuration**:
- Policy A: `MaxSegmentCountPolicy(maxCount: 10)`
- Policy B: `MaxTotalSpanPolicy(maxTotalSpan: 1000 units)`
- Selector strategy: FIFO

**Preconditions**:
- `CachedSegments.Count == 9` (below count limit)
- Total span of all segments = 950 units (below span limit)

**Action**:
- New segment of span 60 units is stored → `Count = 10`, total span = 1010 units

**Sequence**:
1. `MaxSegmentCountPolicy` checks: `10 ≤ 10` → does NOT fire
2. `MaxTotalSpanPolicy` checks: `1010 > 1000` → FIRES
3. `engine.EvaluateAndExecute`: FIFO Selector invoked:
   - Executor builds immune set (the just-stored segment)
   - FIFO Selector samples O(SampleSize) eligible segments; selects the one with the smallest `FifoMetadata.CreatedAt`
   - Executor calls `pressure.Reduce(candidate)` — total span drops
4. If total span still exceeds limit, executor continues sampling until all constraints are satisfied

---

### E3 — Multiple Policies, Both Fire

**Configuration**:
- Policy A: `MaxSegmentCountPolicy(maxCount: 10)`
- Policy B: `MaxTotalSpanPolicy(maxTotalSpan: 1000 units)`
- Selector strategy: smallest-first

**Action**:
- New segment stored → `Count = 12`, total span = 1200 units (both limits exceeded)

**Sequence**:
1. Both policies fire
2. `engine.EvaluateAndExecute` is invoked once with a `CompositePressure`
3. Executor + SmallestFirst Selector must satisfy BOTH constraints simultaneously:
   - Executor builds immune set (the just-stored segment)
   - SmallestFirst Selector samples O(SampleSize) eligible segments; selects the one with the smallest `Range.Span(domain)`
   - Executor calls `pressure.Reduce(candidate)`; loop continues until `Count ≤ 10` AND `total span ≤ 1000`
4. Executor performs a single pass — not one pass per fired policy

**Rationale**: Single-pass eviction is more efficient and avoids redundant iterations over `CachedSegments`.

---

### E4 — Just-Stored Segment Immunity

**Preconditions**:
- `CachedSegments` contains segments `S₁, S₂, S₃, S₄` (count limit = 4)
- A new segment `S₅` (the just-stored one) is about to be added, triggering eviction

**Sequence**:
1. `S₅` is stored — count becomes 5, exceeding limit
2. `engine.EvaluateAndExecute` is invoked; executor builds immune set: `{S₅}`
3. Executor calls `selector.TrySelectCandidate(allSegments, {S₅}, out candidate)` — samples from `{S₁, S₂, S₃, S₄}`; selects appropriate candidate per strategy
4. Selected candidate is removed from storage; count returns to 4

**Rationale**: Without immunity, a newly-stored segment could be immediately evicted (e.g., by LRU since its `LruMetadata.LastAccessedAt` is `now` — but it is the most recently initialized, not most recently accessed by a user). The just-stored segment represents data just fetched from `IDataSource`; evicting it immediately would cause an infinite fetch loop.

---

### E5 — Eviction with FIFO Strategy

**State**: `CachedSegments = [S₁(created: t=1), S₂(created: t=3), S₃(created: t=2)]`
**Trigger**: Count exceeds limit after storing `S₄`

**Sequence**:
1. `S₄` stored; `engine.InitializeSegment(S₄)` attaches `FifoMetadata { CreatedAt = <now> }`; immunity applies to `S₄`
2. `engine.EvaluateAndExecute`: executor builds immune set `{S₄}`; FIFO Selector samples eligible candidates `{S₁, S₂, S₃}` and selects the one with the smallest `CreatedAt` — `S₁(t=1)`
3. Processor removes `S₁` from storage; count returns to limit

---

### E6 — Eviction with LRU Strategy

**State**: `CachedSegments = [S₁(lastAccessed: t=5), S₂(lastAccessed: t=1), S₃(lastAccessed: t=8)]`
**Trigger**: Count exceeds limit after storing `S₄`

**Sequence**:
1. `S₄` stored; `engine.InitializeSegment(S₄)` attaches `LruMetadata { LastAccessedAt = <now> }`; immunity applies to `S₄`
2. `engine.EvaluateAndExecute`: executor builds immune set `{S₄}`; LRU Selector samples eligible candidates `{S₁, S₂, S₃}` and selects the one with the smallest `LastAccessedAt` — `S₂(t=1)`
3. Processor removes `S₂` from storage; count returns to limit

---

## IV. Concurrency Scenarios

### Concurrency Principles

1. User Path is read-only with respect to cache state; it never blocks on background work.
2. Background Path is the sole writer of cache state (Single-Writer rule).
3. Events are produced by the User Path and consumed by the Background Path in FIFO order.
4. Multiple User Path calls may overlap in time; each independently publishes its event.
5. Cache state is always consistent from the User Path's perspective (reads are atomic; no partial state visible).

---

### C1 — Concurrent User Requests (Parallel Reads)

**Situation**:
- Two user threads call `GetDataAsync` concurrently: U₁ requesting `[10, 20]`, U₂ requesting `[30, 40]`
- Both ranges are fully covered by existing segments

**Expected Behavior**:
1. U₁ and U₂ execute their User Path reads concurrently — no serialization between them
2. Both read from `CachedSegments` simultaneously (User Path is read-only; concurrent reads are safe)
3. U₁ publishes event E₁ (fire-and-forget); U₂ publishes event E₂ (fire-and-forget)
4. Background Path processes E₁ then E₂ (or E₂ then E₁, depending on queue order)
5. Both sets of statistics updates are applied

**Note**: Concurrent user reads are safe because the User Path is read-only. The order of E₁ and E₂ in the background queue depends on which `GetDataAsync` call enqueued first.

---

### C2 — User Request While Background Is Processing

**Situation**:
- Background Path is processing event E₁ (storing a new segment)
- A new user request U₂ arrives concurrently

**Expected Behavior**:
1. U₂ reads `CachedSegments` on the User Path — reads the version of state prior to E₁'s storage completing (safe; the user sees a consistent snapshot)
2. U₂ publishes event E₂ to the background queue (after E₁)
3. Background Path finishes processing E₁ (storage complete)
4. Background Path processes E₂

**Note**: The User Path never waits for the Background Path to finish. U₂'s read is guaranteed safe because cache state transitions are atomic (storage is not partially visible).

---

### C3 — Rapid Sequential Requests (Accumulating Events)

**Situation**:
- User produces a burst of requests: U₁, U₂, ..., Uₙ in rapid succession
- Each request publishes an event; Background Path processes them in order

**Expected Behavior**:
1. User Path serves all requests independently and immediately
2. Each request publishes its event to the background queue — NO supersession
3. Background Path drains the queue in FIFO order: E₁, E₂, ..., Eₙ
4. Eviction metadata is updated accurately (every access recorded in the correct FIFO order)
5. Eviction policies are checked after each storage event (not batched)

**Key difference from SWC**: In SWC, a burst of requests results in only the latest intent being executed (supersession). In VPC, every event is processed. See `docs/visited-places/architecture.md` — FIFO vs. Latest-Intent-Wins for the rationale.

**Outcome**: Cache converges to an accurate eviction metadata state reflecting all accesses in order. Eviction decisions are based on complete access history.

---

### C4 — WaitForIdleAsync Semantics Under Concurrency

**Situation**:
- Multiple parallel `GetDataAsync` calls are active; caller also calls `WaitForIdleAsync`

**Expected Behavior**:
1. `WaitForIdleAsync` completes when the activity counter reaches zero — meaning the background was idle **at some point**
2. New background activity may begin immediately after `WaitForIdleAsync` returns if new requests arrive concurrently
3. Under parallel access, the "idle at some point" guarantee does NOT imply that all events from all parallel callers have been processed

**Correct use**: Waiting for background convergence in single-caller scenarios (tests, strong consistency extension).

**Incorrect use**: Assuming the cache is fully quiescent after `await WaitForIdleAsync()` when multiple callers are active concurrently.

**Consistency note**: `GetDataAndWaitForIdleAsync` (strong consistency extension) provides its warm-cache guarantee reliably only under serialized (one-at-a-time) access. See `docs/shared/glossary.md` for formal semantics.

---

---

## V. TTL Scenarios

**Core principle**: When `VisitedPlacesCacheOptions.SegmentTtl` is non-null, each stored segment has a `TtlExpirationWorkItem` scheduled immediately after storage. The TTL actor awaits the delay fire-and-forget on the thread pool, then calls `segment.MarkAsRemoved()` — if it returns `true` (first caller), it removes the segment directly from storage and notifies the eviction engine. TTL expiration is idempotent: if the segment was already evicted by a capacity policy, `MarkAsRemoved()` returns `false` and the removal is a no-op.

---

### T1 — TTL Expiration (Segment Expires Before Eviction)

**Configuration**:
- `SegmentTtl = TimeSpan.FromSeconds(30)`
- Capacity policies: not exceeded at expiry time

**Preconditions**:
- Segment `S₁` was stored at `t=0`; a `TtlExpirationWorkItem` was scheduled for `t=30s`

**Sequence**:
1. TTL actor dequeues the work item at `t=0` and fires `Task.Delay(30s)` independently on the thread pool
2. At `t=30s`, the delay completes
3. TTL actor calls `S₁.MarkAsRemoved()` — returns `true` (first caller; segment is still present)
4. TTL actor calls `_storage.Remove(S₁)` — segment physically removed from storage
5. TTL actor calls `_engine.OnSegmentRemoved(S₁)` — notifies policies
6. `_diagnostics.TtlSegmentExpired()` is fired
7. `S₁` is no longer returned by `FindIntersecting`; subsequent user requests for its range incur a cache miss

**Note**: The User Path sees the removal atomically — `S₁` is either present or absent; no partial state is visible. The Background Storage Loop is unaffected; it continues processing normalization events in parallel.

---

### T2 — TTL Fires After Eviction (Idempotency)

**Configuration**:
- `SegmentTtl = TimeSpan.FromSeconds(60)`
- A capacity policy evicts `S₁` at `t=5s` (before its TTL)

**Sequence**:
1. At `t=5s`, eviction removes `S₁` via `CacheNormalizationExecutor`:
   - `S₁.MarkAsRemoved()` called — sets `_isRemoved = 1`, returns `true`
   - `_storage.Remove(S₁)` called; `engine.OnSegmentsRemoved([S₁])` notified
2. At `t=60s`, the TTL work item fires and calls `S₁.MarkAsRemoved()`:
   - Returns `false` (another caller already set the flag)
   - TTL actor skips `storage.Remove` and `engine.OnSegmentsRemoved` entirely
3. `_diagnostics.TtlSegmentExpired()` is NOT fired — `TryRemove` returned `false` (segment already removed by eviction).

**Invariant enforced**: VPC.T.1 — TTL expiration is idempotent.

---

### T3 — Disposal Cancels Pending TTL Delays

**Situation**:
- Cache has 3 segments `S₁, S₂, S₃` with `SegmentTtl = 10 minutes`; all TTL work items are mid-delay
- `DisposeAsync` is called

**Sequence**:
1. `DisposeAsync` drains the normalization scheduler (`await _userRequestHandler.DisposeAsync()`)
2. `DisposeAsync` disposes the TTL scheduler (`await _ttlScheduler.DisposeAsync()`):
   - TTL scheduler cancels its `CancellationToken`
   - All pending `Task.Delay` calls throw `OperationCanceledException`
   - `TtlExpirationExecutor` catches the cancellation and exits cleanly (no unhandled exception)
3. `DisposeAsync` returns; no TTL work items are left running

**Invariant enforced**: VPC.T.3 — pending TTL delays are cancelled on disposal.

---

## Invariants

Scenarios must be consistent with:

- User Path invariants: `docs/visited-places/invariants.md` (Section VPC.A)
- Background Path invariants: `docs/visited-places/invariants.md` (Section VPC.B)
- Storage invariants: `docs/visited-places/invariants.md` (Section VPC.C)
- Eviction invariants: `docs/visited-places/invariants.md` (Section VPC.E)
- TTL invariants: `docs/visited-places/invariants.md` (Section VPC.T)
- Shared activity tracking invariants: `docs/shared/invariants.md` (Section S.H)

---

## Usage

Use scenarios as a debugging checklist:

1. What did the user call?
2. What was returned (`FullHit`, `PartialHit`, or `FullMiss`)?
3. What event was published? (`UsedSegments`, `FetchedData`, `RequestedRange`)
4. Did the Background Path update statistics? Store new data? Trigger eviction?
5. If eviction ran: which policy fired? Which selector strategy was applied? Which segment was sampled as the worst candidate?
6. Was there a concurrent read? Did it see a consistent cache snapshot?

---

## Edge Cases

- A cache can be non-optimal (stale metadata, suboptimal eviction candidates) between background events; eventual convergence is expected.
- `WaitForIdleAsync` indicates the system was idle at some point, not that it remains idle.
- In Scenario U3, multi-segment assembly requires that the union of segments covers `RequestedRange` with NO gaps. If even one gap exists, the scenario degrades to U4 (Partial Hit).
- In Scenario B3, if the just-stored segment is the only segment (cache was empty before storage), eviction cannot proceed — the policy fires but `TrySelectCandidate` returns `false` immediately (all segments are immune), so the eviction pass is a no-op (the cache cannot evict its only segment; it will remain over-limit until the next storage event adds another eligible candidate).
- Segments are never merged, even if two adjacent segments together span a contiguous range. Merging would reset the eviction metadata of one of the segments and complicate eviction decisions.

---

## See Also

- `docs/visited-places/actors.md` — actor responsibilities per scenario
- `docs/visited-places/invariants.md` — formal invariants
- `docs/visited-places/eviction.md` — eviction architecture (policy-pressure-selector model, strategy catalog)
- `docs/visited-places/storage-strategies.md` — storage internals (append buffer, normalization, stride index)
- `docs/shared/glossary.md` — shared term definitions (WaitForIdleAsync, CacheInteraction, etc.)
