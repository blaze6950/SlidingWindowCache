# Sliding Window Cache — Cache State Machine

This document defines the formal state machine for the Sliding Window Cache, clarifying state transitions, mutation ownership, and concurrency control.

> **📖 For related architectural concepts, see:**
> - [Architecture Model](architecture-model.md) - Single-writer architecture, coordination mechanisms
> - [Invariants](invariants.md) - State invariants and constraints
> - [Scenario Model](scenario-model.md) - Temporal behavior and user scenarios

---

## States

The cache exists in one of three states:

### 1. **Uninitialized**
- **Definition:** Cache has no data and no range defined
- **Characteristics:**
  - `CurrentCacheRange == null`
  - `CacheData == null`
  - `LastRequestedRange == null`
  - `NoRebalanceRange == null`

### 2. **Initialized**
- **Definition:** Cache contains valid data corresponding to a defined range
- **Characteristics:**
  - `CurrentCacheRange != null`
  - `CacheData != null`
  - `CacheData` is consistent with `CurrentCacheRange` (Invariant 11)
  - Cache is contiguous (no gaps, Invariant 9a)
  - System is ready to serve user requests

### 3. **Rebalancing**
- **Definition:** Background normalization is in progress
- **Characteristics:**
  - Cache remains in `Initialized` state from external perspective
  - User Path continues to serve requests normally
  - Rebalance Execution is mutating cache asynchronously
  - Rebalance can be cancelled at any time by User Path

---

## State Transitions

```
┌─────────────────┐
│  Uninitialized  │
└────────┬────────┘
         │
         │ U1: First User Request
         │ (User Path populates cache)
         ▼
┌─────────────────┐
│   Initialized   │◄──────────┐
└────────┬────────┘           │
         │                    │
         │ Any User Request   │
         │ triggers rebalance │
         ▼                    │
┌─────────────────┐           │
│  Rebalancing    │           │
└────────┬────────┘           │
         │                    │
         │ Rebalance          │
         │ completes          │
         └────────────────────┘
         
         (User Request during Rebalancing)
         ┌────────────────────┐
         │ Cancel Rebalance   │
         │ Return to          │
         │ Initialized        │
         └────────────────────┘
```

---

## Transition Details

### T1: Uninitialized → Initialized (Cold Start)
- **Trigger:** First user request (Scenario U1)
- **Actor:** Rebalance Execution (NOT User Path)
- **Sequence:**
  1. User Path fetches `RequestedRange` from IDataSource
  2. User Path returns data to user immediately
  3. User Path publishes intent with delivered data
  4. Rebalance Execution writes to cache (first cache write)
- **Mutation:** Performed by Rebalance Execution ONLY (single-writer)
  - Set `CacheData` = delivered data from intent
  - Set `CurrentCacheRange` = delivered range
  - Set `LastRequestedRange` = `RequestedRange`
- **Atomicity:** Changes applied atomically (Invariant 12)
- **Postcondition:** Cache enters `Initialized` state after rebalance execution completes
- **Note:** User Path is read-only; initial cache population is performed by Rebalance Execution

### T2: Initialized → Rebalancing (Normal Operation)
- **Trigger:** User request (any scenario)
- **Actor:** User Path (reads), Rebalance Executor (writes)
- **Sequence:**
  1. User Path reads from cache or fetches from IDataSource (NO cache mutation)
  2. User Path returns data to user immediately
  3. User Path publishes intent with delivered data
  4. **Decision-driven validation:** Rebalance Decision Engine validates necessity via multi-stage pipeline (THE authority)
  5. **Validation-driven cancellation:** If validation confirms NEW rebalance is necessary, pending rebalance is cancelled and new execution scheduled (coordination mechanism)
  6. **Work avoidance:** If validation rejects (NoRebalanceRange containment, pending coverage, Desired==Current), no cancellation occurs and execution skipped entirely
  7. Rebalance Execution writes to cache (background, only if validated as necessary)
- **Mutation:** Performed by Rebalance Execution ONLY (single-writer architecture)
  - User Path does NOT mutate cache, LastRequested, or NoRebalanceRange (read-only)
  - Rebalance Execution normalizes cache to DesiredCacheRange (only if validated)
- **Concurrency:** User Path is read-only; no race conditions
- **Cancellation Model:** Mechanical coordination tool (prevents concurrent executions), NOT decision mechanism; validation determines necessity
- **Postcondition:** Cache logically enters `Rebalancing` state (background process active, only if all validation stages passed)

