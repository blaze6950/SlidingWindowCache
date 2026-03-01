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

Returned by `GetDataAsync`. `Range` may be null for physical boundary misses (when `IDataSource` returns null for the requested range).

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

## See Also

- `docs/boundary-handling.md`
- `docs/diagnostics.md`
- `docs/invariants.md`
- `docs/storage-strategies.md`
