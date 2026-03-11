# Components: Execution

## Overview

The execution subsystem performs debounced, cancellable background work and is the **only path allowed to mutate shared cache state** (single-writer invariant). It receives validated execution requests from `IntentController` and ensures single-flight, eventually-consistent cache updates.

## Key Components

| Component                                                          | File                                                                                                       | Role                                                               |
|--------------------------------------------------------------------|------------------------------------------------------------------------------------------------------------|--------------------------------------------------------------------|
| `IWorkScheduler<TWorkItem>`                                        | `src/Intervals.NET.Caching/Infrastructure/Scheduling/IWorkScheduler.cs`                                   | Cache-agnostic serialization contract                              |
| `WorkSchedulerBase<TWorkItem>`                                     | `src/Intervals.NET.Caching/Infrastructure/Scheduling/WorkSchedulerBase.cs`                                | Shared execution pipeline: debounce, cancellation, diagnostics, cleanup |
| `UnboundedSerialWorkScheduler<TWorkItem>`                          | `src/Intervals.NET.Caching/Infrastructure/Scheduling/UnboundedSerialWorkScheduler.cs`                     | Default: async task-chaining with per-item cancellation            |
| `BoundedSerialWorkScheduler<TWorkItem>`                            | `src/Intervals.NET.Caching/Infrastructure/Scheduling/BoundedSerialWorkScheduler.cs`                       | Optional: bounded channel-based queue with backpressure            |
| `ISchedulableWorkItem`                                             | `src/Intervals.NET.Caching/Infrastructure/Scheduling/ISchedulableWorkItem.cs`                             | `TWorkItem` constraint: `Cancel()` + `IDisposable` + `CancellationToken` |
| `IWorkSchedulerDiagnostics`                                        | `src/Intervals.NET.Caching/Infrastructure/Scheduling/IWorkSchedulerDiagnostics.cs`                        | Scheduler-level diagnostic events (`WorkStarted`, `WorkCancelled`, `WorkFailed`) |
| `ExecutionRequest<TRange, TData, TDomain>`                         | `src/Intervals.NET.Caching.SlidingWindow/Core/Rebalance/Execution/ExecutionRequest.cs`                    | SWC work item; implements `ISchedulableWorkItem`                   |
| `SlidingWindowWorkSchedulerDiagnostics`                            | `src/Intervals.NET.Caching.SlidingWindow/Infrastructure/Adapters/SlidingWindowWorkSchedulerDiagnostics.cs` | Adapter bridging `ICacheDiagnostics` → `IWorkSchedulerDiagnostics` |
| `RebalanceExecutor<TRange, TData, TDomain>`                        | `src/Intervals.NET.Caching.SlidingWindow/Core/Rebalance/Execution/RebalanceExecutor.cs`                   | Sole writer; performs `Rematerialize`; the single-writer authority |
| `CacheDataExtensionService<TRange, TData, TDomain>`                | `src/Intervals.NET.Caching.SlidingWindow/Infrastructure/Services/CacheDataExtensionService.cs`            | Incremental data fetching; range gap analysis                      |

## Work Schedulers

The generic work schedulers live in `Intervals.NET.Caching` and have **zero coupling to SWC-specific types**. All SWC-specific concerns are injected via delegates:

| Dependency        | Type                                       | Replaces (old design)         |
|-------------------|--------------------------------------------|-------------------------------|
| Executor          | `Func<TWorkItem, CancellationToken, Task>` | `RebalanceExecutor` direct reference |
| Debounce provider | `Func<TimeSpan>`                           | `RuntimeCacheOptionsHolder`   |
| Diagnostics       | `IWorkSchedulerDiagnostics`                | `ICacheDiagnostics`           |
| Activity counter  | `AsyncActivityCounter`                     | (shared from `Intervals.NET.Caching`) |

`SlidingWindowCache.CreateExecutionController` wires these together when constructing the scheduler.

`IntentController` holds a reference to `IWorkScheduler<ExecutionRequest<TRange,TData,TDomain>>` directly — no SWC-specific scheduler interface is needed.

### UnboundedSerialWorkScheduler (default)

