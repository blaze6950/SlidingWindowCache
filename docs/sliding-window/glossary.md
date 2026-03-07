# Glossary — SlidingWindowCache

Canonical definitions for SlidingWindow-specific terms. Shared terms (`IRangeCache`, `IDataSource`, `RangeResult`, `RangeChunk`, `CacheInteraction`, `AsyncActivityCounter`, `WaitForIdleAsync`, layered cache types, concurrency primitives) are defined in `docs/shared/glossary.md`.

---

## Packages

**Intervals.NET.Caching.SlidingWindow**
- NuGet package containing the sliding-window cache implementation: `SlidingWindowCache`, `ISlidingWindowCache`, `SlidingWindowCacheOptions`, `SlidingWindowCacheBuilder`, `GetDataAndWaitOnMissAsync`, `SlidingWindowCacheConsistencyExtensions`, `SlidingWindowLayerExtensions`.

---

## Window Geometry

**Window**
- The cached range maintained around the most recently accessed region, typically larger than the user's requested range. The window slides as the user's access position moves.

**Current Cache Range**
- The range currently held in the cache state (`CacheState.Cache.Range`).

**Desired Cache Range**
- The target range the cache would like to converge to, computed by `ProportionalRangePlanner` from `RequestedRange` and cache size configuration. The Decision Engine compares `DesiredCacheRange` to `CurrentCacheRange` to determine whether rebalance is needed.

**NoRebalanceRange**
- A stability zone derived from `CurrentCacheRange` by applying threshold percentages inward. If `RequestedRange ⊆ NoRebalanceRange`, the Decision Engine skips rebalance at Stage 1 (fast path).
- *Not* the same as `CurrentCacheRange` — it is a shrunk inner zone. The request may extend close to the cache boundary and still fall within `NoRebalanceRange`.

**Left Cache Size / Right Cache Size**
- Configuration multipliers (`SlidingWindowCacheOptions.LeftCacheSize` / `RightCacheSize`) controlling how much to buffer behind and ahead of the current access position, relative to the size of the requested range.

**Left Threshold / Right Threshold**
- Configuration values (`SlidingWindowCacheOptions.LeftThreshold` / `RightThreshold`) controlling the inward shrinkage used to derive `NoRebalanceRange` from `CurrentCacheRange`. When both are specified their sum must not exceed 1.0.

**Available Range**
- `Requested ∩ Current` — data that can be served immediately from the cache without a data-source call.

**Missing Range**
- `Requested \ Current` — data that must be fetched from `IDataSource` to serve the user's request.

---

## Architectural Concepts

**Intent**
- A signal published by the User Path after serving a request. It describes what was delivered (actual data) and what was requested so the background loop can evaluate whether rebalance is worthwhile.
- Intents are signals, not commands: publishing an intent does not guarantee rebalance will execute.

**Latest Intent Wins**
- The newest published intent supersedes older intents via `Interlocked.Exchange`. Intermediate intents may never be processed. This is the primary burst-resistance mechanism.

**Decision-Driven Execution**
- Rebalance work is gated by a multi-stage validation pipeline (5 stages). Decisions are CPU-only and may skip execution entirely. See `docs/sliding-window/invariants.md` group SWC.D.

**Work Avoidance**
- The system prefers skipping rebalance when analysis determines it is unnecessary: request within `NoRebalanceRange`, pending work already covers the request, desired range equals current range.

**Debounce**
- A deliberate delay (`DebounceDelay`) applied before executing rebalance. Bursts of intents settle during the delay so only the last relevant rebalance runs. Configured in `SlidingWindowCacheOptions`; updatable at runtime via `UpdateRuntimeOptions`.

**Normalization**
- The process of converging cached data and cached range to the desired state: fetch missing data, merge with existing, trim to `DesiredCacheRange`, then publish atomically via `Cache.Rematerialize()`.

**Rematerialization**
- Rebuilding the stored representation of cached data (e.g., allocating a new contiguous array in Snapshot mode) to apply a new cache range. Performed exclusively by `RebalanceExecutor`.

**Rebalance Path**
- Background processing: the intent processing loop (Decision Engine) and the execution loop (RebalanceExecutor) together.

