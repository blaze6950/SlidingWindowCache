# Invariants — SlidingWindowCache

SlidingWindow-specific system invariants. Shared invariant groups — **S.H** (activity tracking) and **S.J** (disposal) — are documented in `docs/shared/invariants.md`.

---

## Understanding This Document

This document lists **52 SlidingWindow-specific invariants** across groups SWC.A–SWC.I (groups SWC.A–SWC.G and SWC.I are SWC-specific; S.H and S.J are shared).

### Invariant Categories

#### Behavioral Invariants
- **Nature**: Externally observable behavior via public API
- **Enforcement**: Automated tests (unit, integration)
- **Verification**: Testable through public API without inspecting internal state

#### Architectural Invariants
- **Nature**: Internal structural constraints enforced by code organization
- **Enforcement**: Component boundaries, encapsulation, ownership model
- **Verification**: Code review, type system, access modifiers
- **Note**: NOT directly testable via public API

#### Conceptual Invariants
- **Nature**: Design intent, guarantees, or explicit non-guarantees
- **Enforcement**: Documentation and architectural discipline
- **Note**: Guide future development; NOT meant to be tested directly

### Invariants ≠ Test Coverage

By design, this document contains more invariants than the test suite covers. Architectural invariants are enforced by code structure; conceptual invariants are documented design decisions. Full invariant documentation does not imply full test coverage.

---

## Testing Infrastructure: WaitForIdleAsync

Tests verify behavioral invariants through the public API. To synchronize with background rebalance operations and assert on converged state, use `WaitForIdleAsync()`:

```csharp
await cache.GetDataAsync(newRange);
await cache.WaitForIdleAsync();
// System WAS idle — assert on converged state
Assert.Equal(expectedRange, cache.CurrentCacheRange);
```

`WaitForIdleAsync` completes when the system **was idle at some point** (eventual consistency semantics), not necessarily "is idle now." For formal semantics and race behavior, see `docs/shared/invariants.md` group S.H.

---

## SWC.A. User Path & Fast User Access Invariants

### SWC.A.1 Concurrency & Priority

**SWC.A.1** [Architectural] The User Path and Rebalance Execution **never write to cache concurrently**.

- At any point in time, at most one component has write permission to `CacheState`
- User Path operations must be read-only with respect to cache state
- All cache mutations must be performed by a single designated writer (Rebalance Execution)

**Rationale:** Eliminates write-write races and simplifies reasoning about cache consistency through architectural constraints.

**SWC.A.2** [Architectural] The User Path **always has higher priority** than Rebalance Execution.

- User requests take precedence over background rebalance operations
- Background work must yield when new user activity requires different cache state

**SWC.A.2a** [Behavioral — Test: `Invariant_SWC_A_2a_UserRequestCancelsRebalance`] A user request **MAY cancel** an ongoing or pending Rebalance Execution **only when a new rebalance is validated as necessary** by the multi-stage decision pipeline.

- Cancellation is a coordination mechanism, not a decision mechanism
- Rebalance necessity is determined by analytical validation (Decision Engine), not by user requests automatically
- Validated rebalance necessity triggers cancellation + rescheduling
- Cancellation prevents concurrent rebalance executions, not duplicate decision-making

### SWC.A.2 User-Facing Guarantees

**SWC.A.3** [Behavioral — Test: `Invariant_SWC_A_3_UserPathAlwaysServesRequests`] The User Path **always serves user requests** regardless of the state of rebalance execution.

**SWC.A.4** [Behavioral — Test: `Invariant_SWC_A_4_UserPathNeverWaitsForRebalance`] The User Path **never waits for rebalance execution** to complete.

- *Conditional compliance*: `CopyOnReadStorage` acquires a short-lived lock in `Read()` and `ToRangeData()`, shared with `Rematerialize()`. The lock is held only for the buffer swap and `Range` update, or for the duration of the array copy. All contention is sub-millisecond and bounded. `SnapshotReadStorage` remains fully lock-free. See `docs/sliding-window/storage-strategies.md` for details.

