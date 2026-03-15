# Components Overview — VisitedPlaces Cache

This document is the authoritative component catalog for `VisitedPlacesCache`. It maps every source file to its architectural role, subsystem, and visibility.

For actor responsibilities, see `docs/visited-places/actors.md`. For temporal behavior, see `docs/visited-places/scenarios.md`. For formal invariants, see `docs/visited-places/invariants.md`.

---

## Package Structure

`Intervals.NET.Caching.VisitedPlaces` contains 40 source files organized across four top-level directories:

```
src/Intervals.NET.Caching.VisitedPlaces/
├── Public/                          ← Public API surface (user-facing types)
│   ├── IVisitedPlacesCache.cs
│   ├── Cache/
│   ├── Configuration/
│   ├── Extensions/
│   └── Instrumentation/
├── Core/                            ← Business logic (internal)
│   ├── CachedSegment.cs
│   ├── CacheNormalizationRequest.cs
│   ├── Background/
│   ├── Eviction/
│   ├── Ttl/
│   └── UserPath/
└── Infrastructure/                  ← Infrastructure concerns (internal)
    ├── Adapters/
    └── Storage/
```

---

## Subsystem 1 — Public API

### `Public/IVisitedPlacesCache.cs`

| File                                        | Type      | Visibility | Role                                                                                                          |
|---------------------------------------------|-----------|------------|---------------------------------------------------------------------------------------------------------------|
| `IVisitedPlacesCache<TRange,TData,TDomain>` | interface | public     | VPC-specific public interface; extends `IRangeCache<TRange,TData>` with `WaitForIdleAsync` and `SegmentCount` |

Inherits from `IRangeCache<TRange,TData>` (shared foundation). Adds:
- `WaitForIdleAsync(CancellationToken)` — await background idle
- `int SegmentCount` — number of currently cached segments (diagnostic property)

### `Public/Cache/`

| File                                              | Type           | Visibility | Role                                                                                        |
|---------------------------------------------------|----------------|------------|---------------------------------------------------------------------------------------------|
| `VisitedPlacesCache<TRange,TData,TDomain>`        | `sealed class` | public     | Public facade and composition root; wires all internal actors; implements no business logic |
| `VisitedPlacesCacheBuilder`                       | `static class` | public     | Non-generic entry point: `For(...)` and `Layered(...)` factory methods                      |
| `VisitedPlacesCacheBuilder<TRange,TData,TDomain>` | `sealed class` | public     | Fluent builder; `WithOptions`, `WithEviction`, `WithDiagnostics`, `Build()`                 |

**`VisitedPlacesCache` wiring:**

```
VisitedPlacesCache (composition root)
  ├── _userRequestHandler: UserRequestHandler         ← User Path
  ├── _activityCounter: AsyncActivityCounter          ← WaitForIdleAsync support
  ├── _ttlEngine: TtlEngine?                          ← TTL subsystem (nullable)
  └── Internal construction:
      ├── storage = options.StorageStrategy.Create()
      ├── evictionEngine = new EvictionEngine(policies, selector, diagnostics)
      ├── ttlEngine = new TtlEngine(ttl, storage, evictionEngine, diagnostics) [if SegmentTtl set]
      ├── executor = new CacheNormalizationExecutor(storage, evictionEngine, diagnostics, ttlEngine)
      ├── scheduler = Unbounded/BoundedSerialWorkScheduler(executor, activityCounter)
      └── _userRequestHandler = new UserRequestHandler(storage, dataSource, scheduler, diagnostics, domain)
```

**Disposal sequence:** `UserRequestHandler.DisposeAsync()` → `TtlEngine.DisposeAsync()` (if present). See `docs/visited-places/architecture.md` for the three-state disposal pattern.

### `Public/Configuration/`

