# Scenarios

## Overview

This document describes the temporal behavior of SlidingWindowCache: what happens over time when user requests occur, decisions are evaluated, and background executions run.

## Motivation

Component maps describe "what exists"; scenarios describe "what happens". Scenarios are the fastest way to debug behavior because they connect public API calls to background convergence.

## Base Definitions

The following terms are used consistently across all scenarios:

- **RequestedRange** — A range requested by the user.
- **IsInitialized** — Whether the cache has been initialized (Rebalance Execution has written to the cache at least once).
- **CurrentCacheRange** — The range of data currently stored in the cache.
- **CacheData** — The data corresponding to `CurrentCacheRange`.
- **DesiredCacheRange** — The target cache range computed from `RequestedRange` and cache configuration (left/right expansion sizes, thresholds).
- **NoRebalanceRange** — A range inside which cache rebalance is not required (stability zone).
- **IDataSource** — A sequential, range-based data source.

Canonical definitions: `docs/glossary.md`.

## Design

Scenarios are grouped by path:

1. **User Path** (user thread)
2. **Decision Path** (background intent loop)
3. **Execution Path** (background execution)

---

## I. User Path Scenarios

### U1 — Cold Cache Request

**Preconditions**:
- `IsInitialized == false`
- `CurrentCacheRange == null`
- `CacheData == null`

**Action Sequence**:
1. User requests `RequestedRange`
2. Cache detects it is not initialized
3. Cache requests `RequestedRange` from `IDataSource` in the user thread (unavoidable — user request must be served immediately)
4. A rebalance intent is published (fire-and-forget) with the fetched data
5. Data is returned to the user immediately
6. Rebalance Execution (background) stores the data as `CacheData`, sets `CurrentCacheRange = RequestedRange`, sets `IsInitialized = true`

**Note**: The User Path does not expand the cache beyond `RequestedRange`. Cache expansion to `DesiredCacheRange` is performed exclusively by Rebalance Execution.

---

### U2 — Full Cache Hit (Within NoRebalanceRange)

**Preconditions**:
- `IsInitialized == true`
- `CurrentCacheRange.Contains(RequestedRange) == true`
- `NoRebalanceRange.Contains(RequestedRange) == true`

**Action Sequence**:
1. User requests `RequestedRange`
2. Cache detects a full cache hit
3. Data is read from `CacheData`
4. Rebalance intent is published; Decision Engine rejects execution at Stage 1 (NoRebalanceRange containment)
5. Data is returned to the user

---

### U3 — Full Cache Hit (Outside NoRebalanceRange)

**Preconditions**:
- `IsInitialized == true`
- `CurrentCacheRange.Contains(RequestedRange) == true`
- `NoRebalanceRange.Contains(RequestedRange) == false`

**Action Sequence**:
1. User requests `RequestedRange`
2. Cache detects all requested data is available
3. Subrange is read from `CacheData`
4. Rebalance intent is published; Decision Engine proceeds through validation
5. Data is returned to the user
6. Rebalance executes asynchronously to shift the window

---

### U4 — Partial Cache Hit

**Preconditions**:
- `IsInitialized == true`
- `CurrentCacheRange.Intersects(RequestedRange) == true`
- `CurrentCacheRange.Contains(RequestedRange) == false`

**Action Sequence**:
1. User requests `RequestedRange`
2. Cache computes intersection with `CurrentCacheRange`
3. Missing part is synchronously requested from `IDataSource`
4. Cache:
   - merges cached and newly fetched data **locally** (in-memory assembly, not stored to cache)
   - does **not** trim excess data
   - does **not** update `CurrentCacheRange` (User Path is read-only with respect to cache state)
5. Rebalance intent is published; rebalance executes asynchronously
6. `RequestedRange` data is returned to the user

**Note**: Cache expansion is permitted because `RequestedRange` intersects `CurrentCacheRange`, preserving cache contiguity. Excess data may temporarily remain in `CacheData` for reuse during Rebalance.

---

### U5 — Full Cache Miss (Jump)

