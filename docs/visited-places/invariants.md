# Invariants — VisitedPlaces Cache

VisitedPlaces-specific system invariants. Shared invariant groups — **S.H** (activity tracking) and **S.J** (disposal) — are documented in `docs/shared/invariants.md`.

---

## Understanding This Document

This document lists **VisitedPlaces-specific invariants** across groups VPC.A–VPC.T.

### Invariant Categories

#### Behavioral Invariants
- **Nature**: Externally observable behavior via public API
- **Enforcement**: Automated tests (unit, integration)
- **Verification**: Testable through public API without inspecting internal state

#### Architectural Invariants
- **Nature**: Internal structural constraints enforced by code organization
- **Enforcement**: Component boundaries, encapsulation, ownership model
- **Verification**: Code review, type system, access modifiers
- **Note**: NOT directly testable via public API

#### Conceptual Invariants
- **Nature**: Design intent, guarantees, or explicit non-guarantees
- **Enforcement**: Documentation and architectural discipline
- **Note**: Guide future development; NOT meant to be tested directly

### Invariants ≠ Test Coverage

By design, this document contains more invariants than the test suite covers. Architectural invariants are enforced by code structure; conceptual invariants are documented design decisions. Full invariant documentation does not imply full test coverage.

---

## Testing Infrastructure: WaitForIdleAsync

Tests verify behavioral invariants through the public API. To synchronize with background storage and statistics updates and assert on converged state, use `WaitForIdleAsync()`:

```csharp
await cache.GetDataAsync(range);
await cache.WaitForIdleAsync();
// System WAS idle — assert on converged state
Assert.Equal(expectedCount, cache.SegmentCount);
```

`WaitForIdleAsync` completes when the system **was idle at some point** (eventual consistency semantics), not necessarily "is idle now." For formal semantics and race behavior, see `docs/shared/invariants.md` group S.H.

---

## VPC.A. User Path & Fast User Access Invariants

### VPC.A.1 Concurrency & Writer Exclusivity

**VPC.A.1** [Architectural] The User Path and Background Path **never write to cache state concurrently**.

- At any point in time, at most one component has write permission to `CachedSegments`
- User Path operations MUST be read-only with respect to cache state
- All cache mutations (segment additions, removals, statistics updates) are performed exclusively by the Background Path (Single-Writer rule)

**Rationale:** Eliminates write-write races and simplifies reasoning about segment collection consistency.

**VPC.A.2** [Architectural] The User Path **always has higher priority** than the Background Path.

- User requests take precedence over background storage and eviction operations
- The Background Path must not block the User Path under any circumstance

**VPC.A.3** [Behavioral] The User Path **always serves user requests** regardless of the state of background processing.

**VPC.A.4** [Behavioral] The User Path **never waits for the Background Path** to complete.

- `GetDataAsync` returns immediately after assembling data and publishing the event
- No blocking on background storage, statistics updates, or eviction

**VPC.A.5** [Architectural] The User Path is the **sole source of background events**.

- Only the User Path publishes `CacheNormalizationRequest`s; no other component may inject requests into the background queue

**VPC.A.6** [Architectural] Background storage and statistics updates are **always performed asynchronously** relative to the User Path.

- User requests return immediately; background work executes in its own loop

**VPC.A.7** [Architectural] The User Path performs **only the work necessary to return data to the user**.

- No cache mutations, statistics updates, or eviction work on the user thread
- All background work deferred to the Background Path

**VPC.A.8** [Conceptual] The User Path may synchronously call `IDataSource.FetchAsync` in the user execution context **if needed to serve `RequestedRange`**.

- *Design decision*: Prioritizes user-facing latency
- *Rationale*: User must get data immediately; only true gaps in cached coverage justify a synchronous fetch

---

### VPC.A.2 User-Facing Guarantees

**VPC.A.9** [Behavioral] The user always receives data **exactly corresponding to `RequestedRange`** (subject to boundary semantics).

**VPC.A.9a** [Architectural] `GetDataAsync` returns `RangeResult<TRange, TData>` containing the actual range fulfilled, the corresponding data, and the cache interaction classification.

