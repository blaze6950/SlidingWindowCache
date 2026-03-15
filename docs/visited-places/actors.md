# Actors — VisitedPlaces Cache

This document is the canonical actor catalog for `VisitedPlacesCache`. Formal invariants live in `docs/visited-places/invariants.md`.

---

## Execution Contexts

- **User Thread** — serves `GetDataAsync`; ends at event publish (fire-and-forget).
- **Background Storage Loop** — single background thread; dequeues `CacheNormalizationRequest`s and performs all cache mutations (statistics updates, segment storage, eviction).
- **TTL Loop** — independent background work dispatched fire-and-forget on the thread pool via `ConcurrentWorkScheduler`; awaits TTL delays and removes expired segments directly via `ISegmentStorage`. Only present when `VisitedPlacesCacheOptions.SegmentTtl` is non-null.

There are up to three execution contexts in VPC when TTL is enabled (compared to two in the no-TTL configuration, and three in SlidingWindowCache). There is no Decision Path; the Background Storage Loop combines the roles of event processing and cache mutation. The TTL Loop is an independent actor with its own scheduler and activity counter.

### Execution Context Diagram

```
User Thread                 Background Storage Loop        TTL Loop (if TTL enabled)
──────────────────────      ───────────────────────────    ─────────────────────────
GetDataAsync()
  │
  ├─ read CachedSegments    ← ISegmentStorage (read)
  │
  ├─ [on miss/gap]
  │    └─ IDataSource.FetchAsync()
  │
  ├─ assemble result
  │
  ├─ ActivityCounter.Increment()
  │
  └─ channel.Write(CacheNormalizationRequest)
       │
       │              dequeue event
       │         ┌────────────────────────
       │         │ engine.UpdateMetadata()
       │         │ storage.Add(segment)
       │         │ engine.InitializeSegment()
       │         │ engine.EvaluateAndExecute()
       │         │   ├─ [if triggered]
       │         │   │    executor.Execute()
       │         │   │      └─ selector.TrySelectCandidate() [loop]
       │         │   └─ [if TTL enabled]
       │         │        ttlEngine.ScheduleExpiration()
       │         │              └─ ConcurrentWorkScheduler.Enqueue()
       │         │                       │
       │         │                       │   Task.Delay(ttl) [fire-and-forget]
       │         │                       │       │
       │         │                       │   segment.MarkAsRemoved()
       │         │                       │   storage.Remove()
       │         │                       │   engine.OnSegmentRemoved()
       │         │
       │         └─ ActivityCounter.Decrement()
```

**Key invariants illustrated:**
- User Thread ends at `channel.Write` — never waits for background work
- Background Storage Loop is the sole writer of `CachedSegments`
- TTL Loop uses `segment.MarkAsRemoved()` (idempotent) to collaborate with eviction

---

## Actors

### User Path

**Responsibilities**
- Serve user requests immediately.
- Identify cached segments that cover `RequestedRange` (partial or full).
- Compute true gaps (uncovered sub-ranges within `RequestedRange`).
- Fetch gap data synchronously from `IDataSource` if any gaps exist.
- Assemble response data from cached segments and freshly-fetched gap data (in-memory, local to user thread).
- Publish a `CacheNormalizationRequest` (fire-and-forget) containing used segment references and fetched data.

**Non-responsibilities**
- Does not mutate `CachedSegments`.
- Does not update segment statistics.
- Does not trigger or perform eviction.
- Does not make decisions about what to store or evict (no analytical pipeline).
- Does not fetch beyond `RequestedRange` (no prefetch, no geometry expansion).

**Invariant ownership**
- VPC.A.1. User Path and Background Path never write to cache state concurrently
- VPC.A.2. User Path has higher priority than the Background Path
- VPC.A.3. User Path always serves user requests
- VPC.A.4. User Path never waits for the Background Path
- VPC.A.5. User Path is the sole source of background events
- VPC.A.7. Performs only work necessary to return data
- VPC.A.8. May synchronously request from `IDataSource` for true gaps only
- VPC.A.9. User always receives data exactly corresponding to `RequestedRange`
- VPC.A.10. May read from `CachedSegments` and `IDataSource` but does not mutate cache state
- VPC.A.11. MUST NOT mutate cache state under any circumstance (read-only)
- VPC.C.4. Assembles data from all contributing segments
- VPC.C.5. Computes all true gaps before calling `IDataSource`
- VPC.F.1. Calls `IDataSource` only for true gaps
- VPC.F.4. Cancellation supported on all `IDataSource` calls

