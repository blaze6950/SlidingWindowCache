# Sliding Window Cache — System Invariants

---

## Understanding This Document

This document lists **56 system invariants** that define the behavior, architecture, and design intent of the Sliding Window Cache.

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
- Tracks active operations using atomic operations
- Signals idle state via state-based completion semantics (not event-based)
- Lock-free coordination for all operations
- Provides "was idle" semantics (not "is idle now")

**WaitForIdleAsync() Workflow:**
1. Snapshot current completion state
2. Await completion (occurs when counter reached 0 at snapshot time)
3. Return immediately if already completed, or wait for completion

**Idle State Semantics - "Was Idle" NOT "Is Idle":**

WaitForIdleAsync completes when the system **was idle at some point in time**. 
It does NOT guarantee the system is still idle after completion (new activity may start immediately).

Example race (correct behavior):
1. Background thread decrements counter to 0, signals idle completion
2. New intent arrives, increments counter to 1, creates new busy period
3. Test calls WaitForIdleAsync, observes already-completed state
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
- State-based completion semantics
- Deterministic "was idle" semantics (eventual consistency)
- No timing assumptions, no polling

---

## A. User Path & Fast User Access Invariants

### A.1 Concurrency & Priority

**A.1** 🔵 **[Architectural]** The User Path and Rebalance Execution **never write to cache concurrently**.

**Formal Specification:**
- At any point in time, at most one component has write permission to CacheState
- User Path operations must be read-only with respect to cache state
- All cache mutations must be performed by a single designated writer

**Rationale:** Eliminates write-write races and simplifies reasoning about cache consistency through architectural constraints.

**Implementation:** See `docs/components/overview.md` and `docs/architecture.md` for enforcement mechanism details.

**A.2** 🔵 **[Architectural]** The User Path **always has higher priority** than Rebalance Execution.

**Formal Specification:**
- User requests take precedence over background rebalance operations
- Background work must yield when new user activity requires different cache state
- System prioritizes immediate user needs over optimization work

**Rationale:** Ensures responsive user experience by preventing background optimization from interfering with user-facing operations.

**Implementation:** See `docs/architecture.md` and `docs/components/execution.md` for enforcement mechanism details.

**A.2a** 🟢 **[Behavioral — Test: `Invariant_A_2a_UserRequestCancelsRebalance`]** A User Request **MAY cancel** an ongoing or pending Rebalance Execution **ONLY when a new rebalance is validated as necessary** by the multi-stage decision pipeline.

**Formal Specification:**
- Cancellation is a coordination mechanism, not a decision mechanism
- Rebalance necessity determined by analytical validation (Decision Engine)
- User requests do NOT automatically trigger cancellation
- Validated rebalance necessity triggers cancellation + rescheduling
- Cancellation prevents concurrent rebalance executions, not duplicate decision-making

**Rationale:** Prevents thrashing while allowing necessary cache adjustments when user access pattern changes significantly.

**Implementation:** See `docs/components/execution.md` for enforcement mechanism details.

### A.2 User-Facing Guarantees

**A.3** 🟢 **[Behavioral — Test: `Invariant_A_3_UserPathAlwaysServesRequests`]** The User Path **always serves user requests** regardless of the state of rebalance execution.
- *Observable via*: Public API always returns data successfully
- *Test verifies*: Multiple requests all complete and return correct data

