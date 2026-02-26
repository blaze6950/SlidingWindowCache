# Sliding Window Cache — System Actors & Invariant Ownership

This document maps **system actors** to the invariants they enforce or guarantee.

> **📖 For detailed architectural explanations, see:**
> - [Architecture Model](architecture-model.md) - Threading model, decision-driven execution, single-writer architecture
> - [Invariants](invariants.md) - Complete invariant specifications
> - [Component Map](component-map.md) - Component relationships and structure

---

## 1. User Path (Fast Path / Read Path Actor)

**Role:**  
Handles user requests with minimal latency and maximal isolation from background processes.

**Implementation:**  
**Internal class:** `UserRequestHandler<TRange, TData, TDomain>` (in `UserPath/` namespace)  
**Public facade:** `WindowCache<TRange, TData, TDomain>` delegates all requests to UserRequestHandler

**Execution Context:**  
**Lives in: User Thread**

**Critical Contract:**
```
Every user access that results in assembled data publishes a rebalance intent containing
that delivered data. Requests where IDataSource returns null (physical boundary misses)
do not publish an intent — there is no data to embed (Invariant C.24e).
The UserRequestHandler is READ-ONLY with respect to cache state.
The UserRequestHandler NEVER invokes directly decision logic - it just publishes an intent.
```

**Responsible for invariants:**
- -1. User Path and Rebalance Execution never write to cache concurrently
- 0. User Path has higher priority than rebalance execution
- 0a. User Request MAY cancel any ongoing or pending Rebalance Execution ONLY when a new rebalance is validated as necessary
- 1. User Path always serves user requests
- 2. User Path never waits for rebalance execution
- 3. User Path is the sole source of rebalance intent
- 5. Performs only work necessary to return data
- 6. May synchronously request from IDataSource
- 7. May read cache and source, but does not mutate cache state
- 8. (NEW) MUST NOT mutate cache under any circumstance (read-only)
- 9a. Cache data MUST always remain contiguous (no gaps allowed)
- 10. Always returns exactly RequestedRange
- 24e. Intent MUST contain delivered data (RangeData)
- 24f. Delivered data represents what user actually received

**Explicit Non-Responsibilities:**
- ❌ **NEVER checks NoRebalanceRange** (belongs to DecisionEngine)
- ❌ **NEVER computes DesiredCacheRange** (belongs to GeometryPolicy)
- ❌ **NEVER decides whether to rebalance** (belongs to DecisionEngine)
- ❌ **NEVER writes to cache** (no Rematerialize calls)
- ❌ **NEVER writes to LastRequested**
- ❌ **NEVER writes to NoRebalanceRange**

**Responsibility Type:** ensures and enforces fast, correct user access with strict read-only boundaries

---

## 2. Rebalance Decision Engine (Pure Decision Actor)

**Role:**  
The **sole authority for rebalance necessity determination**. Analyzes the need for rebalance through multi-stage analytical validation without mutating system state. Enables **smart eventual consistency** through work avoidance mechanisms.

**Execution Context:**  
**Lives in: Background Thread** (invoked by `IntentController.ProcessIntentsAsync` in the background intent processing loop)

**Visibility:**
- **Not visible to external users**
- **Owned and invoked by IntentController** (not by Scheduler)
- Invoked from `IntentController.ProcessIntentsAsync()` (background intent processing loop)
- May execute many times; work avoidance allows skipping scheduling entirely

**Critical Rule:**
```
DecisionEngine lives in the background intent processing loop.
DecisionEngine is THE ONLY authority for rebalance necessity determination.
All execution decisions flow from this component's analytical validation.
Decision happens BEFORE execution is scheduled, preventing work buildup.
IntentController OWNS the DecisionEngine instance.
```

**Multi-Stage Validation Pipeline (Work Avoidance):**
1. **Stage 1**: Current Cache NoRebalanceRange containment check (fast path work avoidance)
2. **Stage 2**: Pending Desired Cache NoRebalanceRange validation (anti-thrashing — fully implemented)
3. **Stage 3**: Compute DesiredCacheRange from RequestedRange + configuration
4. **Stage 4**: DesiredCacheRange vs CurrentCacheRange equality check (no-op prevention)

**Enables Smart Eventual Consistency:**
- Prevents thrashing through multi-stage validation
- Reduces redundant I/O via work avoidance (skip unnecessary operations)
- Maintains stability under rapidly changing access patterns
- Ensures convergence to optimal configuration without aggressive over-execution

