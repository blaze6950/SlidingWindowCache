# Components: Public API

## Overview

This page documents the public surface area of SlidingWindowCache: the cache facade, configuration, data source contract, diagnostics, and public DTOs.

## Facade

- `WindowCache<TRange, TData, TDomain>`: primary entry point and composition root.
  - **File**: `src/SlidingWindowCache/Public/WindowCache.cs`
  - Constructs and wires all internal components.
  - Delegates user requests to `UserRequestHandler`.
  - Exposes `WaitForIdleAsync()` for infrastructure/testing synchronization.
- `IWindowCache<TRange, TData, TDomain>`: interface for the facade (for testing/mocking).

## Configuration

### WindowCacheOptions

**File**: `src/SlidingWindowCache/Public/Configuration/WindowCacheOptions.cs`

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

**Invariants**: E.34, E.35 (NoRebalanceRange computation and threshold sum constraint).

### UserCacheReadMode

**File**: `src/SlidingWindowCache/Public/Configuration/UserCacheReadMode.cs`

**Type**: `enum`

| Value        | Description                                                     | Trade-off                                 |
|--------------|-----------------------------------------------------------------|-------------------------------------------|
| `Snapshot`   | Array-based; zero-allocation reads, expensive rematerialization | Fast reads, LOH pressure for large caches |
| `CopyOnRead` | List-based; cheap rematerialization, copy-per-read              | Fast rebalance, allocation on each read   |

**See**: `docs/storage-strategies.md` for detailed comparison and usage scenarios.

## Data Source

### IDataSource\<TRange, TData\>

**File**: `src/SlidingWindowCache/Public/IDataSource.cs`

**Type**: Interface (user-implemented)

- Single-range fetch (required): `FetchAsync(Range<TRange>, CancellationToken)`
- Batch fetch (optional): default implementation uses parallel single-range fetches
- Cancellation is cooperative; implementations must respect `CancellationToken`

**Called from two contexts:**
- **User Path** (`UserRequestHandler`): on cold start (uninitialized cache), full cache miss (no overlap with current cache range), and partial cache hit (for the uncached portion via `CacheDataExtensionService`). These are synchronous to the user request — the user awaits the result.
- **Background Execution Path** (`CacheDataExtensionService` via `RebalanceExecutor`): for incremental cache expansion during background rebalance. Only missing sub-ranges are fetched.

**Implementations must be safe to call from both contexts** and must not assume a single caller thread.

## DTOs

### RangeResult\<TRange, TData\>

**File**: `src/SlidingWindowCache/Public/DTO/RangeResult.cs`

Returned by `GetDataAsync`. Contains three properties:

| Property           | Type                    | Description                                                                                                                 |
|--------------------|-------------------------|-----------------------------------------------------------------------------------------------------------------------------|
| `Range`            | `Range<TRange>?`        | **Nullable**. The actual range returned. `null` indicates no data available (physical boundary miss).                       |
| `Data`             | `ReadOnlyMemory<TData>` | The materialized data. Empty when `Range` is `null`.                                                                        |
| `CacheInteraction` | `CacheInteraction`      | How the request was served: `FullHit` (from cache), `PartialHit` (cache + fetch), or `FullMiss` (cold start or jump fetch). |

`RangeResult` constructor is `internal`; instances are created exclusively by `UserRequestHandler`.

### CacheInteraction

**File**: `src/SlidingWindowCache/Public/Dto/CacheInteraction.cs`

**Type**: `enum`

Classifies how a `GetDataAsync` request was served relative to the current cache state.

| Value        | Meaning                                                                                         |
|--------------|-------------------------------------------------------------------------------------------------|
| `FullMiss`   | Cache was uninitialized (cold start) or `RequestedRange` did not intersect `CurrentCacheRange`. |
| `FullHit`    | `RequestedRange` was fully contained within `CurrentCacheRange`.                                |
| `PartialHit` | `RequestedRange` partially overlapped `CurrentCacheRange`; missing segments were fetched.       |

**Usage**: Inspect `result.CacheInteraction` to branch on cache efficiency per request. The `GetDataAndWaitOnMissAsync` extension method uses this value to decide whether to call `WaitForIdleAsync`.