---

## Consistency Modes

**Hybrid Consistency Mode**
- Opt-in mode provided by `GetDataAndWaitOnMissAsync` (extension method on `ISlidingWindowCache`, in `Intervals.NET.Caching.SlidingWindow`).
- Composes `GetDataAsync` with conditional `WaitForIdleAsync`: waits only when `CacheInteraction` is `PartialHit` or `FullMiss`; returns immediately on `FullHit`.
- Provides warm-cache-speed hot paths with convergence guarantees on cold or near-boundary requests.
- If `WaitForIdleAsync` throws `OperationCanceledException`, the already-obtained result is returned gracefully (degrades to eventual consistency for that call).
- Convergence guarantee holds only under serialized access. See `Serialized Access` below.

**GetDataAndWaitOnMissAsync**
- Extension method on `ISlidingWindowCache` (in `SlidingWindowCacheConsistencyExtensions`, `Intervals.NET.Caching.SlidingWindow`) implementing hybrid consistency mode.
- See `Hybrid Consistency Mode` above and `docs/sliding-window/components/public-api.md`.

**Serialized Access**
- An access pattern in which calls to a cache are issued one at a time (each call completes before the next begins).
- Required for `GetDataAndWaitOnMissAsync` and `GetDataAndWaitForIdleAsync` to provide their "cache has converged" guarantee.
- Under parallel access the extension methods remain safe (no deadlocks or data corruption) but the idle-wait may return early due to `AsyncActivityCounter`'s "was idle at some point" semantics (Invariant S.H.3). See `docs/shared/glossary.md` for `WaitForIdleAsync` semantics.

---

## Storage and Materialization

**UserCacheReadMode**
- Enum controlling how data is stored and served (materialization strategy): `Snapshot` or `CopyOnRead`. Configured in `SlidingWindowCacheOptions`; cannot be changed at runtime.

**Snapshot Mode**
- `UserCacheReadMode.Snapshot`. Stores cache data in an immutable contiguous array. Serves `ReadOnlyMemory<TData>` to callers without per-read allocation. Rebalance cost is higher (full array copy during rematerialization). Default for lock-free reads.

**CopyOnRead Mode**
- `UserCacheReadMode.CopyOnRead`. Stores cache data in a growable `List<TData>`. Serves data by copying into a new array on each read (per-read allocation). Rebalance cost is lower (in-place list manipulation). May use a short-lived lock during read. See `docs/sliding-window/storage-strategies.md` for trade-off details.

**Staging Buffer**
- A temporary buffer used during rebalance execution to assemble a new contiguous data representation before atomic publication via `Cache.Rematerialize()`. See `docs/sliding-window/storage-strategies.md`.

---

## Diagnostics

**ICacheDiagnostics**
- Optional instrumentation interface for observing user requests, decision outcomes, rebalance execution lifecycle, and failures. Implemented by `NoOpDiagnostics` (default), `EventCounterCacheDiagnostics`, or custom implementations. See `docs/sliding-window/diagnostics.md`.

**NoOpDiagnostics**
- Default `ICacheDiagnostics` implementation that does nothing. Designed to be effectively zero-overhead when no instrumentation is needed.

---

## Runtime Options

**UpdateRuntimeOptions**
- Method on `ISlidingWindowCache` that updates a subset of cache options on a live instance without reconstruction.
- Takes an `Action<RuntimeOptionsUpdateBuilder>` callback; only builder fields explicitly set are changed.
- Uses **next-cycle semantics**: changes take effect on the next rebalance decision/execution cycle.
- Throws `ObjectDisposedException` after disposal; throws `ArgumentOutOfRangeException` / `ArgumentException` for invalid values.
- `ReadMode` and `RebalanceQueueCapacity` are creation-time only; cannot be changed at runtime.
- Not available on `LayeredRangeCache` (implements `IRangeCache` only); obtain the target layer via `LayeredRangeCache.Layers` to update its options.

