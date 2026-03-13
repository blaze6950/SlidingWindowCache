# Components: Overview — Sliding Window Cache

## Overview

This folder documents the internal component set of the Sliding Window Cache. It is intentionally split by responsibility and execution context to avoid a single mega-document.

The library is organized across two packages:
- **`Intervals.NET.Caching.SlidingWindow`** — sliding-window cache implementation (`SlidingWindowCache`, `ISlidingWindowCache`, builders, `GetDataAndWaitOnMissAsync`)
- **`Intervals.NET.Caching.VisitedPlaces`** — visited places cache implementation (`VisitedPlacesCache`, `IVisitedPlacesCache`, builders, eviction policies and selectors, TTL)
- **`Intervals.NET.Caching`** (not a package) — shared contracts and infrastructure (`IRangeCache`, `IDataSource`, `LayeredRangeCache`, `RangeCacheDataSourceAdapter`, `LayeredRangeCacheBuilder`, `GetDataAndWaitForIdleAsync`, `AsyncActivityCounter`, `WorkSchedulerBase`)

## Motivation

The system is easier to reason about when components are grouped by:

- execution context (User Path, Decision Path, Execution Path)
- ownership boundaries (who creates/owns what)
- mutation authority (single-writer)

## Design

### Top-Level Component Roles

- Public facade: `SlidingWindowCache<TRange, TData, TDomain>` (in `Intervals.NET.Caching.SlidingWindow`)
- Public interface: `ISlidingWindowCache<TRange, TData, TDomain>` — extends `IRangeCache` with `UpdateRuntimeOptions` + `CurrentRuntimeOptions`
- Shared interface: `IRangeCache<TRange, TData, TDomain>` (in `Intervals.NET.Caching`) — `GetDataAsync` + `WaitForIdleAsync` + `IAsyncDisposable`
- Hybrid consistency extension: `SlidingWindowCacheConsistencyExtensions.GetDataAndWaitOnMissAsync` — on `ISlidingWindowCache` (in `Intervals.NET.Caching.SlidingWindow`)
- Strong consistency extension: `RangeCacheConsistencyExtensions.GetDataAndWaitForIdleAsync` — on `IRangeCache` (in `Intervals.NET.Caching`)
- Runtime configuration: `RuntimeOptionsUpdateBuilder` — fluent builder for `UpdateRuntimeOptions`; only fields explicitly set are changed
- Runtime options snapshot: `RuntimeOptionsSnapshot` — public read-only DTO returned by `ISlidingWindowCache.CurrentRuntimeOptions`
- Shared validation: `RuntimeOptionsValidator` — internal static helper; centralizes cache-size and threshold validation for both `SlidingWindowCacheOptions` and `RuntimeCacheOptions`
- Multi-layer support: `RangeCacheDataSourceAdapter`, `LayeredRangeCacheBuilder`, `LayeredRangeCache` (in `Intervals.NET.Caching`)
- User Path: assembles requested data and publishes intent
- Intent loop: observes latest intent and runs analytical validation
- Execution: performs debounced, cancellable rebalance work and mutates cache state
- Work scheduler (shared): `WorkSchedulerBase<TWorkItem>` — cache-agnostic abstract base; holds shared execution pipeline (debounce → cancellation → executor delegate → diagnostics → cleanup); for SlidingWindowCache the concrete subclasses are `UnboundedSupersessionWorkScheduler<TWorkItem>` (default, latest-wins task-chaining) and `BoundedSupersessionWorkScheduler<TWorkItem>` (bounded channel with latest-wins supersession); `UnboundedSerialWorkScheduler<TWorkItem>` and `BoundedSerialWorkScheduler<TWorkItem>` are also available and used by VisitedPlacesCache

### Component Index

- `docs/sliding-window/components/public-api.md`
- `docs/sliding-window/components/user-path.md`
- `docs/sliding-window/components/intent-management.md`
- `docs/sliding-window/components/decision.md`
- `docs/sliding-window/components/execution.md`
- `docs/sliding-window/components/state-and-storage.md`
- `docs/sliding-window/components/infrastructure.md`

### Ownership (Conceptual)

`SlidingWindowCache` is the composition root. Internals are constructed once and live for the cache lifetime. Disposal cascades through owned components.

