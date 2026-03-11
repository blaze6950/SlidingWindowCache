# Actors — SlidingWindow Cache

This document is the canonical actor catalog for `SlidingWindowCache`. For the shared actor pattern, see `docs/shared/actors.md`. Formal invariants live in `docs/sliding-window/invariants.md`.

---

## Execution Contexts

- **User Thread** — serves `GetDataAsync` and `UpdateRuntimeOptions`; ends at `PublishIntent()` return.
- **Background Intent Loop** — evaluates the latest intent, runs the decision engine, and publishes validated execution requests.
- **Background Execution Loop** — debounced, cancellable rebalance work and cache mutation.

---

## Actors

### User Path

**Responsibilities**
- Serve user requests immediately.
- Assemble `RequestedRange` from cache and/or `IDataSource`.
- Publish an intent containing delivered data.

**Non-responsibilities**
- Does not decide whether to rebalance.
- Does not mutate shared cache state.
- Does not check `NoRebalanceRange` (belongs to Decision Engine).
- Does not compute `DesiredCacheRange` (belongs to Cache Geometry Policy).

**Invariant ownership**
- SWC.A.1. User Path and Rebalance Execution never write to cache concurrently
- SWC.A.2. User Path has higher priority than rebalance execution
- SWC.A.2a. User request MAY cancel any ongoing or pending Rebalance Execution ONLY when a new rebalance is validated as necessary
- SWC.A.3. User Path always serves user requests
- SWC.A.4. User Path never waits for rebalance execution
- SWC.A.5. User Path is the sole source of rebalance intent
- SWC.A.7. Performs only work necessary to return data
- SWC.A.8. May synchronously request from `IDataSource`
- SWC.A.11. May read cache and source, but does not mutate cache state
- SWC.A.12. MUST NOT mutate cache under any circumstance (read-only)
- SWC.C.8e. Intent MUST contain delivered data (`RangeData`)
- SWC.C.8f. Delivered data represents what user actually received

**Components**
- `SlidingWindowCache<TRange, TData, TDomain>` — facade / composition root; also owns `RuntimeCacheOptionsHolder` and exposes `UpdateRuntimeOptions`
- `UserRequestHandler<TRange, TData, TDomain>`
- `CacheDataExtensionService<TRange, TData, TDomain>`

---

### Cache Geometry Policy

**Responsibilities**
- Compute `DesiredCacheRange` from `RequestedRange` + size configuration.
- Compute `NoRebalanceRange` from `CurrentCacheRange` + threshold configuration.
- Encapsulate all sliding window geometry rules (sizes, thresholds).

**Non-responsibilities**
- Does not schedule execution.
- Does not mutate cache state.
- Does not perform I/O.

**Invariant ownership**
- SWC.E.1. `DesiredCacheRange` computed from `RequestedRange` + config
- SWC.E.2. Independent of current cache contents
- SWC.E.3. Canonical target cache state
- SWC.E.4. Sliding window geometry defined by configuration
- SWC.E.5. `NoRebalanceRange` derived from current cache range + config
- SWC.E.6. Threshold sum constraint (`leftThreshold + rightThreshold ≤ 1.0`)

**Components**
- `ProportionalRangePlanner<TRange, TDomain>` — computes `DesiredCacheRange`; reads configuration from `RuntimeCacheOptionsHolder` at invocation time
- `NoRebalanceSatisfactionPolicy<TRange>` / `NoRebalanceRangePlanner<TRange, TDomain>` — computes `NoRebalanceRange`; reads configuration from `RuntimeCacheOptionsHolder` at invocation time

---

### Rebalance Decision

**Responsibilities**
- Sole authority for rebalance necessity.
- Analytical validation only (CPU-only, deterministic, no side effects).
- Enable smart eventual consistency through multi-stage work avoidance.

**Non-responsibilities**
- Does not schedule execution directly.
- Does not mutate cache state.
- Does not call `IDataSource`.

**Invariant ownership**
- SWC.D.1. Decision Path is purely analytical (CPU-only, no I/O)
- SWC.D.2. Never mutates cache state
- SWC.D.3. No rebalance if inside `NoRebalanceRange` (Stage 1 validation)
- SWC.D.4. No rebalance if `DesiredCacheRange == CurrentCacheRange` (Stage 4 validation)
- SWC.D.5. Rebalance triggered only if ALL validation stages confirm necessity

**Components**
- `RebalanceDecisionEngine<TRange, TDomain>`
- `ProportionalRangePlanner<TRange, TDomain>`
- `NoRebalanceSatisfactionPolicy<TRange>` / `NoRebalanceRangePlanner<TRange, TDomain>`

