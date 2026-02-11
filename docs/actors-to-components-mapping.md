# Sliding Window Cache — Actors to Components Mapping

This document maps the **conceptual system actors** defined by the Scenario Model
to **concrete architectural components** of the Sliding Window Cache library.

The purpose of this document is:

- to fix architectural intent
- to clarify responsibility boundaries
- to guide refactoring and further development
- to serve as long-term documentation for contributors and reviewers

Actors are **stable roles**, not execution paths and not necessarily 1:1 with classes.

---

## High-Level Structure

### Execution Context Flow

```
═══════════════════════════════════════════════════════════
User Thread
═══════════════════════════════════════════════════════════

┌───────────────────────┐
│ SlidingWindowCache    │ ← Public Facade
└───────────┬───────────┘
            │
            ▼
┌───────────────────────┐
│ UserRequestHandler    │ ← Fast user-facing logic
└───────────┬───────────┘
            │
            │ publish rebalance intent (fire-and-forget)
            │
            ▼

═══════════════════════════════════════════════════════════
Background / ThreadPool
═══════════════════════════════════════════════════════════

┌───────────────────────────┐
│ RebalanceIntentManager    │ ← Temporal Authority
│                           │   • debounce / cancel obsolete
│                           │   • enforce single-flight
└───────────┬───────────────┘   • schedule execution
            │
            │ invoke decision pipeline
            │
            ▼
┌───────────────────────────┐
│ RebalanceDecisionEngine   │ ← Pure Decision Logic
│                           │   • NoRebalanceRange check
│ + CacheGeometryPolicy     │   • DesiredCacheRange computation
└───────────┬───────────────┘   • allow/block execution
            │
            │ if execution allowed
            │
            ▼
┌───────────────────────────┐
│ RebalanceExecutor         │ ← Mutating Actor
└───────────┬───────────────┘
            │
            │ atomic mutation
            │
            ▼
┌───────────────────────────┐
│ CacheStateManager         │ ← Consistency Guardian
└───────────────────────────┘
```

---

## 1. SlidingWindowCache (Public Facade)

### Role

The single public entry point of the library.

### Implementation

**Implemented as:** `WindowCache<TRange, TData, TDomain>` class

### Responsibilities

- Exposes the public API
- Owns configuration and lifecycle
- Wires internal components together (composition root)
- **Delegates all user requests to UserRequestHandler**
- Does **not** implement business logic itself

### Actor Coverage

- Acts as a **composition root** and **pure facade**
- Does **not** directly correspond to a scenario actor
- All behavioral logic is delegated to internal actors

### Architecture Pattern

WindowCache implements the **Facade Pattern**:
- Public interface: `IWindowCache<TRange, TData, TDomain>.GetDataAsync(...)`
- Internal delegation: Forwards all requests to `UserRequestHandler.HandleRequestAsync(...)`
- Composition: Wires together all internal actors (UserRequestHandler, IntentController, DecisionEngine, Executor)

### Notes

This component should remain thin.
It delegates all behavioral logic to internal actors.

**Key architectural principle:** WindowCache is a **pure facade** - it contains no business logic, only composition and delegation.

---

## 2. UserRequestHandler

*(Fast Path / Read Path Actor)*

### Mapped Actor

**User Path (Fast Path / Read Path Actor)**

### Implementation

**Implemented as:** internal class `UserRequestHandler<TRange, TData, TDomain>` in `UserPath/` namespace

### Execution Context

**Lives in: User Thread**

### Responsibilities

- Handles user requests synchronously
- Decides how to serve RequestedRange:
    - from cache
    - from IDataSource
    - or mixed
- Updates:
    - LastRequestedRange
    - CacheData / CurrentCacheRange **only to cover RequestedRange**
- Triggers rebalance intent
- Never blocks on rebalance

### Critical Contract

```
Every user access produces a rebalance intent.
The UserRequestHandler NEVER invokes decision logic.
```

### Explicit Non-Responsibilities

- No cache normalization
- No trimming or shrinking
- No rebalance execution
- No concurrency control
- **NEVER checks NoRebalanceRange** (belongs to DecisionEngine)
- **NEVER computes DesiredCacheRange** (belongs to GeometryPolicy)
- **NEVER decides whether to rebalance** (belongs to DecisionEngine)

