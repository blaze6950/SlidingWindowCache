# Sliding Window Cache — System Invariants

---

## Understanding This Document

This document lists **49 system invariants** that define the behavior, architecture, and design intent of the Sliding Window Cache.

### Invariant Categories

Invariants are classified into three categories based on their **nature** and **enforcement mechanism**:

#### 🟢 Behavioral Invariants
- **Nature**: Externally observable behavior via public API
- **Enforcement**: Automated tests (unit, integration)
- **Verification**: Can be tested through public API without inspecting internal state
- **Examples**: User request behavior, returned data correctness, cancellation effects

#### 🔵 Architectural Invariants  
- **Nature**: Internal structural constraints enforced by code organization
- **Enforcement**: Component boundaries, encapsulation, ownership model
- **Verification**: Code review, type system, access modifiers
- **Examples**: Atomicity of state updates, component responsibilities, separation of concerns
- **Note**: NOT directly testable via public API (would require white-box testing or test hooks)

#### 🟡 Conceptual Invariants
- **Nature**: Design intent, guarantees, or explicit non-guarantees
- **Enforcement**: Documentation and architectural discipline
- **Verification**: Design reviews, documentation
- **Examples**: "Intent does not guarantee execution", opportunistic behavior, allowed inefficiencies
- **Note**: Guide future development; NOT meant to be tested directly

### Important Meta-Point: Invariants ≠ Test Coverage

**By design, this document contains MORE invariants than the test suite covers.**

This is intentional and correct:
- ✅ **Behavioral invariants** → Covered by automated tests
- ✅ **Architectural invariants** → Enforced by code structure, not tests
- ✅ **Conceptual invariants** → Documented design decisions, not test cases

**Full invariant documentation does NOT imply full test coverage.**  
Different invariant types are enforced at different levels:
- Tests verify externally observable behavior
- Architecture enforces internal structure
- Documentation guides design decisions

Attempting to test architectural or conceptual invariants would require:
- Invasive test hooks or reflection (anti-pattern)
- White-box testing of implementation details (brittle)
- Testing things that are enforced by the type system or compiler

**This separation is a feature, not a gap.**

---

## Testing Infrastructure: Deterministic Synchronization

### Background

Tests verify behavioral invariants through the public API using instrumentation counters
(DEBUG-only) to observe internal state changes. However, tests also need to **synchronize** with background
rebalance operations to ensure cache has converged before making assertions.

### Synchronization Mechanism: `WaitForIdleAsync()`

The cache exposes a public `WaitForIdleAsync()` method for deterministic synchronization with
background rebalance execution:

- **Purpose**: Infrastructure/testing API (not part of domain semantics)
- **Mechanism**: Lock-free idle detection using `AsyncActivityCounter`
- **Guarantee**: Completes when system **was idle at some point** (eventual consistency semantics)
- **Safety**: Fully thread-safe, supports multiple concurrent awaiters

### Implementation Strategy

**AsyncActivityCounter Architecture:**
- Tracks active operations using atomic counter (`Interlocked.Increment/Decrement`)
- Signals idle state via `TaskCompletionSource` (state-based, not event-based)
- Uses `Volatile.Write/Read` for lock-free TCS reference coordination
- Provides "was idle" semantics (not "is idle now")

**WaitForIdleAsync() Workflow:**
1. Snapshot current TaskCompletionSource via `Volatile.Read` (acquire fence)
2. Await the TCS task (completes when counter reached 0 at snapshot time)
3. Return immediately if already completed, or wait for completion

**Idle State Semantics - "Was Idle" NOT "Is Idle":**

WaitForIdleAsync completes when the system **was idle at some point in time**. 
It does NOT guarantee the system is still idle after completion (new activity may start immediately).

Example race (correct behavior):
1. Background thread decrements counter to 0, signals TCS_old
2. New intent arrives, increments counter to 1, creates TCS_new
3. Test calls WaitForIdleAsync, reads TCS_old (already completed)
4. Result: Method returns immediately even though system is now busy

This is **correct behavior** for eventual consistency testing - system WAS idle between steps 1 and 2.
Tests requiring stronger guarantees should implement retry logic or re-check state after await.

**Typical Test Pattern:**

```csharp
// Trigger operation that schedules rebalance
await cache.GetDataAsync(newRange);

// Wait for system to stabilize
await cache.WaitForIdleAsync();

// At this point, system WAS idle (cache converged to consistent state)
// Assert on converged state
Assert.Equal(expectedRange, cache.CurrentCacheRange);
```

### Architectural Boundaries

This synchronization mechanism **does not alter actor responsibilities**:

- ✅ UserRequestHandler remains the ONLY publisher of rebalance intents
- ✅ IntentController remains the lifecycle authority for intent cancellation
- ✅ `IRebalanceExecutionController` remains the authority for background Task execution
- ✅ WindowCache remains a composition root with no business logic

