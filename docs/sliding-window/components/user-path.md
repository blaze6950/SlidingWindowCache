# Components: User Path

## Overview

The User Path serves `GetDataAsync` calls and publishes rebalance intents. It is latency-sensitive and strictly **read-only** with respect to shared cache state.

## Motivation

User requests must not block on background optimization. The user path does the minimum necessary work to return the requested range: read from cache (if available), fetch missing data from `IDataSource` (if needed), then immediately signal background work via an intent and return.

## Key Components

| Component                                           | File                                                                                              | Role                                                |
|-----------------------------------------------------|---------------------------------------------------------------------------------------------------|-----------------------------------------------------|
| `SlidingWindowCache<TRange, TData, TDomain>`        | `src/Intervals.NET.Caching.SlidingWindow/Public/Cache/SlidingWindowCache.cs`                      | Public facade; delegates to `UserRequestHandler`    |
| `UserRequestHandler<TRange, TData, TDomain>`        | `src/Intervals.NET.Caching.SlidingWindow/Core/UserPath/UserRequestHandler.cs`                     | Internal user-path logic; sole publisher of intents |
| `CacheDataExtensionService<TRange, TData, TDomain>` | `src/Intervals.NET.Caching.SlidingWindow/Infrastructure/Services/CacheDataExtensionService.cs`    | Assembles requested range from cache + IDataSource  |
| `IntentController<TRange, TData, TDomain>`          | `src/Intervals.NET.Caching.SlidingWindow/Core/Rebalance/Intent/IntentController.cs`               | Publish-side only from user path                    |

## Execution Context

All user-path code executes on the **⚡ User Thread** (the caller's thread). No blocking on background operations.

## Operation Flow

1. **Cold-start check** — `!state.IsInitialized`: fetch full range from `IDataSource` and serve directly; `CacheInteraction = FullMiss`.
2. **Full cache hit** — `RequestedRange ⊆ Cache.Range`: read directly from storage (zero allocation for Snapshot mode); `CacheInteraction = FullHit`.
3. **Partial cache hit** — intersection exists: serve cached portion + fetch missing segments via `CacheDataExtensionService`; `CacheInteraction = PartialHit`.
4. **Full cache miss** — no intersection: fetch full range from `IDataSource` directly; `CacheInteraction = FullMiss`.
5. **Publish intent** — fire-and-forget; passes `deliveredData` to `IntentController.PublishIntent` and returns immediately.

`CacheInteraction` is classified during scenario detection (steps 1–4) and set on the `RangeResult` returned to the caller (Invariant SWC.A.10b).

## Responsibilities

- Assemble `RequestedRange` from cache and/or `IDataSource`.
- Return data immediately without awaiting rebalance.
- Publish a rebalance intent containing the delivered data (what the caller actually received).

## Non-Responsibilities

- Does **not** decide whether to rebalance.
- Does **not** mutate shared cache state (never calls `Cache.Rematerialize()`, never writes `IsInitialized` or `NoRebalanceRange`).
- Does **not** perform debounce or cancellation.
- Does **not** trim or normalize cache geometry.

## Invariants

| Invariant       | Description                                                                                                                                                                            |
|-----------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| SWC.A.3         | User requests always served immediately (never blocked by rebalance)                                                                                                                   |
| SWC.A.5         | `UserRequestHandler` is the sole publisher of rebalance intents                                                                                                                        |
| SWC.A.6         | Intent publication is fire-and-forget (background only)                                                                                                                                |
| SWC.A.11/SWC.A.12 | User path is strictly read-only w.r.t. `CacheState`                                                                                                                                 |
| SWC.A.10        | Returns exactly `RequestedRange` data                                                                                                                                                  |
| SWC.A.10a       | `RangeResult` contains `Range`, `Data`, and `CacheInteraction` — all set by `UserRequestHandler`                                                                                       |
| SWC.A.10b       | `CacheInteraction` accurately reflects the cache scenario: `FullMiss` (cold start / jump), `FullHit` (fully cached), `PartialHit` (partial overlap)                                    |
| SWC.G.3         | I/O isolation: `IDataSource` called on user's behalf from User Thread (partial hits) or Background Thread (rebalance execution); shared `CacheDataExtensionService` used by both paths |

See `docs/sliding-window/invariants.md` (Section SWC.A: User Path invariants) for full specification.

## Edge Cases

- If `IDataSource` returns null range (physical boundary miss), no intent is published for the missing region.
- Cold-start fetches data directly; the first intent triggers background initialization of cache geometry.

## Limitations

- User path is optimized for a **single logical consumer** pattern. Multiple independent consumers should use separate cache instances.

## See Also

- `docs/sliding-window/boundary-handling.md` — boundary semantics and null return behavior
- `docs/sliding-window/scenarios.md` — step-by-step walkthroughs of hit/miss/partial scenarios
- `docs/sliding-window/invariants.md` — Section SWC.A (User Path invariants), Section SWC.C (Intent invariants)
- `docs/sliding-window/components/intent-management.md` — intent lifecycle after publication
