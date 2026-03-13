# Components: Public API

## Overview

This page documents the public surface area of `Intervals.NET.Caching.SlidingWindow` and `Intervals.NET.Caching`: the cache facade, shared interfaces, configuration, data source contract, diagnostics, and public DTOs.

## Packages

### Intervals.NET.Caching

Shared contracts and infrastructure for all cache implementations:

- `IRangeCache<TRange, TData, TDomain>` — shared cache interface: `GetDataAsync`, `WaitForIdleAsync`, `IAsyncDisposable`
- `IDataSource<TRange, TData>` — data source contract
- `RangeResult<TRange, TData>`, `RangeChunk<TRange, TData>`, `CacheInteraction` — shared DTOs
- `LayeredRangeCache<TRange, TData, TDomain>` — thin `IRangeCache` wrapper for layered stacks
- `RangeCacheDataSourceAdapter<TRange, TData, TDomain>` — adapts `IRangeCache` as `IDataSource`
- `LayeredRangeCacheBuilder<TRange, TData, TDomain>` — fluent builder for layered stacks
- `RangeCacheConsistencyExtensions` — `GetDataAndWaitForIdleAsync` (strong consistency) on `IRangeCache`

### Intervals.NET.Caching.SlidingWindow

SlidingWindow-specific implementation:

- `SlidingWindowCache<TRange, TData, TDomain>` — primary entry point; implements `ISlidingWindowCache`
- `ISlidingWindowCache<TRange, TData, TDomain>` — extends `IRangeCache`; adds `UpdateRuntimeOptions` + `CurrentRuntimeOptions`
- `SlidingWindowCacheBuilder` — builder for single-layer and layered SlidingWindow caches
- `SlidingWindowCacheConsistencyExtensions` — `GetDataAndWaitOnMissAsync` (hybrid consistency) on `ISlidingWindowCache`
- `SlidingWindowCacheOptions` / `SlidingWindowCacheOptionsBuilder` — configuration
- `ICacheDiagnostics` / `EventCounterCacheDiagnostics` / `NoOpDiagnostics` — instrumentation

## Facade

- `SlidingWindowCache<TRange, TData, TDomain>`: primary entry point and composition root.
  - **File**: `src/Intervals.NET.Caching.SlidingWindow/Public/Cache/SlidingWindowCache.cs`
  - Constructs and wires all internal components.
  - Delegates user requests to `UserRequestHandler`.
  - Exposes `WaitForIdleAsync()` for infrastructure/testing synchronization.
- `ISlidingWindowCache<TRange, TData, TDomain>`: interface for the facade (for testing/mocking); extends `IRangeCache`.
  - **File**: `src/Intervals.NET.Caching.SlidingWindow/Public/ISlidingWindowCache.cs`
- `IRangeCache<TRange, TData, TDomain>`: shared base interface.
  - **File**: `src/Intervals.NET.Caching/IRangeCache.cs`

## Configuration

### SlidingWindowCacheOptions

**File**: `src/Intervals.NET.Caching.SlidingWindow/Public/Configuration/SlidingWindowCacheOptions.cs`

**Type**: `record` (immutable, value semantics)

Configuration parameters:

| Parameter                   | Description                                        |
|-----------------------------|----------------------------------------------------|
| `LeftCacheSize`             | Left window coefficient (≥ 0)                      |
| `RightCacheSize`            | Right window coefficient (≥ 0)                     |
| `LeftNoRebalanceThreshold`  | Left stability zone threshold (optional, ≥ 0)      |
| `RightNoRebalanceThreshold` | Right stability zone threshold (optional, ≥ 0)     |
| `RebalanceDebounceDelay`    | Delay before executing a validated rebalance       |
| `UserCacheReadMode`         | Storage strategy (`Snapshot` or `CopyOnRead`)      |
| `RebalanceQueueCapacity`    | Optional; selects channel-based execution when set |

**Validation enforced at construction time:**
- Cache sizes ≥ 0
- Individual thresholds ≥ 0 (when specified)
- `LeftNoRebalanceThreshold + RightNoRebalanceThreshold ≤ 1.0` (prevents overlapping shrinkage zones)
- `RebalanceQueueCapacity > 0` (when specified)

**Invariants**: SWC.E.5, SWC.E.6 (NoRebalanceRange computation and threshold sum constraint).

### UserCacheReadMode

**File**: `src/Intervals.NET.Caching.SlidingWindow/Public/Configuration/UserCacheReadMode.cs`