The method exists solely to expose idle synchronization through the public API for testing,
maintaining architectural separation.

### Relation to Instrumentation Counters

Instrumentation counters track **events** (intent published, execution started, etc.) but are
not used for synchronization. AsyncActivityCounter provides deterministic, race-free idle detection
without polling or timing dependencies.

**Old approach (removed):**
- Counter-based polling with stability windows
- Timing-dependent with configurable intervals
- Complex lifecycle calculation

**Current approach:**
- Lock-free activity tracking via AsyncActivityCounter
- State-based completion via TaskCompletionSource
- Deterministic "was idle" semantics (eventual consistency)
- No timing assumptions, no polling

---

## A. User Path & Fast User Access Invariants

### A.1 Concurrency & Priority

**A.-1** 🔵 **[Architectural]** The User Path and Rebalance Execution **never write to cache concurrently**.
- *Enforced by*: Single-writer architecture - User Path is read-only, only Rebalance Execution writes
- *Architecture*: User Path never mutates cache state; Rebalance Execution is sole writer

**A.0** 🔵 **[Architectural]** The User Path **always has higher priority** than Rebalance Execution.
- *Enforced by*: Component ownership, cancellation protocol
- *Architecture*: User Path cancels rebalance; rebalance checks cancellation

**A.0a** 🟢 **[Behavioral — Test: `Invariant_A_0a_UserRequestCancelsRebalance`]** A User Request **MAY cancel** an ongoing or pending Rebalance Execution **ONLY when a new rebalance is validated as necessary** by the multi-stage decision pipeline.
- *Observable via*: DEBUG instrumentation counters tracking cancellation
- *Test verifies*: Cancellation counter increments when new request arrives and rebalance validation requires rescheduling
- *Clarification*: Cancellation is a mechanical coordination tool (single-writer architecture), not a decision mechanism. Rebalance necessity is determined by the Rebalance Decision Engine through analytical validation (NoRebalanceRange containment, DesiredRange vs CurrentRange comparison). User requests do NOT automatically trigger cancellation; validated rebalance necessity triggers cancellation + rescheduling.
- *Note*: Cancellation prevents concurrent rebalance executions, not duplicate decision-making
- *Implementation*: Uses `Interlocked.Exchange` on `_pendingIntent` for atomic read-and-clear, ensuring only the latest intent is processed and prior intents are superseded atomically

### A.2 User-Facing Guarantees

**A.1** 🟢 **[Behavioral — Test: `Invariant_A2_1_UserPathAlwaysServesRequests`]** The User Path **always serves user requests** regardless of the state of rebalance execution.
- *Observable via*: Public API always returns data successfully
- *Test verifies*: Multiple requests all complete and return correct data

**A.2** 🟢 **[Behavioral — Test: `Invariant_A2_2_UserPathNeverWaitsForRebalance`]** The User Path **never waits for rebalance execution** to complete.
- *Observable via*: Request completion time vs. debounce delay
- *Test verifies*: Request completes in <500ms with 1-second debounce

**A.3** 🔵 **[Architectural]** The User Path is the **sole source of rebalance intent**.
- *Enforced by*: Only `UserRequestHandler` calls `IntentController.PublishIntent()`
- *Architecture*: Encapsulation prevents other components from publishing intents

**A.4** 🔵 **[Architectural]** Rebalance execution is **always performed asynchronously** relative to the User Path.
- *Enforced by*: Background task scheduling in `IRebalanceExecutionController`, fire-and-forget pattern
- *Architecture*: User Path returns immediately after publishing intent

**A.5** 🔵 **[Architectural]** The User Path performs **only the work necessary to return data to the user**.
- *Enforced by*: Responsibility assignment, component boundaries
- *Architecture*: `UserRequestHandler` doesn't normalize/trim cache

**A.6** 🟡 **[Conceptual]** The User Path may synchronously request data from `IDataSource` in the user execution context if needed to serve `RequestedRange`.
- *Design decision*: Prioritizes user-facing latency over background work
- *Rationale*: User must get data immediately; background prefetch is opportunistic

**A.10** 🟢 **[Behavioral — Test: `Invariant_A2_10_UserAlwaysReceivesExactRequestedRange`]** The User always receives data **exactly corresponding to `RequestedRange`**.
- *Observable via*: Returned data length and content
- *Test verifies*: Data matches requested range exactly (no more, no less)

### A.3 Cache Mutation Rules (User Path)

**A.7** 🔵 **[Architectural]** The User Path may read from cache and `IDataSource` but **does not mutate cache state**.
- *Enforced by*: Component responsibilities, read-only architecture
- *Architecture*: User Path has no write access to cache, LastRequested, or NoRebalanceRange

