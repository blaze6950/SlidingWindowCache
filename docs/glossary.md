# Glossary

Canonical definitions for SlidingWindowCache terms. This is a reference, not a tutorial.

Recommended reading order:

1. `README.md`
2. `docs/architecture.md`
3. `docs/invariants.md`
4. `docs/components/overview.md`

## Core Terms

Cache
- The in-memory representation of a contiguous `Range<TRange>` of data, stored using a chosen storage strategy.
- Cache contiguity (no gaps) is a core invariant; see `docs/invariants.md`.

Range
- A value interval (e.g., `[100..200]`) represented by `Intervals.NET`.

Domain
- The mathematical rules for stepping/comparing `TRange` values (e.g., integer fixed-step, DateTime). In code this is the `TDomain` type.

Window
- The cached range maintained around the most recently accessed region, typically larger than the user’s requested range.

## Range Vocabulary

Requested Range
- The `Range<TRange>` passed into `GetDataAsync`.

Delivered Range
- The range the data source actually provided (may be smaller than requested for bounded sources). This is surfaced via `RangeResult.Range`.
- See `docs/boundary-handling.md`.

Current Cache Range
- The range currently held in the cache state.

Desired Cache Range
- The target range the cache would like to converge to based on configuration and the latest intent.

Available Range
- `Requested ∩ Current` (data that can be served immediately from the cache).

Missing Range
- `Requested \ Current` (data that must be fetched from `IDataSource`).

RangeChunk
- A data source return value representing a contiguous chunk: a `Range<TRange>?` plus associated data. `Range == null` means “no data available”.
- See `docs/boundary-handling.md`.

RangeResult
- The public API return from `GetDataAsync`: the delivered `Range<TRange>?` and the materialized data.
- See `docs/boundary-handling.md`.

## Architectural Concepts

User Path
- The user-facing call path (`GetDataAsync`) that serves data immediately and publishes an intent.
- Read-only with respect to shared cache state; see `docs/architecture.md` and `docs/invariants.md`.

Rebalance Path
- Background processing that decides whether to rebalance and, if needed, executes the rebalance and mutates cache state.

Single-Writer Architecture
- Only rebalance execution mutates shared cache state (cache contents, initialization flags, NoRebalanceRange, etc.).
- The User Path does not mutate that shared state.
- Canonical description: `docs/architecture.md`; formal rules: `docs/invariants.md`.

Single Logical Consumer Model
- One cache instance is intended for one coherent access stream (e.g., one viewport/scroll position). Multiple threads may call the cache, as long as they represent the same logical consumer.

Intent
- A signal published by the User Path after serving a request. It describes what was delivered and what was requested so the system can evaluate whether rebalance is worthwhile.
- Intents are signals, not commands: the system may legitimately skip work.

Latest Intent Wins
- The newest published intent supersedes older intents; intermediate intents may never be processed.

Decision-Driven Execution
- Rebalance work is gated by a multi-stage validation pipeline. Decisions are fast (CPU-only) and may skip execution entirely.
- Formal definition: `docs/invariants.md` (Decision Path invariants).

Work Avoidance
- The system prefers skipping rebalance when analysis shows it is unnecessary (e.g., request within NoRebalanceRange, pending work already covers it, desired range already satisfied).

NoRebalanceRange
- A stability zone around the current cache geometry. If the request is inside this zone, the decision engine skips scheduling a rebalance.

Debounce
- A deliberate delay before executing rebalance so bursts can settle and only the last relevant rebalance runs.

Normalization
- The process of converging cached data and cached range to the desired state (fetch missing data, trim, merge, then publish new cache state atomically).

Rematerialization
- Rebuilding the stored representation of cached data (e.g., allocating a new array in Snapshot mode) to apply a new cache range.

## Concurrency And Coordination

Cancellation
- A coordination mechanism to stop obsolete background work; it is not the “decision”. The decision engine remains the sole authority for whether rebalance is necessary.

AsyncActivityCounter
- Tracks ongoing internal operations and supports waiting for “idle” transitions.

WaitForIdleAsync (“Was Idle” Semantics)
- Completes when the system was idle at some point, which is appropriate for tests and convergence checks.
- It does not guarantee the system is still idle after the task completes.

Strong Consistency Mode
- An opt-in mode provided by the `GetDataAndWaitForIdleAsync` extension method on `IWindowCache`.
- Composes `GetDataAsync` (returns data immediately) with `WaitForIdleAsync` (waits for convergence), returning the same `RangeResult` as `GetDataAsync` but only after the cache has reached an idle state.
- Useful for cold start synchronization, integration testing, and any scenario requiring a guarantee that the cache window has converged before proceeding.
- Not recommended for hot paths: adds latency equal to the rebalance execution time (debounce delay + I/O).
- See `README.md` and `docs/components/public-api.md`.

## Multi-Layer Caches

Layered Cache
- A pipeline of two or more `WindowCache` instances where each layer's `IDataSource` is the layer below it. Created via `LayeredWindowCacheBuilder`. The user interacts with the outermost layer; inner layers serve as warm prefetch buffers. See `docs/architecture.md` and `README.md`.

