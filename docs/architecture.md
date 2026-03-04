# Architecture

## Overview

Intervals.NET.Caching is a range-based cache optimized for sequential access. It serves user requests immediately (User Path) and converges the cache to an optimal window asynchronously (Rebalance Path).

This document defines the canonical architecture: threading model, single-writer rule, intent model, decision-driven execution, coordination mechanisms, and disposal.

## Motivation

Traditional caches optimize for random access. Intervals.NET.Caching targets workloads where requests move predictably across a domain (e.g., scrolling, playback, time-series inspection). The goal is:

- Fast reads for the requested range.
- Background window maintenance (prefetch/trim) without blocking the caller.
- Strong architectural constraints that make concurrency correct-by-construction.

## Design

### Public API vs Internal Mechanisms

- Public API (user-facing): `WindowCache<TRange, TData, TDomain>` / `IWindowCache<TRange, TData, TDomain>`.
- Internal mechanisms: User request handling, intent processing loop, decision engine, execution controller(s), rebalance executor, storage strategy.

The public API is intentionally small; most complexity is internal and driven by invariants.

### Threading Model

The system has three execution contexts:

1. User Thread (User Path)
   - Serves `GetDataAsync` calls.
   - Reads cache and/or reads from `IDataSource` to assemble the requested range.
   - Publishes an intent (lightweight atomic signal) and returns; it does not wait for rebalancing.

2. Background Intent Loop (Decision Path)
   - Processes the latest published intent ("latest wins").
   - Runs analytical validation (CPU-only) to decide whether rebalance is necessary.
   - The user thread ends at `PublishIntent()` return. Decision evaluation happens here.

3. Background Execution (Execution Path)
   - Debounces, fetches missing data, and performs cache normalization.
   - This is the only context allowed to mutate shared cache state.

This library is designed for a single logical consumer per cache instance (one coherent access stream). Multiple threads may call the public API as long as the access pattern is still conceptually one consumer. See "Single Cache Instance = Single Consumer" below.

### Single-Writer Architecture

Single-writer is the core simplification:

- **User Path**: read-only with respect to shared cache state (never mutates `Cache`, `IsInitialized`, or `NoRebalanceRange`).
- **Rebalance Execution**: sole writer of shared cache state.

**Write Ownership:** Only `RebalanceExecutor` may write to `CacheState` fields:
- Cache data and range (via `Cache.Rematerialize()` atomic swap)
- `IsInitialized` property (via `internal set` — restricted to rebalance execution)
- `NoRebalanceRange` property (via `internal set` — restricted to rebalance execution)

**Read Safety:** User Path safely reads cache state without locks because:
- User Path never writes to `CacheState` (architectural invariant)
- Rebalance Execution is sole writer (eliminates write-write races)
- `Cache.Rematerialize()` performs atomic reference assignment
- Reference reads are atomic on all supported platforms
- No read-write races: User Path may read while Rebalance executes, but always sees a consistent state (old or new, never partial)

Thread-safety is achieved through **architectural constraints** (single-writer) and **coordination** (cancellation), not through locks on `CacheState` fields.

The single-writer rule is formalized in `docs/invariants.md` and prevents write-write races by construction.

### Execution Serialization

While the single-writer architecture eliminates write-write races between User Path and Rebalance Execution, multiple rebalance operations can be scheduled concurrently. Two layers enforce that only one rebalance writes at a time:

1. **Execution Controller Layer**: Serializes rebalance execution requests using one of two strategies (configured via `WindowCacheOptions.RebalanceQueueCapacity`).
2. **Executor Layer**: `RebalanceExecutor` uses `SemaphoreSlim(1, 1)` for mutual exclusion during cache mutations.

**Execution Controller Strategies:**

| Strategy                 | Configuration                  | Mechanism                           | Backpressure                            | Use Case                               |
|--------------------------|--------------------------------|-------------------------------------|-----------------------------------------|----------------------------------------|
| **Task-based** (default) | `rebalanceQueueCapacity: null` | Lock-free task chaining             | None (returns immediately)              | Recommended for most scenarios         |
| **Channel-based**        | `rebalanceQueueCapacity: >= 1` | `System.Threading.Channels` bounded | Async await on `WriteAsync()` when full | High-frequency or resource-constrained |

