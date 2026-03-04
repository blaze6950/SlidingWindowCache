# Actors

## Overview

Actors are stable responsibilities in the system. They are not necessarily 1:1 with classes; classes implement actor responsibilities.

This document is the canonical merge of the legacy actor mapping docs. It focuses on:

- responsibility and non-responsibility boundaries
- invariant ownership per actor
- execution context
- concrete components involved

Formal rules live in `docs/invariants.md`.

## Execution Contexts

- User thread: serves `GetDataAsync`.
- Background intent loop: evaluates the latest intent and produces validated execution requests.
- Background execution: debounced, cancellable rebalance work and cache mutation.

## Actors

### User Path

Responsibilities
- Serve user requests immediately.
- Assemble `RequestedRange` from cache and/or `IDataSource`.
- Publish an intent containing delivered data.

Non-responsibilities
- Does not decide whether to rebalance.
- Does not mutate shared cache state.
- Does not check `NoRebalanceRange` (belongs to Decision Engine).
- Does not compute `DesiredCacheRange` (belongs to Cache Geometry Policy).

Invariant ownership
- -1. User Path and Rebalance Execution never write to cache concurrently
- 0. User Path has higher priority than rebalance execution
- 0a. User Request MAY cancel any ongoing or pending Rebalance Execution ONLY when a new rebalance is validated as necessary
- 1. User Path always serves user requests
- 2. User Path never waits for rebalance execution
- 3. User Path is the sole source of rebalance intent
- 5. Performs only work necessary to return data
- 6. May synchronously request from IDataSource
- 7. May read cache and source, but does not mutate cache state
- 8. MUST NOT mutate cache under any circumstance (read-only)
- 10. Always returns exactly RequestedRange
- 24e. Intent MUST contain delivered data (RangeData)
- 24f. Delivered data represents what user actually received

Components
- `WindowCache<TRange, TData, TDomain>` (facade / composition root; also owns `RuntimeCacheOptionsHolder` and exposes `UpdateRuntimeOptions`)
- `UserRequestHandler<TRange, TData, TDomain>`
- `CacheDataExtensionService<TRange, TData, TDomain>`

---

### Cache Geometry Policy

Responsibilities
- Compute `DesiredCacheRange` from `RequestedRange` + size configuration.
- Compute `NoRebalanceRange` from `CurrentCacheRange` + threshold configuration.
- Encapsulate all sliding window geometry rules (sizes, thresholds).

Non-responsibilities
- Does not schedule execution.
- Does not mutate cache state.
- Does not perform I/O.

Invariant ownership
- 29. DesiredCacheRange computed from RequestedRange + config
- 30. Independent of current cache contents
- 31. Canonical target cache state
- 32. Sliding window geometry defined by configuration
- 33. NoRebalanceRange derived from current cache range + config
- 35. Threshold sum constraint (leftThreshold + rightThreshold ≤ 1.0)

Components
- `ProportionalRangePlanner<TRange, TDomain>` — computes `DesiredCacheRange`; reads configuration from `RuntimeCacheOptionsHolder` at invocation time
- `NoRebalanceSatisfactionPolicy<TRange>` / `NoRebalanceRangePlanner<TRange, TDomain>` — computes `NoRebalanceRange`; reads configuration from `RuntimeCacheOptionsHolder` at invocation time

---

### Rebalance Decision

Responsibilities
- Sole authority for rebalance necessity.
- Analytical validation only (CPU-only, deterministic, no side effects).
- Enable smart eventual consistency through multi-stage work avoidance.

Non-responsibilities
- Does not schedule execution directly.
- Does not mutate cache state.
- Does not call `IDataSource`.

Invariant ownership
- 24. Decision Path is purely analytical (CPU-only, no I/O)
- 25. Never mutates cache state
- 26. No rebalance if inside NoRebalanceRange (Stage 1 validation)
- 27. No rebalance if DesiredCacheRange == CurrentCacheRange (Stage 4 validation)
- 28. Rebalance triggered only if ALL validation stages confirm necessity

Components
- `RebalanceDecisionEngine<TRange, TDomain>`
- `ProportionalRangePlanner<TRange, TDomain>`
- `NoRebalanceSatisfactionPolicy<TRange>` / `NoRebalanceRangePlanner<TRange, TDomain>`

---

### Intent Management

Responsibilities
- Own intent lifecycle and supersession (latest wins).
- Run the background intent loop and orchestrate decision → cancel → publish execution request.
- Cancellation coordination based on validation results (not a standalone decision mechanism).

