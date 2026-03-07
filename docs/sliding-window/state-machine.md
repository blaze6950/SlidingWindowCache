# Cache State Machine — SlidingWindow Cache

This document defines the cache state machine at the public-observable level and clarifies transitions and mutation authority. Formal invariants: `docs/sliding-window/invariants.md`.

---

## Motivation

Most concurrency complexity disappears if we can answer two questions unambiguously:

1. What state is the cache in?
2. Who is allowed to mutate shared state in that state?

---

## States

The cache is in one of three states:

**1. Uninitialized**
- `CacheState.IsInitialized == false`
- `CacheState.Storage` exists but contains no data (empty buffer)
- `CacheState.NoRebalanceRange == null`

**2. Initialized**
- `CacheState.IsInitialized == true`
- `CacheState.Storage` holds a contiguous, non-empty range of data consistent with `CacheState.Storage.Range` (Invariant SWC.B.1)
- Cache is contiguous — no gaps (Invariant SWC.A.12b)
- Ready to serve user requests

**3. Rebalancing**
- Cache remains `Initialized` from the user-visible perspective
- User Path continues to serve requests normally
- Rebalance Execution is mutating cache asynchronously in the background
- Rebalance can be cancelled at any time

---

## State Transition Diagram

```
┌─────────────────┐
│  Uninitialized  │
└────────┬────────┘
         │
         │ T1: First User Request
         │ (Rebalance Execution writes initial cache)
         ▼
┌─────────────────┐
│   Initialized   │◄───────────────┐
└────────┬────────┘                │
         │                         │
         │ T2: Decision validates  │
         │ rebalance necessary     │
         ▼                         │
┌─────────────────┐                │
│  Rebalancing    │                │
└────────┬────────┘                │
         │                         │
         │ T3: Execution completes │
         └─────────────────────────┘

T4: New user request during Rebalancing
    → Decision validates new execution necessary
    → Previous rebalance cancelled
    → New rebalance scheduled (stays in Rebalancing)
```

---

## Mutation Authority

Mutation authority is constant across all states:

- **User Path**: read-only with respect to shared cache state in every state
- **Rebalance Execution**: sole writer in every state

See `docs/sliding-window/invariants.md` for the formal single-writer rule (Invariants SWC.A.1, SWC.A.11, SWC.A.12, SWC.A.12a).

---

## Transition Details

### T1: Uninitialized → Initialized (Cold Start)

- **Trigger**: First user request (Scenario U1)
- **Actor**: Rebalance Execution (NOT User Path)
- **Sequence**:
  1. User Path fetches `RequestedRange` from `IDataSource`
  2. User Path returns data to user immediately
  3. User Path publishes intent with delivered data
  4. Rebalance Execution performs first cache write
- **Mutations** (Rebalance Execution only):
  - Call `Storage.Rematerialize()` with delivered data and range
  - Set `IsInitialized = true`
- **Atomicity**: Changes applied atomically (Invariant SWC.B.2)
- **Postcondition**: Cache enters `Initialized` after execution completes
- **Note**: User Path is read-only; initial cache population is performed exclusively by Rebalance Execution

### T2: Initialized → Rebalancing (Normal Operation)

- **Trigger**: User request, decision validates rebalance necessary
- **Sequence**:
  1. User Path reads from cache or fetches from `IDataSource` (no cache mutation)
  2. User Path returns data to user immediately
  3. User Path publishes intent with delivered data
  4. Decision Engine runs multi-stage analytical validation (THE authority)
  5. If validation confirms necessity: prior pending rebalance cancelled, new execution scheduled
  6. If validation rejects (NoRebalanceRange containment, pending coverage, Desired==Current): no execution, work avoidance
  7. Rebalance Execution writes to cache (background, only if validated)
- **Mutations**: Rebalance Execution only — User Path never mutates `Cache`, `IsInitialized`, or `NoRebalanceRange`
- **Cancellation model**: Cancellation is mechanical coordination, not the decision mechanism; validation determines necessity
- **Postcondition**: Cache enters `Rebalancing` (only if all validation stages passed)

### T3: Rebalancing → Initialized (Rebalance Completion)

- **Trigger**: Rebalance execution completes successfully
- **Actor**: Rebalance Executor (sole writer)
- **Mutations** (Rebalance Execution only):
  - Use delivered data from intent as authoritative base
  - Fetch missing data for `DesiredCacheRange` (only truly missing parts)
  - Merge delivered data with fetched data
  - Trim to `DesiredCacheRange` (normalization)
  - Call `Storage.Rematerialize()` with merged, trimmed data (sets storage contents and `Storage.Range`)
  - Set `IsInitialized = true`
  - Recompute `NoRebalanceRange`
- **Atomicity**: Changes applied atomically (Invariant SWC.B.2)
- **Postcondition**: Cache returns to stable `Initialized` state

### T4: Rebalancing → Rebalancing (New Request MAY Cancel Active Rebalance)

- **Trigger**: User request arrives during rebalance execution (Scenarios C1, C2)
- **Sequence**:
  1. User Path reads from cache or fetches from `IDataSource` (no cache mutation)
  2. User Path returns data to user immediately
  3. User Path publishes new intent
  4. Decision Engine validates whether new rebalance is necessary
  5. If validation confirms necessity: active rebalance is cancelled; new execution scheduled
  6. If validation rejects necessity: active rebalance continues undisturbed (work avoidance)
  7. If cancelled: Rebalance yields; new rebalance uses new intent's delivered data
- **Critical**: User Path does NOT decide cancellation — Decision Engine validation determines necessity; cancellation is mechanical coordination
- **Note**: "User Request MAY Cancel" means cancellation occurs ONLY when validation confirms new rebalance is necessary