Both strategies extend `RebalanceExecutionControllerBase`, which implements the shared execution pipeline (`ExecuteRequestCoreAsync`: debounce + execute), last-execution-request tracking, and idempotent `DisposeAsync`. Concrete subclasses implement only the publication mechanism (`PublishExecutionRequest`) and their own disposal cleanup (`DisposeAsyncCore`).

**Task-Based Strategy (default):**
- Lock-free using volatile write (single-writer pattern — only intent processing loop writes)
- Fire-and-forget: returns `ValueTask.CompletedTask` immediately, executes on ThreadPool
- Previous request cancelled before chaining new execution
- `await previousTask; await ExecuteRequestAsync(request);` ensures serial execution
- Disposal: captures task chain via volatile read and awaits graceful completion

**Channel-Based Strategy (bounded):**
- `await WriteAsync()` blocks the intent processing loop when the channel is full (intentional throttling)
- Background loop processes requests sequentially from channel (one at a time)
- Disposal: completes channel writer and awaits loop completion

**Executor Layer (both strategies):** `RebalanceExecutor.ExecuteAsync()` uses `SemaphoreSlim(1, 1)`:
- Ensures only one rebalance execution can proceed through cache mutation at a time
- Cancellation token provides early exit while waiting for semaphore
- New rebalance scheduled after old one is cancelled (proper acquisition order)

**Why both CTS and SemaphoreSlim:**
- **CTS**: Lightweight cooperative cancellation signaling (intent obsolescence, user cancellation)
- **SemaphoreSlim**: Mutual exclusion for cache writes (prevents concurrent execution)
- Together: CTS signals "don't do this work anymore"; semaphore enforces "only one at a time"

**Strategy selection:**
- Use **Task-based** for normal operation, maximum performance, minimal overhead
- Use **Channel-based** for high-frequency rebalance scenarios requiring backpressure, or memory-constrained environments

### Runtime-Updatable Options

A subset of cache configuration — `LeftCacheSize`, `RightCacheSize`, `LeftThreshold`, `RightThreshold`, and `DebounceDelay` — can be changed on a live cache instance without reconstruction via `IWindowCache.UpdateRuntimeOptions`.

**Mechanism:**
- `WindowCache` constructs a `RuntimeCacheOptionsHolder` from `WindowCacheOptions` at creation time.
- The holder is shared (by reference) with all components that need configuration: `ProportionalRangePlanner`, `NoRebalanceRangePlanner`, `TaskBasedRebalanceExecutionController`, and `ChannelBasedRebalanceExecutionController`.
- `UpdateRuntimeOptions` applies the builder's deltas to the current `RuntimeCacheOptions` snapshot, validates the result, then publishes the new snapshot via `Volatile.Write`.
- All readers call `holder.Current` at the start of their operation — they always see the latest published snapshot.
- `CurrentRuntimeOptions` returns `holder.Current.ToSnapshot()`, projecting the internal `RuntimeCacheOptions` to the public `RuntimeOptionsSnapshot` DTO. The snapshot is immutable; callers must re-read the property to observe later updates.

**"Next cycle" semantics:** Changes take effect on the next rebalance decision/execution cycle. Ongoing cycles use the snapshot they already read.

**Single-writer guarantee is not affected:** `RuntimeCacheOptionsHolder` is a separate shared reference from `CacheState`. Writing to it does not violate the single-writer rule (which covers cache content mutations only).

**Non-updatable at runtime:** `ReadMode` (materialization strategy) and `RebalanceQueueCapacity` (execution controller selection) are determined at construction and cannot be changed.

### Intent Model (Signals, Not Commands)

After a user request completes and has "delivered data" (what the caller actually received), the User Path publishes an intent containing the delivered range/data.

Key properties:

- Intents represent observed access, not mandatory work.
- A newer intent supersedes an older intent (latest wins).
- Intents exist to inform the decision engine and provide authoritative delivered data for execution.
- Publishing an intent is synchronous in the user thread — atomic `Interlocked.Exchange` + semaphore signal only — then the user thread returns immediately.

### Decision-Driven Execution

