# Components: Shared Infrastructure

Infrastructure components that are cache-agnostic and shared across all cache implementations in this solution.

---

## AsyncActivityCounter

**Location:** `src/Intervals.NET.Caching/Infrastructure/Concurrency/AsyncActivityCounter.cs`
**Namespace:** `Intervals.NET.Caching.Infrastructure.Concurrency` (internal; visible to SlidingWindow via `InternalsVisibleTo`)

### Purpose

`AsyncActivityCounter` tracks in-flight background operations and provides an awaitable notification for when all activity has ceased. It powers `WaitForIdleAsync` across all cache implementations.

### Design

Fully lock-free. Uses only `Interlocked` and `Volatile` operations. Supports concurrent callers from multiple threads (user thread, intent loop, execution loop).

**State model:**
- Counter starts at `0` (idle). A pre-completed `TaskCompletionSource` is created at construction.
- On `0 → 1` transition (`IncrementActivity`): a new `TaskCompletionSource` is created and published via `Volatile.Write` (release fence).
- On `N → 0` transition (`DecrementActivity`): the current `TaskCompletionSource` is read via `Volatile.Read` (acquire fence) and signalled via `TrySetResult`.
- `WaitForIdleAsync` snapshots the current `TaskCompletionSource` via `Volatile.Read` and returns its `Task`.

**Why `TaskCompletionSource` and not `SemaphoreSlim`:** `TCS` is state-based — once completed, all current and future awaiters of the same task complete immediately. `SemaphoreSlim.Release()` is token-based and is consumed by only the first waiter, which would break the multiple-awaiters pattern required here.

### API

```csharp
// Called before making work visible (S.H.1 invariant)
void IncrementActivity();

// Called in finally blocks after work completes (S.H.2 invariant)
void DecrementActivity();

// Returns a Task that completes when the counter reaches 0
Task WaitForIdleAsync(CancellationToken cancellationToken = default);
```

### Invariants

All three invariants from `docs/shared/invariants.md` group **S.H** apply:

- **S.H.1 — Increment-Before-Publish:** `IncrementActivity()` must be called **before** making work visible to any other thread (semaphore release, channel write, `Volatile.Write`, etc.). This prevents `WaitForIdleAsync` from completing in the gap between scheduling and visibility.
- **S.H.2 — Decrement-in-Finally:** `DecrementActivity()` must be called in a `finally` block — unconditional cleanup regardless of success, failure, or cancellation. Unbalanced calls cause counter underflow and `WaitForIdleAsync` hangs.
- **S.H.3 — "Was Idle" Semantics:** `WaitForIdleAsync` completes when the system **was idle at some point in time**, not necessarily when it is currently idle. New activity may start immediately after. This is correct for eventual-consistency callers (tests, disposal).

### Race Analysis

The lock-free design admits benign races between concurrent `IncrementActivity` and `DecrementActivity` calls. Two key interleavings are worth examining:

**Decrement + Increment interleaving (busy-period boundary):**

If T1 decrements to 0 while T2 increments to 1:
1. T1 observes `count = 0`, reads `TCS_old` via `Volatile.Read`, signals `TCS_old` (completes the old busy period)
2. T2 observes `count = 1`, creates `TCS_new`, publishes via `Volatile.Write` (starts a new busy period)
3. Result: `TCS_old` = completed, `_idleTcs` = `TCS_new` (uncompleted), `count = 1` — all correct

The old busy period ends and a new one begins. No corruption occurs.

**WaitForIdleAsync reading a completed TCS:**

T1 decrements to 0 and signals `TCS_old`. T2 increments to 1 and creates `TCS_new`. T3 calls `WaitForIdleAsync` and reads `TCS_old` (already completed). Result: `WaitForIdleAsync` completes immediately even though `count = 1`. This is correct — the system *was* idle between T1 and T2, which satisfies S.H.3 "was idle" semantics.

### Memory Barrier Semantics

TCS lifecycle uses explicit memory barriers:

- **`Volatile.Write` (release fence)** in `IncrementActivity` on the `0 → 1` transition: all prior writes (TCS construction, field initialization) are visible to any thread that subsequently reads via `Volatile.Read`. This ensures readers observe a fully-constructed `TaskCompletionSource`.
- **`Volatile.Read` (acquire fence)** in `DecrementActivity` and `WaitForIdleAsync`: ensures the reader observes the TCS published by the most recent `Volatile.Write`.

**Concurrent `0 → 1` transitions:** If multiple threads call `IncrementActivity` concurrently from idle state, `Interlocked.Increment` guarantees exactly one thread observes `newCount == 1`. That thread creates and publishes the TCS for the new busy period.

### Counter Underflow Protection

`DecrementActivity` checks for negative counter values. If a decrement would go below zero, it restores the counter to `0` via `Interlocked.CompareExchange` and throws `InvalidOperationException`. This surfaces unbalanced `Increment`/`Decrement` call sites immediately.

---

## ReadOnlyMemoryEnumerable

**Location:** `src/Intervals.NET.Caching/Infrastructure/ReadOnlyMemoryEnumerable.cs`
**Namespace:** `Intervals.NET.Caching.Infrastructure` (internal)

### Purpose

`ReadOnlyMemoryEnumerable<T>` wraps a `ReadOnlyMemory<T>` as an `IEnumerable<T>` without allocating a temporary `T[]` or copying the underlying data.

### Allocation Characteristics

The class exposes both a concrete `GetEnumerator()` returning the `Enumerator` struct and the interface `IEnumerable<T>.GetEnumerator()`:

- **Concrete type (`var` / `ReadOnlyMemoryEnumerable<T>`):** `foreach` resolves to the struct `GetEnumerator()` — zero allocation.
- **Interface type (`IEnumerable<T>`):** `GetEnumerator()` returns `IEnumerator<T>`, which boxes the struct enumerator — one heap allocation per call.

Callers should hold the concrete type to keep enumeration allocation-free.

---

## Work Scheduler Infrastructure

**Location:** `src/Intervals.NET.Caching/Infrastructure/Scheduling/`
**Namespace:** `Intervals.NET.Caching.Infrastructure.Scheduling` (internal)

### Purpose

The work scheduler infrastructure abstracts the mechanism for dispatching and executing background work items — serially or concurrently. It is fully cache-agnostic: all cache-type-specific logic is injected via delegates and interfaces.

### Class Hierarchy

```
IWorkScheduler<TWorkItem>                         — generic: Publish + Dispose
  └── ISerialWorkScheduler<TWorkItem>             — marker: single-writer serialization guarantee
        └── ISupersessionWorkScheduler<TWorkItem> — supersession: LastWorkItem + cancel-previous contract

WorkSchedulerBase<TWorkItem>                      — generic base: execution pipeline, disposal guard
  ├── SerialWorkSchedulerBase<TWorkItem>          — template method: sealed Publish + Dispose pipeline
  │     ├── UnboundedSerialWorkScheduler          — task chaining (FIFO, no cancel)
  │     ├── BoundedSerialWorkScheduler            — channel-based (FIFO, no cancel)
  │     └── SupersessionWorkSchedulerBase         — cancel-previous + LastWorkItem (ISupersessionWorkScheduler)
  │           ├── UnboundedSupersessionWorkScheduler — task chaining (supersession)
  │           └── BoundedSupersessionWorkScheduler   — channel-based (supersession)
  └── ConcurrentWorkScheduler                     — independent ThreadPool dispatch
```

### ISchedulableWorkItem

The `TWorkItem` constraint interface:

```csharp
internal interface ISchedulableWorkItem : IDisposable
{
    CancellationToken CancellationToken { get; }
    void Cancel();
}
```

Implementations must make `Cancel()` and `Dispose()` safe to call multiple times and handle disposal races gracefully.

