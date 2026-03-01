# Components: Overview

## Overview

This folder documents the internal component set of SlidingWindowCache. It is intentionally split by responsibility and execution context to avoid a single mega-document.

## Motivation

The system is easier to reason about when components are grouped by:

- execution context (User Path, Decision Path, Execution Path)
- ownership boundaries (who creates/owns what)
- mutation authority (single-writer)

## Design

### Top-Level Component Roles

- Public facade: `WindowCache<TRange, TData, TDomain>`
- Public extensions: `WindowCacheExtensions` — opt-in strong consistency mode (`GetDataAndWaitForIdleAsync`)
- Multi-layer support: `WindowCacheDataSourceAdapter`, `LayeredWindowCacheBuilder`, `LayeredWindowCache`
- User Path: assembles requested data and publishes intent
- Intent loop: observes latest intent and runs analytical validation
- Execution: performs debounced, cancellable rebalance work and mutates cache state

### Component Index

- `docs/components/public-api.md`
- `docs/components/user-path.md`
- `docs/components/intent-management.md`
- `docs/components/decision.md`
- `docs/components/execution.md`
- `docs/components/state-and-storage.md`
- `docs/components/infrastructure.md`
### Ownership (Conceptual)

`WindowCache` is the composition root. Internals are constructed once and live for the cache lifetime. Disposal cascades through owned components.

## Component Hierarchy

```
🟦 WindowCache<TRange, TData, TDomain>                    [Public Facade]
│
├── owns → 🟦 UserRequestHandler<TRange, TData, TDomain>
│
└── composes (at construction):
    ├── 🟦 CacheState<TRange, TData, TDomain>              ⚠️ Shared Mutable
    ├── 🟦 IntentController<TRange, TData, TDomain>
    │   └── uses → 🟧 IRebalanceExecutionController<TRange, TData, TDomain>
    │       ├── implements → 🟦 TaskBasedRebalanceExecutionController (default)
    │       └── implements → 🟦 ChannelBasedRebalanceExecutionController (optional)
    ├── 🟦 RebalanceDecisionEngine<TRange, TDomain>
    │   ├── owns → 🟩 NoRebalanceSatisfactionPolicy<TRange>
    │   └── owns → 🟩 ProportionalRangePlanner<TRange, TDomain>
    ├── 🟦 RebalanceExecutor<TRange, TData, TDomain>
    └── 🟦 CacheDataExtensionService<TRange, TData, TDomain>
        └── uses → 🟧 IDataSource<TRange, TData> (user-provided)

──────────────────────────── Multi-Layer Support ────────────────────────────

🟦 LayeredWindowCacheBuilder<TRange, TData, TDomain>       [Fluent Builder]
│  Static Create(dataSource, domain) → builder
│  AddLayer(options, diagnostics?) → builder (fluent chain)
│  Build() → LayeredWindowCache
│
│  internally wires:
│    IDataSource  →  WindowCache  →  WindowCacheDataSourceAdapter
│                                          │
│                                          ▼
│                                    WindowCache  →  WindowCacheDataSourceAdapter  → ...
│                                          │
│                                          ▼  (outermost)
└─────────────────────────────────►  WindowCache
                                         (user-facing layer, index = LayerCount-1)

🟦 LayeredWindowCache<TRange, TData, TDomain>              [IWindowCache wrapper]
│  LayerCount: int
│  GetDataAsync()      → delegates to outermost WindowCache
│  WaitForIdleAsync()  → awaits all layers sequentially, outermost to innermost
│  DisposeAsync()      → disposes all layers outermost-first

🟦 WindowCacheDataSourceAdapter<TRange, TData, TDomain>    [IDataSource adapter]
│  Wraps IWindowCache as IDataSource
│  FetchAsync() → calls inner cache's GetDataAsync()
│                 wraps ReadOnlyMemory<TData> in ReadOnlyMemoryEnumerable<TData> for RangeChunk (avoids temp TData[] alloc)
```