**A.8** 🔵 **[Architectural — Tests: `Invariant_A3_8_ColdStart`, `_CacheExpansion`, `_FullCacheReplacement`]** The User Path **MUST NOT mutate cache under any circumstance**.
   - User Path is **read-only** with respect to cache state
   - User Path **NEVER** calls `Cache.Rematerialize()`
   - User Path **NEVER** writes to `LastRequested`
   - User Path **NEVER** writes to `NoRebalanceRange`
   - All cache mutations are performed exclusively by Rebalance Execution (single-writer)
- *Observable via*: Instrumentation counters (`CacheExpanded`, `CacheReplaced`) track when CacheDataExtensionService analyzes extension needs
- *Test verifies*: User Path returns correct data without mutating cache; Rebalance Execution populates cache
- *Note*: `CacheExpanded/Replaced` counters are incremented by shared service (`CacheDataExtensionService`) used by both paths during range analysis, not mutation. Tests verify User Path doesn't trigger these counters in specific scenarios where prior rebalance has already expanded cache sufficiently.

**A.9** 🔵 **[Architectural]** Cache mutations are performed **exclusively by Rebalance Execution** (single-writer architecture).
- *Enforced by*: Component encapsulation, internal setters on CacheState
- *Architecture*: Only `RebalanceExecutor` has write access to cache state

**A.9a** 🟢 **[Behavioral — Test: `Invariant_A3_9a_CacheContiguityMaintained`]** **Cache Contiguity Rule:** `CacheData` **MUST always remain contiguous** — gapped or partially materialized cache states are invalid.
- *Observable via*: All requests return valid contiguous data
- *Test verifies*: Sequential overlapping requests all succeed

---

## B. Cache State & Consistency Invariants

**B.11** 🟢 **[Behavioral — Test: `Invariant_B11_CacheDataAndRangeAlwaysConsistent`]** `CacheData` and `CurrentCacheRange` are **always consistent** with each other.
- *Observable via*: Data length always matches range size
- *Test verifies*: For any request, returned data length matches expected range size

**B.12** 🔵 **[Architectural]** Changes to `CacheData` and the corresponding `CurrentCacheRange` are performed **atomically**.
- *Enforced by*: `Rematerialize()` performs atomic swap (staging buffer pattern)
- *Architecture*: Tuple swap `(_activeStorage, _stagingBuffer) = (_stagingBuffer, _activeStorage)` is atomic

**B.13** 🔵 **[Architectural]** The system **never enters a permanently inconsistent state** with respect to `CacheData ↔ CurrentCacheRange`.
- *Enforced by*: Atomic operations, cancellation checks before mutations
- *Architecture*: `ThrowIfCancellationRequested()` prevents applying obsolete results

**B.14** 🟡 **[Conceptual]** Temporary geometric or coverage inefficiencies in the cache are acceptable **if they can be resolved by rebalance execution**.
- *Design decision*: User Path prioritizes speed over optimal cache shape
- *Rationale*: Background rebalance will normalize; temporary inefficiency is acceptable

**B.15** 🟢 **[Behavioral — Test: `Invariant_B15_CancelledRebalanceDoesNotViolateConsistency`]** Partially executed or cancelled rebalance execution **cannot violate `CacheData ↔ CurrentCacheRange` consistency**.
- *Observable via*: Cache continues serving valid data after cancellation
- *Test verifies*: Rapid request changes don't corrupt cache

**B.16** 🔵 **[Architectural]** Results from rebalance execution are applied **only if they correspond to the latest active rebalance intent**.
- *Enforced by*: Cancellation token identity, checks before `Rematerialize()`
- *Architecture*: `ThrowIfCancellationRequested()` before applying changes

---

## C. Rebalance Intent & Temporal Invariants

**C.17** 🔵 **[Architectural]** At most one rebalance intent may be active at any time.
- *Enforced by*: Single-writer architecture, cancellation coordination in IntentController
- *Architecture*: IntentController cancels previous pending rebalance before scheduling new one
- *Note*: This is a structural constraint enforced by component design, not a behavioral guarantee testable via public API

**C.18** 🟡 **[Conceptual]** Previously created intents may become **logically superseded** when a new intent is published, but rebalance execution relevance is determined by the **multi-stage rebalance validation logic**.
- *Design intent*: Obsolescence ≠ cancellation; obsolescence ≠ guaranteed execution prevention
- *Clarification*: Intents are access signals, not commands. An intent represents "user accessed this range," not "must execute rebalance." Execution decisions are governed by the Rebalance Decision Engine's analytical validation (Stage 1: Current Cache NoRebalanceRange check, Stage 2: Pending Desired Cache NoRebalanceRange check if applicable, Stage 3: DesiredCacheRange vs CurrentCacheRange equality check). Previously created intents may be superseded or cancelled, but the decision to execute is always based on current validation state, not intent age. Cancellation occurs ONLY when Decision Engine validation confirms a new rebalance is necessary.

**C.19** 🔵 **[Architectural]** Any rebalance execution can be **cancelled or have its results ignored**.
- *Enforced by*: `CancellationToken` passed through execution pipeline
- *Architecture*: All async operations check cancellation token