**Type**: `enum`

| Value        | Description                                                     | Trade-off                                 |
|--------------|-----------------------------------------------------------------|-------------------------------------------|
| `Snapshot`   | Array-based; zero-allocation reads, expensive rematerialization | Fast reads, LOH pressure for large caches |
| `CopyOnRead` | List-based; cheap rematerialization, copy-per-read              | Fast rebalance, allocation on each read   |

**See**: `docs/sliding-window/storage-strategies.md` for detailed comparison and usage scenarios.

## Data Source

### IDataSource\<TRange, TData\>

**File**: `src/Intervals.NET.Caching/IDataSource.cs`

**Type**: Interface (user-implemented); lives in `Intervals.NET.Caching`

- Single-range fetch (required): `FetchAsync(Range<TRange>, CancellationToken)`
- Batch fetch (optional): default implementation uses parallel single-range fetches
- Cancellation is cooperative; implementations must respect `CancellationToken`

**Called from two contexts:**
- **User Path** (`UserRequestHandler`): on cold start (uninitialized cache), full cache miss (no overlap with current cache range), and partial cache hit (for the uncached portion via `CacheDataExtensionService`). These are synchronous to the user request — the user awaits the result.
- **Background Execution Path** (`CacheDataExtensionService` via `RebalanceExecutor`): for incremental cache expansion during background rebalance. Only missing sub-ranges are fetched.

**Implementations must be safe to call from both contexts** and must not assume a single caller thread.

## DTOs

All DTOs live in `Intervals.NET.Caching`.

### RangeResult\<TRange, TData\>

**File**: `src/Intervals.NET.Caching/Dto/RangeResult.cs`

Returned by `GetDataAsync`. Contains three properties:

| Property           | Type                    | Description                                                                                                                 |
|--------------------|-------------------------|-----------------------------------------------------------------------------------------------------------------------------|
| `Range`            | `Range<TRange>?`        | **Nullable**. The actual range returned. `null` indicates no data available (physical boundary miss).                       |
| `Data`             | `ReadOnlyMemory<TData>` | The materialized data. Empty when `Range` is `null`.                                                                        |
| `CacheInteraction` | `CacheInteraction`      | How the request was served: `FullHit` (from cache), `PartialHit` (cache + fetch), or `FullMiss` (cold start or jump fetch). |

`RangeResult` constructor is `public`; instances are created by `UserRequestHandler` (and potentially by other `IRangeCache` implementations).

### CacheInteraction

**File**: `src/Intervals.NET.Caching/Dto/CacheInteraction.cs`

**Type**: `enum`

Classifies how a `GetDataAsync` request was served relative to the current cache state.

| Value        | Meaning                                                                                         |
|--------------|-------------------------------------------------------------------------------------------------|
| `FullMiss`   | Cache was uninitialized (cold start) or `RequestedRange` did not intersect `CurrentCacheRange`. |
| `FullHit`    | `RequestedRange` was fully contained within `CurrentCacheRange`.                                |
| `PartialHit` | `RequestedRange` partially overlapped `CurrentCacheRange`; missing segments were fetched.       |

**Usage**: Inspect `result.CacheInteraction` to branch on cache efficiency per request. The `GetDataAndWaitOnMissAsync` extension method (on `ISlidingWindowCache`) uses this value to decide whether to call `WaitForIdleAsync`.

**Note**: `ICacheDiagnostics` provides the same three-way classification via `UserRequestFullCacheHit`, `UserRequestPartialCacheHit`, and `UserRequestFullCacheMiss` callbacks — those are aggregate counters; `CacheInteraction` is the per-request programmatic alternative.

### RangeChunk\<TRange, TData\>

**File**: `src/Intervals.NET.Caching/Dto/RangeChunk.cs`

Batch fetch result from `IDataSource`. Contains:
- `Range<TRange> Range` — the range covered by this chunk
- `IEnumerable<TData> Data` — the data for this range

## Diagnostics

### ICacheDiagnostics

**File**: `src/Intervals.NET.Caching.SlidingWindow/Public/Instrumentation/ICacheDiagnostics.cs`

Optional observability interface with 18 event recording methods covering:
- User request outcomes (full hit, partial hit, full miss)
- Data source access events and data unavailability (`DataSegmentUnavailable`)
- Rebalance intent events (published)
- Rebalance execution lifecycle (started, completed, failed via `RebalanceExecutionFailed`)
- Rebalance skip optimizations (NoRebalanceRange stage 1 & 2, same-range short-circuit)