**Component Type Legend:**
- 🟦 CLASS = Reference type (heap-allocated)
- 🟩 STRUCT = Value type (stack-allocated or inline)
- 🟧 INTERFACE = Contract definition
- 🟪 ENUM = Value type enumeration
- 🟨 RECORD = Reference type with value semantics

## Ownership & Data Flow Diagram

```
┌────────────────────────────────────────────────────────────────────────────┐
│                               USER (Consumer)                              │
└────────────────────────────────────────────────────────────────────────────┘
                                      │
                                      │ GetDataAsync(range, ct)
                                      ▼
┌────────────────────────────────────────────────────────────────────────────┐
│ WindowCache<TRange, TData, TDomain>  [Public Facade]                       │
│ sealed, public                                                             │
│                                                                            │
│ Constructor wires:                                                         │
│  • CacheState (shared mutable)                                             │
│  • UserRequestHandler                                                      │
│  • CacheDataExtensionService                                               │
│  • IntentController                                                        │
│      └─ IRebalanceExecutionController                                      │
│  • RebalanceDecisionEngine                                                 │
│      ├─ NoRebalanceSatisfactionPolicy                                      │
│      └─ ProportionalRangePlanner                                           │
│  • RebalanceExecutor                                                       │
│                                                                            │
│ GetDataAsync() → delegates to UserRequestHandler                           │
└────────────────────────────────────────────────────────────────────────────┘


════════════════════════════════ USER THREAD ════════════════════════════════


                                      │
                                      ▼
┌────────────────────────────────────────────────────────────────────────────┐
│ UserRequestHandler  [FAST PATH — READ ONLY]                                │
│                                                                            │
│ HandleRequestAsync(range, ct):                                             │
│  1. Check cold start / cache coverage                                      │
│  2. Fetch missing via CacheDataExtensionService                            │
│  3. Publish intent with assembled data                                     │
│  4. Return ReadOnlyMemory<TData>                                           │
│                                                                            │
│  ✖ NEVER writes to CacheState                                              │
│  ✖ NEVER calls Rematerialize()                                             │
│  ✖ NEVER modifies IsInitialized / NoRebalanceRange                         │
└────────────────────────────────────────────────────────────────────────────┘
                                      │
                                      │ PublishIntent()
                                      ▼


════════════════════════════ BACKGROUND / THREADPOOL ════════════════════════


┌────────────────────────────────────────────────────────────────────────────┐
│ IntentController  [Lifecycle + Background Loop]                            │
│                                                                            │
│ PublishIntent()  (User Thread)                                             │
│  1. Interlocked.Exchange(_pendingIntent)                                   │
│  2. Increment activity counter                                             │
│  3. Signal background loop                                                 │
│                                                                            │
│ ProcessIntentsAsync()  (Background Loop)                                   │
│  1. Wait for signal                                                        │
│  2. Drain pending intent                                                   │
│  3. decision = RebalanceDecisionEngine.Evaluate(...)                       │
│  4. If !ShouldSchedule → skip (work avoidance)                             │
│  5. Cancel previous request                                                │
│  6. Publish execution request                                              │
└────────────────────────────────────────────────────────────────────────────┘
                                      │
                                      ▼
┌────────────────────────────────────────────────────────────────────────────┐
│ RebalanceDecisionEngine  [PURE DECISION LOGIC]                             │
│                                                                            │
│ Evaluate(requested, cacheState, lastRequest):                              │
│  Stage 1: Policy.ShouldRebalance(noRebalanceRange) → maybe skip            │
│  Stage 2: Policy.ShouldRebalance(pendingNRR) → maybe skip                  │
│  Stage 3: desiredRange = Planner.Plan(requested)                           │
│  Stage 4: If desiredRange == currentRange → skip                           │
│  Stage 5: Return Schedule(desiredRange, desiredNRR)                        │
└────────────────────────────────────────────────────────────────────────────┘
                                      │
                                      ▼
┌────────────────────────────────────────────────────────────────────────────┐
│ IRebalanceExecutionController  [EXECUTION SERIALIZATION]                   │
│                                                                            │
│ Strategies:                                                                │
│  • Task chaining (lock-free)                                               │
│  • Channel<ExecutionRequest> (bounded)                                     │
│                                                                            │
│ Execution flow:                                                            │
│  1. Debounce delay (cancellable)                                           │
│  2. Call RebalanceExecutor.ExecuteAsync(...)                               │
└────────────────────────────────────────────────────────────────────────────┘
                                      │
                                      ▼
┌────────────────────────────────────────────────────────────────────────────┐
│ RebalanceExecutor  [MUTATING ACTOR — SOLE WRITER]                          │
│                                                                            │
│ ExecuteAsync(intent, desiredRange, desiredNRR, ct):                        │
│  1. Validate cancellation                                                  │
│  2. Extend cache via CacheDataExtensionService                             │
│  3. Trim to desiredRange                                                   │
│  4. Update NoRebalanceRange                                                │
│  5. Set IsInitialized = true                                               │
│  6. Storage.Rematerialize(normalizedData)                                  │
│                                                                            │
│  ✔ ONLY component allowed to mutate CacheState                             │
└────────────────────────────────────────────────────────────────────────────┘
                                      │
                                      ▼
┌────────────────────────────────────────────────────────────────────────────┐
│ CacheState  [SHARED MUTABLE STATE]                                         │
│                                                                            │
│ Written by:  RebalanceExecutor (sole writer)                               │
│ Read by:     UserRequestHandler, DecisionEngine, IntentController          │
│                                                                            │
│ ICacheStorage implementations:                                             │
│  • SnapshotReadStorage   (array — zero-alloc reads)                        │
│  • CopyOnReadStorage     (List — cheap writes)                             │
└────────────────────────────────────────────────────────────────────────────┘
```