Non-responsibilities
- Does not mutate cache state.
- Does not perform I/O.
- Does not determine rebalance necessity (delegates to Decision Engine).

Invariant ownership
- 17. At most one active rebalance intent
- 18. Older intents may become logically superseded
- 19. Executions can be cancelled based on validation results
- 20. Obsolete intent must not start execution
- 21. At most one rebalance execution active
- 22. Execution reflects latest access pattern and validated necessity
- 23. System eventually stabilizes under load through work avoidance
- 24. Intent does not guarantee execution — execution is opportunistic and validation-driven

Components
- `IntentController<TRange, TData, TDomain>`
- `IRebalanceExecutionController<TRange, TData, TDomain>` implementations

---

### Rebalance Execution Control

Responsibilities
- Debounce and serialize validated executions.
- Cancel obsolete scheduled/active work so only the latest validated execution wins.

Non-responsibilities
- Does not decide necessity.
- Does not determine rebalance necessity (DecisionEngine already validated).

Components
- `IRebalanceExecutionController<TRange, TData, TDomain>` implementations

---

### Mutation (Single Writer)

Responsibilities
- Perform the only mutations of shared cache state.
- Apply cache updates atomically during normalization.
- Mechanically simple: no analytical decisions; assumes decision layer already validated necessity.

Non-responsibilities
- Does not validate rebalance necessity.
- Does not check `NoRebalanceRange` (Stage 1 already passed).
- Does not check if `DesiredCacheRange == CurrentCacheRange` (Stage 4 already passed).

Invariant ownership
- A.4. Rebalance is asynchronous relative to User Path
- F.35. MUST support cancellation at all stages
- F.35a. MUST yield to User Path requests immediately upon cancellation
- F.35b. Partially executed or cancelled execution MUST NOT leave cache inconsistent
- F.36. Only path responsible for cache normalization (single-writer architecture)
- F.36a. Mutates cache ONLY for normalization using delivered data from intent
- F.37. May replace / expand / shrink cache to achieve normalization
- F.38. Requests data only for missing subranges (not covered by delivered data)
- F.39. Does not overwrite intersecting data
- F.40. Upon completion: CacheData corresponds to DesiredCacheRange
- F.41. Upon completion: CurrentCacheRange == DesiredCacheRange
- F.42. Upon completion: NoRebalanceRange recomputed

Components
- `RebalanceExecutor<TRange, TData, TDomain>`
- `CacheState<TRange, TData, TDomain>`

---

### Cache State Manager

Responsibilities
- Ensure atomicity and internal consistency of cache state.
- Coordinate single-writer access between User Path (reads) and Rebalance Execution (writes).

Invariant ownership
- 11. CacheData and CurrentCacheRange are consistent
- 12. Changes applied atomically
- 13. No permanent inconsistent state
- 14. Temporary inefficiencies are acceptable
- 15. Partial / cancelled execution cannot break consistency
- 16. Only latest intent results may be applied

Components
- `CacheState<TRange, TData, TDomain>`

---

### Resource Management

Responsibilities
- Graceful shutdown and idempotent disposal of background loops/resources.

Components
- `WindowCache<TRange, TData, TDomain>` and owned internals

---

## Actor Execution Contexts

| Actor                                      | Execution Context                                | Invoked By                                      |
|--------------------------------------------|--------------------------------------------------|-------------------------------------------------|
| `UserRequestHandler`                       | User Thread                                      | User (public API)                               |
| `IntentController.PublishIntent`           | User Thread (atomic publish only)                | `UserRequestHandler`                            |
| `IntentController.ProcessIntentsAsync`     | Background Loop #1 (intent processing)           | Background task (awaits semaphore)              |
| `RebalanceDecisionEngine`                  | Background Loop #1 (intent processing)           | `IntentController.ProcessIntentsAsync`          |
| `CacheGeometryPolicy` (both components)    | Background Loop #1 (intent processing)           | `RebalanceDecisionEngine`                       |
| `IRebalanceExecutionController`            | Background Execution (strategy-specific)         | `IntentController.ProcessIntentsAsync`          |
| `TaskBasedRebalanceExecutionController`    | Background (ThreadPool task chain)               | Via interface (default strategy)                |
| `ChannelBasedRebalanceExecutionController` | Background Loop #2 (channel reader)              | Via interface (optional strategy)               |
| `RebalanceExecutor`                        | Background Execution (both strategies)           | `IRebalanceExecutionController` implementations |
| `CacheState`                               | Both (User: reads; Background execution: writes) | Both paths (single-writer)                      |