**C.20** 🔵 **[Architectural]** If a rebalance intent becomes obsolete before execution begins, the execution **must not start**.
- *Enforced by*: `IsCancellationRequested` check after debounce
- *Architecture*: Early exit in `IntentController.ProcessIntentsAsync` (cancellation check after debounce)

**C.21** 🔵 **[Architectural]** At any point in time, **at most one rebalance execution is active**.
- *Enforced by*: Cancellation protocol, single intent identity
- *Architecture*: New intent cancels old execution via token

**C.22** 🟡 **[Conceptual]** The results of rebalance execution **always reflect the latest user access pattern**.
- *Design guarantee*: Obsolete results are discarded
- *Rationale*: System converges to user's actual navigation pattern

**C.23** 🟢 **[Behavioral — Test: `Invariant_C23_SystemStabilizesUnderLoad`]** During spikes of user requests, the system **eventually stabilizes** to a consistent cache state.
- *Observable via*: After burst of requests, system serves data correctly
- *Test verifies*: Rapid burst + wait → final request succeeds

**C.24** 🟡 **[Conceptual — Test: `Invariant_C24_IntentDoesNotGuaranteeExecution`]** **Intent does not guarantee execution. Execution is opportunistic and may be skipped entirely.**
   - Publishing an intent does NOT guarantee that rebalance will execute
   - Execution may be cancelled before starting (due to new intent)
   - Execution may be cancelled during execution (User Path priority)
   - Execution may be skipped by DecisionEngine (NoRebalanceRange, DesiredRange == CurrentRange)
   - This is by design: intent represents "user accessed this range", not "must rebalance"
- *Design decision*: Rebalance is opportunistic, not mandatory
- *Test note*: Test verifies skip behavior exists, but non-execution is acceptable

**C.24e** 🔵 **[Architectural]** Intent **MUST contain delivered data** (`RangeData<TRange,TData,TDomain>`) representing what was actually returned to the user for the requested range.
- *Enforced by*: `PublishIntent()` signature requires `deliveredData` parameter
- *Architecture*: User Path materializes data once and passes to both user and intent

**C.24f** 🟡 **[Conceptual]** Delivered data in intent serves as the **authoritative source** for Rebalance Execution, avoiding duplicate fetches and ensuring consistency with user view.
- *Design guarantee*: Rebalance Execution uses delivered data as base, not current cache
- *Rationale*: Eliminates redundant IDataSource calls, ensures cache converges to what user received

---

## D. Rebalance Decision Path Invariants

### D.0 Rebalance Decision Model Overview

The system uses a **multi-stage rebalance decision pipeline**, not a cancellation policy. Rebalance necessity is determined in the background intent processing loop via CPU-only analytical validation performed by the Rebalance Decision Engine.

#### Key Conceptual Distinctions

**Rebalance Decision vs Cancellation:**
- **Rebalance Decision** = Analytical validation determining if rebalance is necessary (decision mechanism)
- **Cancellation** = Mechanical coordination tool ensuring single-writer architecture (coordination mechanism)
- Cancellation is NOT a decision mechanism; it prevents concurrent executions, not duplicate decision-making

**Intent Semantics:**
- Intent represents **observed access**, not mandatory work
- Intent = "user accessed this range" (signal), NOT "must execute rebalance" (command)
- Rebalance may be skipped because:
  - NoRebalanceRange containment (Stage 1 validation)
  - Pending rebalance already covers range (Stage 2 validation, anti-thrashing)
  - Desired == Current range (Stage 4 validation)
  - Intent superseded or cancelled before execution begins

#### Multi-Stage Decision Pipeline

The Rebalance Decision Engine validates rebalance necessity through three sequential stages:

**Stage 1 — Current Cache NoRebalanceRange Validation**
- **Purpose**: Fast-path check against current cache state
- **Logic**: If RequestedRange ⊆ NoRebalanceRange(CurrentCacheRange), skip rebalance
- **Rationale**: Current cache already provides sufficient buffer around request
- **Performance**: O(1) range containment check, no computation needed

**Stage 2 — Pending Desired Cache NoRebalanceRange Validation** (if pending execution exists)
- **Purpose**: Anti-thrashing mechanism preventing oscillation
- **Logic**: If RequestedRange ⊆ NoRebalanceRange(PendingDesiredCacheRange), skip rebalance
- **Rationale**: Pending rebalance execution will satisfy this request when it completes
- **Implementation**: Checks `lastExecutionRequest?.DesiredNoRebalanceRange` — fully implemented

**Stage 3 — Compute DesiredCacheRange**
- **Purpose**: Determine the optimal cache range for the current request
- **Logic**: Use `ProportionalRangePlanner` to compute `DesiredCacheRange` from `RequestedRange` + configuration
- **Performance**: Pure CPU computation, no I/O