**A.4** 🟢 **[Behavioral — Test: `Invariant_A_4_UserPathNeverWaitsForRebalance`]** The User Path **never waits for rebalance execution** to complete.
- *Observable via*: Request completion time vs. debounce delay
- *Test verifies*: Request completes in <500ms with 1-second debounce
- *Conditional compliance*: `CopyOnReadStorage` acquires a short-lived `_lock` in `Read()` and
  `ToRangeData()`, shared with `Rematerialize()`. The lock is held only for the buffer swap and `Range`
  update (in `Rematerialize()`), or for the duration of the array copy (in `Read()` and `ToRangeData()`).
  All contention is sub-millisecond and bounded. `SnapshotReadStorage` remains
  fully lock-free. See [Storage Strategies Guide](storage-strategies.md#invariant-a4---user-path-never-waits-for-rebalance-conditional-compliance) for details.

**A.5** 🔵 **[Architectural]** The User Path is the **sole source of rebalance intent**.

**Formal Specification:**
- Only User Path publishes rebalance intents
- No other component may trigger rebalance operations
- Intent publishing is exclusive to user request handling

**Rationale:** Centralizes intent origination to single actor, simplifying reasoning about when and why rebalances occur.

**Implementation:** See `docs/components/user-path.md` for enforcement mechanism details.

**A.6** 🔵 **[Architectural]** Rebalance execution is **always performed asynchronously** relative to the User Path.

**Formal Specification:**
- User requests return immediately without waiting for rebalance completion
- Rebalance operations execute in background threads
- User Path and rebalance execution are temporally decoupled

**Rationale:** Prevents user requests from blocking on background optimization work, ensuring responsive user experience.

**Implementation:** See `docs/architecture.md` and `docs/components/execution.md` for enforcement mechanism details.

**A.7** 🔵 **[Architectural]** The User Path performs **only the work necessary to return data to the user**.

**Formal Specification:**
- User Path does minimal work: assemble data, return to user
- No cache normalization, trimming, or optimization in User Path
- Background work deferred to rebalance execution

**Rationale:** Minimizes user-facing latency by deferring non-essential work to background threads.

**Implementation:** See `docs/components/user-path.md` for enforcement mechanism details.

**A.8** 🟡 **[Conceptual]** The User Path may synchronously request data from `IDataSource` in the user execution context if needed to serve `RequestedRange`.
- *Design decision*: Prioritizes user-facing latency over background work
- *Rationale*: User must get data immediately; background prefetch is opportunistic

**A.10** 🟢 **[Behavioral — Test: `Invariant_A_10_UserAlwaysReceivesExactRequestedRange`]** The User always receives data **exactly corresponding to `RequestedRange`**.
- *Observable via*: Returned data length and content
- *Test verifies*: Data matches requested range exactly (no more, no less)

**A.10a** 🔵 **[Architectural]** `GetDataAsync` returns `RangeResult<TRange, TData>` containing the actual range fulfilled, the corresponding data, and the cache interaction classification.

**Formal Specification:**
- Return type: `ValueTask<RangeResult<TRange, TData>>`
- `RangeResult.Range` indicates the actual range returned (may differ from requested in bounded data sources)
- `RangeResult.Data` contains `ReadOnlyMemory<TData>` for the returned range
- `RangeResult.CacheInteraction` classifies how the request was served (`FullHit`, `PartialHit`, or `FullMiss`)
- `Range` is nullable to signal data unavailability without exceptions
- When `Range` is non-null, `Data.Length` MUST equal `Range.Span(domain)`

**Rationale:** 
- Explicit boundary contracts between cache and consumers
- Bounded data sources can signal truncation or unavailability gracefully
- No exceptions for normal boundary conditions (out-of-bounds is expected, not exceptional)
- `CacheInteraction` exposes per-request cache efficiency classification for programmatic use

**Related Documentation:** [Boundary Handling Guide](boundary-handling.md) — comprehensive coverage of RangeResult usage patterns, bounded data source implementation, partial fulfillment handling, and testing.

**A.10b** 🔵 **[Architectural]** `RangeResult.CacheInteraction` **accurately reflects** the cache interaction type for every request.

**Formal Specification:**
- `CacheInteraction.FullMiss` — `IsInitialized == false` (cold start) OR `CurrentCacheRange` does not intersect `RequestedRange` (jump)
- `CacheInteraction.FullHit` — `CurrentCacheRange` fully contains `RequestedRange`
- `CacheInteraction.PartialHit` — `CurrentCacheRange` intersects but does not fully contain `RequestedRange`

**Rationale:** Enables callers to branch on cache efficiency per request — for example, `GetDataAndWaitOnMissAsync` (hybrid consistency mode) uses `CacheInteraction` to decide whether to call `WaitForIdleAsync`.

**Implementation:** Set exclusively by `UserRequestHandler.HandleRequestAsync` at scenario classification time. `RangeResult` constructor is `internal`; only `UserRequestHandler` may construct instances.

### A.3 Cache Mutation Rules (User Path)

**A.11** 🔵 **[Architectural]** The User Path may read from cache and `IDataSource` but **does not mutate cache state**.

**Formal Specification:**
- User Path has read-only access to cache state
- No write operations permitted in User Path
- Cache, IsInitialized, and NoRebalanceRange are immutable from User Path perspective

**Rationale:** Enforces single-writer architecture, eliminating write-write races and simplifying concurrency reasoning.

**Implementation:** See `docs/architecture.md` and `docs/components/overview.md` for enforcement mechanism details.

**A.12** 🔵 **[Architectural — Tests: `Invariant_A_12_ColdStart`, `_CacheExpansion`, `_FullCacheReplacement`]** The User Path **MUST NOT mutate cache under any circumstance**.

**Formal Specification:**
- User Path is strictly read-only with respect to cache state
- User Path never triggers cache rematerialization
- User Path never updates IsInitialized or NoRebalanceRange
- All cache mutations exclusively performed by Rebalance Execution (single-writer)

**Rationale:** Enforces single-writer architecture at the strictest level, preventing any mutation-related bugs in User Path.

**Implementation:** See `docs/architecture.md` and `docs/components/overview.md` for enforcement mechanism details.

**A.12a** 🔵 **[Architectural]** Cache mutations are performed **exclusively by Rebalance Execution** (single-writer architecture).

**Formal Specification:**
- Only one component has permission to write to cache state
- Rebalance Execution is the sole writer
- All other components have read-only access

**Rationale:** Single-writer architecture eliminates write-write races and simplifies concurrency model.

**Implementation:** See `docs/architecture.md` and `docs/components/overview.md` for enforcement mechanism details.

**A.12b** 🟢 **[Behavioral — Test: `Invariant_A_12b_CacheContiguityMaintained`]** **Cache Contiguity Rule:** `CacheData` **MUST always remain contiguous** — gapped or partially materialized cache states are invalid.
- *Observable via*: All requests return valid contiguous data
- *Test verifies*: Sequential overlapping requests all succeed

---

## B. Cache State & Consistency Invariants

**B.1** 🟢 **[Behavioral — Test: `Invariant_B_1_CacheDataAndRangeAlwaysConsistent`]** `CacheData` and `CurrentCacheRange` are **always consistent** with each other.
- *Observable via*: Data length always matches range size
- *Test verifies*: For any request, returned data length matches expected range size

**B.2** 🔵 **[Architectural]** Changes to `CacheData` and the corresponding `CurrentCacheRange` are performed **atomically**.

**Formal Specification:**
- Cache data and range updates are indivisible operations
- No intermediate states where data and range are inconsistent
- Updates appear instantaneous to all observers

**Rationale:** Prevents readers from observing inconsistent cache state during updates.

**Implementation:** See `docs/invariants.md` (atomicity invariants) and source XML docs; architecture context in `docs/architecture.md`.

**B.3** 🔵 **[Architectural]** The system **never enters a permanently inconsistent state** with respect to `CacheData ↔ CurrentCacheRange`.

**Formal Specification:**
- Cache data always matches its declared range
- Cancelled operations cannot leave cache in invalid state
- System maintains consistency even under concurrent cancellation

**Rationale:** Ensures cache remains usable even when rebalance operations are cancelled mid-flight.

**Implementation:** See `docs/architecture.md` and execution invariants in `docs/invariants.md`.

**B.4** 🟡 **[Conceptual]** Temporary geometric or coverage inefficiencies in the cache are acceptable **if they can be resolved by rebalance execution**.
- *Design decision*: User Path prioritizes speed over optimal cache shape
- *Rationale*: Background rebalance will normalize; temporary inefficiency is acceptable

**B.5** 🟢 **[Behavioral — Test: `Invariant_B_5_CancelledRebalanceDoesNotViolateConsistency`]** Partially executed or cancelled rebalance execution **cannot violate `CacheData ↔ CurrentCacheRange` consistency**.
- *Observable via*: Cache continues serving valid data after cancellation
- *Test verifies*: Rapid request changes don't corrupt cache

**B.6** 🔵 **[Architectural]** Results from rebalance execution are applied **only if they correspond to the latest active rebalance intent**.

**Formal Specification:**
- Obsolete rebalance results are discarded
- Only current, valid results update cache state
- System prevents applying stale computations

**Rationale:** Prevents cache from being updated with results that no longer match current user access pattern.

**Implementation:** See `docs/components/intent-management.md` and intent invariants in `docs/invariants.md`.

---

## C. Rebalance Intent & Temporal Invariants

**C.1** 🔵 **[Architectural]** At most one rebalance intent may be active at any time.

**Formal Specification:**
- System maintains at most one pending rebalance intent
- New intents supersede previous ones
- Intent singularity prevents buildup of obsolete work

**Rationale:** Prevents queue buildup and ensures system always works toward most recent user access pattern.

**Implementation:** See `docs/components/intent-management.md`.

**C.2** 🟡 **[Conceptual]** Previously created intents may become **logically superseded** when a new intent is published, but rebalance execution relevance is determined by the **multi-stage rebalance validation logic**.
- *Design intent*: Obsolescence ≠ cancellation; obsolescence ≠ guaranteed execution prevention
- *Clarification*: Intents are access signals, not commands. An intent represents "user accessed this range," not "must execute rebalance." Execution decisions are governed by the Rebalance Decision Engine's analytical validation (Stage 1: Current Cache NoRebalanceRange check, Stage 2: Pending Desired Cache NoRebalanceRange check if applicable, Stage 3: DesiredCacheRange vs CurrentCacheRange equality check). Previously created intents may be superseded or cancelled, but the decision to execute is always based on current validation state, not intent age. Cancellation occurs ONLY when Decision Engine validation confirms a new rebalance is necessary.

**C.3** 🔵 **[Architectural]** Any rebalance execution can be **cancelled or have its results ignored**.

**Formal Specification:**
- Rebalance operations are interruptible
- Results from cancelled operations are discarded
- System supports cooperative cancellation throughout pipeline

**Rationale:** Enables User Path priority by allowing cancellation of obsolete background work.

**Implementation:** See `docs/architecture.md` and `docs/components/intent-management.md`.

**C.4** 🔵 **[Architectural]** If a rebalance intent becomes obsolete before execution begins, the execution **must not start**.

**Formal Specification:**
- Obsolete rebalance operations must not execute
- Early exit prevents wasted work
- System validates intent relevance before execution

**Rationale:** Avoids wasting CPU and I/O resources on obsolete cache shapes that no longer match user needs.

**Implementation:** See `docs/components/decision.md` and decision invariants in `docs/invariants.md`.

**C.5** 🔵 **[Architectural]** At any point in time, **at most one rebalance execution is active**.

**Formal Specification:**
- Only one rebalance operation executes at a time
- Concurrent rebalance executions are prevented
- Serial execution guarantees single-writer consistency

**Rationale:** Enforces single-writer architecture by ensuring only one component can mutate cache at any time.

**Implementation:** See `docs/architecture.md` (execution strategies) and `docs/components/execution.md`.

**C.6** 🟡 **[Conceptual]** The results of rebalance execution **always reflect the latest user access pattern**.
- *Design guarantee*: Obsolete results are discarded
- *Rationale*: System converges to user's actual navigation pattern

**C.7** 🟢 **[Behavioral — Test: `Invariant_C_7_SystemStabilizesUnderLoad`]** During spikes of user requests, the system **eventually stabilizes** to a consistent cache state.
- *Observable via*: After burst of requests, system serves data correctly
- *Test verifies*: Rapid burst + wait → final request succeeds

**C.8** 🟡 **[Conceptual — Test: `Invariant_C_8_IntentDoesNotGuaranteeExecution`]** **Intent does not guarantee execution. Execution is opportunistic and may be skipped entirely.**
   - Publishing an intent does NOT guarantee that rebalance will execute
   - Execution may be cancelled before starting (due to new intent)
   - Execution may be cancelled during execution (User Path priority)
   - Execution may be skipped by DecisionEngine (NoRebalanceRange, DesiredRange == CurrentRange)
   - This is by design: intent represents "user accessed this range", not "must rebalance"
- *Design decision*: Rebalance is opportunistic, not mandatory
- *Test note*: Test verifies skip behavior exists, but non-execution is acceptable

**C.8a** 🟢 **[Behavioral]** Intent delivery and cache interaction classification are coupled: intent MUST be published with the actual `CacheInteraction` value for the served request.

**C.8b** 🟢 **[Behavioral]** `RebalanceSkippedNoRebalanceRange` counter increments when execution is skipped because `RequestedRange ⊆ NoRebalanceRange`.

**C.8c** 🟢 **[Behavioral]** `RebalanceSkippedSameRange` counter increments when execution is skipped because `DesiredCacheRange == CurrentCacheRange`.

**C.8d** 🟢 **[Behavioral]** Execution is skipped when cancelled before it starts (not counted in skip counters; counted in cancellation counters).

**C.8e** 🔵 **[Architectural]** Intent **MUST contain delivered data** representing what was actually returned to the user for the requested range.

**Formal Specification:**
- Intent includes actual data delivered to user
- Data materialized once and shared between user response and intent
- Ensures rebalance uses same data user received

**Rationale:** Prevents duplicate data fetching and ensures cache converges to exact data user saw.

**Implementation:** See `docs/components/user-path.md` and intent invariants in `docs/invariants.md`.

**C.8f** 🟡 **[Conceptual]** Delivered data in intent serves as the **authoritative source** for Rebalance Execution, avoiding duplicate fetches and ensuring consistency with user view.
- *Design guarantee*: Rebalance Execution uses delivered data as base, not current cache
- *Rationale*: Eliminates redundant IDataSource calls, ensures cache converges to what user received

---

## D. Rebalance Decision Path Invariants

> **📖 For architectural explanation, see:** `docs/architecture.md`

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

The Rebalance Decision Engine validates rebalance necessity through five stages:

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

**D.1** 🔵 **[Architectural]** The Rebalance Decision Path is **purely analytical** and has **no side effects**.

**Formal Specification:**
- Decision logic is pure: inputs → decision
- No I/O operations during decision evaluation
- No state mutations during decision evaluation
- Deterministic: same inputs always produce same decision

**Rationale:** Pure decision logic enables reasoning about correctness and prevents unintended side effects.

**Implementation:** See `docs/components/execution.md`.

**D.2** 🔵 **[Architectural]** The Decision Path **never mutates cache state**.

**Formal Specification:**
- Decision logic has no write access to cache
- Decision components are read-only with respect to system state
- Separation between decision (analytical) and execution (mutating)

**Rationale:** Enforces clean separation between decision-making and state mutation, simplifying reasoning.

**Implementation:** See `docs/architecture.md` and `docs/components/execution.md`.

**D.2a** 🔵 **[Architectural]** Stage 2 (Pending Desired Cache NoRebalanceRange Validation) **MUST evaluate against the pending execution's `DesiredNoRebalanceRange`**, not the current cache's NoRebalanceRange.

**Formal Specification:**
- Stage 2 reads `lastExecutionRequest?.DesiredNoRebalanceRange` (the NoRebalanceRange that will hold once the pending execution completes)
- If `RequestedRange ⊆ PendingDesiredNoRebalanceRange`, skip rebalance (anti-thrashing)
- This check is skipped if there is no pending execution (`lastExecutionRequest == null`)
- Must NOT fall back to CurrentCacheRange's NoRebalanceRange for this check (that is Stage 1)

**Rationale:** Prevents oscillation when a rebalance is in-flight: a new intent for a nearby range should not interrupt an already-optimal pending execution.

**Implementation:** See `RebalanceDecisionEngine` source and `docs/components/decision.md`.

**D.3** 🟢 **[Behavioral — Test: `Invariant_D_3_NoRebalanceIfRequestInNoRebalanceRange`]** If `RequestedRange` is fully contained within `NoRebalanceRange`, **rebalance execution is prohibited**.
- *Observable via*: DEBUG counters showing execution skipped (policy-based, see C.8b)
- *Test verifies*: Request within NoRebalanceRange doesn't trigger execution

**D.4** 🟢 **[Behavioral — Test: `Invariant_D_4_SkipWhenDesiredEqualsCurrentRange`]** If `DesiredCacheRange == CurrentCacheRange`, **rebalance execution is not required**.
- *Observable via*: DEBUG counter `RebalanceSkippedSameRange` (optimization-based, see C.8c)
- *Test verifies*: Repeated request with same range increments skip counter
- *Implementation*: Early exit in `RebalanceDecisionEngine.Evaluate` (Stage 4) before execution is scheduled

**D.5** 🔵 **[Architectural]** Rebalance execution is triggered **only if ALL stages of the multi-stage decision pipeline confirm necessity**.

**Formal Specification:**
- Five-stage validation pipeline gates execution
- All stages must pass for execution to proceed
- Multi-stage approach prevents unnecessary work while ensuring convergence
- Critical Principle: Rebalance executes ONLY if ALL stages pass validation

**Decision Pipeline Stages**:
1. **Stage 1 — Current Cache NoRebalanceRange Validation**: Skip if RequestedRange contained in current NoRebalanceRange (fast path)
2. **Stage 2 — Pending Desired Cache NoRebalanceRange Validation**: Validate against pending NoRebalanceRange to prevent thrashing
3. **Stage 3 — Compute DesiredCacheRange**: Determine optimal cache range from RequestedRange + configuration
4. **Stage 4 — DesiredCacheRange vs CurrentCacheRange Equality**: Skip if DesiredCacheRange equals CurrentCacheRange (no change needed)
5. **Stage 5 — Schedule Execution**: All stages passed; schedule rebalance execution

**Rationale:** Multi-stage validation prevents thrashing while ensuring cache converges to optimal state.

**Implementation:** See decision engine source XML docs; conceptual model in `docs/architecture.md`.

---

## E. Cache Geometry & Policy Invariants

**E.1** 🟢 **[Behavioral — Test: `Invariant_E_1_DesiredRangeComputedFromConfigAndRequest`]** `DesiredCacheRange` is computed **solely from `RequestedRange` and cache configuration**.
- *Observable via*: After rebalance, cache covers expected expanded range
- *Test verifies*: With config (leftSize=1.0, rightSize=1.0), cache expands as expected

**E.2** 🔵 **[Architectural]** `DesiredCacheRange` is **independent of the current cache contents**, but may use configuration and `RequestedRange`.

**Formal Specification:**
- Desired range computed only from configuration and requested range
- Current cache state does not influence desired range calculation
- Pure function: config + requested range → desired range

**Rationale:** Deterministic range computation ensures predictable cache behavior independent of history.

**Implementation:** See range planner source XML docs; architecture context in `docs/components/decision.md`.

**E.3** 🟡 **[Conceptual]** `DesiredCacheRange` represents the **canonical target state** towards which the system converges.
- *Design concept*: Single source of truth for "what cache should be"
- *Rationale*: Ensures deterministic convergence behavior

**E.4** 🟡 **[Conceptual]** The geometry of the sliding window is **determined by configuration**, not by scenario-specific logic.
- *Design principle*: Configuration drives behavior, not hard-coded heuristics
- *Rationale*: Predictable, user-controllable cache shape

**E.5** 🔵 **[Architectural]** `NoRebalanceRange` is derived **from `CurrentCacheRange` and configuration**.

**Formal Specification:**
- No-rebalance range computed from current cache range and threshold configuration
- Represents stability zone around current cache
- Pure computation: current range + thresholds → no-rebalance range

**Rationale:** Stability zone prevents thrashing when user makes small movements within already-cached area.

**Implementation:** See `docs/components/decision.md`.

**E.6** 🟢 **[Behavioral]** When both `LeftThreshold` and `RightThreshold` are specified (non-null), their sum must not exceed 1.0.

**Formal Specification:**
```
leftThreshold.HasValue && rightThreshold.HasValue 
    => leftThreshold.Value + rightThreshold.Value <= 1.0
```

**Rationale:** Thresholds define inward shrinkage from cache boundaries to create the no-rebalance stability zone. If their sum exceeds 1.0 (100% of cache), the shrinkage zones would overlap, creating invalid range geometry where boundaries would cross.

**Enforcement:** Constructor validation in `WindowCacheOptions` - throws `ArgumentException` at construction time if violated.

**Edge Cases:**
- Exactly 1.0 is valid (thresholds meet at center point, creating zero-width stability zone)
- Single threshold can be any value ≥ 0 (including 1.0 or greater) - sum validation only applies when both are specified
- Both null is valid (no threshold-based rebalancing)

**Test Coverage:** Unit tests in `WindowCacheOptionsTests` verify validation logic.

---

## F. Rebalance Execution Invariants

### F.1 Execution Control & Cancellation

**F.1** 🟢 **[Behavioral — Test: `Invariant_F_1_G_4_RebalanceCancellationBehavior`]** Rebalance Execution **MUST be cancellation-safe** at all stages (before I/O, during I/O, before mutations).
- *Observable via*: Lifecycle tracking integrity (Started == Completed + Cancelled), system stability under concurrent requests
- *Test verifies*: 
  - Deterministic termination: Every started execution reaches terminal state
  - No partial mutations: Cache consistency maintained after cancellation
  - Lifecycle integrity: Accounting remains correct under cancellation
- *Implementation details*: `ThrowIfCancellationRequested()` at multiple checkpoints in execution pipeline
- *Note*: Cancellation is triggered by scheduling decisions (Decision Engine validation), not automatically by user requests
- *Related*: C.8d (execution skipped due to cancellation), A.2a (User Path priority via validation-driven cancellation), G.4 (high-level guarantee)

**F.1a** 🔵 **[Architectural]** Rebalance Execution **MUST yield** to User Path requests immediately upon cancellation.

**Formal Specification:**
- Background operations must check for cancellation signals
- Execution must abort promptly when cancelled
- User Path priority enforced through cooperative cancellation

**Rationale:** Ensures background work never degrades responsiveness to user requests.

**Implementation:** See `docs/components/execution.md`.

**F.1b** 🟢 **[Behavioral — Covered by `Invariant_B_5`]** Partially executed or cancelled Rebalance Execution **MUST NOT leave cache in inconsistent state**.
- *Observable via*: Cache continues serving valid data after cancellation
- *Same test as B.5*

### F.2 Cache Mutation Rules (Rebalance Execution)

**F.2** 🔵 **[Architectural]** The Rebalance Execution Path is the **ONLY component that mutates cache state** (single-writer architecture).

**Formal Specification:**
- Only one component has write permission to cache state
- Exclusive mutation authority: Cache, IsInitialized, NoRebalanceRange
- All other components are read-only

**Rationale:** Single-writer architecture eliminates all write-write races and simplifies concurrency reasoning.

**Implementation:** See `docs/architecture.md`.

**F.2a** 🟢 **[Behavioral — Test: `Invariant_F_2a_RebalanceNormalizesCache`]** Rebalance Execution mutates cache for normalization using **delivered data from intent as authoritative base**:
   - **Uses delivered data** from intent (not current cache) as starting point
   - **Expanding to DesiredCacheRange** by fetching only truly missing ranges
   - **Trimming excess data** outside `DesiredCacheRange`
   - **Writing to cache** via `Cache.Rematerialize()`
   - **Writing to IsInitialized** = true after successful rebalance
   - **Recomputing NoRebalanceRange** based on final cache range
- *Observable via*: After rebalance, cache serves data from expanded range
- *Test verifies*: Cache covers larger area after rebalance completes
- *Single-writer guarantee*: These are the ONLY mutations in the system

**F.3** 🔵 **[Architectural]** Rebalance Execution may **replace, expand, or shrink cache data** to achieve normalization.

**Formal Specification:**
- Full mutation capability: expand, trim, or replace cache entirely
- Flexibility to achieve any desired cache geometry
- Single operation can transform cache to target state

**Rationale:** Complete mutation authority enables efficient convergence to optimal cache shape in single operation.

**Implementation:** See `docs/components/execution.md`.

**F.4** 🔵 **[Architectural]** Rebalance Execution requests data from `IDataSource` **only for missing subranges**.

**Formal Specification:**
- Fetch only gaps between existing cache and desired range
- Minimize redundant data fetching
- Preserve existing cached data during expansion

**Rationale:** Avoids wasting I/O bandwidth by re-fetching data already in cache.

**Implementation:** See `docs/components/user-path.md`.

**F.5** 🔵 **[Architectural]** Rebalance Execution **does not overwrite existing data** that intersects with `DesiredCacheRange`.

**Formal Specification:**
- Existing cached data is preserved during rebalance
- New data merged with existing, not replaced
- Union operation maintains data integrity

**Rationale:** Preserves valid cached data, avoiding redundant fetches and ensuring consistency.

**Implementation:** See execution invariants in `docs/invariants.md`.

### F.3 Post-Execution Guarantees

**F.6** 🟢 **[Behavioral — Test: `Invariant_F_6_F_7_F_8_PostExecutionGuarantees`]** Upon successful completion, `CacheData` **strictly corresponds to `DesiredCacheRange`**.
- *Observable via*: After rebalance, cache serves data from expected normalized range
- *Test verifies*: Can read from expected expanded range

**F.7** 🟢 **[Behavioral — Covered by same test as F.6]** Upon successful completion, `CurrentCacheRange == DesiredCacheRange`.
- *Observable indirectly*: Cache behavior matches expected range
- *Same test as F.6*

**F.8** 🟡 **[Conceptual — Covered by same test as F.6]** Upon successful completion, `NoRebalanceRange` is **recomputed**.
- *Internal state*: Not directly observable via public API
- *Design guarantee*: Threshold zone updated after normalization

---

## G. Execution Context & Scheduling Invariants

**G.1** 🟢 **[Behavioral — Test: `Invariant_G_1_G_2_G_3_ExecutionContextSeparation`]** The User Path operates in the **user execution context**.
- *Observable via*: Request completes quickly without waiting for background work
- *Test verifies*: Request time < debounce delay

### G.2: Rebalance Decision Path and Rebalance Execution Path execute outside the user execution context

**Formal Specification:**
The Rebalance Decision Path and Rebalance Execution Path MUST execute asynchronously outside the user execution context. User requests MUST return immediately without waiting for background analysis or I/O operations.

**Architectural Properties:**
- Fire-and-forget pattern: User request publishes work and returns
- No user blocking: Background work proceeds independently
- Decoupled execution: Decision and Execution run in background threads

**Rationale:** Ensures user requests remain responsive by offloading all optimization work to background threads.

**Implementation:** See `docs/architecture.md`.
- 🔵 **[Architectural — Covered by same test as G.1]**

### G.3: I/O responsibilities are separated between User Path and Rebalance Execution Path

**Formal Specification:**
I/O operations (data fetching via IDataSource) are divided by responsibility:
- **User Path** MAY call `IDataSource.FetchAsync` exclusively to serve the user's immediate requested range (Scenarios U1 Cold Start and U5 Full Cache Miss / Jump). This I/O is unavoidable because the user request cannot be served from cache.
- **Rebalance Execution Path** calls `IDataSource.FetchAsync` exclusively for background cache normalization (expanding or rebuilding the cache beyond the requested range).
- No component other than these two may call `IDataSource.FetchAsync`.

**Architectural Properties:**
- User Path I/O is request-scoped: only fetches exactly the RequestedRange, never more
- Background I/O is normalization-scoped: fetches missing segments to reach DesiredCacheRange
- Responsibilities never overlap: User Path never fetches beyond RequestedRange; Rebalance Execution never serves user requests directly

**Rationale:** Separates the latency-critical user-serving fetch (minimal, unavoidable) from the background optimization fetch (potentially large, deferrable). User Path I/O is bounded by the requested range; background I/O is bounded by cache geometry policy.

**Implementation:** See `docs/architecture.md` and execution invariants.
- 🔵 **[Architectural — Covered by same test as G.1]**

**G.4** 🟢 **[Behavioral — Tests: `Invariant_G_4_UserCancellationDuringFetch`, `Invariant_F_1_G_4_RebalanceCancellationBehavior`]** Cancellation **must be supported** for all scenarios:
  - `Invariant_G_4_UserCancellationDuringFetch`: Cancelling during IDataSource fetch throws OperationCanceledException
  - `Invariant_F_1_G_4_RebalanceCancellationBehavior`: Background rebalance supports cancellation mechanism (high-level guarantee)
- *Important*: System does NOT guarantee cancellation on new requests. Cancellation MAY occur depending on Decision Engine scheduling validation. Focus is on system stability and cache consistency, not deterministic cancellation behavior.
- *Related*: F.1 (detailed rebalance execution cancellation mechanics), A.2a (User Path priority via validation-driven cancellation)

**G.5** 🔵 **[Architectural]** `IDataSource.FetchAsync` **MUST respect boundary semantics**: it may return a range smaller than requested (or null) for bounded data sources, and the cache must propagate this truncated result correctly.

**Formal Specification:**
- `IDataSource.FetchAsync` returns `RangeData<TRange, TData>?` — nullable to signal unavailability
- A non-null result MAY have a smaller range than the requested range (partial fulfillment for bounded sources)
- The cache MUST use the actual returned range, not the requested range, when assembling `RangeResult`
- Callers MUST NOT assume the returned range equals the requested range

**Rationale:** Bounded data sources (e.g., finite files, fixed-size datasets) cannot always fulfill the full requested range. The contract allows graceful truncation without exceptions.

**Implementation:** See `IDataSource` contract, `UserRequestHandler`, `CacheDataExtensionService`, and [Boundary Handling Guide](boundary-handling.md).

---

## H. Activity Tracking & Idle Detection Invariants

### Background

The system provides idle state detection for background operations through an activity counter mechanism. It tracks active work (intent processing, rebalance execution) and signals completion when all work finishes. This enables deterministic synchronization for testing, disposal, and health checks.

**Key Architectural Concept**: Activity tracking creates an **orchestration barrier** — work must increment counter BEFORE becoming visible, ensuring idle detection never misses scheduled-but-not-yet-started work.

**Current Implementation** (implementation details - expected to change):
The `AsyncActivityCounter` component implements this using lock-free synchronization primitives.

### The Two Critical Invariants

### H.1: Increment-Before-Publish Invariant

**Formal Specification:**
Any operation that schedules, publishes, or enqueues background work MUST increment the activity counter BEFORE making that work visible to consumers (via semaphore signal, channel write, volatile write, or task chain).

**Critical Property:**
Prevents "scheduled but invisible to idle detection" race condition. If work becomes visible before counter increment, `WaitForIdleAsync()` could signal idle while work is enqueued but not yet started.

**Architectural Guarantee:**
When activity counter reaches zero (idle state), NO work exists in any of these states:
- Scheduled but not yet visible to consumers
- Enqueued in channels or semaphores
- Published but not yet dequeued

**Rationale:** Ensures idle detection accurately reflects all enqueued work, preventing premature idle signals.

**Implementation:** See `src/Intervals.NET.Caching/Infrastructure/Concurrency/AsyncActivityCounter.cs`.
- 🔵 **[Architectural — Enforced by call site ordering]**

### H.2: Decrement-After-Completion Invariant

**Formal Specification:**
Any operation representing completion of background work MUST decrement the activity counter AFTER work is fully completed, cancelled, or failed. Decrement MUST execute unconditionally regardless of success/failure/cancellation path.

**Critical Property:**
Prevents activity counter leaks that would cause `WaitForIdleAsync()` to hang indefinitely. If decrement is missed on any execution path, the counter never reaches zero and idle detection breaks permanently.

**Architectural Guarantee:**
Activity counter accurately reflects active work count at all times:
- Counter > 0: Background work is active, enqueued, or in-flight
- Counter = 0: All work completed, system is idle
- No missed decrements: Counter cannot leak upward

**Rationale:** Ensures `WaitForIdleAsync()` will eventually complete by preventing counter leaks on any execution path.

**Implementation:** See `src/Intervals.NET.Caching/Infrastructure/Concurrency/AsyncActivityCounter.cs`.
- 🔵 **[Architectural — Enforced by finally blocks]**

**H.3** 🟡 **[Conceptual — Eventual consistency design]** **"Was Idle" Semantics:**
`WaitForIdleAsync()` completes when the system **was idle at some point in time**, NOT when "system is idle now".

- *Design rationale*: State-based completion semantics provide eventual consistency
- *Behavior*: Observing completed state after new activity starts is correct — system WAS idle between observations
- *Implication*: Callers requiring stronger guarantees (e.g., "still idle after await") must implement retry logic or re-check state
- *Testing usage*: Sufficient for convergence testing — system stabilized at snapshot time

**Parallel Access Implication for Hybrid/Strong Consistency Extension Methods:**
`GetDataAndWaitOnMissAsync` and `GetDataAndWaitForIdleAsync` provide their warm-cache guarantee only under **serialized (one-at-a-time) access**. Under parallel access, the guarantee degrades:
- Thread A increments the activity counter 0→1 (has not yet published its new TCS)
- Thread B increments 1→2, then calls `WaitForIdleAsync`, reads the old (already-completed) TCS, and returns immediately — without waiting for Thread A's rebalance
- Result: Thread B observes "was idle" from the *previous* idle period, not the one Thread A is driving

Under parallel access, the methods remain safe (no deadlocks, no crashes, no data corruption) but the "warm cache after await" guarantee is not reliable. These methods are designed for single-logical-consumer, one-at-a-time access patterns.

### Activity-Based Stabilization Barrier

The combination of H.1 and H.2 creates a **stabilization barrier** with strong guarantees:

**Idle state (counter=0) means:**
- ✅ No intents being processed
- ✅ No rebalance executions running  
- ✅ No work enqueued in channels or task chains
- ✅ No "scheduled but invisible" work exists

**Race scenario (correct behavior):**
1. T1 decrements to 0, signals idle completion (idle achieved)
2. T2 increments to 1, creates new busy period
3. T3 calls `WaitForIdleAsync()`, observes already-completed state
4. Result: Method completes immediately even though count=1

This is **correct** — system WAS idle between steps 1 and 2. This is textbook eventual consistency semantics.

### Error Handling & Counter Leak Prevention

**Architectural Principle:**
When background work publication fails (e.g., channel closed, queue full), the activity counter increment MUST be reversed to prevent leaks. This requires exception handling at publication sites.

**Current Implementation Example** (implementation details - expected to change):

One strategy is demonstrated in the channel-based execution controller, which uses try-catch to handle write failures:

```csharp
// Example from ChannelBasedRebalanceExecutionController.cs (lines 237-248)
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

**Current Implementation Trace** (implementation details - expected to change):

Complete trace demonstrating both invariants in current architecture:

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

- **A.1** (Single-Writer Architecture): Activity tracking supports single-writer by tracking execution lifecycle
- **F.1** (Cancellation Support): DecrementActivity in finally blocks ensures counter correctness even on cancellation
- **G.4** (User/Background Cancellation): Activity counter remains balanced regardless of cancellation timing

---

## I. Runtime Options Update Invariants

**I.1** 🟢 **[Behavioral — Tests: `RuntimeOptionsUpdateTests`]** `UpdateRuntimeOptions` **validates the merged options** before publishing. Invalid updates (negative sizes, threshold sum > 1.0, out-of-range threshold) throw and leave the current options unchanged.
- *Observable via*: Exception type and cache still accepts subsequent valid updates
- *Test verifies*: `ArgumentOutOfRangeException` / `ArgumentException` thrown; cache not partially updated

**I.2** 🔵 **[Architectural]** `UpdateRuntimeOptions` uses **next-cycle semantics**: the new options snapshot takes effect on the next rebalance decision/execution cycle. Ongoing cycles use the snapshot already read at cycle start.

**Formal Specification:**
- `RuntimeCacheOptionsHolder.Update` performs a `Volatile.Write` (release fence)
- Planners and execution controllers snapshot `holder.Current` once at the start of their operation
- No running cycle is interrupted or modified mid-flight by an options update

**Rationale:** Prevents mid-cycle inconsistencies (e.g., a planner using new `LeftCacheSize` with old `RightCacheSize`). Cycles are short; the next cycle reflects the update.

**Implementation:** `RuntimeCacheOptionsHolder.Update` in `src/Intervals.NET.Caching/Core/State/RuntimeCacheOptionsHolder.cs`.

**I.3** 🔵 **[Architectural]** `UpdateRuntimeOptions` on a disposed cache **always throws `ObjectDisposedException`**.

**Formal Specification:**
- Disposal state checked via `Volatile.Read` before any options update work
- Consistent with all other post-disposal operation guards in the public API

**Implementation:** Disposal guard in `WindowCache.UpdateRuntimeOptions`.

**I.4** 🟡 **[Conceptual]** **`ReadMode` and `RebalanceQueueCapacity` are creation-time only** — they determine the storage strategy and execution controller strategy, which are wired at construction and cannot be replaced at runtime without reconstruction.
- *Design decision*: These choices affect fundamental system structure (object graph), not just configuration parameters
- *Rationale*: Storage strategies and execution controllers have different object identities and lifecycles; hot-swapping them would require disposal and re-creation of component graphs

---

## Summary Statistics

### Total Invariants: 56

#### By Category:
- 🟢 **Behavioral** (test-covered): 21 invariants
- 🔵 **Architectural** (structure-enforced): 26 invariants  
- 🟡 **Conceptual** (design-level): 9 invariants

#### Test Coverage Analysis:
- **29 automated tests** in `WindowCacheInvariantTests`
- **21 behavioral invariants** directly covered
- **26 architectural invariants** enforced by code structure (not tested)
- **9 conceptual invariants** documented as design guidance (not tested)

**This is by design.** The gap between 56 invariants and 29 tests is intentional:
- Architecture enforces structural constraints automatically
- Conceptual invariants guide development, not runtime behavior
- Tests focus on externally observable behavior

### Cross-References

For each behavioral invariant, the corresponding test is referenced in the invariant description.

For architectural invariants, the enforcement mechanism (component, boundary, pattern) is documented.

For conceptual invariants, the design rationale is explained.

---

## Related Documentation

- **[Components](components/overview.md)** - Component responsibilities and ownership
- **[Architecture](architecture.md)** - Single-consumer model and coordination
- **[Scenarios](scenarios.md)** - Temporal behavior scenarios
- **[Storage Strategies](storage-strategies.md)** - Staging buffer pattern and memory behavior