## Component Hierarchy

```
🟦 SlidingWindowCache<TRange, TData, TDomain>              [Public Facade]
│  implements ISlidingWindowCache (extends IRangeCache)
│
├── owns → 🟦 UserRequestHandler<TRange, TData, TDomain>
│
└── composes (at construction):
    ├── 🟦 CacheState<TRange, TData, TDomain>              ⚠️ Shared Mutable
    ├── 🟦 IntentController<TRange, TData, TDomain>
    │   └── uses → 🟧 IWorkScheduler<ExecutionRequest<TRange, TData, TDomain>>
    │       ├── implements → 🟦 UnboundedSupersessionWorkScheduler<TWorkItem> (default, latest-wins task-chaining)
    │       └── implements → 🟦 BoundedSupersessionWorkScheduler<TWorkItem> (optional, bounded channel with supersession)
    ├── 🟦 RebalanceDecisionEngine<TRange, TDomain>
    │   ├── owns → 🟦 NoRebalanceSatisfactionPolicy<TRange>
    │   └── owns → 🟦 ProportionalRangePlanner<TRange, TDomain>
    ├── 🟦 RebalanceExecutor<TRange, TData, TDomain>
    └── 🟦 CacheDataExtensionService<TRange, TData, TDomain>
        └── uses → 🟧 IDataSource<TRange, TData> (user-provided)

──────────────────────────── Work Schedulers (Intervals.NET.Caching) ───────────────────────────

🟦 WorkSchedulerBase<TWorkItem>   [Abstract base — cache-agnostic]
│  where TWorkItem : class, ISchedulableWorkItem
│  Injects: executor delegate, debounce provider delegate, IWorkSchedulerDiagnostics, AsyncActivityCounter
│  Implements: ExecuteWorkItemCoreAsync() (shared debounce + execute pipeline)
│              DisposeAsync() (idempotent guard + cancel + DisposeAsyncCore)
│  Abstract: PublishWorkItemAsync(...), DisposeAsyncCore()
│
├── implements → 🟦 SupersessionWorkSchedulerBase<TWorkItem>   [Abstract — latest-wins]
│   │  Adds: LastWorkItem, StoreLastWorkItem() (supersession / latest-wins tracking)
│   │
│   ├── implements → 🟦 UnboundedSupersessionWorkScheduler<TWorkItem> (default for SlidingWindowCache)
│   │                  Adds: lock-free task chain (_currentExecutionTask)
│   │                  Overrides: PublishWorkItemAsync → stores latest + chains new task
│   │                             DisposeAsyncCore → awaits task chain
│   │
│   └── implements → 🟦 BoundedSupersessionWorkScheduler<TWorkItem> (optional for SlidingWindowCache)
│                      Adds: BoundedChannel<TWorkItem>, background loop task
│                      Overrides: PublishWorkItemAsync → stores latest + writes to channel
│                                 DisposeAsyncCore → completes channel + awaits loop
│
├── implements → 🟦 UnboundedSerialWorkScheduler<TWorkItem> (used by VisitedPlacesCache)
│                  Adds: lock-free task chain (_currentExecutionTask)
│                  Overrides: PublishWorkItemAsync → chains new task
│                             DisposeAsyncCore → awaits task chain
│
└── implements → 🟦 BoundedSerialWorkScheduler<TWorkItem> (optional for VisitedPlacesCache)
                   Adds: BoundedChannel<TWorkItem>, background loop task
                   Overrides: PublishWorkItemAsync → writes to channel
                              DisposeAsyncCore → completes channel + awaits loop

──────────────────────── Multi-Layer Support (Intervals.NET.Caching) ─────────────────────

🟦 LayeredRangeCacheBuilder<TRange, TData, TDomain>        [Fluent Builder]
│  (in Intervals.NET.Caching)
│  Obtained via SlidingWindowCacheBuilder.Layered(dataSource, domain)
│  AddSlidingWindowLayer(options, diagnostics?) → builder (fluent chain)
│  AddLayer(Func<IDataSource, IRangeCache>) → builder (generic)
│  Build() → IRangeCache (concrete: LayeredRangeCache)
│
│  internally wires:
│    IDataSource  →  SlidingWindowCache  →  RangeCacheDataSourceAdapter
│                                                  │
│                                                  ▼
│                               SlidingWindowCache  →  RangeCacheDataSourceAdapter  → ...
│                                                  │
│                                                  ▼  (outermost)
└─────────────────────────────────►  SlidingWindowCache
                                         (user-facing layer, index = LayerCount-1)

🟦 LayeredRangeCache<TRange, TData, TDomain>               [IRangeCache wrapper]
│  (in Intervals.NET.Caching)
│  implements IRangeCache only (NOT ISlidingWindowCache)
│  LayerCount: int
│  Layers: IReadOnlyList<IRangeCache<TRange, TData, TDomain>>
│  GetDataAsync()              → delegates to outermost layer
│  WaitForIdleAsync()          → awaits all layers sequentially, outermost to innermost
│  DisposeAsync()              → disposes all layers outermost-first

🟦 RangeCacheDataSourceAdapter<TRange, TData, TDomain>     [IDataSource adapter]
│  (in Intervals.NET.Caching)
│  Wraps IRangeCache as IDataSource
│  FetchAsync() → calls inner cache's GetDataAsync()
│                 wraps ReadOnlyMemory<TData> in ReadOnlyMemoryEnumerable<TData> for RangeChunk (avoids temp TData[] alloc)
```

