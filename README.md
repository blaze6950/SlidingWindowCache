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
- [Configuration](#-configuration)
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
│  Decision Engine (User Thread - CPU-only)       │
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
2. **Decision happens synchronously** - validation is CPU-only (microseconds), happens in user thread before scheduling
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

- [Concurrency Model](docs/concurrency-model.md) - Smart eventual consistency and synchronous decision execution
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

**⚠️ Critical Understanding**: Thresholds are **NOT** calculated against individual buffer sizes. They represent a
percentage of the **entire cache window** (left buffer + requested range + right buffer).
See [Understanding the Sliding Window](#-understanding-the-sliding-window) for visual examples.

#### Debouncing

**`debounceDelay`** (TimeSpan, default: 50ms)

- **Definition**: Minimum time delay before executing a rebalance operation after it's triggered
- **Purpose**: Prevents cache thrashing when user rapidly changes access patterns
- **Behavior**: If multiple rebalance requests occur within the debounce window, only the last one executes
- **Typical values**: 20ms to 200ms (depending on data source latency)
- **Trade-off**: Higher values reduce rebalance frequency but may delay cache optimization

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

For detailed architectural documentation, see:

### Mathematical Foundations

- **[Intervals.NET](https://github.com/blaze6950/Intervals.NET)** - Robust interval and range handling library that
  underpins cache logic. See README and documentation for core concepts like `Range`, `Domain`, `RangeData`, and
  interval operations.

### Core Architecture

- **[Invariants](docs/invariants.md)** - Complete list of system invariants and guarantees
- **[Scenario Model](docs/scenario-model.md)** - Temporal behavior scenarios (User Path, Decision Path, Rebalance
  Execution)
- **[Actors & Responsibilities](docs/actors-and-responsibilities.md)** - System actors and invariant ownership mapping
- **[Actors to Components Mapping](docs/actors-to-components-mapping.md)** - How architectural actors map to concrete
  components
- **[Cache State Machine](docs/cache-state-machine.md)** - Formal state machine with mutation ownership and concurrency
  semantics
- **[Concurrency Model](docs/concurrency-model.md)** - Single-writer architecture and eventual consistency model

### Implementation Details

- **[Component Map](docs/component-map.md)** - Comprehensive component catalog with responsibilities and interactions
- **[Storage Strategies](docs/storage-strategies.md)** - Detailed comparison of Snapshot vs. CopyOnRead modes and
  multi-level cache patterns
- **[Diagnostics](docs/diagnostics.md)** - Optional instrumentation and observability guide

### Testing Infrastructure

- **[Invariant Test Suite README](tests/SlidingWindowCache.Invariants.Tests/README.md)** - Comprehensive invariant test
  suite with deterministic synchronization
- **[Benchmark Suite README](benchmarks/SlidingWindowCache.Benchmarks/README.md)** - BenchmarkDotNet performance
  benchmarks
    - **RebalanceFlowBenchmarks** - Behavior-driven rebalance cost analysis (Fixed/Growing/Shrinking span patterns)
    - **UserFlowBenchmarks** - User-facing API latency (Full hit, Partial hit, Full miss scenarios)
    - **ScenarioBenchmarks** - End-to-end cold start performance
    - **Storage Strategy Comparison** - Snapshot vs CopyOnRead allocation and performance tradeoffs across all suites
- **Deterministic Testing**: `WaitForIdleAsync()` API provides race-free synchronization with background rebalance
  operations for testing, graceful shutdown, health checks, and integration scenarios

### Key Architectural Principles

1. **Single-Writer Architecture**: Only Rebalance Execution writes to cache state; User Path is read-only. This
   eliminates race conditions through architectural constraints rather than locks.
   See [Concurrency Model](docs/concurrency-model.md).

2. **Decision-Driven Execution**: Rebalance necessity determined by synchronous CPU-only analytical validation in user
   thread (microseconds). Enables immediate work avoidance and prevents intent thrashing.
   See [Invariants - Section D](docs/invariants.md#d-rebalance-decision-path-invariants).

3. **Multi-Stage Validation Pipeline**:
    - Stage 1: NoRebalanceRange containment check (fast-path rejection)
    - Stage 2: Pending rebalance coverage check (anti-thrashing)
    - Stage 3: Desired == Current check (no-op prevention)

   Rebalance executes ONLY if ALL stages confirm necessity.
   See [Scenario Model - Decision Path](docs/scenario-model.md#ii-rebalance-decision-path--decision-scenarios).

4. **Smart Eventual Consistency**: Cache converges to optimal configuration asynchronously while avoiding unnecessary
   work through validation. System prioritizes decision correctness and work avoidance over aggressive rebalance
   responsiveness.
   See [Concurrency Model - Smart Eventual Consistency](docs/concurrency-model.md#smart-eventual-consistency-model).

5. **Intent Semantics**: Intents represent observed access patterns (signals), not mandatory work (commands). Publishing
   an intent does not guarantee rebalance execution - validation determines necessity.
   See [Invariants C.24](docs/invariants.md).

6. **Cache Contiguity Rule**: Cache data must always remain contiguous (no gaps allowed). Non-intersecting requests
   fully replace the cache rather than creating partial/gapped states. See [Invariants A.9a](docs/invariants.md).

7. **User Path Priority**: User requests always served immediately. When validation confirms new rebalance is necessary,
   pending rebalance is cancelled and rescheduled. Cancellation is mechanical coordination (prevents concurrent
   executions), not a decision mechanism. See [Cache State Machine](docs/cache-state-machine.md).

8. **Lock-Free Concurrency**: Intent management uses `Volatile.Read/Write` for safe memory visibility - no locks, no
   race conditions, guaranteed progress. Thread-safety achieved through architectural constraints (single-writer) and
   atomic reference operations.
   See [Concurrency Model - Lock-Free Implementation](docs/concurrency-model.md#lock-free-implementation).

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