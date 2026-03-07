# Architecture — SlidingWindowCache

SlidingWindow-specific architectural details. Shared foundations (single-writer, intent model, decision-driven execution, `AsyncActivityCounter`, work scheduler abstraction, disposal pattern, layered cache concept) are documented in `docs/shared/architecture.md`.

---

## Overview

`SlidingWindowCache` is a range-based cache optimized for sequential access. It models **one observer moving through data** — a user scrolling, a playback cursor advancing, a time-series viewport sliding. The cache continuously adapts a contiguous window around the current access position, prefetching ahead and trimming behind asynchronously.

The library spans two NuGet packages:

- **`Intervals.NET.Caching`** — shared contracts and infrastructure: `IRangeCache`, `IDataSource`, `RangeResult`, `RangeChunk`, `CacheInteraction`, `LayeredRangeCache`, `RangeCacheDataSourceAdapter`, `LayeredRangeCacheBuilder`, `GetDataAndWaitForIdleAsync`.
- **`Intervals.NET.Caching.SlidingWindow`** — sliding-window implementation: `SlidingWindowCache`, `ISlidingWindowCache`, `SlidingWindowCacheOptions`, `SlidingWindowCacheBuilder`, `GetDataAndWaitOnMissAsync`.

---

## Sliding Window Geometry

The cache maintains a single contiguous range of cached data, centered (or biased) around the last accessed position. The window has two configurable sides:

- **Left cache size** (`LeftCacheSize`): how much data to buffer behind the current access position.
- **Right cache size** (`RightCacheSize`): how much data to prefetch ahead of the current access position.

When the cache converges, the cached range is approximately:

```
[accessPosition - (requestSize × LeftCacheSize),
 accessPosition + (requestSize × RightCacheSize)]
```

The `ProportionalRangePlanner` computes the desired range proportional to the requested range's length. The `NoRebalanceRangePlanner` computes the stability zone — the inner region within the cached range where no rebalance is needed even if the desired range changes slightly.

**Cache contiguity invariant:** No gaps are ever allowed in the cached range. The cache always covers a single contiguous interval. See `docs/sliding-window/invariants.md` group B.

---

## Threading Model

Three execution contexts:

1. **User Thread (User Path)**
   - Serves `GetDataAsync` calls.
   - Reads from `CacheState` (read-only) or calls `IDataSource` for missing data.
   - Publishes an intent and returns immediately — does not wait for rebalancing.

2. **Background Intent Loop (Decision Path)**
   - Processes the latest published intent (latest wins via `Interlocked.Exchange`).
   - Runs the `RebalanceDecisionEngine` analytical pipeline (CPU-only).
   - If rebalance is needed: cancels prior execution request and publishes new one to the work scheduler.
   - If rebalance is not needed: discards intent and decrements activity counter.

3. **Background Execution (Execution Path)**
   - Applies debounce delay (cancellable).
   - Fetches missing data via `IDataSource` (async I/O).
   - Performs cache normalization (trim to desired range).
   - Mutates `CacheState` (single writer: this is the only context that writes).

The user thread ends at `PublishIntent()` return. All analytical and I/O work happens in contexts 2 and 3. See `docs/shared/architecture.md` for the general single-writer and user-path-never-blocks principles.

---

## Single-Writer Details (SWC-Specific)

**Write Ownership:** Only `RebalanceExecutor` may write to `CacheState` fields:
- Cache data and range (via `Cache.Rematerialize()` — atomic reference swap)
- `IsInitialized` (via `internal set` — restricted to rebalance execution)
- `NoRebalanceRange` (via `internal set` — restricted to rebalance execution)

**Read Safety:** User Path reads `CacheState` without locks because:
- User Path never writes to `CacheState` (architectural invariant)
- `Cache.Rematerialize()` performs atomic reference assignment
- Reference reads are atomic on all supported platforms
- No partial states are ever visible — the reader always sees the old complete state or the new complete state

Thread-safety is achieved through architectural constraints (single-writer) and coordination (cancellation), not locks on `CacheState` fields.

---

## Execution Serialization

Two layers enforce that only one rebalance execution writes cache state at a time:

1. **Work Scheduler Layer** (`IWorkScheduler<ExecutionRequest>`): serializes scheduling via task chaining or bounded channel. See `docs/shared/components/infrastructure.md`.
2. **Executor Layer**: `RebalanceExecutor` uses `SemaphoreSlim(1, 1)` for mutual exclusion during cache mutations.

**Execution Controller Strategies (configured via `SlidingWindowCacheOptions.RebalanceQueueCapacity`):**