**Preconditions**:
- `IsInitialized == true`
- `CurrentCacheRange.Intersects(RequestedRange) == false`

**Action Sequence**:
1. User requests `RequestedRange`
2. Cache determines that `RequestedRange` does NOT intersect `CurrentCacheRange`
3. **Cache contiguity enforcement**: Cached data cannot be preserved — merging would create gaps
4. `RequestedRange` is synchronously requested from `IDataSource`
5. Cache:
   - **fully replaces** `CacheData` with new data
   - **fully replaces** `CurrentCacheRange` with `RequestedRange`
6. Rebalance intent is published; rebalance executes asynchronously
7. Data is returned to the user

**Critical**: Partial cache expansion is FORBIDDEN in this case — it would create logical gaps and violate the Cache Contiguity Rule (Invariant A.9a). The cache MUST remain contiguous at all times.

---

## II. Decision Path Scenarios

**Core principle**: Rebalance necessity is determined by multi-stage analytical validation, not by intent existence. Publishing an intent does NOT guarantee execution. The Decision Engine is the sole authority for necessity determination.

The validation pipeline:
1. **Stage 1**: Current Cache `NoRebalanceRange` validation (fast-path rejection)
2. **Stage 2**: Pending Desired Cache `NoRebalanceRange` validation (anti-thrashing)
3. **Stage 3**: Compute `DesiredCacheRange` from `RequestedRange` + configuration
4. **Stage 4**: `DesiredCacheRange` vs `CurrentCacheRange` equality check (no-op prevention)

Execution occurs **only if ALL validation stages confirm necessity**.

---

### D1 — Rebalance Blocked by NoRebalanceRange (Stage 1)

**Condition**:
- `NoRebalanceRange.Contains(RequestedRange) == true`

**Sequence**:
1. Intent arrives; Stage 1 validation begins
2. `NoRebalanceRange` computed from `CurrentCacheRange` is checked
3. `RequestedRange` is fully contained within `NoRebalanceRange`
4. Validation rejects: current cache provides sufficient buffer
5. Fast return — rebalance is skipped; Execution Path is not started

**Rationale**: Current cache already provides adequate coverage around the requested range. No I/O or cache mutation needed.

---

### D1b — Rebalance Blocked by Pending Desired Cache (Stage 2, Anti-Thrashing)

**Condition**:
- Stage 1 passed: `NoRebalanceRange(CurrentCacheRange).Contains(RequestedRange) == false`
- Pending rebalance exists with `PendingDesiredCacheRange`
- `NoRebalanceRange(PendingDesiredCacheRange).Contains(RequestedRange) == true`

**Sequence**:
1. Intent arrives; Stage 1 passes
2. Stage 2: pending rebalance exists — compute `NoRebalanceRange` from `PendingDesiredCacheRange`
3. `RequestedRange` is fully contained within pending `NoRebalanceRange`
4. Validation rejects: pending execution will already satisfy this request
5. Fast return — existing pending rebalance continues undisturbed

**Purpose**: Anti-thrashing mechanism preventing oscillating cache geometry.

**Rationale**: A rebalance is already scheduled that will position the cache optimally for this request. Starting a new rebalance would cancel the pending one, potentially causing thrashing. Better to let the pending rebalance complete.

---

### D2 — Rebalance Blocked by No-Op Geometry (Stage 4)

**Condition**:
- Stage 1 passed: `NoRebalanceRange.Contains(RequestedRange) == false`
- `DesiredCacheRange == CurrentCacheRange`

**Sequence**:
1. Intent arrives; Stages 1–3 pass
2. Stage 3: `DesiredCacheRange` is computed from `RequestedRange` + config
3. Stage 4: `DesiredCacheRange == CurrentCacheRange` — cache already in optimal configuration
4. Validation rejects: no geometry change needed
5. Fast return — rebalance is skipped; Execution Path is not started

**Rationale**: Cache is already sized and positioned optimally. No I/O or cache mutation needed.

---

### D3 — Rebalance Required (All Validation Stages Passed)