**Note**: `ICacheDiagnostics` provides the same three-way classification via `UserRequestFullCacheHit`, `UserRequestPartialCacheHit`, and `UserRequestFullCacheMiss` callbacks — those are aggregate counters; `CacheInteraction` is the per-request programmatic alternative.

### RangeChunk\<TRange, TData\>

**File**: `src/SlidingWindowCache/Public/DTO/RangeChunk.cs`

Batch fetch result from `IDataSource`. Contains:
- `Range<TRange> Range` — the range covered by this chunk
- `IEnumerable<TData> Data` — the data for this range

## Diagnostics

### ICacheDiagnostics

**File**: `src/SlidingWindowCache/Public/Instrumentation/ICacheDiagnostics.cs`

Optional observability interface with 18 event recording methods covering:
- User request outcomes (full hit, partial hit, full miss)
- Data source access events and data unavailability (`DataSegmentUnavailable`)
- Rebalance intent events (published)
- Rebalance execution lifecycle (started, completed, failed via `RebalanceExecutionFailed`)
- Rebalance skip optimizations (NoRebalanceRange stage 1 & 2, same-range short-circuit)

**Implementations**:
- `EventCounterCacheDiagnostics` — thread-safe atomic counter implementation (use for testing and monitoring)
- `NoOpDiagnostics` — zero-overhead default when no diagnostics provided (JIT eliminates all calls)

**See**: `docs/diagnostics.md` for comprehensive usage documentation.

> ⚠️ **Critical**: `RebalanceExecutionFailed` is the only event that signals a background exception. Always wire this in production code.

## Extensions

### WindowCacheConsistencyExtensions

**File**: `src/SlidingWindowCache/Public/WindowCacheConsistencyExtensions.cs`

**Type**: `static class` (extension methods on `IWindowCache<TRange, TData, TDomain>`)

Provides opt-in hybrid and strong consistency modes on top of the default eventual consistency model.

#### GetDataAndWaitOnMissAsync

```csharp
ValueTask<RangeResult<TRange, TData>> GetDataAndWaitOnMissAsync<TRange, TData, TDomain>(
    this IWindowCache<TRange, TData, TDomain> cache,
    Range<TRange> requestedRange,
    CancellationToken cancellationToken = default)
```

Composes `GetDataAsync` + conditional `WaitForIdleAsync` into a single call. Waits for idle only when `result.CacheInteraction != CacheInteraction.FullHit` — i.e., on cold start, jump, or partial hit where a rebalance was triggered. Returns immediately (no idle wait) on a `FullHit`.

**When to use:**
- Warm-cache guarantee on the first request to a new region (cold start or jump)
- Sequential access patterns where occasional rebalances should be awaited but hot hits should not
- Lower overhead than `GetDataAndWaitForIdleAsync` for workloads with frequent `FullHit` results

**When NOT to use:**
- Parallel callers — the "warm cache after await" guarantee requires serialized (one-at-a-time) access (Invariant H.49)
- Hot paths — even though `FullHit` skips the wait, missed requests still incur the full rebalance cycle delay

**Idle semantics**: Inherits "was idle at some point" semantics from `WaitForIdleAsync` (Invariant H.49).

**Exception propagation**: If `GetDataAsync` throws, `WaitForIdleAsync` is never called. If `WaitForIdleAsync` throws `OperationCanceledException`, the already-obtained result is returned (graceful degradation to eventual consistency). Other exceptions from `WaitForIdleAsync` propagate normally.

#### GetDataAndWaitForIdleAsync

```csharp
ValueTask<RangeResult<TRange, TData>> GetDataAndWaitForIdleAsync<TRange, TData, TDomain>(
    this IWindowCache<TRange, TData, TDomain> cache,
    Range<TRange> requestedRange,
    CancellationToken cancellationToken = default)
```

Composes `GetDataAsync` + `WaitForIdleAsync` into a single call. Always waits for idle regardless of `CacheInteraction`. Returns the same `RangeResult<TRange, TData>` as `GetDataAsync`, but does not complete until the cache has reached an idle state.

**When to use:**
- Asserting or inspecting cache geometry after a request (e.g., verifying a rebalance occurred)
- Cold start synchronization before subsequent operations
- Integration tests that require deterministic cache state before making assertions

