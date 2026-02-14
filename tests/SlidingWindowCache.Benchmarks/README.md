# SlidingWindowCache Benchmarks

Comprehensive BenchmarkDotNet performance suite for SlidingWindowCache, measuring architectural performance characteristics using **public API only**.

**🎯 Methodologically Correct Benchmarks**: This suite follows rigorous benchmark methodology to ensure deterministic, reliable, and interpretable results.

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

---

## Design Principles

### 1. Public API Only
- ✅ No internal types
- ✅ No reflection
- ✅ Only uses public `WindowCache` API

### 2. Deterministic Behavior
- ✅ `FakeDataSource` with no randomness
- ✅ `SynchronousDataSource` for zero-latency isolation
- ✅ Stable, predictable data generation
- ✅ Configurable simulated latency
- ✅ No I/O operations

### 3. Methodological Rigor
- ✅ **No state reuse**: Fresh cache per iteration via `[IterationSetup]`
- ✅ **Explicit rebalance handling**: `WaitForIdleAsync` in setup/cleanup, NOT in benchmark methods
- ✅ **Clear separation**: Read microbenchmarks vs partial-hit vs scenario-level
- ✅ **Isolation**: Each benchmark measures ONE thing
- ✅ **MemoryDiagnoser** for allocation tracking
- ✅ **MarkdownExporter** for report generation

---

## Benchmark Categories

Benchmarks are organized by **execution flow** to clearly separate user-facing costs from background maintenance costs.

### 📱 User Request Flow Benchmarks

**File**: `UserFlowBenchmarks.cs`

**Goal**: Measure ONLY user-facing request latency. Rebalance/background activity is EXCLUDED from measurements.

**Contract**:
- Benchmark methods measure ONLY `GetDataAsync` cost
- `WaitForIdleAsync` moved to `[IterationCleanup]`
- Fresh cache per iteration
- Deterministic overlap patterns (no randomness)

**Benchmark Methods**:

| Method | Purpose | Range Pattern |
|--------|---------|---------------|
| `User_FullHit_Snapshot` | Baseline: Full cache hit with Snapshot mode | [1100, 1900] ⊂ [1000, 2000] |
| `User_FullHit_CopyOnRead` | Full cache hit with CopyOnRead mode | [1100, 1900] ⊂ [1000, 2000] |
| `User_PartialHit_ForwardShift_Snapshot` | Partial hit moving right (Snapshot) | [1500, 2500] ∩ [1000, 2000] (50% overlap) |
| `User_PartialHit_ForwardShift_CopyOnRead` | Partial hit moving right (CopyOnRead) | [1500, 2500] ∩ [1000, 2000] (50% overlap) |
| `User_PartialHit_BackwardShift_Snapshot` | Partial hit moving left (Snapshot) | [500, 1500] ∩ [1000, 2000] (50% overlap) |
| `User_PartialHit_BackwardShift_CopyOnRead` | Partial hit moving left (CopyOnRead) | [500, 1500] ∩ [1000, 2000] (50% overlap) |
| `User_FullMiss_Snapshot` | Full cache miss (Snapshot) | [5000, 6000] ⊄ [1000, 2000] (no overlap) |
| `User_FullMiss_CopyOnRead` | Full cache miss (CopyOnRead) | [5000, 6000] ⊄ [1000, 2000] (no overlap) |

**Expected Results**:
- Full hit: Snapshot ~0 allocations, CopyOnRead allocates per read
- Partial hit: Both modes serve request immediately, rebalance deferred to cleanup
- Full miss: Request served from data source, rebalance deferred to cleanup

---

### ⚙️ Rebalance/Maintenance Flow Benchmarks

**File**: `RebalanceFlowBenchmarks.cs`

**Goal**: Measure ONLY window maintenance and rebalance operation costs, isolated from I/O latency.

**Contract**:
- Uses `SynchronousDataSource` (zero latency) to isolate cache mechanics
- `WaitForIdleAsync` INSIDE benchmark methods (measuring rebalance)
- Trigger mutation → explicitly wait for stabilization
- Aggressive thresholds ensure rebalancing occurs

**Benchmark Methods**:

| Method | Purpose | Trigger Pattern |
|--------|---------|-----------------|
| `Rebalance_AfterPartialHit_Snapshot` | Baseline: Rebalance cost after partial hit (Snapshot) | [1500, 2500] → triggers rebalance |
| `Rebalance_AfterPartialHit_CopyOnRead` | Rebalance cost after partial hit (CopyOnRead) | [1500, 2500] → triggers rebalance |
| `Rebalance_AfterFullMiss_Snapshot` | Rebalance cost after full miss (Snapshot) | [5000, 6000] → full replacement |
| `Rebalance_AfterFullMiss_CopyOnRead` | Rebalance cost after full miss (CopyOnRead) | [5000, 6000] → full replacement |

**Expected Results**:
- Snapshot: Higher rebalance cost (full array allocation, potential LOH pressure)
- CopyOnRead: Lower rebalance cost (incremental list operations)
- Clear architectural tradeoff: fast reads vs fast maintenance

---

### 🌍 Scenario Benchmarks (End-to-End)

**File**: `ScenarioBenchmarks.cs`

**Goal**: End-to-end scenario testing including cold start and locality patterns. NOT microbenchmarks.