**Condition**:
- Stage 1 passed: `NoRebalanceRange.Contains(RequestedRange) == false`
- Stage 2 passed (if applicable): Pending coverage does not satisfy request
- Stage 4 passed: `DesiredCacheRange != CurrentCacheRange`

**Sequence**:
1. Intent arrives; all validation stages pass
2. Stage 3: `DesiredCacheRange` computed
3. Stage 4 confirms: cache geometry change required
4. Validation confirms necessity
5. Prior pending execution is cancelled (if any)
6. New execution is scheduled

**Rationale**: ALL validation stages confirm that cache requires rebalancing. Rebalance Execution will normalize cache to `DesiredCacheRange` using delivered data as authoritative source.

---

## III. Execution Path Scenarios

### R1 — Build from Scratch

**Preconditions**:
- `CurrentCacheRange == null`

OR:
- `DesiredCacheRange.Intersects(CurrentCacheRange) == false`

**Sequence**:
1. `DesiredCacheRange` is requested from `IDataSource`
2. `CacheData` is fully replaced
3. `CurrentCacheRange` is set to `DesiredCacheRange`
4. `NoRebalanceRange` is computed

---

### R2 — Expand Cache (Partial Overlap)

**Preconditions**:
- `DesiredCacheRange.Intersects(CurrentCacheRange) == true`
- `DesiredCacheRange != CurrentCacheRange`

**Sequence**:
1. Missing subranges are computed (`DesiredCacheRange \ CurrentCacheRange`)
2. Missing data is requested from `IDataSource`
3. Data is merged with existing `CacheData`
4. `CacheData` is normalized to `DesiredCacheRange`
5. `NoRebalanceRange` is updated

---

### R3 — Shrink / Normalize Cache

**Preconditions**:
- `CurrentCacheRange.Contains(DesiredCacheRange) == true`

**Sequence**:
1. `CacheData` is trimmed to `DesiredCacheRange`
2. `CurrentCacheRange` is updated
3. `NoRebalanceRange` is recomputed

---

## IV. Concurrency and Cancellation Scenarios

### Concurrency Principles

1. User Path is never blocked by rebalance logic.
2. Multiple rebalance triggers may overlap in time.
3. Only the **latest validated rebalance intent** is executed.
4. Obsolete rebalance work must be cancelled or abandoned.
5. Rebalance execution must support cancellation at all stages.
6. Cache state may be temporarily non-optimal but must always be consistent.

---

### C1 — New Request While Rebalance Is Pending

**Situation**:
- User request U₁ triggers rebalance R₁ (fire-and-forget)
- R₁ has not started execution yet (queued or debouncing)
- User request U₂ arrives before R₁ executes

**Expected Behavior**:
1. New intent from U₂ supersedes R₁; Decision Engine validates necessity
2. User Path for U₂ executes normally and immediately
3. If validation confirms: R₁ is cancelled; new rebalance R₂ is scheduled
4. If validation rejects: R₁ continues (anti-thrashing, Stage 2 validation)
5. Only R₂ is allowed to execute (if scheduled)

**Outcome**: No rebalance work executes based on outdated intent. User Path always has priority.

---

### C2 — New Request While Rebalance Is Executing

**Situation**:
- User request U₁ triggers rebalance R₁
- R₁ has already started execution (I/O or merge in progress)
- User request U₂ arrives and triggers rebalance R₂

**Expected Behavior**:
1. New intent from U₂ supersedes R₁; Decision Engine validates necessity
2. User Path for U₂ executes normally and immediately
3. If validation confirms: R₁ receives cancellation signal
4. R₁ stops as early as possible or completes but discards its results
5. R₂ proceeds with fresh `DesiredCacheRange`

**Outcome**: Cache normalization reflects the most recent validated access pattern. User Path and Rebalance Execution never mutate cache concurrently.

---

### C3 — Multiple Rapid User Requests (Spike)

**Situation**:
- User produces a burst of requests: U₁, U₂, U₃, ..., Uₙ
- Each request publishes an intent; rebalance execution cannot keep up

