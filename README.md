# Sliding Window Cache

**A read-only, range-based, sequential-optimized cache with background rebalancing and cancellation-aware prefetching.**

---

## Overview

The Sliding Window Cache is a high-performance caching library designed for scenarios where data is accessed in sequential or predictable patterns across ranges. It automatically prefetches and maintains a "window" of data around the most recently requested range, significantly reducing the need for repeated data source queries.

### Key Features

- **Automatic Prefetching**: Intelligently prefetches data on both sides of requested ranges based on configurable coefficients
- **Background Rebalancing**: Asynchronously adjusts the cache window when access patterns change, with debouncing to avoid thrashing
- **Cancellation-Aware**: Full support for `CancellationToken` throughout the async pipeline
- **Range-Based Operations**: Built on top of the [`Intervals.NET`](https://github.com/blaze6950/Intervals.NET] library) for robust range handling
- **Configurable Read Modes**: Choose between different materialization strategies based on your performance requirements

---

## Sliding Window Cache Concept

Traditional caches work with individual keys. A sliding window cache, in contrast, operates on **continuous ranges** of data:

1. **User requests a range** (e.g., records 100-200)
2. **Cache fetches more than requested** (e.g., records 50-300) based on configured left/right cache coefficients
3. **Subsequent requests within the window are served instantly** from materialized data
4. **Window automatically rebalances** when the user moves outside threshold boundaries

This pattern is ideal for:
- Time-series data (sensor readings, logs, metrics)
- Paginated datasets with forward/backward navigation
- Sequential data processing (video frames, audio samples)
- Any scenario with high spatial or temporal locality of access

---

## Materialization for Fast Access

### Why Materialization?

The cache **always materializes** the data it fetches, meaning it stores the data in memory in a directly accessible format (arrays or lists) rather than keeping lazy enumerables. This design choice ensures:

- **Fast, predictable read performance**: No deferred execution chains on the hot path
- **Multiple reads without re-enumeration**: The same data can be read many times at zero cost (in Snapshot mode)
- **Clean separation of concerns**: Data fetching (I/O-bound) is decoupled from data serving (CPU-bound)

### Read Modes: Snapshot vs. CopyOnRead

The cache supports two materialization strategies, configured at creation time via the `UserCacheReadMode` enum:

#### Snapshot Mode (`UserCacheReadMode.Snapshot`)

**Storage**: Contiguous array (`TData[]`)  
**Read behavior**: Returns `ReadOnlyMemory<TData>` pointing directly to internal array  
**Rebalance behavior**: Always allocates a new array

**Advantages:**
- ✅ **Zero allocations on read** – no memory overhead per request
- ✅ **Fastest read performance** – direct memory view
- ✅ Ideal for **read-heavy scenarios** with frequent access to cached data

**Disadvantages:**
- ❌ **Expensive rebalancing** – always allocates a new array, even if size is unchanged
- ❌ **Large Object Heap (LOH) pressure** – arrays ≥85,000 bytes go to LOH, which can cause fragmentation
- ❌ Higher memory usage during rebalance (old + new arrays temporarily coexist)

**Best for:**
- Applications that read the same data many times
- Scenarios where cache updates are infrequent relative to reads
- Systems with ample memory and minimal LOH concerns

#### CopyOnRead Mode (`UserCacheReadMode.CopyOnRead`)

**Storage**: Growable list (`List<TData>`)  
**Read behavior**: Allocates a new array and copies the requested range  
**Rebalance behavior**: Uses `List<T>` operations (Clear + AddRange)

**Advantages:**
- ✅ **Cheaper rebalancing** – `List<T>` can grow without always allocating large arrays
- ✅ **Reduced LOH pressure** – avoids large contiguous allocations in most cases
- ✅ Ideal for **memory-sensitive scenarios** or when rebalancing is frequent

**Disadvantages:**
- ❌ **Allocates on every read** – new array per request
- ❌ **Copy overhead** – data must be copied from list to array
- ❌ Slower read performance compared to Snapshot mode

**Best for:**
- Applications with frequent cache rebalancing
- Memory-constrained environments
- Scenarios where each range is typically read once or twice
- Systems sensitive to LOH fragmentation

### Choosing a Read Mode

| Scenario | Recommended Mode |
|----------|------------------|
| High read-to-rebalance ratio (e.g., 100:1) | **Snapshot** |
| Frequent rebalancing (e.g., random access patterns) | **CopyOnRead** |
| Large cache sizes (>85KB arrays) | **CopyOnRead** |
| Read-once patterns | **CopyOnRead** |
| Repeated reads of the same range | **Snapshot** |
| Memory-constrained systems | **CopyOnRead** |

**For detailed comparison and multi-level cache composition patterns, see [Storage Strategies Guide](docs/STORAGE_STRATEGIES.md).**

---

## Usage Example

```csharp
using SlidingWindowCache;
using SlidingWindowCache.Configuration;
using Intervals.NET;
using Intervals.NET.Domain.Default.Numeric;

// Configure the cache behavior
var options = new WindowCacheOptions(
    leftCacheSize: 1.0,    // Cache 100% of requested range size to the left
    rightCacheSize: 2.0,   // Cache 200% of requested range size to the right
    leftThreshold: 0.2,    // Rebalance if <20% left buffer remains
    rightThreshold: 0.2    // Rebalance if <20% right buffer remains
);

// Create cache with Snapshot mode (zero-allocation reads)
var cache = WindowCache<int, string, IntegerFixedStepDomain>.Create(
    dataSource: myDataSource,
    domain: new IntegerFixedStepDomain(),
    options: options,
    readMode: UserCacheReadMode.Snapshot
);

// Request data - returns ReadOnlyMemory<string>
var data = await cache.GetDataAsync(
    Range.Closed(100, 200),
    cancellationToken
);

// Access the data
foreach (var item in data.Span)
{
    Console.WriteLine(item);
}
```

---

## Configuration

See `WindowCacheOptions` for detailed configuration parameters:
- **Left/Right Cache Coefficients**: Control how much extra data to prefetch
- **Threshold Policies**: Define when rebalancing should occur
- **Debounce Delay**: Prevent thrashing during rapid access pattern changes

---

## Documentation

For detailed architectural documentation, see:

- **[Invariants](docs/invariants.md)** - Complete list of system invariants and guarantees
- **[Scenario Model](docs/scenario-model.md)** - Temporal behavior scenarios (User Path, Decision Path, Rebalance Execution)
- **[Actors & Responsibilities](docs/actors-and-responsibilities.md)** - System actors and invariant ownership mapping
- **[Cache State Machine](docs/cache-state-machine.md)** - Formal state machine with mutation ownership and concurrency semantics

### Key Architectural Principles

1. **Cache Contiguity**: Cache data must always remain contiguous (no gaps). Non-intersecting requests fully replace the cache.
2. **User Priority**: User requests always cancel ongoing/pending rebalance before performing cache mutations.
3. **Mutation Ownership**: Both User Path and Rebalance Execution may mutate cache, but never concurrently. User Path has priority.

---

## Performance Considerations

- **Snapshot mode**: O(1) reads, but O(n) rebalance with array allocation
- **CopyOnRead mode**: O(n) reads (copy cost), but cheaper rebalance operations
- **Rebalancing is asynchronous**: Does not block user reads
- **Debouncing**: Multiple rapid requests trigger only one rebalance operation

---

## License

MIT