**Component Type Legend:**
- 🟦 CLASS = Reference type (heap-allocated)
- 🟩 STRUCT = Value type (stack-allocated or inline)
- 🟧 INTERFACE = Contract definition
- 🟪 ENUM = Value type enumeration

> **Note:** `ProportionalRangePlanner` and `NoRebalanceRangePlanner` are `internal sealed class` types so they can hold a reference to the shared `RuntimeCacheOptionsHolder` and read configuration at invocation time.

## Ownership & Data Flow Diagram

```
┌────────────────────────────────────────────────────────────────────────────┐
│                               USER (Consumer)                              │
└────────────────────────────────────────────────────────────────────────────┘
                                      │
                                      │ GetDataAsync(range, ct)
                                      ▼
┌────────────────────────────────────────────────────────────────────────────┐
│ SlidingWindowCache<TRange, TData, TDomain>  [Public Facade]                │
│ sealed, public                                                             │
│                                                                            │
│ Constructor wires:                                                         │
│  • CacheState (shared mutable)                                             │
│  • RuntimeCacheOptionsHolder (shared, volatile — runtime option updates)   │
│  • UserRequestHandler                                                      │
│  • CacheDataExtensionService                                               │
│  • IntentController                                                        │
│      └─ IWorkScheduler<ExecutionRequest<...>>                              │
│  • RebalanceDecisionEngine                                                 │
│      ├─ NoRebalanceSatisfactionPolicy                                      │
│      └─ ProportionalRangePlanner                                           │
│  • RebalanceExecutor                                                       │
│                                                                            │
│ GetDataAsync()           → delegates to UserRequestHandler                 │
│ UpdateRuntimeOptions()   → updates OptionsHolder atomically                │
│ CurrentRuntimeOptions    → returns OptionsHolder.Current.ToSnapshot()      │
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
┌─────────────────────────────────────────────────────────────────────────────────────────┐
│ IWorkScheduler<ExecutionRequest<...>>  [EXECUTION SERIALIZATION]                        │
│                                                                                         │
│ Strategies:                                                                             │
│  • Task chaining (lock-free, latest-wins) — UnboundedSupersessionWorkScheduler          │
│  • Channel<ExecutionRequest> (bounded, latest-wins) — BoundedSupersessionWorkScheduler  │
│                                                                                         │
│ Execution flow:                                                                         │
│  1. Debounce delay (cancellable)                                                        │
│  2. Call RebalanceExecutor.ExecuteAsync(...)                                            │
└─────────────────────────────────────────────────────────────────────────────────────────┘
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
┌───────────────────────────────────────────────────────────────────────────────────┐
│ CacheState  [SHARED MUTABLE STATE]                                                │
│                                                                                   │
│ Written by:  RebalanceExecutor (sole writer)                                      │
│ Read by:     UserRequestHandler, DecisionEngine, IntentController                 │
│                                                                                   │
│ ICacheStorage implementations:                                                    │
│  • SnapshotReadStorage   (array — zero-alloc reads)                               │
│  • CopyOnReadStorage     (List — cheap writes)                                    │
│                                                                                   │
│ RuntimeCacheOptionsHolder  [SHARED RUNTIME CONFIGURATION]                         │
│                                                                                   │
│ Written by:  SlidingWindowCache.UpdateRuntimeOptions (Volatile.Write)             │
│ Read by:     ProportionalRangePlanner, NoRebalanceRangePlanner,                   │
│              UnboundedSupersessionWorkScheduler (via debounce provider delegate), │
│              BoundedSupersessionWorkScheduler (via debounce provider delegate)    │
└───────────────────────────────────────────────────────────────────────────────────┘
```

