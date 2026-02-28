# Components: User Path

## Overview

The User Path serves `GetDataAsync` calls and publishes rebalance intents. It is latency-sensitive and strictly **read-only** with respect to shared cache state.

## Motivation

User requests must not block on background optimization. The user path does the minimum necessary work to return the requested range: read from cache (if available), fetch missing data from `IDataSource` (if needed), then immediately signal background work via an intent and return.

## Key Components

| Component                                           | File                                                                           | Role                                                |
|-----------------------------------------------------|--------------------------------------------------------------------------------|-----------------------------------------------------|
| `WindowCache<TRange, TData, TDomain>`               | `src/SlidingWindowCache/Public/WindowCache.cs`                                 | Public facade; delegates to `UserRequestHandler`    |
| `UserRequestHandler<TRange, TData, TDomain>`        | `src/SlidingWindowCache/Core/UserPath/UserRequestHandler.cs`                   | Internal user-path logic; sole publisher of intents |
| `CacheDataExtensionService<TRange, TData, TDomain>` | `src/SlidingWindowCache/Core/Rebalance/Execution/CacheDataExtensionService.cs` | Assembles requested range from cache + IDataSource  |
| `IntentController<TRange, TData, TDomain>`          | `src/SlidingWindowCache/Core/Rebalance/Intent/IntentController.cs`             | Publish-side only from user path                    |

## Execution Context

All user-path code executes on the **⚡ User Thread** (the caller's thread). No blocking on background operations.

## Operation Flow

1. **Cold-start check** — `!state.IsInitialized`: fetch full range from `IDataSource` and serve directly.
2. **Full cache hit** — `RequestedRange ⊆ Cache.Range`: read directly from storage (zero allocation for Snapshot mode).
3. **Partial cache hit** — intersection exists: serve cached portion + fetch missing segments via `CacheDataExtensionService`.
4. **Full cache miss** — no intersection: fetch full range from `IDataSource` directly.
5. **Publish intent** — fire-and-forget; passes `deliveredData` to `IntentController.PublishIntent` and returns immediately.

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

| Invariant | Description                                                                                                                                                                            |
|-----------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| A.0       | User requests always served immediately (never blocked by rebalance)                                                                                                                   |
| A.3       | `UserRequestHandler` is the sole publisher of rebalance intents                                                                                                                        |
| A.4       | Intent publication is fire-and-forget (background only)                                                                                                                                |
| A.5       | User path is strictly read-only w.r.t. `CacheState`                                                                                                                                    |
| A.10      | Returns exactly `RequestedRange` data                                                                                                                                                  |
| G.45      | I/O isolation: `IDataSource` called on user's behalf from User Thread (partial hits) or Background Thread (rebalance execution); shared `CacheDataExtensionService` used by both paths |

See `docs/invariants.md` (Section A: User Path invariants) for full specification.

## Edge Cases

- If `IDataSource` returns null (physical boundary miss), no intent is published for the missing region.
- Cold-start fetches data directly; the first intent triggers background initialization of cache geometry.

## Limitations

- User path is optimized for a **single logical consumer** pattern. Multiple independent consumers should use separate cache instances.

## See Also

- `docs/boundary-handling.md` — boundary semantics and null return behavior
- `docs/scenarios.md` — step-by-step walkthroughs of hit/miss/partial scenarios
- `docs/invariants.md` — Section A (User Path invariants), Section C (Intent invariants)
- `docs/components/intent-management.md` — intent lifecycle after publication
