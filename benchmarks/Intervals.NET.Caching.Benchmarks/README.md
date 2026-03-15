# Intervals.NET.Caching Benchmarks

Comprehensive BenchmarkDotNet performance suite for Intervals.NET.Caching, measuring architectural performance characteristics of **all three cache implementations** using **public API only**.

**Methodologically Correct Benchmarks**: This suite follows rigorous benchmark methodology to ensure deterministic, reliable, and interpretable results.

---

## Current Performance Baselines

For current measured performance data, see the committed reports in `Results/`:

### SlidingWindow Cache (SWC)
- **User Request Flow**: [UserFlowBenchmarks-report-github.md](Results/Intervals.NET.Caching.Benchmarks.Benchmarks.UserFlowBenchmarks-report-github.md)
- **Rebalance Mechanics**: [RebalanceFlowBenchmarks-report-github.md](Results/Intervals.NET.Caching.Benchmarks.Benchmarks.RebalanceFlowBenchmarks-report-github.md)
- **End-to-End Scenarios**: [ScenarioBenchmarks-report-github.md](Results/Intervals.NET.Caching.Benchmarks.Benchmarks.ScenarioBenchmarks-report-github.md)
- **Execution Strategy Comparison**: [ExecutionStrategyBenchmarks-report-github.md](Results/Intervals.NET.Caching.Benchmarks.Benchmarks.ExecutionStrategyBenchmarks-report-github.md)

These reports are updated when benchmarks are re-run and committed to track performance over time.

---

## Overview

This benchmark project provides reliable, deterministic performance measurements for **three cache implementations** organized by execution flow:

### Cache Implementations

1. **SlidingWindow Cache (SWC)** — Sequential-access optimized, single contiguous window with geometry-based prefetch
2. **VisitedPlaces Cache (VPC)** — Random-access optimized, non-contiguous segments with eviction and TTL
3. **Layered Cache** — Compositions of SWC and VPC in multi-layer topologies

### Execution Flow Model

Each cache has **two independent cost centers**:

1. **User Request Flow** — Measures latency/cost of user-facing API calls
   - Rebalance/background activity is **NOT** included in measured results
   - Focus: Direct `GetDataAsync` call overhead

2. **Background/Maintenance Flow** — Measures cost of background operations
   - Explicitly waits for stabilization using `WaitForIdleAsync`
   - Focus: Rebalance (SWC), normalization/eviction (VPC), or layer propagation (Layered)

---

## Project Structure

```
benchmarks/Intervals.NET.Caching.Benchmarks/
├── Infrastructure/
│   ├── SynchronousDataSource.cs      # Zero-latency data source
│   ├── SlowDataSource.cs             # Configurable-latency data source
│   ├── VpcCacheHelpers.cs            # VPC factory methods and population helpers
│   └── LayeredCacheHelpers.cs        # Layered topology factory methods
├── SlidingWindow/
│   ├── UserFlowBenchmarks.cs         # 8 methods × 9 params = 72 cases
│   ├── RebalanceFlowBenchmarks.cs    # 1 method × 18 params = 18 cases
│   ├── ScenarioBenchmarks.cs         # 2 methods × 9 params = 18 cases
│   ├── ExecutionStrategyBenchmarks.cs # 2 methods × 9 params = 18 cases
│   └── ConstructionBenchmarks.cs     # 4 methods, no params = 4 cases
├── VisitedPlaces/
│   ├── CacheHitBenchmarks.cs         # 1 method × 32 params = 32 cases
│   ├── CacheMissBenchmarks.cs        # 2 methods × 16 params = 32 cases
│   ├── PartialHitBenchmarks.cs       # 2 methods × ~24 params = ~48 cases
│   ├── ScenarioBenchmarks.cs         # 3 methods × 12 params = 36 cases
│   └── ConstructionBenchmarks.cs     # 4 methods, no params = 4 cases
├── Layered/
│   ├── UserFlowBenchmarks.cs         # 9 methods × 3 params = 27 cases
│   ├── RebalanceBenchmarks.cs        # 3 methods × 2 params = 6 cases
│   ├── ScenarioBenchmarks.cs         # 6 methods × 2 params = 12 cases
│   └── ConstructionBenchmarks.cs     # 3 methods, no params = 3 cases
├── Results/                          # Committed benchmark reports
└── Program.cs
```

**Total: ~17 classes, ~50 methods, ~330 benchmark cases**

---

## Design Principles

### 1. Public API Only
- No internal types, no `InternalsVisibleTo`, no reflection
- Only uses public cache APIs (`IRangeCache`, builders, constructors)

### 2. Deterministic Behavior
- `SynchronousDataSource` with zero-latency, deterministic data generation
- No randomness, no I/O operations
- Fresh cache per iteration via `[IterationSetup]`

