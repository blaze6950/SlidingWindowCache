# Architecture — Shared Concepts

Architectural principles that apply across all cache implementations in this solution.

---

## Single-Writer Architecture

Only one component — the **designated background execution component** — is permitted to mutate shared cache state. All other components (especially the User Path) are strictly read-only with respect to cached data.

**Why:** Eliminates the need for locks on the hot read path. User requests read from a snapshot that only background execution can replace. This enables lock-free reads while maintaining strong consistency guarantees.

**Key rules:**
- User Path: read-only at all times, in all cache states
- Background execution component: sole writer — all cache mutations go through this component
- Cache mutations are atomic (all-or-nothing — no partial states are ever visible)

---

## User Path Never Blocks

User requests must return data immediately without waiting for background optimization.

The User Path reads from the current cache state (or fetches from `IDataSource` on miss), assembles the result, and returns it. It then signals background work (fire-and-forget) and returns to the caller.

**Consequence:** Data returned to the user is always correct, but the cache window may not yet be in the optimal configuration. Background work converges the cache asynchronously.

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
- `UnboundedSerialWorkScheduler` — lock-guarded task chaining (default)
- `BoundedSerialWorkScheduler` — bounded channel with backpressure (optional)

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

`WaitForIdleAsync` on a `LayeredRangeCache` awaits all layers sequentially, **outermost first**. The outermost layer is awaited first because its rebalance drives fetch requests into inner layers; only after it is idle can inner layers be known to have received all pending work. Each inner layer is then awaited in turn until the deepest layer is idle, guaranteeing the entire stack has converged.

### RangeCacheDataSourceAdapter

`RangeCacheDataSourceAdapter<TRange, TData, TDomain>` is the composition point for multi-layer stacks. It adapts any `IRangeCache` as an `IDataSource`, allowing a cache instance to act as the backing store for a higher (closer-to-user) layer.

**Design details:**

- **Zero-copy data flow:** The `ReadOnlyMemory<TData>` from `RangeResult` is wrapped in a `ReadOnlyMemoryEnumerable<TData>` and passed directly as `RangeChunk.Data`. This avoids allocating a temporary `TData[]` proportional to the data range.
- **Consistency model:** The adapter uses `GetDataAsync` (eventual consistency), not the strong consistency variants. Each layer manages its own rebalance lifecycle independently — the user always gets correct data immediately, and background optimization happens asynchronously at each layer.
- **Non-ownership lifecycle:** The adapter does NOT own the inner cache. It holds a reference but does not dispose it. Lifecycle management is the responsibility of `LayeredRangeCache`.

---

## See Also

- `docs/shared/invariants.md` — formal invariant groups S.H (activity tracking) and S.J (disposal)
- `docs/shared/components/infrastructure.md` — `AsyncActivityCounter` and work schedulers
- `docs/sliding-window/architecture.md` — SlidingWindow-specific architectural details (intent model, decision-driven execution, execution serialization, rebalance execution)
- `docs/visited-places/architecture.md` — VisitedPlaces-specific architectural details (FIFO processing, TTL, disposal)