| File                                                        | Type           | Visibility | Role                                                                                 |
|-------------------------------------------------------------|----------------|------------|--------------------------------------------------------------------------------------|
| `VisitedPlacesCacheOptions<TRange,TData>`                   | `record`       | public     | Main configuration: `StorageStrategy`, `SegmentTtl?`, `EventChannelCapacity?`        |
| `VisitedPlacesCacheOptionsBuilder<TRange,TData>`            | `sealed class` | public     | Fluent builder for `VisitedPlacesCacheOptions`                                       |
| `StorageStrategyOptions<TRange,TData>`                      | abstract class | public     | Base for storage strategy options; exposes `Create()` factory                        |
| `SnapshotAppendBufferStorageOptions<TRange,TData>`          | `sealed class` | public     | Options for `SnapshotAppendBufferStorage` (default strategy)                         |
| `LinkedListStrideIndexStorageOptions<TRange,TData,TDomain>` | `sealed class` | public     | Options for `LinkedListStrideIndexStorage` (high-segment-count strategy)             |
| `EvictionSamplingOptions`                                   | `record`       | public     | Configures random sampling: `SampleSize`                                             |
| `EvictionConfigBuilder<TRange,TData>`                       | `sealed class` | public     | Fluent builder for eviction policies + selector; used by `WithEviction(Action<...>)` |

### `Public/Extensions/`

| File                           | Type           | Visibility | Role                                                                                                  |
|--------------------------------|----------------|------------|-------------------------------------------------------------------------------------------------------|
| `VisitedPlacesLayerExtensions` | `static class` | public     | `AddVisitedPlacesLayer(...)` extension on `LayeredRangeCacheBuilder`; wires a VPC instance as a layer |

### `Public/Instrumentation/`

| File                             | Type           | Visibility | Role                                                                                       |
|----------------------------------|----------------|------------|--------------------------------------------------------------------------------------------|
| `IVisitedPlacesCacheDiagnostics` | interface      | public     | 11 VPC-specific events + 5 inherited from `ICacheDiagnostics`; extends `ICacheDiagnostics` |
| `NoOpDiagnostics`                | `sealed class` | public     | Default no-op implementation; used when no diagnostics is provided                         |

For the full event reference, see `docs/visited-places/diagnostics.md`.

---

## Subsystem 2 — Core: Shared Data Types

| File                                           | Type           | Visibility | Role                                                                                  |
|------------------------------------------------|----------------|------------|---------------------------------------------------------------------------------------|
| `Core/CachedSegment<TRange,TData>`             | `sealed class` | internal   | Single cache entry: range, data, `EvictionMetadata?`, `MarkAsRemoved()` (Interlocked) |
| `Core/CacheNormalizationRequest<TRange,TData>` | `sealed class` | internal   | Background event: `UsedSegments`, `FetchedData?`, `RequestedRange`                    |

**`CachedSegment` key properties:**
- `Range` — the segment's range boundary
- `Data` — the cached `ReadOnlyMemory<TData>`
- `IEvictionMetadata? EvictionMetadata` — owned by the Eviction Selector; null until initialized
- `bool TryMarkAsRemoved()` — atomic removal flag (`Interlocked.CompareExchange`); enables idempotent TTL+eviction coordination (Invariant VPC.T.1)

---

## Subsystem 3 — Core: User Path

| File                                                     | Type           | Visibility | Role                                                                                                                                                          |
|----------------------------------------------------------|----------------|------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `Core/UserPath/UserRequestHandler<TRange,TData,TDomain>` | `sealed class` | internal   | Reads `CachedSegments`, computes gaps, fetches from `IDataSource`, assembles response, publishes event; implements `IAsyncDisposable` (cascades to scheduler) |

**Flow:**
```
UserRequestHandler.HandleRequestAsync(requestedRange, ct)
  1. FindIntersecting(requestedRange) → overlapping segments
  2. Compute gaps (sub-ranges not covered by any segment)
  3. For each gap: await dataSource.FetchAsync(gap, ct) → RangeChunk
  4. Assemble response from segments + fetched chunks (in-memory, local)
  5. Construct CacheNormalizationRequest { UsedSegments, FetchedData, RequestedRange }
  6. scheduler.ScheduleAsync(request) [fire-and-forget]
  7. Return RangeResult to caller
```

**Allocation profile per scenario:**

