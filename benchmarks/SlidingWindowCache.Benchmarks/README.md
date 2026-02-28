# SlidingWindowCache Benchmarks

Comprehensive BenchmarkDotNet performance suite for SlidingWindowCache, measuring architectural performance characteristics using **public API only**.

**Methodologically Correct Benchmarks**: This suite follows rigorous benchmark methodology to ensure deterministic, reliable, and interpretable results.

---

## Current Performance Baselines

For current measured performance data, see the committed reports in `benchmarks/SlidingWindowCache.Benchmarks/Results/`:

- **User Request Flow**: [UserFlowBenchmarks-report-github.md](Results/SlidingWindowCache.Benchmarks.Benchmarks.UserFlowBenchmarks-report-github.md)
- **Rebalance Mechanics**: [RebalanceFlowBenchmarks-report-github.md](Results/SlidingWindowCache.Benchmarks.Benchmarks.RebalanceFlowBenchmarks-report-github.md)
- **End-to-End Scenarios**: [ScenarioBenchmarks-report-github.md](Results/SlidingWindowCache.Benchmarks.Benchmarks.ScenarioBenchmarks-report-github.md)
- **Execution Strategy Comparison**: [ExecutionStrategyBenchmarks-report-github.md](Results/SlidingWindowCache.Benchmarks.Benchmarks.ExecutionStrategyBenchmarks-report-github.md)

These reports are updated when benchmarks are re-run and committed to track performance over time.

---

## Overview

This benchmark project provides reliable, deterministic performance measurements organized around **two distinct execution flows** of SlidingWindowCache:

### Execution Flow Model

SlidingWindowCache has **two independent cost centers**:

1. **User Request Flow** → Measures latency/cost of user-facing API calls
   - Rebalance/background activity is **NOT** included in measured results
   - Focus: Direct `GetDataAsync` call overhead
   
2. **Rebalance/Maintenance Flow** → Measures cost of window maintenance operations
   - Explicitly waits for stabilization using `WaitForIdleAsync`
   - Focus: Background window management and cache mutation costs

### What We Measure

- **Snapshot vs CopyOnRead** storage modes across both flows
- **User Request Flow**: Full hit, partial hit, full miss scenarios
- **Rebalance Flow**: Maintenance costs after partial hit and full miss
- **Scenario Testing**: Cold start performance and sequential locality advantages
- **Scaling Behavior**: Performance across varying data volumes and cache sizes

---

## Parameterization Strategy

Benchmarks are **parameterized** to measure scaling behavior across different workload characteristics. The parameter strategy differs by benchmark suite to target specific performance aspects:

### User Flow & Scenario Benchmarks Parameters

These benchmarks use a 2-axis parameter matrix to explore cache sizing tradeoffs:

1. **`RangeSpan`** - Requested range size
   - Values: `[100, 1_000, 10_000]`
   - Purpose: Test how storage strategies scale with data volume
   - Range: Small to large data volumes

2. **`CacheCoefficientSize`** - Left/right prefetch multipliers
   - Values: `[1, 10, 100]`
   - Purpose: Test rebalance cost vs cache size tradeoff
   - Total cache size = `RangeSpan × (1 + leftCoeff + rightCoeff)`

**Parameter Matrix**: 3 range sizes × 3 cache coefficients = **9 parameter combinations per benchmark method**

### Rebalance Flow Benchmarks Parameters

These benchmarks use a 3-axis orthogonal design to isolate rebalance behavior:

1. **`Behavior`** - Range span evolution pattern
   - Values: `[Fixed, Growing, Shrinking]`
   - Purpose: Models how requested range span changes over time
   - Fixed: Constant span, position shifts
   - Growing: Span increases each iteration
   - Shrinking: Span decreases each iteration

2. **`Strategy`** - Storage rematerialization approach
   - Values: `[Snapshot, CopyOnRead]`
   - Purpose: Compare array-based vs list-based storage under different dynamics

3. **`BaseSpanSize`** - Initial requested range size
   - Values: `[100, 1_000, 10_000]`
   - Purpose: Test scaling behavior from small to large data volumes

