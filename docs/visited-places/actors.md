# Actors — VisitedPlaces Cache

This document is the canonical actor catalog for `VisitedPlacesCache`. Formal invariants live in `docs/visited-places/invariants.md`.

---

## Execution Contexts

- **User Thread** — serves `GetDataAsync`; ends at event publish (fire-and-forget).
- **Background Storage Loop** — single background thread; dequeues `BackgroundEvent`s and performs all cache mutations (statistics updates, segment storage, eviction).

There are exactly two execution contexts in VPC (compared to three in SlidingWindowCache). There is no Decision Path; the Background Path combines the roles of event processing and cache mutation.

---

## Actors

### User Path

**Responsibilities**
- Serve user requests immediately.
- Identify cached segments that cover `RequestedRange` (partial or full).
- Compute true gaps (uncovered sub-ranges within `RequestedRange`).
- Fetch gap data synchronously from `IDataSource` if any gaps exist.
- Assemble response data from cached segments and freshly-fetched gap data (in-memory, local to user thread).
- Publish a `BackgroundEvent` (fire-and-forget) containing used segment references and fetched data.

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
- Construct a `BackgroundEvent` after every `GetDataAsync` call.
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
- Dequeue `BackgroundEvent`s in FIFO order.
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
- Process each `BackgroundEvent` in the fixed sequence: metadata update → storage → eviction evaluation → eviction execution.
- Delegate metadata updates to the configured Eviction Selector (`selector.UpdateMetadata`).
- Delegate segment storage to the Storage Strategy.
- Call `selector.InitializeMetadata(segment, now)` immediately after each new segment is stored.
- Delegate eviction evaluation to all configured Eviction Policies.
- Delegate eviction execution to the Eviction Executor.

**Non-responsibilities**
- Does not serve user requests.
- Does not call `IDataSource` (no background I/O).
- Does not own or interpret metadata schema (delegated entirely to the selector).

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
- `BackgroundEventProcessor<TRange, TData, TDomain>`

---

### Segment Storage

**Responsibilities**
- Maintain `CachedSegments` as a sorted, searchable, non-contiguous collection.
- Support efficient range intersection queries for User Path reads.
- Support efficient segment insertion for Background Path writes.
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

**Non-responsibilities**
- Does not determine which segments to evict (owned by Eviction Executor + Selector).
- Does not perform eviction.
- Does not estimate how many segments to remove.
- Does not access or modify eviction metadata.

**Invariant ownership**
- VPC.E.1. Eviction governed by pluggable Eviction Policy
- VPC.E.1a. Eviction triggered when ANY policy fires (OR-combined)

**Components**
- `MaxSegmentCountPolicy<TRange, TData>`
- `MaxTotalSpanPolicy<TRange, TData, TDomain>`
- *(additional policies as configured)*

---

### Eviction Executor

**Responsibilities**
- When invoked after a policy fires: receive all segments + the just-stored segment, filter out the immune (just-stored) segment, pass eligible candidates to the configured Eviction Selector for ordering, and remove segments in selector order until all pressures are satisfied.
- Report each removed segment via diagnostics.

**Non-responsibilities**
- Does not decide whether eviction should run (owned by Eviction Policy).
- Does not own or update eviction metadata (delegated entirely to the Eviction Selector).
- Does not add new segments to `CachedSegments`.
- Does not serve user requests.

**Invariant ownership**
- VPC.E.2. Constraint satisfaction loop (removes in selector order until all pressures satisfied)
- VPC.E.2a. Runs at most once per background event (single pass via CompositePressure)
- VPC.E.3. Just-stored segment is immune from eviction
- VPC.E.3a. No-op if just-stored segment is the only candidate
- VPC.E.6. Remaining segments and their metadata are consistent after eviction

**Components**
- `EvictionExecutor<TRange, TData, TDomain>`

---

### Eviction Selector

**Responsibilities**
- Define, create, and update per-segment eviction metadata.
- Order eviction candidates for the Eviction Executor.
- Implement `InitializeMetadata(segment, now)` — attach selector-specific metadata to a newly-stored segment.
- Implement `UpdateMetadata(usedSegments, now)` — update metadata for segments accessed by the User Path.
- Implement `OrderCandidates(segments)` — return candidates in eviction priority order.

**Non-responsibilities**
- Does not decide whether eviction should run (owned by Eviction Policy).
- Does not filter immune segments (owned by Eviction Executor).
- Does not remove segments from storage (owned by Eviction Executor).

**Invariant ownership**
- VPC.E.4. Per-segment metadata owned by the Eviction Selector
- VPC.E.4a. Metadata initialized at storage time via `InitializeMetadata`
- VPC.E.4b. Metadata updated on `UsedSegments` events via `UpdateMetadata`

