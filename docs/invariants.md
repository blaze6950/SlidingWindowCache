# Sliding Window Cache — System Invariants (Classified)

---

## Understanding This Document

This document lists **46 system invariants** that define the behavior, architecture, and design intent of the Sliding Window Cache.

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
- **Mechanism**: Task lifecycle tracking using observe-and-stabilize pattern
- **Guarantee**: Returns only when no rebalance execution is running
- **Safety**: Works correctly under concurrent intent cancellation and rescheduling

### Implementation Strategy

- `RebalanceScheduler` tracks latest background Task in `_idleTask` field
- `WaitForIdleAsync()` implements observe-and-stabilize loop:
  1. Read current `_idleTask` via `Volatile.Read` (ensures visibility)
  2. Await the observed Task
  3. Re-check if `_idleTask` changed (new rebalance scheduled)
  4. Loop until Task reference stabilizes and completes

This provides deterministic synchronization useful for testing, graceful shutdown, 
health checks, and other infrastructure scenarios.

### Architectural Boundaries

This synchronization mechanism **does not alter actor responsibilities**:

- ✅ UserRequestHandler remains the ONLY publisher of rebalance intents
- ✅ IntentController remains the lifecycle authority for intent cancellation
- ✅ RebalanceScheduler remains the authority for background Task execution
- ✅ WindowCache remains a composition root with no business logic

The method exists solely to expose idle synchronization through the public API for testing,
maintaining architectural separation.

### Relation to Instrumentation Counters

Instrumentation counters track **events** (intent published, execution started, etc.) but are
not used for synchronization. The observe-and-stabilize pattern based on Task lifecycle provides
deterministic, race-free synchronization without polling or timing dependencies.

**Old approach (removed):**
- Counter-based polling with stability windows
- Timing-dependent with configurable intervals
- Complex lifecycle calculation

**Current approach:**
- Direct Task lifecycle tracking
- Deterministic (no timing assumptions)
- Simple and race-free

---

## A. User Path & Fast User Access Invariants

### A.1 Concurrency & Priority

**A.-1** 🔵 **[Architectural]** The User Path and Rebalance Execution **never write to cache concurrently**.
- *Enforced by*: Single-writer architecture - User Path is read-only, only Rebalance Execution writes
- *Architecture*: User Path never mutates cache state; Rebalance Execution is sole writer

**A.0** 🔵 **[Architectural]** The User Path **always has higher priority** than Rebalance Execution.
- *Enforced by*: Component ownership, cancellation protocol
- *Architecture*: User Path cancels rebalance; rebalance checks cancellation

**A.0a** 🟢 **[Behavioral — Test: `Invariant_A_0a_UserRequestCancelsRebalance`]** Every User Request **MUST cancel** any ongoing or pending Rebalance Execution to ensure rebalance doesn't interfere with User Path data assembly.
- *Observable via*: DEBUG instrumentation counters tracking cancellation
- *Test verifies*: Cancellation counter increments when new request arrives
- *Note*: Cancellation ensures User Path priority, not mutation safety (User Path is read-only)

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
- *Enforced by*: `Task.Run()` in `RebalanceScheduler`, fire-and-forget pattern
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

**C.17** 🟢 **[Behavioral — Test: `Invariant_C17_AtMostOneActiveIntent`]** At any point in time, there is **at most one active rebalance intent**.
- *Observable via*: DEBUG counters showing intent published/cancelled
- *Test verifies*: Multiple rapid requests show N published, N-1 cancelled

**C.18** 🟢 **[Behavioral — Test: `Invariant_C18_PreviousIntentBecomesObsolete`]** Any previously created rebalance intent is **considered obsolete** after a new intent is generated.
- *Observable via*: DEBUG counters tracking intent lifecycle
- *Test verifies*: Old intent cancelled when new one published

**C.19** 🔵 **[Architectural]** Any rebalance execution can be **cancelled or have its results ignored**.
- *Enforced by*: `CancellationToken` passed through execution pipeline
- *Architecture*: All async operations check cancellation token

**C.20** 🔵 **[Architectural]** If a rebalance intent becomes obsolete before execution begins, the execution **must not start**.
- *Enforced by*: `IsCancellationRequested` check after debounce
- *Architecture*: Early exit in `RebalanceScheduler.ExecutePipelineAsync`

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
- *Implementation*: Early exit in `RebalanceExecutor.ExecuteAsync` before I/O operations

**D.29** 🔵 **[Architectural]** Rebalance execution is triggered **only if the Decision Path confirms necessity**.
- *Enforced by*: `RebalanceScheduler` checks decision before calling executor
- *Architecture*: Decision result gates execution

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

**F.35** 🟢 **[Behavioral — Test: `Invariant_F35_RebalanceExecutionSupportsCancellation`]** Rebalance Execution **MUST support cancellation** at all stages (before I/O, during I/O, before mutations).
- *Observable via*: DEBUG counters showing execution cancelled, lifecycle tracking (Started == Completed + Cancelled)
- *Test verifies*: Rapid requests cancel pending rebalance, execution lifecycle properly tracked
- *Implementation details*: `ThrowIfCancellationRequested()` at multiple checkpoints in execution pipeline
- *Related*: C.24d (execution skipped due to cancellation), A.0a (User Path triggers cancellation), G.46 (high-level guarantee)

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
2. **Background rebalance cancellation**: New user requests cancel pending/ongoing rebalance execution
- *Observable via*: 
  - User cancellation: OperationCanceledException thrown during IDataSource fetch
  - Rebalance cancellation: DEBUG counters showing intent/execution cancelled
- *Test verifies*: 
  - `Invariant_G46_UserCancellationDuringFetch`: Cancelling during IDataSource fetch throws OperationCanceledException
  - `Invariant_G46_RebalanceCancellation`: Background rebalance supports cancellation (high-level guarantee)
- *Related*: F.35 (detailed rebalance execution cancellation mechanics), A.0a (User Path priority via cancellation)

---

## Summary Statistics

### Total Invariants: 47

#### By Category:
- 🟢 **Behavioral** (test-covered): 19 invariants
- 🔵 **Architectural** (structure-enforced): 20 invariants  
- 🟡 **Conceptual** (design-level): 8 invariants

#### Test Coverage Analysis:
- **29 automated tests** in `WindowCacheInvariantTests`
- **19 behavioral invariants** directly covered
- **20 architectural invariants** enforced by code structure (not tested)
- **8 conceptual invariants** documented as design guidance (not tested)

**This is by design.** The gap between 47 invariants and 29 tests is intentional:
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