**Responsible for invariants:**
- 24. Decision Path is purely analytical (CPU-only, no I/O)
- 25. Never mutates cache state
- 26. No rebalance if inside NoRebalanceRange (Stage 1 validation)
- 27. No rebalance if DesiredCacheRange == CurrentCacheRange (Stage 4 validation)
- 28. Rebalance triggered only if ALL validation stages confirm necessity

**Responsibility Type:** ensures correctness of rebalance necessity decisions through analytical validation, enabling smart eventual consistency

**Note:** Not a top-level actor — internal tool of IntentManager/Executor pipeline, but THE authority for necessity determination and work avoidance.

---

## 3. Cache Geometry Policy (Configuration & Policy Actor)

**Role:**  
Defines canonical sliding window shape and rules.

**Implementation:**
This logical actor is internally decomposed into two components for separation of concerns:
- **NoRebalanceRangePlanner** - Computes NoRebalanceRange, checks threshold-based triggering
- **ProportionalRangePlanner** - Computes DesiredCacheRange, plans cache geometry

**Configuration Validation** (WindowCacheOptions):
- Cache size coefficients ≥ 0
- Individual thresholds ≥ 0 (when specified)
- **Threshold sum ≤ 1.0** (when both thresholds specified) - prevents overlapping shrinkage zones
- RebalanceQueueCapacity > 0 or null
- All validation occurs at construction time (fail-fast)

**Execution Context:**  
**Lives in: Background Thread** (invoked synchronously by RebalanceDecisionEngine within intent processing loop)

**Characteristics:**  
Pure functions, lightweight structs (value types), CPU-only, side-effect free

**Responsible for invariants:**
- 29. DesiredCacheRange computed from RequestedRange + config [ProportionalRangePlanner]
- 30. Independent of current cache contents [ProportionalRangePlanner]
- 31. Canonical target cache state [ProportionalRangePlanner]
- 32. Sliding window geometry defined by configuration [Both components]
- 33. NoRebalanceRange derived from current cache range + config [ThresholdRebalancePolicy]
- 35. Threshold sum constraint (leftThreshold + rightThreshold ≤ 1.0) [WindowCacheOptions validation]

**Responsibility Type:** sets rules and constraints

**Note:** Internally decomposed into two components that handle different aspects:
- **When to rebalance** (threshold rules) → NoRebalanceRangePlanner
- **What shape to target** (cache geometry) → ProportionalRangePlanner

---

## 4. IntentController (Intent & Concurrency Actor)

**Role:**  
Manages lifecycle of rebalance intents, orchestrates decision pipeline, and coordinates cancellation based on validation results.

**Implementation:**
This logical actor is internally decomposed into two components for separation of concerns:
- **IntentController** (Intent Controller) - owns DecisionEngine, intent lifecycle, cancellation coordination, decision invocation, background intent processing loop
- **IRebalanceExecutionController** (Execution Controller) - timing, debounce, background execution orchestration (owned by IntentController)

**Execution Context:**  
**Mixed:**
- **User Thread**: PublishIntent() only (atomic ops + signal, fire-and-forget)
- **Background Thread**: Intent processing loop, decision evaluation, cancellation, execution request enqueuing


**Ownership Hierarchy:**
```
IntentController (User Thread for PublishIntent; Background Thread for ProcessIntentsAsync)
├── owns DecisionEngine (invokes in ProcessIntentsAsync loop)
├── owns IRebalanceExecutionController (created in constructor)
│   └── owns RebalanceExecutor (passed to ExecutionController)
└── manages _pendingIntent snapshot (Interlocked.Exchange — latest-wins)
```

**Enhanced Role (Decision-Driven Model):**

Now responsible for:
- **Receiving intents** (when user request produces assembled data) [IntentController.PublishIntent - User Thread]
- **Owning and invoking DecisionEngine** [IntentController - Background Thread (intent processing loop), synchronous]
- **Intent identity and versioning** via ExecutionRequest snapshot [IntentController]
- **Cancellation coordination** based on validation results from owned DecisionEngine [IntentController - Background Thread]
- **Deduplication** via synchronous decision evaluation [IntentController - Background Thread (intent processing loop)]
- **Debouncing** [Execution Controller - Background]
- **Single-flight execution** enforcement [Both components via cancellation]
- **Starting background execution** [Execution Controller]
- **Orchestrating the validation-driven decision pipeline**: [IntentController - Background Thread (intent processing loop), synchronous]
  1. **IntentController.ProcessIntentsAsync()** invokes owned DecisionEngine synchronously (Background Thread)
  2. If ALL validation stages pass → cancel old pending, enqueue new execution request via ExecutionController
  3. If validation rejects → continue loop (work avoidance, no execution)
  4. **ExecutionController.PublishExecutionRequest()** enqueues to channel (processed by separate execution loop)
  5. **Background Task** performs debounce delay + ExecuteAsync (only this part is async)