| Scenario    | Heap allocations | Details                                                                                                                                              |
|-------------|------------------|------------------------------------------------------------------------------------------------------------------------------------------------------|
| Full Hit    | 3                | Storage snapshot (irreducible) + `hittingRangeData` array + `pieces` pool rental + result array                                                      |
| Full Miss   | 3                | Storage snapshot + `[chunk]` wrapper + result data array                                                                                             |
| Partial Hit | 6                | Storage snapshot + `hittingRangeData` array + `PrependAndResume` state machine + chunks array + `merged` array + `pieces` pool rental + result array |

**Allocation strategy notes:**
- `hittingRangeData` and merged sources buffer are plain heap arrays (`new T[]`). Both cross `await` points, making `ArrayPool` or `ref struct` approaches structurally unsound. In the typical case (1–2 hitting segments) the arrays are tiny and short-lived (Gen0).
- The `pieces` working buffer inside `Assemble` is rented from `ArrayPool<T>.Shared` and returned before the method exits — `Assemble` is synchronous, so the rental scope is tight.
- `ComputeGaps` returns a deferred `IEnumerable<T>`; the caller probes it with a single `MoveNext()` call. On Partial Hit, `PrependAndResume` resumes the same enumerator — the chain is walked exactly once, no intermediate array is materialized for gaps.
- Each iteration in `ComputeGaps` passes the current remaining sequence and the segment range to a static local `Subtract` — no closure is created, eliminating one heap allocation per hitting segment compared to an equivalent `SelectMany` lambda.

---

## Subsystem 4 — Core: Background Path

| File                                                               | Type           | Visibility | Role                                                                                                                                                                                        |
|--------------------------------------------------------------------|----------------|------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `Core/Background/CacheNormalizationExecutor<TRange,TData,TDomain>` | `sealed class` | internal   | Processes `CacheNormalizationRequest`s; implements the four-step background sequence; sole storage writer (add path); delegates eviction to `EvictionEngine`, TTL scheduling to `TtlEngine` |

**Four-step sequence per event (Invariant VPC.B.3):** metadata update → storage + TTL scheduling → eviction evaluation + execution → post-removal. See `docs/visited-places/architecture.md` — Threading Model, Context 2 for the authoritative step-by-step description.

---

## Subsystem 5 — Core: Eviction

The eviction subsystem implements a **constraint satisfaction** model with five components. For full architecture, see `docs/visited-places/eviction.md`.

### Interfaces (Public)

| File                                            | Type      | Visibility | Role                                                                                                                     |
|-------------------------------------------------|-----------|------------|--------------------------------------------------------------------------------------------------------------------------|
| `Core/Eviction/IEvictionPolicy<TRange,TData>`   | interface | public     | Evaluates capacity constraint; produces `IEvictionPressure`; lifecycle: `OnSegmentAdded`, `OnSegmentRemoved`, `Evaluate` |
| `Core/Eviction/IEvictionPressure`               | interface | public     | Tracks constraint satisfaction: `IsExceeded`, `Reduce(segment)`                                                          |
| `Core/Eviction/IEvictionSelector<TRange,TData>` | interface | public     | Selects worst candidate via `TrySelectCandidate`; manages per-segment `IEvictionMetadata`                                |
| `Core/Eviction/IEvictionMetadata`               | interface | public     | Marker interface for selector-specific per-segment metadata                                                              |

### Policies (Public)

| File                                                              | Type           | Visibility | Role                                                                                     |
|-------------------------------------------------------------------|----------------|------------|------------------------------------------------------------------------------------------|
| `Core/Eviction/Policies/MaxSegmentCountPolicy<TRange,TData>`      | `sealed class` | public     | Fires when `CachedSegments.Count > maxCount`; O(1) via `Interlocked` count tracking      |
| `Core/Eviction/Policies/MaxTotalSpanPolicy<TRange,TData,TDomain>` | `sealed class` | public     | Fires when total span of all segments exceeds `maxTotalSpan`; O(1) via running aggregate |

### Pressure Types (Internal)