Rebalance execution is gated by analytical validation. The decision engine runs a multi-stage pipeline and may decide to skip execution entirely.

**Key distinction:**
- **Rebalance Validation** = Decision mechanism (analytical, CPU-only, determines necessity) — THE authority
- **Cancellation** = Coordination mechanism (mechanical, prevents concurrent executions) — coordination tool only

Cancellation does NOT drive decisions; validated rebalance necessity drives cancellation.

This separation matters:
- Decisions are fast, deterministic, and CPU-only.
- Execution is slow(er), may do I/O, and is cancellable.

The canonical formal definition of the validation pipeline is in `docs/invariants.md` (Decision Path invariants).

### Smart Eventual Consistency Model

Cache state converges to optimal configuration asynchronously through decision-driven rebalance execution:

1. **User Path** returns correct data immediately (from cache or `IDataSource`) and classifies the request as `FullHit`, `PartialHit`, or `FullMiss` — exposed on `RangeResult.CacheInteraction`
2. **User Path** publishes intent with delivered data (synchronously in user thread — lightweight signal only)
3. **Intent processing loop** (background) wakes on semaphore signal, reads latest intent via `Interlocked.Exchange`
4. **Rebalance Decision Engine** validates rebalance necessity through multi-stage analytical pipeline (background intent loop — CPU-only, side-effect free)
5. **Work avoidance**: Rebalance skipped if validation determines it is unnecessary (NoRebalanceRange containment, Desired==Current, pending rebalance coverage) — all in background intent loop before scheduling
6. **Scheduling**: if execution required, cancels prior execution request and publishes a new one (background intent loop)
7. **Background execution**: debounce delay + actual rebalance I/O operations
8. **Debounce delay** controls convergence timing and prevents thrashing
9. **User correctness** never depends on cache state being up-to-date

Key insight: User always receives correct data, regardless of whether the cache has converged.

"Smart" characteristic: The system avoids unnecessary work through multi-stage validation rather than blindly executing every intent. This prevents thrashing, reduces redundant I/O, and maintains stability under rapidly changing access patterns while ensuring eventual convergence to optimal configuration.

### Coordination Mechanisms (Lock-Free)

The architecture prioritizes user requests. Coordination uses atomic primitives instead of locks where practical:

- **Intent publication**: `Interlocked.Exchange` for atomic latest-wins publication; `SemaphoreSlim` to signal background loop
- **Serialization**: at most one rebalance execution active (SemaphoreSlim + CTS)
- **Idle detection**: `AsyncActivityCounter` — fully lock-free, uses only `Interlocked` and `Volatile` operations; supports `WaitForIdleAsync`

**Safe visibility pattern:**
```csharp
// IntentController — atomic intent replacement (latest-wins)
var previousIntent = Interlocked.Exchange(ref _pendingIntent, newIntent);

// AsyncActivityCounter — idle detection
var newCount = Interlocked.Increment(ref _activityCount);  // Atomic counter
Volatile.Write(ref _idleTcs, newTcs);                      // Publish TCS with release fence
var tcs = Volatile.Read(ref _idleTcs);                     // Observe TCS with acquire fence
```

See also: `docs/invariants.md` (Activity tracking invariants).

### AsyncActivityCounter — Lock-Free Idle Detection

`AsyncActivityCounter` tracks all in-flight activity (user requests + background loops). When the counter reaches zero, the current `TaskCompletionSource` is completed, unblocking all waiters.

**Architecture:**
- Fully lock-free: `Interlocked` and `Volatile` operations only
- State-based semantics: `TaskCompletionSource` provides persistent idle state (not event-based)
- Multiple awaiter support: all threads awaiting idle state complete when signaled
- Eventual consistency: "was idle at some point" semantics (not "is idle now")

**Why `TaskCompletionSource`, not `SemaphoreSlim`:**

| Primitive | Semantics | Idle State Behavior | Correct? |
|---|---|---|---|
| `TaskCompletionSource` | State-based | All awaiters observe persistent idle state | ✅ Yes |
| `SemaphoreSlim` | Event/token | First awaiter consumes release; others block | ❌ No |