- `RangeResult.Range` indicates the actual range returned (may be smaller than requested for bounded data sources)
- `RangeResult.Data` contains `ReadOnlyMemory<TData>` for the returned range
- `RangeResult.CacheInteraction` classifies how the request was served (`FullHit`, `PartialHit`, or `FullMiss`)
- `Range` is nullable to signal data unavailability without exceptions
- When `Range` is non-null, `Data.Length` MUST equal `Range.Span(domain)`

**VPC.A.9b** [Architectural] `RangeResult.CacheInteraction` **accurately reflects** the cache interaction type for every request.

- `FullMiss` — no segment in `CachedSegments` intersects `RequestedRange`
- `FullHit` — the union of one or more segments fully covers `RequestedRange` with no gaps
- `PartialHit` — some portion of `RequestedRange` is covered by cached segments, but at least one gap remains and must be fetched from `IDataSource`

---

### VPC.A.3 Cache Mutation Rules (User Path)

**VPC.A.10** [Architectural] The User Path may read from `CachedSegments` and `IDataSource` but **does not mutate cache state**.

- `CachedSegments` and segment `EvictionMetadata` are immutable from the User Path perspective
- In-memory data assembly (merging reads from multiple segments) is local to the user thread; no shared state is written

**VPC.A.11** [Architectural] The User Path **MUST NOT mutate cache state under any circumstance** (read-only path).

- User Path never adds or removes segments
- User Path never updates segment statistics
- All cache mutations exclusively performed by the Background Path (Single-Writer rule)

**VPC.A.12** [Architectural] Cache mutations are performed **exclusively by the Background Path** (single-writer architecture).

---

## VPC.B. Background Path & Event Processing Invariants

### VPC.B.1 FIFO Ordering

**VPC.B.1** [Architectural] The Background Path processes `CacheNormalizationRequest`s in **strict FIFO order**.

- Events are consumed in the exact order they were enqueued by the User Path
- No supersession: a newer event does NOT skip or cancel an older one
- Every event is processed; none are discarded silently

**VPC.B.1a** [Conceptual] **Event FIFO ordering is required for metadata accuracy.**

- Metadata accuracy depends on processing every access event in order (e.g., LRU `LastAccessedAt`)
- Supersession (as in SlidingWindowCache) would silently lose access events, corrupting eviction decisions (e.g., LRU evicting a heavily-used segment)

**VPC.B.2** [Architectural] **Every** `CacheNormalizationRequest` published by the User Path is **eventually processed** by the Background Path.

- No event is dropped, overwritten, or lost after enqueue

### VPC.B.2 Event Processing Steps

**VPC.B.3** [Architectural] Each `CacheNormalizationRequest` is processed in the following **fixed sequence**:

1. Update metadata for all `UsedSegments` by delegating to the `EvictionEngine` (`engine.UpdateMetadata` → `selector.UpdateMetadata`)
2. Store `FetchedData` as new segment(s), if present. When `FetchedChunks.Count == 1`, a single `storage.Add` call is made. When `FetchedChunks.Count > 1` (multi-gap partial hit), `storage.AddRange` is used to insert all segments in a single structural update (see `docs/visited-places/storage-strategies.md` — Bulk Storage: AddRange). Call `engine.InitializeSegment(segment)` after each stored segment.
3. Evaluate all Eviction Policies and execute eviction if any policy is exceeded (`engine.EvaluateAndExecute`), only if new data was stored in step 2
4. Remove evicted segments from storage (`storage.Remove` per segment); call `engine.OnSegmentRemoved(segment)` after each removal

**VPC.B.3a** [Architectural] **Metadata update always precedes storage** in the processing sequence.

- Metadata for used segments is updated before new segments are stored, ensuring consistent metadata state during eviction evaluation

**VPC.B.3b** [Architectural] **Eviction evaluation only occurs after a storage step.**

- Events with `FetchedData == null` (stats-only events from full cache hits) do NOT trigger eviction evaluation
- Eviction is triggered exclusively by the addition of new segments