**Canonical implementations:**
- `ExecutionRequest<TRange,TData,TDomain>` (SlidingWindow) — supersession serial use; owns its `CancellationTokenSource`; cancelled automatically by `UnboundedSupersessionWorkScheduler` on supersession
- `CacheNormalizationRequest<TRange,TData,TDomain>` (VisitedPlacesCache) — FIFO serial use; `Cancel()` is an intentional no-op (VPC.A.11: normalization requests are NEVER cancelled)
- `TtlExpirationWorkItem<TRange,TData>` (VisitedPlacesCache) — concurrent use; `Cancel()` and `Dispose()` are intentional no-ops because cancellation is driven by a shared `CancellationToken` passed in at construction

### IWorkScheduler\<TWorkItem\>

```csharp
internal interface IWorkScheduler<TWorkItem> : IAsyncDisposable
    where TWorkItem : class, ISchedulableWorkItem
{
    ValueTask PublishWorkItemAsync(TWorkItem workItem, CancellationToken loopCancellationToken);
}
```

The base scheduling contract. All implementations (serial and concurrent) implement this interface.

**`loopCancellationToken`:** Used by the bounded serial strategy to unblock a blocked `WriteAsync` during disposal. Other strategies accept the parameter for API consistency.

### ISerialWorkScheduler\<TWorkItem\>

```csharp
internal interface ISerialWorkScheduler<TWorkItem> : IWorkScheduler<TWorkItem>
    where TWorkItem : class, ISchedulableWorkItem
{
    // No members — pure marker interface
}
```

A **marker interface** that signals the single-writer serialization guarantee: no two work items published to this scheduler will ever execute concurrently. This is the foundational contract enabling consumers to mutate shared state without locks.

**Why a marker and not just `IWorkScheduler`:** Scheduler types are swappable via dependency injection. The marker interface allows compile-time enforcement of which components require serialized execution (e.g. `UserRequestHandler`, `VisitedPlacesCache`) versus which tolerate concurrent dispatch. It also scopes the interface hierarchy: supersession semantics extend `ISerialWorkScheduler`, not `IWorkScheduler`.

**FIFO guarantee:** All implementations of `ISerialWorkScheduler` are FIFO — work items execute in the order they are published, with no cancellation of pending items. For supersession semantics (cancel-previous-on-publish), see `ISupersessionWorkScheduler`.

**Implementations:** `UnboundedSerialWorkScheduler`, `BoundedSerialWorkScheduler`, `UnboundedSupersessionWorkScheduler`, `BoundedSupersessionWorkScheduler`.

### ISupersessionWorkScheduler\<TWorkItem\>

```csharp
internal interface ISupersessionWorkScheduler<TWorkItem> : ISerialWorkScheduler<TWorkItem>
    where TWorkItem : class, ISchedulableWorkItem
{
    TWorkItem? LastWorkItem { get; }
}
```

Extends `ISerialWorkScheduler<TWorkItem>` with the **supersession contract**: when a new work item is published, the previously published (and still-pending) work item is automatically cancelled before the new item is enqueued. This moves cancel-previous ownership from the consumer into the scheduler.

**`LastWorkItem`:** The most recently published work item, readable via `Volatile.Read`. Consumers (e.g. `IntentController`) read this **before** calling `PublishWorkItemAsync` to inspect the pending work item's desired state for anti-thrashing decisions. The scheduler handles the actual cancellation inside `PublishWorkItemAsync` — consumers do not call `lastWorkItem.Cancel()` manually.

**Cancel-on-dispose:** In addition to cancel-previous-on-publish, supersession schedulers also cancel the last work item during `DisposeAsync`, ensuring no stale pending work executes after the scheduler is torn down.

**Why not on `ISerialWorkScheduler`:** FIFO serial consumers (e.g. VisitedPlacesCache normalization path) must never cancel pending items (VPC.A.11). Keeping supersession on a sub-interface preserves the FIFO-safe base interface and prevents accidental cancel-previous behavior in non-supersession contexts.

**Implementations:** `UnboundedSupersessionWorkScheduler`, `BoundedSupersessionWorkScheduler`.

