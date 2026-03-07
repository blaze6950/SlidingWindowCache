# Architecture — Shared Concepts

Architectural principles that apply across all cache implementations in this solution.

---

## Single-Writer Architecture

Only one component — the **Rebalance Execution** component — is permitted to mutate shared cache state. All other components (especially the User Path) are strictly read-only with respect to cached data.

**Why:** Eliminates the need for locks on the hot read path. User requests read from a snapshot that only background execution can replace. This enables lock-free reads while maintaining strong consistency guarantees.

**Key rules:**
- User Path: read-only at all times, in all cache states
- Rebalance Execution: sole writer — all cache mutations go through this component
- Cache mutations are atomic (all-or-nothing — no partial states are ever visible)

---

## User Path Never Blocks

User requests must return data immediately without waiting for background optimization.

The User Path reads from the current cache state (or fetches from `IDataSource` on miss), assembles the result, and returns it. It then signals background work (fire-and-forget) and returns to the caller.

**Consequence:** Data returned to the user is always correct, but the cache window may not yet be in the optimal configuration. Background work converges the cache asynchronously.

---

## Intent Model

The User Path signals background work by publishing an **intent** — a lightweight, versioned signal carrying the delivered data and the requested range. Intents are not commands: publishing an intent does not guarantee that background execution will occur.

The intent model has two key properties:

1. **Latest-intent-wins:** When multiple intents are published in rapid succession, only the most recent one is processed. Intermediate intents are superseded and discarded. This is the primary burst-resistance mechanism.

2. **Fire-and-forget:** The User Path publishes the intent and returns immediately without awaiting any background response.

---

## Decision-Driven Execution

Before scheduling cache mutations, background logic runs a multi-stage analytical validation to determine whether rebalancing is actually necessary. Execution is scheduled **only if all validation stages confirm necessity**.

This prevents:
- Redundant rebalancing when the cache is already optimal
- Thrashing when the access pattern changes rapidly
- Unnecessary I/O when the cache already covers the request

The decision is always a pure CPU-only operation: no I/O, no state mutation.

---

## AsyncActivityCounter

The `AsyncActivityCounter` (in `Intervals.NET.Caching`) tracks in-flight background operations for all cache implementations. It enables `WaitForIdleAsync` to know when all background work has completed.

**Ordering invariants:**
- **S.H.1 — Increment before publish:** The activity counter is always incremented **before** making work visible to any other thread (semaphore release, channel write, `Volatile.Write`, etc.).
- **S.H.2 — Decrement in `finally`:** The activity counter is always decremented in `finally` blocks — unconditional cleanup regardless of success, failure, or cancellation.
- **S.H.3 — "Was idle at some point" semantics:** `WaitForIdleAsync` completes when the counter **reached** zero, not necessarily when it is currently zero. New activity may start immediately after.

---

## Work Scheduler Abstraction

The `IWorkScheduler<TWorkItem>` abstraction (in `Intervals.NET.Caching`) serializes background execution requests, applies debounce delays, and handles cancellation and diagnostics. It is cache-agnostic: all cache-specific logic is injected via delegates.

Two implementations are provided:
- `TaskBasedWorkScheduler` — lock-free task chaining (default)
- `ChannelBasedWorkScheduler` — bounded channel with backpressure (optional)

---

## Disposal Pattern

All cache implementations implement `IAsyncDisposable`. Disposal is:
- **Graceful:** Background operations are cancelled cooperatively, not forcibly terminated
- **Idempotent:** Multiple dispose calls are safe
- **Concurrent-safe:** Disposal may be called while background operations are in progress
- **Post-disposal guard:** All public methods throw `ObjectDisposedException` after disposal

---

## Layered Cache Concept

Multiple cache instances may be composed into a stack where each layer uses the layer below it as its `IDataSource`. The outermost layer is user-facing (small, fast window); inner layers provide progressively larger buffers to amortize high-latency data source access.

`WaitForIdleAsync` on a `LayeredRangeCache` awaits all layers sequentially (outermost first) so that the full stack converges before returning.

---

## See Also

- `docs/shared/invariants.md` — formal invariant groups S.H (activity tracking) and S.J (disposal)
- `docs/shared/components/infrastructure.md` — `AsyncActivityCounter` and work schedulers
- `docs/sliding-window/architecture.md` — SlidingWindow-specific architectural details
