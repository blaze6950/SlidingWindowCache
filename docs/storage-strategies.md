# Sliding Window Cache - Storage Strategies Guide

> **📖 For component implementation details, see:**
> - [Component Map - Storage Section](component-map.md#3-storage-implementations) - SnapshotReadStorage and CopyOnReadStorage architecture

## Overview

The WindowCache supports two distinct storage strategies, selectable via `WindowCacheOptions.ReadMode`:

1. **Snapshot Storage** - Optimized for read performance
2. **CopyOnRead Storage with Staging Buffer** - Optimized for rematerialization performance

This guide explains when to use each strategy and their trade-offs.

---

## Storage Strategy Comparison

| Aspect                 | Snapshot Storage                  | CopyOnRead Storage                |
|------------------------|-----------------------------------|-----------------------------------|
| **Read Cost**          | O(1) - zero allocation            | O(n) - allocates and copies       |
| **Rematerialize Cost** | O(n) - always allocates new array | O(1)* - reuses capacity           |
| **Memory Pattern**     | Single array, replaced atomically | Dual buffers, swapped atomically  |
| **Buffer Growth**      | Always allocates exact size       | Grows but never shrinks           |
| **LOH Risk**           | High for >85KB arrays             | Lower (List growth strategy)      |
| **Best For**           | Read-heavy workloads              | Rematerialization-heavy workloads |
| **Typical Use Case**   | User-facing cache layer           | Background cache layer            |

*Amortized O(1) when capacity is sufficient

---

## Snapshot Storage

### Design

```
┌─────────────────────────────────┐
│   SnapshotReadStorage           │
├─────────────────────────────────┤
│  _storage: TData[]              │  ← Single array
│  Range: Range<TRange>           │
└─────────────────────────────────┘
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

- ✅ **Zero-allocation reads**: Returns `ReadOnlyMemory` slice over internal array
- ✅ **Simple and predictable**: Single buffer, no complexity
- ❌ **Expensive rematerialization**: Always allocates new array (even if size unchanged)
- ❌ **LOH pressure**: Arrays ≥85KB go to Large Object Heap (no compaction)

### When to Use

- **Read-to-rematerialization ratio > 10:1**
- **Repeated reads of the same range** (user scrolling back/forth)
- **Small to medium cache sizes** (<85KB to avoid LOH)
- **User-facing cache layers** where read latency matters

### Example Scenario

```csharp
// User-facing viewport cache for UI data grid
var options = new WindowCacheOptions(
    leftCacheSize: 0.5,
    rightCacheSize: 0.5,
    readMode: UserCacheReadMode.Snapshot  // ← Zero-allocation reads
);

var cache = new WindowCache<int, GridRow, IntegerFixedStepDomain>(
    dataSource, domain, options);

// User scrolls: many reads, few rebalances
for (int i = 0; i < 100; i++)
{
    var data = await cache.GetDataAsync(Range.Closed(i, i + 20), ct);
    // ← Zero allocation on each read
}
```

---

## CopyOnRead Storage with Staging Buffer

### Design

```
┌─────────────────────────────────┐
│   CopyOnReadStorage             │
├─────────────────────────────────┤
│  _activeStorage: List<TData>    │  ← Active (immutable during reads)
│  _stagingBuffer: List<TData>    │  ← Staging (write-only during rematerialize)
│  Range: Range<TRange>           │
└─────────────────────────────────┘

Rematerialize Flow:
┌──────────────┐     ┌──────────────┐
│ Active       │     │ Staging      │
│ [old data]   │     │ [empty]      │
└──────────────┘     └──────────────┘
                             ↓ Clear() preserves capacity
                     ┌──────────────┐
                     │ Staging      │
                     │ []           │
                     └──────────────┘
                             ↓ AddRange(newData)
                     ┌──────────────┐
                     │ Staging      │
                     │ [new data]   │
                     └──────────────┘
                             ↓ Swap references
┌──────────────┐     ┌──────────────┐
│ Active       │ ←── │ Staging      │
│ [new data]   │     │ [old data]   │
└──────────────┘     └──────────────┘
```

### Staging Buffer Pattern

The dual-buffer pattern solves a critical correctness issue:

**Problem:** When `rangeData.Data` is derived from the same storage (e.g., LINQ chain during cache expansion), mutating
storage during enumeration corrupts the data.

**Solution:** Never mutate active storage during enumeration. Instead:

1. Materialize into separate staging buffer
2. Atomically swap buffer references
3. Reuse old active buffer as staging for next operation

### Behavior

**Rematerialize:**

```csharp
_stagingBuffer.Clear();                    // Preserves capacity
_stagingBuffer.AddRange(rangeData.Data);   // Single-pass enumeration
(_activeStorage, _stagingBuffer) = (_stagingBuffer, _activeStorage);  // Atomic swap
Range = rangeData.Range;
```

**Read:**

```csharp
if (!Range.Contains(range))
    throw new ArgumentOutOfRangeException(nameof(range), ...);

var result = new TData[length];  // Allocates
for (var i = 0; i < length; i++)
    result[i] = _activeStorage[(int)startOffset + i];
return new ReadOnlyMemory<TData>(result);
```

### Characteristics

- ✅ **Cheap rematerialization**: Reuses capacity, no allocation if size ≤ capacity
- ✅ **No LOH pressure**: List growth strategy avoids large single allocations
- ✅ **Correct enumeration**: Staging buffer prevents corruption
- ✅ **Amortized performance**: Cost decreases over time as capacity stabilizes
- ❌ **Expensive reads**: Each read allocates and copies
- ❌ **Higher memory**: Two buffers instead of one

### Memory Behavior

- **Buffers may grow but never shrink**: Amortizes allocation cost
- **Capacity reuse**: Once buffers reach steady state, no more allocations during rematerialization
- **Predictable**: No hidden allocations, clear worst-case behavior

### When to Use

- **Rematerialization-to-read ratio > 1:5** (frequent rebalancing)
- **Large sliding windows** (>100KB typical size)
- **Random access patterns** (frequent non-intersecting jumps)
- **Background cache layers** feeding other caches
- **Composition scenarios** (described below)

### Example Scenario: Multi-Level Cache Composition

```csharp
// BACKGROUND LAYER: Large distant cache with CopyOnRead
var backgroundOptions = new WindowCacheOptions(
    leftCacheSize: 10.0,      // Cache 10x requested range
    rightCacheSize: 10.0,
    leftThreshold: 0.3,
    rightThreshold: 0.3,
    readMode: UserCacheReadMode.CopyOnRead  // ← Cheap rematerialization
);

var backgroundCache = new WindowCache<int, byte[], IntegerFixedStepDomain>(
    slowDataSource,  // Network/disk
    domain,
    backgroundOptions
);

// USER-FACING LAYER: Small nearby cache with Snapshot
var userOptions = new WindowCacheOptions(
    leftCacheSize: 0.5,
    rightCacheSize: 0.5,
    readMode: UserCacheReadMode.Snapshot  // ← Zero-allocation reads
);

// Wrap background cache as IDataSource for user cache
// (Implement IDataSource<int, byte[]> wrapping the background cache — not provided by the library)
IDataSource<int, byte[]> cachedDataSource = new BackgroundCacheAdapter(backgroundCache);

var userCache = new WindowCache<int, byte[], IntegerFixedStepDomain>(
    cachedDataSource,  // Reads from background cache
    domain,
    userOptions
);

// User scrolls: 
// - userCache: many reads (zero-alloc), rare rebalancing
// - backgroundCache: infrequent reads (copy), frequent rebalancing
```

This composition leverages the strengths of both strategies:

- **Background layer**: Handles large distant window, absorbs rebalancing cost
- **User layer**: Handles small nearby window, serves reads with zero allocation

---

## Decision Matrix

### Choose **Snapshot** if:

1. ✅ You expect **many reads per rematerialization** (>10:1 ratio)
2. ✅ Cache size is **predictable and modest** (<85KB)
3. ✅ Read latency is **critical** (user-facing UI)
4. ✅ Memory allocation during rematerialization is **acceptable**

### Choose **CopyOnRead** if:

1. ✅ You expect **frequent rematerialization** (random access, non-sequential)
2. ✅ Cache size is **large** (>100KB)
3. ✅ Read latency is **less critical** (background layer)
4. ✅ You want to **amortize allocation cost** over time
5. ✅ You're building a **multi-level cache composition**

### Default Recommendation

- **User-facing caches**: Start with **Snapshot**
- **Background caches**: Start with **CopyOnRead**
- **Unsure**: Start with **Snapshot**, profile, switch if rebalancing becomes bottleneck

---

## Performance Characteristics

### Snapshot Storage

| Operation     | Time | Allocation    |
|---------------|------|---------------|
| Read          | O(1) | 0 bytes       |
| Rematerialize | O(n) | n × sizeof(T) |
| ToRangeData   | O(1) | 0 bytes*      |

*Returns lazy enumerable

### CopyOnRead Storage

| Operation            | Time | Allocation    |
|----------------------|------|---------------|
| Read                 | O(n) | n × sizeof(T) |
| Rematerialize (cold) | O(n) | n × sizeof(T) |
| Rematerialize (warm) | O(n) | 0 bytes**     |
| ToRangeData          | O(1) | 0 bytes*      |

*Returns lazy enumerable  
**When capacity is sufficient

### Measured Benchmark Results

Real-world measurements from `RebalanceFlowBenchmarks` demonstrate the allocation tradeoffs:

**Fixed Span Behavior (BaseSpanSize=100, 10 rebalance operations):**
- Snapshot: ~224KB allocated
- CopyOnRead: ~92KB allocated
- **CopyOnRead advantage: 2.4x lower allocation**

**Fixed Span Behavior (BaseSpanSize=10,000, 10 rebalance operations):**
- Snapshot: ~16.5MB allocated (with Gen2 GC pressure)
- CopyOnRead: ~2.5MB allocated
- **CopyOnRead advantage: 6.6x lower allocation, reduced LOH pressure**

**Growing Span Behavior (BaseSpanSize=100, span increases 100 per iteration):**
- Snapshot: ~967KB allocated
- CopyOnRead: ~560KB allocated
- **CopyOnRead maintains 1.7x advantage even under dynamic growth**

**Key Observations:**
1. **Consistent allocation advantage**: CopyOnRead shows 2-6x lower allocations across all scenarios
2. **Baseline execution time**: ~1.05-1.07s (cumulative rebalance + overhead for 10 operations)
3. **LOH impact**: Snapshot mode triggers Gen2 collections at BaseSpanSize=10,000
4. **Buffer reuse**: CopyOnRead amortizes capacity growth, reducing steady-state allocations

These results validate the design philosophy: CopyOnRead trades per-read allocation cost for dramatically reduced rematerialization overhead.

For complete benchmark details, see [Benchmark Suite README](../benchmarks/SlidingWindowCache.Benchmarks/README.md).

---

## Implementation Details: Staging Buffer Pattern

### Why Two Buffers?

Consider cache expansion during user request:

```csharp
// Current cache: [100, 110]
var currentData = cache.ToRangeData();  // Lazy IEnumerable over _activeStorage

// User requests: [105, 115]
var extendedData = await ExtendCacheAsync(currentData, [105, 115]);
// extendedData.Data = Concat(currentData.Data, newlyFetched)
// This is a LINQ chain still tied to _activeStorage!

cache.Rematerialize(extendedData);
// OLD (BROKEN): _storage.Clear() → corrupts LINQ chain mid-enumeration
// NEW (CORRECT): _stagingBuffer.Clear() → _activeStorage remains immutable
```

### Buffer Swap Invariants

1. **Active storage is immutable during reads**: Never mutated until swap
2. **Staging buffer is write-only during rematerialization**: Cleared, filled, swapped
3. **Swap is atomic**: Single tuple assignment
4. **Buffers never shrink**: Capacity grows monotonically, amortizing allocation cost

### Memory Growth Example

```
Initial state:
_activeStorage: capacity=0, count=0
_stagingBuffer: capacity=0, count=0

After Rematerialize([100 items]):
_activeStorage: capacity=128, count=100  ← List grew to 128
_stagingBuffer: capacity=0, count=0

After Rematerialize([150 items]):
_activeStorage: capacity=256, count=150  ← Reused capacity=128, grew to 256
_stagingBuffer: capacity=128, count=100  ← Swapped, now has old capacity

After Rematerialize([120 items]):
_activeStorage: capacity=128, count=120  ← Reused capacity=128, no allocation!
_stagingBuffer: capacity=256, count=150  ← Swapped

Steady state reached: Both buffers have sufficient capacity, no more allocations
```

---

## Alignment with System Invariants

The staging buffer pattern directly supports key system invariants:

### Invariant A.3.8 - Cache Mutation Rules

- **Cold Start**: Staging buffer safely materializes initial cache
- **Expansion**: Active storage stays immutable while LINQ chains enumerate it
- **Replacement**: Atomic swap ensures clean transition

### Invariant A.3.9a - Cache Contiguity

- Single-pass enumeration into staging buffer maintains contiguity
- No partial or gapped states

### Invariant B.11-12 - Atomic Consistency

- Tuple swap `(_activeStorage, _stagingBuffer) = (_stagingBuffer, _activeStorage)` is atomic
- Range update happens after swap, completing atomic change
- No intermediate inconsistent states

### Invariant B.15 - Cancellation Safety

- If rematerialization is cancelled mid-AddRange, staging buffer is abandoned
- Active storage remains unchanged, cache stays consistent

---

## Testing Considerations

### Snapshot Storage Tests

```csharp
[Fact]
public async Task SnapshotMode_ZeroAllocationReads()
{
    var options = new WindowCacheOptions(readMode: UserCacheReadMode.Snapshot);
    var cache = new WindowCache<int, int, IntegerFixedStepDomain>(...);
    
    var data1 = await cache.GetDataAsync(Range.Closed(100, 110), ct);
    var data2 = await cache.GetDataAsync(Range.Closed(105, 115), ct);
    
    // Both reads return slices over same underlying array (until rematerialization)
    // No allocations for reads
}
```

### CopyOnRead Storage Tests

```csharp
[Fact]
public async Task CopyOnReadMode_CorrectDuringExpansion()
{
    var options = new WindowCacheOptions(readMode: UserCacheReadMode.CopyOnRead);
    var cache = new WindowCache<int, int, IntegerFixedStepDomain>(...);
    
    // First request: [100, 110]
    await cache.GetDataAsync(Range.Closed(100, 110), ct);
    
    // Second request: [105, 115] (intersects, triggers expansion)
    var data = await cache.GetDataAsync(Range.Closed(105, 115), ct);
    
    // Staging buffer pattern ensures correctness:
    // - Old storage remains immutable during LINQ enumeration
    // - New data materialized into staging buffer
    // - Buffers swapped atomically
    
    VerifyDataMatchesRange(data, Range.Closed(105, 115));
}
```

---

## Summary

- **Snapshot**: Fast reads, expensive rematerialization, best for read-heavy workloads
- **CopyOnRead with Staging Buffer**: Fast rematerialization, expensive reads, best for rematerialization-heavy
  workloads
- **Composition**: Combine both strategies in multi-level caches for optimal performance
- **Staging Buffer**: Critical correctness pattern preventing enumeration corruption

Choose based on your access pattern. When in doubt, start with Snapshot and profile.