Cascading Rebalance
- When an outer layer's rebalance fetches missing ranges from the inner layer via `GetDataAsync`, each fetch publishes a rebalance intent on the inner layer. If those ranges fall outside the inner layer's `NoRebalanceRange`, the inner layer also schedules a rebalance. Under correct configuration (inner buffers 5–10× larger than outer buffers) this is rare — the inner layer's Decision Engine rejects the intent at Stage 1. Under misconfiguration it becomes continuous (see "Cascading Rebalance Thrashing"). See `docs/architecture.md` (Cascading Rebalance Behavior) and `docs/scenarios.md` (Scenarios L6, L7).

Cascading Rebalance Thrashing
- The failure mode of a misconfigured layered cache where every outer layer rebalance triggers an inner layer rebalance, which re-centers the inner layer toward only one side of the outer layer's gap, leaving it poorly positioned for the next rebalance. Symptoms: `l2.RebalanceExecutionCompleted ≈ l1.RebalanceExecutionCompleted`; the inner layer provides no buffering benefit. Resolution: increase inner layer buffer sizes to 5–10× the outer layer's and use thresholds of 0.2–0.3. See `docs/scenarios.md` (Scenario L7).

Layer
- A single `WindowCache` instance in a layered cache stack. Layers are ordered by proximity to the user: L1 = outermost (user-facing), L2 = next inner, Lₙ = innermost (closest to the real data source).

WindowCacheDataSourceAdapter
- Adapts an `IWindowCache` to the `IDataSource` interface, enabling it to act as the backing store for an outer `WindowCache`. This is the composition point for building layered caches. The adapter does not own the inner cache; ownership is managed by `LayeredWindowCache`. See `src/SlidingWindowCache/Public/WindowCacheDataSourceAdapter.cs`.

LayeredWindowCacheBuilder
- Fluent builder that wires `WindowCache` layers into a `LayeredWindowCache`. Layers are added bottom-up (deepest/innermost first, user-facing last). Each `AddLayer` call adds one `WindowCache` on top of the current stack. `Build()` returns a `LayeredWindowCache` that owns all layers. See `src/SlidingWindowCache/Public/LayeredWindowCacheBuilder.cs`.

LayeredWindowCache
- A thin `IWindowCache` wrapper that owns a stack of `WindowCache` layers. Delegates `GetDataAsync` to the outermost layer. `WaitForIdleAsync` awaits all layers sequentially, outermost to innermost, ensuring full-stack convergence (required for correct behavior of `GetDataAndWaitForIdleAsync`). Disposes all layers outermost-first on `DisposeAsync`. Exposes `LayerCount`. See `src/SlidingWindowCache/Public/LayeredWindowCache.cs`.

## Storage And Materialization

UserCacheReadMode
- Controls how data is stored and served (materialization strategy). See `docs/storage-strategies.md`.

Snapshot Mode
- Stores data in an immutable contiguous array and serves `ReadOnlyMemory<TData>` without per-read allocations.

CopyOnRead Mode
- Stores data in a growable structure and copies on read (allocates per read) to reduce rebalance costs/LOH pressure in some scenarios.

Staging Buffer
- A temporary buffer used during rebalance to assemble a new contiguous representation before atomic publication.
- See `docs/storage-strategies.md`.

## Diagnostics

ICacheDiagnostics
- Optional instrumentation surface for observing user requests, decisions, rebalance execution, and failures.
- See `docs/diagnostics.md`.

NoOpDiagnostics
- The default diagnostics implementation that does nothing (intended to be effectively zero overhead).

## Common Misconceptions

**Intent vs Command**: Intents are signals — evaluation may skip execution entirely. They are not commands that guarantee rebalance will happen.

**Async Rebalancing**: `GetDataAsync` returns immediately; the User Path completes at `PublishIntent()` return. Rebalancing happens in background loops after the user thread has already returned.

**"Was Idle" Semantics**: `WaitForIdleAsync` guarantees the system was idle at some point, not that it is still idle after the task completes. New activity may start immediately after completion. Re-check state if stronger guarantees are needed.

**NoRebalanceRange**: This is a stability zone derived from the current cache range using threshold percentages. It is NOT the same as the current cache range — it is a shrunk inner zone. If the requested range falls within this zone, rebalance is skipped even though the requested range may extend close to the cache boundary.

## Concurrency Primitives

**Volatile Read / Write**: Memory barriers. `Volatile.Write` = release fence (writes before it are visible before the write is observed). `Volatile.Read` = acquire fence (reads after it observe writes before the corresponding release). Used for lock-free publishing of shared state.

**Interlocked Operations**: Atomic operations that complete without locks — `Increment`, `Decrement`, `Exchange`, `CompareExchange`. Used for activity counting, intent replacement, and disposal state transitions.

**Acquire-Release Ordering**: Memory ordering model used throughout. Writes before a "release" fence are visible to any thread that subsequently observes an "acquire" fence on the same location. The `AsyncActivityCounter` and intent publication patterns rely on this for safe visibility across threads without locks.

## See Also

`README.md`
`docs/architecture.md`
`docs/components/overview.md`
`docs/actors.md`
`docs/scenarios.md`
`docs/state-machine.md`
`docs/invariants.md`
`docs/boundary-handling.md`
`docs/storage-strategies.md`
`docs/diagnostics.md`