**SWC.A.5** [Architectural] The User Path is the **sole source of rebalance intent**.

- Only User Path publishes rebalance intents; no other component may trigger rebalance operations

**SWC.A.6** [Architectural] Rebalance execution is **always performed asynchronously** relative to the User Path.

- User requests return immediately without waiting for rebalance completion
- Rebalance operations execute in background threads

**SWC.A.7** [Architectural] The User Path performs **only the work necessary to return data to the user**.

- No cache normalization, trimming, or optimization in User Path
- Background work deferred to Rebalance Execution

**SWC.A.8** [Conceptual] The User Path may synchronously call `IDataSource.FetchAsync` in the user execution context **if needed to serve `RequestedRange`**.

- *Design decision*: Prioritizes user-facing latency over background work
- *Rationale*: User must get data immediately; background prefetch is opportunistic

**SWC.A.10** [Behavioral — Test: `Invariant_SWC_A_10_UserAlwaysReceivesExactRequestedRange`] The user always receives data **exactly corresponding to `RequestedRange`**.

**SWC.A.10a** [Architectural] `GetDataAsync` returns `RangeResult<TRange, TData>` containing the actual range fulfilled, the corresponding data, and the cache interaction classification.

- `RangeResult.Range` indicates the actual range returned (may differ from requested in bounded data sources)
- `RangeResult.Data` contains `ReadOnlyMemory<TData>` for the returned range
- `RangeResult.CacheInteraction` classifies how the request was served (`FullHit`, `PartialHit`, or `FullMiss`)
- `Range` is nullable to signal data unavailability without exceptions
- When `Range` is non-null, `Data.Length` MUST equal `Range.Span(domain)`

See `docs/sliding-window/boundary-handling.md` for RangeResult usage patterns.

**SWC.A.10b** [Architectural] `RangeResult.CacheInteraction` **accurately reflects** the cache interaction type for every request.

- `FullMiss` — `IsInitialized == false` (cold start) OR `CurrentCacheRange` does not intersect `RequestedRange`
- `FullHit` — `CurrentCacheRange` fully contains `RequestedRange`
- `PartialHit` — `CurrentCacheRange` intersects but does not fully contain `RequestedRange`

Set exclusively by `UserRequestHandler.HandleRequestAsync`. `RangeResult` constructor is `internal`; only `UserRequestHandler` may construct instances.

### SWC.A.3 Cache Mutation Rules (User Path)

**SWC.A.11** [Architectural] The User Path may read from cache and `IDataSource` but **does not mutate cache state**.

- Read-only access to `CacheState`: `Cache`, `IsInitialized`, and `NoRebalanceRange` are immutable from User Path perspective

**SWC.A.12** [Architectural — Tests: `Invariant_SWC_A_12_ColdStart`, `_CacheExpansion`, `_FullCacheReplacement`] The User Path **MUST NOT mutate cache under any circumstance**.

- User Path never triggers cache rematerialization
- User Path never updates `IsInitialized` or `NoRebalanceRange`
- All cache mutations exclusively performed by Rebalance Execution (single-writer)

**SWC.A.12a** [Architectural] Cache mutations are performed **exclusively by Rebalance Execution** (single-writer architecture).

**SWC.A.12b** [Behavioral — Test: `Invariant_SWC_A_12b_CacheContiguityMaintained`] **Cache Contiguity Rule:** `CacheData` **MUST always remain contiguous** — gapped or partially materialized cache states are invalid.

---

## SWC.B. Cache State & Consistency Invariants

**SWC.B.1** [Behavioral — Test: `Invariant_SWC_B_1_CacheDataAndRangeAlwaysConsistent`] `CacheData` and `CurrentCacheRange` are **always consistent** with each other.

