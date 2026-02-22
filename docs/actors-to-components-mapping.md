# Sliding Window Cache — Actors to Components Mapping

This document maps the **conceptual system actors** defined by the Scenario Model
to **concrete architectural components** of the Sliding Window Cache library.

> **📖 For detailed architectural explanations, see:**
> - [Architecture Model](architecture-model.md) - Threading model, execution contexts, coordination mechanisms
> - [Component Map](component-map.md) - Complete component catalog with relationships
> - [Actors and Responsibilities](actors-and-responsibilities.md) - Invariant ownership by actor

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
═══════════════════════════════════════════════════════════
User Thread (Synchronous - Publish Intent Only)
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
            │ publish rebalance intent (synchronous)
            │
            ▼
┌───────────────────────────┐
│ IntentController          │ ← Intent Lifecycle & Orchestration
│ (Rebalance Intent Mgr)    │   • publishes intent atomically
│                           │   • signals background loop
└───────────┬───────────────┘   • returns immediately (fire-and-forget)
            │
            │ atomic publish + semaphore signal (returns to user)
            │
            ▼
          RETURN TO USER (User thread ends here) ← 🔄 Background loop picks up intent
                                                  │
                                                  ▼
┌───────────────────────────┐
│ RebalanceDecisionEngine   │ ← Pure Decision Logic (Background Loop!)
│                           │   • NoRebalanceRange check
│ + CacheGeometryPolicy     │   • DesiredCacheRange computation
└───────────┬───────────────┘   • allow/block execution
            │
            │ if validation confirms necessity
            │
            ▼
┌───────────────────────────┐
│ ScheduleRebalance()       │ ← Creates background task (returns synchronously)
└───────────┬───────────────┘
            │
            │ Background scheduling - HERE background starts ⚡→🔄
            │
            ▼

═══════════════════════════════════════════════════════════
Background / ThreadPool (After background scheduling)
═══════════════════════════════════════════════════════════

            ▼
┌───────────────────────────┐
│ Debounce Delay            │ ← Wait before execution
└───────────┬───────────────┘
            │
            ▼
┌───────────────────────────┐
│ RebalanceExecutor         │ ← Mutating Actor (I/O operations)
└───────────┬───────────────┘
            │
            │ atomic mutation
            │
            ▼