**Rationale:** Eviction triggered by reads alone (without new storage) would cause thrashing in read-heavy caches that never exceed capacity. Capacity limits are segment-count or span-based; pure reads do not increase either.

### VPC.B.3 Background Path Mutation Rules

**VPC.B.4** [Architectural] The Background Path is the **ONLY component that mutates `CachedSegments` and segment `EvictionMetadata`**.

**VPC.B.5** [Architectural] Cache state transitions are **atomic from the User Path's perspective**.

- A segment is either fully present (with valid data and statistics) or absent
- No partially-initialized segment is ever visible to User Path reads

**VPC.B.6** [Architectural] The Background Path **does not serve user requests directly**; it only maintains the segment collection and statistics for future User Path reads.

**VPC.B.7** [Architectural] `CachedSegment.EvictionMetadata` is **mutable only by the Background Path**.

- `EvictionMetadata` is written by `selector.InitializeMetadata` (on storage) and `selector.UpdateMetadata` (on each event cycle) — both called exclusively from the Background Storage Loop
- The User Path reads `EvictionMetadata` only indirectly (via the segment's data); it never writes or updates it
- `EnsureMetadata` in `SamplingEvictionSelector` may also initialize metadata on first access by the eviction loop — still within the Background Path

---

## VPC.C. Segment Storage & Non-Contiguity Invariants

### VPC.C.1 Non-Contiguous Storage

**VPC.C.1** [Architectural] `CachedSegments` is a **collection of non-contiguous segments**. Gaps between segments are explicitly permitted.

- There is no contiguity requirement in VPC (contrast with SWC's Cache Contiguity Rule)
- A point in the domain may be absent from `CachedSegments`; this is a valid cache state

**VPC.C.2** [Architectural] **Segments are never merged**, even if two segments are near-adjacent.

- Two segments whose ranges are consecutive in the domain (no shared point, no gap between them) remain as two distinct segments
- Merging would reset the statistics of one of the segments and complicate eviction decisions
- Each independently-fetched sub-range occupies its own permanent entry until evicted

**VPC.C.3** [Architectural] **No two segments may share any discrete domain point**.

- Each point in the domain may be cached in at most one segment
- All VPC ranges use **closed boundaries** (`[start, end]`), so sharing a boundary value means sharing a discrete point — this is prohibited
- Formally, for any two consecutive segments in sorted order: `End[i] < Start[i+1]` (strict inequality)
- A corollary: `End[i] + 1 ≤ Start[i+1]` for integer-valued domains
- Storing data for a range whose `[start, end]` overlaps or touches an existing segment's `[start, end]` is an implementation error

**Rationale:** Shared points would make assembly ambiguous and statistics tracking unreliable. Gap detection logic in the User Path assumes strictly disjoint coverage. The strict-inequality constraint (`End[i] < Start[i+1]`) is also relied upon by the storage layer: `FindIntersecting` uses it to prove that no segment before the binary-search anchor can intersect the query range (see `docs/visited-places/storage-strategies.md`).

### VPC.C.2 Assembly

**VPC.C.4** [Architectural] The User Path MUST assemble data from **all contributing segments** when their union covers `RequestedRange`.

- If the union of two or more segments spans `RequestedRange` with no gaps, `CacheInteraction == FullHit` regardless of how many segments contributed
- The assembled result is always a local, in-memory operation on the user thread
- Assembled data is never stored back to `CachedSegments` as a merged segment

**VPC.C.5** [Architectural] The User Path MUST compute **all true gaps** within `RequestedRange` before calling `IDataSource.FetchAsync`.

- A true gap is a sub-range within `RequestedRange` not covered by any segment in `CachedSegments`
- Each distinct gap is fetched independently (or as a batch call)
- Fetching more than the gap (e.g., rounding up to a convenient boundary) is not prohibited at the `IDataSource` level, but the cache stores exactly what is returned by `IDataSource`

### VPC.C.3 Segment Freshness

**VPC.C.6** [Conceptual] Segments support **TTL-based expiration** via `VisitedPlacesCacheOptions.SegmentTtl`.

- When `SegmentTtl` is non-null, a `TtlExpirationWorkItem` is scheduled immediately after each segment is stored.
- The TTL actor awaits the expiration delay fire-and-forget on the thread pool and then removes the segment directly via `ISegmentStorage`.
- When `SegmentTtl` is null (default), no TTL work items are scheduled and segments are only evicted by the configured eviction policies.

**VPC.C.7** [Architectural] **`SnapshotAppendBufferStorage` normalizes atomically**: the transition from (old snapshot, non-zero append count) to (new merged snapshot, zero append count) is performed under a lock shared with `FindIntersecting`.

- `FindIntersecting` captures `(_snapshot, _appendCount)` as a consistent pair under `_normalizeLock` before searching. The search itself runs lock-free against the locally-captured values.
- `Normalize()` publishes the merged snapshot and resets `_appendCount` to zero inside `_normalizeLock`, so readers always see either (old snapshot, old count) or (new snapshot, 0) — never the mixed state.
- Without this guarantee, `FindIntersecting` could return the same segment reference twice (once from the new snapshot, once from the stale append buffer count), causing `Assemble` to double the data for that segment — silent data corruption.
- The lock is held for nanoseconds (two field reads on the reader side, two field writes on the writer side). `Normalize` fires at most once per `appendBufferSize` additions, so contention is negligible.
- `LinkedListStrideIndexStorage` is not affected — it inserts segments directly into the linked list with no dual-source scan.
- **`_appendBuffer` is intentionally NOT cleared after normalization.** A `FindIntersecting` call that captured `appendCount > 0` before the lock update is still iterating `_appendBuffer` lock-free when `Normalize` completes. Calling `Array.Clear` on the shared buffer at that point nulls out slots the reader is actively dereferencing, causing a `NullReferenceException`. Leaving stale references in place is safe: readers entering after the lock update capture `appendCount = 0` and skip the buffer scan entirely; the next `Add()` call overwrites each slot before incrementing the count, so stale entries are never observable to new readers.

**VPC.C.8** [Architectural] **`MergeSorted` defensively trims its result array** to the actual number of elements written, guarding against a TOCTOU race with the TTL Loop.

- `Normalize()` counts live segments in two passes (counting pass, then merge pass). If a TTL work item calls `CachedSegment.TryMarkAsRemoved()` on a segment between these two passes, that segment is counted as live but then skipped as removed during the merge — leaving null trailing slots in the result array.
- Without trimming, `FindIntersecting`'s binary search (`FindLastAtOrBefore`) would dereference a null element, producing a `NullReferenceException` on the User Path.
- `MergeSorted` compares the write cursor `k` against `result.Length` after all merge loops complete. If `k < result.Length` (race occurred), it calls `Array.Resize(ref result, k)` to discard the null trailing slots before publishing.
- On the common path (no concurrent TTL expiration during the narrow count-to-merge window), `k == result.Length` and the branch is not taken — zero overhead.
- This fix is entirely lock-free: it requires no coordination between the Background Storage Loop and the TTL Loop beyond the existing `CachedSegment.TryMarkAsRemoved()` CAS. The counting pass remains a good-faith size hint that avoids allocation on the common case; it does not need to be exact.

---

## VPC.D. Concurrency Invariants

**VPC.D.1** [Architectural] The execution model includes three execution contexts: User Thread, Background Storage Loop, and TTL Loop.

- No other threads may access cache-internal mutable state
- The TTL Loop accesses storage directly via `ISegmentStorage` and uses `CachedSegment.MarkAsRemoved()` for atomic, idempotent removal coordination

**VPC.D.2** [Architectural] User Path read operations on `CachedSegments` are **safe under concurrent access** from multiple user threads.

- Multiple user threads may simultaneously read `CachedSegments` (read-only access is concurrency-safe)
- Only the Background Path writes; User Path threads never contend for write access

**VPC.D.3** [Architectural] The Background Path operates as a **single writer in a single thread** (the Background Storage Loop).

- No concurrent writes to `CachedSegments` or segment `EvictionMetadata` are ever possible
- Internal storage strategy state (append buffer, stride index) is owned exclusively by the Background Path

**VPC.D.4** [Architectural] `CacheNormalizationRequest`s published by multiple concurrent User Path calls are **safely enqueued** without coordination between them.

- The event queue (channel) handles concurrent producers and a single consumer safely
- The order of events from concurrent producers is not deterministic; both orderings are valid

**VPC.D.5** [Conceptual] `GetDataAndWaitForIdleAsync` (strong consistency extension) provides its warm-cache guarantee **only under serialized (one-at-a-time) access**.

- Under parallel callers, `WaitForIdleAsync`'s "was idle at some point" semantics (Invariant S.H.3) may return after the old TCS completes but before the event from a concurrent request has been processed
- The method remains safe (no crashes, no hangs) under parallel access, but the guarantee degrades

**VPC.D.6** [Architectural] **Thread-safe eviction policy lifecycle**: `IEvictionPolicy` instances are constructed once at cache initialization and accessed from **two execution contexts**: the Background Storage Loop (for `OnSegmentAdded`, `Evaluate`, and eviction-driven `OnSegmentRemoved`) and the TTL thread pool (for TTL-driven `OnSegmentRemoved`).

- **`OnSegmentRemoved` must be thread-safe**: it can be called from either the Background Storage Loop or the TTL thread (via `TtlExpirationExecutor` → `EvictionEngine.OnSegmentRemoved`). The `Interlocked.CompareExchange` gate in `CachedSegment.TryMarkAsRemoved()` ensures only one caller invokes `OnSegmentRemoved` per segment, but the calling thread varies. Built-in policies use `Interlocked` operations for this reason
- **`OnSegmentAdded` and `Evaluate` remain single-threaded**: called only from the Background Storage Loop, inheriting VPC.D.3's single-writer guarantee
- Pressure objects (`IEvictionPressure`) are stack-local: created fresh per evaluation cycle by `IEvictionPolicy.Evaluate`, used within a single `EvaluateAndExecute` call, and then discarded
- The `EvictionExecutor` and `IEvictionSelector` are single-threaded — they run only within the Background Storage Loop's `EvaluateAndExecute` call

**VPC.D.7** [Architectural] **`LinkedListStrideIndexStorage.FindIntersecting` re-validates the stride anchor inside `_listSyncRoot`** before using it as the walk start node.

- The stride index is published lock-free via `Volatile.Write`; `FindIntersecting` reads it via `Volatile.Read` and performs a binary search to find the rightmost anchor at or before `range.Start` — all without holding the lock.
- An outer `anchorNode.List != null` check (before lock acquisition) acts as a fast-path hint: it avoids acquiring `_listSyncRoot` when the anchor is obviously stale.
- However, `NormalizeStrideIndex` Pass 2 can physically unlink the anchor node (inside its own per-node `_listSyncRoot` acquisition) between the outer check and `FindIntersecting`'s own lock acquisition — a TOCTOU race. After `Remove()`, `node.Next` is null, so the walk would start from the unlinked node and terminate immediately, producing a false cache miss.
- The fix: after acquiring `_listSyncRoot`, `FindIntersecting` re-evaluates `startNode?.List == null`. If the anchor was unlinked in the narrow window between the two checks, `startNode` is reset to null and the walk falls back to `_list.First` — a safe full-list walk.
- On the common path (anchor still live), the inner check is a single null comparison against a volatile field — negligible overhead.

**Enforcement:** `LinkedListStrideIndexStorage.FindIntersecting` in `src/Intervals.NET.Caching.VisitedPlaces/Infrastructure/Storage/LinkedListStrideIndexStorage.cs`

---

## VPC.E. Eviction Invariants

### VPC.E.1 Policy-Pressure Model

**VPC.E.1** [Architectural] Eviction is governed by a **pluggable Eviction Policy** (`IEvictionPolicy`) that evaluates cache state and produces **pressure objects** (`IEvictionPressure`) representing violated constraints.

- At least one policy is configured at construction time
- Multiple policies may be active simultaneously
- Policies MUST NOT estimate how many segments to remove — they only express whether a constraint is violated

**VPC.E.1a** [Architectural] Eviction is triggered when **ANY** configured Eviction Policy produces a pressure whose `IsExceeded` is `true`.

- Policies are OR-combined: if at least one produces an exceeded pressure, eviction runs
- All policies are checked after every storage step
- When no policy is exceeded, `NoPressure<TRange,TData>.Instance` is used (singleton, always `IsExceeded = false`)

**VPC.E.2** [Architectural] Eviction execution follows a **constraint satisfaction loop**:

- The **`EvictionEngine`** coordinates evaluation and execution: it calls `EvictionPolicyEvaluator.Evaluate` to obtain a pressure, then delegates to `EvictionExecutor.Execute` if exceeded.
- The **Eviction Executor** runs the loop: repeatedly calls `IEvictionSelector.TrySelectCandidate(allSegments, immuneSegments, out candidate)` until `pressure.IsExceeded = false` or no eligible candidates remain.
- The **Eviction Selector** (`IEvictionSelector`) determines candidate selection via random O(SampleSize) sampling — it does NOT sort candidates.
- Pressure objects update themselves via `Reduce(segment)` as each segment is selected, tracking actual constraint satisfaction.

**VPC.E.2a** [Architectural] The constraint satisfaction loop runs **at most once per background event** regardless of how many policies produced exceeded pressures.

- A `CompositePressure` aggregates all exceeded pressures; the loop removes segments until `IsExceeded = false` for all
- When only a single policy is exceeded, its pressure is used directly (no composite wrapping)

**Rationale:** The constraint satisfaction model eliminates the old mismatch where evaluators estimated removal counts (assuming a specific removal order) while executors used a different order. Pressure objects track actual constraint satisfaction as segments are removed, guaranteeing correctness regardless of selector strategy.

### VPC.E.2 Just-Stored Segment Immunity

**VPC.E.3** [Architectural] The **just-stored segment is immune** from eviction in the same background event processing step in which it was stored.

- When `EvictionEngine.EvaluateAndExecute` is invoked, the `justStoredSegments` list is passed to `EvictionExecutor.Execute`, which seeds the immune `HashSet` from it before the selection loop begins
- The selector skips immune segments inline during sampling (the immune set is passed as a parameter to `TrySelectCandidate`)
- For bulk stores (`AddRange`, when `FetchedChunks.Count > 1`), **all** segments stored in the current event cycle are in the immune set — not just the last one. This prevents any of the newly-stored gap segments from being immediately re-evicted in the same event cycle.
- The immune segments are the exact segments added in step 2 of the current event's processing sequence

**Rationale:** Without immunity, a newly-stored segment could be immediately evicted (e.g., by LRU, since its `LastAccessedAt` is the earliest among all segments). Immediate eviction of just-stored data would cause an infinite fetch-store-evict loop on every new access to an uncached range.

**VPC.E.3a** [Conceptual] If the just-stored segment is the **only segment** in `CachedSegments` when eviction is triggered, the Eviction Executor is a no-op for that event.

- The cache cannot evict its only segment; it will remain over-limit until the next storage event adds another eligible candidate
- This is an expected edge case in very low-capacity configurations

### VPC.E.3 Eviction Selector Metadata Ownership

**VPC.E.4** [Architectural] Per-segment eviction metadata is **owned by the Eviction Selector**, not by a shared statistics record.

- Each selector defines its own metadata type (nested `internal sealed class` implementing `IEvictionMetadata`) and stores it on `CachedSegment.EvictionMetadata`
- The `EvictionEngine` delegates metadata management to the configured selector:
  - Step 1: calls `engine.UpdateMetadata(usedSegments)` → `selector.UpdateMetadata` for each event cycle
  - Step 2: calls `engine.InitializeSegment(segment)` → `selector.InitializeMetadata(segment)` immediately after each segment is stored
- Time-aware selectors (LRU, FIFO) obtain the current timestamp from an injected `TimeProvider`; time-agnostic selectors (SmallestFirst) compute metadata from the segment itself

**VPC.E.4a** [Architectural] Per-segment metadata is initialized when the segment is stored:

- `engine.InitializeSegment(segment)` is called by `CacheNormalizationExecutor` immediately after each `_storage.Add(segment)` or, for bulk stores, after each segment stored via `_storage.AddRange(segments[])`, which in turn calls `selector.InitializeMetadata(segment)`
- Example: `LruMetadata { LastAccessedAt = TimeProvider.GetUtcNow().UtcDateTime }`, `FifoMetadata { CreatedAt = TimeProvider.GetUtcNow().UtcDateTime }`, `SmallestFirstMetadata { Span = segment.Range.Span(domain).Value }`

**VPC.E.4b** [Architectural] Per-segment metadata is updated when the segment appears in a `CacheNormalizationRequest`'s `UsedSegments` list:

- `engine.UpdateMetadata(usedSegments)` is called by `CacheNormalizationExecutor` at the start of each event cycle, which delegates to `selector.UpdateMetadata(usedSegments)`
- Example: `LruMetadata.LastAccessedAt = TimeProvider.GetUtcNow().UtcDateTime`; FIFO and SmallestFirst selectors perform no-op updates

**VPC.E.4c** [Architectural] Before every `IsWorse` comparison in the sampling loop, `EnsureMetadata` is called on the sampled segment, **guaranteeing valid selector-specific metadata** for all comparisons:

- `SamplingEvictionSelector.TrySelectCandidate` calls `EnsureMetadata(segment)` before passing any segment to `IsWorse`
- If metadata is null or belongs to a different selector type (e.g., after a runtime selector switch), `EnsureMetadata` creates and attaches the correct metadata — this repair persists permanently on the segment
- `IsWorse` is always pure: it can safely cast `segment.EvictionMetadata` without null checks or type-mismatch guards

**VPC.E.5** [Architectural] Eviction evaluation and execution are performed **exclusively by the Background Path**, never by the User Path.

- No eviction logic runs on the user thread under any circumstance

### VPC.E.4 Post-Eviction Consistency

**VPC.E.6** [Architectural] After eviction, all remaining segments and their metadata remain **consistent and valid**.

- Removed segments leave no dangling metadata references
- No remaining segment references a removed segment

**VPC.E.7** [Conceptual] After eviction, the cache may still be above-limit in edge cases (see VPC.E.3a). This is acceptable; the next storage event will trigger another eviction pass.

**VPC.E.8** [Architectural] The eviction subsystem internals (`EvictionPolicyEvaluator`, `EvictionExecutor`, `IEvictionSelector`) are **encapsulated behind `EvictionEngine`**.

- `CacheNormalizationExecutor` depends only on `EvictionEngine` — it has no direct reference to the evaluator, executor, or selector
- This boundary enforces single-responsibility: the executor owns storage mutations; the engine owns eviction coordination

---

## VPC.T. TTL (Time-To-Live) Invariants

**VPC.T.1** [Architectural] TTL expiration is **idempotent**: if a segment has already been evicted by a capacity policy when its TTL fires, the removal is a no-op.

- `TtlExpirationExecutor` calls `storage.TryRemove(segment)`, which internally calls `segment.TryMarkAsRemoved()` (an `Interlocked.CompareExchange` on the segment's `_isRemoved` field) before performing any storage mutation.
- If `TryMarkAsRemoved()` returns `false` (another caller already set the flag), `TryRemove` returns `false` and the TTL actor skips removal entirely.
- This ensures that concurrent eviction and TTL expiration cannot produce a double-remove or corrupt storage state.

**VPC.T.2** [Architectural] The TTL actor **never blocks the User Path**: it runs fire-and-forget on the thread pool via a dedicated `ConcurrentWorkScheduler`.

- `TtlExpirationExecutor` awaits `Task.Delay(ttl - elapsed)` independently on the thread pool; each TTL work item runs concurrently with others.
- TTL work items do not interact with the User Path or enqueue work into the Background Storage Loop. They do call `EvictionEngine.OnSegmentRemoved` to update policy aggregates (e.g., segment count), but this is thread-safe via `Interlocked` operations (see VPC.D.6).
- TTL work items use their own `AsyncActivityCounter` so that `WaitForIdleAsync` does not wait for long-running TTL delays.

**VPC.T.3** [Conceptual] Pending TTL delays are **cancelled on disposal**.

- When `VisitedPlacesCache.DisposeAsync` is called, `TtlEngine.DisposeAsync` is invoked after the normalization scheduler has been drained.
- The `ConcurrentWorkScheduler`'s `CancellationToken` is cancelled, aborting any in-progress `Task.Delay` calls via `OperationCanceledException`.
- No TTL work item outlives the cache instance.

**VPC.T.4** [Architectural] The TTL subsystem internals (`TtlExpirationExecutor`, `ConcurrentWorkScheduler`, `AsyncActivityCounter`, `CancellationTokenSource`) are **encapsulated behind `TtlEngine`**.

- `CacheNormalizationExecutor` depends only on `TtlEngine` — it has no direct reference to the executor, scheduler, activity counter, or disposal CTS.
- `VisitedPlacesCache` holds a single `TtlEngine?` field — the three-field infrastructure (`_ttlActivityCounter`, `_ttlScheduler`, `_ttlDisposalCts`) is owned internally by the engine.
- This boundary enforces single-responsibility: the executor owns storage mutations; the engine owns TTL lifecycle coordination.

---

## VPC.F. Data Source & I/O Invariants

**VPC.F.1** [Architectural] `IDataSource.FetchAsync` is called **only for true gaps** — sub-ranges of `RequestedRange` not covered by any segment in `CachedSegments`.

- User Path I/O is bounded by the uncovered gaps within `RequestedRange`
- Background Path has no I/O responsibility (it stores data delivered by the User Path's event)

**VPC.F.2** [Architectural] `IDataSource.FetchAsync` **MUST respect boundary semantics**: it may return a range smaller than requested (or null) for bounded data sources.

- A non-null `RangeChunk.Range` MAY be smaller than the requested range (partial fulfillment)
- The cache MUST use the actual returned range, not the requested range
- `null` `RangeChunk.Range` signals no data available; no segment is stored for that gap

**VPC.F.3** [Conceptual] **VPC does not prefetch** beyond `RequestedRange`.

- Unlike SlidingWindowCache, VPC has no geometry-based expansion of fetches
- Fetches are strictly demand-driven: only what is needed to serve the current user request is fetched

**VPC.F.4** [Architectural] Cancellation **MUST be supported** for all `IDataSource.FetchAsync` calls on the User Path.

- User Path I/O is cancellable via the `CancellationToken` passed to `GetDataAsync`
- Background Path has no I/O calls; cancellation is only relevant on the User Path

---

## Summary

VPC invariant groups:

| Group  | Description                               | Count |
|--------|-------------------------------------------|-------|
| VPC.A  | User Path & Fast User Access              | 12    |
| VPC.B  | Background Path & Event Processing        | 8     |
| VPC.C  | Segment Storage & Non-Contiguity          | 8     |
| VPC.D  | Concurrency                               | 7     |
| VPC.E  | Eviction                                  | 14    |
| VPC.F  | Data Source & I/O                         | 4     |
| VPC.T  | TTL (Time-To-Live)                        | 4     |

Shared invariants (S.H, S.J) are in `docs/shared/invariants.md`.

---

## See Also

- `docs/shared/invariants.md` — shared invariant groups S.H (activity tracking) and S.J (disposal)
- `docs/visited-places/scenarios.md` — temporal scenario walkthroughs
- `docs/visited-places/actors.md` — actor responsibilities and invariant ownership
- `docs/visited-places/eviction.md` — eviction architecture (policy-pressure-selector model, strategy catalog)
- `docs/visited-places/storage-strategies.md` — storage internals
- `docs/shared/glossary.md` — shared term definitions