| File                                                     | Type           | Visibility | Role                                                                                                |
|----------------------------------------------------------|----------------|------------|-----------------------------------------------------------------------------------------------------|
| `Core/Eviction/Pressure/NoPressure<TRange,TData>`        | `sealed class` | public     | Singleton; `IsExceeded = false` always; returned when no policy fires                               |
| `Core/Eviction/Pressure/CompositePressure<TRange,TData>` | `sealed class` | internal   | Wraps multiple exceeded pressures; `IsExceeded = any child IsExceeded`; `Reduce` calls all children |

### Selectors (Public)

| File                                                                          | Type             | Visibility | Role                                                                                                                  |
|-------------------------------------------------------------------------------|------------------|------------|-----------------------------------------------------------------------------------------------------------------------|
| `Core/Eviction/SamplingEvictionSelector<TRange,TData>`                        | `abstract class` | public     | Base class for all built-in selectors; implements `TrySelectCandidate`; extension points: `EnsureMetadata`, `IsWorse` |
| `Core/Eviction/Selectors/LruEvictionSelector<TRange,TData>`                   | `sealed class`   | public     | Selects worst by `LruMetadata.LastAccessedAt` from random sample; uses `TimeProvider`                                 |
| `Core/Eviction/Selectors/FifoEvictionSelector<TRange,TData>`                  | `sealed class`   | public     | Selects worst by `FifoMetadata.CreatedAt` from random sample; uses `TimeProvider`                                     |
| `Core/Eviction/Selectors/SmallestFirstEvictionSelector<TRange,TData,TDomain>` | `sealed class`   | public     | Selects worst by `SmallestFirstMetadata.Span` from random sample; no `TimeProvider`                                   |

### Engine Components (Internal)

| File                                                  | Type           | Visibility | Role                                                                                                                            |
|-------------------------------------------------------|----------------|------------|---------------------------------------------------------------------------------------------------------------------------------|
| `Core/Eviction/EvictionEngine<TRange,TData>`          | `sealed class` | internal   | Single eviction facade for `CacheNormalizationExecutor`; orchestrates evaluator, executor, selector; fires eviction diagnostics |
| `Core/Eviction/EvictionExecutor<TRange,TData>`        | `sealed class` | internal   | Internal to `EvictionEngine`; runs constraint satisfaction loop; returns `toRemove` list                                        |
| `Core/Eviction/EvictionPolicyEvaluator<TRange,TData>` | `sealed class` | internal   | Internal to `EvictionEngine`; notifies all policies of lifecycle events; aggregates pressures into single `IEvictionPressure`   |

**Ownership hierarchy:**
```
CacheNormalizationExecutor
  └── EvictionEngine                     ← sole eviction dependency for the executor
        ├── EvictionPolicyEvaluator      ← hidden from executor
        │     └── IEvictionPolicy[]
        ├── EvictionExecutor             ← hidden from executor
        └── IEvictionSelector
```

---

## Subsystem 6 — Core: TTL

| File                                           | Type           | Visibility | Role                                                                                                                              |
|------------------------------------------------|----------------|------------|-----------------------------------------------------------------------------------------------------------------------------------|
| `Core/Ttl/TtlEngine<TRange,TData>`             | `sealed class` | internal   | Single TTL facade for `CacheNormalizationExecutor`; owns scheduler, activity counter, disposal CTS; implements `IAsyncDisposable` |
| `Core/Ttl/TtlExpirationExecutor<TRange,TData>` | `sealed class` | internal   | Internal to `TtlEngine`; awaits `Task.Delay`, calls `MarkAsRemoved()`, removes from storage, notifies engine                      |
| `Core/Ttl/TtlExpirationWorkItem<TRange,TData>` | `sealed class` | internal   | Internal to `TtlEngine`; carries segment reference and expiry timestamp                                                           |

**Ownership hierarchy:**
```
CacheNormalizationExecutor
  └── TtlEngine?                         ← sole TTL dependency; null if SegmentTtl not set
        ├── ConcurrentWorkScheduler      ← dispatches work items to thread pool
        ├── TtlExpirationExecutor        ← awaits delay, performs removal
        ├── AsyncActivityCounter         ← private; NOT the same as the cache's main counter
        └── CancellationTokenSource      ← cancelled on DisposeAsync
```