### IWorkSchedulerDiagnostics

The scheduler-level diagnostics interface, decoupling generic schedulers from any cache-type-specific diagnostics:

```csharp
internal interface IWorkSchedulerDiagnostics
{
    void WorkStarted();
    void WorkCancelled();
    void WorkFailed(Exception ex);
}
```

Cache implementations supply a thin adapter that bridges their own diagnostics interface to `IWorkSchedulerDiagnostics`. For SlidingWindow, this adapter is `SlidingWindowWorkSchedulerDiagnostics` (in `src/Intervals.NET.Caching.SlidingWindow/Infrastructure/Adapters/`).

### WorkSchedulerBase\<TWorkItem\>

Abstract base class centralizing the shared execution pipeline. Contains only logic that is identical across **all** scheduler types.

```
ExecuteWorkItemCoreAsync pipeline (per work item):
  1. Signal WorkStarted diagnostic
  2. Snapshot debounce delay from provider ("next cycle" semantics)
  3. await Task.Delay(debounceDelay, cancellationToken)  [skipped when zero]
  4. Explicit IsCancellationRequested check (Task.Delay race guard) [skipped when zero]
  5. await Executor(workItem, cancellationToken)
  6. catch OperationCanceledException → WorkCancelled diagnostic
  7. catch Exception → WorkFailed diagnostic
  8. finally: workItem.Dispose(); ActivityCounter.DecrementActivity()
```