### Key Guarantees

- Always returns exactly RequestedRange
- Always responds, regardless of rebalance state

### Implementation Note

Invoked by WindowCache via delegation:
```csharp
// WindowCache.GetDataAsync(...) implementation:
return _userRequestHandler.HandleRequestAsync(requestedRange, cancellationToken);
```

---

## 3. RebalanceDecisionEngine

*(Pure Decision Actor)*

### Mapped Actor

**Rebalance Decision Engine**

### Execution Context

**Lives in: Background / ThreadPool**

### Visibility

- **Not visible to User Path**
- Invoked only by RebalanceScheduler
- May execute many times, results may be discarded

### Critical Rule

```
DecisionEngine lives strictly inside the background contour.
```

### Responsibilities

- Evaluates whether rebalance is required
- Checks:
    - NoRebalanceRange
    - DesiredCacheRange vs CurrentCacheRange
- Produces a boolean decision

### Characteristics

- Pure
- Deterministic
- Side-effect free
- Does not mutate cache state

### Notes

This component should be:

- easily testable
- fully synchronous
- independent of execution context

**Not a top-level actor** — internal tool of IntentManager/Executor pipeline.

---

## 4. CacheGeometryPolicy

*(Configuration & Policy Actor)*

### Mapped Actor

**Cache Geometry Policy**

### Implementation

**Implemented as:** Two separate components working together as a unified policy:

1. **ThresholdRebalancePolicy**
   - `internal readonly struct ThresholdRebalancePolicy<TRange, TDomain>`
   - File: `src/SlidingWindowCache/CacheRebalance/Policy/ThresholdRebalancePolicy.cs`
   - Computes `NoRebalanceRange`
   - Checks if rebalance is needed based on threshold rules

2. **ProportionalRangePlanner**
   - `internal readonly struct ProportionalRangePlanner<TRange, TDomain>`
   - File: `src/SlidingWindowCache/DesiredRangePlanner/ProportionalRangePlanner.cs`
   - Computes `DesiredCacheRange`
   - Plans canonical cache geometry based on proportional expansion

**Key Principle:** The logical actor (Cache Geometry Policy) is decomposed into 
two cooperating components for separation of concerns. Each component handles 
one aspect of cache geometry: thresholds (when to rebalance) and planning (what 
shape to target).

**Used by:** RebalanceDecisionEngine composes both components to make rebalance decisions.

### Execution Context

**Lives in: Background / ThreadPool** (invoked by RebalanceDecisionEngine)

### Component Responsibilities

#### ThresholdRebalancePolicy (Threshold Rules)
- Computes `NoRebalanceRange` from `CurrentCacheRange` + threshold configuration
- Determines if requested range falls outside no-rebalance zone
- Enforces threshold-based rebalance triggering rules
- Configuration: `LeftThreshold`, `RightThreshold`

#### ProportionalRangePlanner (Shape Planning)
- Computes `DesiredCacheRange` from `RequestedRange` + size configuration
- Defines canonical cache shape by expanding request proportionally
- Independent of current cache contents (pure function of request + config)
- Configuration: `LeftCacheSize`, `RightCacheSize`

### Responsibilities

Together, these components:
- Compute `DesiredCacheRange` [ProportionalRangePlanner]
- Compute `NoRebalanceRange` [ThresholdRebalancePolicy]
- Encapsulate all sliding window rules:
    - left/right sizes [ProportionalRangePlanner]
    - thresholds [ThresholdRebalancePolicy]
    - expansion rules [ProportionalRangePlanner]

### Characteristics

- Stateless (both are readonly structs)
- Fully configuration-driven
- Independent of cache contents
- Pure functions (deterministic, no side effects)

### Notes

This actor defines the **canonical shape** of the cache.

The split into two components reflects separation of concerns:
- **When to rebalance** (threshold-based triggering) → ThresholdRebalancePolicy
- **What shape to target** (desired cache geometry) → ProportionalRangePlanner

Similar to RebalanceIntentManager, this logical actor is internally decomposed 
but externally appears as a unified policy concept.

---

## 5. RebalanceIntentManager