**RuntimeOptionsUpdateBuilder**
- Public fluent builder passed to the `UpdateRuntimeOptions` callback.
- Methods: `WithLeftCacheSize`, `WithRightCacheSize`, `WithLeftThreshold`, `ClearLeftThreshold`, `WithRightThreshold`, `ClearRightThreshold`, `WithDebounceDelay`.
- `ClearLeftThreshold` / `ClearRightThreshold` explicitly set threshold to `null`, distinguishing "don't change" from "set to null".
- Constructor is `internal`.

**RuntimeOptionsSnapshot**
- Public read-only DTO capturing the current values of the five runtime-updatable options at the moment the `CurrentRuntimeOptions` property was read.
- Immutable — subsequent `UpdateRuntimeOptions` calls do not affect previously obtained snapshots.
- Obtained via `ISlidingWindowCache.CurrentRuntimeOptions`. Constructor is `internal`.

**RuntimeCacheOptions** *(internal)*
- Internal immutable snapshot of the runtime-updatable configuration: `LeftCacheSize`, `RightCacheSize`, `LeftThreshold`, `RightThreshold`, `DebounceDelay`.
- Created from `SlidingWindowCacheOptions` at construction; republished on each `UpdateRuntimeOptions` call.
- Exposes `ToSnapshot()` → `RuntimeOptionsSnapshot`.

**RuntimeCacheOptionsHolder** *(internal)*
- Internal volatile wrapper holding the current `RuntimeCacheOptions` snapshot.
- Readers call `holder.Current` at invocation time — always see the latest published snapshot.
- `Update(RuntimeCacheOptions)` publishes atomically via `Volatile.Write`.

**RuntimeOptionsValidator** *(internal)*
- Internal static helper containing shared validation logic for sizes and thresholds.
- Used by both `SlidingWindowCacheOptions` and `RuntimeCacheOptions` to avoid duplicated validation rules.

---

## Multi-Layer Caches (SWC-Specific Terms)

**Cascading Rebalance**
- When L1's rebalance fetches missing ranges from L2 via `GetDataAsync`, each fetch publishes a rebalance intent on L2. If those ranges fall outside L2's `NoRebalanceRange`, L2 schedules its own rebalance. Under correct configuration (L2 buffer 5–10× L1's), the Decision Engine rejects at Stage 1 — steady state. Under misconfiguration it becomes continuous. See `docs/sliding-window/architecture.md` and `docs/sliding-window/scenarios.md`.

**Cascading Rebalance Thrashing**
- Failure mode where every L1 rebalance triggers an L2 rebalance, which re-centers L2 toward only one side of L1's gap, leaving L2 poorly positioned for the next L1 rebalance.
- Symptom: `l2.RebalanceExecutionCompleted ≈ l1.RebalanceExecutionCompleted`; inner layer provides no buffering benefit.
- Resolution: Increase inner layer buffer sizes to 5–10× outer layer's; use `LeftThreshold`/`RightThreshold` of 0.2–0.3.

---

## Common Misconceptions

**Intent vs Command**: Intents are signals — evaluation may skip execution entirely. They are not commands that guarantee rebalance will happen.

**Async Rebalancing**: `GetDataAsync` returns immediately; the User Path ends at `PublishIntent()` return. Rebalancing happens in background loops after the user thread has already returned.

**NoRebalanceRange vs CurrentCacheRange**: `NoRebalanceRange` is a shrunk stability zone *inside* `CurrentCacheRange`. The request may be close to the cache boundary and still fall within `NoRebalanceRange`.

**"Was Idle" Semantics**: `WaitForIdleAsync` guarantees the system *was* idle at some point, not that it *is* still idle. See `docs/shared/glossary.md`.

---

## See Also

- `docs/shared/glossary.md` — shared terms (`IRangeCache`, `IDataSource`, `RangeResult`, `AsyncActivityCounter`, layered cache types, concurrency primitives)
- `docs/sliding-window/architecture.md` — architecture and coordination model
- `docs/sliding-window/invariants.md` — formal invariant groups A–I
- `docs/sliding-window/storage-strategies.md` — Snapshot vs CopyOnRead trade-offs
- `docs/sliding-window/scenarios.md` — temporal scenario walkthroughs
- `docs/sliding-window/components/public-api.md` — public API reference