**Parameter Matrix**: 3 behaviors × 2 strategies × 3 sizes = **18 parameter combinations**

### Expected Scaling Insights

**Snapshot Mode:**
- ✅ **Advantage at small-to-medium sizes** (RangeSpan < 10,000)
  - Zero-allocation reads dominate
  - Rebalance cost acceptable
- ⚠️ **LOH pressure at large sizes** (RangeSpan ≥ 10,000)
  - Array allocations go to LOH (no compaction)
  - GC pressure increases with Gen2 collections visible

**CopyOnRead Mode:**
- ❌ **Disadvantage at small sizes** (RangeSpan < 1,000)
  - Per-read allocation overhead visible
  - List overhead not amortized
- ✅ **Competitive at medium-to-large sizes** (RangeSpan ≥ 1,000)
  - List growth amortizes allocation cost
  - Reduced LOH pressure

### Interpretation Guide

When analyzing results, look for:

1. **Allocation patterns**: 
   - Snapshot: Zero on read, large on rebalance
   - CopyOnRead: Constant on read, incremental on rebalance

2. **Memory usage trends**:
   - Watch for Gen2 collections (LOH pressure indicator at large BaseSpanSize)
   - Compare total allocated bytes across modes

3. **Execution time patterns**:
   - Compare rebalance cost across parameters
   - Observe user flow latencies for cache hits vs misses

4. **Behavior-driven insights (RebalanceFlowBenchmarks)**:
   - Fixed span: Predictable, stable costs
   - Growing span: Storage strategy differences become visible
   - Shrinking span: Both strategies handle gracefully
   - CopyOnRead shows more stable allocation patterns across behaviors

---

## Design Principles

### 1. Public API Only
- ✅ No internal types
- ✅ No reflection
- ✅ Only uses public `WindowCache` API

### 2. Deterministic Behavior
- ✅ `SynchronousDataSource` with no randomness
- ✅ `SynchronousDataSource` for zero-latency isolation
- ✅ Stable, predictable data generation
- ✅ No I/O operations

### 3. Methodological Rigor
- ✅ **No state reuse**: Fresh cache per iteration via `[IterationSetup]`
- ✅ **Explicit rebalance handling**: `WaitForIdleAsync` in setup/cleanup for `UserFlowBenchmarks`; INSIDE benchmark method for `RebalanceFlowBenchmarks` (measuring rebalance completion as part of cost)
- ✅ **Clear separation**: Read microbenchmarks vs partial-hit vs scenario-level
- ✅ **Isolation**: Each benchmark measures ONE thing
- ✅ **MemoryDiagnoser** for allocation tracking
- ✅ **MarkdownExporter** for report generation
- ✅ **Parameterization**: Comprehensive scaling analysis

---

## Benchmark Categories

Benchmarks are organized by **execution flow** to clearly separate user-facing costs from background maintenance costs.

### User Request Flow Benchmarks

**File**: `UserFlowBenchmarks.cs`

**Goal**: Measure ONLY user-facing request latency. Rebalance/background activity is EXCLUDED from measurements.

**Parameters**: `RangeSpan` × `CacheCoefficientSize` = **9 combinations**
- RangeSpan: `[100, 1_000, 10_000]`
- CacheCoefficientSize: `[1, 10, 100]`

**Contract**:
- Benchmark methods measure ONLY `GetDataAsync` cost
- `WaitForIdleAsync` moved to `[IterationCleanup]`
- Fresh cache per iteration
- Deterministic overlap patterns (no randomness)

**Benchmark Methods** (grouped by category):