**SWC.B.2** [Architectural] Changes to `CacheData` and the corresponding `CurrentCacheRange` are performed **atomically**.

- No intermediate states where data and range are inconsistent
- Updates appear instantaneous to all observers (via `Cache.Rematerialize()` atomic reference swap)

**SWC.B.3** [Architectural] The system **never enters a permanently inconsistent state** with respect to `CacheData ↔ CurrentCacheRange`.

- Cancelled operations cannot leave the cache in an invalid state

**SWC.B.4** [Conceptual] Temporary geometric or coverage inefficiencies in the cache are acceptable **if they can be resolved by rebalance execution**.

- *Rationale*: Background rebalance will normalize; temporary inefficiency is acceptable

**SWC.B.5** [Behavioral — Test: `Invariant_SWC_B_5_CancelledRebalanceDoesNotViolateConsistency`] Partially executed or cancelled Rebalance Execution **cannot violate `CacheData ↔ CurrentCacheRange` consistency**.

**SWC.B.6** [Architectural] Results from Rebalance Execution are applied **only if they correspond to the latest active rebalance intent**.

- Obsolete rebalance results are discarded
- Only current, valid results update cache state

---

## SWC.C. Rebalance Intent & Temporal Invariants

**SWC.C.1** [Architectural] At most one rebalance intent may be active at any time.

- New intents supersede previous ones via `Interlocked.Exchange`

**SWC.C.2** [Conceptual] Previously created intents may become **logically superseded** when a new intent is published, but rebalance execution relevance is determined by the **multi-stage rebalance validation logic**.

- *Clarification*: Intents are access signals, not commands. An intent represents "user accessed this range," not "must execute rebalance." Execution decisions are governed by the Decision Engine's analytical validation. Cancellation occurs ONLY when Decision Engine validation confirms a new rebalance is necessary.

**SWC.C.3** [Architectural] Any rebalance execution can be **cancelled or have its results ignored**.

- Supports cooperative cancellation throughout pipeline

**SWC.C.4** [Architectural] If a rebalance intent becomes obsolete before execution begins, the execution **must not start**.

**SWC.C.5** [Architectural] At any point in time, **at most one rebalance execution is active**.

**SWC.C.6** [Conceptual] The results of rebalance execution **always reflect the latest user access pattern**.

- *Rationale*: System converges to user's actual navigation pattern

**SWC.C.7** [Behavioral — Test: `Invariant_SWC_C_7_SystemStabilizesUnderLoad`] During spikes of user requests, the system **eventually stabilizes** to a consistent cache state.

**SWC.C.8** [Conceptual — Test: `Invariant_SWC_C_8_IntentDoesNotGuaranteeExecution`] **Intent does not guarantee execution. Execution is opportunistic and may be skipped entirely.**

- Publishing an intent does NOT guarantee that rebalance will execute
- Execution may be cancelled before starting (due to new intent)
- Execution may be skipped by `DecisionEngine` (`NoRebalanceRange`, `DesiredRange == CurrentRange`)

**SWC.C.8a** [Behavioral] Intent delivery and cache interaction classification are coupled: intent MUST be published with the actual `CacheInteraction` value for the served request.

**SWC.C.8b** [Behavioral] `RebalanceSkippedNoRebalanceRange` counter increments when execution is skipped because `RequestedRange ⊆ NoRebalanceRange`.

**SWC.C.8c** [Behavioral] `RebalanceSkippedSameRange` counter increments when execution is skipped because `DesiredCacheRange == CurrentCacheRange`.

**SWC.C.8d** [Behavioral] Execution is skipped when cancelled before it starts (not counted in skip counters; counted in cancellation counters).

**SWC.C.8e** [Architectural] Intent **MUST contain delivered data** representing what was actually returned to the user for the requested range.

- Intent includes actual data delivered to user; data is materialized once and shared between user response and intent

**SWC.C.8f** [Conceptual] Delivered data in intent serves as the **authoritative source** for Rebalance Execution, avoiding duplicate fetches and ensuring consistency with user view.