Idle detection requires state-based semantics: when the system becomes idle, ALL current and future awaiters (until the next busy period) should complete immediately.

**Memory barriers:**
- `Volatile.Write` (release fence): publishes fully-constructed TCS on 0→1 transition
- `Volatile.Read` (acquire fence): observes published TCS on N→0 transition and in `WaitForIdleAsync`

**"Was idle" semantics — not "is idle":** `WaitForIdleAsync` completes when the system was idle at some point. It does not guarantee the system is still idle after completion. This is correct for eventual consistency models. Callers requiring stronger guarantees must re-check state after await.

**Opt-in consistency modes:** Two extension methods on `IWindowCache` layer consistency guarantees on top of the default eventual consistency model:
- `GetDataAndWaitOnMissAsync` — **hybrid mode**: waits for idle only when `CacheInteraction` is `PartialHit` or `FullMiss`; returns immediately on `FullHit`. Provides warm-cache performance on hot paths while ensuring convergence on cold or near-boundary requests.
- `GetDataAndWaitForIdleAsync` — **strong mode**: always waits for idle regardless of cache interaction type. Useful for cold start synchronization and integration tests.

**Serialized access requirement:** Both extension methods provide their "cache has converged" guarantee only under serialized (one-at-a-time) access. Under parallel access the guarantee degrades: a caller may observe an already-completed (stale) idle `TaskCompletionSource` due to the gap between `Interlocked.Increment` (0→1) and `Volatile.Write` of the new TCS in `AsyncActivityCounter.IncrementActivity`. The methods remain safe (no deadlocks or data corruption) but may return before convergence is actually complete. See `README.md` and `docs/components/public-api.md` for usage details.

---

## Single Cache Instance = Single Consumer

A sliding window cache models the behavior of **one observer moving through data**.

Each cache instance represents one user, one access trajectory, one temporal sequence of requests. Attempting to share a single cache instance across multiple users or threads violates this fundamental assumption.

The single-consumer constraint exists for coherent access patterns, not for mutation safety (User Path is read-only, so parallel reads are safe from a mutation perspective, but still violate the single-consumer model).

### Why This Is a Requirement

**1. Sliding Window Requires a Unified Access Pattern**

The cache continuously adapts its window based on observed access. If multiple consumers request unrelated ranges:
- there is no single `DesiredCacheRange`
- the window oscillates or becomes unstable
- cache efficiency collapses

This is not a concurrency bug — it is a model mismatch.

**2. Rebalance Logic Depends on a Single Timeline**

Rebalance behavior relies on ordered intents representing sequential access observations, multi-stage validation, "latest validated decision wins" semantics, and eventual stabilization through work avoidance. These guarantees require a single temporal sequence of access events. Multiple consumers introduce conflicting timelines that cannot be meaningfully merged.

**3. Architecture Reflects the Ideology**

The system architecture enforces single-thread access, isolates rebalance logic from user code, and assumes coherent access intent. These choices exist to preserve the model, not to define the constraint.

### Multi-User Environments

**✅ Correct approach:** Create one cache instance per user (or per logical consumer):

```csharp
// Each consumer gets its own independent cache instance
var userACache = new WindowCache<int, byte[], IntDomain>(dataSource, options);
var userBCache = new WindowCache<int, byte[], IntDomain>(dataSource, options);
```

Each cache instance operates independently, maintains its own sliding window, and runs its own rebalance lifecycle.

**❌ Incorrect approach:** Do not share a cache instance across threads, multiplex multiple users through a single cache, or attempt to synchronize access externally. External synchronization does not solve the underlying model conflict.

---

## Disposal and Resource Management

### Disposal Architecture

`WindowCache` implements `IAsyncDisposable` to ensure proper cleanup of background processing resources. The disposal mechanism follows the same concurrency principles as the rest of the system: lock-free synchronization with graceful coordination.

### Disposal State Machine

Disposal uses a three-state pattern with lock-free transitions:

```
States:
  0 = Active    (accepting operations)
  1 = Disposing (disposal in progress)
  2 = Disposed  (cleanup complete)

Transitions:
  0 → 1: First DisposeAsync() call wins via Interlocked.CompareExchange
  1 → 2: Disposal completes, state updated via Volatile.Write

Concurrent Calls:
  - First call  (0→1): Performs actual disposal
  - Concurrent  (1):   Spin-wait until state becomes 2
  - Subsequent  (2):   Return immediately (idempotent)
```