## Invariant Implementation Mapping

This section bridges architectural invariants (in `docs/sliding-window/invariants.md`) to their concrete implementations. Each invariant is enforced through specific component interactions, code patterns, or architectural constraints.

### Single-Writer Architecture
**Invariants**: SWC.A.1, SWC.A.11, SWC.A.12, SWC.A.12a, SWC.F.2

Only `RebalanceExecutor` has write access to `CacheState` internal setters. User Path components have read-only references. Internal visibility modifiers prevent external mutations.

- `src/Intervals.NET.Caching.SlidingWindow/Core/State/CacheState.cs` — internal setters restrict write access
- `src/Intervals.NET.Caching.SlidingWindow/Core/Rebalance/Execution/RebalanceExecutor.cs` — exclusive mutation authority
- `src/Intervals.NET.Caching.SlidingWindow/Core/UserPath/UserRequestHandler.cs` — read-only access pattern

### Priority and Cancellation
**Invariants**: SWC.A.2, SWC.A.2a, SWC.C.3, SWC.F.1a

`CancellationTokenSource` coordination between intent publishing and execution. `RebalanceDecisionEngine` validates necessity before triggering cancellation. Multiple checkpoints in execution pipeline check for cancellation.

- `src/Intervals.NET.Caching.SlidingWindow/Core/Rebalance/Intent/IntentController.cs` — cancellation token lifecycle
- `src/Intervals.NET.Caching.SlidingWindow/Core/Rebalance/Decision/RebalanceDecisionEngine.cs` — validation gates cancellation
- `src/Intervals.NET.Caching.SlidingWindow/Core/Rebalance/Execution/RebalanceExecutor.cs` — `ThrowIfCancellationRequested` checkpoints

### Intent Management and Cancellation
**Invariants**: SWC.A.2a, SWC.C.1, SWC.C.4, SWC.C.5

`Interlocked.Exchange` replaces previous intent atomically (latest-wins). Single-writer architecture for intent state. Cancellation checked after debounce delay before execution starts.

- `src/Intervals.NET.Caching.SlidingWindow/Core/Rebalance/Intent/IntentController.cs` — atomic intent replacement

### UserRequestHandler Responsibilities
**Invariants**: SWC.A.5, SWC.A.7

Only `UserRequestHandler` has access to `IntentController.PublishIntent`. Its scope is limited to data assembly; no normalization logic.

- `src/Intervals.NET.Caching.SlidingWindow/Core/UserPath/UserRequestHandler.cs` — exclusive intent publisher
- `src/Intervals.NET.Caching.SlidingWindow/Core/Rebalance/Intent/IntentController.cs` — internal visibility on publication interface

### Async Execution Model
**Invariants**: SWC.A.6, SWC.G.2

`UserRequestHandler` publishes intent and returns immediately (fire-and-forget). `IWorkScheduler<ExecutionRequest<...>>` schedules execution via task chaining or channels. User thread and ThreadPool thread contexts are separated.

- `src/Intervals.NET.Caching.SlidingWindow/Core/Rebalance/Intent/IntentController.cs` — `ProcessIntentsAsync` runs on background thread
- `src/Intervals.NET.Caching/Infrastructure/Scheduling/Supersession/UnboundedSupersessionWorkScheduler.cs` — latest-wins task-chaining serialization
- `src/Intervals.NET.Caching/Infrastructure/Scheduling/Supersession/BoundedSupersessionWorkScheduler.cs` — channel-based background execution with supersession

### Atomic Cache Updates
**Invariants**: SWC.B.2, SWC.B.3