*(Intent & Concurrency Actor)*

### Mapped Actor

**Rebalance Intent Manager**

### Implementation

**Implemented as:** Two internal components working together as a unified actor:

1. **IntentController**
   - `internal class IntentController<TRange, TData, TDomain>`
   - File: `src/SlidingWindowCache/CacheRebalance/IntentController.cs`
   - Owns intent identity and cancellation lifecycle
   - Exposes `CancelPendingRebalance()` and `PublishIntent()` to User Path

2. **RebalanceScheduler (Execution Scheduler)**
   - `internal class RebalanceScheduler<TRange, TData, TDomain>`
   - File: `src/SlidingWindowCache/CacheRebalance/RebalanceScheduler.cs`
   - Owns debounce timing and background execution
   - Orchestrates DecisionEngine → Executor pipeline
   - Ensures single-flight execution
   - **Intentionally stateless** - does not own intent identity
   - **DEBUG-only Task tracking** - provides `WaitForIdleAsync()` for deterministic testing (zero RELEASE overhead)

**Key Principle:** The logical actor (Rebalance Intent Manager) is decomposed into 
two cooperating components for separation of concerns, but externally appears as 
a single unified actor.

### Execution Context

**Lives in: Background / ThreadPool**

### Enhanced Role (Corrected Model)

The Rebalance Intent Manager actor is responsible for:

- **Receiving intents** (on every user request) [Intent Controller responsibility]
- **Intent lifecycle management** (identity, versioning) [Intent Controller responsibility]
- **Cancellation** of obsolete intents [Intent Controller responsibility]
- **Deduplication** and debouncing [Execution Scheduler responsibility]
- **Single-flight execution** enforcement [Execution Scheduler responsibility]
- **Starting background tasks** [Execution Scheduler responsibility]
- **Orchestrating the decision pipeline**: [Execution Scheduler responsibility]
  1. Invoke DecisionEngine
  2. If allowed, invoke Executor
  3. Handle cancellation

### Component Responsibilities

#### Intent Controller (IntentController)
- Owns `CancellationTokenSource` for current intent
- Provides `CancelPendingRebalance()` for User Path priority
- Provides `PublishIntent()` to receive new intents
- Invalidates previous intent when new intent arrives
- Does NOT perform scheduling or timing logic
- Does NOT orchestrate execution pipeline

#### Execution Scheduler (RebalanceScheduler)
- Receives intent + cancellation token from Intent Controller
- Performs debounce delay
- Checks intent validity before execution starts
- Orchestrates DecisionEngine → Executor pipeline
- Ensures only one execution runs at a time (via cancellation)
- Does NOT own intent identity or versioning
- Does NOT decide whether rebalance is logically required
- **DEBUG-only**: Tracks background Task for deterministic synchronization (`WaitForIdleAsync()`)

**Important**: RebalanceScheduler is intentionally stateless and does not own intent identity.
All intent lifecycle, superseding, and cancellation semantics are delegated to the Intent Controller (IntentController).
The scheduler only receives a CancellationToken for each scheduled execution and checks its validity.

### Key Decision Authority

- **When to invoke decision logic** [Scheduler decides after debounce]
- **When to skip execution entirely** [DecisionEngine decides based on logic]

### Owns

- Intent versioning [Intent Controller]
- Cancellation tokens [Intent Controller]
- Scheduling logic [Execution Scheduler]
- Pipeline orchestration [Execution Scheduler]

### Pipeline Orchestration (Philosophy A)

```
IntentManager (Intent Controller)
    ├── manage intent lifecycle
    └── delegate to Scheduler
            ↓
        RebalanceScheduler (Execution Scheduler)
            ├── debounce delay
            ├── check validity
            └── start pipeline
                    ↓
                DecisionEngine
                    ↓
                Executor
```

**Benefits:**
- Clear separation: lifecycle vs. execution
- Intent Controller pattern for versioned operations
- Decision remains pure and testable
- Executor simply executes
- Single Responsibility Principle maintained

### Notes

This is the **temporal authority** of the system.

The internal decomposition is an implementation detail - from an architectural
perspective, this is a single unified actor.

---

## 6. RebalanceExecutor

*(Mutating Actor)*

### Mapped Actor