### Disposal Sequence

When `DisposeAsync()` is called, cleanup cascades through the ownership hierarchy:

```
WindowCache.DisposeAsync()
  └─> UserRequestHandler.DisposeAsync()
      └─> IntentController.DisposeAsync()
          ├─> Cancel intent processing loop (CancellationTokenSource)
          ├─> Wait for processing loop to exit (Task.Wait)
          ├─> IRebalanceExecutionController.DisposeAsync()
          │   ├─> Task-based: Capture task chain (volatile read) + await completion
          │   └─> Channel-based: Complete channel writer + await loop completion
          └─> Dispose coordination resources (SemaphoreSlim, CancellationTokenSource)
```

Key properties:
- **Graceful shutdown**: Background tasks finish current work before exiting
- **No forced termination**: Cancellation signals used, not thread aborts
- **Cascading disposal**: Follows ownership hierarchy (parent disposes children)

### Concurrent Disposal Safety

The three-state pattern handles concurrent disposal using `TaskCompletionSource` for async coordination:

- **Winner thread (0→1)**: Creates `TaskCompletionSource`, performs disposal, signals result or exception
- **Loser threads (state=1)**: Brief spin-wait for TCS publication (CPU-only), then `await tcs.Task` asynchronously
- **Exception propagation**: All threads observe the winner's disposal outcome (success or exception)
- **Idempotency**: Safe to call multiple times

`TaskCompletionSource` is used (rather than spinning) because disposal involves async operations. Spin-waiting would burn CPU while async work completes. TCS allows async coordination without thread-pool starvation, consistent with the project's lock-free async patterns.

### Operation Blocking After Disposal

All public operations check disposal state using lock-free reads (`Volatile.Read`) before performing any work, and immediately throw `ObjectDisposedException` if the cache has been disposed.

### Disposal and Single-Writer Architecture

Disposal respects the single-writer architecture:
- **User Path**: read-only; disposal just blocks new reads
- **Rebalance Execution**: single writer; disposal waits for current execution to finish gracefully
- No write-write races introduced by disposal
- Uses same cancellation mechanism as rebalance operations

---

## Multi-Layer Caches

### Overview

Multiple `WindowCache` instances can be stacked into a cache pipeline where each layer's
`IDataSource` is the layer below it. This is built into the library via three public types:

- **`WindowCacheDataSourceAdapter`** — adapts any `IWindowCache` as an `IDataSource` so it can
  serve as a backing store for an outer `WindowCache`.
- **`LayeredWindowCacheBuilder`** — fluent builder that wires the layers together and returns a
  `LayeredWindowCache` that owns and disposes all of them.
- **`LayeredWindowCache`** — thin `IWindowCache` wrapper that delegates `GetDataAsync` to the
  outermost layer, awaits all layers sequentially (outermost-to-innermost) on `WaitForIdleAsync`,
  and disposes all layers outermost-first on disposal.

### Architectural Properties

**Each layer is an independent `WindowCache`.**
Every layer obeys the full single-writer architecture, decision-driven execution, and smart
eventual consistency model described in this document. There is no shared state between layers.

**Data flows inward on miss, outward on return.**
When the outermost layer does not have data in its window, it calls the adapter's `FetchAsync`,
which calls `GetDataAsync` on the next inner layer. This cascades inward until the real data
source is reached. Each layer then caches the data it fetched and returns it up the chain.

**Full-stack convergence via `WaitForIdleAsync`.**
`WaitForIdleAsync` on `LayeredWindowCache` awaits all layers sequentially, outermost to innermost.
The outermost layer must be awaited first, because its rebalance drives fetch requests (via the
adapter) into inner layers — only once the outer layer is idle can inner layers be known to have
received all pending work. This guarantees that calling `GetDataAndWaitForIdleAsync` on a
`LayeredWindowCache` waits for the entire cache stack to converge, not just the user-facing layer.
Each inner layer independently manages its own idle state via `AsyncActivityCounter`.

