# Components: Rebalance Path

## Overview

The Rebalance Path is responsible for decision-making and cache mutation. It runs entirely in the background, enforces execution serialization, and is the only subsystem permitted to mutate shared cache state.

## Motivation

Rebalancing is expensive: it involves debounce delays, optional I/O, and atomic cache mutations. The system avoids unnecessary work by running a multi-stage validation pipeline before scheduling execution. Only when all stages confirm necessity does rebalance proceed.

## Key Components

| Component                                                  | File                                                                                                    | Role                                                         |
|------------------------------------------------------------|---------------------------------------------------------------------------------------------------------|--------------------------------------------------------------|
| `IntentController<TRange, TData, TDomain>`                 | `src/Intervals.NET.Caching.SlidingWindow/Core/Rebalance/Intent/IntentController.cs`                    | Background loop; decision orchestration; cancellation        |
| `RebalanceDecisionEngine<TRange, TDomain>`                 | `src/Intervals.NET.Caching.SlidingWindow/Core/Rebalance/Decision/RebalanceDecisionEngine.cs`           | **Sole authority** for rebalance necessity; 5-stage pipeline |
| `NoRebalanceSatisfactionPolicy<TRange>`                    | `src/Intervals.NET.Caching.SlidingWindow/Core/Rebalance/Decision/NoRebalanceSatisfactionPolicy.cs`     | Stages 1 & 2: NoRebalanceRange containment checks            |
| `ProportionalRangePlanner<TRange, TDomain>`                | `src/Intervals.NET.Caching.SlidingWindow/Core/Planning/ProportionalRangePlanner.cs`                    | Stage 3: desired cache range computation                     |
| `NoRebalanceRangePlanner<TRange, TDomain>`                 | `src/Intervals.NET.Caching.SlidingWindow/Core/Planning/NoRebalanceRangePlanner.cs`                     | Stage 3: desired NoRebalanceRange computation                |
| `IWorkScheduler<ExecutionRequest<TRange, TData, TDomain>>` | `src/Intervals.NET.Caching/Infrastructure/Scheduling/IWorkScheduler.cs`                                | Debounce + single-flight execution contract (generic scheduler) |
| `RebalanceExecutor<TRange, TData, TDomain>`                | `src/Intervals.NET.Caching.SlidingWindow/Core/Rebalance/Execution/RebalanceExecutor.cs`                | Sole writer; performs `Rematerialize`                        |

See also the split component pages for deeper detail:

- `docs/sliding-window/components/intent-management.md` — intent lifecycle, `PublishIntent`, background loop
- `docs/sliding-window/components/decision.md` — 5-stage validation pipeline specification
- `docs/sliding-window/components/execution.md` — execution controllers, `RebalanceExecutor`, cancellation checkpoints

## Decision vs Execution

These are distinct concerns with separate components:

| Aspect           | Decision                         | Execution                          |
|------------------|----------------------------------|------------------------------------|
| **Authority**    | `RebalanceDecisionEngine` (sole) | `RebalanceExecutor` (sole writer)  |
| **Nature**       | CPU-only, pure, deterministic    | Debounced, cancellable, may do I/O |
| **State access** | Read-only                        | Write (sole)                       |
| **I/O**          | Never                            | Yes (`IDataSource.FetchAsync`)     |
| **Invariants**   | SWC.D.1, SWC.D.2, SWC.D.3, SWC.D.4, SWC.D.5 | SWC.A.12a, SWC.F.2, SWC.B.2, SWC.B.3, SWC.F.1, SWC.F.3–SWC.F.5 |

The formal 5-stage validation pipeline is specified in `docs/sliding-window/invariants.md` (Section SWC.D).

## End-to-End Flow

