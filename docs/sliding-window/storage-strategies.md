# Storage Strategies — SlidingWindow Cache

For component implementation details, see `docs/sliding-window/components/state-and-storage.md`.

---

## Overview

`SlidingWindowCache` supports two distinct storage strategies, selectable via `SlidingWindowCacheOptions.ReadMode`:

1. **Snapshot Storage** — optimized for read performance
2. **CopyOnRead Storage with Staging Buffer** — optimized for rematerialization performance

---

## Storage Strategy Comparison

| Aspect | Snapshot Storage | CopyOnRead Storage |
|---|---|---|
| **Read Cost** | O(1) — zero allocation | O(n) — allocates and copies |
| **Rematerialize Cost** | O(n) — always allocates new array | O(1)* — reuses capacity |
| **Memory Pattern** | Single array, replaced atomically | Dual buffers, swap synchronized by lock |
| **Buffer Growth** | Always allocates exact size | Grows but never shrinks |
| **LOH Risk** | High for >85KB arrays | Lower (List growth strategy) |
| **Best For** | Read-heavy workloads | Rematerialization-heavy workloads |
| **Typical Use Case** | User-facing cache layer | Background cache layer |

*Amortized O(1) when capacity is sufficient

---

## Snapshot Storage

### Design

```
┌──────────────────────────────────┐
│   SnapshotReadStorage            │
├──────────────────────────────────┤
│  _storage: TData[]               │  < Single array
│  Range: Range<TRange>            │
└──────────────────────────────────┘
```

### Behavior

**Rematerialize:**

```csharp
Range = rangeData.Range;
_storage = rangeData.Data.ToArray();  // Always allocates new array
```

**Read:**

```csharp
return new ReadOnlyMemory<TData>(_storage, offset, length);  // Zero allocation
```

### Characteristics

- **Zero-allocation reads**: Returns `ReadOnlyMemory` slice over internal array
- **Simple and predictable**: Single buffer, no complexity
- **Expensive rematerialization**: Always allocates new array (even if size unchanged)
- **LOH pressure**: Arrays ≥85KB go to Large Object Heap (no compaction)

### When to Use

- Read-to-rematerialization ratio > 10:1
- Repeated reads of the same range (user scrolling back/forth)
- Small to medium cache sizes (<85KB to avoid LOH)
- User-facing cache layers where read latency matters

### Example

```csharp
// User-facing viewport cache for UI data grid
var options = new SlidingWindowCacheOptions(
    leftCacheSize: 0.5,
    rightCacheSize: 0.5,
    readMode: UserCacheReadMode.Snapshot  // Zero-allocation reads
);

var cache = new SlidingWindowCache<int, GridRow, IntegerFixedStepDomain>(
    dataSource, domain, options);

// User scrolls: many reads, few rebalances
for (int i = 0; i < 100; i++)
{
    var data = await cache.GetDataAsync(Range.Closed(i, i + 20), ct);
    // Zero allocation on each read
}
```

---

## CopyOnRead Storage with Staging Buffer

### Design

```
┌──────────────────────────────────┐
│   CopyOnReadStorage              │
├──────────────────────────────────┤
│  _activeStorage: List<TData>     │  < Active (immutable during reads)
│  _stagingBuffer: List<TData>     │  < Staging (write-only during rematerialize)
│  Range: Range<TRange>            │
└──────────────────────────────────┘

Rematerialize Flow:
┌───────────────┐     ┌───────────────┐
│ Active        │     │ Staging       │
│ [old data]    │     │ [empty]       │
└───────────────┘     └───────────────┘
                               v Clear() preserves capacity
                      ┌───────────────┐
                      │ Staging       │
                      │ []            │
                      └───────────────┘
                               v AddRange(newData)
                      ┌───────────────┐
                      │ Staging       │
                      │ [new data]    │
                      └───────────────┘
                               v Swap references
┌───────────────┐     ┌───────────────┐
│ Active        │ <-- │ Staging       │
│ [new data]    │     │ [old data]    │
└───────────────┘     └───────────────┘
```

### Staging Buffer Pattern

The dual-buffer pattern solves a critical correctness issue:

**Problem:** When `rangeData.Data` is derived from the same storage (e.g., LINQ chain during cache expansion), mutating storage during enumeration corrupts the data.

**Solution:** Never mutate active storage during enumeration. Instead:

1. Materialize into separate staging buffer
2. Atomically swap buffer references
3. Reuse old active buffer as staging for next operation

### Behavior

**Rematerialize:**

```csharp
// Enumerate outside the lock (may be a LINQ chain over _activeStorage)
_stagingBuffer.Clear();
_stagingBuffer.AddRange(rangeData.Data);

lock (_lock)
{
    (_activeStorage, _stagingBuffer) = (_stagingBuffer, _activeStorage);  // Swap under lock
    Range = rangeData.Range;
}
```

**Read:**

```csharp
lock (_lock)
{
    if (!Range.Contains(range))
        throw new ArgumentOutOfRangeException(nameof(range), ...);

    var result = new TData[length];  // Allocates
    for (var i = 0; i < length; i++)
        result[i] = _activeStorage[(int)startOffset + i];
    return new ReadOnlyMemory<TData>(result);
}
```