## Invariant Implementation Mapping

This section bridges architectural invariants (in `docs/invariants.md`) to their concrete implementations. Each invariant is enforced through specific component interactions, code patterns, or architectural constraints.

### Single-Writer Architecture
**Invariants**: A.-1, A.7, A.8, A.9, F.36

Only `RebalanceExecutor` has write access to `CacheState` internal setters. User Path components have read-only references. Internal visibility modifiers prevent external mutations.

- `src/SlidingWindowCache/Core/State/CacheState.cs` — internal setters restrict write access
- `src/SlidingWindowCache/Core/Rebalance/Execution/RebalanceExecutor.cs` — exclusive mutation authority
- `src/SlidingWindowCache/Core/UserPath/UserRequestHandler.cs` — read-only access pattern

### Priority and Cancellation
**Invariants**: A.0, A.0a, C.19, F.35a

`CancellationTokenSource` coordination between intent publishing and execution. `RebalanceDecisionEngine` validates necessity before triggering cancellation. Multiple checkpoints in execution pipeline check for cancellation.

- `src/SlidingWindowCache/Core/Rebalance/Intent/IntentController.cs` — cancellation token lifecycle
- `src/SlidingWindowCache/Core/Rebalance/Decision/RebalanceDecisionEngine.cs` — validation gates cancellation
- `src/SlidingWindowCache/Core/Rebalance/Execution/RebalanceExecutor.cs` — `ThrowIfCancellationRequested` checkpoints

### Intent Management and Cancellation
**Invariants**: A.0a, C.17, C.20, C.21

`Interlocked.Exchange` replaces previous intent atomically (latest-wins). Single-writer architecture for intent state. Cancellation checked after debounce delay before execution starts.

- `src/SlidingWindowCache/Core/Rebalance/Intent/IntentController.cs` — atomic intent replacement

### UserRequestHandler Responsibilities
**Invariants**: A.3, A.5

Only `UserRequestHandler` has access to `IntentController.PublishIntent`. Its scope is limited to data assembly; no normalization logic.

- `src/SlidingWindowCache/Core/UserPath/UserRequestHandler.cs` — exclusive intent publisher
- `src/SlidingWindowCache/Core/Rebalance/Intent/IntentController.cs` — internal visibility on publication interface