**Key design note:** `TtlEngine` uses its **own private `AsyncActivityCounter`**. This means `VisitedPlacesCache.WaitForIdleAsync()` does NOT wait for pending TTL delays — it only waits for the Background Storage Loop to drain. This is intentional: TTL delays can be arbitrarily long; blocking `WaitForIdleAsync` on them would make it unusable for tests.

---

## Subsystem 7 — Infrastructure: Storage

| File                                                                | Type             | Visibility | Role                                                                                                                                      |
|---------------------------------------------------------------------|------------------|------------|-------------------------------------------------------------------------------------------------------------------------------------------|
| `Infrastructure/Storage/ISegmentStorage<TRange,TData>`              | interface        | internal   | Core storage contract: `Add`, `AddRange`, `Remove`, `FindIntersecting`, `GetAll`, `GetRandomSegment`, `Count`                             |
| `Infrastructure/Storage/SegmentStorageBase<TRange,TData>`           | `abstract class` | internal   | Shared base for both strategies; implements `FindIntersecting` binary search anchor                                                       |
| `Infrastructure/Storage/SnapshotAppendBufferStorage<TRange,TData>`  | `sealed class`   | internal   | Default; sorted snapshot + unsorted append buffer; User Path reads snapshot; Background Path normalizes buffer into snapshot periodically |
| `Infrastructure/Storage/LinkedListStrideIndexStorage<TRange,TData>` | `sealed class`   | internal   | Alternative; doubly-linked list + stride index; O(log N) insertion + O(k) range query; better for high segment counts                     |

For performance characteristics and trade-offs, see `docs/visited-places/storage-strategies.md`.

### `ISegmentStorage` interface summary

```csharp
void Add(CachedSegment<TRange, TData> segment);
void AddRange(CachedSegment<TRange, TData>[] segments);  // Bulk insert for multi-gap events (FetchedChunks.Count > 1)
void Remove(CachedSegment<TRange, TData> segment);
IReadOnlyList<CachedSegment<TRange, TData>> FindIntersecting(Range<TRange> range);
IReadOnlyList<CachedSegment<TRange, TData>> GetAll();
CachedSegment<TRange, TData>? GetRandomSegment(Random rng);  // Used by selectors for O(1) sampling
int Count { get; }
```

---

## Subsystem 8 — Infrastructure: Adapters

| File                                                            | Type           | Visibility | Role                                                                                                                              |
|-----------------------------------------------------------------|----------------|------------|-----------------------------------------------------------------------------------------------------------------------------------|
| `Infrastructure/Adapters/VisitedPlacesWorkSchedulerDiagnostics` | `sealed class` | internal   | Adapts `IWorkSchedulerDiagnostics` to `IVisitedPlacesCacheDiagnostics`; maps scheduler lifecycle events to VPC diagnostic methods |

---

## Component Dependency Graph

```
VisitedPlacesCache (Public Facade / Composition Root)
│
├── UserRequestHandler (User Path)
│     ├── ISegmentStorage (read-only)
│     ├── IDataSource (gap fetches)
│     └── ISerialWorkScheduler → publishes CacheNormalizationRequest
│
├── AsyncActivityCounter (main)
│     └── WaitForIdleAsync support
│
└── TtlEngine? (TTL Path, optional)
      ├── ConcurrentWorkScheduler
      ├── TtlExpirationExecutor
      │     ├── ISegmentStorage (remove)
      │     └── EvictionEngine.OnSegmentRemoved
      ├── AsyncActivityCounter (private, TTL-only)
      └── CancellationTokenSource

─── Background Storage Loop ───────────────────────────────────────────────
ISerialWorkScheduler
  └── CacheNormalizationExecutor (Background Path)
        ├── ISegmentStorage (add + remove — sole add-path writer)
        ├── EvictionEngine (eviction facade)
        │     ├── EvictionPolicyEvaluator
        │     │     └── IEvictionPolicy[] (MaxSegmentCountPolicy, MaxTotalSpanPolicy, ...)
        │     ├── EvictionExecutor
        │     └── IEvictionSelector (LruEvictionSelector, FifoEvictionSelector, ...)
        └── TtlEngine? (schedules expiration work items)
```