### 3. Methodological Rigor
- **No state reuse**: Fresh cache per iteration
- **Explicit background handling**: `WaitForIdleAsync` in setup/cleanup (user flow) or inside benchmark (rebalance/scenario)
- **Clear separation**: Each benchmark measures ONE thing
- **`[MemoryDiagnoser]`** for allocation tracking
- **`[MarkdownExporter]`** for report generation

---

## SlidingWindow Benchmarks

### UserFlowBenchmarks

**Goal**: Measure ONLY user-facing request latency. Background activity excluded.

**Parameters**: `RangeSpan{100,1K,10K}` × `CacheCoefficientSize{1,10,100}` = 9 combinations

| Category   | Methods                                              | Purpose                |
|------------|------------------------------------------------------|------------------------|
| FullHit    | `User_FullHit_Snapshot`, `User_FullHit_CopyOnRead`   | Baseline read cost     |
| PartialHit | Forward/Backward × Snapshot/CopyOnRead               | Partial overlap cost   |
| FullMiss   | `User_FullMiss_Snapshot`, `User_FullMiss_CopyOnRead` | Full cache replacement |

### RebalanceFlowBenchmarks

**Goal**: Measure rebalance mechanics and storage rematerialization cost.

**Parameters**: `Behavior{Fixed,Growing,Shrinking}` × `Strategy{Snapshot,CopyOnRead}` × `BaseSpanSize{100,1K,10K}` = 18 combinations

Single `Rebalance` method: 10 sequential requests, each followed by `WaitForIdleAsync`.

### ScenarioBenchmarks

**Goal**: Cold start performance (end-to-end).

**Parameters**: `RangeSpan{100,1K,10K}` × `CacheCoefficientSize{1,10,100}` = 9 combinations

### ExecutionStrategyBenchmarks

**Goal**: Unbounded vs bounded execution queue under burst patterns.

**Parameters**: `DataSourceLatencyMs{0,50,100}` × `BurstSize{10,100,1000}` = 9 combinations

### ConstructionBenchmarks

**Goal**: Builder pipeline vs raw constructor cost.

4 methods: `Builder_Snapshot`, `Builder_CopyOnRead`, `Constructor_Snapshot`, `Constructor_CopyOnRead`

---

## VisitedPlaces Benchmarks

### CacheHitBenchmarks

**Goal**: Measure read cost when all requested segments are cached.

**Parameters**: `HitSegments{1,10,100,1000}` × `TotalSegments{1K,100K}` × `StorageStrategy{Snapshot,LinkedList}` × `EvictionSelector{Lru,Fifo}` = 32 combinations

### CacheMissBenchmarks

**Goal**: Measure fetch + store cost for uncached ranges, with and without eviction.

**Parameters**: `TotalSegments{10,1K,100K,1M}` × `StorageStrategy` × `AppendBufferSize{1,8}` = 32 combinations

2 methods: `CacheMiss_NoEviction`, `CacheMiss_WithEviction`

### PartialHitBenchmarks

**Goal**: Measure cost when request partially overlaps existing segments.

2 methods:
- `PartialHit_SingleGap`: `IntersectingSegments{1,10,100,1000}` × `TotalSegments{1K,100K}` × `StorageStrategy`
- `PartialHit_MultipleGaps`: `GapCount{1,10,100,1000}` × `TotalSegments{10K,100K}` × `StorageStrategy` × `AppendBufferSize{1,8}`

### ScenarioBenchmarks

**Goal**: End-to-end scenarios with deterministic burst patterns.

**Parameters**: `BurstSize{10,50,100}` × `StorageStrategy` × `SchedulingStrategy{Unbounded,Bounded}` = 12 combinations

3 methods: `Scenario_ColdStart` (all misses), `Scenario_AllHits` (all hits), `Scenario_Churn` (misses at capacity with eviction)

### ConstructionBenchmarks

**Goal**: Builder pipeline vs raw constructor cost.

4 methods: `Builder_Snapshot`, `Builder_LinkedList`, `Constructor_Snapshot`, `Constructor_LinkedList`

---

## Layered Benchmarks

### Topologies

All layered benchmarks cover three topologies:

| Topology      | Description                               | Layers (inner → outer) |
|---------------|-------------------------------------------|------------------------|
| **SwcSwc**    | Homogeneous sliding window stack          | SWC + SWC              |
| **VpcSwc**    | Random-access backed by sequential-access | VPC + SWC              |
| **VpcSwcSwc** | Three-layer deep stack                    | VPC + SWC + SWC        |

Default configuration: SWC layers use `leftCacheSize=2.0`, `rightCacheSize=2.0`, `debounceDelay=Zero`. VPC layers use Snapshot storage, `MaxSegmentCount=1000`, LRU selector.

### UserFlowBenchmarks

