# Scenarios — VisitedPlaces Cache

This document describes the temporal behavior of `VisitedPlacesCache`: what happens over time when user requests occur, background events are processed, and eviction runs.

Canonical term definitions: `docs/visited-places/glossary.md` (to be written). Formal invariants: `docs/visited-places/invariants.md` (to be written).

---

## Motivation

Component maps describe "what exists"; scenarios describe "what happens". Scenarios are the fastest way to debug behavior because they connect public API calls to background convergence.

---

## Base Definitions

- **RequestedRange** — A range requested by the user.
- **CachedSegments** — The collection of non-contiguous cached segments currently stored in the cache.
- **Segment** — A single contiguous range with its associated data, stored in `CachedSegments`.
- **EvictionMetadata** — Per-segment metadata owned by the configured Eviction Selector (`IEvictionMetadata?` on each `CachedSegment`). Selector-specific: `LruMetadata { LastAccessedAt }`, `FifoMetadata { CreatedAt }`, or null for selectors that need no metadata.
- **BackgroundEvent** — A message published by the User Path to the Background Path after every `GetDataAsync` call. Carries used segment references and any newly fetched data.
- **IDataSource** — A range-based data source used to fetch data absent from the cache.
- **EvictionPolicy** — Determines whether eviction should run (e.g., too many segments, too much total span). Multiple policies may be active; eviction triggers when ANY fires. Produces an `IEvictionPressure` object representing the violated constraint.
- **EvictionSelector** — Defines, creates, and updates per-segment eviction metadata. Determines the order in which candidates are considered for removal (LRU, FIFO, smallest-first, etc.).
- **EvictionExecutor** — Performs eviction via a constraint satisfaction loop: filters immune segments, orders candidates via the Eviction Selector, and removes them until all pressures are satisfied.

---

## Design

Scenarios are grouped by path:

1. **User Path** (user thread)
2. **Background Path** (background storage loop)
3. **Eviction**
4. **Concurrency**

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
5. A `BackgroundEvent` is published (fire-and-forget): `{ UsedSegments: [], FetchedData: <fetched range data>, RequestedRange }`
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
5. A `BackgroundEvent` is published: `{ UsedSegments: [S], FetchedData: null, RequestedRange }`
6. Background Path calls `selector.UpdateMetadata([S], now)` — e.g., LRU selector updates `S.LruMetadata.LastAccessedAt`

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
6. A `BackgroundEvent` is published: `{ UsedSegments: [S₁, S₂, ...], FetchedData: null, RequestedRange }`
7. Background Path calls `selector.UpdateMetadata([S₁, S₂, ...], now)` for each contributing segment

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
6. A `BackgroundEvent` is published: `{ UsedSegments: [S₁, ...], FetchedData: <gap data>, RequestedRange }`
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
5. A `BackgroundEvent` is published: `{ UsedSegments: [], FetchedData: <fetched range data>, RequestedRange }`
6. Background Path stores fetched data as a new `Segment` in `CachedSegments`

**Key difference from SWC**: Unlike SlidingWindowCache, VPC does NOT discard existing cached segments on a full miss. Existing segments remain intact; only the new data for `RequestedRange` is added. There is no contiguity requirement enforcing a full cache reset.

**Consistency note**: `GetDataAndWaitForIdleAsync` will call `WaitForIdleAsync` after this scenario, waiting for background storage to complete.

---

## II. Background Path Scenarios

**Core principle**: The Background Path is the sole writer of cache state. It processes `BackgroundEvent`s in strict FIFO order. No supersession — every event is processed. Each event triggers:

1. **Metadata update** — update per-segment eviction metadata for all used segments by delegating to the configured Eviction Selector (`selector.UpdateMetadata`)
2. **Storage** — store fetched data as new segment(s), if `FetchedData != null`; call `selector.InitializeMetadata(segment, now)` for each new segment
3. **Eviction evaluation** — check all configured Eviction Policies, if new data was stored
4. **Eviction execution** — if any policy produced an exceeded pressure, execute eviction via the constraint satisfaction loop (Eviction Executor + Selector)

---

### B1 — Stats-Only Event (Full Hit)

**Preconditions**:
- Event has `UsedSegments: [S₁, ...]`, `FetchedData: null`

**Sequence**:
1. Background Path dequeues the event
2. `selector.UpdateMetadata([S₁, ...], now)` — selector updates metadata for each used segment
   - LRU: sets `LruMetadata.LastAccessedAt = now` on each
   - FIFO / SmallestFirst: no-op
3. No storage step (no new data)
4. No eviction evaluation (eviction is only triggered after storage)