- Uses **async task chaining**: each `PublishWorkItemAsync` call creates a new `async Task` that first `await`s the previous task, then unconditionally yields to the ThreadPool via `await Task.Yield()`, then runs `ExecuteWorkItemCoreAsync` after the debounce delay. No `Task.Run` is used — `Task.Yield()` in `ChainExecutionAsync` is the explicit mechanism that guarantees ThreadPool execution regardless of whether the previous task completed synchronously or the executor itself is synchronous.
- On each new work item: a new task is chained onto the tail of the previous one; the caller (`IntentController`) creates a per-request `CancellationTokenSource` so any in-progress debounce delay can be cancelled when superseded.
- The chaining approach is lock-free: `_currentExecutionTask` is updated via `Volatile.Write` after each chain step.
- Selected when `SlidingWindowCacheOptions.RebalanceQueueCapacity` is `null`

### BoundedSerialWorkScheduler (optional)

- Uses `System.Threading.Channels.Channel<T>` with `BoundedChannelFullMode.Wait`
- Provides backpressure semantics: when the channel is at capacity, `PublishWorkItemAsync` (an `async ValueTask`) awaits the channel write, throttling the background intent processing loop. **No requests are ever dropped.**
- A dedicated `ProcessWorkItemsAsync` loop reads from the channel and executes items sequentially.
- Selected when `SlidingWindowCacheOptions.RebalanceQueueCapacity` is set

**Strategy comparison:**

| Aspect       | UnboundedSerial            | BoundedSerial          |
|--------------|----------------------------|------------------------|
| Debounce     | Per-item delay             | Channel draining       |
| Backpressure | None                       | Bounded capacity       |
| Cancellation | CancellationToken per task | Token per channel item |
| Default      | ✅ Yes                      | No                     |

**See**: `docs/shared/components/infrastructure.md` for detailed scheduler internals.

## ExecutionRequest — SWC Work Item

`ExecutionRequest<TRange,TData,TDomain>` implements `ISchedulableWorkItem` and carries:
- `Intent` — the rebalance intent (delivered data + requested range)
- `DesiredRange` — target cache range from the decision engine
- `DesiredNoRebalanceRange` — desired stability zone after execution
- `CancellationToken` — exposed from an owned `CancellationTokenSource`

**Creation:** `IntentController` creates `ExecutionRequest` directly (before calling `PublishWorkItemAsync`). The scheduler is a pure serialization mechanism — it does not own work-item construction.

## RebalanceExecutor — Single Writer

`RebalanceExecutor` is the **sole authority** for cache mutations. All other components are read-only with respect to `CacheState`.

**Execution flow:**

1. `ThrowIfCancellationRequested` — before any I/O (pre-I/O checkpoint)
2. Compute desired range gaps: `DesiredRange \ CurrentCacheRange`
3. Call `CacheDataExtensionService.ExtendCacheDataAsync` — fetches only missing subranges
4. `ThrowIfCancellationRequested` — after I/O, before mutations (pre-mutation checkpoint)
5. Call `CacheState.Rematerialize(newRangeData)` — atomic cache update
6. Update `CacheState.NoRebalanceRange` — new stability zone
7. Set `CacheState.IsInitialized = true` (if first execution)

**Cancellation checkpoints** (Invariant SWC.F.1):
- Before I/O: avoids unnecessary fetches
- After I/O: discards fetched data if superseded
- Before mutation: guarantees only latest validated execution applies changes

## CacheDataExtensionService — Incremental Fetching

**File**: `src/Intervals.NET.Caching.SlidingWindow/Infrastructure/Services/CacheDataExtensionService.cs`

- Computes missing ranges via range algebra: `DesiredRange \ CachedRange`
- Fetches only the gaps (not the full desired range)
- Merges new data with preserved existing data (union operation)
- Propagates `CancellationToken` to `IDataSource.FetchAsync`

**Invariants**: SWC.F.4 (incremental fetching), SWC.F.5 (data preservation during expansion).

## Responsibilities