---

### Intent Management

**Responsibilities**
- Own intent lifecycle and supersession (latest wins).
- Run the background intent loop and orchestrate decision → cancel → publish execution request.
- Cancellation coordination based on validation results (not a standalone decision mechanism).

**Non-responsibilities**
- Does not mutate cache state.
- Does not perform I/O.
- Does not determine rebalance necessity (delegates to Decision Engine).

**Invariant ownership**
- SWC.C.1. At most one active rebalance intent
- SWC.C.2. Older intents may become logically superseded
- SWC.C.3. Executions can be cancelled based on validation results
- SWC.C.4. Obsolete intent must not start execution
- SWC.C.5. At most one rebalance execution active
- SWC.C.6. Execution reflects latest access pattern and validated necessity
- SWC.C.7. System eventually stabilizes under load through work avoidance
- SWC.C.8. Intent does not guarantee execution — execution is opportunistic and validation-driven

**Components**
- `IntentController<TRange, TData, TDomain>`
- `IWorkScheduler<ExecutionRequest<TRange, TData, TDomain>>` implementations (generic scheduler in `Intervals.NET.Caching`)

---

### Rebalance Execution Control

**Responsibilities**
- Debounce and serialize validated executions.
- Cancel obsolete scheduled/active work so only the latest validated execution wins.

**Non-responsibilities**
- Does not decide necessity.
- Does not determine rebalance necessity (DecisionEngine already validated).

**Components**
- `UnboundedSerialWorkScheduler<ExecutionRequest<TRange, TData, TDomain>>` (default; in `Intervals.NET.Caching`)
- `BoundedSerialWorkScheduler<ExecutionRequest<TRange, TData, TDomain>>` (bounded; in `Intervals.NET.Caching`)

---

### Mutation (Single Writer)

**Responsibilities**
- Perform the only mutations of shared cache state.
- Apply cache updates atomically during normalization.
- Mechanically simple: no analytical decisions; assumes decision layer already validated necessity.

**Non-responsibilities**
- Does not validate rebalance necessity.
- Does not check `NoRebalanceRange` (Stage 1 already passed).
- Does not check if `DesiredCacheRange == CurrentCacheRange` (Stage 4 already passed).

**Invariant ownership**
- SWC.A.6. Rebalance is asynchronous relative to User Path
- SWC.F.1. MUST support cancellation at all stages
- SWC.F.1a. MUST yield to User Path requests immediately upon cancellation
- SWC.F.1b. Partially executed or cancelled execution MUST NOT leave cache inconsistent
- SWC.F.2. Only path responsible for cache normalization (single-writer architecture)
- SWC.F.2a. Mutates cache ONLY for normalization using delivered data from intent
- SWC.F.3. May replace / expand / shrink cache to achieve normalization
- SWC.F.4. Requests data only for missing subranges (not covered by delivered data)
- SWC.F.5. Does not overwrite intersecting data
- SWC.F.6. Upon completion: `CacheData` corresponds to `DesiredCacheRange`
- SWC.F.7. Upon completion: `CurrentCacheRange == DesiredCacheRange`
- SWC.F.8. Upon completion: `NoRebalanceRange` recomputed

**Components**
- `RebalanceExecutor<TRange, TData, TDomain>`
- `CacheState<TRange, TData, TDomain>`

---

### Cache State Manager

**Responsibilities**
- Ensure atomicity and internal consistency of cache state.
- Coordinate single-writer access between User Path (reads) and Rebalance Execution (writes).

**Invariant ownership**
- SWC.B.1. `CacheData` and `CurrentCacheRange` are consistent
- SWC.B.2. Changes applied atomically
- SWC.B.3. No permanent inconsistent state
- SWC.B.4. Temporary inefficiencies are acceptable
- SWC.B.5. Partial / cancelled execution cannot break consistency
- SWC.B.6. Only latest intent results may be applied

**Components**
- `CacheState<TRange, TData, TDomain>`

---

### Resource Management

**Responsibilities**
- Graceful shutdown and idempotent disposal of background loops and resources.

**Components**
- `SlidingWindowCache<TRange, TData, TDomain>` and owned internals

---

## Actor Execution Context Summary