---

## SWC.D. Rebalance Decision Path Invariants

The Rebalance Decision Engine validates rebalance necessity through a five-stage CPU-only pipeline, run in the background intent processing loop. See `docs/sliding-window/architecture.md` for the full pipeline description.

**Key distinction:**
- **Rebalance Decision** = Analytical validation determining if rebalance is necessary (decision mechanism)
- **Cancellation** = Mechanical coordination tool ensuring single-writer architecture (coordination mechanism)

**SWC.D.1** [Architectural] The Rebalance Decision Path is **purely analytical** and has **no side effects**.

- Pure function: inputs → decision
- No I/O, no state mutations during decision evaluation
- Deterministic: same inputs always produce same decision

**SWC.D.2** [Architectural] The Decision Path **never mutates cache state**.

- Decision components have no write access to cache
- Clean separation between decision (analytical) and execution (mutating)

**SWC.D.2a** [Architectural] Stage 2 **MUST evaluate against the pending execution's `DesiredNoRebalanceRange`**, not the current cache's `NoRebalanceRange`.

- Stage 2 reads `lastWorkItem?.DesiredNoRebalanceRange` (the `NoRebalanceRange` that will hold once the pending execution completes)
- Must NOT fall back to `CurrentCacheRange`'s `NoRebalanceRange` for this check (that is Stage 1)

**Rationale:** Prevents oscillation when a rebalance is in-flight: a new intent for a nearby range should not interrupt an already-optimal pending execution.

**SWC.D.3** [Behavioral — Test: `Invariant_SWC_D_3_NoRebalanceIfRequestInNoRebalanceRange`] If `RequestedRange ⊆ NoRebalanceRange`, **rebalance execution is prohibited** (Stage 1 skip).

**SWC.D.4** [Behavioral — Test: `Invariant_SWC_D_4_SkipWhenDesiredEqualsCurrentRange`] If `DesiredCacheRange == CurrentCacheRange`, **rebalance execution is not required** (Stage 4 skip).

**SWC.D.5** [Architectural] Rebalance execution is triggered **only if ALL stages of the multi-stage decision pipeline confirm necessity**.

Decision pipeline stages:
1. Stage 1 — Current Cache `NoRebalanceRange` check: skip if `RequestedRange ⊆ CurrentNoRebalanceRange`
2. Stage 2 — Pending `DesiredNoRebalanceRange` check: skip if `RequestedRange ⊆ PendingDesiredNoRebalanceRange` (anti-thrashing)
3. Stage 3 — Compute `DesiredCacheRange` via `ProportionalRangePlanner` + `NoRebalanceRangePlanner`
4. Stage 4 — Equality check: skip if `DesiredCacheRange == CurrentCacheRange`
5. Stage 5 — Schedule execution: all stages passed

---

## SWC.E. Cache Geometry & Policy Invariants

**SWC.E.1** [Behavioral — Test: `Invariant_SWC_E_1_DesiredRangeComputedFromConfigAndRequest`] `DesiredCacheRange` is computed **solely from `RequestedRange` and cache configuration**.

**SWC.E.2** [Architectural] `DesiredCacheRange` is **independent of the current cache contents**, but may use configuration and `RequestedRange`.

- Pure function: config + requested range → desired range
- Deterministic computation ensures predictable behavior independent of history

**SWC.E.3** [Conceptual] `DesiredCacheRange` represents the **canonical target state** towards which the system converges.

**SWC.E.4** [Conceptual] The geometry of the sliding window is **determined by configuration**, not by scenario-specific logic.

- *Rationale*: Predictable, user-controllable cache shape

**SWC.E.5** [Architectural] `NoRebalanceRange` is derived **from `CurrentCacheRange` and configuration**.

- Represents the stability zone: the inner region where no rebalance is triggered even if desired range changes slightly
- Pure computation: current range + thresholds → no-rebalance range