- Debounce validated execution requests (burst resistance via delay or channel)
- Ensure single-flight rebalance execution (cancel obsolete work; serialize new work)
- Fetch missing data incrementally from `IDataSource` (gaps only)
- Apply atomic cache update (`Rematerialize`)
- Maintain cancellation checkpoints to preserve cache consistency

## Non-Responsibilities

- Does **not** decide whether to rebalance — decision is validated upstream by `RebalanceDecisionEngine` before this subsystem is invoked.
- Does **not** publish intents.
- Does **not** serve user requests.
- Does **not** construct `ExecutionRequest` — that is `IntentController`'s responsibility.

## Exception Handling

Exceptions thrown by `RebalanceExecutor` are caught **inside the work schedulers**, not in `IntentController.ProcessIntentsAsync`:

- **`UnboundedSerialWorkScheduler`**: Exceptions from `ExecuteWorkItemCoreAsync` (including `OperationCanceledException`) are caught in `ChainExecutionAsync`. An outer try/catch in `ChainExecutionAsync` also handles failures propagated from the previous chained task.
- **`BoundedSerialWorkScheduler`**: Exceptions from `ExecuteWorkItemCoreAsync` are caught inside the `ProcessWorkItemsAsync` reader loop.

In both cases, `OperationCanceledException` is reported via `IWorkSchedulerDiagnostics.WorkCancelled` (which `SlidingWindowWorkSchedulerDiagnostics` maps to `ICacheDiagnostics.RebalanceExecutionCancelled`) and other exceptions via `WorkFailed` (→ `RebalanceExecutionFailed`). Background execution exceptions are **never propagated to the user thread**.

`IntentController.ProcessIntentsAsync` has its own exception handling for the intent processing loop itself (e.g., decision evaluation failures or channel write errors), which are also reported via `ICacheDiagnostics.RebalanceExecutionFailed` and swallowed to keep the loop alive.

> ⚠️ Always wire `RebalanceExecutionFailed` in production — it is the only signal for background execution failures. See `docs/sliding-window/diagnostics.md`.

## Invariants

| Invariant         | Description                                                                                            |
|-------------------|--------------------------------------------------------------------------------------------------------|
| SWC.A.12a/SWC.F.2 | Only `RebalanceExecutor` writes to `CacheState` (single-writer)                                        |
| SWC.A.4           | User path never blocks waiting for rebalance                                                           |
| SWC.B.2           | Cache updates are atomic (all-or-nothing via `Rematerialize`)                                          |
| SWC.B.3           | Consistency under cancellation: mutations discarded if cancelled                                       |
| SWC.B.5           | Cancelled rebalance cannot violate `CacheData ↔ CurrentCacheRange` consistency                        |
| SWC.B.6           | Obsolete results never applied (cancellation token identity check)                                     |
| SWC.C.5           | Serial execution: at most one active rebalance at a time                                               |
| SWC.F.1           | Multiple cancellation checkpoints: before I/O, after I/O, before mutation                              |
| SWC.F.1a          | Cancellation-before-mutation guarantee                                                                 |
| SWC.F.3           | `Rematerialize` accepts arbitrary range and data (full replacement)                                    |
| SWC.F.4           | Incremental fetching: only missing subranges fetched                                                   |
| SWC.F.5           | Data preservation: existing cached data merged during expansion                                        |
| SWC.G.3           | I/O isolation: User Path MAY call `IDataSource` for U1/U5 (cold start / full miss); Rebalance Execution calls it for background normalization only |
| S.H.1             | Activity counter incremented before channel write / task chain step                                    |
| S.H.2             | Activity counter decremented in `finally` blocks                                                       |

See `docs/sliding-window/invariants.md` (Sections SWC.A, SWC.B, SWC.C, SWC.F, SWC.G, S.H) for full specification.

## See Also

- `docs/sliding-window/components/state-and-storage.md` — `CacheState` and storage strategy internals
- `docs/sliding-window/components/decision.md` — what validation happens before execution is enqueued
- `docs/sliding-window/invariants.md` — Sections B (state invariants) and F (execution invariants)
- `docs/sliding-window/diagnostics.md` — observing execution lifecycle events
- `docs/shared/components/infrastructure.md` — work scheduler internals