Storage strategies build new state before atomic swap. `Volatile.Write` atomically publishes new cache state reference (Snapshot). `CopyOnReadStorage` uses a lock-protected buffer swap instead. `Rematerialize` succeeds completely or not at all.

- `src/Intervals.NET.Caching.SlidingWindow/Infrastructure/Storage/SnapshotReadStorage.cs` — `Array.Copy` + `Volatile.Write`
- `src/Intervals.NET.Caching.SlidingWindow/Infrastructure/Storage/CopyOnReadStorage.cs` — lock-protected dual-buffer swap (`_lock`)
- `src/Intervals.NET.Caching.SlidingWindow/Core/State/CacheState.cs` — `Rematerialize` ensures atomicity

### Consistency Under Cancellation
**Invariants**: SWC.B.3, SWC.B.5, SWC.F.1b

Final cancellation check before applying cache updates. Results applied atomically or discarded entirely. `try-finally` blocks ensure cleanup on cancellation.

- `src/Intervals.NET.Caching.SlidingWindow/Core/Rebalance/Execution/RebalanceExecutor.cs` — `ThrowIfCancellationRequested` before `Rematerialize`

### Obsolete Result Prevention
**Invariants**: SWC.B.6, SWC.C.4

Each intent has a unique `CancellationToken`. Execution checks if cancellation is requested before applying results. Only results from the latest non-cancelled intent are applied.

- `src/Intervals.NET.Caching.SlidingWindow/Core/Rebalance/Execution/RebalanceExecutor.cs` — cancellation validation before mutation
- `src/Intervals.NET.Caching.SlidingWindow/Core/Rebalance/Intent/IntentController.cs` — token lifecycle management

### Intent Singularity
**Invariant**: SWC.C.1

`Interlocked.Exchange` ensures exactly one active intent. New intent atomically replaces previous one. At most one pending intent at any time (no queue buildup).

- `src/Intervals.NET.Caching.SlidingWindow/Core/Rebalance/Intent/IntentController.cs` — `Interlocked.Exchange` for atomic intent replacement

### Cancellation Protocol
**Invariant**: SWC.C.3

`CancellationToken` passed through the entire pipeline. Multiple checkpoints: before I/O, after I/O, before mutations. Results from cancelled operations are never applied.

- `src/Intervals.NET.Caching.SlidingWindow/Core/Rebalance/Execution/RebalanceExecutor.cs` — multiple `ThrowIfCancellationRequested` calls
- `src/Intervals.NET.Caching.SlidingWindow/Infrastructure/Services/CacheDataExtensionService.cs` — cancellation token propagated to `IDataSource`

### Early Exit Validation
**Invariants**: SWC.C.4, SWC.D.5

Post-debounce cancellation check before execution. Each validation stage can exit early. All stages must pass for execution to proceed.

- `src/Intervals.NET.Caching.SlidingWindow/Core/Rebalance/Intent/IntentController.cs` — cancellation check after debounce
- `src/Intervals.NET.Caching.SlidingWindow/Core/Rebalance/Decision/RebalanceDecisionEngine.cs` — multi-stage early exit

### Serial Execution Guarantee
**Invariant**: SWC.C.5

Previous execution cancelled before starting new one. Single `IWorkScheduler<ExecutionRequest<...>>` instance per cache. Intent processing loop ensures serial execution.

- `src/Intervals.NET.Caching.SlidingWindow/Core/Rebalance/Intent/IntentController.cs` — sequential intent loop + cancellation of prior execution

### Intent Data Contract
**Invariant**: SWC.C.8e

`PublishIntent` signature requires `deliveredData` parameter. `UserRequestHandler` materializes data once, passes it to both user and intent. Compiler enforces data presence.

- `src/Intervals.NET.Caching.SlidingWindow/Core/Rebalance/Intent/IntentController.cs` — `PublishIntent(requestedRange, deliveredData)` signature
- `src/Intervals.NET.Caching.SlidingWindow/Core/UserPath/UserRequestHandler.cs` — single data materialization shared between paths

### Pure Decision Logic
**Invariants**: SWC.D.1, SWC.D.2

`RebalanceDecisionEngine` has no mutable fields. Decision policies are classes with no side effects. No I/O in decision path. Pure function: `(state, intent, config) → decision`.

