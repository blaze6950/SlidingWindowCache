# Components: Execution

## Overview

The execution subsystem performs debounced, cancellable background work and is the **only path allowed to mutate shared cache state** (single-writer invariant). It receives validated execution requests from `IntentController` and ensures single-flight, eventually-consistent cache updates.

## Key Components

| Component                                                          | File                                                                                          | Role                                                               |
|--------------------------------------------------------------------|-----------------------------------------------------------------------------------------------|--------------------------------------------------------------------|
| `IRebalanceExecutionController<TRange, TData, TDomain>`            | `src/SlidingWindowCache/Core/Rebalance/Execution/IRebalanceExecutionController.cs`            | Execution serialization contract                                   |
| `TaskBasedRebalanceExecutionController<TRange, TData, TDomain>`    | `src/SlidingWindowCache/Core/Rebalance/Execution/TaskBasedRebalanceExecutionController.cs`    | Default: async task-chaining debounce + per-request cancellation   |
| `ChannelBasedRebalanceExecutionController<TRange, TData, TDomain>` | `src/SlidingWindowCache/Core/Rebalance/Execution/ChannelBasedRebalanceExecutionController.cs` | Optional: channel-based bounded execution queue with backpressure  |
| `RebalanceExecutor<TRange, TData, TDomain>`                        | `src/SlidingWindowCache/Core/Rebalance/Execution/RebalanceExecutor.cs`                        | Sole writer; performs `Rematerialize`; the single-writer authority |
| `CacheDataExtensionService<TRange, TData, TDomain>`                | `src/SlidingWindowCache/Infrastructure/Services/CacheDataExtensionService.cs`                 | Incremental data fetching; range gap analysis                      |

## Execution Controllers

### TaskBasedRebalanceExecutionController (default)

- Uses **async task chaining**: each `PublishExecutionRequest` call creates a new `async Task` that first `await`s the previous task, then runs `ExecuteRequestAsync` after the debounce delay. No `Task.Run` is used — the async state machine naturally schedules continuations on the thread pool via `ConfigureAwait(false)`.
- On each new execution request: a new task is chained onto the tail of the previous one; a per-request `CancellationTokenSource` is created so any in-progress debounce delay can be cancelled when superseded.
- The chaining approach is lock-free: `_currentExecutionTask` is updated via `Volatile.Write` after each chain step.
- Selected when `WindowCacheOptions.RebalanceQueueCapacity` is `null`

### ChannelBasedRebalanceExecutionController (optional)

- Uses `System.Threading.Channels.Channel<T>` with `BoundedChannelFullMode.Wait`
- Provides backpressure semantics: when the channel is at capacity, `PublishExecutionRequest` (an `async ValueTask`) awaits the channel write, throttling the background intent processing loop. **No requests are ever dropped.**
- A dedicated `ProcessExecutionRequestsAsync` loop reads from the channel and executes requests sequentially.
- Selected when `WindowCacheOptions.RebalanceQueueCapacity` is set

**Strategy comparison:**

| Aspect       | TaskBased                  | ChannelBased           |
|--------------|----------------------------|------------------------|
| Debounce     | Per-request delay          | Channel draining       |
| Backpressure | None                       | Bounded capacity       |
| Cancellation | CancellationToken per task | Token per channel item |
| Default      | ✅ Yes                      | No                     |

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

**Cancellation checkpoints** (Invariant F.1):
- Before I/O: avoids unnecessary fetches
- After I/O: discards fetched data if superseded
- Before mutation: guarantees only latest validated execution applies changes

## CacheDataExtensionService — Incremental Fetching

**File**: `src/SlidingWindowCache/Infrastructure/Services/CacheDataExtensionService.cs`

- Computes missing ranges via range algebra: `DesiredRange \ CachedRange`
- Fetches only the gaps (not the full desired range)
- Merges new data with preserved existing data (union operation)
- Propagates `CancellationToken` to `IDataSource.FetchAsync`

**Invariants**: F.4 (incremental fetching), F.5 (data preservation during expansion).

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

## Exception Handling

Exceptions thrown by `RebalanceExecutor` are caught **inside the execution controllers**, not in `IntentController.ProcessIntentsAsync`:

- **`TaskBasedRebalanceExecutionController`**: Exceptions from `ExecuteRequestAsync` (including `OperationCanceledException`) are caught in `ChainExecutionAsync`. An outer try/catch in `ChainExecutionAsync` also handles failures propagated from the previous chained task.
- **`ChannelBasedRebalanceExecutionController`**: Exceptions from `ExecuteRequestAsync` are caught inside the `ProcessExecutionRequestsAsync` reader loop.

In both cases, `OperationCanceledException` is reported via `ICacheDiagnostics.RebalanceExecutionCancelled` and other exceptions via `ICacheDiagnostics.RebalanceExecutionFailed`. Background execution exceptions are **never propagated to the user thread**.

`IntentController.ProcessIntentsAsync` has its own exception handling for the intent processing loop itself (e.g., decision evaluation failures or channel write errors during `PublishExecutionRequest`), which are also reported via `ICacheDiagnostics.RebalanceExecutionFailed` and swallowed to keep the loop alive.

> ⚠️ Always wire `RebalanceExecutionFailed` in production — it is the only signal for background execution failures. See `docs/diagnostics.md`.

## Invariants

| Invariant | Description                                                                                            |
|-----------|--------------------------------------------------------------------------------------------------------|
| A.12a/F.2 | Only `RebalanceExecutor` writes to `CacheState` (single-writer)                                        |
| A.4       | User path never blocks waiting for rebalance                                                           |
| B.2       | Cache updates are atomic (all-or-nothing via `Rematerialize`)                                          |
| B.3       | Consistency under cancellation: mutations discarded if cancelled                                       |
| B.5       | Cancelled rebalance cannot violate `CacheData ↔ CurrentCacheRange` consistency                        |
| B.6       | Obsolete results never applied (cancellation token identity check)                                     |
| C.5       | Serial execution: at most one active rebalance at a time                                               |
| F.1       | Multiple cancellation checkpoints: before I/O, after I/O, before mutation                              |
| F.1a      | Cancellation-before-mutation guarantee                                                                 |
| F.3       | `Rematerialize` accepts arbitrary range and data (full replacement)                                    |
| F.4       | Incremental fetching: only missing subranges fetched                                                   |
| F.5       | Data preservation: existing cached data merged during expansion                                        |
| G.3       | I/O isolation: User Path MAY call `IDataSource` for U1/U5 (cold start / full miss); Rebalance Execution calls it for background normalization only |
| H.1       | Activity counter incremented before channel write / task chain step                                    |
| H.2       | Activity counter decremented in `finally` blocks                                                       |

See `docs/invariants.md` (Sections A, B, C, F, G, H) for full specification.

## See Also

- `docs/components/state-and-storage.md` — `CacheState` and storage strategy internals
- `docs/components/decision.md` — what validation happens before execution is enqueued
- `docs/invariants.md` — Sections B (state invariants) and F (execution invariants)
- `docs/diagnostics.md` — observing execution lifecycle events