### Async Execution Model
**Invariants**: A.4, G.44

`UserRequestHandler` publishes intent and returns immediately (fire-and-forget). `IRebalanceExecutionController` schedules execution via `Task.Run` or channels. User thread and ThreadPool thread contexts are separated.

- `src/SlidingWindowCache/Core/Rebalance/Intent/IntentController.cs` — `ProcessIntentsAsync` runs on background thread
- `src/SlidingWindowCache/Infrastructure/Execution/TaskBasedRebalanceExecutionController.cs` — `Task.Run` scheduling
- `src/SlidingWindowCache/Infrastructure/Execution/ChannelBasedRebalanceExecutionController.cs` — channel-based background execution

### Atomic Cache Updates
**Invariants**: B.12, B.13

Storage strategies build new state before atomic swap. `Volatile.Write` atomically publishes new cache state reference (Snapshot). `CopyOnReadStorage` uses a lock-protected buffer swap instead. `Rematerialize` succeeds completely or not at all.

- `src/SlidingWindowCache/Infrastructure/Storage/SnapshotReadStorage.cs` — `Array.Copy` + `Volatile.Write`
- `src/SlidingWindowCache/Infrastructure/Storage/CopyOnReadStorage.cs` — lock-protected dual-buffer swap (`_lock`)
- `src/SlidingWindowCache/Core/State/CacheState.cs` — `Rematerialize` ensures atomicity

### Consistency Under Cancellation
**Invariants**: B.13, B.15, F.35b

Final cancellation check before applying cache updates. Results applied atomically or discarded entirely. `try-finally` blocks ensure cleanup on cancellation.

- `src/SlidingWindowCache/Core/Rebalance/Execution/RebalanceExecutor.cs` — `ThrowIfCancellationRequested` before `Rematerialize`

### Obsolete Result Prevention
**Invariants**: B.16, C.20

Each intent has a unique `CancellationToken`. Execution checks if cancellation is requested before applying results. Only results from the latest non-cancelled intent are applied.

- `src/SlidingWindowCache/Core/Rebalance/Execution/RebalanceExecutor.cs` — cancellation validation before mutation
- `src/SlidingWindowCache/Core/Rebalance/Intent/IntentController.cs` — token lifecycle management

### Intent Singularity
**Invariant**: C.17

`Interlocked.Exchange` ensures exactly one active intent. New intent atomically replaces previous one. At most one pending intent at any time (no queue buildup).

- `src/SlidingWindowCache/Core/Rebalance/Intent/IntentController.cs` — `Interlocked.Exchange` for atomic intent replacement

### Cancellation Protocol
**Invariant**: C.19

`CancellationToken` passed through the entire pipeline. Multiple checkpoints: before I/O, after I/O, before mutations. Results from cancelled operations are never applied.

- `src/SlidingWindowCache/Core/Rebalance/Execution/RebalanceExecutor.cs` — multiple `ThrowIfCancellationRequested` calls
- `src/SlidingWindowCache/Infrastructure/Services/CacheDataExtensionService.cs` — cancellation token propagated to `IDataSource`

### Early Exit Validation
**Invariants**: C.20, D.29

Post-debounce cancellation check before execution. Each validation stage can exit early. All stages must pass for execution to proceed.

- `src/SlidingWindowCache/Core/Rebalance/Intent/IntentController.cs` — cancellation check after debounce
- `src/SlidingWindowCache/Core/Rebalance/Decision/RebalanceDecisionEngine.cs` — multi-stage early exit

### Serial Execution Guarantee
**Invariant**: C.21

Previous execution cancelled before starting new one. Single `IRebalanceExecutionController` instance per cache. Intent processing loop ensures serial execution.

- `src/SlidingWindowCache/Core/Rebalance/Intent/IntentController.cs` — sequential intent loop + cancellation of prior execution

### Intent Data Contract
**Invariant**: C.24e