| Category       | Method                                     | Purpose                                     |
|----------------|--------------------------------------------|---------------------------------------------|
| **FullHit**    | `User_FullHit_Snapshot`                    | Baseline: Full cache hit with Snapshot mode |
| **FullHit**    | `User_FullHit_CopyOnRead`                  | Full cache hit with CopyOnRead mode         |
| **PartialHit** | `User_PartialHit_ForwardShift_Snapshot`    | Partial hit moving right (Snapshot)         |
| **PartialHit** | `User_PartialHit_ForwardShift_CopyOnRead`  | Partial hit moving right (CopyOnRead)       |
| **PartialHit** | `User_PartialHit_BackwardShift_Snapshot`   | Partial hit moving left (Snapshot)          |
| **PartialHit** | `User_PartialHit_BackwardShift_CopyOnRead` | Partial hit moving left (CopyOnRead)        |
| **FullMiss**   | `User_FullMiss_Snapshot`                   | Full cache miss (Snapshot)                  |
| **FullMiss**   | `User_FullMiss_CopyOnRead`                 | Full cache miss (CopyOnRead)                |

**Expected Results**:
- Full hit: Snapshot shows minimal allocation, CopyOnRead allocation scales with cache size
- Partial hit: Both modes serve request immediately, rebalance deferred to cleanup
- Full miss: Request served from data source, rebalance deferred to cleanup
- **Scaling**: CopyOnRead allocation grows linearly with `CacheCoefficientSize`

---

### Rebalance Flow Benchmarks

**File**: `RebalanceFlowBenchmarks.cs`

**Goal**: Measure rebalance mechanics and storage rematerialization cost through behavior-driven modeling. This suite isolates how storage strategies handle different range span evolution patterns.

**Philosophy**: Models system behavior through three orthogonal axes:
- **Span Behavior** (Fixed/Growing/Shrinking) - How requested range span evolves
- **Storage Strategy** (Snapshot/CopyOnRead) - Rematerialization approach
- **Base Span Size** (100/1,000/10,000) - Scaling behavior

**Parameters**: `Behavior` × `Strategy` × `BaseSpanSize` = **18 combinations**
- Behavior: `[Fixed, Growing, Shrinking]`
- Strategy: `[Snapshot, CopyOnRead]`
- BaseSpanSize: `[100, 1_000, 10_000]`

**Contract**:
- Uses `SynchronousDataSource` (zero latency) to isolate cache mechanics from I/O
- `WaitForIdleAsync` INSIDE benchmark methods (measuring rebalance completion)
- Deterministic request sequence generated in `IterationSetup`
- Each request triggers rebalance via aggressive thresholds
- Executes 10 requests per invocation, measuring cumulative rebalance cost

**Benchmark Method**:

| Method      | Purpose                                                                                      |
|-------------|----------------------------------------------------------------------------------------------|
| `Rebalance` | Measures complete rebalance cycle cost for the configured span behavior and storage strategy |

**Span Behaviors Explained**:
- **Fixed**: Span remains constant, position shifts by +1 each request (models stable sliding window)
- **Growing**: Span increases by 100 elements per request (models expanding data requirements)
- **Shrinking**: Span decreases by 100 elements per request (models contracting data requirements)

**Expected Results**:
- **Execution time**: Cumulative rebalance overhead for 10 operations
- **Allocation patterns**:
  - Fixed/Snapshot: Higher allocations, scales with BaseSpanSize
  - Fixed/CopyOnRead: Lower allocations due to buffer reuse
  - CopyOnRead shows allocation reduction through buffer reuse
- **GC pressure**: Gen2 collections may be visible at large BaseSpanSize for Snapshot mode
- **Behavior impact**: Growing span may increase allocation for CopyOnRead compared to Fixed

---

### Scenario Benchmarks (End-to-End)

**File**: `ScenarioBenchmarks.cs`

**Goal**: End-to-end scenario testing focusing on cold start performance. NOT microbenchmarks - measures complete workflows.

**Parameters**: `RangeSpan` × `CacheCoefficientSize` = **9 combinations**
- RangeSpan: `[100, 1_000, 10_000]`
- CacheCoefficientSize: `[1, 10, 100]`

**Contract**:
- Fresh cache per iteration
- Cold start: Measures complete initialization including rebalance
- `WaitForIdleAsync` is PART of the measured cold start cost

**Benchmark Methods** (grouped by category):

| Category      | Method                           | Purpose                                       |
|---------------|----------------------------------|-----------------------------------------------|
| **ColdStart** | `ColdStart_Rebalance_Snapshot`   | Baseline: Initial cache population (Snapshot) |
| **ColdStart** | `ColdStart_Rebalance_CopyOnRead` | Initial cache population (CopyOnRead)         |

