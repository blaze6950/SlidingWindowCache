# Sliding Window Cache

**A read-only, range-based, sequential-optimized cache with decision-driven background rebalancing, smart eventual
consistency, and intelligent work avoidance.**

---

[![CI/CD](https://github.com/blaze6950/SlidingWindowCache/actions/workflows/slidingwindowcache.yml/badge.svg)](https://github.com/blaze6950/SlidingWindowCache/actions/workflows/slidingwindowcache.yml)
[![NuGet](https://img.shields.io/nuget/v/SlidingWindowCache.svg)](https://www.nuget.org/packages/SlidingWindowCache/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/SlidingWindowCache.svg)](https://www.nuget.org/packages/SlidingWindowCache/)
[![codecov](https://codecov.io/gh/blaze6950/SlidingWindowCache/graph/badge.svg?token=RFQBNX7MMD)](https://codecov.io/gh/blaze6950/SlidingWindowCache)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![.NET 8.0](https://img.shields.io/badge/.NET-8.0-blue.svg)](https://dotnet.microsoft.com/download/dotnet/8.0)

---

## 📑 Table of Contents

- [Overview](#-overview)
- [Key Features](#key-features)
- [Decision-Driven Rebalance Execution](#decision-driven-rebalance-execution)
- [Sliding Window Cache Concept](#-sliding-window-cache-concept)
- [Understanding the Sliding Window](#-understanding-the-sliding-window)
- [Materialization for Fast Access](#-materialization-for-fast-access)
- [Usage Example](#-usage-example)
- [Resource Management](#-resource-management)
- [Configuration](#-configuration)
- [Execution Strategy Selection](#-execution-strategy-selection)
- [Optional Diagnostics](#-optional-diagnostics)
- [Documentation](#-documentation)
- [Performance Considerations](#-performance-considerations)
- [CI/CD & Package Information](#-cicd--package-information)
- [Contributing & Feedback](#-contributing--feedback)
- [License](#license)

---

## 📦 Overview

The Sliding Window Cache is a high-performance caching library designed for scenarios where data is accessed in
sequential or predictable patterns across ranges. It automatically prefetches and maintains a "window" of data around
the most recently requested range, significantly reducing the need for repeated data source queries.

### Key Features

- **Automatic Prefetching**: Intelligently prefetches data on both sides of requested ranges based on configurable
  coefficients
- **Smart Eventual Consistency**: Decision-driven rebalance execution with multi-stage analytical validation ensures the
  cache converges to optimal configuration while avoiding unnecessary work
- **Work Avoidance Through Validation**: Multi-stage decision pipeline (NoRebalanceRange containment, pending rebalance
  coverage, cache geometry analysis) prevents thrashing, reduces redundant I/O, and maintains system stability under
  rapidly changing access patterns
- **Background Rebalancing**: Asynchronously adjusts the cache window when validation confirms necessity, with
  debouncing to control convergence timing
- **Opportunistic Execution**: Rebalance operations may be skipped when validation determines they are unnecessary (
  intent represents observed access, not mandatory work)
- **Single-Writer Architecture**: User Path is read-only; only Rebalance Execution mutates cache state, eliminating race
  conditions with cancellation support for coordination
- **Range-Based Operations**: Built on top of the [`Intervals.NET`](https://github.com/blaze6950/Intervals.NET) library
  for robust range handling
- **Configurable Read Modes**: Choose between different materialization strategies based on your performance
  requirements
- **Optional Diagnostics**: Built-in instrumentation for monitoring cache behavior and validating system invariants
- **Full Cancellation Support**: User-provided `CancellationToken` propagates through the async pipeline; rebalance
  operations support cancellation at all stages

### Decision-Driven Rebalance Execution

> **📖 For detailed architectural explanation, see:** [Architecture Model - Decision-Driven Execution](docs/architecture-model.md#rebalance-validation-vs-cancellation)

The cache uses a sophisticated **decision-driven model** where rebalance necessity is determined by analytical
validation rather than blindly executing every user request. This prevents thrashing, reduces unnecessary I/O, and
maintains stability under rapid access pattern changes.

**Visual Flow:**

```
User Request
     │
     ▼
┌─────────────────────────────────────────────────┐
│  User Path (User Thread - Synchronous)          │
│  • Read from cache or fetch missing data        │
│  • Return data immediately to user              │
│  • Publish intent with delivered data           │
└────────────┬────────────────────────────────────┘
             │
             ▼
┌─────────────────────────────────────────────────┐
│  Decision Engine (Background Loop - CPU-only)   │
│  Stage 1: NoRebalanceRange check                │
│  Stage 2: Pending coverage check                │
│  Stage 3: Desired == Current check              │
│  → Decision: SKIP or SCHEDULE                   │
└────────────┬────────────────────────────────────┘
             │
             ├─── If SKIP: return (work avoidance) ✓
             │
             └─── If SCHEDULE:
                       │
                       ▼
             ┌─────────────────────────────────────┐
             │  Background Rebalance (ThreadPool)  │
             │  • Debounce delay                   │
             │  • Fetch missing data (I/O)         │
             │  • Normalize cache to desired range │
             │  • Update cache state atomically    │
             └─────────────────────────────────────┘
```

**Key Points:**

1. **User requests never block** - data returned immediately, rebalance happens later
2. **Decision happens in background** - validation is CPU-only (microseconds), happens in the intent processing loop before scheduling
3. **Work avoidance prevents thrashing** - validation may skip rebalance entirely if unnecessary
4. **Only I/O happens in background** - debounce + data fetching + cache updates run asynchronously
5. **Smart eventual consistency** - cache converges to optimal state while avoiding unnecessary operations

**Why This Matters:**

- **Handles request bursts correctly**: First request schedules rebalance, subsequent requests validate and skip if
  pending rebalance covers them
- **No background queue buildup**: Decisions made immediately, not queued
- **Prevents oscillation**: Stage 2 validation checks if pending rebalance will satisfy request
- **Lightweight**: Decision logic is pure CPU (math, conditions), no I/O blocking

**For complete architectural details, see:**

- [Architecture Model](docs/architecture-model.md) - Smart eventual consistency and synchronous decision execution
- [Invariants](docs/invariants.md) - Multi-stage validation pipeline specification (Section D)
- [Scenario Model](docs/scenario-model.md) - Temporal behavior and decision scenarios

---

## 🎯 Sliding Window Cache Concept

Traditional caches work with individual keys. A sliding window cache, in contrast, operates on **continuous ranges** of
data:

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

## 🔍 Understanding the Sliding Window

### Visual: Requested Range vs. Cache Window

When you request a range, the cache actually fetches and stores a larger window:

```
Requested Range (what user asks for):
                         [======== USER REQUEST ========]

Actual Cache Window (what cache stores):
    [=== LEFT BUFFER ===][======== USER REQUEST ========][=== RIGHT BUFFER ===]
     ← leftCacheSize      requestedRange size              rightCacheSize →
```

The **left** and **right buffers** are calculated as multiples of the requested range size using the `leftCacheSize` and
`rightCacheSize` coefficients.

### Visual: Rebalance Trigger

Rebalancing occurs when a new request moves outside the threshold boundaries:

```
Current Cache Window:
[========*===================== CACHE ======================*=======]
         ↑                                                  ↑
 Left Threshold (20%)                              Right Threshold (20%)

Scenario 1: Request within thresholds → No rebalance
[========*===================== CACHE ======================*=======]
              [---- new request ----]  ✓ Served from cache

Scenario 2: Request outside threshold → Rebalance triggered
[========*===================== CACHE ======================*=======]
                                          [---- new request ----]
                                                     ↓
                            🔄 Rebalance: Shift window right
```

### Visual: Configuration Impact

How coefficients control the cache window size:

```
Example: User requests range of size 100

leftCacheSize = 1.0, rightCacheSize = 2.0
[==== 100 ====][======= 100 =======][============ 200 ============]
 Left Buffer    Requested Range       Right Buffer
 
Total Cache Window = 100 + 100 + 200 = 400 items

leftThreshold = 0.2 (20% of 400 = 80 items)
rightThreshold = 0.2 (20% of 400 = 80 items)
```

**Key insight:** Threshold percentages are calculated based on the **total cache window size**, not individual buffer
sizes.

---

## 💾 Materialization for Fast Access

### Why Materialization?

The cache **always materializes** the data it fetches, meaning it stores the data in memory in a directly accessible
format (arrays or lists) rather than keeping lazy enumerables. This design choice ensures:

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

**Quick Decision Guide:**

| Your Scenario        | Recommended Mode | Why                    |
|----------------------|------------------|------------------------|
| Read data many times | **Snapshot**     | Zero-allocation reads  |
| Frequent rebalancing | **CopyOnRead**   | Cheaper cache updates  |
| Large cache (>85KB)  | **CopyOnRead**   | Avoid LOH pressure     |
| Memory constrained   | **CopyOnRead**   | Better memory behavior |
| Read-once patterns   | **CopyOnRead**   | Copy cost already paid |
| Read-heavy workload  | **Snapshot**     | Direct memory access   |

**For detailed comparison, performance benchmarks, multi-level cache composition patterns, and staging buffer
implementation details, see [Storage Strategies Guide](docs/storage-strategies.md).**

---

## 🚀 Usage Example

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

## 🔄 Resource Management

WindowCache manages background processing tasks and resources that require explicit disposal. **Always dispose the cache when done** to prevent resource leaks and ensure graceful shutdown of background operations.

### Disposal Pattern

WindowCache implements `IAsyncDisposable` for proper async resource cleanup:

```csharp
// Recommended: Use await using declaration
await using var cache = new WindowCache<int, string, IntegerFixedStepDomain>(
    dataSource,
    domain,
    options,
    cacheDiagnostics
);

// Use the cache
var data = await cache.GetDataAsync(Range.Closed(0, 100), cancellationToken);

// DisposeAsync called automatically at end of scope
```

### What Disposal Does

When `DisposeAsync()` is called, the cache:

1. **Stops accepting new requests** - All methods throw `ObjectDisposedException` after disposal
2. **Cancels background rebalance processing** - Signals cancellation to intent processing and execution loops
3. **Waits for current operations to complete** - Gracefully allows in-flight rebalance operations to finish
4. **Releases all resources** - Disposes channels, semaphores, and cancellation token sources
5. **Is idempotent** - Safe to call multiple times, handles concurrent disposal attempts

### Disposal Behavior

**Graceful Shutdown:**
```csharp
await using var cache = CreateCache();

// Make requests
await cache.GetDataAsync(range1, ct);
await cache.GetDataAsync(range2, ct);

// No need to call WaitForIdleAsync() before disposal
// DisposeAsync() handles graceful shutdown automatically
```

**After Disposal:**
```csharp
var cache = CreateCache();
await cache.DisposeAsync();

// All operations throw ObjectDisposedException
await cache.GetDataAsync(range, ct);       // ❌ Throws ObjectDisposedException
await cache.WaitForIdleAsync();            // ❌ Throws ObjectDisposedException
await cache.DisposeAsync();                // ✅ Succeeds (idempotent)
```

**Long-Lived Cache:**
```csharp
public class DataService : IAsyncDisposable
{
    private readonly WindowCache<int, string, IntegerFixedStepDomain> _cache;

    public DataService(IDataSource<int, string> dataSource)
    {
        _cache = new WindowCache<int, string, IntegerFixedStepDomain>(
            dataSource,
            new IntegerFixedStepDomain(),
            options
        );
    }

    public ValueTask<ReadOnlyMemory<string>> GetDataAsync(Range<int> range, CancellationToken ct)
        => _cache.GetDataAsync(range, ct);

    public async ValueTask DisposeAsync()
    {
        await _cache.DisposeAsync();
    }
}
```

### Important Notes

- **No timeout needed**: Disposal completes when background tasks finish their current work (typically milliseconds)
- **Thread-safe**: Multiple concurrent disposal calls are handled safely using lock-free synchronization
- **No forced termination**: Background operations are cancelled gracefully, not forcibly terminated
- **Memory eligible for GC**: After disposal, the cache becomes eligible for garbage collection

---

## ⚙️ Configuration

The `WindowCacheOptions` class provides fine-grained control over cache behavior. Understanding these parameters is
essential for optimal performance.

### Configuration Parameters

#### Cache Size Coefficients

**`leftCacheSize`** (double, default: 1.0)

- **Definition**: Multiplier applied to the requested range size to determine the left buffer size
- **Practical meaning**: How much data to prefetch *before* the requested range
- **Example**: If user requests 100 items and `leftCacheSize = 1.5`, the cache prefetches 150 items to the left
- **Typical values**: 0.5 to 2.0 (depending on backward navigation patterns)

**`rightCacheSize`** (double, default: 2.0)

- **Definition**: Multiplier applied to the requested range size to determine the right buffer size
- **Practical meaning**: How much data to prefetch *after* the requested range
- **Example**: If user requests 100 items and `rightCacheSize = 2.0`, the cache prefetches 200 items to the right
- **Typical values**: 1.0 to 3.0 (higher for forward-scrolling scenarios)

#### Threshold Policies

**`leftThreshold`** (double, default: 0.2)

- **Definition**: Percentage of the **total cache window size** that triggers rebalancing when crossed on the left
- **Calculation**: `leftThreshold × (Left Buffer + Requested Range + Right Buffer)`
- **Example**: With total window of 400 items and `leftThreshold = 0.2`, rebalance triggers when user moves within 80
  items of the left edge
- **Typical values**: 0.15 to 0.3 (lower = more aggressive rebalancing)

**`rightThreshold`** (double, default: 0.2)

- **Definition**: Percentage of the **total cache window size** that triggers rebalancing when crossed on the right
- **Calculation**: `rightThreshold × (Left Buffer + Requested Range + Right Buffer)`
- **Example**: With total window of 400 items and `rightThreshold = 0.2`, rebalance triggers when user moves within 80
  items of the right edge
- **Typical values**: 0.15 to 0.3 (lower = more aggressive rebalancing)

**🚨 Important Constraint: Threshold Sum**

The **sum of `leftThreshold` and `rightThreshold` must not exceed 1.0** when both are specified.

**Why?** Thresholds represent percentages of the total cache window that are shrunk inward from each side to create the no-rebalance stability zone. If their sum exceeds 1.0 (100%), the shrinkage zones would overlap, creating an impossible geometric configuration.

**Examples:**
- ✅ Valid: `leftThreshold: 0.3, rightThreshold: 0.3` (sum = 0.6)
- ✅ Valid: `leftThreshold: 0.5, rightThreshold: 0.5` (sum = 1.0 - boundaries meet at center)
- ✅ Valid: `leftThreshold: 0.8, rightThreshold: null` (only one threshold)
- ❌ Invalid: `leftThreshold: 0.6, rightThreshold: 0.6` (sum = 1.2 - overlapping!)

**Validation:** This constraint is enforced at construction time - `WindowCacheOptions` constructor will throw `ArgumentException` if violated.

**⚠️ Critical Understanding**: Thresholds are **NOT** calculated against individual buffer sizes. They represent a
percentage of the **entire cache window** (left buffer + requested range + right buffer).
See [Understanding the Sliding Window](#-understanding-the-sliding-window) for visual examples.

#### Debouncing

**`debounceDelay`** (TimeSpan, default: 100ms)

- **Definition**: Minimum time delay before executing a rebalance operation after it's triggered
- **Purpose**: Prevents cache thrashing when user rapidly changes access patterns
- **Behavior**: If multiple rebalance requests occur within the debounce window, only the last one executes
- **Typical values**: 20ms to 200ms (depending on data source latency)
- **Trade-off**: Higher values reduce rebalance frequency but may delay cache optimization

#### Execution Strategy

**`rebalanceQueueCapacity`** (int?, default: null)

- **Definition**: Controls the rebalance execution serialization strategy
- **Default**: `null` (unbounded task-based strategy - recommended for most scenarios)
- **Bounded capacity**: Set to `>= 1` to use channel-based strategy with backpressure
- **Purpose**: Choose between lightweight task chaining or strict queue capacity control
- **When to use bounded strategy**:
  - High-frequency rebalance scenarios requiring backpressure
  - Memory-constrained environments where queue growth must be limited
  - Testing scenarios requiring deterministic queue behavior
- **When to use unbounded strategy (default)**:
  - Normal operation with typical rebalance frequencies
  - Maximum performance with minimal overhead
  - Fire-and-forget execution model preferred
- **Trade-off**: Bounded capacity provides backpressure control but may slow intent processing when queue is full

**Strategy Comparison:**

| Strategy                 | Queue Capacity            | Backpressure     | Overhead        | Use Case                               |
|--------------------------|---------------------------|------------------|-----------------|----------------------------------------|
| **Task-based** (default) | Unbounded                 | None             | Minimal         | Recommended for most scenarios         |
| **Channel-based**        | Bounded (`capacity >= 1`) | Blocks when full | Slightly higher | High-frequency or resource-constrained |

**Note**: Both strategies guarantee single-writer architecture - only one rebalance executes at a time.

### Configuration Examples

**Forward-heavy scrolling** (e.g., log viewer, video player):

```csharp
var options = new WindowCacheOptions(
    leftCacheSize: 0.5,    // Minimal backward buffer
    rightCacheSize: 3.0,   // Aggressive forward prefetching
    leftThreshold: 0.25,
    rightThreshold: 0.15   // Trigger rebalance earlier when moving forward
);
```

**Bidirectional navigation** (e.g., paginated data grid):

```csharp
var options = new WindowCacheOptions(
    leftCacheSize: 1.5,    // Balanced backward buffer
    rightCacheSize: 1.5,   // Balanced forward buffer
    leftThreshold: 0.2,
    rightThreshold: 0.2
);
```

**Aggressive prefetching with stability** (e.g., high-latency data source):

```csharp
var options = new WindowCacheOptions(
    leftCacheSize: 2.0,
    rightCacheSize: 3.0,
    leftThreshold: 0.1,    // Rebalance early to maintain large buffers
    rightThreshold: 0.1,
    debounceDelay: TimeSpan.FromMilliseconds(100)  // Wait for access pattern to stabilize
);
```

**Bounded execution strategy** (e.g., high-frequency access with backpressure control):

```csharp
var options = new WindowCacheOptions(
    leftCacheSize: 1.0,
    rightCacheSize: 2.0,
    readMode: UserCacheReadMode.Snapshot,
    leftThreshold: 0.2,
    rightThreshold: 0.2,
    rebalanceQueueCapacity: 5  // Limit pending rebalance operations to 5
);
```

---

## ⚡ Execution Strategy Selection

The `rebalanceQueueCapacity` configuration parameter controls how the cache serializes background rebalance operations. Choosing the right strategy depends on your expected burst load characteristics and I/O latency patterns.

### Strategy Overview

| Configuration | Implementation | Queue Behavior | Best For |
|---------------|----------------|----------------|----------|
| `null` (default) | Task-based | Unbounded accumulation via task chaining | **99% of use cases** - typical workloads with moderate burst patterns |
| `>= 1` (e.g., `10`) | Channel-based | Bounded queue with backpressure | Extreme high-frequency scenarios (1000+ rapid requests with I/O latency) |

### Unbounded Execution (Default - Recommended)

**Configuration**:
```csharp
var options = new WindowCacheOptions(
    leftCacheSize: 1.0,
    rightCacheSize: 2.0,
    rebalanceQueueCapacity: null // Unbounded (default)
);
```

**Characteristics**:
- Task-based execution with unbounded task chaining
- Minimal overhead
- Excellent for typical workloads (burst ≤100 requests)
- Effective cancellation of obsolete rebalance operations
- No backpressure - intent processing never blocks

**Best for**:
- Web APIs with moderate scrolling (10-100 rapid requests)
- Gaming/real-time applications with fast local data
- Most production scenarios with typical access patterns
- Any scenario where request bursts are ≤100 or I/O latency is low

✅ **Recommended for 99% of use cases**

---

### Bounded Execution (High-Frequency Optimization)

**Configuration**:
```csharp
var options = new WindowCacheOptions(
    leftCacheSize: 1.0,
    rightCacheSize: 2.0,
    rebalanceQueueCapacity: 10 // Bounded queue with capacity of 10
);
```

**Characteristics**:
- Channel-based execution with bounded queue and backpressure
- Prevents unbounded queue accumulation under extreme burst loads
- Intent processing blocks when queue is full (applies backpressure)
- Provides dramatic speedup (25-196×) under extreme conditions (1000+ burst with I/O latency)
- Slightly less memory usage (5-9% reduction)
- Performs identically to unbounded for typical workloads (burst ≤100)

**Best for**:
- Streaming sensor data at 1000+ Hz with network I/O
- Any scenario with 1000+ rapid requests and significant I/O latency (50-100ms+)
- Systems requiring predictable bounded queue behavior
- Memory-constrained environments where accumulation must be prevented

⚠️ **Use for extreme high-frequency edge cases only**

---

### Decision Guide

**Choose Unbounded (null) if:**
- ✅ Your application has typical access patterns (10-100 rapid requests)
- ✅ I/O latency is low (<50ms) or burst size is moderate (≤100)
- ✅ You want minimal overhead and maximum performance for common scenarios
- ✅ **This covers 99% of production use cases**

**Choose Bounded (capacity ≥ 10) if:**
- ✅ Your application experiences extreme burst loads (1000+ rapid requests)
- ✅ Data source has significant latency (50-100ms+) during bursts
- ✅ You need predictable queue depth to prevent accumulation
- ✅ You require bounded memory usage for rebalance operations

**Key Insight**: Both strategies perform identically for typical workloads (burst ≤100). The bounded strategy's dramatic performance advantage (25-196× faster) only appears under **extreme conditions** (1000+ burst with I/O latency), making unbounded the safer default choice.

**For comprehensive benchmark methodology, performance data, and detailed analysis**, see:
- [ExecutionStrategyBenchmarks Documentation](benchmarks/SlidingWindowCache.Benchmarks/README.md#-execution-strategy-benchmarks)
- [Benchmark Results](benchmarks/SlidingWindowCache.Benchmarks/Results/SlidingWindowCache.Benchmarks.Benchmarks.ExecutionStrategyBenchmarks-report-github.md)

---

## 📊 Optional Diagnostics

The cache supports optional diagnostics for monitoring behavior, measuring performance, and validating system
invariants. This is useful for:

- **Testing and validation**: Verify cache behavior meets expected patterns
- **Performance monitoring**: Track cache hit/miss ratios and rebalance frequency
- **Debugging**: Understand cache lifecycle events in development
- **Production observability**: Optional instrumentation for metrics collection

### ⚠️ CRITICAL: Exception Handling

**You MUST handle the `RebalanceExecutionFailed` event in production applications.**

Rebalance operations run in fire-and-forget background tasks. When exceptions occur, they are silently swallowed to
prevent application crashes. Without proper handling of `RebalanceExecutionFailed`:

- ❌ Silent failures in background operations
- ❌ Cache stops rebalancing with no indication
- ❌ Degraded performance with no diagnostics
- ❌ Data source errors go unnoticed

**Minimum requirement: Log all failures**

```csharp
public class LoggingCacheDiagnostics : ICacheDiagnostics
{
    private readonly ILogger<LoggingCacheDiagnostics> _logger;
    
    public LoggingCacheDiagnostics(ILogger<LoggingCacheDiagnostics> logger)
    {
        _logger = logger;
    }
    
    public void RebalanceExecutionFailed(Exception ex)
    {
        // CRITICAL: Always log rebalance failures
        _logger.LogError(ex, "Cache rebalance execution failed. Cache may not be optimally sized.");
    }
    
    // ...implement other methods (can be no-op if you only care about failures)...
}
```

For production systems, consider:

- **Alerting**: Trigger alerts after N consecutive failures
- **Metrics**: Track failure rate and exception types
- **Circuit breaker**: Disable rebalancing after repeated failures
- **Structured logging**: Include cache state and requested range context

### Using Diagnostics

```csharp
using SlidingWindowCache.Infrastructure.Instrumentation;

// Create diagnostics instance
var diagnostics = new EventCounterCacheDiagnostics();

// Pass to cache constructor
var cache = new WindowCache<int, string, IntegerFixedStepDomain>(
    dataSource: myDataSource,
    domain: new IntegerFixedStepDomain(),
    options: options,
    cacheDiagnostics: diagnostics  // Optional parameter
);

// Access diagnostic counters
Console.WriteLine($"Full cache hits: {diagnostics.UserRequestFullCacheHit}");
Console.WriteLine($"Rebalances completed: {diagnostics.RebalanceExecutionCompleted}");
```

### Zero-Cost Abstraction

If no diagnostics instance is provided (default), the cache uses `NoOpDiagnostics` - a zero-overhead implementation with
empty method bodies that the JIT compiler can optimize away completely. This ensures diagnostics add zero performance
overhead when not used.

**For complete metric descriptions, custom implementations, and advanced patterns,
see [Diagnostics Guide](docs/diagnostics.md).**

---

## 📚 Documentation

### Learning Paths

**Choose your path based on your needs:**

#### 🚀 Path 1: Quick Start (Getting Started Fast)

**Goal**: Get up and running with working code and common patterns.

1. **[README - Quick Start](#-quick-start)** - Basic usage examples (you're already here!)
2. **[README - Configuration Guide](#configuration)** - Understand the 5 key parameters
3. **[Storage Strategies](docs/storage-strategies.md)** - Choose Snapshot vs CopyOnRead for your use case
4. **[Glossary - Common Misconceptions](docs/glossary.md#common-misconceptions)** - Avoid common pitfalls
5. **[Diagnostics](docs/diagnostics.md)** - Add optional instrumentation for visibility

**When to use this path**: Building features, integrating the cache, performance tuning.

---

#### 🏗️ Path 2: Deep Dive (Advanced Understanding)

**Goal**: Understand architecture, invariants, and implementation details.

1. **[Glossary](docs/glossary.md)** - 📖 **Start here** - Canonical term definitions with navigation guide
2. **[Architecture Model](docs/architecture-model.md)** - Core architectural patterns (single-writer, decision-driven execution, smart eventual consistency)
3. **[Invariants](docs/invariants.md)** - 49 system invariants with formal specifications
4. **[Component Map](docs/component-map.md)** - Comprehensive component catalog with invariant implementation mapping
5. **[Scenario Model](docs/scenario-model.md)** - Temporal behavior scenarios (User Path, Decision Path, Execution Path)
6. **[Cache State Machine](docs/cache-state-machine.md)** - Formal state transitions and mutation ownership
7. **[Actors & Responsibilities](docs/actors-and-responsibilities.md)** - Actor model with invariant ownership
8. **[Actors to Components Mapping](docs/actors-to-components-mapping.md)** - Architectural actors → concrete components

**When to use this path**: Contributing code, debugging complex issues, understanding design decisions, architectural review.

---

### Reference Documentation

#### Mathematical Foundations

- **[Intervals.NET](https://github.com/blaze6950/Intervals.NET)** - Interval/range library providing `Range`, `Domain`, `RangeData`, and interval operations

#### Testing & Benchmarking

- **[Invariant Test Suite](tests/SlidingWindowCache.Invariants.Tests/README.md)** - 27 automated invariant tests validating architectural contracts
- **[Benchmark Suite](benchmarks/SlidingWindowCache.Benchmarks/README.md)** - BenchmarkDotNet performance benchmarks:
  - **RebalanceFlowBenchmarks** - Rebalance cost analysis (Fixed/Growing/Shrinking patterns)
  - **UserFlowBenchmarks** - User-facing API latency (Hit/Partial/Miss scenarios)
  - **ScenarioBenchmarks** - End-to-end cold start performance
  - **Storage Comparison** - Snapshot vs CopyOnRead tradeoffs

#### Testing Infrastructure

**Deterministic Synchronization**: `WaitForIdleAsync()` provides race-free synchronization with background operations for testing, shutdown, health checks. Uses "was idle at some point" semantics (eventual consistency). See [Invariants - Testing Infrastructure](docs/invariants.md#testing-infrastructure-deterministic-synchronization).

### Key Architectural Principles

> **📖 For detailed explanations, see:** [Architecture Model](docs/architecture-model.md) | [Invariants](docs/invariants.md) | [Glossary](docs/glossary.md)

1. **Single-Writer Architecture**: Only Rebalance Execution writes to cache state; User Path is read-only. Eliminates race conditions through architectural constraints.

2. **Decision-Driven Execution**: Rebalance necessity determined by analytical validation before execution. Enables work avoidance and prevents thrashing.

3. **Multi-Stage Validation Pipeline**: Four validation stages must all pass before rebalance executes (NoRebalanceRange check, pending coverage check, desired==current check). See [Scenario Model - Decision Path](docs/scenario-model.md#ii-rebalance-decision-path--decision-scenarios).

4. **Smart Eventual Consistency**: Cache converges to optimal state asynchronously while avoiding unnecessary operations through validation.

5. **Intent Semantics**: Intents are signals (observed access patterns), not commands (mandatory work). Validation determines execution necessity.

6. **Cache Contiguity**: Cache data remains contiguous without gaps. Non-intersecting requests replace cache entirely.

7. **User Path Priority**: User requests always served immediately. Background rebalancing never blocks user operations.

8. **Lock-Free Concurrency**: Intent management uses atomic operations (`Volatile`, `Interlocked`). Execution serialization ensures single-writer semantics.

---

## ⚡ Performance Considerations

- **Snapshot mode**: O(1) reads, but O(n) rebalance with array allocation
- **CopyOnRead mode**: O(n) reads (copy cost), but cheaper rebalance operations
- **Rebalancing is asynchronous**: Does not block user reads
- **Debouncing**: Multiple rapid requests trigger only one rebalance operation
- **Diagnostics overhead**: Zero when not used (NoOpDiagnostics); minimal when enabled (~1-5ns per event)

---

## 🔧 CI/CD & Package Information

### Continuous Integration

This project uses GitHub Actions for automated testing and deployment:

- **Build & Test**: Runs on every push and pull request
    - Compiles entire solution in Release configuration
    - Executes all test suites (Unit, Integration, Invariants) with code coverage
    - Validates WebAssembly compatibility via `net8.0-browser` compilation
    - Uploads coverage reports to Codecov

- **NuGet Publishing**: Automatic on main branch pushes
    - Packages library with symbols and source link
    - Publishes to NuGet.org with skip-duplicate
    - Stores package artifacts in workflow runs

### WebAssembly Support

SlidingWindowCache is validated for WebAssembly compatibility:

- **Target Framework**: `net8.0-browser` compilation validated in CI
- **Validation Project**: `SlidingWindowCache.WasmValidation` ensures all public APIs work in browser environments
- **Compatibility**: All library features available in Blazor WebAssembly and other WASM scenarios

### NuGet Package

**Package ID**: `SlidingWindowCache`  
**Current Version**: 1.0.0

```bash
# Install via .NET CLI
dotnet add package SlidingWindowCache

# Install via Package Manager
Install-Package SlidingWindowCache
```

**Package Contents**:

- Main library assembly (`SlidingWindowCache.dll`)
- Debug symbols (`.snupkg` for debugging)
- Source Link (GitHub source integration for "Go to Definition")
- README.md (this file)

**Dependencies**:

- Intervals.NET.Data (>= 0.0.1)
- Intervals.NET.Domain.Default (>= 0.0.2)
- Intervals.NET.Domain.Extensions (>= 0.0.3)
- .NET 8.0 or higher

---

## 🤝 Contributing & Feedback

This project is a **personal R&D and engineering exploration** focused on cache design patterns, concurrent systems
architecture, and performance optimization. While it's primarily a research endeavor, feedback and community input are
highly valued and welcomed.

### We Welcome

- **Bug reports** - Found an issue? Please open a GitHub issue with reproduction steps
- **Feature suggestions** - Have ideas for improvements? Start a discussion or open an issue
- **Performance insights** - Benchmarked the cache in your scenario? Share your findings
- **Architecture feedback** - Thoughts on the design patterns or implementation? Let's discuss
- **Documentation improvements** - Found something unclear? Contributions to docs are appreciated
- **Positive feedback** - If the library is useful to you, that's great to know!

### How to Contribute

- **Issues**: Use [GitHub Issues](https://github.com/blaze6950/SlidingWindowCache/issues) for bugs, feature requests, or
  questions
- **Discussions**: Use [GitHub Discussions](https://github.com/blaze6950/SlidingWindowCache/discussions) for broader
  topics, ideas, or design conversations
- **Pull Requests**: Code contributions are welcome, but please open an issue first to discuss significant changes

This project benefits from community feedback while maintaining a focused research direction. All constructive input
helps improve the library's design, implementation, and documentation.

---

## License

MIT