**When NOT to use:**
- Hot paths — the idle wait adds latency equal to the full rebalance cycle (debounce delay + data fetch + cache update)
- Rapid sequential requests — eliminates debounce and work-avoidance benefits
- Parallel callers — same serialized access requirement as `GetDataAndWaitOnMissAsync`

**Idle semantics**: Inherits "was idle at some point" semantics from `WaitForIdleAsync` (Invariant H.49). Unlike `GetDataAndWaitOnMissAsync`, always waits even on `FullHit`.

**Exception propagation**: If `GetDataAsync` throws, `WaitForIdleAsync` is never called. If `WaitForIdleAsync` throws `OperationCanceledException`, the already-obtained result is returned (graceful degradation to eventual consistency). Other exceptions from `WaitForIdleAsync` propagate normally.

**See**: `README.md` (Consistency Modes section) and `docs/architecture.md` for broader context.

## Multi-Layer Cache

Three classes support building layered cache stacks where each layer's data source is the layer below it:

### WindowCacheDataSourceAdapter\<TRange, TData, TDomain\>

**File**: `src/SlidingWindowCache/Public/WindowCacheDataSourceAdapter.cs`

**Type**: `sealed class` implementing `IDataSource<TRange, TData>`

Wraps an `IWindowCache` as an `IDataSource`, allowing any `WindowCache` to act as the data source for an outer `WindowCache`. Data is retrieved using eventual consistency (`GetDataAsync`).

- Wraps `ReadOnlyMemory<TData>` (returned by `IWindowCache.GetDataAsync`) in a `ReadOnlyMemoryEnumerable<TData>` to satisfy the `IEnumerable<TData>` contract of `IDataSource.FetchAsync`. This avoids allocating a temporary `TData[]` copy — the wrapper holds only a reference to the existing backing array via `ReadOnlyMemory<TData>`, and the data is enumerated lazily in a single pass during the outer cache's rematerialization.
- Does **not** own the wrapped cache; the caller is responsible for disposing it.

### LayeredWindowCache\<TRange, TData, TDomain\>

**File**: `src/SlidingWindowCache/Public/LayeredWindowCache.cs`

**Type**: `sealed class` implementing `IWindowCache<TRange, TData, TDomain>` and `IAsyncDisposable`

A thin wrapper that:
- Delegates `GetDataAsync` to the outermost layer.
- **`WaitForIdleAsync` awaits all layers sequentially, outermost to innermost.** The outer layer is awaited first because its rebalance drives fetch requests into inner layers. This ensures `GetDataAndWaitForIdleAsync` correctly waits for the entire cache stack to converge.
- **Owns** all layer `WindowCache` instances and disposes them in reverse order (outermost first) when disposed.
- Exposes `LayerCount` for inspection.

Typically created via `LayeredWindowCacheBuilder.Build()` rather than directly.

### LayeredWindowCacheBuilder\<TRange, TData, TDomain\>

**File**: `src/SlidingWindowCache/Public/LayeredWindowCacheBuilder.cs`

**Type**: `sealed class` — fluent builder

```csharp
await using var cache = LayeredWindowCacheBuilder<int, byte[], IntegerFixedStepDomain>
    .Create(realDataSource, domain)
    .AddLayer(deepOptions)   // L2: inner layer (CopyOnRead, large buffers)
    .AddLayer(userOptions)   // L1: outer layer (Snapshot, small buffers)
    .Build();
```

- `Create(dataSource, domain)` — factory entry point; validates both `dataSource` and `domain` are not null.
- `AddLayer(options, diagnostics?)` — adds a layer on top; first call = innermost layer, last call = outermost (user-facing).
- `Build()` — constructs all `WindowCache` instances, wires them via `WindowCacheDataSourceAdapter`, and wraps them in `LayeredWindowCache`.
- Throws `InvalidOperationException` from `Build()` if no layers were added.

**See**: `README.md` (Multi-Layer Cache section) and `docs/storage-strategies.md` for recommended layer configuration patterns.

- `docs/boundary-handling.md`
- `docs/diagnostics.md`
- `docs/invariants.md`
- `docs/storage-strategies.md`