---

## Source File Count Summary

| Subsystem                | Files  |
|--------------------------|--------|
| Public API               | 14     |
| Core: Shared Data Types  | 2      |
| Core: User Path          | 1      |
| Core: Background Path    | 1      |
| Core: Eviction           | 14     |
| Core: TTL                | 3      |
| Infrastructure: Storage  | 4      |
| Infrastructure: Adapters | 1      |
| **Total**                | **40** |

---

## Shared Foundation Components (from `Intervals.NET.Caching`)

VPC depends on the following shared foundation types (compiled into the assembly via `ProjectReference` with `PrivateAssets="all"`):

| Component                                        | Location                                                      | Role                                               |
|--------------------------------------------------|---------------------------------------------------------------|----------------------------------------------------|
| `IRangeCache<TRange,TData>`                      | `src/Intervals.NET.Caching/`                                  | Shared cache interface                             |
| `IDataSource<TRange,TData>`                      | `src/Intervals.NET.Caching/`                                  | Data source contract                               |
| `RangeResult<TRange,TData>`                      | `src/Intervals.NET.Caching/Dto/`                              | Return type for `GetDataAsync`                     |
| `RangeChunk<TRange,TData>`                       | `src/Intervals.NET.Caching/Dto/`                              | Single fetched chunk from `IDataSource`            |
| `CacheInteraction`                               | `src/Intervals.NET.Caching/Dto/`                              | `FullHit`, `PartialHit`, `FullMiss` enum           |
| `ICacheDiagnostics`                              | `src/Intervals.NET.Caching/`                                  | Base diagnostics interface                         |
| `AsyncActivityCounter`                           | `src/Intervals.NET.Caching/Infrastructure/Concurrency/`       | Lock-free activity tracking for `WaitForIdleAsync` |
| `ISerialWorkScheduler<T>`                        | `src/Intervals.NET.Caching/Infrastructure/Scheduling/Serial/` | Background serialization abstraction               |
| `UnboundedSerialWorkScheduler<T>`                | `src/Intervals.NET.Caching/Infrastructure/Scheduling/Serial/` | Default lock-free task-chaining scheduler          |
| `BoundedSerialWorkScheduler<T>`                  | `src/Intervals.NET.Caching/Infrastructure/Scheduling/Serial/` | Bounded-channel scheduler with backpressure        |
| `ConcurrentWorkScheduler<T>`                     | `src/Intervals.NET.Caching/Infrastructure/Scheduling/`        | Fire-and-forget scheduler (used by TTL)            |
| `LayeredRangeCache<TRange,TData>`                | `src/Intervals.NET.Caching/Layered/`                          | Multi-layer cache wrapper                          |
| `LayeredRangeCacheBuilder<TRange,TData,TDomain>` | `src/Intervals.NET.Caching/Layered/`                          | Fluent layered cache builder                       |
| `RangeCacheDataSourceAdapter<TRange,TData>`      | `src/Intervals.NET.Caching/Layered/`                          | Adapts `IRangeCache` as `IDataSource`              |
| `RangeCacheConsistencyExtensions`                | `src/Intervals.NET.Caching/Extensions/`                       | `GetDataAndWaitForIdleAsync` extension             |

For shared component details, see `docs/shared/components/` (infrastructure, public-api, layered).

---

## See Also

- `docs/visited-places/actors.md` — actor responsibilities per component
- `docs/visited-places/architecture.md` — threading model, FIFO vs. supersession, disposal
- `docs/visited-places/eviction.md` — full eviction architecture
- `docs/visited-places/storage-strategies.md` — storage strategy internals
- `docs/visited-places/diagnostics.md` — full diagnostics event reference
- `docs/shared/components/` — shared foundation component catalog