**Critical:** The user thread ends at `PublishIntent()` return (after atomic operations only). Decision evaluation runs in the background intent loop. Cache mutations run in a separate background execution loop.

---

## Actors vs Scenarios Reference

| Scenario                           | User Path                                                                       | Decision Engine                                  | Geometry Policy            | Intent Management               | Rebalance Executor                                                      | Cache State Manager        |
|------------------------------------|---------------------------------------------------------------------------------|--------------------------------------------------|----------------------------|---------------------------------|-------------------------------------------------------------------------|----------------------------|
| **U1 – Cold Cache**                | Requests from IDataSource, returns data, publishes intent                       | –                                                | Computes DesiredCacheRange | Receives intent                 | Executes rebalance (writes IsInitialized, CurrentCacheRange, CacheData) | Validates atomic update    |
| **U2 – Full Cache Hit (Exact)**    | Reads from cache, publishes intent                                              | Checks NoRebalanceRange                          | Computes DesiredCacheRange | Receives intent                 | Executes if required                                                    | Monitors consistency       |
| **U3 – Full Cache Hit (Shifted)**  | Reads subrange from cache, publishes intent                                     | Checks NoRebalanceRange                          | Computes DesiredCacheRange | Receives intent                 | Executes if required                                                    | Monitors consistency       |
| **U4 – Partial Cache Hit**         | Reads intersection, requests missing from IDataSource, merges, publishes intent | Checks NoRebalanceRange                          | Computes DesiredCacheRange | Receives intent                 | Executes merge and normalization                                        | Ensures atomic merge       |
| **U5 – Full Cache Miss (Jump)**    | Requests full range from IDataSource, publishes intent                          | Checks NoRebalanceRange                          | Computes DesiredCacheRange | Receives intent                 | Executes full normalization                                             | Ensures atomic replacement |
| **D1 – NoRebalanceRange Block**    | –                                                                               | Checks NoRebalanceRange, decides no execution    | –                          | Receives intent (blocked)       | –                                                                       | –                          |
| **D2 – Desired == Current**        | –                                                                               | Computes DesiredCacheRange, decides no execution | Computes DesiredCacheRange | Receives intent (no-op)         | –                                                                       | –                          |
| **D3 – Rebalance Required**        | –                                                                               | Computes DesiredCacheRange, confirms execution   | Computes DesiredCacheRange | Issues rebalance request        | Executes rebalance                                                      | Ensures consistency        |
| **R1 – Build from Scratch**        | –                                                                               | –                                                | Defines DesiredCacheRange  | Receives intent                 | Requests full range, replaces cache                                     | Atomic replacement         |
| **R2 – Expand Cache**              | –                                                                               | –                                                | Defines DesiredCacheRange  | Receives intent                 | Requests missing subranges, merges                                      | Atomic merge               |
| **R3 – Shrink / Normalize**        | –                                                                               | –                                                | Defines DesiredCacheRange  | Receives intent                 | Trims cache to DesiredCacheRange                                        | Atomic trim                |
| **C1 – Rebalance Trigger Pending** | Executes normally                                                               | –                                                | –                          | Debounces, allows only latest   | Cancels obsolete                                                        | Ensures atomicity          |
| **C2 – Rebalance Executing**       | Executes normally                                                               | –                                                | –                          | Marks latest intent             | Cancels or discards obsolete                                            | Ensures atomicity          |
| **C3 – Spike / Multiple Requests** | Executes normally                                                               | –                                                | –                          | Debounces & coordinates intents | Executes only latest                                                    | Ensures atomicity          |

---

## Architectural Summary

| Actor                    | Primary Concern                               |
|--------------------------|-----------------------------------------------|
| User Path                | Speed and availability                        |
| Cache Geometry Policy    | Deterministic cache shape                     |
| Rebalance Decision       | Correctness of necessity determination        |
| Intent Management        | Time, concurrency, and pipeline orchestration |
| Mutation (Single Writer) | Physical cache mutation                       |
| Cache State Manager      | Safety and consistency                        |
| Resource Management      | Lifecycle and cleanup                         |

## See Also

- `docs/architecture.md`
- `docs/scenarios.md`
- `docs/components/overview.md`
- `docs/invariants.md`
