# Components: Intent Management

## Overview

Intent management bridges the user path and background work. It receives access signals (intents) from the user thread, applies "latest intent wins" semantics, and runs the background intent processing loop that drives the decision and execution pipeline.

## Key Components

| Component                                  | File                                                               | Role                                                                                          |
|--------------------------------------------|--------------------------------------------------------------------|-----------------------------------------------------------------------------------------------|
| `IntentController<TRange, TData, TDomain>` | `src/SlidingWindowCache/Core/Rebalance/Intent/IntentController.cs` | Manages intent lifecycle; runs background processing loop                                     |
| `Intent<TRange, TData, TDomain>`           | `src/SlidingWindowCache/Core/Rebalance/Intent/Intent.cs`           | Carries `RequestedRange` + `AssembledRangeData`; cancellation is owned by execution requests |

## Execution Contexts

| Method                  | Context              | Description                                                 |
|-------------------------|----------------------|-------------------------------------------------------------|
| `PublishIntent()`       | ⚡ User Thread        | Atomic intent store + semaphore signal; returns immediately |
| `ProcessIntentsAsync()` | 🔄 Background Thread | Decision evaluation, cancellation, execution enqueue        |
| `DisposeAsync()`        | Caller thread        | Cancels loop, awaits completion, disposes resources         |

## PublishIntent (User Thread)

Called by `UserRequestHandler` after serving a request:

1. Atomically replaces pending intent via `Interlocked.Exchange` (latest wins; previous intent superseded)
2. Increments `AsyncActivityCounter` (before signalling — ordering required by Invariant H.47)
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
6. If execution required: cancel previous `CancellationTokenSource`, enqueue to `IRebalanceExecutionController`
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
- Does **not** perform debounce delay (handled by `IRebalanceExecutionController` implementations).
- Does **not** decide rebalance necessity (delegated to `RebalanceDecisionEngine`).

## Internal State

| Field                | Type                      | Description                                                        |
|----------------------|---------------------------|--------------------------------------------------------------------|
| `_pendingIntent`     | `Intent?` (volatile)      | Latest unprocessed intent; written by user thread, cleared by loop |
| `_intentSignal`      | `SemaphoreSlim`           | Wakes background loop when new intent arrives                      |
| `_loopCancellation`  | `CancellationTokenSource` | Cancels the background loop on disposal                            |
| `_activityCounter`   | `AsyncActivityCounter`    | Tracks in-flight operations for `WaitForIdleAsync`                 |

## Invariants

| Invariant | Description                                                              |
|-----------|--------------------------------------------------------------------------|
| C.17      | At most one pending intent at any time (atomic replacement)              |
| C.18      | Previous intents become obsolete when superseded                         |
| C.19      | Cancellation is cooperative via `CancellationToken`                      |
| C.20      | Cancellation checked after debounce before execution starts              |
| C.21      | At most one active rebalance scheduled at a time                         |
| C.24      | Intent does not guarantee execution                                      |
| C.24e     | Intent carries `deliveredData` (the data the user actually received)     |
| H.47      | Activity counter incremented before semaphore signal (ordering)          |
| H.48      | Activity counter decremented in `finally` blocks (unconditional cleanup) |

See `docs/invariants.md` (Section C: Intent invariants, Section H: Activity counter invariants) for full specification.

## See Also

- `docs/components/decision.md` — what `RebalanceDecisionEngine` does with the intent
- `docs/components/execution.md` — what `IRebalanceExecutionController` does after enqueue
- `docs/components/infrastructure.md` — `AsyncActivityCounter` and `WaitForIdleAsync` semantics
- `docs/invariants.md` — Sections C and H