**SWC.E.6** [Behavioral] When both `LeftThreshold` and `RightThreshold` are specified (non-null), their sum must not exceed 1.0.

```
leftThreshold.HasValue && rightThreshold.HasValue 
    => leftThreshold.Value + rightThreshold.Value <= 1.0
```

**Rationale:** Thresholds define inward shrinkage from cache boundaries. If their sum exceeds 1.0, shrinkage zones overlap, creating invalid geometry where boundaries cross.

- Exactly 1.0 is valid (thresholds meet at center point, zero-width stability zone)
- A single threshold can be any value ≥ 0; sum validation only applies when both are specified
- Both null is valid

**Enforcement:** Constructor validation in `SlidingWindowCacheOptions` throws `ArgumentException` at construction time if violated.

---

## SWC.F. Rebalance Execution Invariants

### SWC.F.1 Execution Control & Cancellation

**SWC.F.1** [Behavioral — Test: `Invariant_SWC_F_1_G_4_RebalanceCancellationBehavior`] Rebalance Execution **MUST be cancellation-safe** at all stages (before I/O, during I/O, before mutations).

- Deterministic termination: every started execution reaches terminal state
- No partial mutations: cache consistency maintained after cancellation
- Lifecycle integrity: accounting remains correct under cancellation
- `ThrowIfCancellationRequested()` at multiple checkpoints in execution pipeline

**SWC.F.1a** [Architectural] Rebalance Execution **MUST yield** to User Path requests immediately upon cancellation.

- Background operations check cancellation signals; must abort promptly when cancelled

**SWC.F.1b** [Behavioral — Covered by `Invariant_SWC_B_5`] Partially executed or cancelled Rebalance Execution **MUST NOT leave cache in inconsistent state**.

### SWC.F.2 Cache Mutation Rules (Rebalance Execution)

**SWC.F.2** [Architectural] The Rebalance Execution Path is the **ONLY component that mutates cache state** (single-writer architecture).

- Exclusive mutation authority: `Cache`, `IsInitialized`, `NoRebalanceRange`
- All other components are read-only

**SWC.F.2a** [Behavioral — Test: `Invariant_SWC_F_2a_RebalanceNormalizesCache`] Rebalance Execution mutates cache for normalization using **delivered data from intent as authoritative base**:

- Uses delivered data from intent (not current cache) as starting point
- Expands to `DesiredCacheRange` by fetching only truly missing ranges
- Trims excess data outside `DesiredCacheRange`
- Writes to cache via `Cache.Rematerialize()` (atomic reference swap)
- Sets `IsInitialized = true` after successful rebalance
- Recomputes `NoRebalanceRange` based on final cache range

**SWC.F.3** [Architectural] Rebalance Execution may **replace, expand, or shrink cache data** to achieve normalization.

**SWC.F.4** [Architectural] Rebalance Execution requests data from `IDataSource` **only for missing subranges**.

**SWC.F.5** [Architectural] Rebalance Execution **does not overwrite existing data** that intersects with `DesiredCacheRange`.

- Existing cached data is preserved during rebalance; new data merged with existing

### SWC.F.3 Post-Execution Guarantees

**SWC.F.6** [Behavioral — Test: `Invariant_SWC_F_6_F_7_F_8_PostExecutionGuarantees`] Upon successful completion, `CacheData` **strictly corresponds to `DesiredCacheRange`**.

**SWC.F.7** [Behavioral — Covered by same test as SWC.F.6] Upon successful completion, `CurrentCacheRange == DesiredCacheRange`.

**SWC.F.8** [Conceptual — Covered by same test as SWC.F.6] Upon successful completion, `NoRebalanceRange` is **recomputed** based on the final cache range.

---

## SWC.G. Execution Context & Scheduling Invariants

**SWC.G.1** [Behavioral — Test: `Invariant_SWC_G_1_G_2_G_3_ExecutionContextSeparation`] The User Path operates in the **user execution context**.