- `src/Intervals.NET.Caching.SlidingWindow/Core/Rebalance/Decision/RebalanceDecisionEngine.cs` — pure evaluation logic
- `src/Intervals.NET.Caching.SlidingWindow/Core/Planning/NoRebalanceSatisfactionPolicy.cs` — stateless policy
- `src/Intervals.NET.Caching.SlidingWindow/Core/Planning/ProportionalRangePlanner.cs` — stateless planner

### Decision-Execution Separation
**Invariant**: SWC.D.2

Decision components have no references to mutable state setters. Decision Engine reads `CacheState` but cannot modify it. Decision and Execution interfaces are distinct.

- `src/Intervals.NET.Caching.SlidingWindow/Core/Rebalance/Decision/RebalanceDecisionEngine.cs` — read-only state access
- `src/Intervals.NET.Caching.SlidingWindow/Core/Rebalance/Execution/RebalanceExecutor.cs` — exclusive write access

### Multi-Stage Decision Pipeline
**Invariant**: SWC.D.5

Five-stage pipeline with early exits. Stage 1: current `NoRebalanceRange` containment (fast path). Stage 2: pending `NoRebalanceRange` validation (thrashing prevention). Stage 3: `DesiredCacheRange` computation. Stage 4: equality check (`DesiredCacheRange == CurrentCacheRange`). Stage 5: execution scheduling (only if all stages pass).

- `src/Intervals.NET.Caching.SlidingWindow/Core/Rebalance/Decision/RebalanceDecisionEngine.cs` — complete pipeline implementation

### Desired Range Computation
**Invariants**: SWC.E.1, SWC.E.2

`ProportionalRangePlanner.Plan(requestedRange, config)` is a pure function — same inputs always produce same output. Never reads `CurrentCacheRange`. Reads configuration from a shared `RuntimeCacheOptionsHolder` at invocation time to support runtime option updates.

- `src/Intervals.NET.Caching.SlidingWindow/Core/Planning/ProportionalRangePlanner.cs` — pure range calculation

### NoRebalanceRange Computation
**Invariants**: SWC.E.5, SWC.E.6

`NoRebalanceRangePlanner.Plan(currentCacheRange)` — pure function of current range + config. Applies threshold percentages as negative expansion. Returns `null` when individual thresholds ≥ 1.0 (no stability zone possible). `SlidingWindowCacheOptions` constructor ensures threshold sum ≤ 1.0 at construction time. Reads configuration from a shared `RuntimeCacheOptionsHolder` at invocation time to support runtime option updates.

- `src/Intervals.NET.Caching.SlidingWindow/Core/Planning/NoRebalanceRangePlanner.cs` — NoRebalanceRange computation
- `src/Intervals.NET.Caching.SlidingWindow/Public/Configuration/SlidingWindowCacheOptions.cs` — threshold sum validation

### Cancellation Checkpoints
**Invariants**: SWC.F.1, SWC.F.1a

Three checkpoints: before `IDataSource.FetchAsync`, after data fetching, before `Rematerialize`. `OperationCanceledException` propagates to cleanup handlers.

- `src/Intervals.NET.Caching.SlidingWindow/Core/Rebalance/Execution/RebalanceExecutor.cs` — multiple checkpoint locations

### Cache Normalization Operations
**Invariant**: SWC.F.3

`CacheState.Rematerialize` accepts arbitrary range and data (full replacement). `ICacheStorage` abstraction enables different normalization strategies.

- `src/Intervals.NET.Caching.SlidingWindow/Core/State/CacheState.cs` — `Rematerialize` method
- `src/Intervals.NET.Caching.SlidingWindow/Infrastructure/Storage/` — storage strategy implementations

### Incremental Data Fetching
**Invariant**: SWC.F.4

`CacheDataExtensionService.ExtendCacheDataAsync` computes missing ranges via range subtraction (`DesiredRange \ CachedRange`). Fetches only missing subranges via `IDataSource`.

- `src/Intervals.NET.Caching.SlidingWindow/Infrastructure/Services/CacheDataExtensionService.cs` — range gap logic in `ExtendCacheDataAsync`

### Data Preservation During Expansion
**Invariant**: SWC.F.5

New data merged with existing via range union. Existing data enumerated and preserved during rematerialization. New data only fills gaps; does not replace existing.