┌───────────────────────────┐
│ CacheState                │ ← Consistency (single-writer)
└───────────────────────────┘
```

**Critical:** Everything up to `PublishIntent()` happens **synchronously in the user thread** (atomic intent publish + semaphore signal only). Decision evaluation, scheduling, and all execution happen in background loops.

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

**Lives in: Background Thread (Intent Processing Loop)** (invoked by IntentController.ProcessIntentsAsync)

**Critical:** Decision evaluation happens ASYNCHRONOUSLY in background intent processing loop after PublishIntent() returns to user.

### Visibility

- **Not visible to external users**
- **Owned by IntentController** (composed in constructor)
- Invoked by `IntentController.ProcessIntentsAsync` (background intent processing loop)
- May execute many times; work avoidance allows skipping scheduling entirely

### Ownership

**Owned by:** IntentController  
**Created by:** IntentController constructor  
**Lifecycle:** Same as IntentController (cache lifetime)

### Critical Rule

```
DecisionEngine executes in the background intent processing loop.
DecisionEngine is THE SOLE AUTHORITY for rebalance necessity determination.
Decision happens BEFORE execution is scheduled (prevents work buildup, intent thrashing).
```

### Responsibilities

- **THE sole authority for rebalance necessity determination** (not a helper, but THE decision maker)
- Evaluates whether rebalance is required through multi-stage analytical validation:
  - **Stage 1**: NoRebalanceRange containment check (fast path work avoidance)
  - **Stage 2**: Pending Desired Cache NoRebalanceRange validation (anti-thrashing — fully implemented)
  - **Stage 3**: Compute DesiredCacheRange from RequestedRange + configuration
  - **Stage 4**: DesiredCacheRange vs CurrentCacheRange equality check (no-op prevention)
- Produces analytical decision (execute or skip) that drives system behavior
- Enables smart eventual consistency through work avoidance mechanisms
- Rebalance executes ONLY if ALL validation stages confirm necessity (prevents thrashing, redundant I/O, oscillation)

### Characteristics

- Pure (CPU-only, no I/O)
- Deterministic
- Side-effect free
- Does not mutate cache state
- Authority for necessity determination (not a mere helper)

### Notes

This component should be:

- easily testable
- fully synchronous
- independent of execution context

**Critical Distinction:** While this is an internal tool of IntentManager/Executor pipeline,
it is **THE sole authority** for determining rebalance necessity. All execution decisions
flow from this component's analytical validation.

---

## 4. CacheGeometryPolicy

*(Configuration & Policy Actor)*

### Mapped Actor

**Cache Geometry Policy**

### Implementation

**Implemented as:** Two separate components working together as a unified policy:

1. **ThresholdRebalancePolicy**
   - `internal readonly struct ThresholdRebalancePolicy<TRange, TDomain>`
   - File: `src/SlidingWindowCache/Core/Rebalance/Decision/ThresholdRebalancePolicy.cs`
   - Computes `NoRebalanceRange`
   - Checks if rebalance is needed based on threshold rules

2. **ProportionalRangePlanner**
   - `internal readonly struct ProportionalRangePlanner<TRange, TDomain>`
   - File: `src/SlidingWindowCache/Core/Planning/ProportionalRangePlanner.cs`
   - Computes `DesiredCacheRange`
   - Plans canonical cache geometry based on proportional expansion

**Key Principle:** The logical actor (Cache Geometry Policy) is decomposed into 
two cooperating components for separation of concerns. Each component handles 
one aspect of cache geometry: thresholds (when to rebalance) and planning (what 
shape to target).

**Used by:** RebalanceDecisionEngine composes both components to make rebalance decisions.

### Execution Context

**Background Thread (Intent Processing Loop)** (invoked by RebalanceDecisionEngine during intent processing)

**Characteristics:**
- Pure functions, lightweight structs (value types)
- CPU-only calculations (no I/O)
- Side-effect free
- Inline execution as part of DecisionEngine.Evaluate() call chain

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

**IntentController Actor**

### Implementation

**Implemented as:** Two internal components working together as a unified actor:

1. **IntentController**
   - `internal sealed class IntentController<TRange, TData, TDomain>`
   - File: `src/SlidingWindowCache/Core/Rebalance/Intent/IntentController.cs`
   - **Owns DecisionEngine** (composes in constructor)
   - **Owns IRebalanceExecutionController** (injected)
   - Manages intent lifecycle: `PublishIntent()` (user thread) + `ProcessIntentsAsync()` (background loop)
   - Atomically tracks latest intent via `_pendingIntent` field (`Interlocked.Exchange` — latest-wins)
   - Signals background loop via `SemaphoreSlim`

2. **IRebalanceExecutionController** (Execution Controller)
   - Interface: `IRebalanceExecutionController<TRange, TData, TDomain>`
   - Implementations: `TaskBasedRebalanceExecutionController` (default) and `ChannelBasedRebalanceExecutionController`
   - **Owned by IntentController** (injected in constructor)
   - Handles debounce timing and background execution
   - `PublishExecutionRequest()` is `ValueTask` (enqueues or creates execution)
   - Ensures single-flight execution via cancellation tokens

**Key Principle:** IntentController is the owner/orchestrator. It owns both DecisionEngine and the ExecutionController, invokes DecisionEngine in the background intent processing loop, and delegates background execution to ExecutionController.

### Execution Context

**Mixed:**
- **User Thread**: `PublishIntent()` only (atomic `Interlocked.Exchange` + semaphore signal, fire-and-forget)
- **Background Loop #1**: `ProcessIntentsAsync()` — reads intent, evaluates decision, schedules execution

### Enhanced Role (Decision-Driven Model)

The IntentController actor is responsible for:

- **Receiving intents** (on every user request) [`IntentController.PublishIntent()` - User Thread, atomic only]
- **Owning and invoking DecisionEngine** [`IntentController` owns; invokes in `ProcessIntentsAsync` background loop]
- **Intent lifecycle management** via `_pendingIntent` field (`Interlocked.Exchange` — latest-wins atomics)
- **Cancellation coordination** based on validation results from owned DecisionEngine [`IntentController` - Background Loop]
- **Work avoidance** through background decision evaluation [`IntentController.ProcessIntentsAsync`]
- **Debouncing** [Execution Controller — Background, after execution request enqueued]
- **Single-flight execution** enforcement [Both components via cancellation + execution serialization]
- **Starting background execution** [Execution Controller — `PublishExecutionRequest()`]
- **Orchestrating the validation-driven decision pipeline**: [`IntentController.ProcessIntentsAsync` - Background Loop]
  1. **`IntentController.PublishIntent()`** atomically replaces `_pendingIntent`, signals semaphore (User Thread — returns immediately)
  2. **`IntentController.ProcessIntentsAsync()`** wakes on semaphore; reads latest intent via `Interlocked.Exchange`
  3. **`RebalanceDecisionEngine.Evaluate()`** performs multi-stage validation (Background Loop, CPU-only)
  4. If validation rejects → record diagnostic, decrement activity counter, continue loop (work avoidance)
  5. If validation confirms → cancel prior execution request, call `ExecutionController.PublishExecutionRequest()`
  6. **ExecutionController** performs debounce delay + `RebalanceExecutor.ExecuteAsync()` (Background)

**Key Principle:** `IntentController` is the owner/orchestrator. It **owns DecisionEngine** and invokes it **in the background intent processing loop**, enabling work avoidance and preventing intent thrashing. The **DecisionEngine (owned by IntentController) is THE sole authority** for necessity determination. This separation enables **smart eventual consistency**: the system converges to optimal configuration while avoiding unnecessary operations.

### Component Responsibilities

#### Intent Controller (IntentController)
- Owns `_pendingIntent` field — updated via `Interlocked.Exchange` for atomic latest-wins semantics
- Provides `PublishIntent()` to receive new intents from User Path (user thread — lightweight signal only)
- Runs `ProcessIntentsAsync()` background loop: waits on semaphore, evaluates decision, schedules execution
- Invalidates previous intent atomically when new intent arrives (Interlocked.Exchange replaces and discards prior)
- Does NOT perform scheduling or timing logic (delegates to ExecutionController)
- Does NOT determine rebalance necessity (DecisionEngine's job)
- **Lock-free implementation** using `Interlocked.Exchange` for safe atomic intent replacement
- **Thread-safe without locks** — no race conditions, no blocking
- Validated by `ConcurrencyStabilityTests` under concurrent load

#### Execution Controller (IRebalanceExecutionController)
- Receives execution request from Intent Controller
- Performs debounce delay
- Checks execution request validity/cancellation before execution starts
- Orchestrates `RebalanceExecutor.ExecuteAsync()` based on cancellation token
- Ensures only one execution runs at a time (via cancellation of prior request)
- Does NOT own intent identity or versioning
- Does NOT decide whether rebalance is logically required (delegated to DecisionEngine)

### Key Decision Authority

- **When to wake and process** [Background semaphore signal from `PublishIntent()`]
- **Whether rebalance is necessary** [DecisionEngine validates through multi-stage pipeline]
- **When to skip execution entirely** [DecisionEngine validation result]

### Owns

- Intent versioning [Intent Controller via `_pendingIntent`]
- Cancellation tokens [Execution Controller per execution request]
- Scheduling logic [Execution Controller]
- Pipeline orchestration based on validation results [Both components]

### Pipeline Orchestration (Validation-Driven Model)

```
User Thread
─────────────────────────────────────────────────────
IntentController.PublishIntent()
    ├── Interlocked.Exchange(_pendingIntent, intent)  ← latest-wins
    ├── _activityCounter.IncrementActivity()
    └── _intentSignal.Release()                       ← returns to user