`PublishIntent` signature requires `deliveredData` parameter. `UserRequestHandler` materializes data once, passes it to both user and intent. Compiler enforces data presence.

- `src/SlidingWindowCache/Core/Rebalance/Intent/IntentController.cs` — `PublishIntent(requestedRange, deliveredData)` signature
- `src/SlidingWindowCache/Core/UserPath/UserRequestHandler.cs` — single data materialization shared between paths

### Pure Decision Logic
**Invariants**: D.25, D.26

`RebalanceDecisionEngine` has no mutable fields. Decision policies are structs with no side effects. No I/O in decision path. Pure function: `(state, intent, config) → decision`.

- `src/SlidingWindowCache/Core/Rebalance/Decision/RebalanceDecisionEngine.cs` — pure evaluation logic
- `src/SlidingWindowCache/Core/Planning/NoRebalanceSatisfactionPolicy.cs` — stateless struct
- `src/SlidingWindowCache/Core/Planning/ProportionalRangePlanner.cs` — stateless struct

### Decision-Execution Separation
**Invariant**: D.26

Decision components have no references to mutable state setters. Decision Engine reads `CacheState` but cannot modify it. Decision and Execution interfaces are distinct.

- `src/SlidingWindowCache/Core/Rebalance/Decision/RebalanceDecisionEngine.cs` — read-only state access
- `src/SlidingWindowCache/Core/Rebalance/Execution/RebalanceExecutor.cs` — exclusive write access

### Multi-Stage Decision Pipeline
**Invariant**: D.29

Five-stage pipeline with early exits. Stage 1: current `NoRebalanceRange` containment (fast path). Stage 2: pending `NoRebalanceRange` validation (thrashing prevention). Stage 3: `DesiredCacheRange` computation. Stage 4: equality check (`DesiredCacheRange == CurrentCacheRange`). Stage 5: execution scheduling (only if all stages pass).

- `src/SlidingWindowCache/Core/Rebalance/Decision/RebalanceDecisionEngine.cs` — complete pipeline implementation

### Desired Range Computation
**Invariants**: E.30, E.31

`ProportionalRangePlanner.Plan(requestedRange, config)` is a pure function — same inputs always produce same output. Never reads `CurrentCacheRange`.

- `src/SlidingWindowCache/Core/Planning/ProportionalRangePlanner.cs` — pure range calculation

### NoRebalanceRange Computation
**Invariants**: E.34, E.35

`NoRebalanceRangePlanner.Plan(currentCacheRange)` — pure function of current range + config. Applies threshold percentages as negative expansion. Returns `null` when individual thresholds ≥ 1.0 (no stability zone possible). `WindowCacheOptions` constructor ensures threshold sum ≤ 1.0 at construction time.

- `src/SlidingWindowCache/Core/Planning/NoRebalanceRangePlanner.cs` — NoRebalanceRange computation
- `src/SlidingWindowCache/Public/Configuration/WindowCacheOptions.cs` — threshold sum validation

### Cancellation Checkpoints
**Invariants**: F.35, F.35a

Three checkpoints: before `IDataSource.FetchAsync`, after data fetching, before `Rematerialize`. `OperationCanceledException` propagates to cleanup handlers.

- `src/SlidingWindowCache/Core/Rebalance/Execution/RebalanceExecutor.cs` — multiple checkpoint locations

### Cache Normalization Operations
**Invariant**: F.37

`CacheState.Rematerialize` accepts arbitrary range and data (full replacement). `ICacheStorage` abstraction enables different normalization strategies.

- `src/SlidingWindowCache/Core/State/CacheState.cs` — `Rematerialize` method
- `src/SlidingWindowCache/Infrastructure/Storage/` — storage strategy implementations

### Incremental Data Fetching
**Invariant**: F.38

`CacheDataExtensionService.ExtendCacheDataAsync` computes missing ranges via range subtraction (`DesiredRange \ CachedRange`). Fetches only missing subranges via `IDataSource`.

- `src/SlidingWindowCache/Infrastructure/Services/CacheDataExtensionService.cs` — range gap logic in `ExtendCacheDataAsync`