### T3: Rebalancing → Initialized (Rebalance Completion)
- **Trigger:** Rebalance execution completes successfully
- **Actor:** Rebalance Executor (sole writer)
- **Mutation:** Performed by Rebalance Execution ONLY
  - Use delivered data from intent as authoritative base
  - Fetch missing data for `DesiredCacheRange` (only truly missing parts)
  - Merge delivered data with fetched data
  - Trim to `DesiredCacheRange` (normalization)
  - Set `CacheData` and `CurrentCacheRange` via `Rematerialize()`
  - Set `LastRequestedRange` = original requested range from intent
  - Recompute `NoRebalanceRange`
- **Atomicity:** Changes applied atomically (Invariant 12)
- **Postcondition:** Cache returns to stable `Initialized` state

### T4: Rebalancing → Initialized (User Request MAY Cancel Rebalance)
- **Trigger:** User request arrives during rebalance execution (Scenarios C1, C2)
- **Actor:** User Path (publishes intent), Rebalance Decision Engine (validates and determines necessity), Rebalance Execution (yields if cancelled)
- **Sequence:**
  1. User Path reads from cache or fetches from IDataSource (NO cache mutation)
  2. User Path returns data to user immediately
  3. User Path publishes new intent with delivered data
  4. **Decision Engine validates:** Multi-stage analytical pipeline determines if NEW rebalance is necessary (THE authority)
  5. **Validation confirms necessity** → Pending rebalance is cancelled and new execution scheduled (coordination via cancellation token)
  6. **Validation rejects necessity** → Pending rebalance continues undisturbed (no cancellation, work avoidance)
  7. If cancelled: Rebalance yields; new rebalance uses new intent's delivered data (if validated)
- **Critical Principle:** User Path does NOT decide cancellation; Decision Engine validation determines necessity, cancellation is mechanical coordination
- **Priority Model:** User Path priority enforced via validation-driven cancellation, not automatic cancellation on every request
- **Cancellation Semantics:** Mechanical coordination tool (single-writer architecture), NOT decision mechanism; prevents concurrent executions, not duplicate decision-making
- **Note:** "User Request MAY Cancel" = cancellation occurs ONLY when validation confirms new rebalance necessary

---

## Mutation Ownership Matrix

| State         | User Path Mutations | Rebalance Execution Mutations                                                                                        |
|---------------|---------------------|----------------------------------------------------------------------------------------------------------------------|
| Uninitialized | ❌ None              | ✅ Initial cache write (after first user request)                                                                     |
| Initialized   | ❌ None              | ❌ Not active                                                                                                         |
| Rebalancing   | ❌ None              | ✅ All cache mutations (expand, trim, write to cache/LastRequested/NoRebalanceRange)<br>⚠️ MUST yield on cancellation |

### Mutation Rules Summary

**User Path mutations (Invariant 8 - NEW):**
- ❌ **NONE** - User Path is read-only with respect to cache state
- User Path NEVER calls `Cache.Rematerialize()`
- User Path NEVER writes to `LastRequested`
- User Path NEVER writes to `NoRebalanceRange`

**Rebalance Execution mutations (Invariant 36, 36a):**
1. Uses delivered data from intent as authoritative base
2. Expanding to `DesiredCacheRange` (fetch only truly missing ranges)
3. Trimming excess data outside `DesiredCacheRange`
4. Writing to `Cache.Rematerialize()` (cache data and range)
5. Writing to `LastRequested`
6. Recomputing and writing to `NoRebalanceRange`

**Single-Writer Architecture (Invariant -1):**
- User Path **NEVER** mutates cache (read-only)
- Rebalance Execution is the **SOLE WRITER** of all cache state
- User Path **cancels rebalance** to prevent interference (priority via cancellation)
- Rebalance Execution **MUST yield** immediately on cancellation (Invariant 34a)
- No race conditions possible (single-writer eliminates mutation conflicts)

---

## Concurrency Semantics

### Cancellation Protocol

User Path has priority but does NOT mutate cache:

1. **Pre-operation cancellation:** User Path publishes new intent (atomically supersedes any prior intent); background loop cancels active rebalance execution when it processes the new intent
2. **Read/fetch:** User Path reads from cache or fetches from IDataSource (NO mutation)
3. **Immediate return:** User Path returns data to user (never waits)
4. **Intent publication:** User Path emits intent with delivered data
5. **Rebalance yields:** Background rebalance stops if cancelled
6. **New rebalance:** New intent triggers new rebalance execution with new delivered data