### Characteristics

- **Cheap rematerialization**: Reuses capacity, no allocation if size ≤ capacity
- **No LOH pressure**: List growth strategy avoids large single allocations
- **Correct enumeration**: Staging buffer prevents corruption during LINQ-derived expansion
- **Amortized performance**: Cost decreases over time as capacity stabilizes
- **Safe concurrent access**: `Read()`, `Rematerialize()`, and `ToRangeData()` share a lock; mid-swap observation is impossible
- **Expensive reads**: Each read acquires a lock, allocates, and copies
- **Higher memory**: Two buffers instead of one
- **Lock contention**: Reader briefly blocks if rematerialization is in progress (bounded to the swap duration, not the full rebalance cycle)

### Memory Behavior

- Buffers may grow but never shrink: amortizes allocation cost
- Capacity reuse: Once buffers reach steady state, no more allocations during rematerialization
- Predictable: No hidden allocations, clear worst-case behavior

### When to Use

- Rematerialization-to-read ratio > 1:5 (frequent rebalancing)
- Large sliding windows (>100KB typical size)
- Random access patterns (frequent non-intersecting jumps)
- Background cache layers feeding other caches
- Composition scenarios (described below)

### Example: Multi-Level Cache Composition

```csharp
// Two-layer cache: L2 (CopyOnRead, large) > L1 (Snapshot, small)
await using var cache = await SlidingWindowCacheBuilder.Layered(slowDataSource, domain)
    .AddSlidingWindowLayer(new SlidingWindowCacheOptions(    // L2: deep background cache
        leftCacheSize: 10.0,
        rightCacheSize: 10.0,
        leftThreshold: 0.3,
        rightThreshold: 0.3,
        readMode: UserCacheReadMode.CopyOnRead))  // cheap rematerialization
    .AddSlidingWindowLayer(new SlidingWindowCacheOptions(    // L1: user-facing cache
        leftCacheSize: 0.5,
        rightCacheSize: 0.5,
        readMode: UserCacheReadMode.Snapshot))    // zero-allocation reads
    .BuildAsync();
```

---

## Decision Matrix

### Choose **Snapshot** if:

1. You expect **many reads per rematerialization** (>10:1 ratio)
2. Cache size is **predictable and modest** (<85KB)
3. Read latency is **critical** (user-facing UI)
4. Memory allocation during rematerialization is **acceptable**

### Choose **CopyOnRead** if:

1. You expect **frequent rematerialization** (random access, non-sequential)
2. Cache size is **large** (>100KB)
3. Read latency is **less critical** (background layer)
4. You want to **amortize allocation cost** over time
5. You're building a **multi-level cache composition**

### Default Recommendation

- **User-facing caches**: Start with **Snapshot**
- **Background caches**: Start with **CopyOnRead**
- **Unsure**: Start with **Snapshot**, profile, switch if rebalancing becomes bottleneck

---

## Performance Characteristics

### Snapshot Storage

| Operation | Time | Allocation |
|---|---|---|
| Read | O(1) | 0 bytes |
| Rematerialize | O(n) | n × sizeof(T) |
| ToRangeData | O(1) | 0 bytes* |

*Returns lazy enumerable

### CopyOnRead Storage

| Operation | Time | Allocation | Notes |
|---|---|---|---|
| Read | O(n) | n × sizeof(T) | Lock acquired + copy |
| Rematerialize (cold) | O(n) | n × sizeof(T) | Enumerate outside lock |
| Rematerialize (warm) | O(n) | 0 bytes** | Enumerate outside lock |
| ToRangeData | O(n) | n × sizeof(T) | Lock acquired + array snapshot copy |

**When capacity is sufficient

### Measured Benchmark Results

Real-world measurements from `RebalanceFlowBenchmarks`:

**Fixed Span (BaseSpanSize=100, 10 rebalance operations):**
- Snapshot: ~224KB allocated
- CopyOnRead: ~92KB allocated
- **CopyOnRead advantage: 2.4x lower allocation**

**Fixed Span (BaseSpanSize=10,000, 10 rebalance operations):**
- Snapshot: ~16.5MB allocated (with Gen2 GC pressure)
- CopyOnRead: ~2.5MB allocated
- **CopyOnRead advantage: 6.6x lower allocation, reduced LOH pressure**

**Growing Span (BaseSpanSize=100, span increases 100 per iteration):**
- Snapshot: ~967KB allocated
- CopyOnRead: ~560KB allocated
- **CopyOnRead maintains 1.7x advantage even under dynamic growth**

Key observations:
1. CopyOnRead shows 2–6× lower allocations across all scenarios
2. Baseline execution time: ~1.05–1.07s (cumulative for 10 operations)
3. Snapshot mode triggers Gen2 GC collections at BaseSpanSize=10,000
4. CopyOnRead amortizes capacity growth, reducing steady-state allocations

