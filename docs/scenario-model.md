# Sliding Window Cache — Scenario Model (Temporal Perspective)

This document describes the complete behavioral model of the Sliding Window Cache
from a temporal and procedural perspective.

The goal is to explicitly capture all possible execution scenarios and paths
before projecting them onto architectural components, responsibilities, and APIs.

The model is structured into three independent but sequentially connected paths
(one logically follows another):

1. User Path — synchronous, user-facing behavior
2. Rebalance Decision Path — validation and decision making
3. Rebalance Execution Path — asynchronous cache normalization

---

## Base Definitions

The following terms are used consistently across all scenarios:

- **RequestedRange**  
  A range requested by the user.

- **LastRequestedRange**  
  The most recent range served by the User Path.

- **CurrentCacheRange**  
  The range of data currently stored in the cache.

- **CacheData**  
  The data corresponding to CurrentCacheRange.

- **DesiredCacheRange**  
  The target cache range computed from RequestedRange and cache configuration
  (left/right expansion sizes, thresholds, etc.).

- **NoRebalanceRange**  
  A range inside which cache rebalance is not required.

- **IDataSource**  
  A sequential, range-based data source.

---

## Testing Infrastructure Note

**Deterministic Synchronization**: Tests use `cache.WaitForIdleAsync()` to synchronize with
background rebalance completion. This is infrastructure/testing API implementing an
observe-and-stabilize pattern based on Task lifecycle tracking.

This synchronization mechanism is **not part of the domain flow** described below.
It exists solely to enable deterministic testing without timing dependencies.

See [Architecture Model](architecture-model.md) for implementation details.

---

# I. USER PATH — User-Facing Scenarios