### Cancellation Guarantees (Invariants 34, 34a, 34b)

- Rebalance Execution **MUST support cancellation** at all stages
- Rebalance Execution **MUST yield** to User Path immediately
- Cancelled execution **MUST NOT leave cache inconsistent**

### State Safety

- **Atomicity:** All cache mutations are atomic (Invariant 12)
- **Consistency:** `CacheData ↔ CurrentCacheRange` always consistent (Invariant 11)
- **Contiguity:** Cache data never contains gaps (Invariant 9a)
- **Idempotence:** Multiple cancellations are safe

---

## State Invariants by State

### In Uninitialized State:
- ✅ All range and data fields are null
- ✅ User Path is read-only (no mutations)
- ✅ Rebalance Execution is not active (will activate after first user request)

### In Initialized State:
- ✅ `CacheData ↔ CurrentCacheRange` consistent (Invariant 11)
- ✅ Cache is contiguous (Invariant 9a)
- ✅ User Path is read-only (Invariant 8 - NEW)
- ✅ Rebalance Execution is not active

### In Rebalancing State:
- ✅ `CacheData ↔ CurrentCacheRange` remain consistent (Invariant 11)
- ✅ Cache is contiguous (Invariant 9a)
- ✅ User Path may cancel but NOT mutate (Invariants 0, 0a)
- ✅ Rebalance Execution is active and sole writer (Invariant 36)
- ✅ Rebalance Execution is cancellable (Invariant 34)
- ✅ **Single-writer architecture** (no race conditions)

---

## Examples

### Example 1: Cold Start → Initialized
```
State: Uninitialized
User requests [100, 200]
→ User Path fetches [100, 200] from IDataSource
→ User Path returns data to user immediately
→ User Path publishes intent with delivered data
→ Rebalance Execution writes to cache (first cache write)
→ Sets CacheData, CurrentCacheRange, LastRequested
→ Triggers rebalance (fire-and-forget)
State: Initialized
```

### Example 2: Expansion During Rebalancing
```
State: Initialized
CurrentCacheRange = [100, 200]

User requests [150, 250]
→ User Path reads [150, 200] from cache, fetches [200, 250] from IDataSource
→ User Path returns assembled data to user
→ User Path publishes intent with delivered data [150, 250]
→ Triggers rebalance R1 for DesiredCacheRange = [50, 300]
State: Rebalancing (R1 executing in background)

User requests [200, 300] (before R1 completes)
→ CANCELS R1 (Invariant 0a - User Path priority)
→ User Path reads/fetches data (NO cache mutation)
→ User Path returns data [200, 300] to user
→ User Path publishes new intent with delivered data [200, 300]
→ Triggers rebalance R2 for new DesiredCacheRange
State: Rebalancing (R2 executing)
```

### Example 3: Full Cache Miss During Rebalancing
```
State: Rebalancing
CurrentCacheRange = [100, 200]
Rebalance R1 executing for DesiredCacheRange = [50, 250]

User requests [500, 600] (no intersection)
→ CANCELS R1 (Invariant 0a - User Path priority)
→ User Path fetches [500, 600] from IDataSource (cache miss)
→ User Path returns data to user
→ User Path publishes intent with delivered data [500, 600]
→ Triggers rebalance R2 for new DesiredCacheRange = [450, 650]
State: Rebalancing (R2 executing - will eventually replace cache)
```

---

## Architectural Summary

This state machine enforces three critical architectural constraints:

1. **Single-Writer Architecture:** Only Rebalance Execution mutates cache state (Invariant 36)
2. **User Path Read-Only:** User Path never mutates cache, LastRequested, or NoRebalanceRange (Invariant 8)
3. **User Priority via Cancellation:** User requests cancel rebalance to prevent interference, not for mutation exclusion (Invariants 0, 0a)

The state machine guarantees:
- Fast, non-blocking user access (Invariants 1, 2)
- Eventual convergence to optimal cache shape (Invariant 23)
- Atomic, consistent cache state (Invariants 11, 12)
- No race conditions (single-writer eliminates mutation conflicts)
- Safe cancellation at any time (Invariants 34, 34a, 34b)