**Components**
- `VisitedPlacesCache<TRange, TData, TDomain>` — facade / composition root
- `UserRequestHandler<TRange, TData, TDomain>`

---

### Event Publisher

**Responsibilities**
- Construct a `CacheNormalizationRequest` after every `GetDataAsync` call.
- Enqueue the event into the background channel (thread-safe, non-blocking).
- Manage the `AsyncActivityCounter` lifecycle for the published event (increment before publish, decrement in the Background Path's `finally`).

**Non-responsibilities**
- Does not process events.
- Does not make decisions about the event payload's downstream effect.

**Invariant ownership**
- VPC.A.6. Background work is asynchronous relative to the User Path
- VPC.B.2. Every published event is eventually processed
- S.H.1. Activity counter incremented before event becomes visible to background
- S.H.2. Activity counter decremented in `finally` (Background Path's responsibility)

**Components**
- `VisitedPlacesCache<TRange, TData, TDomain>` (event construction and enqueue)

---

### Background Event Loop

**Responsibilities**
- Dequeue `CacheNormalizationRequest`s in FIFO order.
- Dispatch each event to the Background Path for processing.
- Ensure sequential (non-concurrent) processing of all events.
- Manage loop lifecycle (start on construction, exit on disposal cancellation).

**Non-responsibilities**
- Does not make decisions about event content.
- Does not access user-facing API.

**Invariant ownership**
- VPC.B.1. Strict FIFO ordering of event processing
- VPC.B.1a. FIFO ordering required for statistics accuracy
- VPC.B.2. Every event eventually processed
- VPC.D.3. Background Path operates as a single writer in a single thread

**Components**
- `VisitedPlacesCache<TRange, TData, TDomain>` (background loop entry point)
- Event channel (shared infrastructure)

---

### Background Path (Event Processor)

**Responsibilities**
- Process each `CacheNormalizationRequest` in the fixed four-step sequence (Invariant VPC.B.3): (1) metadata update, (2) storage, (3) eviction evaluation + execution, (4) post-removal notification. See `docs/visited-places/architecture.md` — Threading Model, Context 2 for the authoritative step-by-step description.
- Perform all `storage.Add` and `storage.Remove` calls (sole storage writer on the add path).
- Use `storage.AddRange` for multi-gap events (`FetchedChunks.Count > 1`) to avoid quadratic normalization cost (see `docs/visited-places/storage-strategies.md` — Bulk Storage: AddRange).
- Delegate all eviction concerns through `EvictionEngine` (sole eviction dependency).

**Non-responsibilities**
- Does not serve user requests.
- Does not call `IDataSource` (no background I/O).
- Does not own or interpret metadata schema (delegated entirely to the selector via the engine).
- Does not interact directly with `EvictionPolicyEvaluator`, `EvictionExecutor`, or `IEvictionSelector` — all eviction concerns go through `EvictionEngine`.

**Invariant ownership**
- VPC.A.1. Sole writer of cache state
- VPC.A.12. Sole authority for all cache mutations
- VPC.B.3. Fixed event processing sequence
- VPC.B.3a. Metadata update precedes storage
- VPC.B.3b. Eviction evaluation only after storage
- VPC.B.4. Only component that mutates `CachedSegments` and segment `EvictionMetadata`
- VPC.B.5. Cache state transitions are atomic from User Path's perspective
- VPC.E.5. Eviction evaluation and execution performed exclusively by Background Path

**Components**
- `CacheNormalizationExecutor<TRange, TData, TDomain>`

---

### Segment Storage

**Responsibilities**
- Maintain `CachedSegments` as a sorted, searchable, non-contiguous collection.
- Support efficient range intersection queries for User Path reads.
- Support efficient segment insertion for Background Path writes, via both `Add` (single segment) and `AddRange` (bulk insert for multi-gap events).
- Implement the selected storage strategy (Snapshot + Append Buffer, or LinkedList + Stride Index).

**Non-responsibilities**
- Does not evaluate eviction conditions.
- Does not track per-segment eviction metadata (metadata is owned by the Eviction Selector).
- Does not merge segments.
- Does not enforce segment capacity limits.

**Invariant ownership**
- VPC.C.1. Non-contiguous segment collection (gaps permitted)
- VPC.C.2. Segments are never merged
- VPC.C.3. Overlapping segments not permitted
- VPC.B.5. Storage transitions are atomic

**Components**
- `SnapshotAppendBufferStorage<TRange, TData>` (default, for smaller caches)
- `LinkedListStrideIndexStorage<TRange, TData>` (for larger caches)

---

### Eviction Policy

**Responsibilities**
- Determine whether eviction should run after each storage step.
- Evaluate the current `CachedSegments` state and produce an `IEvictionPressure` object: `NoPressure` if the constraint is satisfied, or an exceeded pressure if the constraint is violated.
- (Stateful policies only) Maintain an incremental aggregate updated via `OnSegmentAdded` / `OnSegmentRemoved` for O(1) `Evaluate`.

**Non-responsibilities**
- Does not determine which segments to evict (owned by Eviction Engine + Selector).
- Does not perform eviction.
- Does not estimate how many segments to remove.
- Does not access or modify eviction metadata.

**Invariant ownership**
- VPC.E.1. Eviction governed by pluggable Eviction Policy
- VPC.E.1a. Eviction triggered when ANY policy fires (OR-combined)

**Components**
- `MaxSegmentCountPolicy<TRange, TData>` — O(1) via `Interlocked` count tracking in `OnSegmentAdded`/`OnSegmentRemoved`
- `MaxTotalSpanPolicy<TRange, TData, TDomain>` — maintains running span aggregate via `OnSegmentAdded`/`OnSegmentRemoved`
- *(additional policies as configured)*

---

### Eviction Engine

**Responsibilities**
- Serve as the **single eviction facade** for `CacheNormalizationExecutor` — the processor depends only on the engine.
- Delegate selector metadata operations (`UpdateMetadata`, `InitializeSegment`) to the configured `IEvictionSelector`.
- Delegate segment lifecycle notifications (`InitializeSegment`, `OnSegmentRemoved`) to the internal `EvictionPolicyEvaluator`.
- Evaluate all policies and execute the constraint satisfaction loop via `EvaluateAndExecute`; return the list of segments to remove.
- Fire eviction-specific diagnostics (`EvictionEvaluated`, `EvictionTriggered`, `EvictionExecuted`).

**Non-responsibilities**
- Does not perform storage mutations (`storage.Add` / `storage.Remove` remain in `CacheNormalizationExecutor`).
- Does not serve user requests.
- Does not expose `EvictionPolicyEvaluator`, `EvictionExecutor`, or `IEvictionSelector` to the processor.

**Invariant ownership**
- VPC.E.2. Constraint satisfaction loop (executor runs via `TrySelectCandidate` until pressure satisfied)
- VPC.E.2a. Runs at most once per background event (`EvaluateAndExecute` called once per event)
- VPC.E.3. Just-stored segments are immune from eviction (immune set passed to selector)
- VPC.E.3a. No-op if all candidates are immune (`TrySelectCandidate` returns `false`)
- VPC.E.4. Metadata owned by Eviction Selector (engine delegates to selector)
- VPC.E.6. Remaining segments and their metadata are consistent after eviction
- VPC.E.8. Eviction internals are encapsulated behind the engine facade

**Components**
- `EvictionEngine<TRange, TData>`

---

### Eviction Executor *(internal component of Eviction Engine)*

The Eviction Executor is an **internal implementation detail of `EvictionEngine`**, not a top-level actor. It is not visible to `CacheNormalizationExecutor` or `VisitedPlacesCache`.

**Responsibilities**
- Execute the constraint satisfaction loop: build the immune set, repeatedly call `selector.TrySelectCandidate`, accumulate `toRemove`, call `pressure.Reduce` per candidate, until `IsExceeded = false` or no eligible candidates remain.
- Return the `toRemove` list to `EvictionEngine` for diagnostic firing and forwarding to the processor.

**Non-responsibilities**
- Does not remove segments from storage (no `ISegmentStorage` reference).
- Does not fire diagnostics (owned by `EvictionEngine`).
- Does not decide whether eviction should run (owned by Eviction Policy / `EvictionPolicyEvaluator`).
- Does not own or update eviction metadata (delegated entirely to the Eviction Selector).

**Components**
- `EvictionExecutor<TRange, TData>`

---

### Eviction Selector

**Responsibilities**
- Own, create, and update per-segment eviction metadata (`IEvictionMetadata? EvictionMetadata` on each `CachedSegment`).
- Select the single worst eviction candidate from a random sample of segments via `TrySelectCandidate` (O(SampleSize)).
- Skip immune segments inline during sampling (the immune set is passed as a parameter).

**Non-responsibilities**
- Does not decide whether eviction should run (owned by Eviction Policy).
- Does not pre-filter or remove immune segments from a separate collection (skips them during sampling).
- Does not remove segments from storage (owned by `CacheNormalizationExecutor`).
- Does not sort or scan the entire segment collection (O(SampleSize) only).

**Invariant ownership**
- VPC.E.4. Per-segment metadata owned by the Eviction Selector
- VPC.E.4a. Metadata initialized at storage time via `InitializeMetadata`
- VPC.E.4b. Metadata updated on `UsedSegments` events via `UpdateMetadata`
- VPC.E.4c. Metadata guaranteed valid before every `IsWorse` comparison via `EnsureMetadata`

**Components**
- `LruEvictionSelector<TRange, TData>` — selects worst by `LruMetadata.LastAccessedAt` from a random sample; uses `TimeProvider` for timestamps
- `FifoEvictionSelector<TRange, TData>` — selects worst by `FifoMetadata.CreatedAt` from a random sample; uses `TimeProvider` for timestamps
- `SmallestFirstEvictionSelector<TRange, TData, TDomain>` — selects worst by `SmallestFirstMetadata.Span` from a random sample; span pre-cached from `Range.Span(domain)` at initialization

> For metadata types, lifecycle, sampling contract, `SamplingEvictionSelector` base class, and `TimeProvider` injection details, see `docs/visited-places/eviction.md` — Component 3 (Eviction Selector) and Eviction Metadata.

---

### TTL Actor

**Responsibilities**
- Receive a newly stored segment from `CacheNormalizationExecutor` (via `TtlEngine.ScheduleExpirationAsync`) when `SegmentTtl` is configured.
- Await TTL delay fire-and-forget on the thread pool; on expiry, call `segment.MarkAsRemoved()` and, if first caller, perform storage removal and eviction engine notification.
- Fire `IVisitedPlacesCacheDiagnostics.TtlSegmentExpired()` only on actual removal.
- Support cancellation on disposal. See `docs/visited-places/architecture.md` — Threading Model, Context 3 for the authoritative mechanism description.

**Non-responsibilities**
- Does not interact with the normalization scheduler or the Background Storage Loop directly.
- Does not serve user requests.
- Does not evaluate eviction policies.
- Does not block `WaitForIdleAsync` (uses its own private `AsyncActivityCounter` inside `TtlEngine`).

**Invariant ownership**
- VPC.T.1. Idempotent removal via `segment.MarkAsRemoved()` (Interlocked.CompareExchange)
- VPC.T.2. Never blocks the User Path (fire-and-forget thread pool + dedicated activity counter)
- VPC.T.3. Pending delays cancelled on disposal
- VPC.T.4. TTL subsystem internals encapsulated behind `TtlEngine`

**Components**
- `TtlEngine<TRange, TData>` — facade; owns scheduler, activity counter, disposal CTS, and executor wiring
- `TtlExpirationExecutor<TRange, TData>` — internal to `TtlEngine`; awaits delay and performs removal
- `TtlExpirationWorkItem<TRange, TData>` — internal to `TtlEngine`; carries segment reference and expiry timestamp
- `ConcurrentWorkScheduler<TtlExpirationWorkItem<TRange, TData>>` — internal to `TtlEngine`; one per cache, TTL-dedicated

---

### Resource Management

**Responsibilities**
- Graceful shutdown and idempotent disposal of the Background Storage Loop and all owned resources.
- Signal the loop cancellation token on disposal.
- `DisposeAsync` awaits loop completion before returning.

**Components**
- `VisitedPlacesCache<TRange, TData, TDomain>` and all owned internals

---

## Actor Execution Context Summary

| Actor                             | Execution Context                        | Invoked By                             |
|-----------------------------------|------------------------------------------|----------------------------------------|
| `UserRequestHandler`              | User Thread                              | User (public API)                      |
| Event Publisher                   | User Thread (enqueue only, non-blocking) | `UserRequestHandler`                   |
| Background Event Loop             | Background Storage Loop                  | Background task (awaits channel)       |
| Background Path (Event Processor) | Background Storage Loop                  | Background Event Loop                  |
| Segment Storage (read)            | User Thread                              | `UserRequestHandler`                   |
| Segment Storage (write)           | Background Storage Loop or TTL Loop      | Background Path (eviction) / TTL Actor |
| Eviction Policy                   | Background Storage Loop                  | Eviction Engine (via evaluator)        |
| Eviction Engine                   | Background Storage Loop                  | Background Path                        |
| Eviction Executor (internal)      | Background Storage Loop                  | Eviction Engine                        |
| Eviction Selector (metadata)      | Background Storage Loop                  | Eviction Engine                        |
| TTL Actor                         | Thread Pool (fire-and-forget)            | TTL scheduler (work item queue)        |

**Critical:** The user thread ends at event enqueue (after non-blocking channel write). All cache mutations — storage, statistics updates, eviction — occur exclusively in the Background Storage Loop (via `CacheNormalizationExecutor`). TTL-driven removals run fire-and-forget on the thread pool via `TtlExpirationExecutor`; idempotency is guaranteed by `CachedSegment.MarkAsRemoved()` (Interlocked.CompareExchange).

---

## Actors vs Scenarios Reference

| Scenario                                   | User Path                                                                        | Storage                              | Eviction Policy                | Eviction Engine / Selector                                                                            |
|--------------------------------------------|----------------------------------------------------------------------------------|--------------------------------------|--------------------------------|-------------------------------------------------------------------------------------------------------|
| **U1 – Cold Cache**                        | Requests from `IDataSource`, returns data, publishes event                       | Stores new segment (background)      | Checked after storage          | `InitializeSegment`; `EvaluateAndExecute` if policy triggered                                         |
| **U2 – Full Hit (Single Segment)**         | Reads from segment, publishes stats-only event                                   | —                                    | NOT checked (stats-only event) | `UpdateMetadata` for used segment                                                                     |
| **U3 – Full Hit (Multi-Segment)**          | Reads from multiple segments, assembles in-memory, publishes stats-only event    | —                                    | NOT checked                    | `UpdateMetadata` for all used segments                                                                |
| **U4 – Partial Hit**                       | Reads intersection, requests gaps from `IDataSource`, assembles, publishes event | Stores gap segment(s) (background)   | Checked after storage          | `UpdateMetadata` for used; `InitializeSegment` for new; `EvaluateAndExecute` if triggered             |
| **U5 – Full Miss**                         | Requests full range from `IDataSource`, returns data, publishes event            | Stores new segment (background)      | Checked after storage          | `InitializeSegment` for new segment; `EvaluateAndExecute` if triggered                                |
| **B1 – Stats-Only Event**                  | —                                                                                | —                                    | NOT checked                    | `UpdateMetadata` for used segments                                                                    |
| **B2 – Store, No Eviction**                | —                                                                                | Stores new segment                   | Checked; does not fire         | `InitializeSegment` for new segment                                                                   |
| **B3 – Store, Eviction Triggered**         | —                                                                                | Stores new segment                   | Checked; fires                 | `InitializeSegment`; engine runs `EvaluateAndExecute`; selector samples candidates; processor removes |
| **E1 – Max Count Exceeded**                | —                                                                                | Added new segment (count over limit) | Fires                          | Engine invokes executor; LRU selector samples candidates; worst selected                              |
| **E4 – Immunity Rule**                     | —                                                                                | Added new segment                    | Fires                          | Just-stored excluded from sampling; engine evicts from remaining candidates                           |
| **C1 – Concurrent Reads**                  | Both read concurrently (safe)                                                    | —                                    | —                              | —                                                                                                     |
| **C2 – Read During Background Processing** | Reads consistent snapshot                                                        | Mutates atomically                   | —                              | —                                                                                                     |

---

## Architectural Summary

| Actor                       | Primary Concern                                                   |
|-----------------------------|-------------------------------------------------------------------|
| User Path                   | Speed and availability                                            |
| Event Publisher             | Reliable, non-blocking event delivery                             |
| Background Event Loop       | FIFO ordering and sequential processing                           |
| Background Path             | Correct mutation sequencing; sole storage writer (add path)       |
| Segment Storage             | Efficient range lookup and insertion                              |
| Eviction Policy             | Capacity limit enforcement                                        |
| Eviction Engine             | Eviction facade; orchestrates selector, evaluator, executor       |
| Eviction Executor           | Constraint satisfaction loop (internal to engine)                 |
| Eviction Selector           | Candidate sampling and per-segment metadata ownership             |
| TTL Actor                   | Time-bounded segment expiration; fire-and-forget on thread pool   |
| Resource Management         | Lifecycle and cleanup                                             |

---

## See Also

- `docs/visited-places/scenarios.md` — temporal scenario walkthroughs
- `docs/visited-places/invariants.md` — formal invariants
- `docs/visited-places/eviction.md` — eviction architecture detail
- `docs/visited-places/storage-strategies.md` — storage implementation detail
- `docs/shared/glossary.md` — shared term definitions