- Request completes quickly without waiting for background work

**SWC.G.2** [Architectural — Covered by same test as SWC.G.1] The Rebalance Decision Path and Rebalance Execution Path **execute outside the user execution context**.

- Fire-and-forget pattern: User request publishes work and returns
- No user blocking: Background work proceeds independently

**SWC.G.3** [Architectural — Covered by same test as SWC.G.1] I/O responsibilities are **separated between User Path and Rebalance Execution Path**.

- **User Path** MAY call `IDataSource.FetchAsync` exclusively to serve the user's immediate `RequestedRange` (cold start, full miss/jump). This I/O is unavoidable.
- **Rebalance Execution Path** calls `IDataSource.FetchAsync` exclusively for background cache normalization (expanding or rebuilding beyond the requested range).
- User Path I/O is bounded by the requested range; Rebalance I/O is bounded by cache geometry policy. Responsibilities never overlap.

**SWC.G.4** [Behavioral — Tests: `Invariant_SWC_G_4_UserCancellationDuringFetch`, `Invariant_SWC_F_1_G_4_RebalanceCancellationBehavior`] Cancellation **must be supported** for all scenarios.

- System does NOT guarantee cancellation on every new request. Cancellation MAY occur depending on Decision Engine scheduling validation.

**SWC.G.5** [Architectural] `IDataSource.FetchAsync` **MUST respect boundary semantics**: it may return a range smaller than requested (or null) for bounded data sources, and the cache must propagate this truncated result correctly.

- `IDataSource.FetchAsync` returns `RangeData<TRange, TData>?` — nullable to signal unavailability
- A non-null result MAY have a smaller range than requested (partial fulfillment)
- The cache MUST use the actual returned range, not the requested range

See `docs/sliding-window/boundary-handling.md` for details.

---

## SWC.I. Runtime Options Update Invariants

**SWC.I.1** [Behavioral — Tests: `RuntimeOptionsUpdateTests`] `UpdateRuntimeOptions` **validates the merged options** before publishing. Invalid updates throw and leave the current options unchanged.

**SWC.I.2** [Architectural] `UpdateRuntimeOptions` uses **next-cycle semantics**: the new options snapshot takes effect on the next rebalance decision/execution cycle.

- `RuntimeCacheOptionsHolder.Update` performs `Volatile.Write` (release fence)
- Planners and execution controllers snapshot `holder.Current` once at cycle start
- No running cycle is interrupted mid-flight by an options update

**Rationale:** Prevents mid-cycle inconsistencies (e.g., a planner using new `LeftCacheSize` with old `RightCacheSize`).

**SWC.I.3** [Architectural] `UpdateRuntimeOptions` on a disposed cache **always throws `ObjectDisposedException`**.

**SWC.I.4** [Conceptual] **`ReadMode` and `RebalanceQueueCapacity` are creation-time only** — they determine the storage strategy and execution controller strategy, which are wired at construction and cannot be changed without reconstruction.

---

## Summary

52 SlidingWindow-specific invariants across groups SWC.A–SWC.I:

- **Behavioral** (test-covered): 21 invariants
- **Architectural** (structure-enforced): 22 invariants
- **Conceptual** (design-level): 9 invariants

Shared invariants (S.H, S.J) are in `docs/shared/invariants.md`.

---

## See Also

- `docs/shared/invariants.md` — shared invariant groups S.H (activity tracking) and S.J (disposal)
- `docs/sliding-window/architecture.md` — architecture and coordination model
- `docs/sliding-window/scenarios.md` — temporal scenario walkthroughs
- `docs/sliding-window/storage-strategies.md` — SWC.A.4 conditional compliance details
- `docs/sliding-window/boundary-handling.md` — SWC.A.10a, SWC.G.5 boundary contract details
- `docs/sliding-window/components/overview.md` — component catalog