```
[User Thread]          [Background: Intent Loop]        [Background: Execution]
     │                          │                                │
     │ PublishIntent()          │                                │
     │─────────────────────────▶│                                │
     │                          │ DecisionEngine.Evaluate()      │
     │                          │ (5-stage pipeline)             │
     │                          │                                │
     │                          │ [Skip? → discard]              │
     │                          │                                │
     │                          │ Cancel previous CTS            │
     │                          │──────────────────────────────▶ │
     │                          │ Enqueue execution request      │
     │                          │──────────────────────────────▶ │
     │                          │                                │ Debounce
     │                          │                                │ FetchAsync (gaps only)
     │                          │                                │ ThrowIfCancelled
     │                          │                                │ Rematerialize (atomic)
     │                          │                                │ Update NoRebalanceRange
```

## Cancellation

Cancellation is **mechanical coordination**, not a decision mechanism:

- `IntentController` cancels the previous `CancellationTokenSource` when a new validated execution is needed.
- `RebalanceExecutor` checks cancellation at multiple checkpoints (before I/O, after I/O, before mutation).
- Cancelled results are **always discarded** — partial mutations never occur.

The decision about *whether* to cancel is made by `RebalanceDecisionEngine` (via the 5-stage pipeline), not by cancellation itself.

## Invariants

| Invariant   | Description                                                    |
|-------------|----------------------------------------------------------------|
| SWC.A.12a   | Only `RebalanceExecutor` writes `CacheState` (exclusive authority) |
| SWC.F.2     | Rebalance Execution is the sole component permitted to mutate cache state |
| SWC.B.2     | Atomic cache updates via `Rematerialize`                       |
| SWC.B.3     | Consistency under cancellation (discard, never partial-apply)  |
| SWC.B.5     | Cancelled rebalance execution cannot violate cache consistency  |
| SWC.C.3     | Cooperative cancellation via `CancellationToken`               |
| SWC.C.4     | Cancellation checked after debounce, before execution          |
| SWC.C.5     | At most one active rebalance scheduled at a time               |
| SWC.D.1     | Decision path is purely analytical (no I/O, no state mutation) |
| SWC.D.2     | Decision never mutates cache state                             |
| SWC.D.3     | No rebalance if inside current NoRebalanceRange (Stage 1)      |
| SWC.D.4     | No rebalance if DesiredRange == CurrentRange (Stage 4)         |
| SWC.D.5     | Execution proceeds only if ALL 5 stages pass                   |
| SWC.F.1     | Multiple cancellation checkpoints in execution                 |
| SWC.F.1a    | Cancellation-before-mutation guarantee                         |
| SWC.F.3–SWC.F.5 | Correct atomic rematerialization with data preservation    |

See `docs/sliding-window/invariants.md` (Sections SWC.B, SWC.C, SWC.D, SWC.F) for full specification.

## Usage

When debugging a rebalance:

1. Find the scenario in `docs/sliding-window/scenarios.md` (Decision/Execution sections).
  2. Confirm the 5-stage decision pipeline via `docs/sliding-window/invariants.md` Section SWC.D.
3. Inspect `IntentController`, `RebalanceDecisionEngine`, `IWorkScheduler`, `RebalanceExecutor` XML docs.

## Edge Cases

- **Bursty access**: multiple intents may collapse into one execution (latest-intent-wins semantics).
- **Cancellation checkpoints**: execution must yield at each checkpoint without leaving cache in an inconsistent state. Rematerialization is all-or-nothing.
- **Same-range short-circuit**: if `DesiredCacheRange == CurrentCacheRange` (Stage 4), execution is skipped even if it passed Stages 1–3.

## Limitations

- Not optimized for concurrent independent consumers; use one cache instance per consumer.

## See Also

- `docs/sliding-window/diagnostics.md` — observing decisions and executions via `ICacheDiagnostics` events
- `docs/sliding-window/invariants.md` — Sections SWC.C (intent), SWC.D (decision), SWC.F (execution)
- `docs/sliding-window/architecture.md` — single-writer architecture and execution serialization model