For complete benchmark details, see `benchmarks/Intervals.NET.Caching.SlidingWindow.Benchmarks/README.md`.

---

## Implementation Details: Staging Buffer Pattern

### Why Two Buffers?

Consider cache expansion during user request:

```csharp
// Current cache: [100, 110]
var currentData = cache.ToRangeData();
// CopyOnReadStorage: acquires _lock, copies _activeStorage to a new array, returns immutable snapshot.
// The returned RangeData.Data is decoupled from the live buffers — no lazy reference.

// User requests: [105, 115]
var extendedData = await ExtendCacheAsync(currentData, [105, 115]);
// extendedData.Data = Union(currentData.Data, newlyFetched)
// Safe to enumerate later: currentData.Data is an array, not a live List reference.

cache.Rematerialize(extendedData);
// _stagingBuffer.Clear() is safe: extendedData.Data chains from the immutable snapshot array,
// not from _activeStorage directly.
```

**Why the snapshot copy matters:** Without `.ToArray()`, `ToRangeData()` would return a lazy `IEnumerable` over the live `_activeStorage` list. That reference is published as an `Intent` and consumed asynchronously on the rebalance thread. A second `Rematerialize()` call would swap the list to `_stagingBuffer` and clear it before the Intent is consumed — silently emptying the enumerable mid-enumeration (or causing `InvalidOperationException`). The snapshot copy eliminates this race entirely.

### Buffer Swap Invariants

1. **Active storage is immutable during reads**: Never mutated until swap; lock prevents concurrent observation mid-swap
2. **Staging buffer is write-only during rematerialization**: Cleared and filled outside the lock, then swapped under lock
3. **Swap is lock-protected**: `Read()`, `ToRangeData()`, and `Rematerialize()` share `_lock`; all callers always observe a consistent `(_activeStorage, Range)` pair
4. **Buffers never shrink**: Capacity grows monotonically, amortizing allocation cost
5. **`ToRangeData()` snapshots are immutable**: Copies `_activeStorage` to a new array under the lock; a subsequent `Rematerialize()` cannot corrupt or empty data still referenced by an outstanding enumerable

### Memory Growth Example

```
Initial state:
_activeStorage: capacity=0, count=0
_stagingBuffer: capacity=0, count=0

After Rematerialize([100 items]):
_activeStorage: capacity=128, count=100  < List grew to 128
_stagingBuffer: capacity=0, count=0

After Rematerialize([150 items]):
_activeStorage: capacity=256, count=150  < Reused capacity=128, grew to 256
_stagingBuffer: capacity=128, count=100  < Swapped, now has old capacity

After Rematerialize([120 items]):
_activeStorage: capacity=128, count=120  < Reused capacity=128, no allocation!
_stagingBuffer: capacity=256, count=150  < Swapped

Steady state reached: Both buffers have sufficient capacity, no more allocations
```

---

## Alignment with System Invariants

### Invariant SWC.A.12 — Cache Mutation Rules

- **Cold Start**: Staging buffer safely materializes initial cache
- **Expansion**: Active storage stays immutable while LINQ chains enumerate it
- **Replacement**: Atomic swap ensures clean transition

### Invariant SWC.A.12b — Cache Contiguity

- Single-pass enumeration into staging buffer maintains contiguity
- No partial or gapped states

### Invariant SWC.B.1–SWC.B.2 — Atomic Consistency

- Swap and Range update both happen inside `lock (_lock)`, so `Read()` always observes a consistent `(_activeStorage, Range)` pair
- No intermediate inconsistent state is observable

### Invariant SWC.A.4 — User Path Never Waits for Rebalance (Conditional Compliance)

- `CopyOnReadStorage` is **conditionally compliant**: `Read()` and `ToRangeData()` acquire `_lock`, which is also held by `Rematerialize()` for the duration of the buffer swap and Range update (a fast, bounded operation).
- Contention is limited to the swap itself — not the full rebalance cycle. The enumeration into the staging buffer happens **before** the lock is acquired.
- `SnapshotReadStorage` remains fully lock-free if strict SWC.A.4 compliance is required.

### Invariant SWC.B.5 — Cancellation Safety

- If rematerialization is cancelled mid-`AddRange`, the staging buffer is abandoned
- Active storage remains unchanged; cache stays consistent

---

## Summary

- **Snapshot**: Fast reads (zero-allocation), expensive rematerialization — best for read-heavy workloads
- **CopyOnRead with Staging Buffer**: Fast rematerialization, reads copy under lock — best for rematerialization-heavy workloads
- **Composition**: Combine both strategies in multi-level caches using `LayeredRangeCacheBuilder` for optimal performance
- **Staging Buffer**: Critical correctness pattern preventing enumeration corruption during cache expansion

Choose based on your access pattern. When in doubt, start with Snapshot and profile.

---

## See Also

- `docs/sliding-window/components/state-and-storage.md` — `CacheState`, storage class implementations
- `docs/sliding-window/scenarios.md` — scenarios involving cache expansion and rematerialization
- `docs/sliding-window/glossary.md` — Snapshot, CopyOnRead, Rematerialization terms
