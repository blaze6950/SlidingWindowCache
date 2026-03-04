# Components: State and Storage

## Overview

State and storage define how cached data is held, read, and published. `CacheState` is the central shared mutable state of the system — written exclusively by `RebalanceExecutor`, and read by `UserRequestHandler` and `RebalanceDecisionEngine`.

## Key Components

| Component                                     | File                                                                   | Role                                                |
|-----------------------------------------------|------------------------------------------------------------------------|-----------------------------------------------------|
| `CacheState<TRange, TData, TDomain>`          | `src/Intervals.NET.Caching/Core/State/CacheState.cs`                      | Shared mutable state; the single coordination point |
| `ICacheStorage<TRange, TData, TDomain>`       | `src/Intervals.NET.Caching/Infrastructure/Storage/ICacheStorage.cs`       | Internal storage contract                           |
| `SnapshotReadStorage<TRange, TData, TDomain>` | `src/Intervals.NET.Caching/Infrastructure/Storage/SnapshotReadStorage.cs` | Array-based; zero-allocation reads                  |
| `CopyOnReadStorage<TRange, TData, TDomain>`   | `src/Intervals.NET.Caching/Infrastructure/Storage/CopyOnReadStorage.cs`   | List-based; cheap rematerialization                 |

## CacheState

**File**: `src/Intervals.NET.Caching/Core/State/CacheState.cs`

`CacheState` is shared by reference across `UserRequestHandler`, `RebalanceDecisionEngine`, and `RebalanceExecutor`. It holds:

| Field              | Type            | Written by               | Read by                                |
|--------------------|-----------------|--------------------------|----------------------------------------|
| `Storage`          | `ICacheStorage` | `RebalanceExecutor` only | `UserRequestHandler`, `DecisionEngine` |
| `IsInitialized`    | `bool`          | `RebalanceExecutor` only | `UserRequestHandler`                   |
| `NoRebalanceRange` | `Range?`        | `RebalanceExecutor` only | `DecisionEngine`                       |

**Single-Writer Rule (Invariants A.12a, F.2):** Only `RebalanceExecutor` writes any field of `CacheState`. User path components are read-only. This is enforced by internal visibility modifiers (setters are `internal`), not by locks.

**Visibility model:** `CacheState` itself has no locks. Cross-thread visibility for `IsInitialized` and `NoRebalanceRange` is provided by the single-writer architecture — only one background thread ever writes these fields, and readers accept eventual consistency. Storage-level thread safety is handled inside each `ICacheStorage` implementation: `SnapshotReadStorage` uses a `volatile` array field with release/acquire fence ordering; `CopyOnReadStorage` uses a `lock` for its active-buffer swap and all reads.

**Atomic updates via `Rematerialize`:** The `Rematerialize` method replaces the storage contents in a single atomic operation (under `lock` for `CopyOnReadStorage`, via `volatile` write for `SnapshotReadStorage`). No intermediate states are visible to readers.

## Storage Strategies

### SnapshotReadStorage

**Type**: `internal sealed class`

**Strategy**: Array-based with atomic replacement on rematerialization.

| Operation       | Behavior                                                                      |
|-----------------|-------------------------------------------------------------------------------|
| `Rematerialize` | Allocates new `TData[]`, performs `Array.Copy`, atomically replaces reference |
| `Read`          | Returns zero-allocation `ReadOnlyMemory<TData>` view over internal array      |
| `ToRangeData`   | Creates snapshot from current array                                           |

**Characteristics**:
- ✅ Zero-allocation reads (fastest user path)
- ❌ Expensive rematerialization (always allocates new array)
- ⚠️ Large arrays (≥ 85 KB) may end up on the LOH
- Best for: read-heavy workloads, predictable memory patterns

### CopyOnReadStorage

**Type**: `internal sealed class`

**Strategy**: Dual-buffer pattern — active storage is never mutated during enumeration.

