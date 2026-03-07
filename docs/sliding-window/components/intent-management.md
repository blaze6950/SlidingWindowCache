# Components: Intent Management

## Overview

Intent management bridges the user path and background work. It receives access signals (intents) from the user thread, applies "latest intent wins" semantics, and runs the background intent processing loop that drives the decision and execution pipeline.

## Key Components

| Component                                  | File                                                                                 | Role                                                                                          |
|--------------------------------------------|--------------------------------------------------------------------------------------|-----------------------------------------------------------------------------------------------|
| `IntentController<TRange, TData, TDomain>` | `src/Intervals.NET.Caching.SlidingWindow/Core/Rebalance/Intent/IntentController.cs` | Manages intent lifecycle; runs background processing loop                                     |
| `Intent<TRange, TData, TDomain>`           | `src/Intervals.NET.Caching.SlidingWindow/Core/Rebalance/Intent/Intent.cs`           | Carries `RequestedRange` + `AssembledRangeData`; cancellation is owned by execution requests |

## Execution Contexts

| Method                  | Context              | Description                                                 |
|-------------------------|----------------------|-------------------------------------------------------------|
| `PublishIntent()`       | ⚡ User Thread        | Atomic intent store + semaphore signal; returns immediately |
| `ProcessIntentsAsync()` | 🔄 Background Thread | Decision evaluation, cancellation, execution enqueue        |
| `DisposeAsync()`        | Caller thread        | Cancels loop, awaits completion, disposes resources         |

## PublishIntent (User Thread)

Called by `UserRequestHandler` after serving a request:

1. Atomically replaces pending intent via `Interlocked.Exchange` (latest wins; previous intent superseded)
2. Increments `AsyncActivityCounter` (before signalling — ordering required by Invariant S.H.1)
3. Releases semaphore (wakes up `ProcessIntentsAsync` if sleeping)
4. Records `RebalanceIntentPublished` diagnostic event
5. Returns immediately (fire-and-forget)

**Intent does not guarantee execution.** It is a signal, not a command. Execution is decided by `RebalanceDecisionEngine` in the background loop.

## ProcessIntentsAsync (Background Loop)

Runs for the lifetime of the cache on a dedicated background task:

1. Wait on semaphore (no CPU spinning) — passes `_loopCancellation.Token` to `WaitAsync` so disposal cancels the wait cleanly
2. Atomically read and clear pending intent via `Interlocked.Exchange`
3. If intent is null (multiple intents collapsed before the loop read): decrement activity counter in `finally`, continue
4. Invoke `RebalanceDecisionEngine.Evaluate()` (5-stage pipeline, CPU-only)
5. If no execution required: record skip diagnostic, decrement activity counter, continue
6. If execution required: cancel previous `CancellationTokenSource`, enqueue to `IWorkScheduler<ExecutionRequest<...>>`
7. Decrement activity counter in `finally` block (unconditional cleanup)

## Intent Supersession

Rapid user access bursts naturally collapse: each new `PublishIntent` call atomically replaces the pending intent. The background loop always processes the **most recent** intent, discarding any intermediate ones. This is the primary burst-resistance mechanism.

```
User burst: intent₁ → intent₂ → intent₃
                                    ↓ (loop wakes up once)
                          Processes intent₃ only; intent₁ and intent₂ are gone
```

## Responsibilities

- Accept access signals from the user thread
- Maintain "latest intent wins" supersession semantics
- Run the background loop: decision evaluation → cancellation → execution enqueue
- Track activity via `AsyncActivityCounter` for `WaitForIdleAsync` support

## Non-Responsibilities

- Does **not** perform cache mutations.
- Does **not** perform I/O.
- Does **not** perform debounce delay (handled by `IWorkScheduler<ExecutionRequest<...>>` implementations).
- Does **not** decide rebalance necessity (delegated to `RebalanceDecisionEngine`).

## Internal State

| Field               | Type                      | Description                                                        |
|---------------------|---------------------------|--------------------------------------------------------------------|
| `_pendingIntent`    | `Intent?` (volatile)      | Latest unprocessed intent; written by user thread, cleared by loop |
| `_intentSignal`     | `SemaphoreSlim`           | Wakes background loop when new intent arrives                      |
| `_loopCancellation` | `CancellationTokenSource` | Cancels the background loop on disposal                            |
| `_activityCounter`  | `AsyncActivityCounter`    | Tracks in-flight operations for `WaitForIdleAsync`                 |

## Invariants

| Invariant  | Description                                                              |
|------------|--------------------------------------------------------------------------|
| SWC.C.1    | At most one pending intent at any time (atomic replacement)              |
| SWC.C.2    | Previous intents become obsolete when superseded                         |
| SWC.C.3    | Cancellation is cooperative via `CancellationToken`                      |
| SWC.C.4    | Cancellation checked after debounce before execution starts              |
| SWC.C.5    | At most one active rebalance scheduled at a time                         |
| SWC.C.8    | Intent does not guarantee execution                                      |
| SWC.C.8e   | Intent carries `deliveredData` (the data the user actually received)     |
| S.H.1      | Activity counter incremented before semaphore signal (ordering)          |
| S.H.2      | Activity counter decremented in `finally` blocks (unconditional cleanup) |

See `docs/sliding-window/invariants.md` (Section SWC.C: Intent invariants, Section S.H: Activity counter invariants) for full specification.

## See Also

- `docs/sliding-window/components/decision.md` — what `RebalanceDecisionEngine` does with the intent
- `docs/sliding-window/components/execution.md` — what `IWorkScheduler<ExecutionRequest<...>>` does after enqueue
- `docs/shared/components/infrastructure.md` — `AsyncActivityCounter` and `WaitForIdleAsync` semantics
- `docs/sliding-window/invariants.md` — Sections SWC.C and S.H
