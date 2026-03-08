# Components: Infrastructure — Sliding Window Cache

## Overview

This document covers the SlidingWindow-specific infrastructure wiring: the thread safety model, component execution contexts, the complete three-phase flow diagram, and the `SlidingWindowWorkSchedulerDiagnostics` adapter.

For cache-agnostic infrastructure components (`AsyncActivityCounter`, `IWorkScheduler`, `WorkSchedulerBase`, `TaskBasedWorkScheduler`, `ChannelBasedWorkScheduler`), see [`docs/shared/components/infrastructure.md`](../../shared/components/infrastructure.md).

---

## Thread Safety Model

### Concurrency Philosophy

The Sliding Window Cache follows a **single consumer model** (see `docs/sliding-window/architecture.md`):

> A cache instance is designed for one logical consumer — one user, one access trajectory, one temporal sequence of requests. This is an ideological requirement, not merely a technical limitation.

### Key Principles

1. **Single Logical Consumer**: One cache instance = one user, one coherent access pattern
2. **Execution Serialization**: Intent-level serialization via semaphore; execution-level serialization via task-chaining or channel; `Interlocked.Exchange` for atomic pending rebalance cancellation; no `lock` or `Monitor` in hot path
3. **Coordination Mechanism**: Single-writer architecture (User Path is read-only, only Rebalance Execution writes to `CacheState`); validation-driven cancellation (`DecisionEngine` confirms necessity then triggers cancellation); atomic updates via `Rematerialize()` (atomic array/List reference swap)

### Component Thread Contexts

| Component                                  | Thread Context | Notes                                                      |
|--------------------------------------------|----------------|------------------------------------------------------------|
| `SlidingWindowCache`                       | Neutral        | Just delegates                                             |
| `UserRequestHandler`                       | ⚡ User Thread  | Synchronous, fast path                                     |
| `IntentController.PublishIntent()`         | ⚡ User Thread  | Atomic intent storage + semaphore signal (fire-and-forget) |
| `IntentController.ProcessIntentsAsync()`   | 🔄 Background  | Intent processing loop; invokes `DecisionEngine`           |
| `RebalanceDecisionEngine`                  | 🔄 Background  | CPU-only; runs in intent processing loop                   |
| `ProportionalRangePlanner`                 | 🔄 Background  | Invoked by `DecisionEngine`                                |
| `NoRebalanceRangePlanner`                  | 🔄 Background  | Invoked by `DecisionEngine`                                |
| `NoRebalanceSatisfactionPolicy`            | 🔄 Background  | Invoked by `DecisionEngine`                                |
| `IWorkScheduler.PublishWorkItemAsync()`    | 🔄 Background  | Task-based: sync; channel-based: async await               |
| `TaskBasedWorkScheduler.ChainExecutionAsync()` | 🔄 Background | Task chain execution (sequential)                        |
| `ChannelBasedWorkScheduler.ProcessWorkItemsAsync()` | 🔄 Background | Channel loop execution                              |
| `RebalanceExecutor`                        | 🔄 Background  | ThreadPool, async, I/O                                     |
| `CacheDataExtensionService`                | Both ⚡🔄       | User Thread OR Background                                  |
| `CacheState`                               | Both ⚡🔄       | Shared mutable (no locks; single-writer)                   |
| Storage (`Snapshot`/`CopyOnRead`)          | Both ⚡🔄       | Owned by `CacheState`                                      |

**Critical:** `PublishIntent()` is a synchronous user-thread operation (atomic ops only, no decision logic). Decision logic (`DecisionEngine`, planners, policy) executes in the **background intent processing loop**. Rebalance execution (I/O) happens in a **separate background execution loop**.

---

## Complete Three-Phase Flow Diagram