**Implementations**:
- `EventCounterCacheDiagnostics` — thread-safe atomic counter implementation (use for testing and monitoring)
- `NoOpDiagnostics` — zero-overhead default when no diagnostics provided (JIT eliminates all calls)

**See**: `docs/sliding-window/diagnostics.md` for comprehensive usage documentation.

> ⚠️ **Critical**: `RebalanceExecutionFailed` is the only event that signals a background exception. Always wire this in production code.

## Extensions

### SlidingWindowCacheConsistencyExtensions

**File**: `src/Intervals.NET.Caching.SlidingWindow/Public/Extensions/SlidingWindowCacheConsistencyExtensions.cs`

**Type**: `static class` (extension methods on `ISlidingWindowCache<TRange, TData, TDomain>`)

Provides the **hybrid consistency mode** on top of the default eventual consistency model.

#### GetDataAndWaitOnMissAsync

```csharp
ValueTask<RangeResult<TRange, TData>> GetDataAndWaitOnMissAsync<TRange, TData, TDomain>(
    this ISlidingWindowCache<TRange, TData, TDomain> cache,
    Range<TRange> requestedRange,
    CancellationToken cancellationToken = default)
```

Composes `GetDataAsync` + conditional `WaitForIdleAsync` into a single call. Waits for idle only when `result.CacheInteraction != CacheInteraction.FullHit` — i.e., on cold start, jump, or partial hit where a rebalance was triggered. Returns immediately (no idle wait) on a `FullHit`.

**SlidingWindow-specific**: This extension is on `ISlidingWindowCache`, not `IRangeCache`. It exploits `CacheInteraction` semantics specific to the SlidingWindow implementation.

**When to use:**
- Warm-cache guarantee on the first request to a new region (cold start or jump)
- Sequential access patterns where occasional rebalances should be awaited but hot hits should not
- Lower overhead than `GetDataAndWaitForIdleAsync` for workloads with frequent `FullHit` results

**When NOT to use:**
- Parallel callers — the "warm cache after await" guarantee requires serialized (one-at-a-time) access (Invariant S.H.3)
- Hot paths — even though `FullHit` skips the wait, missed requests still incur the full rebalance cycle delay

**Idle semantics**: Inherits "was idle at some point" semantics from `WaitForIdleAsync` (Invariant S.H.3).

**Exception propagation**: If `GetDataAsync` throws, `WaitForIdleAsync` is never called. If `WaitForIdleAsync` throws `OperationCanceledException`, the already-obtained result is returned (graceful degradation to eventual consistency). Other exceptions from `WaitForIdleAsync` propagate normally.

### RangeCacheConsistencyExtensions

**File**: `src/Intervals.NET.Caching/Extensions/RangeCacheConsistencyExtensions.cs`

**Type**: `static class` (extension methods on `IRangeCache<TRange, TData, TDomain>`)

Provides the **strong consistency mode** shared across all `IRangeCache` implementations.

#### GetDataAndWaitForIdleAsync

```csharp
ValueTask<RangeResult<TRange, TData>> GetDataAndWaitForIdleAsync<TRange, TData, TDomain>(
    this IRangeCache<TRange, TData, TDomain> cache,
    Range<TRange> requestedRange,
    CancellationToken cancellationToken = default)
```

Composes `GetDataAsync` + `WaitForIdleAsync` into a single call. Always waits for idle regardless of `CacheInteraction`. Returns the same `RangeResult<TRange, TData>` as `GetDataAsync`, but does not complete until the cache has reached an idle state.

**Shared**: This extension is on `IRangeCache` (in `Intervals.NET.Caching`) and works for all cache implementations including `LayeredRangeCache`.

**When to use:**
- Asserting or inspecting cache geometry after a request (e.g., verifying a rebalance occurred)
- Cold start synchronization before subsequent operations
- Integration tests that require deterministic cache state before making assertions

**When NOT to use:**
- Hot paths — the idle wait adds latency equal to the full rebalance cycle (debounce delay + data fetch + cache update)
- Rapid sequential requests — eliminates debounce and work-avoidance benefits
- Parallel callers — same serialized access requirement as `GetDataAndWaitOnMissAsync`

**Idle semantics**: Inherits "was idle at some point" semantics from `WaitForIdleAsync` (Invariant S.H.3). Unlike `GetDataAndWaitOnMissAsync`, always waits even on `FullHit`.