| Actor | Execution Context | Invoked By |
|---|---|---|
| `UserRequestHandler` | User Thread | User (public API) |
| `IntentController.PublishIntent` | User Thread (atomic publish only) | `UserRequestHandler` |
| `IntentController.ProcessIntentsAsync` | Background Loop #1 (intent processing) | Background task (awaits semaphore) |
| `RebalanceDecisionEngine` | Background Loop #1 (intent processing) | `IntentController.ProcessIntentsAsync` |
| `CacheGeometryPolicy` (both components) | Background Loop #1 (intent processing) | `RebalanceDecisionEngine` |
| `IWorkScheduler.PublishWorkItemAsync` | Background Loop #1 (intent processing) | `IntentController.ProcessIntentsAsync` |
| `UnboundedSerialWorkScheduler` | Background (ThreadPool task chain) | Via interface (default strategy) |
| `BoundedSerialWorkScheduler` | Background Loop #2 (channel reader) | Via interface (optional strategy) |
| `RebalanceExecutor` | Background Execution (both strategies) | `IWorkScheduler` implementations |
| `CacheState` | Both (User: reads; Background execution: writes) | Both paths (single-writer) |

**Critical:** The user thread ends at `PublishIntent()` return (after atomic operations only). Decision evaluation runs in the background intent loop. Cache mutations run in a separate background execution loop.

---

## Actors vs Scenarios Reference

| Scenario | User Path | Decision Engine | Geometry Policy | Intent Management | Rebalance Executor | Cache State Manager |
|---|---|---|---|---|---|---|
| **U1 – Cold Cache** | Requests from `IDataSource`, returns data, publishes intent | — | Computes `DesiredCacheRange` | Receives intent | Executes rebalance (writes `IsInitialized`, `CurrentCacheRange`, `CacheData`) | Validates atomic update |
| **U2 – Full Cache Hit (Exact)** | Reads from cache, publishes intent | Checks `NoRebalanceRange` | Computes `DesiredCacheRange` | Receives intent | Executes if required | Monitors consistency |
| **U3 – Full Cache Hit (Shifted)** | Reads subrange from cache, publishes intent | Checks `NoRebalanceRange` | Computes `DesiredCacheRange` | Receives intent | Executes if required | Monitors consistency |
| **U4 – Partial Cache Hit** | Reads intersection, requests missing from `IDataSource`, merges, publishes intent | Checks `NoRebalanceRange` | Computes `DesiredCacheRange` | Receives intent | Executes merge and normalization | Ensures atomic merge |
| **U5 – Full Cache Miss (Jump)** | Requests full range from `IDataSource`, publishes intent | Checks `NoRebalanceRange` | Computes `DesiredCacheRange` | Receives intent | Executes full normalization | Ensures atomic replacement |
| **D1 – NoRebalanceRange Block** | — | Checks `NoRebalanceRange`, decides no execution | — | Receives intent (blocked) | — | — |
| **D2 – Desired == Current** | — | Computes `DesiredCacheRange`, decides no execution | Computes `DesiredCacheRange` | Receives intent (no-op) | — | — |
| **D3 – Rebalance Required** | — | Computes `DesiredCacheRange`, confirms execution | Computes `DesiredCacheRange` | Issues rebalance request | Executes rebalance | Ensures consistency |
| **R1 – Build from Scratch** | — | — | Defines `DesiredCacheRange` | Receives intent | Requests full range, replaces cache | Atomic replacement |
| **R2 – Expand Cache** | — | — | Defines `DesiredCacheRange` | Receives intent | Requests missing subranges, merges | Atomic merge |
| **R3 – Shrink / Normalize** | — | — | Defines `DesiredCacheRange` | Receives intent | Trims cache to `DesiredCacheRange` | Atomic trim |
| **C1 – Rebalance Trigger Pending** | Executes normally | — | — | Debounces, allows only latest | Cancels obsolete | Ensures atomicity |
| **C2 – Rebalance Executing** | Executes normally | — | — | Marks latest intent | Cancels or discards obsolete | Ensures atomicity |
| **C3 – Spike / Multiple Requests** | Executes normally | — | — | Debounces & coordinates intents | Executes only latest | Ensures atomicity |

---

## Architectural Summary

| Actor | Primary Concern |
|---|---|
| User Path | Speed and availability |
| Cache Geometry Policy | Deterministic cache shape |
| Rebalance Decision | Correctness of necessity determination |
| Intent Management | Time, concurrency, and pipeline orchestration |
| Mutation (Single Writer) | Physical cache mutation |
| Cache State Manager | Safety and consistency |
| Resource Management | Lifecycle and cleanup |

---

## See Also

- `docs/shared/actors.md` — shared actor pattern
- `docs/sliding-window/architecture.md`
- `docs/sliding-window/scenarios.md`
- `docs/sliding-window/invariants.md`
- `docs/sliding-window/components/overview.md`