```
┌──────────────────────────────────────────────────────────────────────┐
│ PHASE 1: USER THREAD (Synchronous — Fast Path)                       │
├──────────────────────────────────────────────────────────────────────┤
│ SlidingWindowCache.GetDataAsync()  — entry point (user-facing API)   │
│           ↓                                                          │
│ UserRequestHandler.HandleRequestAsync()                              │
│   • Read cache state (read-only)                                     │
│   • Fetch missing data from IDataSource (if needed)                  │
│   • Assemble result data                                             │
│   • Call IntentController.PublishIntent()                            │
│           ↓                                                          │
│ IntentController.PublishIntent()                                     │
│   • Interlocked.Exchange(_pendingIntent, intent)  (O(1))             │
│   • _activityCounter.IncrementActivity()                             │
│   • _intentSignal.Release()  (signal background loop)                │
│   • Return immediately                                               │
│           ↓                                                          │
│ Return data to user  ← USER THREAD BOUNDARY ENDS HERE                │
└──────────────────────────────────────────────────────────────────────┘
                               ↓ (semaphore signal)
┌──────────────────────────────────────────────────────────────────────┐
│ PHASE 2: BACKGROUND THREAD #1 (Intent Processing Loop)               │
├──────────────────────────────────────────────────────────────────────┤
│ IntentController.ProcessIntentsAsync()  (infinite loop)              │
│   • await _intentSignal.WaitAsync()                                  │
│   • Interlocked.Exchange(_pendingIntent, null)  → read intent        │
│           ↓                                                          │
│ RebalanceDecisionEngine.Evaluate()                                   │
│   Stage 1: Current NoRebalanceRange check  (fast-path skip)          │
│   Stage 2: Pending NoRebalanceRange check  (thrashing prevention)    │
│   Stage 3: ProportionalRangePlanner.Plan()  + NoRebalanceRangePlanner│
│   Stage 4: DesiredCacheRange == CurrentCacheRange?  (no-op skip)     │
│   Stage 5: Return Schedule decision                                  │
│           ↓                                                          │
│ If skip: continue loop (work avoidance, diagnostics event)           │
│ If execute:                                                          │
│   • lastWorkItem?.Cancel()                                           │
│   • IWorkScheduler.PublishWorkItemAsync()                            │
│     └─ Task-based: Volatile.Write (synchronous)                      │
│     └─ Channel-based: await WriteAsync()                             │
└──────────────────────────────────────────────────────────────────────┘
                               ↓ (strategy-specific)
┌──────────────────────────────────────────────────────────────────────┐
│ PHASE 3: BACKGROUND EXECUTION (Strategy-Specific)                    │
├──────────────────────────────────────────────────────────────────────┤
│ TASK-BASED: ChainExecutionAsync()  (chained async method)            │
│   • await Task.Yield()  (force ThreadPool context switch — 1st stmt) │
│   • await previousTask  (serial ordering)                            │
│   • await ExecuteWorkItemCoreAsync()                                 │
│ OR CHANNEL-BASED: ProcessWorkItemsAsync()  (infinite loop)           │
│   • await foreach (channel read)  (sequential processing)            │
│           ↓                                                          │
│ ExecuteWorkItemCoreAsync()  (both strategies)                        │
│   • await Task.Delay(debounce)  (cancellable)                        │
│   • Cancellation check                                               │
│           ↓                                                          │
│ RebalanceExecutor.ExecuteAsync()                                     │
│   • ct.ThrowIfCancellationRequested()  (before I/O)                  │
│   • Extend cache data via IDataSource  (async I/O)                   │
│   • ct.ThrowIfCancellationRequested()  (after I/O)                   │
│   • Trim to desired range                                            │
│   • ct.ThrowIfCancellationRequested()  (before mutation)             │
│   ┌──────────────────────────────────────┐                           │
│   │ CACHE MUTATION (SINGLE WRITER)       │                           │
│   │ • Cache.Rematerialize()              │                           │
│   │ • IsInitialized = true               │                           │
│   │ • NoRebalanceRange = desiredNRR      │                           │
│   └──────────────────────────────────────┘                           │
└──────────────────────────────────────────────────────────────────────┘
```

**Threading boundaries:**

- **User Thread Boundary**: Ends at `PublishIntent()` return. Everything before: synchronous, blocking user request. `PublishIntent()`: atomic ops only (microseconds), returns immediately.
- **Background Thread #1**: Intent processing loop. Single dedicated thread via semaphore wait. Processes intents sequentially (one at a time). CPU-only decision logic (microseconds). No I/O.
- **Background Execution**: Strategy-specific serialization. Task-based: chained async methods with `Task.Yield()` forcing ThreadPool dispatch before each execution. Channel-based: single dedicated loop via channel reader. Both: sequential (one at a time). I/O operations. SOLE writer to cache state.

---

## SlidingWindowWorkSchedulerDiagnostics

**File**: `src/Intervals.NET.Caching.SlidingWindow/Infrastructure/Adapters/SlidingWindowWorkSchedulerDiagnostics.cs`

Thin adapter that bridges `ICacheDiagnostics` → `IWorkSchedulerDiagnostics`, allowing the generic `WorkSchedulerBase` to emit diagnostics without any knowledge of SWC-specific types.

| `IWorkSchedulerDiagnostics` method | Maps to `ICacheDiagnostics`         |
|------------------------------------|-------------------------------------|
| `WorkStarted()`                    | `RebalanceExecutionStarted()`       |
| `WorkCancelled()`                  | `RebalanceExecutionCancelled()`     |
| `WorkFailed(Exception ex)`         | `RebalanceExecutionFailed(ex)`      |

This adapter is constructed inside `SlidingWindowCache` and injected into the work scheduler at construction time.

---

## Concurrency Guarantees

- ✅ User requests NEVER block on decision evaluation
- ✅ User requests NEVER block on rebalance execution
- ✅ At most ONE decision evaluation active at a time (sequential loop)
- ✅ At most ONE rebalance execution active at a time (sequential loop + strategy serialization)
- ✅ Cache mutations are SERIALIZED (single-writer via sequential execution)
- ✅ No race conditions on cache state (read-only User Path + single writer)
- ✅ No locks in hot path (Volatile/Interlocked only)

---

## Invariants

- Atomic cache mutation and state consistency: `docs/sliding-window/invariants.md` (Cache state and execution invariants).
- Activity tracking and "was idle" semantics: `docs/sliding-window/invariants.md` (Activity tracking invariants).

## Usage

For contributors:

- If you touch cache state publication, re-check single-writer and atomicity invariants.
- If you touch idle detection, re-check activity tracking invariants and tests.
- If you touch the intent loop or execution controllers, re-check the threading boundary described above.

## Edge Cases

- Storage strategy may use short critical sections internally; see `docs/sliding-window/storage-strategies.md`.

## Limitations

- Diagnostics should remain optional and low-overhead.
- Thread safety is guaranteed for the single-consumer model only; see `docs/sliding-window/architecture.md`.

## See Also

- `docs/shared/components/infrastructure.md` — `AsyncActivityCounter`, work schedulers (shared infrastructure)
- `docs/sliding-window/diagnostics.md` — production instrumentation patterns
- `docs/sliding-window/architecture.md` — threading model overview
