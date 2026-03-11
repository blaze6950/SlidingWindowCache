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

### Counter Underflow Protection

`DecrementActivity` checks for negative counter values. If a decrement would go below zero, it restores the counter to `0` via `Interlocked.CompareExchange` and throws `InvalidOperationException`. This surfaces unbalanced `Increment`/`Decrement` call sites immediately.

---

## IWorkScheduler / Work Scheduler Implementations

**Location:** `src/Intervals.NET.Caching/Infrastructure/Scheduling/`
**Namespace:** `Intervals.NET.Caching.Infrastructure.Scheduling` (internal)

### Purpose

`IWorkScheduler<TWorkItem>` abstracts the mechanism for serializing background execution requests, applying debounce delays, and handling cancellation and diagnostics. It is fully cache-agnostic: all cache-type-specific logic is injected via delegates and interfaces.

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

### IWorkScheduler\<TWorkItem\>

```csharp
internal interface IWorkScheduler<TWorkItem> : IAsyncDisposable
    where TWorkItem : class, ISchedulableWorkItem
{
    ValueTask PublishWorkItemAsync(TWorkItem workItem, CancellationToken loopCancellationToken);
    TWorkItem? LastWorkItem { get; }
}
```

**`LastWorkItem`:** The most recently published work item, readable via `Volatile.Read`. Callers (e.g. `IntentController`) read this before publishing a new item to cancel the previous pending execution and to inspect its pending desired state (e.g. for anti-thrashing decisions). All implementations write it via `Volatile.Write`.

**Single-writer guarantee:** All implementations must guarantee serialized execution — no two work items may execute concurrently. This is the foundational invariant allowing consumers to mutate shared state without locks.

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

Abstract base class centralizing the shared execution pipeline:

```
ExecuteWorkItemCoreAsync pipeline (per work item):
  1. Signal WorkStarted diagnostic
  2. Snapshot debounce delay from provider ("next cycle" semantics)
  3. await Task.Delay(debounceDelay, cancellationToken)
  4. Explicit IsCancellationRequested check (Task.Delay race guard)
  5. await Executor(workItem, cancellationToken)
  6. catch OperationCanceledException → WorkCancelled diagnostic
  7. catch Exception → WorkFailed diagnostic
  8. finally: workItem.Dispose(); ActivityCounter.DecrementActivity()
```

The `finally` block in step 8 is the canonical S.H.2 call site for scheduler-owned decrements.

**Disposal protocol (`DisposeAsync`):**
1. Idempotent guard via `Interlocked.CompareExchange`
2. Cancel last work item (`Volatile.Read(_lastWorkItem)?.Cancel()`)
3. Delegate to `DisposeAsyncCore()` (strategy-specific teardown)
4. Dispose last work item resources

### UnboundedSerialWorkScheduler\<TWorkItem\>

**Serialization mechanism:** Lock-free task chaining. Each new work item is chained to await the previous execution's `Task` before starting its own.

```csharp
// Conceptual model:
var previousTask = Volatile.Read(ref _currentExecutionTask);
var newTask = ChainExecutionAsync(previousTask, workItem);
Volatile.Write(ref _currentExecutionTask, newTask);
// Returns ValueTask.CompletedTask immediately (fire-and-forget)
```

The `Volatile.Write` is safe here because `PublishWorkItemAsync` is called from the single-writer intent processing loop only — no lock is needed.

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

**Characteristics:**

| Property        | Value                          |
|-----------------|--------------------------------|
| Queue bound     | Unbounded (task chain)         |
| Caller blocks?  | Never — always fire-and-forget |
| Memory overhead | Single `Task` reference        |
| Backpressure    | None                           |
| Default?        | Yes                            |

**When to use:** Standard APIs with typical request patterns; IoT sensor streams; background batch processing; any scenario where request bursts are temporary.

**Disposal teardown:** `DisposeAsyncCore` reads the current task chain via `Volatile.Read` and awaits it.

### BoundedSerialWorkScheduler\<TWorkItem\>

**Serialization mechanism:** Bounded `Channel<TWorkItem>` with a single-reader execution loop.

```csharp
// Construction: starts execution loop immediately
_workChannel = Channel.CreateBounded<TWorkItem>(new BoundedChannelOptions(capacity)
{
    SingleReader = true,
    SingleWriter = true,
    FullMode = BoundedChannelFullMode.Wait  // backpressure
});
_executionLoopTask = ProcessWorkItemsAsync();

// Execution loop:
await foreach (var item in _workChannel.Reader.ReadAllAsync())
    await ExecuteWorkItemCoreAsync(item);
```

**Backpressure:** When the channel is at capacity, `PublishWorkItemAsync` awaits `WriteAsync` (using `loopCancellationToken` to unblock during disposal). This throttles the caller's processing loop; user requests continue to be served without blocking.

**Characteristics:**

| Property        | Value                                                |
|-----------------|------------------------------------------------------|
| Queue bound     | Bounded (`capacity` parameter, must be ≥ 1)          |
| Caller blocks?  | Only when channel is full (intentional backpressure) |
| Memory overhead | Fixed (`capacity × item size`)                       |
| Backpressure    | Yes                                                  |
| Default?        | No — opt-in via builder                              |

**When to use:** High-frequency patterns (> 1000 requests/sec); resource-constrained environments; scenarios where backpressure throttling is desired.

**Disposal teardown:** `DisposeAsyncCore` calls `_workChannel.Writer.Complete()` then awaits `_executionLoopTask`.

---

## Comparison: UnboundedSerial vs BoundedSerial

| Concern         | UnboundedSerialWorkScheduler | BoundedSerialWorkScheduler           |
|-----------------|------------------------------|--------------------------------------|
| Serialization   | Task continuation chaining   | Bounded channel + single reader loop |
| Caller blocking | Never                        | Only when channel full               |
| Memory          | O(1) task reference          | O(capacity)                          |
| Backpressure    | None                         | Yes                                  |
| Complexity      | Lower                        | Slightly higher                      |
| Default         | Yes                          | No                                   |

Both provide the same single-writer serialization guarantee and the same `ExecuteWorkItemCoreAsync` pipeline. The choice is purely about flow control characteristics.

---

## See Also

- `docs/shared/invariants.md` — invariant groups S.H (activity tracking) and S.J (disposal)
- `docs/shared/architecture.md` — `AsyncActivityCounter` and `IWorkScheduler` in architectural context
- `docs/sliding-window/components/infrastructure.md` — SlidingWindow-specific wiring (`SlidingWindowWorkSchedulerDiagnostics`, `ExecutionRequest`)