### Data Preservation During Expansion
**Invariant**: F.39

New data merged with existing via range union. Existing data enumerated and preserved during rematerialization. New data only fills gaps; does not replace existing.

- `src/SlidingWindowCache/Infrastructure/Services/CacheDataExtensionService.cs` — union logic in `ExtendCacheDataAsync`

### I/O Isolation
**Invariant**: G.45

`UserRequestHandler` completes before any `IDataSource.FetchAsync` calls in rebalance path. All `IDataSource` interactions happen in `RebalanceExecutor` on a background thread.

- `src/SlidingWindowCache/Core/UserPath/UserRequestHandler.cs` — no rebalance-path `IDataSource` calls
- `src/SlidingWindowCache/Core/Rebalance/Execution/RebalanceExecutor.cs` — `IDataSource` calls only in background execution

### Activity Counter Ordering
**Invariant**: H.47

Activity counter incremented **before** semaphore signal, channel write, or volatile write (strict ordering discipline at all publication sites).

- `src/SlidingWindowCache/Core/Rebalance/Intent/IntentController.cs` — increment before `semaphore.Release`
- `src/SlidingWindowCache/Infrastructure/Execution/` — increment before channel write or `Task.Run`

### Activity Counter Cleanup
**Invariant**: H.48

Decrement in `finally` blocks — unconditional execution regardless of success, failure, or cancellation.

- `src/SlidingWindowCache/Core/Rebalance/Intent/IntentController.cs` — `finally` block in `ProcessIntentsAsync`
- `src/SlidingWindowCache/Infrastructure/Execution/` — `finally` blocks in execution controllers

---

## Architectural Patterns Used

### 1. Facade Pattern
`WindowCache` acts as a facade that hides internal complexity and provides a simple public API. Contains no business logic; all behavioral logic is delegated to internal actors.

### 2. Composition Root
`WindowCache` constructor wires all components together in one place.

### 3. Actor Model (Conceptual)
Components follow actor-like patterns with clear responsibilities and message passing (method calls). Each actor has a defined execution context and responsibility boundary.

### 4. Intent Controller Pattern
`IntentController` manages versioned, cancellable operations through `CancellationTokenSource` identity and `Interlocked.Exchange` latest-wins semantics.

### 5. Strategy Pattern
`ICacheStorage` with two implementations (`SnapshotReadStorage`, `CopyOnReadStorage`) allows runtime selection of storage strategy based on read/write trade-offs.

### 6. Value Object Pattern
`NoRebalanceSatisfactionPolicy`, `ProportionalRangePlanner`, and `RebalanceDecision` are immutable value types with pure behavior (no side effects, deterministic).

### 7. Shared Mutable State (Controlled)
`CacheState` is intentionally shared mutable state, coordinated via single-writer architecture (not locks). The single writer (`RebalanceExecutor`) is the sole authority for mutations.

### 8. Single Consumer Model
The entire architecture assumes one logical consumer, avoiding traditional synchronization primitives in favor of architectural constraints (single-writer, read-only User Path).

---

## Invariants

Canonical invariants live in `docs/invariants.md`. Component-level details in this folder focus on "what exists" and "who does what"; they link back to the formal rules.

## Usage

Contributors should read in this order:

1. `docs/components/public-api.md`
2. `docs/components/user-path.md`
3. `docs/components/intent-management.md`
4. `docs/components/decision.md`
5. `docs/components/execution.md`
6. `docs/components/state-and-storage.md`
7. `docs/components/infrastructure.md`

## See Also

- `docs/scenarios.md` — step-by-step temporal walkthroughs
- `docs/actors.md` — actor responsibilities and invariant ownership
- `docs/architecture.md` — threading model and concurrency details
- `docs/invariants.md` — formal invariant specifications

## Edge Cases

- "Latest intent wins" means intermediate intents can be skipped; this is by design.

## Limitations

- Component docs are descriptive; algorithm-level detail is in source XML docs.