**Rationale**: Eviction should not be triggered by reads alone. Triggering on reads could cause thrashing in heavily-accessed caches that never add new data.

---

### B2 — Store New Segment (No Eviction Triggered)

**Preconditions**:
- Event has `FetchedData: <range data>` (may or may not have `UsedSegments`)
- No Eviction Evaluator fires after storage

**Sequence**:
1. Background Path dequeues the event
2. If `UsedSegments` is non-empty: `selector.UpdateMetadata(usedSegments, now)`
3. Store `FetchedData` as a new `Segment` in `CachedSegments`
   - Segment is added in sorted order (or appended to the strategy's append buffer)
   - `selector.InitializeMetadata(segment, now)` — e.g., `LruMetadata { LastAccessedAt = now }`, `FifoMetadata { CreatedAt = now }`, or no-op
4. Check all Eviction Policies — none fire
5. Processing complete; cache now has one additional segment

**Note**: The just-stored segment always has **immunity** — it is never eligible for eviction in the same processing step in which it was stored (Invariant VPC.E.3).

---

### B3 — Store New Segment (Eviction Triggered)

**Preconditions**:
- Event has `FetchedData: <range data>`
- At least one Eviction Evaluator fires after storage (e.g., segment count exceeds limit)

**Sequence**:
1. Background Path dequeues the event
2. If `UsedSegments` is non-empty: `selector.UpdateMetadata(usedSegments, now)`
3. Store `FetchedData` as a new `Segment` in `CachedSegments`; `selector.InitializeMetadata(segment, now)` attaches fresh metadata
4. Check all Eviction Policies — at least one fires
5. Eviction Executor is invoked:
   - Evaluates all eligible segments (excluding just-stored segment — immunity rule)
   - Passes eligible candidates to the Eviction Selector for ordering
   - Removes selected segments from `CachedSegments` until all pressures are satisfied
6. Cache returns to within-policy state

**Note**: Multiple policies may fire simultaneously. The Eviction Executor runs once per event (not once per fired policy), using `CompositePressure` to satisfy all constraints simultaneously.

---

### B4 — Multi-Gap Event (Partial Hit with Multiple Fetched Ranges)

**Preconditions**:
- User Path fetched multiple disjoint gap ranges from `IDataSource` to serve a `PartialHit`
- Event has `UsedSegments: [S₁, ...]` and `FetchedData: <multiple gap ranges>`

**Sequence**:
1. Background Path dequeues the event
2. Update statistics for used segments
3. Store each gap range as a separate new `Segment` in `CachedSegments`
   - Each stored segment is added independently; no merging with existing segments
   - `selector.InitializeMetadata(segment, now)` is called for each new segment
4. Check all Eviction Evaluators (after all new segments are stored)
5. If any evaluator fires: Eviction Executor selects and removes eligible segments

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

**Rationale**: Metadata accuracy depends on processing every access. Supersession would silently lose access events, causing incorrect eviction decisions (e.g., LRU evicting a recently-used segment).

---

## III. Eviction Scenarios

### E1 — Policy Fires: Max Segment Count Exceeded

**Configuration**:
- Policy: `MaxSegmentCountPolicy(maxCount: 10)`
- Selector strategy: LRU

**Sequence**:
1. Background Path stores a new segment, bringing total count to 11
2. `MaxSegmentCountPolicy` fires: `CachedSegments.Count (11) > maxCount (10)`
3. Eviction Executor + LRU Selector:
   - LRU Selector orders candidates ascending by `LruMetadata.LastAccessedAt`
   - Executor removes the first candidate (least recently accessed) from `CachedSegments`
4. Total segment count returns to 10

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
3. Eviction Executor + FIFO Selector:
   - FIFO Selector orders candidates ascending by `FifoMetadata.CreatedAt`
   - Executor removes the oldest segment; total span drops
4. If total span still exceeds limit after first removal, Executor removes additional segments until all constraints are satisfied

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
2. Eviction Executor is invoked once with a `CompositePressure`
3. Executor + SmallestFirst Selector must satisfy BOTH constraints simultaneously:
   - SmallestFirst Selector orders candidates ascending by `Range.Span(domain)`
   - Executor removes smallest segments first
   - Continues removing until `Count ≤ 10` AND `total span ≤ 1000`
4. Executor performs a single pass — not one pass per fired policy

**Rationale**: Single-pass eviction is more efficient and avoids redundant iterations over `CachedSegments`.

---

### E4 — Just-Stored Segment Immunity

**Preconditions**:
- `CachedSegments` contains segments `S₁, S₂, S₃, S₄` (count limit = 4)
- A new segment `S₅` (the just-stored one) is about to be added, triggering eviction

**Sequence**:
1. `S₅` is stored — count becomes 5, exceeding limit
2. Eviction Executor is invoked; eligible candidates: `{S₁, S₂, S₃, S₄}` — `S₅` is excluded
3. Executor selects the appropriate candidate from `{S₁, S₂, S₃, S₄}` per its strategy
4. Selected candidate is removed; count returns to 4

**Rationale**: Without immunity, a newly-stored segment could be immediately evicted (e.g., by LRU since its `LruMetadata.LastAccessedAt` is `now` — but it is the most recently initialized, not most recently accessed by a user). The just-stored segment represents data just fetched from `IDataSource`; evicting it immediately would cause an infinite fetch loop.

---

### E5 — Eviction with FIFO Strategy

**State**: `CachedSegments = [S₁(created: t=1), S₂(created: t=3), S₃(created: t=2)]`
**Trigger**: Count exceeds limit after storing `S₄`

**Sequence**:
1. `S₄` stored; `selector.InitializeMetadata(S₄, now)` attaches `FifoMetadata { CreatedAt = now }`; immunity applies to `S₄`
2. FIFO Selector orders eligible candidates by `FifoMetadata.CreatedAt` ascending: `[S₁(t=1), S₃(t=2), S₂(t=3)]`
3. Executor removes `S₁` (oldest `CreatedAt = t=1`); count returns to limit

---

### E6 — Eviction with LRU Strategy

**State**: `CachedSegments = [S₁(lastAccessed: t=5), S₂(lastAccessed: t=1), S₃(lastAccessed: t=8)]`
**Trigger**: Count exceeds limit after storing `S₄`

**Sequence**:
1. `S₄` stored; `selector.InitializeMetadata(S₄, now)` attaches `LruMetadata { LastAccessedAt = now }`; immunity applies to `S₄`
2. LRU Selector orders eligible candidates by `LruMetadata.LastAccessedAt` ascending: `[S₂(t=1), S₁(t=5), S₃(t=8)]`
3. Executor removes `S₂` (least recently used: `LastAccessedAt = t=1`); count returns to limit

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

**Key difference from SWC**: In SWC, a burst of requests results in only the latest intent being executed (supersession). In VPC, every event is processed — statistics accuracy requires it.

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

## Invariants

Scenarios must be consistent with:

- User Path invariants: `docs/visited-places/invariants.md` (Section VPC.A)
- Background Path invariants: `docs/visited-places/invariants.md` (Section VPC.B)
- Storage invariants: `docs/visited-places/invariants.md` (Section VPC.C)
- Eviction invariants: `docs/visited-places/invariants.md` (Section VPC.E)
- Shared activity tracking invariants: `docs/shared/invariants.md` (Section S.H)

---

## Usage

Use scenarios as a debugging checklist:

1. What did the user call?
2. What was returned (`FullHit`, `PartialHit`, or `FullMiss`)?
3. What event was published? (`UsedSegments`, `FetchedData`, `RequestedRange`)
4. Did the Background Path update statistics? Store new data? Trigger eviction?
5. If eviction ran: which evaluator fired? Which strategy was applied? Which segment was removed?
6. Was there a concurrent read? Did it see a consistent cache snapshot?

---

## Edge Cases

- A cache can be non-optimal (stale metadata, suboptimal eviction candidates) between background events; eventual convergence is expected.
- `WaitForIdleAsync` indicates the system was idle at some point, not that it remains idle.
- In Scenario U3, multi-segment assembly requires that the union of segments covers `RequestedRange` with NO gaps. If even one gap exists, the scenario degrades to U4 (Partial Hit).
- In Scenario B3, if the just-stored segment is the only segment (cache was empty before storage), eviction cannot proceed — the evaluator firing with only immune segments present is a no-op (the cache cannot evict its only segment; it will remain over-limit until the next storage event adds another eligible candidate).
- Segments are never merged, even if two adjacent segments together span a contiguous range. Merging would reset the eviction metadata of one of the segments and complicate eviction decisions.

---

## See Also

- `docs/visited-places/actors.md` — actor responsibilities per scenario
- `docs/visited-places/invariants.md` — formal invariants
- `docs/visited-places/eviction.md` — eviction architecture (policy-pressure-selector model, strategy catalog)
- `docs/visited-places/storage-strategies.md` — storage internals (append buffer, normalization, stride index)
- `docs/shared/glossary.md` — shared term definitions (WaitForIdleAsync, CacheInteraction, etc.)