**Stage 4 — DesiredCacheRange vs CurrentCacheRange Equality Check**
- **Purpose**: Avoid no-op rebalance operations
- **Logic**: If `DesiredCacheRange == CurrentCacheRange`, skip rebalance
- **Rationale**: Cache is already in optimal configuration for this request
- **Performance**: Requires computing desired range but avoids I/O

#### Decision Authority

- **Rebalance Decision Engine** = Sole authority for rebalance necessity determination
- **User Path** = Read-only with respect to cache state; publishes intents with delivered data
- **Cancellation** = Coordination tool for single-writer architecture, NOT decision mechanism
- **Rebalance Execution** = Mechanically simple; assumes decision layer already validated necessity

#### System Stability Principle

The system prioritizes **decision correctness and work avoidance** over aggressive rebalance responsiveness.

**Meaning:**
- Avoid thrashing (redundant rebalance operations)
- Avoid redundant I/O (fetching data already in cache or pending)
- Avoid oscillating cache geometry (constantly resizing based on rapid access pattern changes)
- Accept temporary cache inefficiency if background rebalance will correct it

**Trade-off:** Slight delay in cache optimization vs. system stability and resource efficiency

**D.25** 🔵 **[Architectural]** The Rebalance Decision Path is **purely analytical** and has **no side effects**.
- *Enforced by*: `RebalanceDecisionEngine` is stateless, uses value types
- *Architecture*: Pure function: inputs → decision (no I/O, no mutations)

**D.26** 🔵 **[Architectural]** The Decision Path **never mutates cache state**.
- *Enforced by*: No write access to `CacheState` in decision components
- *Architecture*: Decision components don't have reference to mutable cache

**D.27** 🟢 **[Behavioral — Test: `Invariant_D27_NoRebalanceIfRequestInNoRebalanceRange`]** If `RequestedRange` is fully contained within `NoRebalanceRange`, **rebalance execution is prohibited**.
- *Observable via*: DEBUG counters showing execution skipped (policy-based, see C.24b)
- *Test verifies*: Request within NoRebalanceRange doesn't trigger execution

**D.28** 🟢 **[Behavioral — Test: `Invariant_D28_SkipWhenDesiredEqualsCurrentRange`]** If `DesiredCacheRange == CurrentCacheRange`, **rebalance execution is not required**.
- *Observable via*: DEBUG counter `RebalanceSkippedSameRange` (optimization-based, see C.24c)
- *Test verifies*: Repeated request with same range increments skip counter
- *Implementation*: Early exit in `RebalanceDecisionEngine.Evaluate` (Stage 4) before execution is scheduled

**D.29** 🔵 **[Architectural]** Rebalance execution is triggered **only if ALL stages of the multi-stage decision pipeline confirm necessity**.
- *Enforced by*: `IntentController.ProcessIntentsAsync` checks `RebalanceDecisionEngine` result before calling executor
- *Architecture*: Decision result gates execution
- *Decision Pipeline Stages*:
  1. **Stage 1 — Current Cache NoRebalanceRange Validation**: If RequestedRange is contained within the NoRebalanceRange computed from CurrentCacheRange, skip rebalance (fast path)
  2. **Stage 2 — Pending Desired Cache NoRebalanceRange Validation** (if pending execution exists): Validate against the NoRebalanceRange computed from the pending DesiredCacheRange to prevent thrashing/oscillation
  3. **Stage 3 — Compute DesiredCacheRange**: Determine optimal cache range from RequestedRange + configuration
  4. **Stage 4 — DesiredCacheRange vs CurrentCacheRange Equality Check**: If computed DesiredCacheRange equals CurrentCacheRange, skip rebalance (no change needed)
  5. **Stage 5 — Schedule Execution**: All stages passed; schedule rebalance execution
- *Critical Principle*: Rebalance executes ONLY if ALL stages pass validation. This multi-stage approach prevents unnecessary I/O, cache thrashing, and oscillating cache geometry while ensuring the system converges to optimal configuration.

---

## E. Cache Geometry & Policy Invariants

**E.30** 🟢 **[Behavioral — Test: `Invariant_E30_DesiredRangeComputedFromConfigAndRequest`]** `DesiredCacheRange` is computed **solely from `RequestedRange` and cache configuration**.
- *Observable via*: After rebalance, cache covers expected expanded range
- *Test verifies*: With config (leftSize=1.0, rightSize=1.0), cache expands as expected

**E.31** 🔵 **[Architectural]** `DesiredCacheRange` is **independent of the current cache contents**, but may use configuration and `RequestedRange`.
- *Enforced by*: `ProportionalRangePlanner.Plan()` doesn't access current cache
- *Architecture*: Pure function using only config + requested range

**E.32** 🟡 **[Conceptual]** `DesiredCacheRange` represents the **canonical target state** towards which the system converges.
- *Design concept*: Single source of truth for "what cache should be"
- *Rationale*: Ensures deterministic convergence behavior