The `finally` block in step 8 is the canonical S.H.2 call site for scheduler-owned decrements. Every work item is disposed here (or in `PublishWorkItemAsync`'s error handler) — no separate dispose step is needed during scheduler disposal.

**Disposal protocol (`DisposeAsync`):**
1. Idempotent guard via `Interlocked.CompareExchange`
2. Delegate to `DisposeAsyncCore()` (strategy-specific teardown; serial subclasses also cancel the last item here)

### SerialWorkSchedulerBase\<TWorkItem\>

Intermediate abstract class between `WorkSchedulerBase` and the FIFO leaf classes and `SupersessionWorkSchedulerBase`. Implements `ISerialWorkScheduler<TWorkItem>`.

Uses the **Template Method pattern** to provide a sealed, invariant execution pipeline while allowing subclasses to inject type-specific behavior at two hook points.

**Sealed `PublishWorkItemAsync` pipeline:**
```
1. Disposal guard (throws ObjectDisposedException if already disposed)
2. ActivityCounter.IncrementActivity()          [S.H.1 invariant]
3. OnBeforeEnqueue(workItem)                    [virtual hook — no-op in FIFO; sealed override in SupersessionWorkSchedulerBase]
4. EnqueueWorkItemAsync(workItem, ct)           [abstract — task chaining or channel write]
```

**Sealed `DisposeAsyncCore` pipeline:**
```
1. OnBeforeSerialDispose()                      [virtual hook — no-op in FIFO; sealed override in SupersessionWorkSchedulerBase]
2. DisposeSerialAsyncCore()                     [abstract — await task chain or complete channel + await loop]
```

**Virtual hooks (no-op defaults):**
- `OnBeforeEnqueue(TWorkItem workItem)` — called synchronously before enqueue; `SupersessionWorkSchedulerBase` seals the override to cancel the previous item and store the new one via `Volatile.Write`
- `OnBeforeSerialDispose()` — called synchronously before strategy teardown; `SupersessionWorkSchedulerBase` seals the override to cancel the last pending item

**Abstract methods implemented by all leaf classes:**
- `EnqueueWorkItemAsync(TWorkItem workItem, CancellationToken ct)` — enqueues the item (task chaining or channel write)
- `DisposeSerialAsyncCore()` — strategy-specific teardown (await chain or complete channel + await loop)

**Why sealed pipelines:** Sealing `PublishWorkItemAsync` and `DisposeAsyncCore` in the base class guarantees that the invariant-critical steps (S.H.1 increment, disposal guard, hook ordering) can never be accidentally bypassed or reordered by subclasses. Subclasses customize only their designated hook/abstract methods.

### UnboundedSerialWorkScheduler\<TWorkItem\>

**Serialization mechanism:** Lock-guarded task chaining. Each new work item is chained to await the previous execution's `Task` before starting its own. A `_chainLock` makes the read-chain-write sequence atomic, ensuring serialization is preserved even under concurrent publishers (e.g. multiple VPC user threads calling `GetDataAsync` simultaneously).

```csharp
// Conceptual model:
lock (_chainLock)
{
    previousTask = _currentExecutionTask;
    newTask = ChainExecutionAsync(previousTask, workItem);
    _currentExecutionTask = newTask;
}
// Returns ValueTask.CompletedTask immediately (fire-and-forget)
```

The lock is held only for the synchronous read-chain-write sequence (no awaits inside), so contention duration is negligible.

**`ChainExecutionAsync` — ThreadPool guarantee via `Task.Yield()`:**

`ChainExecutionAsync` follows three ordered steps:

```
1. await Task.Yield()          — immediate ThreadPool context switch (very first statement)
2. await previousTask          — sequential ordering (wait for previous to finish)
3. await ExecuteWorkItemCoreAsync() — run work item on ThreadPool thread
```

`Task.Yield()` is the very first statement. Because `PublishWorkItemAsync` calls `ChainExecutionAsync` fire-and-forget (not awaited), the async state machine starts executing synchronously on the caller's thread until the first genuine yield point. By placing `Task.Yield()` first, the caller's thread is freed immediately and the entire method body — including `await previousTask`, its exception handler, and `ExecuteWorkItemCoreAsync` — runs on the ThreadPool.

Sequential ordering is fully preserved: `await previousTask` (step 2) still blocks execution of the current work item until the previous one completes — it just does so on a ThreadPool thread rather than the caller's thread.

Without `Task.Yield()`, a synchronous executor (e.g. returning `Task.CompletedTask` immediately) would run inline on the caller's thread, violating the fire-and-forget contract and invariants VPC.A.4, VPC.A.6, VPC.A.7.

**FIFO semantics:** Items are never cancelled. This is the correct strategy for VisitedPlacesCache normalization (VPC.A.11). For SlidingWindow (supersession), use `UnboundedSupersessionWorkScheduler`.

**Characteristics:**

| Property        | Value                          |
|-----------------|--------------------------------|
| Queue bound     | Unbounded (task chain)         |
| Caller blocks?  | Never — always fire-and-forget |
| Memory overhead | Single `Task` reference        |
| Backpressure    | None                           |
| Cancel-previous | No — FIFO                      |
| Default?        | Yes                            |

**When to use:** Standard APIs with typical request patterns; IoT sensor streams; background batch processing; any scenario where request bursts are temporary.

**Disposal teardown (`DisposeSerialAsyncCore`):** captures the current task chain under `_chainLock` and awaits it.

### SupersessionWorkSchedulerBase\<TWorkItem\>

Intermediate abstract class between `SerialWorkSchedulerBase` and the two supersession leaf classes. Implements `ISupersessionWorkScheduler<TWorkItem>`.

Owns the entire supersession protocol in one place — the single source of truth for concurrency-sensitive cancel-previous logic:
- `_lastWorkItem` field (volatile read/write)
- `LastWorkItem` property (`Volatile.Read`)
- **Sealed** `OnBeforeEnqueue` override: cancels `_lastWorkItem` then stores the new item via `Volatile.Write`
- **Sealed** `OnBeforeSerialDispose` override: cancels `_lastWorkItem`

The hooks are **sealed** here (not just overridden) to prevent the leaf classes from accidentally re-overriding the cancel-previous protocol. Leaf classes are responsible only for their serialization mechanism (`EnqueueWorkItemAsync` and `DisposeSerialAsyncCore`).

**Why a shared base instead of per-leaf duplication:** The supersession protocol is concurrency-sensitive (volatile fences, cancel ordering). Duplicating it across both leaf classes would create two independent mutation sites for the same protocol — a maintenance risk in a codebase with formal concurrency invariants. A shared base provides a single source of truth.

### UnboundedSupersessionWorkScheduler\<TWorkItem\>

Extends `SupersessionWorkSchedulerBase`. Implements task-chaining serialization (same mechanism as `UnboundedSerialWorkScheduler`).

**Serialization mechanism:** Lock-guarded task chaining — identical to `UnboundedSerialWorkScheduler`. Inherits the supersession protocol (`_lastWorkItem`, `LastWorkItem`, `OnBeforeEnqueue`, `OnBeforeSerialDispose`) from `SupersessionWorkSchedulerBase`.

**Consumer:** SlidingWindow's `IntentController` / `SlidingWindowCache` — latest rebalance intent supersedes all previous ones.

### BoundedSerialWorkScheduler\<TWorkItem\>

**Serialization mechanism:** Bounded `Channel<TWorkItem>` with a single-reader execution loop.

```csharp
// Construction: starts execution loop immediately
_workChannel = Channel.CreateBounded<TWorkItem>(new BoundedChannelOptions(capacity)
{
    SingleReader = true,
    SingleWriter = singleWriter,  // false for VPC (concurrent user threads); true for single-writer callers
    FullMode = BoundedChannelFullMode.Wait  // backpressure
});
_executionLoopTask = ProcessWorkItemsAsync();

// Execution loop:
await foreach (var item in _workChannel.Reader.ReadAllAsync())
    await ExecuteWorkItemCoreAsync(item);
```

**`singleWriter` parameter:** Pass `false` when multiple threads may call `PublishWorkItemAsync` concurrently (e.g. VPC, where concurrent user requests each publish a normalization event). Pass `true` only when the calling context guarantees a single publishing thread. The channel's `SingleWriter` hint is an API contract with the `Channel<T>` implementation — violating it (passing `true` with multiple concurrent writers) is undefined behaviour and could break in future .NET versions.

**Backpressure:** When the channel is at capacity, `PublishWorkItemAsync` awaits `WriteAsync` (using `loopCancellationToken` to unblock during disposal). This throttles the caller's processing loop; user requests continue to be served without blocking.

**FIFO semantics:** Items are never cancelled. This is the correct strategy for VisitedPlacesCache normalization (VPC.A.11). For SlidingWindow (supersession), use `BoundedSupersessionWorkScheduler`.

**Characteristics:**

| Property        | Value                                                |
|-----------------|------------------------------------------------------|
| Queue bound     | Bounded (`capacity` parameter, must be ≥ 1)          |
| Caller blocks?  | Only when channel is full (intentional backpressure) |
| Memory overhead | Fixed (`capacity × item size`)                       |
| Backpressure    | Yes                                                  |
| Cancel-previous | No — FIFO                                            |
| Default?        | No — opt-in via builder                              |

**When to use:** High-frequency patterns (> 1000 requests/sec); resource-constrained environments; scenarios where backpressure throttling is desired.

**Disposal teardown (`DisposeSerialAsyncCore`):** calls `_workChannel.Writer.Complete()` then awaits `_executionLoopTask`.

### BoundedSupersessionWorkScheduler\<TWorkItem\>

Extends `SupersessionWorkSchedulerBase`. Implements channel-based serialization (same mechanism as `BoundedSerialWorkScheduler`).

**Serialization mechanism:** Bounded channel — identical to `BoundedSerialWorkScheduler`. Inherits the supersession protocol from `SupersessionWorkSchedulerBase`.

**Consumer:** SlidingWindow's `IntentController` / `SlidingWindowCache` when bounded scheduler is configured — latest rebalance intent supersedes all previous ones.

### ConcurrentWorkScheduler\<TWorkItem\>

**Dispatch mechanism:** Each work item is dispatched independently to the ThreadPool via `ThreadPool.QueueUserWorkItem`. No ordering or exclusion guarantees.

```csharp
ThreadPool.QueueUserWorkItem(
    static state => _ = state.scheduler.ExecuteWorkItemCoreAsync(state.workItem),
    state: (scheduler: this, workItem),
    preferLocal: false);
```

**Primary consumer:** TTL expiration path (VisitedPlacesCache). Each TTL work item awaits `Task.Delay(remaining)` independently — serialized execution would block all subsequent delays behind each other, making a concurrent scheduler essential.

**Cancellation and disposal:** Because items are independent, there is no meaningful "last item" to cancel on disposal. Cancellation of all in-flight items is driven by a shared `CancellationToken` passed into each work item at construction. The cache cancels that token during its `DisposeAsync`, causing all pending `Task.Delay` calls to throw `OperationCanceledException` and drain immediately. The cache then awaits the TTL activity counter going idle to confirm all items have finished. `DisposeAsyncCore` is a no-op.

**Characteristics:**

| Property       | Value                                           |
|----------------|-------------------------------------------------|
| Queue bound    | Unbounded (each item on ThreadPool)             |
| Caller blocks? | Never — always fire-and-forget                  |
| Ordering       | None — items are fully independent              |
| Backpressure   | None                                            |
| LastWorkItem   | N/A — does not implement `ISerialWorkScheduler` |

**When to use:** Work items that must execute concurrently (e.g. TTL delays); items whose concurrent execution is safe via atomic operations.

**Disposal teardown (`DisposeAsyncCore`):** No-op — drain is owned by the caller.

---

## Comparison: All Five Schedulers

| Concern                | UnboundedSerialWorkScheduler  | UnboundedSupersessionWorkScheduler     | BoundedSerialWorkScheduler           | BoundedSupersessionWorkScheduler     | ConcurrentWorkScheduler         |
|------------------------|-------------------------------|----------------------------------------|--------------------------------------|--------------------------------------|---------------------------------|
| Execution order        | Serial (one at a time)        | Serial (one at a time)                 | Serial (one at a time)               | Serial (one at a time)               | Concurrent (all at once)        |
| Serialization          | Task continuation chaining    | Task continuation chaining             | Bounded channel + single reader loop | Bounded channel + single reader loop | None                            |
| Caller blocking        | Never                         | Never                                  | Only when channel full               | Only when channel full               | Never                           |
| Memory                 | O(1) task reference           | O(1) task reference                    | O(capacity)                          | O(capacity)                          | O(N in-flight items)            |
| Backpressure           | None                          | None                                   | Yes                                  | Yes                                  | None                            |
| Cancel-previous-on-pub | No — FIFO                     | Yes — supersession                     | No — FIFO                            | Yes — supersession                   | No                              |
| LastWorkItem           | No                            | Yes (`ISupersessionWorkScheduler`)     | No                                   | Yes (`ISupersessionWorkScheduler`)   | No                              |
| Cancel-on-dispose      | No                            | Yes (last item)                        | No                                   | Yes (last item)                      | No (shared CTS owned by caller) |
| Implements             | `ISerialWorkScheduler`        | `ISupersessionWorkScheduler`           | `ISerialWorkScheduler`               | `ISupersessionWorkScheduler`         | `IWorkScheduler`                |
| Consumer               | VisitedPlacesCache (VPC.A.11) | SlidingWindowCache (unbounded default) | VisitedPlacesCache (bounded opt-in)  | SlidingWindowCache (bounded opt-in)  | TTL expiration path             |
| Default?               | Yes (VPC)                     | Yes (SWC)                              | No — opt-in                          | No — opt-in                          | TTL path only                   |

---

## See Also

- `docs/shared/invariants.md` — invariant groups S.H (activity tracking) and S.J (disposal)
- `docs/shared/architecture.md` — `AsyncActivityCounter` and `IWorkScheduler` in architectural context
- `docs/sliding-window/components/infrastructure.md` — SlidingWindow-specific wiring (`SlidingWindowWorkSchedulerDiagnostics`, `ExecutionRequest`)
