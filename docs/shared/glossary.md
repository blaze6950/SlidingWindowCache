# Glossary — Shared Concepts

Canonical definitions for terms that apply across all cache implementations in this solution.

---

## Interfaces

### IRangeCache\<TRange, TData, TDomain\>

The shared cache interface. Exposes:
- `GetDataAsync(Range<TRange>, CancellationToken) → ValueTask<RangeResult<TRange, TData>>`
- `WaitForIdleAsync(CancellationToken) → Task`
- `IAsyncDisposable`

All cache implementations in this solution implement `IRangeCache`.

### IDataSource\<TRange, TData\>

The data source contract. Cache implementations call this to fetch data that is not yet cached.

- `FetchAsync(Range<TRange>, CancellationToken) → Task<RangeChunk<TRange, TData>>` — single-range fetch (required)
- `FetchAsync(IEnumerable<Range<TRange>>, CancellationToken) → Task<IEnumerable<RangeChunk<TRange, TData>>>` — batch fetch (default: parallelized single-range calls)

Lives in `Intervals.NET.Caching`. Implemented by users of the library.

---

## DTOs

### RangeResult\<TRange, TData\>

Returned by `GetDataAsync`. Three properties:

| Property           | Type                    | Description                                                                       |
|--------------------|-------------------------|-----------------------------------------------------------------------------------|
| `Range`            | `Range<TRange>?`        | **Nullable.** The actual range of data returned. `null` = physical boundary miss. |
| `Data`             | `ReadOnlyMemory<TData>` | The materialized data. Empty when `Range` is `null`.                              |
| `CacheInteraction` | `CacheInteraction`      | How the request was served: `FullHit`, `PartialHit`, or `FullMiss`.               |

### RangeChunk\<TRange, TData\>

The unit returned by `IDataSource.FetchAsync`. Contains:
- `Range<TRange>? Range` — the range covered by this chunk (`null` if the data source has no data for the requested range)
- `IEnumerable<TData> Data` — the data for this range

### CacheInteraction

`enum` classifying how a `GetDataAsync` request was served relative to cached state.

| Value        | Meaning                                                                             |
|--------------|-------------------------------------------------------------------------------------|
| `FullMiss`   | Cache uninitialized or requested range had no overlap with cached data.             |
| `FullHit`    | Requested range was fully contained within cached data.                             |
| `PartialHit` | Requested range partially overlapped cached data; missing segments were fetched.    |

Per-request programmatic value — complement to aggregate `ICacheDiagnostics` counters.

---

## Shared Concurrency Primitives

### AsyncActivityCounter

A fully lock-free counter tracking in-flight background operations. Lives in `Intervals.NET.Caching` (`src/Intervals.NET.Caching/Infrastructure/Concurrency/AsyncActivityCounter.cs`), visible to SlidingWindow via `InternalsVisibleTo`.

**Purpose:** Enables `WaitForIdleAsync` to know when all background work has completed.

**Key semantics:**
- `IncrementActivity()` — increments counter, creates a new `TaskCompletionSource` if the counter transitions from 0→1
- `DecrementActivity()` — decrements counter, signals the current TCS if the counter reaches 0
- Counter incremented **before** publishing work (Invariant S.H.1); decremented in `finally` blocks (Invariant S.H.2)
- Fully lock-free: uses `Interlocked` operations and `Volatile` reads/writes

### WaitForIdleAsync

`IRangeCache.WaitForIdleAsync()` completes when the cache **was idle at some point** — not "is idle now" (Invariant S.H.3).

**Semantics:** "Was idle at some point" means the activity counter reached zero, but new activity may have started immediately after. The caller should not assume the cache is still idle after `await` returns.

**Correct use:** Waiting for background convergence in tests or strong consistency scenarios.

**Incorrect use:** Assuming the cache is fully quiescent after `await` — new requests may have been processed concurrently.

---

## Layered Cache Terms

### Layered Cache

A stack of `IRangeCache` instances where each layer uses the layer below it as its `IDataSource`. Built via `LayeredRangeCacheBuilder`. Outer layers have smaller, faster windows; inner layers have larger, slower buffers.

**Notation:** L1 = outermost (user-facing); Lₙ = innermost (closest to real `IDataSource`).

### LayeredRangeCacheBuilder

Fluent builder for layered stacks. Obtained via `SlidingWindowCacheBuilder.Layered(dataSource, domain)`.

### LayeredRangeCache

Thin `IRangeCache` wrapper that:
- Delegates `GetDataAsync` to the outermost layer
- `WaitForIdleAsync` awaits all layers sequentially (outermost first)
- Owns and disposes all layers

### RangeCacheDataSourceAdapter

Adapts an `IRangeCache` as an `IDataSource`, allowing any cache implementation to serve as the data source for an outer cache layer.

---

## Consistency Modes

### Eventual Consistency (default)

`GetDataAsync` returns data immediately. Background work converges the cache asynchronously. The returned data is correct but the cache window may not yet be optimally positioned.

### Strong Consistency

`GetDataAndWaitForIdleAsync` (extension on `IRangeCache`) — always waits for idle after `GetDataAsync`, regardless of `CacheInteraction`. Defined in `RangeCacheConsistencyExtensions`.

**Serialized access requirement:** Under parallel callers the "warm cache" guarantee degrades due to `WaitForIdleAsync`'s "was idle at some point" semantics (Invariant S.H.3).

---

## See Also

- `docs/shared/architecture.md` — shared architectural principles (single-writer, activity counter, disposal)
- `docs/shared/invariants.md` — shared invariant groups (activity tracking, disposal)
- `docs/sliding-window/glossary.md` — SlidingWindow-specific terms
- `docs/visited-places/glossary.md` — VisitedPlaces-specific terms (segment, eviction metadata, TTL, normalization)