**Authority:** *Owns DecisionEngine and invokes it synchronously. Owns time and concurrency, orchestrates validation-driven execution. Does NOT determine rebalance necessity (delegates to owned DecisionEngine).*

**Key Principle:** Cancellation is mechanical coordination (prevents concurrent executions), NOT a decision mechanism. The **DecisionEngine (owned by IntentController) is THE sole authority** for determining rebalance necessity. IntentController invokes it in the background intent processing loop (`ProcessIntentsAsync`), enabling work avoidance and preventing intent thrashing. This separation enables smart eventual consistency through work avoidance.

**Responsible for invariants:**
- 17. At most one active rebalance intent
- 18. Older intents may become logically superseded
- 19. Executions can be cancelled based on validation results
- 20. Obsolete intent must not start execution
- 21. At most one rebalance execution active
- 22. Execution reflects latest access pattern and validated necessity
- 23. System eventually stabilizes under load through work avoidance
- 24. Intent does not guarantee execution - execution is opportunistic and validation-driven

**Responsibility Type:** controls and coordinates intent execution based on validation results

**Note:** Internally decomposed into IntentController + RebalanceExecutionController,
but externally appears as a single unified actor.

---

## 5. Rebalance Executor (Single-Writer Actor)

**Role:**  
The **ONLY component** that mutates cache state (single-writer architecture). Performs mechanical cache normalization using delivered data from intent as authoritative source. **Intentionally simple**: no analytical decisions, assumes decision layer already validated necessity.

**Execution Context:**  
**Lives in: Background / ThreadPool**

**Single-Writer Guarantee:**
Rebalance Executor is the ONLY component that mutates:
- Cache data and range (via `Cache.Rematerialize()`)
- `LastRequested` field
- `NoRebalanceRange` field

This eliminates race conditions and ensures consistent cache state.

**Critical Principle:**
Executor is **mechanically simple** with no analytical logic:
- Does NOT validate rebalance necessity (DecisionEngine already validated)
- Does NOT check NoRebalanceRange (validation stage 1 already passed)
- Does NOT compute whether Desired == Current (validation stage 3 already passed)
- Assumes decision pipeline already confirmed necessity
- Performs only: fetch missing data, merge with delivered data, trim to desired range, write atomically

**Responsible for invariants:**
- 4. Rebalance is asynchronous relative to User Path
- 34. MUST support cancellation at all stages
- 34a. MUST yield to User Path requests immediately upon cancellation
- 34b. Partially executed or cancelled execution MUST NOT leave cache inconsistent
- 35. Only path responsible for cache normalization
- 35a. Mutates cache ONLY for normalization, using delivered data from intent:
  - Uses delivered data from intent as authoritative base (not current cache)
  - Expanding to DesiredCacheRange by fetching only truly missing ranges
  - Trimming excess data outside DesiredCacheRange
  - Writing to Cache.Rematerialize()
  - Writing to LastRequested
  - Recomputing NoRebalanceRange
- 36. May replace / expand / shrink cache to achieve normalization
- 37. Requests data only for missing subranges (not covered by delivered data)
- 38. Does not overwrite intersecting data
- 39. Upon completion: CacheData corresponds to DesiredCacheRange
- 40. Upon completion: CurrentCacheRange == DesiredCacheRange
- 41. Upon completion: NoRebalanceRange recomputed

**Responsibility Type:** executes rebalance and normalizes cache (cancellable, never concurrent with User Path, assumes validated necessity)

---

## 6. Cache State Manager (Consistency & Atomicity Actor)

**Role:**  
Ensures atomicity and internal consistency of cache state, coordinates cancellation between User Path and Rebalance Execution based on validation results.

**Responsible for invariants:**
- 11. CacheData and CurrentCacheRange are consistent
- 12. Changes applied atomically
- 13. No permanent inconsistent state
- 14. Temporary inefficiencies are acceptable
- 15. Partial / cancelled execution cannot break consistency
- 16. Only latest intent results may be applied
- 0a. Coordinates cancellation: User Request cancels ongoing/pending Rebalance ONLY when validation confirms new rebalance is necessary

**Responsibility Type:** guarantees state correctness and coordinates single-writer execution

---

## 🧠 Architectural Summary