| Operation       | Behavior                                                                                         |
|-----------------|--------------------------------------------------------------------------------------------------|
| `Rematerialize` | Fills staging buffer outside the lock, then atomically swaps staging/active buffers under lock   |
| `Read`          | Acquires lock, allocates `TData[]`, copies from active buffer, returns `ReadOnlyMemory`          |
| `ToRangeData`   | Acquires lock, copies active buffer via `.ToArray()`, returns immutable `RangeData` snapshot     |

**Staging Buffer Pattern:**
```
Active buffer:   [existing data]  ← user reads here (lock-protected)
Staging buffer:  [new data]       ← rematerialization builds here (outside lock)
                      ↓ swap (under lock, sub-microsecond)
Active buffer:   [new data]       ← now visible to reads
Staging buffer:  [old data]       ← reused next rematerialization (capacity preserved)
```

**Characteristics**:
- ✅ Cheap rematerialization (amortized O(1) when capacity sufficient)
- ✅ No LOH pressure (List growth strategy)
- ✅ Correct enumeration during LINQ-derived expansion (staging buffer filled outside lock using LINQ chains over immutable data)
- ❌ Allocation on every read (lock + array copy)
- Best for: rematerialization-heavy workloads, large sliding windows

> **Note**: `ToRangeData()` acquires the same lock as `Read()` and `Rematerialize()` (the critical section). It returns an immutable snapshot — a freshly allocated array — that is fully decoupled from the mutable buffer lifecycle. See `docs/storage-strategies.md`.

### Strategy Selection

Controlled by `WindowCacheOptions.UserCacheReadMode`:
- `UserCacheReadMode.Snapshot` → `SnapshotReadStorage`
- `UserCacheReadMode.CopyOnRead` → `CopyOnReadStorage`

## Read/Write Pattern Summary

```
UserRequestHandler  ──reads───▶ CacheState.Storage.Read()
                                CacheState.Storage.ToRangeData()
                                CacheState.IsInitialized

DecisionEngine      ──reads───▶ CacheState.NoRebalanceRange
                                CacheState.Storage.Range

RebalanceExecutor   ──writes──▶ CacheState.Storage.Rematerialize()  ← SOLE WRITER
                                CacheState.NoRebalanceRange         ← SOLE WRITER
                                CacheState.IsInitialized            ← SOLE WRITER
```

## Invariants

| Invariant | Description                                                          |
|-----------|----------------------------------------------------------------------|
| A.11      | User Path does not mutate `CacheState` (read-only)                   |
| A.12a     | Only `RebalanceExecutor` writes `CacheState` (exclusive authority)   |
| A.12b     | Cache is always contiguous (no gaps in cached range)                 |
| B.1       | `CacheData` and `CurrentCacheRange` are always consistent            |
| B.2       | Cache updates are atomic via `Rematerialize`                         |
| B.3       | Consistency under cancellation: partial results discarded            |
| B.5       | Cancelled rebalance execution cannot violate cache consistency        |
| E.5       | `NoRebalanceRange` is derived from `CurrentCacheRange` and config    |
| F.2       | Rebalance Execution is the sole authority for all cache mutations    |
| F.3       | `Rematerialize` accepts arbitrary range and replaces entire contents |

See `docs/invariants.md` (Sections A, B, E, F) for full specification.

## Notes

- "Single logical consumer" is a **usage model** constraint; internal concurrency (user thread + background loops) is fully supported by design.
- Multiple threads from the **same** logical consumer can call `GetDataAsync` safely — the user path is read-only.
- Multiple **independent** consumers should use separate cache instances; sharing violates the coherent access pattern assumption.

## See Also

- `docs/storage-strategies.md` — detailed strategy comparison, performance characteristics, and selection guide
- `docs/invariants.md` — Sections A (write authority), B (state invariants), E (range planning)
- `docs/components/execution.md` — how `RebalanceExecutor` performs writes