**E.33** 🟡 **[Conceptual]** The geometry of the sliding window is **determined by configuration**, not by scenario-specific logic.
- *Design principle*: Configuration drives behavior, not hard-coded heuristics
- *Rationale*: Predictable, user-controllable cache shape

**E.34** 🔵 **[Architectural]** `NoRebalanceRange` is derived **from `CurrentCacheRange` and configuration**.
- *Enforced by*: `ThresholdRebalancePolicy.GetNoRebalanceRange()` implementation
- *Architecture*: Shrinks current range by threshold ratios

---

## F. Rebalance Execution Invariants

### F.1 Execution Control & Cancellation

**F.35** 🟢 **[Behavioral — Test: `Invariant_F35_G46_RebalanceCancellationBehavior`]** Rebalance Execution **MUST be cancellation-safe** at all stages (before I/O, during I/O, before mutations).
- *Observable via*: Lifecycle tracking integrity (Started == Completed + Cancelled), system stability under concurrent requests
- *Test verifies*: 
  - Deterministic termination: Every started execution reaches terminal state
  - No partial mutations: Cache consistency maintained after cancellation
  - Lifecycle integrity: Accounting remains correct under cancellation
- *Implementation details*: `ThrowIfCancellationRequested()` at multiple checkpoints in execution pipeline
- *Note*: Cancellation is triggered by scheduling decisions (Decision Engine validation), not automatically by user requests
- *Related*: C.24d (execution skipped due to cancellation), A.0a (User Path priority via validation-driven cancellation), G.46 (high-level guarantee)

**F.35a** 🔵 **[Architectural]** Rebalance Execution **MUST yield** to User Path requests immediately upon cancellation.
- *Enforced by*: `ThrowIfCancellationRequested()` at multiple checkpoints
- *Architecture*: Cancellation checks before/after I/O, before mutations

**F.35b** 🟢 **[Behavioral — Covered by `Invariant_B15`]** Partially executed or cancelled Rebalance Execution **MUST NOT leave cache in inconsistent state**.
- *Observable via*: Cache continues serving valid data after cancellation
- *Same test as B.15*

### F.2 Cache Mutation Rules (Rebalance Execution)

**F.36** 🔵 **[Architectural]** The Rebalance Execution Path is the **ONLY component that mutates cache state** (single-writer architecture).
- *Enforced by*: Component encapsulation, internal setters on CacheState
- *Architecture*: Only `RebalanceExecutor` writes to Cache, LastRequested, NoRebalanceRange

**F.36a** 🟢 **[Behavioral — Test: `Invariant_F36a_RebalanceNormalizesCache`]** Rebalance Execution mutates cache for normalization using **delivered data from intent as authoritative base**:
   - **Uses delivered data** from intent (not current cache) as starting point
   - **Expanding to DesiredCacheRange** by fetching only truly missing ranges
   - **Trimming excess data** outside `DesiredCacheRange`
   - **Writing to cache** via `Cache.Rematerialize()`
   - **Writing to LastRequested** with original requested range
   - **Recomputing NoRebalanceRange** based on final cache range
- *Observable via*: After rebalance, cache serves data from expanded range
- *Test verifies*: Cache covers larger area after rebalance completes
- *Single-writer guarantee*: These are the ONLY mutations in the system

**F.37** 🔵 **[Architectural]** Rebalance Execution may **replace, expand, or shrink cache data** to achieve normalization.
- *Enforced by*: `RebalanceExecutor` has full mutation capability
- *Architecture*: Can call `Rematerialize()` with any range

**F.38** 🔵 **[Architectural]** Rebalance Execution requests data from `IDataSource` **only for missing subranges**.
- *Enforced by*: `CacheDataExtensionService.ExtendCacheAsync()` calculates missing ranges
- *Architecture*: Union logic preserves existing data

**F.39** 🔵 **[Architectural]** Rebalance Execution **does not overwrite existing data** that intersects with `DesiredCacheRange`.
- *Enforced by*: `ExtendCacheAsync()` unions new data with existing
- *Architecture*: Staging buffer pattern preserves active storage during enumeration

### F.3 Post-Execution Guarantees

**F.40** 🟢 **[Behavioral — Test: `Invariant_F40_F41_F42_PostExecutionGuarantees`]** Upon successful completion, `CacheData` **strictly corresponds to `DesiredCacheRange`**.
- *Observable via*: After rebalance, cache serves data from expected normalized range
- *Test verifies*: Can read from expected expanded range

**F.41** 🟢 **[Behavioral — Covered by same test as F.40]** Upon successful completion, `CurrentCacheRange == DesiredCacheRange`.
- *Observable indirectly*: Cache behavior matches expected range
- *Same test as F.40*