**Consistent model — not strong consistency between layers.**
The adapter uses `GetDataAsync` (eventual consistency), not `GetDataAndWaitForIdleAsync`. Inner
layers are not forced to converge before serving the outer layer. Each layer serves correct data
immediately; prefetch optimization propagates asynchronously at each layer independently.

**No new concurrency model.** A layered cache is not a multi-consumer scenario. All user
requests flow through the single outermost layer, which remains the sole logical consumer of the
next inner layer (via the adapter). The single-consumer model holds at every layer boundary.

**Disposal order.** `LayeredWindowCache.DisposeAsync` disposes layers outermost-first:
the user-facing layer is stopped first (no new requests flow into inner layers), then each inner
layer is disposed in turn. This mirrors the single-writer disposal sequence at each layer.

### Recommended Layer Configuration

| Layer                                       | `UserCacheReadMode` | Buffer size | Purpose                                |
|---------------------------------------------|---------------------|-------------|----------------------------------------|
| Innermost (deepest, closest to data source) | `CopyOnRead`        | 5–10×       | Wide prefetch window; absorbs I/O cost |
| Intermediate (optional)                     | `CopyOnRead`        | 1–3×        | Narrows window toward working set      |
| Outermost (user-facing)                     | `Snapshot`          | 0.3–1.0×    | Zero-allocation reads; minimal memory  |

Inner layers with `CopyOnRead` make cache writes cheap (growable list, no copy on write) while
outer `Snapshot` layers make reads cheap (single contiguous array, zero per-read allocation).

### Cascading Rebalance Behavior

This is the most important configuration concern in a layered cache setup.

#### Mechanism

When L1 rebalances, its `CacheDataExtensionService` computes missing ranges
(`DesiredCacheRange \ AssembledRangeData`) and calls the batch `FetchAsync(IEnumerable<Range>, ct)`
on the `WindowCacheDataSourceAdapter`. Because the adapter only implements the single-range
`FetchAsync` overload, the default `IDataSource` interface implementation dispatches one
parallel call per missing range via `Task.WhenAll`.

Each call reaches L2's `GetDataAsync`, which:
1. Serves the data immediately (from L2's cache or by fetching from L2's own data source)
2. **Publishes a rebalance intent on L2** with that individual range

When L1's `DesiredCacheRange` extends beyond L2's current window on both sides, L1's rebalance
produces two gap ranges (left and right). Both `GetDataAsync` calls on L2 happen in parallel.
L2's intent loop processes whichever intent it sees last ("latest wins"), and if that range
falls outside L2's `NoRebalanceRange`, L2 schedules its own background rebalance.

This is a **cascading rebalance**: L1's rebalance triggers L2's rebalance. Under sequential
access with correct configuration this should be rare. Under misconfiguration it becomes a
continuous cycle — every L1 rebalance triggers an L2 rebalance, which re-centers L2 toward
just one gap side, leaving L2 poorly positioned for L1's next rebalance.

#### Natural Mitigations Already in Place

The system provides several natural defences against cascading rebalances, even before
configuration is considered:

- **"Latest wins" semantics**: When two parallel `GetDataAsync` calls publish intents on L2,
  the intent loop processes only the surviving (latest) intent. At most one L2 rebalance is
  triggered per L1 rebalance burst, regardless of how many gap ranges L1 fetched.
- **Debounce delay**: L2's debounce delay further coalesces rapid sequential intent publications.
  Parallel intents from a single L1 rebalance will typically be absorbed into one debounce window.
- **Decision engine work avoidance**: If the surviving intent range falls within L2's
  `NoRebalanceRange`, L2's Decision Engine rejects rebalance at Stage 1 (fast path). No L2
  rebalance is triggered at all. This is the **desired steady-state** under correct configuration.

#### Configuration Requirements

The natural mitigations are only effective when L2's buffer is substantially larger than L1's.
The goal is that L1's full `DesiredCacheRange` fits comfortably within L2's `NoRebalanceRange`
during normal sequential access — making Stage 1 rejection the norm, not the exception.

**Buffer ratio rule of thumb:**