**Rebalance Executor**

### Responsibilities

- Executes rebalance when authorized
- Performs I/O with IDataSource
- Computes missing ranges
- Merges / trims / replaces cache data
- Produces normalized cache state

### Characteristics

- Asynchronous
- Cancellable
- Heavyweight

### Constraints

- Must be overwrite-safe
- Must respect cancellation
- Must never apply obsolete results

---

## 7. CacheStateManager

*(Consistency & Atomicity Actor)*

### Mapped Actor

**Cache State Manager**

### Responsibilities

- Owns CacheData and CurrentCacheRange
- Applies mutations atomically
- Guards consistency invariants
- Ensures overwrite safety

### Notes

This actor may be:

- a separate component
- or a well-defined internal module

Its **conceptual separation is mandatory** even if physically co-located.

---

## Architectural Intent Summary

| Actor              | Primary Concern         |
|--------------------|-------------------------|
| UserRequestHandler | Speed & availability    |
| DecisionEngine     | Correctness of decision |
| GeometryPolicy     | Deterministic shape     |
| IntentManager      | Time & concurrency      |
| RebalanceExecutor  | Physical mutation       |
| CacheStateManager  | Safety & consistency    |

---

## Execution Context Model

### Corrected Mental Model

```
User Thread
───────────
UserRequestHandler
    ├── serve request (sync)
    └── publish rebalance intent (fire-and-forget)
            │
            ▼
Background / ThreadPool
───────────────────────
RebalanceIntentManager
    ├── debounce / cancel obsolete intents
    ├── enforce single-flight
    └── schedule execution
            │
            ▼
RebalanceDecisionEngine
    ├── NoRebalanceRange check
    ├── DesiredCacheRange computation
    └── no-op or allow execution
            │
            ▼
RebalanceExecutor
    └── mutate cache if allowed
```

### Key Principle

🔑 **DecisionEngine lives strictly within the background contour.**

### Actor Execution Contexts

| Actor                      | Execution Context     | Invoked By               |
|----------------------------|-----------------------|--------------------------|
| UserRequestHandler         | User Thread           | User (public API)        |
| IntentController           | Background/ThreadPool | UserRequestHandler       |
| RebalanceScheduler         | Background/ThreadPool | IntentController         |
| RebalanceDecisionEngine    | Background/ThreadPool | RebalanceScheduler       |
| CacheGeometryPolicy        | Background/ThreadPool | RebalanceDecisionEngine  |
| RebalanceExecutor          | Background/ThreadPool | RebalanceScheduler       |
| CacheStateManager          | Both (with locking)   | Both paths (coordinated) |

### Responsibilities Refixed

#### UserRequestHandler (Updated Role)

- ✅ Serves user requests
- ✅ **Always publishes rebalance intent**
- ❌ **Never** checks NoRebalanceRange
- ❌ **Never** computes DesiredCacheRange
- ❌ **Never** decides "to rebalance or not"

**Contract:** *Every user access produces a rebalance intent.*

#### RebalanceIntentManager (Enhanced Role)

The Rebalance Intent Manager ACTOR (implemented via IntentController + RebalanceScheduler) is the **orchestrator** responsible for:

- ✅ Receiving intent on **every user request** [IntentController]
- ✅ Deduplication and debouncing [RebalanceScheduler]
- ✅ Cancelling obsolete intents [IntentController]
- ✅ Single-flight enforcement [Both components via cancellation]
- ✅ **Launching background task** [RebalanceScheduler]
- ✅ **Deciding when to start decision logic** [RebalanceScheduler]
- ✅ **Deciding when to skip execution** [DecisionEngine via RebalanceScheduler]
- ⚠️ **Intent does not guarantee execution** - execution is opportunistic

**Authority:** *Owns time and concurrency.*

#### RebalanceDecisionEngine (Clarified Role)

**Not a top-level actor** — internal tool of IntentManager/Executor pipeline.

- ❌ Not visible to User Path
- ✅ Invoked only in background
- ✅ Can execute many times
- ✅ Results may be discarded

**Contract:** *Given intent + current snapshot, decide if execution is allowed.*

---

This mapping is **normative**.
Future refactoring must preserve these responsibility boundaries.