**Expected Results**:
- Cold start: Measures complete initialization including rebalance
- Allocation patterns differ between modes:
  - Snapshot: Single upfront array allocation
  - CopyOnRead: List-based incremental allocation, less memory spike
- **Scaling**: Both modes should show comparable execution times
- **Memory differences**: 
  - Small ranges: Minimal differences between storage modes
  - Large ranges: Both modes show substantial allocations, with varying ratios
  - CopyOnRead allocation ratio varies depending on cache size
- **GC impact**: Gen2 collections may be visible at largest parameter combinations

---

### Execution Strategy Benchmarks

**File**: `ExecutionStrategyBenchmarks.cs`

**Goal**: Compare unbounded vs bounded execution queue performance under rapid burst request patterns with cache-hit optimization. Measures how queue capacity configuration affects system convergence time under varying I/O latencies and burst loads.

**Philosophy**: This benchmark evaluates the performance trade-offs between:
- **Unbounded (NoCapacity)**: `RebalanceQueueCapacity = null` → Task-based execution with unbounded accumulation
- **Bounded (WithCapacity)**: `RebalanceQueueCapacity = 10` → Channel-based execution with bounded queue and backpressure

**Parameters**: `DataSourceLatencyMs` × `BurstSize` = **9 combinations**
- DataSourceLatencyMs: `[0, 50, 100]` - Simulates network/database I/O latency
- BurstSize: `[10, 100, 1000]` - Number of rapid sequential requests

**Baseline**: `BurstPattern_NoCapacity` (unbounded queue, Task-based implementation)

**Contract**:
- Cold start prepopulation ensures all burst requests are cache hits in User Path
- Sequential request pattern with +1 shift triggers rebalance intents (leftThreshold=1.0)
- DebounceDelay = 0ms (critical for measurable queue accumulation)
- Measures convergence time until system idle (via `WaitForIdleAsync`)
- BenchmarkDotNet automatically calculates ratio columns relative to NoCapacity baseline

**Benchmark Methods**:

| Method                      | Baseline | Configuration                   | Implementation                  | Purpose                         |
|-----------------------------|----------|---------------------------------|---------------------------------|---------------------------------|
| `BurstPattern_NoCapacity`   | ✓ Yes    | `RebalanceQueueCapacity = null` | Task-based unbounded execution  | Baseline for ratio calculations |
| `BurstPattern_WithCapacity` | -        | `RebalanceQueueCapacity = 10`   | Channel-based bounded execution | Measured relative to baseline   |

**Interpretation Guide**:

**Ratio Column Interpretation**:
- **Ratio < 1.0**: WithCapacity is faster than NoCapacity
  - Example: Ratio = 0.012 means WithCapacity is 83× faster (1 / 0.012 ≈ 83)
- **Ratio > 1.0**: WithCapacity is slower than NoCapacity
  - Example: Ratio = 1.44 means WithCapacity is 1.44× slower (44% overhead)
- **Ratio ≈ 1.0**: Both strategies perform similarly

**What to Look For**:

1. **Low Latency Scenarios**: Both strategies typically perform similarly at low burst sizes; bounded may show advantages at extreme burst sizes

2. **High Latency + High Burst**: Bounded strategy's backpressure mechanism should provide significant speedup when both I/O latency and burst size are high

3. **Memory Allocation**: Compare Alloc Ratio column to assess memory efficiency differences between strategies

**When to Use Each Strategy**:

✅ **Unbounded (NoCapacity) - Recommended for typical use cases**:
- Web APIs with moderate scrolling (10-100 rapid requests)
- Gaming/real-time with fast local data
- Scenarios where burst sizes remain moderate
- Minimal overhead, excellent typical-case performance

✅ **Bounded (WithCapacity) - High-frequency edge cases**:
- Streaming sensor data at very high frequencies (1000+ Hz) with network I/O
- Scenarios with extreme burst sizes and significant I/O latency
- When predictable bounded behavior is critical

---