**Exception propagation**: If `GetDataAsync` throws, `WaitForIdleAsync` is never called. If `WaitForIdleAsync` throws `OperationCanceledException`, the already-obtained result is returned (graceful degradation to eventual consistency). Other exceptions from `WaitForIdleAsync` propagate normally.

**See**: `README.md` (Consistency Modes section) and `docs/sliding-window/architecture.md` for broader context.

## Multi-Layer Cache

Three classes in `Intervals.NET.Caching` support building layered cache stacks where each layer's data source is the layer below it. `SlidingWindowCacheBuilder` provides the `AddSlidingWindowLayer` extension for convenience.

### RangeCacheDataSourceAdapter\<TRange, TData, TDomain\>

**File**: `src/Intervals.NET.Caching/Layered/RangeCacheDataSourceAdapter.cs`

**Type**: `sealed class` implementing `IDataSource<TRange, TData>`

Wraps an `IRangeCache` as an `IDataSource`, allowing any `IRangeCache` implementation to act as the data source for an outer cache. Data is retrieved using eventual consistency (`GetDataAsync`).

- Wraps `ReadOnlyMemory<TData>` (returned by `IRangeCache.GetDataAsync`) in a `ReadOnlyMemoryEnumerable<TData>` to satisfy the `IEnumerable<TData>` contract of `IDataSource.FetchAsync`. This avoids allocating a temporary `TData[]` copy — the wrapper holds only a reference to the existing backing array via `ReadOnlyMemory<TData>`, and the data is enumerated lazily in a single pass during the outer cache's rematerialization.
- Does **not** own the wrapped cache; the caller is responsible for disposing it.

### LayeredRangeCache\<TRange, TData, TDomain\>

**File**: `src/Intervals.NET.Caching/Layered/LayeredRangeCache.cs`

**Type**: `sealed class` implementing `IRangeCache<TRange, TData, TDomain>` and `IAsyncDisposable`

A thin wrapper that:
- Delegates `GetDataAsync` to the outermost layer.
- **`WaitForIdleAsync` awaits all layers sequentially, outermost to innermost.** The outer layer is awaited first because its rebalance drives fetch requests into inner layers. This ensures `GetDataAndWaitForIdleAsync` correctly waits for the entire cache stack to converge.
- **Owns** all layer cache instances and disposes them in reverse order (outermost first) when disposed.
- Exposes `LayerCount` for inspection.
- Implements `IRangeCache` only (not `ISlidingWindowCache`); `UpdateRuntimeOptions`/`CurrentRuntimeOptions` are not delegated.

Typically created via `LayeredRangeCacheBuilder.BuildAsync()` rather than directly. Constructor is `internal`; use the builder.

### LayeredRangeCacheBuilder\<TRange, TData, TDomain\>

**File**: `src/Intervals.NET.Caching/Layered/LayeredRangeCacheBuilder.cs`

**Type**: `sealed class` — fluent builder

```csharp
await using var cache = await SlidingWindowCacheBuilder.Layered(realDataSource, domain)
    .AddSlidingWindowLayer(deepOptions)   // L2: inner layer (CopyOnRead, large buffers)
    .AddSlidingWindowLayer(userOptions)   // L1: outer layer (Snapshot, small buffers)
    .BuildAsync();
```

- Obtain an instance via `SlidingWindowCacheBuilder.Layered(dataSource, domain)` — enables full generic type inference.
- `AddLayer(Func<IDataSource, IRangeCache>)` — generic factory-based layer addition.
- `AddSlidingWindowLayer(options, diagnostics?)` — convenience extension method (in SlidingWindow package); first call = innermost layer, last call = outermost (user-facing). Also accepts `Action<SlidingWindowCacheOptionsBuilder>` for inline configuration.
- `BuildAsync()` — constructs all cache instances, wires them via `RangeCacheDataSourceAdapter`, and wraps them in `LayeredRangeCache`. Returns `ValueTask<IRangeCache<TRange, TData, TDomain>>`; concrete type is `LayeredRangeCache<>`.
- Throws `InvalidOperationException` from `BuildAsync()` if no layers were added, or if an inline delegate fails validation.

**See**: `README.md` (Multi-Layer Cache section) and `docs/sliding-window/storage-strategies.md` for recommended layer configuration patterns.

## See Also

- `docs/sliding-window/boundary-handling.md`
- `docs/sliding-window/diagnostics.md`
- `docs/sliding-window/invariants.md`
- `docs/sliding-window/storage-strategies.md`