Background Loop #1 (IntentController.ProcessIntentsAsync)
─────────────────────────────────────────────────────
    ├── await _intentSignal.WaitAsync()
    ├── intent = Interlocked.Exchange(_pendingIntent, null)
    └── RebalanceDecisionEngine.Evaluate(intent, lastExecutionRequest, currentRange)
            ├── Stage 1: Current NoRebalanceRange containment → skip if contained
            ├── Stage 2: Pending execution NoRebalanceRange → skip if covered
            ├── Stage 3: Compute DesiredCacheRange
            ├── Stage 4: DesiredCacheRange == CurrentCacheRange → skip if equal
            └── Stage 5: ShouldSchedule = true
            ↓
    ├── if !ShouldSchedule → continue loop (work avoidance)
    └── if ShouldSchedule → ExecutionController.PublishExecutionRequest(...)

Background Execution (ExecutionController + RebalanceExecutor)
─────────────────────────────────────────────────────
    ├── debounce delay
    ├── check cancellation
    └── RebalanceExecutor.ExecuteAsync(...)
            └── atomic cache mutation
```

**Benefits:**
- Clear separation: lifecycle vs. execution vs. decision
- User thread returns immediately (atomic signal only)
- Decision authority clearly assigned to DecisionEngine (background loop)
- Executor mechanically simple (assumes validated necessity)
- Single Responsibility Principle maintained
- Cancellation is coordination (prevents concurrent executions), NOT decision mechanism

### Notes

This is the **temporal authority** of the system, orchestrating validation-driven execution.

The internal decomposition is an implementation detail — from an architectural
perspective, this is a single unified actor that coordinates intent lifecycle,
validation pipeline, and execution timing.

---

## 6. RebalanceExecutor

*(Mutating Actor)*

### Mapped Actor

**Rebalance Executor**

### Responsibilities

- Executes rebalance when authorized by DecisionEngine validation
- Performs I/O with IDataSource
- Computes missing ranges
- Merges / trims / replaces cache data
- Produces normalized cache state
- **Mechanically simple**: No analytical decisions, assumes DecisionEngine already validated necessity

### Characteristics

- Asynchronous
- Cancellable
- Heavyweight (I/O operations)
- **No decision logic**: Does NOT validate rebalance necessity
- **No range checks**: Does NOT check NoRebalanceRange (Stage 1 already passed)
- **No geometry validation**: Does NOT check if Desired == Current (Stage 3 already passed)
- **Assumes validated**: Decision pipeline already confirmed necessity before invocation

### Constraints

- Must be overwrite-safe
- Must respect cancellation
- Must never apply obsolete results
- Must maintain atomic cache updates

### Critical Principle

Executor is intentionally simple and mechanical:
1. Receive validated DesiredCacheRange from DecisionEngine
2. Use delivered data from intent as authoritative base
3. Fetch missing data for DesiredCacheRange
4. Merge delivered + fetched data
5. Trim to DesiredCacheRange
6. Write atomically via Rematerialize()

**NO analytical validation** - all decision logic belongs to DecisionEngine.

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

🔑 **DecisionEngine executes in the background intent processing loop (`IntentController.ProcessIntentsAsync`), enabling work avoidance and preventing intent thrashing. The user thread returns immediately after `PublishIntent()`.**

### Actor Execution Contexts

| Actor                                    | Execution Context                                | Invoked By                                    |
|------------------------------------------|--------------------------------------------------|-----------------------------------------------|
| UserRequestHandler                       | User Thread                                      | User (public API)                             |
| IntentController.PublishIntent           | **User Thread (atomic publish only)**            | UserRequestHandler                            |
| IntentController.ProcessIntentsAsync     | **Background Loop #1 (intent processing)**       | Background task (awaits semaphore)            |
| RebalanceDecisionEngine                  | **Background Loop #1 (intent processing)**       | IntentController.ProcessIntentsAsync          |
| CacheGeometryPolicy                      | **Background Loop #1 (intent processing)**       | RebalanceDecisionEngine                       |
| IRebalanceExecutionController            | **Background Execution (strategy-specific)**     | IntentController.ProcessIntentsAsync          |
| TaskBasedRebalanceExecutionController    | **Background (ThreadPool task chain)**           | Via interface (default strategy)              |
| ChannelBasedRebalanceExecutionController | **Background Loop #2 (channel reader)**          | Via interface (optional strategy)             |
| RebalanceExecutor                        | **Background Execution (both strategies)**       | IRebalanceExecutionController implementations |
| CacheStateManager                        | Both (User: reads, Background execution: writes) | Both paths (single-writer)                    |

**Critical:** User thread ends at `PublishIntent()` return (after atomic operations). Decision evaluation runs in background intent processing loop. Cache mutations run in separate background execution loop.

### Responsibilities Refixed

#### UserRequestHandler (Updated Role)

- ✅ Serves user requests
- ✅ **Always publishes rebalance intent**
- ❌ **Never** checks NoRebalanceRange
- ❌ **Never** computes DesiredCacheRange
- ❌ **Never** decides "to rebalance or not"

**Contract:** *Every user access produces a rebalance intent.*

#### RebalanceIntentManager (Enhanced Role)

The IntentController ACTOR (implemented via `IntentController` + `IRebalanceExecutionController`) is the **orchestrator** responsible for:

- ✅ Receiving intent on **every user request** [`IntentController.PublishIntent()`]
- ✅ Deduplication and debouncing [`IRebalanceExecutionController`]
- ✅ Cancelling obsolete intents [`IntentController` via `Interlocked.Exchange` latest-wins]
- ✅ Single-flight enforcement [Both components via cancellation]
- ✅ **Launching background execution** [`IRebalanceExecutionController.PublishExecutionRequest()`]
- ✅ **Deciding when to start decision logic** [`IntentController.ProcessIntentsAsync` background loop]
- ✅ **Deciding when to skip execution** [DecisionEngine via `IntentController.ProcessIntentsAsync`]
- ⚠️ **Intent does not guarantee execution** — execution is opportunistic

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