## Running Benchmarks

### Quick Start

```bash
# Run all benchmarks (WARNING: This will take 2-4 hours with current parameterization)
dotnet run -c Release --project benchmarks/SlidingWindowCache.Benchmarks

# Run specific benchmark class
dotnet run -c Release --project benchmarks/SlidingWindowCache.Benchmarks --filter "*UserFlowBenchmarks*"
dotnet run -c Release --project benchmarks/SlidingWindowCache.Benchmarks --filter "*RebalanceFlowBenchmarks*"
dotnet run -c Release --project benchmarks/SlidingWindowCache.Benchmarks --filter "*ScenarioBenchmarks*"
dotnet run -c Release --project benchmarks/SlidingWindowCache.Benchmarks --filter "*ExecutionStrategyBenchmarks*"
```

### Filtering Options

```bash
# Run only FullHit category (UserFlowBenchmarks)
dotnet run -c Release --project benchmarks/SlidingWindowCache.Benchmarks --filter "*FullHit*"

# Run only Rebalance benchmarks
dotnet run -c Release --project benchmarks/SlidingWindowCache.Benchmarks --filter "*RebalanceFlowBenchmarks*"

# Run specific method
dotnet run -c Release --project benchmarks/SlidingWindowCache.Benchmarks --filter "*User_FullHit_Snapshot*"

# Run specific parameter combination (e.g., BaseSpanSize=1000)
dotnet run -c Release --project benchmarks/SlidingWindowCache.Benchmarks --filter "*" -- --filter "*BaseSpanSize_1000*"
```

### Managing Execution Time

With parameterization, total execution time can be significant:

**Default configuration:**
- UserFlowBenchmarks: 9 parameters × 8 methods = 72 benchmarks
- RebalanceFlowBenchmarks: 18 parameters × 1 method = 18 benchmarks  
- ScenarioBenchmarks: 9 parameters × 2 methods = 18 benchmarks
- ExecutionStrategyBenchmarks: 9 parameters × 2 methods = 18 benchmarks
- **Total: ~126 individual benchmarks**
- **Estimated time: 3-5 hours** (depending on hardware)

**Faster turnaround options:**

1. **Use SimpleJob for development:**
```csharp
[SimpleJob(warmupCount: 3, iterationCount: 5)]  // Add to class attributes
```

2. **Run subset of parameters:**
```bash
# Comment out larger parameter values in code temporarily
[Params(100, 1_000)]  // Instead of all 3 values
```

3. **Run by category:**
```bash
# Focus on one flow at a time
dotnet run -c Release --project benchmarks/SlidingWindowCache.Benchmarks --filter "*FullHit*"
```

4. **Run single benchmark class:**
```bash
# Test specific aspect
dotnet run -c Release --project benchmarks/SlidingWindowCache.Benchmarks --filter "*ScenarioBenchmarks*"
```

---

## Data Sources

### SynchronousDataSource
Zero-latency synchronous data source for isolating cache mechanics:

```csharp
// Zero latency - isolates rebalance cost from I/O
var dataSource = new SynchronousDataSource(domain);
```

**Purpose**:
- Used in all benchmarks for deterministic, reproducible results
- Returns synchronous `IEnumerable<T>` wrapped in completed `Task`
- No `Task.Delay` or async overhead
- Measures pure cache mechanics without I/O interference

**Data Generation**:
- Deterministic: Position `i` produces value `i`
- No randomness
- Stable across runs
- Predictable memory footprint

---

## Interpreting Results

### Mean Execution Time
- Lower is better
- Compare Snapshot vs CopyOnRead for same scenario
- Look for order-of-magnitude differences

### Allocations
- **Snapshot mode**: Watch for large array allocations during rebalance
- **CopyOnRead mode**: Watch for per-read allocations
- **Gen 0/1/2**: Track garbage collection pressure

### Memory Diagnostics
- **Allocated**: Total bytes allocated
- **Gen 0/1/2 Collections**: GC pressure indicator
- **LOH**: Large Object Heap allocations (arrays ≥85KB)

---

## Methodological Guarantees