---

## Mutation Ownership Matrix

| State | User Path Mutations | Rebalance Execution Mutations |
|---|---|---|
| Uninitialized | None | Initial cache write (after first user request intent) |
| Initialized | None | Not active |
| Rebalancing | None | All cache mutations (expand, trim, Rematerialize, IsInitialized, NoRebalanceRange) — must yield on cancellation |

**User Path mutations (Invariants SWC.A.11, SWC.A.12)**:
- User Path NEVER calls `Storage.Rematerialize()`
- User Path NEVER writes to `IsInitialized`
- User Path NEVER writes to `NoRebalanceRange`

**Rebalance Execution mutations (Invariants SWC.F.2, SWC.F.2a)**:
1. Uses delivered data from intent as authoritative base
2. Expands to `DesiredCacheRange` (fetches only truly missing ranges)
3. Trims excess data outside `DesiredCacheRange`
4. Calls `Storage.Rematerialize()` (atomically replaces storage data and `Storage.Range`)
5. Writes to `IsInitialized = true`
6. Recomputes and writes to `NoRebalanceRange`

---

## Concurrency Semantics

**Cancellation Protocol**:

1. User Path publishes new intent (atomically supersedes prior intent)
2. Background loop observes new intent; cancels active rebalance if validation confirms necessity
3. User Path reads from cache or fetches from `IDataSource` (no mutation)
4. User Path returns data to user (never waits)
5. New rebalance proceeds with new intent's delivered data (if validated)
6. Cancelled rebalance yields without leaving cache inconsistent

**Cancellation Guarantees (Invariants SWC.F.1, SWC.F.1a, SWC.F.1b)**:
- Rebalance Execution MUST support cancellation at all stages
- Rebalance Execution MUST yield immediately when cancelled
- Cancelled execution MUST NOT leave cache inconsistent

**State Safety**:
- **Atomicity**: All cache mutations are atomic (Invariant SWC.B.2)
- **Consistency**: `Storage` data and `Storage.Range` always consistent (Invariant SWC.B.1)
- **Contiguity**: Cache data never contains gaps (Invariant SWC.A.12b)
- **Idempotence**: Multiple cancellations are safe

---

## State Invariants by State

**In Uninitialized**:
- `IsInitialized == false`; `Storage` contains no data; `NoRebalanceRange == null`
- User Path is read-only (no mutations)
- Rebalance Execution is not active (activates after first intent)

**In Initialized**:
- `Storage` data and `Storage.Range` consistent (Invariant SWC.B.1)
- Cache is contiguous (Invariant SWC.A.12b)
- User Path is read-only (Invariant SWC.A.12)
- Rebalance Execution is not active

**In Rebalancing**:
- `Storage` data and `Storage.Range` remain consistent (Invariant SWC.B.1)
- Cache is contiguous (Invariant SWC.A.12b)
- User Path may cause cancellation but NOT mutate (Invariants SWC.A.2, SWC.A.2a)
- Rebalance Execution is active and sole writer (Invariant SWC.F.2)
- Rebalance Execution is cancellable (Invariant SWC.F.1)
- Single-writer architecture: no race conditions possible

---

## Worked Examples

### Example 1: Cold Start

```
State: Uninitialized
User requests [100, 200]
→ User Path fetches [100, 200] from IDataSource
→ User Path returns data to user immediately
→ User Path publishes intent with delivered data [100, 200]
→ Rebalance Execution calls Storage.Rematerialize([100,200]), sets IsInitialized=true
State: Initialized
```

### Example 2: Expansion During Rebalancing

```
State: Initialized
Storage.Range = [100, 200]

User requests [150, 250]
→ User Path reads [150,200] from cache, fetches [201,250] from IDataSource
→ User Path returns assembled data to user
→ User Path publishes intent with delivered data [150, 250]
→ Decision validates: rebalance necessary → schedules R1 for DesiredCacheRange=[50,300]
State: Rebalancing (R1 executing)

User requests [200, 300] (before R1 completes)
→ User Path reads/fetches data (no cache mutation)
→ User Path returns data to user
→ User Path publishes intent with delivered data [200, 300]
→ Decision validates: new rebalance necessary → cancels R1, schedules R2
State: Rebalancing (R2 executing with new DesiredCacheRange)
```

### Example 3: Full Cache Miss During Rebalancing

```
State: Rebalancing
Storage.Range = [100, 200]
R1 executing for DesiredCacheRange = [50, 250]

User requests [500, 600] (no intersection with Storage.Range)
→ User Path fetches [500, 600] from IDataSource (full miss)
→ User Path returns data to user
→ User Path publishes intent with delivered data [500, 600]
→ Decision validates: new rebalance necessary → cancels R1, schedules R2
State: Rebalancing (R2 executing, will replace cache at DesiredCacheRange=[450,650])
```

---

## Edge Cases

- Cancellation may cause a rebalancing execution to stop early; atomicity guarantees prevent partial-state publication.
- Multiple rapid cancellations are safe; the single-writer architecture and atomic Rematerialize prevent inconsistency.

## Limitations

- This is a conceptual machine; internal implementation may use additional internal markers.
- The "Rebalancing" state is from the system's perspective; from the user's perspective the cache is always "Initialized" and serving requests.

---

## See Also

- `docs/sliding-window/invariants.md` — formal invariants (Sections A, B, D, F)
- `docs/sliding-window/scenarios.md` — temporal scenario walkthroughs
- `docs/sliding-window/diagnostics.md` — observing state transitions in production