**Contract**:
- Fresh cache per iteration
- Cold start: Measures complete initialization including rebalance
- Locality: Simulates sequential access patterns, cleanup handles stabilization

**Benchmark Methods**:

| Method | Purpose | Pattern |
|--------|---------|---------|
| `ColdStart_Rebalance_Snapshot` | Baseline: Initial cache population (Snapshot) | Empty → [1000, 2000] + WaitForIdleAsync |
| `ColdStart_Rebalance_CopyOnRead` | Initial cache population (CopyOnRead) | Empty → [1000, 2000] + WaitForIdleAsync |
| `User_LocalityScenario_DirectDataSource` | Baseline: No caching (direct data source) | 10 sequential requests |
| `User_LocalityScenario_Snapshot` | Sequential access with Snapshot mode | 10 sequential requests with prefetch |
| `User_LocalityScenario_CopyOnRead` | Sequential access with CopyOnRead mode | 10 sequential requests with prefetch |

**Expected Results**:
- Cold start: Allocation patterns differ between modes
- Locality: 70-80% reduction in data source calls vs direct access

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

## Running Benchmarks

### Run All Benchmarks
```bash
cd tests/SlidingWindowCache.Benchmarks
dotnet run -c Release
```

### Run Specific Benchmark Class
```bash
# User request flow benchmarks
dotnet run -c Release -- --filter *UserFlowBenchmarks*

# Rebalance/maintenance flow benchmarks
dotnet run -c Release -- --filter *RebalanceFlowBenchmarks*

# Scenario benchmarks (cold start + locality)
dotnet run -c Release -- --filter *ScenarioBenchmarks*
```

### Run Specific Method
```bash
# User flow examples
dotnet run -c Release -- --filter *User_FullHit*
dotnet run -c Release -- --filter *User_PartialHit*

# Rebalance flow examples
dotnet run -c Release -- --filter *Rebalance_AfterPartialHit*

# Scenario examples
dotnet run -c Release -- --filter *ColdStart_Rebalance*
dotnet run -c Release -- --filter *User_LocalityScenario*
```

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
- `RebalanceCostBenchmarks` uses `SynchronousDataSource` to isolate cache mechanics from I/O
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

## Deprecated Benchmarks

### ⚠️ Old Benchmark Files (DEPRECATED - REPLACED BY EXECUTION FLOW MODEL)

The following benchmark files have been replaced by the new execution flow model:

**Issues with Old Organization**:
- Mixed user-facing costs with maintenance costs
- Unclear separation between execution flows
- Difficult to interpret which costs are user-visible
- Inconsistent handling of WaitForIdleAsync

**Old Files → New Files Mapping**:

| Old File | Replaced By | New Method Names |
|----------|-------------|------------------|
| `FullHitBenchmarks.cs` | `UserFlowBenchmarks.cs` | `User_FullHit_Snapshot`, `User_FullHit_CopyOnRead` |
| `PartialHitBenchmarks.cs` | `UserFlowBenchmarks.cs` | `User_PartialHit_ForwardShift_*`, `User_PartialHit_BackwardShift_*` |
| `FullMissBenchmarks.cs` | `UserFlowBenchmarks.cs` | `User_FullMiss_Snapshot`, `User_FullMiss_CopyOnRead` |
| `RebalanceCostBenchmarks.cs` | `RebalanceFlowBenchmarks.cs` | `Rebalance_AfterPartialHit_*`, `Rebalance_AfterFullMiss_*` |
| `LocalityAdvantageBenchmarks.cs` | `ScenarioBenchmarks.cs` | `User_LocalityScenario_*` |
| `ColdStartBenchmarks.cs` | `ScenarioBenchmarks.cs` | `ColdStart_Rebalance_*` |

**Action**: The old files can be safely deleted. All functionality is preserved in the new execution flow model with improved clarity and semantic naming.

---

## Architecture Goals

These benchmarks validate:
1. **User request flow isolation** (measured without rebalance contamination in `UserFlowBenchmarks`)
2. **Rebalance cost tradeoffs** (Snapshot vs CopyOnRead, isolated in `RebalanceFlowBenchmarks`)
3. **Sequential locality optimization** (vs direct data source, validated in `ScenarioBenchmarks`)
4. **Memory pressure characteristics** (allocations, GC, LOH across all flows)
5. **Deterministic partial-hit behavior** (`UserFlowBenchmarks` with guaranteed overlap)
6. **Cold start performance** (end-to-end initialization in `ScenarioBenchmarks`)

---

## Output Files

After running benchmarks, find results in:
```
BenchmarkDotNet.Artifacts/
├── results/
│   ├── SlidingWindowCache.Benchmarks.UserFlowBenchmarks-report.html
│   ├── SlidingWindowCache.Benchmarks.UserFlowBenchmarks-report.md
│   ├── SlidingWindowCache.Benchmarks.RebalanceFlowBenchmarks-report.html
│   ├── SlidingWindowCache.Benchmarks.RebalanceFlowBenchmarks-report.md
│   ├── SlidingWindowCache.Benchmarks.ScenarioBenchmarks-report.html
│   └── SlidingWindowCache.Benchmarks.ScenarioBenchmarks-report.md
└── logs/
    └── ... (detailed logs)
```

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