- **User Path:** speed and availability
- **Decision Engine:** pure logic
- **IntentController:** temporal correctness and concurrency
- **Executor:** mutation
- **State Manager:** correctness and consistency
- **Geometry Policy:** deterministic cache shape

---

# Sliding Window Cache — Actors vs Scenarios Reference

This table maps **actors** to the scenarios they participate in and clarifies **read/write responsibilities**.

| Scenario                                | User Path                                                                                                               | Decision Engine                                  | Geometry Policy            | IntentController                         | Rebalance Executor                                                                     | Cache State Manager                                    | Notes                                      |
|-----------------------------------------|-------------------------------------------------------------------------------------------------------------------------|--------------------------------------------------|----------------------------|------------------------------------------|----------------------------------------------------------------------------------------|--------------------------------------------------------|--------------------------------------------|
| **U1 – Cold Cache**                     | Requests data from IDataSource, returns data to user, publishes rebalance intent                                        | –                                                | Computes DesiredCacheRange | Receives intent                          | Executes rebalance asynchronously (writes LastRequested, CurrentCacheRange, CacheData) | Validates atomic update of CacheData/CurrentCacheRange | User served directly                       |
| **U2 – Full Cache Hit (Exact)**         | Reads from cache, publishes rebalance intent                                                                            | Checks NoRebalanceRange                          | Computes DesiredCacheRange | Receives intent                          | Executes if rebalance required                                                         | Monitors consistency                                   | Minimal I/O                                |
| **U3 – Full Cache Hit (Shifted)**       | Reads subrange from cache, publishes rebalance intent                                                                   | Checks NoRebalanceRange                          | Computes DesiredCacheRange | Receives intent                          | Executes if rebalance required                                                         | Monitors consistency                                   | Cache hit but different LastRequestedRange |
| **U4 – Partial Cache Hit**              | Reads intersection, requests missing from IDataSource, merges locally, returns data to user, publishes rebalance intent | Checks NoRebalanceRange                          | Computes DesiredCacheRange | Receives intent                          | Executes merge and normalization                                                       | Ensures atomic merge & consistency                     | Temporary excess data allowed              |
| **U5 – Full Cache Miss (Jump)**         | Requests full range from IDataSource, returns data to user, publishes rebalance intent                                  | Checks NoRebalanceRange                          | Computes DesiredCacheRange | Receives intent                          | Executes full normalization                                                            | Ensures atomic replacement                             | No cached data usable                      |
| **D1 – NoRebalanceRange Block**         | –                                                                                                                       | Checks NoRebalanceRange, decides no execution    | –                          | Receives intent (blocked)                | –                                                                                      | –                                                      | Fast path skip                             |
| **D2 – Desired == Current**             | –                                                                                                                       | Computes DesiredCacheRange, decides no execution | Computes DesiredCacheRange | Receives intent (no-op)                  | –                                                                                      | –                                                      | No mutation required                       |
| **D3 – Rebalance Required**             | –                                                                                                                       | Computes DesiredCacheRange, confirms execution   | Computes DesiredCacheRange | Issues rebalance intent                  | Executes rebalance                                                                     | Ensures consistency                                    | Rebalance triggered asynchronously         |
| **R1 – Build from Scratch**             | –                                                                                                                       | –                                                | Defines DesiredCacheRange  | Receives intent                          | Requests full range, replaces cache                                                    | Atomic replacement                                     | Cache initialized from empty               |
| **R2 – Expand Cache (Partial Overlap)** | –                                                                                                                       | –                                                | Defines DesiredCacheRange  | Receives intent                          | Requests missing subranges, merges with existing cache                                 | Atomic merge, consistency                              | Cache partially reused                     |
| **R3 – Shrink / Normalize**             | –                                                                                                                       | –                                                | Defines DesiredCacheRange  | Receives intent                          | Trims cache to DesiredCacheRange                                                       | Atomic trim, consistency                               | Cache normalized to target                 |
| **C1 – Rebalance Trigger Pending**      | Executes normally                                                                                                       | –                                                | –                          | Debounces old intent, allows only latest | Cancels obsolete                                                                       | Ensures atomicity                                      | Fast user response guaranteed              |
| **C2 – Rebalance Executing**            | Executes normally                                                                                                       | –                                                | –                          | Marks latest intent                      | Cancels or discards obsolete execution                                                 | Ensures atomicity                                      | Latest execution wins                      |
| **C3 – Spike / Multiple Requests**      | Executes normally                                                                                                       | –                                                | –                          | Debounces & coordinates intents          | Executes only latest rebalance                                                         | Ensures atomicity                                      | Single-flight execution enforced           |