**F.42** 🟡 **[Conceptual — Covered by same test as F.40]** Upon successful completion, `NoRebalanceRange` is **recomputed**.
- *Internal state*: Not directly observable via public API
- *Design guarantee*: Threshold zone updated after normalization

---

## G. Execution Context & Scheduling Invariants

**G.43** 🟢 **[Behavioral — Test: `Invariant_G43_G44_G45_ExecutionContextSeparation`]** The User Path operates in the **user execution context**.
- *Observable via*: Request completes quickly without waiting for background work
- *Test verifies*: Request time < debounce delay

**G.44** 🔵 **[Architectural — Covered by same test as G.43]** Rebalance Decision Path and Rebalance Execution Path execute **outside the user execution context**.
- *Enforced by*: `Task.Run()` executes in ThreadPool
- *Architecture*: Fire-and-forget pattern, async execution

**G.45** 🔵 **[Architectural — Covered by same test as G.43]** Rebalance Execution Path performs I/O **only in a background execution context**.
- *Enforced by*: `ExecuteAsync` runs in ThreadPool thread
- *Architecture*: User Path returns before background I/O starts

**G.46** 🟢 **[Behavioral — Tests: `Invariant_G46_UserCancellationDuringFetch`, `Invariant_G46_RebalanceCancellation`]** Cancellation **must be supported** for all scenarios:
1. **User-facing cancellation**: User-provided CancellationToken propagates through User Path to IDataSource.FetchAsync()
2. **Background rebalance cancellation**: System supports cancellation of pending/ongoing rebalance execution
- *Observable via*: 
  - User cancellation: OperationCanceledException thrown during IDataSource fetch
  - Rebalance cancellation: System stability and lifecycle integrity under concurrent requests
- *Test verifies*: 
  - `Invariant_G46_UserCancellationDuringFetch`: Cancelling during IDataSource fetch throws OperationCanceledException
  - `Invariant_G46_RebalanceCancellation`: Background rebalance supports cancellation mechanism (high-level guarantee)
- *Important*: System does NOT guarantee cancellation on new requests. Cancellation MAY occur depending on Decision Engine scheduling validation. Focus is on system stability and cache consistency, not deterministic cancellation behavior.
- *Related*: F.35 (detailed rebalance execution cancellation mechanics), A.0a (User Path priority via validation-driven cancellation)

---

## H. Activity Tracking & Idle Detection Invariants

### Background

The `AsyncActivityCounter` provides lock-free idle state detection for background operations. It tracks active work (intent processing, rebalance execution) and signals completion when all work finishes. This enables deterministic synchronization for testing, disposal, and health checks.

**Key Concept**: Activity tracking creates an **orchestration barrier** — work must increment counter BEFORE becoming visible, ensuring idle detection never misses scheduled-but-not-yet-started work.

### The Two Critical Invariants

**H.47** 🔵 **[Architectural — Enforced by call site ordering]** **Increment-Before-Publish Invariant:**
Any operation that schedules, publishes, or enqueues background work MUST call `IncrementActivity()` BEFORE making that work visible to consumers.

- *Enforced by*: Explicit ordering in all publication call sites
- *Architecture*: Activity counter incremented before semaphore signal, channel write, or volatile write
- *Critical property*: Prevents "scheduled but invisible to idle detection" race condition
- *Call sites*:
  - `IntentController.PublishIntent()`: Increment (line 173) before `_intentSignal.Release()` (line 177)
  - `TaskBasedRebalanceExecutionController.PublishExecutionRequest()`: Increment (line 196) before `Volatile.Write(_currentExecutionTask)` (line 220)
  - `ChannelBasedRebalanceExecutionController.PublishExecutionRequest()`: Increment (line 220) before `WriteAsync()` (line 239)

**H.48** 🔵 **[Architectural — Enforced by finally blocks]** **Decrement-After-Completion Invariant:**
Any operation representing completion of background work MUST call `DecrementActivity()` in a finally block AFTER work is fully completed or cancelled.

- *Enforced by*: finally block placement in all processing loops
- *Architecture*: Decrement always executes regardless of success/cancellation/exception path
- *Critical property*: Prevents activity counter leaks that would cause `WaitForIdleAsync()` to hang indefinitely
- *Call sites*:
  - `IntentController.ProcessIntentsAsync()`: Decrement in finally block (line 271) after intent processing
  - `TaskBasedRebalanceExecutionController.ExecuteRequestAsync()`: Decrement in finally block (line 349) after execution
  - `ChannelBasedRebalanceExecutionController.ExecutionLoopAsync()`: Decrement in finally block (line 327) after execution
  - `ChannelBasedRebalanceExecutionController.PublishExecutionRequest()`: Manual decrement in catch block (line 245) on channel write failure

**H.49** 🟡 **[Conceptual — Eventual consistency design]** **"Was Idle" Semantics:**
`WaitForIdleAsync()` completes when the system **was idle at some point in time**, NOT when "system is idle now".