- `src/Intervals.NET.Caching.SlidingWindow/Infrastructure/Services/CacheDataExtensionService.cs` — union logic in `ExtendCacheDataAsync`

### I/O Isolation
**Invariant**: SWC.G.3

`UserRequestHandler` completes before any `IDataSource.FetchAsync` calls in rebalance path. All `IDataSource` interactions happen in `RebalanceExecutor` on a background thread.

- `src/Intervals.NET.Caching.SlidingWindow/Core/UserPath/UserRequestHandler.cs` — no rebalance-path `IDataSource` calls
- `src/Intervals.NET.Caching.SlidingWindow/Core/Rebalance/Execution/RebalanceExecutor.cs` — `IDataSource` calls only in background execution

### Activity Counter Ordering
**Invariant**: S.H.1

Activity counter incremented **before** semaphore signal, channel write, or volatile write (strict ordering discipline at all publication sites).

- `src/Intervals.NET.Caching.SlidingWindow/Core/Rebalance/Intent/IntentController.cs` — increment before `semaphore.Release`
- `src/Intervals.NET.Caching.SlidingWindow/Infrastructure/Execution/` — increment before `Volatile.Write` (task chain step) or channel write

### Activity Counter Cleanup
**Invariant**: S.H.2

Decrement in `finally` blocks — unconditional execution regardless of success, failure, or cancellation.

- `src/Intervals.NET.Caching.SlidingWindow/Core/Rebalance/Intent/IntentController.cs` — `finally` block in `ProcessIntentsAsync`
- `src/Intervals.NET.Caching.SlidingWindow/Infrastructure/Execution/` — `finally` blocks in execution controllers

---

## Architectural Patterns Used

### 1. Facade Pattern
`SlidingWindowCache` acts as a facade that hides internal complexity and provides a simple public API. Contains no business logic; all behavioral logic is delegated to internal actors.

### 2. Composition Root
`SlidingWindowCache` constructor wires all components together in one place.

### 3. Actor Model (Conceptual)
Components follow actor-like patterns with clear responsibilities and message passing (method calls). Each actor has a defined execution context and responsibility boundary.

### 4. Intent Controller Pattern
`IntentController` manages versioned, cancellable operations through `CancellationTokenSource` identity and `Interlocked.Exchange` latest-wins semantics.

### 5. Strategy Pattern
`ICacheStorage` with two implementations (`SnapshotReadStorage`, `CopyOnReadStorage`) allows runtime selection of storage strategy based on read/write trade-offs.

### 6. Value Object Pattern
`RebalanceDecision` is an immutable value type with pure behavior (no side effects, deterministic). `NoRebalanceSatisfactionPolicy` and `ProportionalRangePlanner` are `internal sealed class` types (stateless, pure functions).

### 7. Shared Mutable State (Controlled)
`CacheState` is intentionally shared mutable state, coordinated via single-writer architecture (not locks). The single writer (`RebalanceExecutor`) is the sole authority for mutations.

### 8. Single Consumer Model
The entire architecture assumes one logical consumer, avoiding traditional synchronization primitives in favor of architectural constraints (single-writer, read-only User Path).

---

## Invariants

Canonical invariants live in `docs/sliding-window/invariants.md`. Component-level details in this folder focus on "what exists" and "who does what"; they link back to the formal rules.

## Usage

Contributors should read in this order:

1. `docs/sliding-window/components/public-api.md`
2. `docs/sliding-window/components/user-path.md`
3. `docs/sliding-window/components/intent-management.md`
4. `docs/sliding-window/components/decision.md`
5. `docs/sliding-window/components/execution.md`
6. `docs/sliding-window/components/state-and-storage.md`
7. `docs/sliding-window/components/infrastructure.md`

## See Also

- `docs/sliding-window/scenarios.md` — step-by-step temporal walkthroughs
- `docs/sliding-window/actors.md` — actor responsibilities and invariant ownership
- `docs/sliding-window/architecture.md` — threading model and concurrency details
- `docs/sliding-window/invariants.md` — formal invariant specifications

## Edge Cases

- "Latest intent wins" means intermediate intents can be skipped; this is by design.

## Limitations

- Component docs are descriptive; algorithm-level detail is in source XML docs.