| Strategy | Configuration | Mechanism | Backpressure | Use Case |
|---|---|---|---|---|
| Task-based (default) | `rebalanceQueueCapacity: null` | Lock-free task chaining | None | Recommended for most scenarios |
| Channel-based | `rebalanceQueueCapacity: >= 1` | Bounded channel | Async await on `WriteAsync` when full | High-frequency or resource-constrained |

**Why both CTS and SemaphoreSlim:**
- **CTS**: Cooperative cancellation signaling (intent obsolescence, user cancellation)
- **SemaphoreSlim**: Mutual exclusion for cache writes (prevents concurrent execution)
- Together: CTS signals "don't do this work anymore"; semaphore enforces "only one at a time"

---

## Decision-Driven Execution (SWC Pipeline)

The `RebalanceDecisionEngine` runs a multi-stage analytical pipeline (CPU-only, side-effect free) before any execution is scheduled:

| Stage | Check | On Rejection |
|---|---|---|
| 1 | Request falls within `CurrentNoRebalanceRange` | Skip — fast path, no rebalance needed |
| 2 | Request falls within pending `DesiredNoRebalanceRange` (from last work item) | Skip — thrashing prevention |
| 3 | Compute `DesiredCacheRange` + `DesiredNoRebalanceRange` via `ProportionalRangePlanner` / `NoRebalanceRangePlanner` | — |
| 4 | `DesiredCacheRange == CurrentCacheRange` | Skip — already optimal |
| 5 | Schedule rebalance execution | — |

Work avoidance: execution is scheduled only when all validation stages confirm necessity. See `docs/sliding-window/invariants.md` group D for formal invariants.

---

## Runtime-Updatable Options

A subset of configuration can be changed on a live cache instance without reconstruction via `ISlidingWindowCache.UpdateRuntimeOptions`:

- `LeftCacheSize`, `RightCacheSize`
- `LeftThreshold`, `RightThreshold`
- `DebounceDelay`

**Non-updatable:** `ReadMode` (materialization strategy) and `RebalanceQueueCapacity` (execution controller selection) are determined at construction and cannot be changed.

**Mechanism:** `SlidingWindowCache` constructs a `RuntimeCacheOptionsHolder` from `SlidingWindowCacheOptions`. The holder is shared by reference with `ProportionalRangePlanner`, `NoRebalanceRangePlanner`, and the work scheduler. `UpdateRuntimeOptions` validates and publishes the new snapshot via `Volatile.Write`. All readers call `holder.Current` at the start of their operation.

**"Next cycle" semantics:** Changes take effect on the next rebalance decision/execution cycle. Ongoing cycles use the snapshot they already captured.

---

## Smart Eventual Consistency Model

Cache state converges to optimal configuration asynchronously:

1. User Path returns correct data immediately (from cache or `IDataSource`) and classifies as `FullHit`, `PartialHit`, or `FullMiss` via `RangeResult.CacheInteraction`
2. User Path publishes intent with delivered data (synchronous, atomic — lightweight signal only)
3. Intent loop wakes on semaphore signal, reads latest intent via `Interlocked.Exchange`
4. `RebalanceDecisionEngine` validates necessity (CPU-only, background)
5. Work avoidance: rebalance skipped if validation rejects (Stage 1–4)
6. If execution required: cancels prior request, publishes new `ExecutionRequest` to work scheduler
7. Debounce delay → rebalance I/O → cache mutation (single writer)

**Key insight:** User always receives correct data, regardless of whether the cache has converged to the optimal window.

---

## Consistency Modes

Three opt-in consistency modes layer on top of eventual consistency:

| Mode | Method | Waits for idle? | When to use |
|---|---|---|---|
| Eventual (default) | `GetDataAsync` | Never | Normal operation |
| Hybrid | `GetDataAndWaitOnMissAsync` | Only on `PartialHit` or `FullMiss` | Warm-cache guarantee without always paying idle-wait cost |
| Strong | `GetDataAndWaitForIdleAsync` | Always | Cold-start synchronization, integration tests |

**Serialized access requirement for Hybrid/Strong:** Both methods provide their convergence guarantee only under serialized (one-at-a-time) access. Under parallel access the guarantee degrades gracefully (no deadlocks or data corruption) but may return before convergence is complete. See `docs/sliding-window/components/public-api.md` for usage details.

---

## Single Cache Instance = Single Consumer

A sliding window cache models one observer moving through data. Each cache instance represents one user, one access trajectory, one temporal sequence of requests.

**Why this is a requirement:**
1. **Unified access pattern**: `DesiredCacheRange` is computed from a single access trajectory. Multiple consumers produce conflicting trajectories — there is no single meaningful desired range.
2. **Single timeline**: Rebalance logic depends on ordered intents from a single sequence of access events. Multiple consumers introduce conflicting timelines.

**For multi-user environments:** Create one cache instance per logical consumer:

```csharp
// Each consumer gets its own independent cache instance
var userACache = new SlidingWindowCache<int, byte[], IntDomain>(dataSource, options);
var userBCache = new SlidingWindowCache<int, byte[], IntDomain>(dataSource, options);
```

Do not share a cache instance across users or synchronize external access — external synchronization does not solve the underlying model conflict.

---

## Disposal Architecture

`SlidingWindowCache` implements `IAsyncDisposable`. Disposal uses a three-state, lock-free pattern:

```
0 = Active → 1 = Disposing → 2 = Disposed

Transitions:
  0→1: First DisposeAsync() call wins via Interlocked.CompareExchange
  1→2: Disposal completes

Concurrent calls:
  First (0→1): Performs actual disposal
  Concurrent (1): Spin-wait until state reaches 2
  Subsequent (2): Return immediately (idempotent)
```

**Disposal sequence:**
```
SlidingWindowCache.DisposeAsync()
  └─> UserRequestHandler.DisposeAsync()
      └─> IntentController.DisposeAsync()
          ├─> Cancel intent processing loop (CancellationTokenSource)
          ├─> Wait for intent loop to exit
          ├─> IWorkScheduler.DisposeAsync()
          │   ├─> Task-based: await task chain
          │   └─> Channel-based: Complete channel writer + await loop
          └─> Dispose coordination resources (SemaphoreSlim, CTS)
```

Post-disposal: all public methods throw `ObjectDisposedException` (checked via `Volatile.Read` before any work).

See `docs/shared/invariants.md` group J for formal disposal invariants.

---

## Multi-Layer Caches

Multiple `SlidingWindowCache` instances can be stacked into a cache pipeline. The outermost layer is user-facing (small, fast window); inner layers provide progressively larger buffers to amortize data-source latency.

Three public types in `Intervals.NET.Caching` support this:

- **`RangeCacheDataSourceAdapter`** — adapts any `IRangeCache` as an `IDataSource`
- **`LayeredRangeCacheBuilder`** — fluent builder that wires layers and returns a `LayeredRangeCache` (obtainable via `SlidingWindowCacheBuilder.Layered(...)`)
- **`LayeredRangeCache`** — thin `IRangeCache` wrapper; delegates `GetDataAsync` to outermost layer; awaits all layers outermost-first on `WaitForIdleAsync`

### Key Properties

- Each layer is an independent `SlidingWindowCache` — no shared state between layers.
- Data flows inward on miss (outer layer fetches from inner layer's `GetDataAsync`), outward on return.
- `WaitForIdleAsync` on `LayeredRangeCache` awaits outermost layer first, then inner layers, ensuring full-stack convergence.
- `LayeredRangeCache` implements `IRangeCache` only — `UpdateRuntimeOptions` and `CurrentRuntimeOptions` are not available directly; access individual layers via `LayeredRangeCache.Layers`.

### Cascading Rebalance

When L1 rebalances and its desired range extends beyond L2's current window, L1 calls L2's `GetDataAsync` for the missing ranges. Each `GetDataAsync` call publishes a rebalance intent on L2. Under "latest wins" semantics, at most one L2 rebalance is triggered per L1 rebalance burst.

**Natural mitigations:** latest-wins intent supersession; debounce delay; Decision Engine Stage 1 fast-path rejection when L2's `NoRebalanceRange` already covers L1's desired range (the desired steady-state with correct configuration).

**Configuration requirement:** L2's buffer size should be 5–10× L1's to ensure L1's `DesiredCacheRange` typically falls within L2's `NoRebalanceRange`, making Stage 1 rejection the norm.

| Layer | `leftCacheSize` / `rightCacheSize` | `leftThreshold` / `rightThreshold` |
|---|---|---|
| L1 (outermost) | 0.3–1.0× | 0.1–0.2 |
| L2 (inner) | 5–10× L1's buffer | 0.2–0.3 |
| L3+ (deeper) | 3–5× the layer above | 0.2–0.3 |

**Anti-pattern:** L2 buffer too close to L1's size — L2 must re-center on every L1 rebalance, providing no meaningful buffering benefit. Symptom: `l2.RebalanceExecutionCompleted` count approaches `l1.RebalanceExecutionCompleted`.

---

## See Also

- `docs/shared/architecture.md` — shared principles (single-writer, user-path-never-blocks, intent model, etc.)
- `docs/sliding-window/invariants.md` — formal invariant groups A–I
- `docs/sliding-window/state-machine.md` — state machine specification
- `docs/sliding-window/storage-strategies.md` — Snapshot vs CopyOnRead trade-offs
- `docs/sliding-window/scenarios.md` — temporal scenario walkthroughs including layered scenarios
- `docs/shared/components/infrastructure.md` — `AsyncActivityCounter` and work schedulers
- `docs/sliding-window/components/overview.md` — component catalog
