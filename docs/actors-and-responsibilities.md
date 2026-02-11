# Sliding Window Cache — System Actors & Invariant Ownership

This document maps **system actors** to the invariants they enforce or guarantee.

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
Every user access produces a rebalance intent containing delivered data.
The UserRequestHandler is READ-ONLY with respect to cache state.
The UserRequestHandler NEVER invokes directly decision logic - it just publishes an intent.
```

**Responsible for invariants:**
- -1. User Path and Rebalance Execution never write to cache concurrently
- 0. User Path has higher priority than rebalance execution
- 0a. Every User Request MUST cancel any ongoing or pending Rebalance Execution to prevent interference
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
Analyzes the need for rebalance and forms intents without mutating system state.

**Execution Context:**  
**Lives in: Background / ThreadPool**

**Visibility:**
- **Not visible to User Path**
- Invoked only by RebalanceIntentManager
- May execute many times, results may be discarded

**Critical Rule:**
```
DecisionEngine lives strictly inside the background contour.
```

**Responsible for invariants:**
- 24. Decision Path is purely analytical
- 25. Never mutates cache state
- 26. No rebalance if inside NoRebalanceRange
- 27. No rebalance if DesiredCacheRange == CurrentCacheRange
- 28. Rebalance triggered only if confirmed necessary

**Responsibility Type:** ensures correctness of decisions

**Note:** Not a top-level actor — internal tool of IntentManager/Executor pipeline.

---

## 3. Cache Geometry Policy (Configuration & Policy Actor)

**Role:**  
Defines canonical sliding window shape and rules.

**Implementation:**
This logical actor is internally decomposed into two components for separation of concerns:
- **ThresholdRebalancePolicy** - Computes NoRebalanceRange, checks threshold-based triggering
- **ProportionalRangePlanner** - Computes DesiredCacheRange, plans cache geometry

**Execution Context:**  
**Lives in: Background / ThreadPool** (invoked by RebalanceDecisionEngine)

**Responsible for invariants:**
- 29. DesiredCacheRange computed from RequestedRange + config [ProportionalRangePlanner]
- 30. Independent of current cache contents [ProportionalRangePlanner]
- 31. Canonical target cache state [ProportionalRangePlanner]
- 32. Sliding window geometry defined by configuration [Both components]
- 33. NoRebalanceRange derived from current cache range + config [ThresholdRebalancePolicy]

**Responsibility Type:** sets rules and constraints

**Note:** Internally decomposed into two components that handle different aspects:
- **When to rebalance** (threshold rules) → ThresholdRebalancePolicy
- **What shape to target** (cache geometry) → ProportionalRangePlanner

---

## 4. Rebalance Intent Manager (Intent & Concurrency Actor)

**Role:**  
Manages lifecycle of rebalance intents and prevents races and stale applications.

**Implementation:**
This logical actor is internally decomposed into two components for separation of concerns:
- **IntentController** (Intent Controller) - intent identity, lifecycle, cancellation
- **RebalanceScheduler** (Execution Scheduler) - timing, debounce, pipeline orchestration (stateless, plus DEBUG-only Task tracking for testing)

**Execution Context:**  
**Lives in: Background / ThreadPool**

**Enhanced Role (Corrected Model):**

Now responsible for:
- **Receiving intents** (on every user request) [Intent Controller]
- **Intent identity and versioning** [Intent Controller]
- **Cancellation** of obsolete intents [Intent Controller]
- **Deduplication** and debouncing [Execution Scheduler]
- **Single-flight execution** enforcement [Execution Scheduler]
- **Starting background tasks** [Execution Scheduler]
- **Orchestrating the decision pipeline**: [Execution Scheduler]
  1. Invoke DecisionEngine
  2. If allowed, invoke Executor
  3. Handle cancellation

**Authority:** *Owns time and concurrency.*

**Responsible for invariants:**
- 17. At most one active rebalance intent
- 18. Older intents become obsolete
- 19. Executions can be cancelled or ignored
- 20. Obsolete intent must not start execution
- 21. At most one rebalance execution active
- 22. Execution reflects latest access pattern
- 23. System eventually stabilizes under load
- 24. Intent does not guarantee execution - execution is opportunistic

**Responsibility Type:** controls and coordinates intent execution

**Note:** Internally decomposed into Intent Controller + Execution Scheduler,
but externally appears as a single unified actor.

---

## 5. Rebalance Executor (Single-Writer Actor)

**Role:**  
The **ONLY component** that mutates cache state (single-writer architecture). Responsible for cache normalization using delivered data from intent as authoritative source.

**Execution Context:**  
**Lives in: Background / ThreadPool**

**Single-Writer Guarantee:**
Rebalance Executor is the ONLY component that mutates:
- Cache data and range (via `Cache.Rematerialize()`)
- `LastRequested` field
- `NoRebalanceRange` field

This eliminates race conditions and ensures consistent cache state.

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

**Responsibility Type:** executes rebalance and normalizes cache (cancellable, never concurrent with User Path)

---

## 6. Cache State Manager (Consistency & Atomicity Actor)

**Role:**  
Ensures atomicity and internal consistency of cache state, coordinates cancellation between User Path and Rebalance Execution.

**Responsible for invariants:**
- 11. CacheData and CurrentCacheRange are consistent
- 12. Changes applied atomically
- 13. No permanent inconsistent state
- 14. Temporary inefficiencies are acceptable
- 15. Partial / cancelled execution cannot break consistency
- 16. Only latest intent results may be applied
- 0a. Coordinates cancellation: User Request cancels ongoing/pending Rebalance before mutation

**Responsibility Type:** guarantees state correctness and mutual exclusion

---

## 🧠 Architectural Summary

- **User Path:** speed and availability
- **Decision Engine:** pure logic
- **Intent Manager:** temporal correctness and concurrency
- **Executor:** mutation
- **State Manager:** correctness and consistency
- **Geometry Policy:** deterministic cache shape

---

# Sliding Window Cache — Actors vs Scenarios Reference

This table maps **actors** to the scenarios they participate in and clarifies **read/write responsibilities**.

| Scenario                                | User Path                                                                                                                  | Decision Engine                                  | Geometry Policy            | Intent Manager                           | Rebalance Executor                                     | Cache State Manager                                    | Notes                                      |
|-----------------------------------------|----------------------------------------------------------------------------------------------------------------------------|--------------------------------------------------|----------------------------|------------------------------------------|--------------------------------------------------------|--------------------------------------------------------|--------------------------------------------|
| **U1 – Cold Cache**                     | Requests data from IDataSource, updates LastRequestedRange & CurrentCacheRange, triggers rebalance                         | –                                                | Computes DesiredCacheRange | Receives intent                          | Executes rebalance asynchronously                      | Validates atomic update of CacheData/CurrentCacheRange | User served directly                       |
| **U2 – Full Cache Hit (Exact)**         | Reads from cache, updates LastRequestedRange, triggers rebalance                                                           | Checks NoRebalanceRange                          | Computes DesiredCacheRange | Receives intent                          | Executes if rebalance required                         | Monitors consistency                                   | Minimal I/O                                |
| **U3 – Full Cache Hit (Shifted)**       | Reads subrange from cache, updates LastRequestedRange, triggers rebalance                                                  | Checks NoRebalanceRange                          | Computes DesiredCacheRange | Receives intent                          | Executes if rebalance required                         | Monitors consistency                                   | Cache hit but different LastRequestedRange |
| **U4 – Partial Cache Hit**              | Reads intersection, requests missing from IDataSource, merges, updates LastRequestedRange, triggers rebalance              | Checks NoRebalanceRange                          | Computes DesiredCacheRange | Receives intent                          | Executes merge and normalization                       | Ensures atomic merge & consistency                     | Temporary excess data allowed              |
| **U5 – Full Cache Miss (Jump)**         | Requests full range from IDataSource, replaces CacheData/CurrentCacheRange, updates LastRequestedRange, triggers rebalance | Checks NoRebalanceRange                          | Computes DesiredCacheRange | Receives intent                          | Executes full normalization                            | Ensures atomic replacement                             | No cached data usable                      |
| **D1 – NoRebalanceRange Block**         | –                                                                                                                          | Checks NoRebalanceRange, decides no execution    | –                          | Receives intent (blocked)                | –                                                      | –                                                      | Fast path skip                             |
| **D2 – Desired == Current**             | –                                                                                                                          | Computes DesiredCacheRange, decides no execution | Computes DesiredCacheRange | Receives intent (no-op)                  | –                                                      | –                                                      | No mutation required                       |
| **D3 – Rebalance Required**             | –                                                                                                                          | Computes DesiredCacheRange, confirms execution   | Computes DesiredCacheRange | Issues rebalance intent                  | Executes rebalance                                     | Ensures consistency                                    | Rebalance triggered asynchronously         |
| **R1 – Build from Scratch**             | –                                                                                                                          | –                                                | Defines DesiredCacheRange  | Receives intent                          | Requests full range, replaces cache                    | Atomic replacement                                     | Cache initialized from empty               |
| **R2 – Expand Cache (Partial Overlap)** | –                                                                                                                          | –                                                | Defines DesiredCacheRange  | Receives intent                          | Requests missing subranges, merges with existing cache | Atomic merge, consistency                              | Cache partially reused                     |
| **R3 – Shrink / Normalize**             | –                                                                                                                          | –                                                | Defines DesiredCacheRange  | Receives intent                          | Trims cache to DesiredCacheRange                       | Atomic trim, consistency                               | Cache normalized to target                 |
| **C1 – Rebalance Trigger Pending**      | Executes normally                                                                                                          | –                                                | –                          | Debounces old intent, allows only latest | Cancels obsolete                                       | Ensures atomicity                                      | Fast user response guaranteed              |
| **C2 – Rebalance Executing**            | Executes normally                                                                                                          | –                                                | –                          | Marks latest intent                      | Cancels or discards obsolete execution                 | Ensures atomicity                                      | Latest execution wins                      |
| **C3 – Spike / Multiple Requests**      | Executes normally                                                                                                          | –                                                | –                          | Debounces & coordinates intents          | Executes only latest rebalance                         | Ensures atomicity                                      | Single-flight execution enforced           |