**Expected Behavior**:
1. User Path serves all requests independently
2. Intents are superseded ("latest wins")
3. At most one rebalance execution is active at any time
4. Only the final validated intent is executed
5. All intermediate rebalance work is cancelled or skipped via decision validation

**Outcome**: System remains responsive and converges to a stable cache state once user activity slows.

---

### Cancellation and State Safety Guarantees

For concurrency correctness, the following guarantees hold:

- Rebalance execution is cancellable at all stages (before I/O, after I/O, before mutation)
- Cache mutations are atomic — no partial state is ever visible
- Partial rebalance results must not corrupt cache state (cancelled execution discards results)
- Final rebalance always produces a fully normalized, consistent cache

Temporary non-optimal cache geometry is acceptable. Permanent inconsistency is not.

---

## V. Multi-Layer Cache Scenarios

These scenarios describe the temporal behavior when `LayeredWindowCacheBuilder` is used to
create a cache stack of two or more `WindowCache` layers.

**Notation:** L1 = outermost (user-facing) layer; L2 = next inner layer; Lₙ = innermost layer
(directly above the real `IDataSource`). Data requests flow L1 → L2 → ... → Lₙ → data source;
data returns in reverse order.

---

### L1 — Cold Start (All Layers Uninitialized)

**Preconditions:**
- All layers uninitialized (`IsInitialized == false` at every layer)

**Action Sequence:**
1. User calls `GetDataAsync(range)` on `LayeredWindowCache` → delegates to L1
2. L1 (cold): calls `FetchAsync(range)` on the adapter → calls L2's `GetDataAsync(range)`
3. L2 (cold): calls `FetchAsync(range)` on the adapter → continues inward until Lₙ
4. Lₙ (cold): fetches `range` from the real `IDataSource`; returns data; publishes intent
5. Each inner layer returns data upward, each publishes its own rebalance intent (fire-and-forget)
6. L1 receives data from L2 adapter; publishes its own intent; returns data to user
7. In the background, each layer independently rebalances to its configured `DesiredCacheRange`

**Key insight:** The first user request traverses the full stack. Subsequent requests will be
served from whichever layer has the data in its window (L1 first, then L2, etc.).

---

### L2 — L1 Cache Hit (Outermost Layer Serves Request)

**Preconditions:**
- All layers initialized
- L1 `CurrentCacheRange.Contains(requestedRange) == true`

**Action Sequence:**
1. User calls `GetDataAsync(requestedRange)` → L1 has the data
2. L1 serves the request from its cache without contacting L2
3. L1 publishes an intent (fire-and-forget); Decision Engine evaluates whether L1 needs rebalancing
4. L2 and deeper layers are NOT contacted; they continue their own background rebalancing independently

**Key insight:** The outermost layer absorbs requests that fall within its window, providing the
lowest latency. Inner layers are only contacted on L1 misses.

---

### L3 — L1 Miss, L2 Hit (Outer Miss Delegates to Next Layer)

**Preconditions:**
- All layers initialized
- L1 does NOT have `requestedRange` in its window
- L2 `CurrentCacheRange.Contains(requestedRange) == true`

**Action Sequence:**
1. User calls `GetDataAsync(requestedRange)` → L1 misses
2. L1 calls `FetchAsync(requestedRange)` on the L2 adapter
3. L2 serves the request from its own cache; publishes its own rebalance intent
4. L2 adapter returns a `RangeChunk` to L1
5. L1 assembles and returns data to the user; publishes its rebalance intent
6. L1's background rebalance subsequently fetches the wider range from L2 (via adapter),
   expanding L1's window to cover similar future requests without contacting L2

**Key insight:** L2 acts as a warm prefetch buffer. L1 pays one adapter call on miss, then
rebalances to prevent the same miss on the next request.

---

### L4 — Full Stack Miss (Request Falls Outside All Layer Windows)

**Preconditions:**
- All layers initialized
- `requestedRange` falls outside every layer's current window (e.g., a large jump)