- *Design rationale*: State-based completion semantics (TaskCompletionSource) provide eventual consistency
- *Behavior*: Reading a completed TCS after new activity starts is correct — system WAS idle between observations
- *Implication*: Callers requiring stronger guarantees (e.g., "still idle after await") must implement retry logic or re-check state
- *Testing usage*: Sufficient for convergence testing — system stabilized at snapshot time

### Activity-Based Stabilization Barrier

The combination of H.47 and H.48 creates a **stabilization barrier** with strong guarantees:

**Idle state (counter=0) means:**
- ✅ No intents being processed
- ✅ No rebalance executions running  
- ✅ No work enqueued in channels or task chains
- ✅ No "scheduled but invisible" work exists

**Race scenario (correct behavior):**
1. T1 decrements to 0, signals TCS_old (idle achieved)
2. T2 increments to 1, creates TCS_new (new busy period)
3. T3 calls `WaitForIdleAsync()`, reads TCS_old (already completed)
4. Result: Method completes immediately even though count=1

This is **correct** — system WAS idle between steps 1 and 2. This is textbook eventual consistency semantics.

### Error Handling & Counter Leak Prevention

**ChannelBasedRebalanceExecutionController** demonstrates exceptional error handling:

```csharp
// Lines 237-248 in ChannelBasedRebalanceExecutionController.cs
try
{
    await _executionChannel.Writer.WriteAsync(request).ConfigureAwait(false);
}
catch (Exception ex)
{
    request.Dispose();
    _activityCounter.DecrementActivity();  // Manual cleanup prevents leak
    _cacheDiagnostics.RebalanceExecutionFailed(ex);
    throw;
}
```

If channel write fails (e.g., channel completed during disposal race), the catch block manually decrements to prevent counter leak. This ensures counter remains balanced even in edge cases.

### Execution Flow Example

Complete trace demonstrating both invariants:

```
1. User Thread: GetDataAsync(range)
   ├─> IntentController.PublishIntent()
   │   ├─> Write intent reference
   │   ├─> ✅ IncrementActivity()              [count: 0→1, TCS_A created]
   │   └─> Release semaphore (intent visible)
   │
2. Intent Processing Loop (Background Thread)
   ├─> Wake up, read intent
   ├─> DecisionEngine evaluates
   ├─> If skip: jump to finally
   │   └─> finally: ✅ DecrementActivity()     [count: 1→0, TCS_A signaled → IDLE]
   │
   ├─> If schedule:
   │   ├─> ExecutionController.PublishExecutionRequest()
   │   │   ├─> ✅ IncrementActivity()          [count: 1→2]
   │   │   └─> Enqueue/chain execution request (work visible)
   │   └─> finally: ✅ DecrementActivity()     [count: 2→1]
   │
3. Rebalance Execution Loop (Background Thread)
   ├─> Dequeue/await execution request
   ├─> Executor.ExecuteAsync() [CACHE MUTATIONS]
   └─> finally: ✅ DecrementActivity()         [count: 1→0, TCS_A signaled → IDLE]
```

**Key insight**: Idle state occurs ONLY when no work is active, enqueued, or scheduled. The increment-before-publish pattern ensures this guarantee holds across all execution paths.

### Relation to Other Invariants

- **A.-1** (Single-Writer Architecture): Activity tracking supports single-writer by tracking execution lifecycle
- **F.35** (Cancellation Support): DecrementActivity in finally blocks ensures counter correctness even on cancellation
- **G.46** (User/Background Cancellation): Activity counter remains balanced regardless of cancellation timing

---

## Summary Statistics

### Total Invariants: 49

#### By Category:
- 🟢 **Behavioral** (test-covered): 19 invariants
- 🔵 **Architectural** (structure-enforced): 22 invariants  
- 🟡 **Conceptual** (design-level): 8 invariants

#### Test Coverage Analysis:
- **29 automated tests** in `WindowCacheInvariantTests`
- **19 behavioral invariants** directly covered
- **22 architectural invariants** enforced by code structure (not tested)
- **8 conceptual invariants** documented as design guidance (not tested)

**This is by design.** The gap between 49 invariants and 29 tests is intentional:
- Architecture enforces structural constraints automatically
- Conceptual invariants guide development, not runtime behavior
- Tests focus on externally observable behavior

### Cross-References

For each behavioral invariant, the corresponding test is referenced in the invariant description.

For architectural invariants, the enforcement mechanism (component, boundary, pattern) is documented.

For conceptual invariants, the design rationale is explained.

---

## Related Documentation

- **[Component Map](component-map.md)** - Detailed component responsibilities and ownership
- **[Concurrency Model](concurrency-model.md)** - Single-consumer model and coordination
- **[Scenario Model](scenario-model.md)** - Temporal behavior scenarios
- **[Storage Strategies](storage-strategies.md)** - Staging buffer pattern and memory behavior