### ✅ No State Drift
Every iteration starts from a clean, deterministic cache state via `[IterationSetup]`.

### ✅ Explicit Rebalance Handling
- Benchmarks that trigger rebalance use `[IterationCleanup]` to wait for completion
- NO `WaitForIdleAsync` inside benchmark methods (would contaminate measurements)
- Setup phases use `WaitForIdleAsync` to ensure deterministic starting state

### ✅ Clear Separation
- **Read microbenchmarks**: Rebalance disabled, measure read path only
- **Partial hit benchmarks**: Rebalance enabled, deterministic overlap, cleanup handles rebalance
- **Scenario benchmarks**: Full sequential patterns, cleanup handles stabilization

### ✅ Isolation
- `RebalanceFlowBenchmarks` uses `SynchronousDataSource` to isolate cache mechanics from I/O
- Each benchmark measures ONE architectural characteristic

---

## Expected Performance Characteristics

### Snapshot Mode
- ✅ **Best for**: Read-heavy workloads (high read:rebalance ratio)
- ✅ **Strengths**: Zero-allocation reads, fastest read performance
- ❌ **Weaknesses**: Expensive rebalancing, LOH pressure

### CopyOnRead Mode
- ✅ **Best for**: Write-heavy workloads (frequent rebalancing)
- ✅ **Strengths**: Cheap rebalancing, reduced LOH pressure
- ❌ **Weaknesses**: Allocates on every read, slower read performance

### Sequential Locality
- ✅ **Cache advantage**: Reduces data source calls by 70-80%
- ✅ **Prefetching benefit**: Most requests served from cache
- ✅ **Latency hiding**: Background rebalancing doesn't block reads

---

## Architecture Goals

These benchmarks validate:
1. **User request flow isolation** - User-facing latency measured without rebalance contamination (`UserFlowBenchmarks`)
2. **Behavior-driven rebalance analysis** - How storage strategies handle Fixed/Growing/Shrinking span dynamics (`RebalanceFlowBenchmarks`)
3. **Storage strategy tradeoffs** - Snapshot vs CopyOnRead across all workload patterns with measured allocation differences
4. **Cold start characteristics** - Complete initialization cost including first rebalance (`ScenarioBenchmarks`)
5. **Execution queue strategy comparison** - Unbounded vs bounded queue performance under varying burst loads and I/O latencies (`ExecutionStrategyBenchmarks`)
6. **Memory pressure patterns** - Allocations, GC pressure, LOH impact across parameter ranges
7. **Scaling behavior** - Performance characteristics from small (100) to large (10,000) data volumes
8. **Deterministic reproducibility** - Zero-latency `SynchronousDataSource` isolates cache mechanics from I/O variance

---

## Output Files

After running benchmarks, results are generated in two locations:

### Results Directory (Committed to Repository)
```
benchmarks/SlidingWindowCache.Benchmarks/Results/
├── SlidingWindowCache.Benchmarks.Benchmarks.UserFlowBenchmarks-report-github.md
├── SlidingWindowCache.Benchmarks.Benchmarks.RebalanceFlowBenchmarks-report-github.md
├── SlidingWindowCache.Benchmarks.Benchmarks.ScenarioBenchmarks-report-github.md
└── SlidingWindowCache.Benchmarks.Benchmarks.ExecutionStrategyBenchmarks-report-github.md
```

These markdown reports are checked into version control for:
- Performance regression tracking
- Historical comparison
- Documentation of expected performance characteristics

### BenchmarkDotNet Artifacts (Local Only)
```
BenchmarkDotNet.Artifacts/
├── results/
│   ├── *.html (HTML reports)
│   ├── *.md (Markdown reports)
│   └── *.csv (Raw data)
└── logs/
    └── ... (detailed execution logs)
```

These files are generated locally and excluded from version control (`.gitignore`).

---

## CI/CD Integration

These benchmarks can be integrated into CI/CD for:
- **Performance regression detection**
- **Release performance validation**
- **Architectural decision documentation**
- **Historical performance tracking**

Example: Run on every release and commit results to repository.

---

## License

MIT (same as parent project)