*(Synchronous — executed in the user's thread)*

The User Path is responsible only for:

- deciding how to serve the user request
- selecting the data source (cache or IDataSource)
- triggering rebalance (without executing it)

---

## User Scenario U1 — Cold Cache Request

### Preconditions

- `LastRequestedRange == null`
- `CurrentCacheRange == null`
- `CacheData == null`

### Action Sequence

1. User requests RequestedRange
2. Cache detects that it is not initialized
3. Cache requests RequestedRange from IDataSource in the user thread  
   (this is unavoidable because the user request must be served)
4. Received data:
    - is stored as CacheData
    - CurrentCacheRange is set to RequestedRange
    - LastRequestedRange is set to RequestedRange
5. Rebalance is triggered asynchronously (fire-and-forget background work)
6. Data is immediately returned to the user

**Note:**  
The User Path does not expand the cache beyond RequestedRange.

---

## User Scenario U2 — Full Cache Hit (Exact Match with LastRequestedRange)

### Preconditions

- Cache is initialized
- `RequestedRange == LastRequestedRange`
- `CurrentCacheRange.Contains(RequestedRange) == true`

### Action Sequence

1. User requests RequestedRange
2. Cache detects a full cache hit
3. Data is read from CacheData
4. LastRequestedRange is updated
5. Rebalance is triggered asynchronously  
   (because `NoRebalanceRange.Contains(RequestedRange)` may be false)
6. Data is returned to the user

---

## User Scenario U3 — Full Cache Hit (Shifted Range)

### Preconditions

- Cache is initialized
- `RequestedRange != LastRequestedRange`
- `CurrentCacheRange.Contains(RequestedRange) == true`

### Action Sequence

1. User requests RequestedRange
2. Cache detects that all requested data is available
3. Subrange is read from CacheData
4. LastRequestedRange is updated
5. Rebalance is triggered asynchronously
6. Data is returned to the user

---

## User Scenario U4 — Partial Cache Hit

### Preconditions

- Cache is initialized
- `CurrentCacheRange.Intersects(RequestedRange) == true`
- `CurrentCacheRange.Contains(RequestedRange) == false`

### Action Sequence

1. User requests RequestedRange
2. Cache computes intersection with CurrentCacheRange
3. Missing part is synchronously requested from IDataSource
4. Cache:
    - merges cached and newly fetched data (cache expansion)
    - does **not** trim excess data
    - updates CurrentCacheRange to cover both old and new data
5. LastRequestedRange is updated
6. Rebalance is triggered asynchronously
7. RequestedRange data is returned to the user

**Note:**  
Cache expansion is permitted here because RequestedRange intersects CurrentCacheRange,
preserving cache contiguity. Excess data may temporarily remain in CacheData for reuse during Rebalance.

---

## User Scenario U5 — Full Cache Miss (Jump)

### Preconditions

- Cache is initialized
- `CurrentCacheRange.Intersects(RequestedRange) == false`

### Action Sequence

1. User requests RequestedRange
2. Cache determines that RequestedRange does NOT intersect with CurrentCacheRange
3. **Cache contiguity enforcement:** Cached data cannot be preserved (would create gaps)
4. RequestedRange is synchronously requested from IDataSource
5. Cache:
    - **fully replaces** CacheData with new data
    - **fully replaces** CurrentCacheRange with RequestedRange
6. LastRequestedRange is updated
7. Rebalance is triggered asynchronously
8. Data is returned to the user

**Critical Note:**  
Partial cache expansion is FORBIDDEN in this case, as it would create logical gaps
and violate the Cache Contiguity Rule (Invariant 9a). The cache MUST remain contiguous.

---

# II. REBALANCE DECISION PATH — Decision Scenarios

> **📖 For architectural explanation of decision-driven execution, see:** [Architecture Model - Decision-Driven Execution](architecture-model.md#rebalance-validation-vs-cancellation)

> **⚡ Execution Context:** This entire path executes in a **dedicated background thread** (IntentController.ProcessIntentsAsync loop). The user thread returns immediately after publishing the intent (fire-and-forget). See IntentController.cs:228-230 for implementation details.

**Core Principle**: Rebalance necessity is determined by multi-stage analytical validation, not by intent existence.

Publishing a rebalance intent does NOT guarantee execution. The **Rebalance Decision Engine**
is the sole authority for determining rebalance necessity through a multi-stage validation pipeline:

1. **Stage 1**: Current Cache NoRebalanceRange validation (fast-path rejection)
2. **Stage 2**: Pending Desired Cache NoRebalanceRange validation (anti-thrashing)
3. **Stage 3**: Compute DesiredCacheRange from RequestedRange + configuration
4. **Stage 4**: DesiredCacheRange vs CurrentCacheRange equality check (no-op prevention)

Execution occurs **ONLY if ALL validation stages confirm necessity**. The decision path
may determine that execution is not needed (NoRebalanceRange containment, pending
rebalance coverage, or DesiredRange == CurrentRange), in which case execution is
skipped entirely. Additionally, intents may be superseded or cancelled before
execution begins.

The Rebalance Decision Path:

- never mutates cache state (pure analytical logic, CPU-only)
- may result in a no-op (work avoidance through validation)
- determines whether execution is required (THE authority for necessity determination)

This path is always triggered by the User Path, but validation determines execution.

---

## Decision Scenario D1 — Rebalance Blocked by NoRebalanceRange (Stage 1 Validation)

### Condition

- `NoRebalanceRange.Contains(RequestedRange) == true`

### Sequence

1. Decision path starts (Stage 1: Current Cache NoRebalanceRange validation)
2. NoRebalanceRange computed from CurrentCacheRange is checked
3. RequestedRange is fully contained within NoRebalanceRange
4. Validation rejects: rebalance unnecessary (current cache provides sufficient buffer)
5. Fast return — rebalance is skipped  
   (Execution Path is not started)

**Rationale**: Current cache already provides adequate coverage around the requested range.
No I/O or cache mutation needed.

---

## Decision Scenario D2 — Rebalance Allowed but Desired Equals Current (Stage 4 Validation)

### Condition

- `NoRebalanceRange.Contains(RequestedRange) == false` (Stage 1 passed)
- `DesiredCacheRange == CurrentCacheRange`

### Sequence

1. Decision path starts
2. Stage 1 validation: NoRebalanceRange check — no fast return
3. Stage 3: DesiredCacheRange is computed from RequestedRange + config
4. Stage 4 validation: Desired equals Current (cache already in optimal configuration)
5. Validation rejects: rebalance unnecessary (no geometry change needed)
6. Fast return — rebalance is skipped  
   (Execution Path is not started)

**Rationale**: Cache is already sized and positioned optimally for this request.
No I/O or cache mutation needed.

---

## Decision Scenario D3 — Rebalance Required (All Validation Stages Passed)

### Condition

- `NoRebalanceRange.Contains(RequestedRange) == false` (Stage 1 passed)
- `DesiredCacheRange != CurrentCacheRange` (Stage 4 confirms change needed)

### Sequence

1. Decision path starts
2. Stage 1 validation: NoRebalanceRange check — no fast return
3. Stage 2 validation (if applicable): Pending Desired Cache NoRebalanceRange check — no rejection
4. Stage 3: DesiredCacheRange is computed from RequestedRange + config
5. Stage 4 validation: Desired differs from Current (cache geometry change required)
6. Validation confirms: rebalance necessary
7. Execution Path is started asynchronously

**Rationale**: ALL validation stages confirm that cache requires rebalancing to optimal configuration.
Rebalance Execution will normalize cache to DesiredCacheRange using delivered data as authoritative source.

---

## Decision Scenario D1b — Rebalance Blocked by Pending Desired Cache (Stage 2 Validation - Anti-Thrashing)

### Condition

- Stage 1 passed: `NoRebalanceRange(CurrentCacheRange).Contains(RequestedRange) == false`
- Stage 2 check: Pending rebalance exists with PendingDesiredCacheRange
- `NoRebalanceRange(PendingDesiredCacheRange).Contains(RequestedRange) == true`

### Sequence

1. Decision path starts
2. Stage 1 validation: Current Cache NoRebalanceRange check — no fast return
3. Stage 2 validation: Check if pending rebalance exists
4. If pending rebalance exists, compute NoRebalanceRange from PendingDesiredCacheRange
5. RequestedRange is fully contained within pending NoRebalanceRange
6. Validation rejects: rebalance unnecessary (pending execution will satisfy this request)
7. Fast return — rebalance is skipped  
   (Execution Path is not started, existing pending rebalance continues)

**Purpose**: Anti-thrashing mechanism preventing oscillating cache geometry.

**Rationale**: A rebalance is already scheduled/executing that will position the cache
optimally for this request. Starting a new rebalance would cancel the pending one,
potentially causing thrashing if user access pattern is rapidly changing. Better to let
the pending rebalance complete.

**Note**: Stage 2 is fully implemented — `RebalanceDecisionEngine.Evaluate()` checks `lastExecutionRequest?.DesiredNoRebalanceRange` to determine if a pending execution already covers the requested range.

---

# III. REBALANCE EXECUTION PATH — Execution Scenarios

The Execution Path is the only path that:

- performs I/O
- mutates cache state
- normalizes cache structure

---

## Rebalance Scenario R1 — Build from Scratch

### Preconditions

- `CurrentCacheRange == null`

**OR**

- `DesiredCacheRange.Intersects(CurrentCacheRange) == false`

### Sequence

1. DesiredCacheRange is requested from IDataSource
2. CacheData is fully replaced
3. CurrentCacheRange is set to DesiredCacheRange
4. NoRebalanceRange is computed

---

## Rebalance Scenario R2 — Expand Cache (Partial Overlap)

### Preconditions

- `DesiredCacheRange.Intersects(CurrentCacheRange) == true`
- `DesiredCacheRange != CurrentCacheRange`

### Sequence

1. Missing subranges are computed
2. Missing data is requested from IDataSource
3. Data is merged with existing CacheData
4. CacheData is normalized to DesiredCacheRange
5. NoRebalanceRange is updated

---

## Rebalance Scenario R3 — Shrink / Normalize Cache

### Preconditions

- `CurrentCacheRange.Contains(DesiredCacheRange) == true`

### Sequence

1. CacheData is trimmed to DesiredCacheRange
2. CurrentCacheRange is updated
3. NoRebalanceRange is recomputed

---

# IV. CONCURRENCY & CANCELLATION SCENARIOS

This section describes temporal and concurrency-related scenarios
that occur when user requests arrive while rebalance logic is pending
or already executing.

These scenarios are fundamental to the **Fast User Access** philosophy
and define how obsolete background work must be handled.

---

## Concurrency Principles

The Sliding Window Cache follows these rules:

1. User Path is never blocked by rebalance logic
2. Multiple rebalance triggers may overlap in time
3. Only the **latest rebalance intent** is relevant
4. Obsolete rebalance work must be cancelled or abandoned
5. Rebalance execution must support cancellation
6. Cache state may be temporarily inconsistent but must be overwrite-safe

---

## Concurrency Scenario C1 — Rebalance Triggered While Previous Rebalance Is Pending

### Situation

- User request U₁ triggers rebalance R₁ (fire-and-forget)
- R₁ has not started execution yet (queued or delayed)
- User request U₂ arrives before R₁ executes

### Expected Behavior

1. **U₂ cancels any pending rebalance work before performing its own cache mutations**
2. User Path for U₂ executes normally and immediately
3. A new rebalance trigger R₂ is issued
4. R₁ is cancelled or marked obsolete
5. Only R₂ is allowed to proceed to execution

**Outcome:**  
No rebalance work is executed based on outdated user intent. User Path always has priority.

---

## Concurrency Scenario C2 — Rebalance Triggered While Previous Rebalance Is Executing

### Situation

- User request U₁ triggers rebalance R₁
- R₁ has already started execution (I/O or merge in progress)
- User request U₂ arrives and triggers rebalance R₂

### Expected Behavior

1. **U₂ cancels ongoing rebalance execution R₁ before performing its own cache mutations**
2. User Path for U₂ executes normally and immediately
3. R₂ becomes the latest rebalance intent
4. R₁ receives a cancellation signal
5. R₁:
    - stops execution as early as possible, OR
    - completes but discards its results
6. R₂ proceeds with fresh DesiredCacheRange

**Outcome:**  
Cache normalization reflects the most recent user access pattern. User Path and Rebalance Execution never mutate cache
concurrently.

---

## Concurrency Scenario C3 — Multiple Rapid User Requests (Spike / Random Access)

### Situation

- User produces a burst of requests: U₁, U₂, U₃, ..., Uₙ
- Each request triggers rebalance
- Rebalance execution cannot keep up with trigger rate

### Expected Behavior

1. User Path always serves requests independently
2. Rebalance triggers are debounced or superseded
3. At most one rebalance execution is active at any time
4. Only the final rebalance intent is executed
5. All intermediate rebalance work is cancelled or skipped

**Outcome:**  
System remains responsive and converges to a stable cache state
once user activity slows down.

---

## Cancellation and State Safety Guarantees

To support these scenarios, the following guarantees must hold:

- Rebalance execution must be cancellable
- Cache mutations must be atomic or overwrite-safe
- Partial rebalance results must not corrupt cache state
- Final rebalance always produces a fully normalized cache

Temporary inconsistency is acceptable.
Permanent inconsistency is not.

---

## Design Note

Concurrency handling is a **behavioral requirement**, not an implementation detail.

The specific mechanism (cancellation tokens, versioning, actors, single-flight execution)
is intentionally left unspecified and will be defined during architectural projection.

---

# Final Picture

- User Path is fast, synchronous, and always responds
- Decision Path is lightweight and often results in no-op
- Execution Path is heavy, isolated, and asynchronous

All scenarios:

- are responsibility-isolated
- are expressed as temporal processes
- are independent of specific storage implementations

---

## Notes and Considerations

1. Decision Path and Execution Path should not execute in the user thread.
   The Decision Path is lightweight, CPU-only (no I/O), and often results in no-op.
   The Execution Path involves asynchronous I/O (IDataSource access).

   Using a ThreadPool-based or background scheduling approach aligns with
   the core philosophy of SlidingWindowCache:
   **fast user access with minimal mandatory work in the user thread**.

2. Rebalance Execution scenarios (R1–R3) may be implemented as a unified pipeline:
    - compute missing ranges
    - request missing data
    - merge with existing CacheData (if any)
    - trim to DesiredCacheRange
    - recompute NoRebalanceRange

   This document intentionally keeps these scenarios separate, as they describe
   **semantic behavior**, not implementation strategy.