**Components**
- `LruEvictionSelector<TRange, TData>` — orders by `LruMetadata.LastAccessedAt` ascending
- `FifoEvictionSelector<TRange, TData>` — orders by `FifoMetadata.CreatedAt` ascending
- `SmallestFirstEvictionSelector<TRange, TData, TDomain>` — orders by `Range.Span(domain)` ascending; no metadata

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

| Actor                             | Execution Context                        | Invoked By                       |
|-----------------------------------|------------------------------------------|----------------------------------|
| `UserRequestHandler`              | User Thread                              | User (public API)                |
| Event Publisher                   | User Thread (enqueue only, non-blocking) | `UserRequestHandler`             |
| Background Event Loop             | Background Storage Loop                  | Background task (awaits channel) |
| Background Path (Event Processor) | Background Storage Loop                  | Background Event Loop            |
| Segment Storage (read)            | User Thread                              | `UserRequestHandler`             |
| Segment Storage (write)           | Background Storage Loop                  | Background Path                  |
| Eviction Policy                   | Background Storage Loop                  | Background Path                  |
| Eviction Selector (metadata)      | Background Storage Loop                  | Background Path                  |
| Eviction Executor (eviction)      | Background Storage Loop                  | Background Path                  |

**Critical:** The user thread ends at event enqueue (after non-blocking channel write). All cache mutations — storage, statistics updates, eviction — occur exclusively in the Background Storage Loop.

---

## Actors vs Scenarios Reference

| Scenario                                   | User Path                                                                        | Storage                              | Eviction Policy                | Eviction Selector / Executor                                         |
|--------------------------------------------|----------------------------------------------------------------------------------|--------------------------------------|--------------------------------|----------------------------------------------------------------------|
| **U1 – Cold Cache**                        | Requests from `IDataSource`, returns data, publishes event                       | Stores new segment (background)      | Checked after storage          | Initializes metadata; evicts if policy triggered                     |
| **U2 – Full Hit (Single Segment)**         | Reads from segment, publishes stats-only event                                   | —                                    | NOT checked (stats-only event) | Updates metadata for used segment                                    |
| **U3 – Full Hit (Multi-Segment)**          | Reads from multiple segments, assembles in-memory, publishes stats-only event    | —                                    | NOT checked                    | Updates metadata for all used segments                               |
| **U4 – Partial Hit**                       | Reads intersection, requests gaps from `IDataSource`, assembles, publishes event | Stores gap segment(s) (background)   | Checked after storage          | Updates metadata for used segments; initializes for new; evicts if triggered |
| **U5 – Full Miss**                         | Requests full range from `IDataSource`, returns data, publishes event            | Stores new segment (background)      | Checked after storage          | Initializes metadata for new segment; evicts if triggered            |
| **B1 – Stats-Only Event**                  | —                                                                                | —                                    | NOT checked                    | Updates metadata for used segments                                   |
| **B2 – Store, No Eviction**                | —                                                                                | Stores new segment                   | Checked; does not fire         | Initializes metadata for new segment                                 |
| **B3 – Store, Eviction Triggered**         | —                                                                                | Stores new segment                   | Checked; fires                 | Initializes metadata; selector orders candidates; executor removes   |
| **E1 – Max Count Exceeded**                | —                                                                                | Added new segment (count over limit) | Fires                          | Executor removes LRU candidate (excluding just-stored)               |
| **E4 – Immunity Rule**                     | —                                                                                | Added new segment                    | Fires                          | Excludes just-stored; executor evicts from remaining                 |
| **C1 – Concurrent Reads**                  | Both read concurrently (safe)                                                    | —                                    | —                              | —                                                                    |
| **C2 – Read During Background Processing** | Reads consistent snapshot                                                        | Mutates atomically                   | —                              | —                                                                    |

---

## Architectural Summary

| Actor                 | Primary Concern                                       |
|-----------------------|-------------------------------------------------------|
| User Path             | Speed and availability                                |
| Event Publisher       | Reliable, non-blocking event delivery                 |
| Background Event Loop | FIFO ordering and sequential processing               |
| Background Path       | Correct mutation sequencing                           |
| Segment Storage       | Efficient range lookup and insertion                  |
| Eviction Policy       | Capacity limit enforcement                            |
| Eviction Selector     | Candidate ordering and per-segment metadata ownership |
| Eviction Executor     | Constraint satisfaction loop and segment removal      |
| Resource Management   | Lifecycle and cleanup                                 |

---

## See Also

- `docs/visited-places/scenarios.md` — temporal scenario walkthroughs
- `docs/visited-places/invariants.md` — formal invariants
- `docs/visited-places/eviction.md` — eviction architecture detail
- `docs/visited-places/storage-strategies.md` — storage implementation detail
- `docs/shared/glossary.md` — shared term definitions