**Action Sequence:**
1. User calls `GetDataAsync(requestedRange)` → L1 misses
2. L1 adapter → L2 misses → ... → Lₙ misses → real `IDataSource` fetches data
3. Data flows back up the chain; each layer publishes its own rebalance intent
4. User receives data immediately; all layers' background rebalances cascade independently

**Note:** In a large jump, each layer's rebalance independently re-centers around the new region.
The stack converges from the inside out: Lₙ expands first (driving real I/O), then L(n-1) expands
from Lₙ's new window, and finally L1 expands from L2.

---

### L5 — Per-Layer Diagnostics Observation

**Setup:**
```csharp
var l2Diagnostics = new EventCounterCacheDiagnostics();
var l1Diagnostics = new EventCounterCacheDiagnostics();

await using var cache = LayeredWindowCacheBuilder<int, byte[], IntegerFixedStepDomain>
    .Create(dataSource, domain)
    .AddLayer(deepOptions,  l2Diagnostics)   // L2
    .AddLayer(userOptions,  l1Diagnostics)   // L1
    .Build();
```

**Observation pattern:**
- `l1Diagnostics.UserRequestFullCacheHit` — requests served entirely from L1
- `l2Diagnostics.UserRequestFullCacheHit` — requests L1 delegated to L2 that L2 served from cache
- `l2Diagnostics.DataSourceFetchSingleRange` — requests that reached the real data source
- `l1Diagnostics.RebalanceExecutionCompleted` — how often L1's window was re-centered

**Key insight:** Each layer has fully independent diagnostics. By comparing hit rates across
layers you can tune buffer sizes and thresholds for the access pattern in production.

---

### L6 — Cascading Rebalance (L1 Rebalance Triggers L2 Rebalance)

This scenario describes the internal mechanics of a cascading rebalance. Understanding it
is essential for correct layer configuration. See also `docs/architecture.md` (Cascading
Rebalance Behavior) and Scenario L7 for the anti-pattern case.

**Preconditions:**
- Both layers initialized
- User has scrolled forward enough that L1's `DesiredCacheRange` now extends **beyond** L2's
  `NoRebalanceRange` on at least one side (e.g., L2's buffers are too small relative to L1's)

**Action Sequence:**
1. User calls `GetDataAsync(range)` → L1 serves from cache; publishes rebalance intent
2. L1's Decision Engine confirms rebalance needed (range outside L1's `NoRebalanceRange`)
3. L1's rebalance computes: `AssembledRangeData = [100, 250]`, `DesiredCacheRange = [50, 300]`
4. Missing ranges: left gap `[50, 100)` and right gap `(250, 300]`
5. L1 calls `dataSource.FetchAsync({[50,100), (250,300]}, ct)` on the adapter
6. The adapter's default batch implementation dispatches **two parallel** `GetDataAsync` calls to L2
7. L2 serves both ranges from its cache (or its own data source); returns data to L1
8. **L2 publishes two rebalance intents** — one per `GetDataAsync` call (fire-and-forget)
9. L2's intent loop applies "latest wins" — one intent supersedes the other
10. L2's Decision Engine evaluates the surviving intent against L2's `NoRebalanceRange`

**Branch A — Cascading rebalance avoided (correct configuration):**
- The surviving range falls inside L2's `NoRebalanceRange` (Stage 1 rejection)
- L2 skips rebalance entirely — no I/O, no cache mutation
- This is the **desired steady-state**: L2's large buffer absorbed L1's fetch without reacting