**Goal**: User-facing request latency across topologies and interaction patterns.

**Parameters**: `RangeSpan{100,1K,10K}` = 3 combinations

9 methods: 3 topologies × 3 scenarios (FullHit, PartialHit, FullMiss)

### RebalanceBenchmarks

**Goal**: Rebalance/maintenance cost per topology.

**Parameters**: `BaseSpanSize{100,1K}` = 2 combinations

3 methods: one per topology. 10 sequential requests with shift, each followed by `WaitForIdleAsync`.

### ScenarioBenchmarks

**Goal**: End-to-end scenarios per topology.

**Parameters**: `RangeSpan{100,1K}` = 2 combinations

6 methods: 3 topologies × 2 scenarios (ColdStart, SequentialLocality)

### ConstructionBenchmarks

**Goal**: Pure construction cost per topology.

3 methods: `Construction_SwcSwc`, `Construction_VpcSwc`, `Construction_VpcSwcSwc`

---

## Running Benchmarks

### Quick Start

```bash
# Run all benchmarks (WARNING: This will take many hours with full parameterization)
dotnet run -c Release --project benchmarks/Intervals.NET.Caching.Benchmarks

# Run by cache type
dotnet run -c Release --project benchmarks/Intervals.NET.Caching.Benchmarks --filter "*SlidingWindow*"
dotnet run -c Release --project benchmarks/Intervals.NET.Caching.Benchmarks --filter "*VisitedPlaces*"
dotnet run -c Release --project benchmarks/Intervals.NET.Caching.Benchmarks --filter "*Layered*"

# Run specific benchmark class
dotnet run -c Release --project benchmarks/Intervals.NET.Caching.Benchmarks --filter "*UserFlowBenchmarks*"
dotnet run -c Release --project benchmarks/Intervals.NET.Caching.Benchmarks --filter "*CacheHitBenchmarks*"
dotnet run -c Release --project benchmarks/Intervals.NET.Caching.Benchmarks --filter "*ConstructionBenchmarks*"

# Run specific method
dotnet run -c Release --project benchmarks/Intervals.NET.Caching.Benchmarks --filter "*FullHit_SwcSwc*"
```

### Managing Execution Time

With ~330 total benchmark cases, full execution takes many hours. Strategies for faster turnaround:

1. **Run by cache type**: Focus on SWC, VPC, or Layered independently
2. **Run by benchmark class**: Target specific benchmark files
3. **Use `[SimpleJob]` for development**: Add `[SimpleJob(warmupCount: 3, iterationCount: 5)]`
4. **Reduce parameters temporarily**: Comment out larger parameter values

---

## Data Sources

### SynchronousDataSource
Zero-latency synchronous data source for isolating cache mechanics. Returns `Task.FromResult` with deterministic data (position `i` produces value `i`).

### SlowDataSource
Configurable-latency data source for simulating network/IO delay. Used by `ExecutionStrategyBenchmarks`.

---

## Interpreting Results

### Mean Execution Time
- Lower is better
- Compare storage strategies (Snapshot vs CopyOnRead/LinkedList) within same scenario
- Compare topologies within layered benchmarks

### Allocations
- **SWC Snapshot**: Zero on read, large on rebalance
- **SWC CopyOnRead**: Constant on read, incremental on rebalance
- **VPC Snapshot**: Lock-free reads (snapshot + append buffer), array allocations at normalization
- **VPC LinkedList**: Holds lock during read walk, no array allocations

### Memory Diagnostics
- **Allocated**: Total bytes allocated
- **Gen 0/1/2 Collections**: GC pressure indicator
- **LOH**: Large Object Heap allocations (arrays >85KB)

---

## Methodological Guarantees

### No State Drift
Every iteration starts from a clean, deterministic cache state via `[IterationSetup]`.

### Explicit Background Handling
- **User flow benchmarks**: `WaitForIdleAsync` in `[IterationCleanup]`, not in benchmark method
- **Rebalance/scenario benchmarks**: `WaitForIdleAsync` inside benchmark method (measuring complete workflow)

### Clear Separation
Each benchmark measures one architectural characteristic. User flow is separated from background maintenance.

### Isolation
`SynchronousDataSource` isolates cache mechanics from I/O variance. Each benchmark class targets a specific aspect.

---

## Output Files

### Results Directory (Committed to Repository)
```
benchmarks/Intervals.NET.Caching.Benchmarks/Results/
```

Markdown reports checked into version control for performance regression tracking.

### BenchmarkDotNet Artifacts (Local Only)
```
BenchmarkDotNet.Artifacts/
├── results/   (HTML, Markdown, CSV reports)
└── logs/      (detailed execution logs)
```

Generated locally and excluded from version control (`.gitignore`).

---

## License

MIT (same as parent project)
