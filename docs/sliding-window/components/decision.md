# Components: Decision

## Overview

The decision subsystem determines whether a rebalance execution is necessary. It is analytical (CPU-only), deterministic, and has no side effects.

## Key Components

- `RebalanceDecisionEngine<TRange, TDomain>` — sole authority for rebalance necessity
- `RebalanceDecision<TRange>` / `RebalanceReason` — decision result value types
- `ProportionalRangePlanner<TRange, TDomain>` — computes `DesiredCacheRange`
- `NoRebalanceSatisfactionPolicy<TRange>` — checks `NoRebalanceRange` containment
- `NoRebalanceRangePlanner<TRange, TDomain>` — computes `NoRebalanceRange`

## Responsibilities

- Sole authority for rebalance necessity determination.
- Compute desired cache geometry from the request and configuration.
- Apply work-avoidance checks (stability zone, pending coverage, no-op geometry).

## Non-Responsibilities

- Does not schedule execution (publishes only a decision).
- Does not mutate cache state.
- Does not call `IDataSource`.

---

## Multi-Stage Validation Pipeline

`RebalanceDecisionEngine` runs a five-stage pipeline. All stages must pass for execution to be scheduled. Any stage may return an early-exit skip decision.

| Stage | Component                                              | Check                                                                    | Skip Condition                                                        |
|-------|--------------------------------------------------------|--------------------------------------------------------------------------|-----------------------------------------------------------------------|
| **1** | `NoRebalanceSatisfactionPolicy`                        | Is `RequestedRange` inside `NoRebalanceRange(CurrentCacheRange)`?        | Yes → skip (current cache provides sufficient buffer)                 |
| **2** | `NoRebalanceSatisfactionPolicy`                        | Is `RequestedRange` inside `NoRebalanceRange(PendingDesiredCacheRange)`? | Yes → skip (pending execution will cover request; prevents thrashing) |
| **3** | `ProportionalRangePlanner` + `NoRebalanceRangePlanner` | Compute `DesiredCacheRange` and `DesiredNoRebalanceRange`                | —                                                                     |
| **4** | `RebalanceDecisionEngine`                              | Is `DesiredCacheRange == CurrentCacheRange`?                             | Yes → skip (cache already optimal; no mutation needed)                |
| **5** | —                                                      | All stages passed                                                        | Return `Schedule(desiredRange, desiredNRR)`                           |

**Execution rule**: Rebalance executes ONLY if all five stages confirm necessity.

## Component Responsibilities in Decision Model

| Component                               | Role                                                      | Decision Authority      |
|-----------------------------------------|-----------------------------------------------------------|-------------------------|
| `UserRequestHandler`                    | Read-only; publishes intents with delivered data          | None                    |
| `IntentController`                      | Manages intent lifecycle; runs background processing loop | None                    |
| `IWorkScheduler<ExecutionRequest<...>>` | Debounce + execution serialization                        | None                    |
| `RebalanceDecisionEngine`               | **SOLE AUTHORITY** for necessity determination            | **Yes — THE authority** |
| `NoRebalanceSatisfactionPolicy`         | Stages 1 & 2 validation (NoRebalanceRange check)          | Analytical input        |
| `ProportionalRangePlanner`              | Stage 3: computes desired cache geometry                  | Analytical input        |
| `RebalanceExecutor`                     | Mechanical execution; assumes validated necessity         | None                    |

## System Stability Principle

The system prioritizes **decision correctness and work avoidance** over aggressive rebalance responsiveness, enabling smart eventual consistency.

**Work avoidance mechanisms:**
- Stage 1: Avoid rebalance if current cache provides sufficient buffer (NoRebalanceRange containment)
- Stage 2: Avoid redundant rebalance if pending execution will cover the request (anti-thrashing)
- Stage 4: Avoid no-op mutations if cache already in optimal configuration (Desired == Current)

**Trade-offs:**
- ✅ Prevents thrashing and oscillation (stability over aggressive responsiveness)
- ✅ Reduces redundant I/O operations (efficiency through validation)
- ✅ System remains stable under rapidly changing access patterns
- ⚠️ May delay cache optimization by debounce period (acceptable for stability)

**Characteristics of all decision components:**
- `internal sealed class` types with no mutable fields (stateless, pure functions)
- Pure functions: same inputs → same output, no side effects
- CPU-only: no I/O, no state mutation
- Fully synchronous: no async operations

## See Also

- `docs/sliding-window/invariants.md` — formal Decision Path invariant specifications (SWC.D.1–SWC.D.5)
- `docs/sliding-window/architecture.md` — Decision-Driven Execution section
- `docs/sliding-window/components/overview.md` — Invariant Implementation Mapping (Decision subsection)