**Branch B — Cascading rebalance occurs (buffer too small):**
- The surviving range falls outside L2's `NoRebalanceRange`
- L2 schedules its own background rebalance
- L2 re-centers toward the surviving intent range (one gap side, not the midpoint of L1's desired range)
- L2's `CurrentCacheRange` shifts — potentially leaving it poorly positioned for L1's next rebalance

**Key insight:** Whether Branch A or Branch B occurs is determined entirely by configuration.
Making L2's `leftCacheSize`/`rightCacheSize` 5–10× larger than L1's, and using
`leftThreshold`/`rightThreshold` of 0.2–0.3, makes Branch A the norm.

---

### L7 — Anti-Pattern: Cascading Rebalance Thrashing

This scenario describes the failure mode when inner layer buffers are too close in size to outer
layer buffers. Do not configure a layered cache this way.

**Configuration (wrong):**
```
L1: leftCacheSize=1.0, rightCacheSize=1.0, leftThreshold=0.1, rightThreshold=0.1
L2: leftCacheSize=1.5, rightCacheSize=1.5, leftThreshold=0.1, rightThreshold=0.1
```
L2's buffers are only 1.5× L1's — not nearly enough.

**Access pattern:** User scrolls sequentially, one step per second.

**What happens (step by step):**

1. **Step 1** — User requests `[100, 110]`
   - Cold start: both layers fetch from data source; L2 rebalances to `[0, 260]`; L1 rebalances to `[0, 220]`
   - Both layers converge around `[100, 110]`

2. **Step 2** — User requests `[200, 210]`
   - L1: `[200, 210]` is within L1's window → cache hit; L1 publishes intent; L1 rebalances to `[100, 310]`
   - L1's rebalance fetches right gap `(220, 310]` from L2 via adapter
   - L2: `(220, 310]` extends slightly beyond L2's `NoRebalanceRange` (L2 only has window to ~260)
   - L2 re-centers to `[110, 410]` — **L2 rebalanced unnecessarily**

3. **Step 3** — User requests `[300, 310]`
   - L1 rebalances to `[200, 410]`; fetches right gap `(310, 410]` from L2
   - L2: right gap at `(310, 410]` is near L2's new boundary → L2 rebalances again
   - L2 re-centers to `[210, 510]` — **L2 rebalanced again**

4. **Pattern repeats every scroll step** — L2's rebalance count tracks L1's rebalance count

**Observed symptoms:**
- `l2.RebalanceExecutionCompleted ≈ l1.RebalanceExecutionCompleted` (L2 rebalances as often as L1)
- `l2.DataSourceFetchMissingSegments` is high (L2 repeatedly fetches from the real data source)
- L2 provides no meaningful prefetch advantage over a single-layer cache
- Data source I/O is not reduced compared to using L1 alone

**Resolution:**
```
L2: leftCacheSize=8.0, rightCacheSize=8.0, leftThreshold=0.25, rightThreshold=0.25
```
With 8× buffers, L2's `DesiredCacheRange` spans `[100 - 800, 100 + 800]` after the first
rebalance. L1's subsequent `DesiredCacheRange` values (length ~300) remain well within L2's
`NoRebalanceRange` (L2's window shrunk by 25% thresholds on each side). L2's Decision Engine
rejects rebalance at Stage 1 for every normal sequential scroll step.

**Diagnostic check:** After resolving misconfiguration, `l2.RebalanceSkippedCurrentNoRebalanceRange`
should be much higher than `l2.RebalanceExecutionCompleted` during normal sequential access.

---

## Invariants

Scenarios must be consistent with:

- User Path invariants: `docs/invariants.md` (Section A)
- Decision Path invariants: `docs/invariants.md` (Section D)
- Execution invariants: `docs/invariants.md` (Section F)
- Cache state invariants: `docs/invariants.md` (Section B)

## Usage

Use scenarios as a debugging checklist:

1. What did the user call?
2. What was delivered?
3. What intent was published?
4. Did the decision validate execution? If not, which stage rejected?
5. Did execution run, debounce, and mutate atomically?
6. Was there a concurrent cancellation? Did the cache remain consistent?

## Examples

Diagnostics examples in `docs/diagnostics.md` show how to observe these scenario transitions in production.

## Edge Cases

- A cache can be "temporarily non-optimal"; eventual convergence is expected.
- `WaitForIdleAsync` indicates the system was idle at some point, not that it remains idle.
- In Scenario D1b, the pending rebalance may already be in execution; it continues undisturbed if validation confirms it will satisfy the new request.

## Limitations

- Scenarios are behavioral descriptions, not an exhaustive proof; invariants are the normative source.