| Layer          | `leftCacheSize` / `rightCacheSize` | `leftThreshold` / `rightThreshold`         |
|----------------|------------------------------------|--------------------------------------------|
| L1 (outermost) | 0.3–1.0×                           | 0.1–0.2 (can be tight — L2 absorbs misses) |
| L2 (inner)     | 5–10× L1's buffer                  | 0.2–0.3 (wider stability zone)             |
| L3+ (deeper)   | 3–5× the layer above               | 0.2–0.3                                    |

With these ratios, L1's `DesiredCacheRange` (which expands L1's buffer around the request)
typically falls well within L2's `NoRebalanceRange` (which is L2's buffer shrunk by its
thresholds). L2's Decision Engine skips rebalance at Stage 1, and no cascading occurs.

**Why the ratio matters more than the absolute size:**

Suppose L1 has `leftCacheSize=1.0, rightCacheSize=1.0` and `requestedRange` has length 100.
L1's `DesiredCacheRange` will be approximately `[request - 100, request + 100]` (length 300).
For L2's Stage 1 to reject the rebalance, L2's `NoRebalanceRange` must contain that
`[request - 100, request + 100]` interval. L2's `NoRebalanceRange` is derived from
`CurrentCacheRange` by applying L2's thresholds inward. So L2 needs a `CurrentCacheRange`
substantially larger than L1's `DesiredCacheRange`.

#### Anti-Pattern: Buffers Too Close in Size

**What goes wrong when L2's buffer is similar to L1's:**

1. User scrolls → L1 rebalances, extending to `[50, 300]`
2. L1 fetches left gap `[50, 100)` and right gap `(250, 300]` from L2 in parallel
3. Both ranges fall outside L2's `NoRebalanceRange` (L2's buffer isn't large enough to cover them)
4. L2 re-centers toward the last-processed gap — say, `(250, 300]`
5. L2's `CurrentCacheRange` is now `[200, 380]`
6. User scrolls again → L1 rebalances to `[120, 370]`
7. Left gap `[120, 200)` falls outside L2's window — L2 must fetch from its own data source
8. L2 re-centers again → oscillation

**Symptoms:** `l2.RebalanceExecutionCompleted` count approaches `l1.RebalanceExecutionCompleted`.
The inner layer provides no meaningful buffering benefit. Data source I/O per user request is
not reduced compared to a single-layer cache.

**Resolution:** Increase L2's `leftCacheSize` and `rightCacheSize` to 5–10× L1's values, and
set L2's `leftThreshold` / `rightThreshold` to 0.2–0.3.

### See Also

- `README.md` — Multi-Layer Cache usage examples and configuration warning
- `docs/scenarios.md` — Scenarios L6 (cascading rebalance mechanics) and L7 (anti-pattern)
- `docs/storage-strategies.md` — Storage strategy trade-offs for layered configs
- `docs/components/public-api.md` — API reference for the three new public types

---

## Invariants

This document explains the model; the formal guarantees live in `docs/invariants.md`.

Canonical references:

- Single-writer and user-path priority: `docs/invariants.md` (User Path invariants)
- Intent semantics and temporal rules: `docs/invariants.md` (Intent invariants)
- Decision-driven validation pipeline: `docs/invariants.md` (Decision Path invariants)
- Execution serialization and cancellation: `docs/invariants.md` (Execution invariants)
- Activity tracking and idle detection: `docs/invariants.md` (Activity tracking invariants)

## Edge Cases

- Multi-user sharing a single cache instance: not a supported usage model; create one cache per logical consumer.
- Rapid bursty access: intent supersession plus validation plus debouncing avoids work thrash.
- Cancellation: user requests can cause validated cancellation of background execution; cancellation is a coordination mechanism, not a decision mechanism.

## Limitations

- Not designed as a general-purpose multi-tenant cache.
- Eventual convergence: the cache may temporarily be non-optimal; it converges asynchronously.
- Some behaviors depend on storage strategy trade-offs; see `docs/storage-strategies.md`.

## Usage

For how to use the public API:

- Start at `README.md`.
- Boundary semantics: `docs/boundary-handling.md`.
- Storage strategy selection: `docs/storage-strategies.md`.
- Diagnostics: `docs/diagnostics.md`.
