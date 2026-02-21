# Sliding Window Cache - Complete Component Map

## Document Purpose

This document provides a comprehensive map of all components in the Sliding Window Cache, including:
- Component types (value/reference types)
- Ownership relationships
- Read/write patterns
- Data flow diagrams
- Thread safety model
- Rebalance Decision Model and multi-stage validation pipeline

**Last Updated**: February 16, 2026

---

## Table of Contents

1. [Component Statistics](#component-statistics)
2. [Component Type Legend](#component-type-legend)
3. [Component Hierarchy](#component-hierarchy)
4. [Detailed Component Catalog](#detailed-component-catalog)
5. [Ownership & Data Flow Diagram](#ownership--data-flow-diagram)
6. [Read/Write Patterns](#readwrite-patterns)
7. [Thread Safety Model](#thread-safety-model)
8. [Type Summary Tables](#type-summary-tables)

---

## Component Statistics

**Total Components**: 19 files in the codebase

**By Type**:
- 🟦 **Classes (Reference Types)**: 10
- 🟩 **Structs (Value Types)**: 3
- 🟧 **Interfaces**: 2
- 🟪 **Enums**: 1
- 🟨 **Records**: 2

**By Mutability**:
- **Immutable**: 12 components
- **Mutable**: 5 components (CacheState, IntentManager._currentIntentCts, Storage implementations)

**By Execution Context**:
- **User Thread**: 1 (UserRequestHandler)
- **Background / ThreadPool**: 4 (Scheduler, DecisionEngine, Executor, + async parts of IntentManager)
- **Both Contexts**: 1 (CacheDataFetcher)
- **Neutral**: 13 (configuration, data structures, interfaces)

**Shared Mutable State**:
- **CacheState** (shared by UserRequestHandler, RebalanceExecutor, DecisionEngine)
- No other shared mutable state

**External Dependencies**:
- **IDataSource** (user-provided implementation)
- **TDomain** (from Intervals.NET library)

---

## Component Type Legend

- **🟦 CLASS** = Reference type (heap-allocated, passed by reference)
- **🟩 STRUCT** = Value type (stack-allocated or inline, passed by value)
- **🟧 INTERFACE** = Contract definition
- **🟪 ENUM** = Value type enumeration
- **🟨 RECORD** = Reference type with value semantics

**Ownership Arrows**:
- `owns →` = Component owns/contains the other
- `reads ⊳` = Component reads from the other
- `writes ⊲` = Component writes to the other
- `uses ◇` = Component uses/depends on the other

**Mutability Indicators**:
- ✏️ = Mutable field/property
- 🔒 = Readonly/immutable
- ⚠️ = Mutable shared state (requires coordination)

---

## Component Hierarchy

### Public API Layer

```
🟦 WindowCache<TRange, TData, TDomain>                    [Public Facade]
│
├── owns → 🟦 UserRequestHandler<TRange, TData, TDomain>
│
└── composes (at construction):
    ├── 🟦 CacheState<TRange, TData, TDomain>              ⚠️ Shared Mutable
    ├── 🟦 IntentController<TRange, TData, TDomain>
    │   └── owns → 🟦 RebalanceScheduler<TRange, TData, TDomain>
    ├── 🟦 RebalanceDecisionEngine<TRange, TDomain>
    │   ├── owns → 🟩 ThresholdRebalancePolicy<TRange, TDomain>
    │   └── owns → 🟩 ProportionalRangePlanner<TRange, TDomain>
    ├── 🟦 RebalanceExecutor<TRange, TData, TDomain>
    └── 🟦 CacheDataExtensionService<TRange, TData, TDomain>
        └── uses → 🟧 IDataSource<TRange, TData> (user-provided)
```

---

## Rebalance Decision Model & Validation Pipeline

### Core Conceptual Framework

The system uses a **multi-stage rebalance decision pipeline**, not a cancellation policy. This section clarifies the conceptual model that drives the architecture.

#### Key Distinctions

**Rebalance Validation vs Cancellation:**
- **Rebalance Validation** = Analytical decision mechanism (determines necessity)
- **Cancellation** = Mechanical coordination tool (prevents concurrent executions)
- Cancellation is NOT a decision mechanism; it ensures single-writer architecture

**Intent Semantics:**
- Intent = Access signal ("user accessed this range"), NOT command ("must rebalance")
- Publishing intent does NOT guarantee execution (opportunistic behavior)
- Execution determined by multi-stage validation, not intent existence

### Multi-Stage Validation Pipeline

**Authority**: `RebalanceDecisionEngine` is the sole authority for rebalance necessity determination.

**Pipeline Stages** (all must pass for execution):

1. **Stage 1: Current Cache NoRebalanceRange Validation**
   - Component: `ThresholdRebalancePolicy.ShouldRebalance()`
   - Check: Is RequestedRange contained in NoRebalanceRange(CurrentCacheRange)?
   - Purpose: Fast-path rejection if current cache provides sufficient buffer
   - Result: Skip if contained (no I/O needed)

2. **Stage 2: Pending Desired Cache NoRebalanceRange Validation** (anti-thrashing)
   - Conceptual: Check if pending rebalance will satisfy request
   - Check: Is RequestedRange contained in NoRebalanceRange(PendingDesiredCacheRange)?
   - Purpose: Prevent oscillating cache geometry (thrashing)
   - Result: Skip if pending rebalance covers request
   - Note: May be implemented via cancellation timing optimization

3. **Stage 3: DesiredCacheRange vs CurrentCacheRange Equality**
   - Component: `RebalanceDecisionEngine.Evaluate` (pre-scheduling analytical check)
   - Check: Does computed DesiredCacheRange == CurrentCacheRange?
   - Purpose: Avoid no-op mutations
   - Result: Skip scheduling if cache already in optimal configuration

**Execution Rule**: Rebalance executes ONLY if ALL stages confirm necessity.

### Component Responsibilities in Decision Model

| Component | Role | Decision Authority |
|-----------|------|-------------------|
| **UserRequestHandler** | Read-only; publishes intents with delivered data | No decision authority |
| **IntentController** | Manages intent lifecycle; coordinates cancellation | No decision authority |
| **RebalanceScheduler** | Orchestrates validation pipeline timing | No decision authority |
| **RebalanceDecisionEngine** | **SOLE AUTHORITY** for necessity determination | **Yes - THE authority** |
| **ThresholdRebalancePolicy** | Stage 1 validation (NoRebalanceRange check) | Analytical input |
| **ProportionalRangePlanner** | Computes desired cache geometry | Analytical input |
| **RebalanceExecutor** | Mechanical execution; assumes validated necessity | No decision authority |

### System Stability Principle

The system prioritizes **decision correctness and work avoidance** over aggressive rebalance responsiveness, enabling **smart eventual consistency**.

**Work Avoidance Mechanisms:**
- Stage 1: Avoid rebalance if current cache sufficient (NoRebalanceRange containment)
- Stage 2: Avoid redundant rebalance if pending execution covers request (anti-thrashing)
- Stage 3: Avoid no-op mutations if cache already optimal (Desired==Current)

**Smart Eventual Consistency:**

The cache converges to optimal configuration asynchronously through decision-driven execution:
- User always receives correct data immediately (from cache or IDataSource)
- Decision Engine validates necessity through multi-stage pipeline (THE authority)
- Work avoidance prevents unnecessary operations (thrashing, redundant I/O, oscillation)
- Cache state updates occur in background ONLY when validated as necessary
- System remains stable under rapidly changing access patterns

**Trade-offs:**
- ✅ Prevents thrashing and oscillation (stability over aggressive responsiveness)
- ✅ Reduces redundant I/O operations (efficiency through validation)
- ✅ Improves system stability under rapid access pattern changes (work avoidance)
- ⚠️ May delay cache optimization by debounce period (acceptable for stability gains)

**Related Documentation:**
- See [Concurrency Model - Smart Eventual Consistency](concurrency-model.md#smart-eventual-consistency-model) for detailed consistency semantics
- See [Invariants - Section D](invariants.md#d-rebalance-decision-path-invariants) for multi-stage validation pipeline specification

---

## Detailed Component Catalog

### 1. Configuration & Data Transfer Types

#### 🟨 WindowCacheOptions
```csharp
public record WindowCacheOptions
```

**File**: `src/SlidingWindowCache/Configuration/WindowCacheOptions.cs`

**Type**: Record (reference type with value semantics)

**Properties** (all readonly):
- `double LeftCacheSize` - Coefficient for left cache size (≥0)
- `double RightCacheSize` - Coefficient for right cache size (≥0)
- `double? LeftThreshold` - Left rebalance threshold percentage (optional, ≥0)
- `double? RightThreshold` - Right rebalance threshold percentage (optional, ≥0)
- `TimeSpan DebounceDelay` - Debounce delay for rebalance operations (default: 100ms)
- `UserCacheReadMode ReadMode` - Cache read strategy (Snapshot or CopyOnRead)

**Ownership**: Created by user, passed to WindowCache constructor

**Mutability**: Immutable (init-only properties)

**Lifetime**: Lives as long as cache instance

**Used by**: 
- WindowCache (constructor)
- ThresholdRebalancePolicy (threshold configuration)
- ProportionalRangePlanner (size configuration)

---

#### 🟪 UserCacheReadMode
```csharp
public enum UserCacheReadMode
```

**File**: `src/SlidingWindowCache/UserCacheReadMode.cs`

**Type**: Enum (value type)

**Values**:
- `Snapshot` - Zero-allocation reads, expensive rebalance (uses array)
- `CopyOnRead` - Allocation on reads, cheap rebalance (uses List)

**Ownership**: Part of WindowCacheOptions

**Mutability**: Immutable

**Used by**: 
- WindowCacheOptions
- ICacheStorage implementations (determines storage strategy)

**Trade-offs**:
- **Snapshot**: Fast reads, slow rebalance, LOH pressure for large caches
- **CopyOnRead**: Slow reads, fast rebalance, better memory pressure

---

#### 🟧 IDataSource<TRangeType, TDataType>
```csharp
public interface IDataSource<TRangeType, TDataType>
    where TRangeType : IComparable<TRangeType>
```

**File**: `src/SlidingWindowCache/IDataSource.cs`

**Type**: Interface (contract)

**Methods**:
- `Task<IEnumerable<TDataType>> FetchAsync(Range<TRangeType> range, CancellationToken ct)`
  - Required: Fetch data for a single range
- `Task<IEnumerable<RangeChunk<TRangeType, TDataType>>> FetchAsync(IEnumerable<Range<TRangeType>> ranges, CancellationToken ct)`
  - Optional override: Batch fetch optimization

**Ownership**: User provides implementation

**Used by**: CacheDataExtensionService (calls to fetch external data)

**Operations**: Read-only (fetches external data)

**Characteristics**:
- User-implemented
- May perform I/O (network, disk, database)
- Should respect CancellationToken
- Default batch implementation uses parallel fetch

---

#### 🟨 RangeChunk<TRangeType, TDataType>
```csharp
public record RangeChunk<TRangeType, TDataType>(Range<TRangeType> Range, IEnumerable<TDataType> Data)
    where TRangeType : IComparable<TRangeType>
```

**File**: `src/SlidingWindowCache/DTO/RangeChunk.cs`

**Type**: Record (reference type, immutable)

**Properties**:
- `Range<TRangeType> Range` - The range covered by this chunk
- `IEnumerable<TDataType> Data` - The data for this range

**Ownership**: Created by IDataSource, consumed by CacheDataExtensionService

**Mutability**: Immutable

**Lifetime**: Temporary (method return value)

**Purpose**: Encapsulates data fetched for a particular range (batch fetch result)

---

### 2. Storage Layer

#### 🟧 ICacheStorage<TRange, TData, TDomain>
```csharp
internal interface ICacheStorage<TRange, TData, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
```

**File**: `src/SlidingWindowCache/Storage/ICacheStorage.cs`

**Type**: Interface (internal)

**Properties**:
- `UserCacheReadMode Mode { get; }` - The read mode this strategy implements
- `Range<TRange> Range { get; }` - Current range of cached data

**Methods**:
- `void Rematerialize(RangeData<TRange, TData, TDomain> rangeData)` ⊲ **WRITE**
  - Replaces internal storage with new range data
  - Called during cache initialization and rebalancing
- `ReadOnlyMemory<TData> Read(Range<TRange> range)` ⊳ **READ**
  - Returns data for the specified range
  - Behavior varies by implementation (zero-copy vs. copy)
- `RangeData<TRange, TData, TDomain> ToRangeData()` ⊳ **READ**
  - Converts current state to RangeData representation

**Implementations**:
- `SnapshotReadStorage<TRange, TData, TDomain>`
- `CopyOnReadStorage<TRange, TData, TDomain>`

**Owned by**: CacheState

**Writers**: UserRequestHandler, RebalanceExecutor (via CacheState)

**Readers**: UserRequestHandler, RebalanceExecutor

---

#### 🟦 SnapshotReadStorage<TRange, TData, TDomain>
```csharp
internal sealed class SnapshotReadStorage<TRange, TData, TDomain> : ICacheStorage<TRange, TData, TDomain>
```

**File**: `src/SlidingWindowCache/Storage/SnapshotReadStorage.cs`

**Type**: Class (sealed)

**Fields**:
- `TDomain _domain` (readonly) - Domain for range calculations
- ✏️ `TData[] _storage` - Mutable array holding cached data
- ✏️ `Range<TRange> Range` (property) - Current cache range

**Operations**:
- `Rematerialize()` ⊲ **WRITE**
  - Allocates new array
  - Replaces `_storage` completely
  - Updates `Range`
- `Read()` ⊳ **READ**
  - Returns `ReadOnlyMemory<TData>` view over internal array
  - **Zero allocation** (slice of existing array)
- `ToRangeData()` ⊳ **READ**
  - Creates RangeData from current array

**Characteristics**:
- ✅ Zero-allocation reads (fast)
- ❌ Expensive rebalance (always allocates new array)
- ⚠️ Large arrays may end up on LOH (≥85KB)

**Ownership**: Owned by CacheState (single instance)

**Internal State**: `TData[]` array (mutable, replaced atomically)

**Thread Safety**: Not thread-safe (single consumer model)

**Best for**: Read-heavy workloads, predictable memory patterns

---

#### 🟦 CopyOnReadStorage<TRange, TData, TDomain>
```csharp
internal sealed class CopyOnReadStorage<TRange, TData, TDomain> : ICacheStorage<TRange, TData, TDomain>
```

**File**: `src/SlidingWindowCache/Storage/CopyOnReadStorage.cs`

**Type**: Class (sealed)

**Fields**:
- `TDomain _domain` (readonly) - Domain for range calculations
- ✏️ `List<TData> _activeStorage` - Active storage (immutable during reads)
- ✏️ `List<TData> _stagingBuffer` - Staging buffer (write-only during rematerialization)
- ✏️ `Range<TRange> Range` (property) - Current cache range

**Staging Buffer Pattern**:
- Two internal buffers: active storage + staging buffer
- Active storage never mutated during enumeration
- Staging buffer cleared, filled, then swapped with active
- Buffers may grow but never shrink (capacity reuse)

**Operations**:
- `Rematerialize()` ⊲ **WRITE**
  - Clears staging buffer (preserves capacity)
  - Enumerates range data into staging (single-pass)
  - Atomically swaps staging ↔ active
  - Updates `Range`
- `Read()` ⊳ **READ**
  - Allocates new `TData[]` array
  - Copies from active storage
  - Returns as `ReadOnlyMemory<TData>`
- `ToRangeData()` ⊳ **READ**
  - Returns lazy enumerable over active storage
  - Safe because active storage is immutable during reads

**Characteristics**:
- ✅ Cheap rematerialization (amortized O(1) when capacity sufficient)
- ❌ Expensive reads (allocates + copies)
- ✅ Correct enumeration (staging buffer prevents corruption)
- ✅ No LOH pressure (List growth strategy)
- ✅ Satisfies Invariants A.3.8, A.3.9a, B.11-12

**Ownership**: Owned by CacheState (single instance)

**Internal State**: Two `List<TData>` (swapped atomically)

**Thread Safety**: Not thread-safe (single consumer model)

**Best for**: Rematerialization-heavy workloads, large sliding windows, background cache layers

**See**: [Storage Strategies Guide](storage-strategies.md) for detailed comparison and usage scenarios

---

### 3. Diagnostics Infrastructure

#### 🟧 ICacheDiagnostics
```csharp
public interface ICacheDiagnostics
```

**File**: `src/SlidingWindowCache/Infrastructure/Instrumentation/ICacheDiagnostics.cs`

**Type**: Interface (public)

**Purpose**: Optional observability and instrumentation for cache behavioral events

**Methods** (15 event recording methods):

**User Path Events:**
- `void UserRequestServed()` - Records completed user request
- `void CacheExpanded()` - Records cache expansion (partial hit optimization)
- `void CacheReplaced()` - Records cache replacement (non-intersecting jump)
- `void UserRequestFullCacheHit()` - Records full cache hit (optimal path)
- `void UserRequestPartialCacheHit()` - Records partial cache hit with extension
- `void UserRequestFullCacheMiss()` - Records full cache miss (cold start or jump)

**Data Source Access Events:**
- `void DataSourceFetchSingleRange()` - Records single-range fetch from IDataSource
- `void DataSourceFetchMissingSegments()` - Records multi-segment fetch (gap filling)

**Rebalance Intent Lifecycle Events:**
- `void RebalanceIntentPublished()` - Records intent publication by User Path
- `void RebalanceIntentCancelled()` - Records intent cancellation before/during execution

**Rebalance Execution Lifecycle Events:**
- `void RebalanceExecutionStarted()` - Records execution start after decision approval
- `void RebalanceExecutionCompleted()` - Records successful execution completion
- `void RebalanceExecutionCancelled()` - Records execution cancellation mid-flight

**Rebalance Skip Optimization Events:**
- `void RebalanceSkippedNoRebalanceRange()` - Records skip due to NoRebalanceRange policy
- `void RebalanceSkippedSameRange()` - Records skip due to same-range optimization

**Implementations**:
- `EventCounterCacheDiagnostics` - Default counter-based implementation
- `NoOpDiagnostics` - Zero-cost no-op implementation (default)

**Usage**: Passed to WindowCache constructor as optional parameter

**Ownership**: User creates instance (optional), passed by reference to all actors

**Integration Points**:
- All actors receive diagnostics instance via constructor injection
- Events recorded at key behavioral points throughout cache lifecycle

**Zero-Cost Design**: When not provided, `NoOpDiagnostics` is used with empty methods that JIT optimizes away

**See**: [Diagnostics Guide](diagnostics.md) for comprehensive usage documentation

---

#### 🟦 EventCounterCacheDiagnostics
```csharp
public class EventCounterCacheDiagnostics : ICacheDiagnostics
```

**File**: `src/SlidingWindowCache/Infrastructure/Instrumentation/DefaultCacheDiagnostics.cs`

**Type**: Class (public, thread-safe)

**Purpose**: Default thread-safe implementation using atomic counters

**Fields** (15 private int counters):
- `_userRequestServed`, `_cacheExpanded`, `_cacheReplaced`
- `_userRequestFullCacheHit`, `_userRequestPartialCacheHit`, `_userRequestFullCacheMiss`
- `_dataSourceFetchSingleRange`, `_dataSourceFetchMissingSegments`
- `_rebalanceIntentPublished`, `_rebalanceIntentCancelled`
- `_rebalanceExecutionStarted`, `_rebalanceExecutionCompleted`, `_rebalanceExecutionCancelled`
- `_rebalanceSkippedNoRebalanceRange`, `_rebalanceSkippedSameRange`

**Properties**: 15 read-only properties exposing counter values

**Methods**:
- 15 event recording methods (explicit interface implementation)
  - All use `Interlocked.Increment` for thread-safety
  - ~1-5 nanoseconds per event
- `void Reset()` - Resets all counters to zero (for test isolation)

**Characteristics**:
- ✅ Thread-safe (atomic operations, no locks)
- ✅ Low overhead (~60 bytes memory, <5ns per event)
- ✅ Instance-based (multiple caches can have separate diagnostics)
- ✅ Observable state for testing and monitoring

**Use Cases**:
- Testing and validation (primary use case)
- Development debugging
- Production monitoring (optional)

**Thread Safety**: Thread-safe via `Interlocked.Increment`

**Lifetime**: Typically matches cache lifetime

**See**: [Diagnostics Guide](diagnostics.md) for complete API reference and examples

---

#### 🟦 NoOpDiagnostics
```csharp
public class NoOpDiagnostics : ICacheDiagnostics
```

**File**: `src/SlidingWindowCache/Infrastructure/Instrumentation/NoOpDiagnostics.cs`

**Type**: Class (public, singleton-compatible)

**Purpose**: Zero-overhead no-op implementation for production use

**Methods**: All 15 interface methods implemented as empty method bodies

**Characteristics**:
- ✅ **Absolute zero overhead** - empty methods inlined/eliminated by JIT
- ✅ No state (0 bytes memory)
- ✅ No allocations
- ✅ No performance impact

**Usage**: Automatically used when `cacheDiagnostics` parameter is `null` (default)

**Design Rationale**: 
- Enables diagnostics API without forcing overhead when not needed
- JIT compiler optimizes away empty method calls completely
- Maintains clean API without conditional logic in hot paths

**Thread Safety**: Stateless, inherently thread-safe

**Lifetime**: Can be singleton or per-cache (doesn't matter - no state)

---

### 4. State Management

#### 🟦 CacheState<TRange, TData, TDomain>
```csharp
internal sealed class CacheState<TRange, TData, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
```

**File**: `src/SlidingWindowCache/CacheState.cs`

**Type**: Class (sealed)

**Properties** (all mutable):
- ✏️ `ICacheStorage<TRange, TData, TDomain> Cache { get; }` - The actual cache storage
- ✏️ `Range<TRange>? LastRequested { get; set; }` - Last requested range by user
- ✏️ `Range<TRange>? NoRebalanceRange { get; set; }` - Range within which no rebalancing occurs
- 🔒 `TDomain Domain { get; }` - Domain for range calculations (readonly)

**Ownership**: 
- Created by WindowCache constructor
- **Shared by reference** across multiple components

**Shared with** (read/write):
- **UserRequestHandler** ⊲⊳
  - Reads: `Cache.Range`, `Cache.Read()`, `Cache.ToRangeData()`
  - Writes: `Cache.Rematerialize()`, `LastRequested`
- **RebalanceExecutor** ⊲⊳
  - Reads: `Cache.Range`, `Cache.ToRangeData()`
  - Writes: `Cache.Rematerialize()`, `NoRebalanceRange`
- **RebalanceScheduler** ⊳ (via DecisionEngine)
  - Reads: `NoRebalanceRange`

**Characteristics**:
- ⚠️ **Mutable shared state** (central coordination point)
- ❌ **No internal locking** (single consumer model by design)
- ✅ **Atomic operations** (Rematerialize replaces storage atomically)

**Thread Safety**: 
- Not thread-safe (intentional)
- Coordination via CancellationToken
- User Path cancels rebalance before mutations

**Role**: Central point for cache data and metadata

---

### 5. User Path (Fast Path)

#### 🟦 UserRequestHandler<TRange, TData, TDomain>
```csharp
internal sealed class UserRequestHandler<TRange, TData, TDomain>
```

**File**: `src/SlidingWindowCache/UserPath/UserRequestHandler.cs`

**Type**: Class (sealed)

**Fields** (all readonly):
- `CacheState<TRange, TData, TDomain> _state`
- `CacheDataExtensionService<TRange, TData, TDomain> _cacheExtensionService`
- `IntentController<TRange, TData, TDomain> _intentManager`

**Main Method**:
```csharp
public async ValueTask<ReadOnlyMemory<TData>> HandleRequestAsync(
    Range<TRange> requestedRange,
    CancellationToken cancellationToken)
```

**Operation Flow**:
1. **Cancel pending rebalance** - `_intentManager.CancelPendingRebalance()`
2. **Check cache coverage** - `_state.Cache.Range.Contains(requestedRange)`
3. **Extend if needed** - `_cacheFetcher.ExtendCacheAsync()` + `_state.Cache.Rematerialize()`
4. **Update metadata** - `_state.LastRequested = requestedRange`
5. **Trigger rebalance** - `_intentManager.PublishIntent(requestedRange)` (fire-and-forget)
6. **Return data** - `_state.Cache.Read(requestedRange)`

**Reads from**:
- ⊳ `_state.Cache` (Range, Read, ToRangeData)

**Writes to**:
- ⊲ `_state.Cache` (via Rematerialize - expands to cover requested range)
- ⊲ `_state.LastRequested`

**Uses**:
- ◇ `_cacheFetcher` (to fetch missing data)
- ◇ `_intentManager` (PublishIntent, CancelPendingRebalance)

**Characteristics**:
- ✅ Executes in **User Thread** (synchronous)
- ✅ Always serves user requests (never waits for rebalance)
- ✅ May expand cache to cover requested range
- ✅ Always triggers rebalance intent
- ❌ **Never** trims or normalizes cache
- ❌ **Never** invokes decision logic
- ❌ **Never** blocks on rebalance

**Ownership**: Owned by WindowCache

**Execution Context**: User Thread (synchronous)

**Responsibilities**: Serve user requests fast, trigger rebalance intents

**Invariants Enforced**:
- A.1-0a: Cancels rebalance before cache mutations
- 1: Always serves user requests
- 2: Never waits for rebalance execution
- 3: Sole source of rebalance intent
- 10: Always returns exactly RequestedRange

---

### 5. Rebalance System - Intent Management

#### 🟦 IntentController<TRange, TData, TDomain>
```csharp
internal sealed class IntentController<TRange, TData, TDomain>
```

**File**: `src/SlidingWindowCache/CacheRebalance/IntentController.cs`

**Type**: Class (sealed)

**Role**: Intent Controller (component 1 of 2 in Rebalance Intent Manager actor)

**Fields**:
- `RebalanceScheduler<TRange, TData, TDomain> _scheduler` (readonly)
- `RebalanceDecisionEngine<TRange, TDomain> _decisionEngine` (readonly)
- `CacheState<TRange, TData, TDomain> _state` (readonly reference to shared state)
- ✏️ `PendingRebalance<TRange>? _pendingRebalance` - **Mutable**, tracks current pending rebalance (accessed via Volatile.Read/Write)

**Key Methods**:

**`PublishIntent(Intent<TRange, TData, TDomain> intent)`**:
```csharp
public void PublishIntent(Intent<TRange, TData, TDomain> intent)
{
    // 1. Evaluate necessity via DecisionEngine (THE authority)
    var pendingSnapshot = Volatile.Read(ref _pendingRebalance);
    var decision = _decisionEngine.Evaluate(intent.RequestedRange, _state, pendingSnapshot);
    
    // 2. If validation rejects, skip entirely (work avoidance)
    if (!decision.ShouldSchedule) return;
    
    // 3. Cancel pending via domain object (validation-driven cancellation)
    var oldPending = Volatile.Read(ref _pendingRebalance);
    oldPending?.Cancel();
    
    // 4. Delegate to scheduler, capture returned PendingRebalance
    var newPending = _scheduler.ScheduleRebalance(intent, decision);
    
    // 5. Update snapshot for next Stage 2 validation
    Volatile.Write(ref _pendingRebalance, newPending);
}
```

**`CancelPendingRebalance()`**:
```csharp
public void CancelPendingRebalance()
{
    var pending = Volatile.Read(ref _pendingRebalance);
    if (pending == null) return;
    
    // DDD-style cancellation through domain object
    pending.Cancel();
    Volatile.Write(ref _pendingRebalance, null);
}
```

**`WaitForIdleAsync(TimeSpan? timeout = null)`** (Infrastructure/Testing):
```csharp
public async Task WaitForIdleAsync(TimeSpan? timeout = null)
{
    // Observe-and-stabilize pattern using PendingRebalance.ExecutionTask
    while (stopwatch.Elapsed < maxWait)
    {
        var observedPending = Volatile.Read(ref _pendingRebalance);
        if (observedPending?.ExecutionTask == null) return;
        
        await observedPending.ExecutionTask;
        
        var currentPending = Volatile.Read(ref _pendingRebalance);
        if (ReferenceEquals(observedPending, currentPending)) return;
    }
}
```

**Characteristics**:
- ✅ Owns pending rebalance snapshot (`_pendingRebalance` field)
- ✅ Single-flight enforcement (only one active intent via cancellation)
- ✅ Exposes cancellation to User Path via `CancelPendingRebalance()`
- ✅ **Lock-free implementation** using `Volatile.Read/Write` for safe memory visibility
- ✅ **DDD-style cancellation** - PendingRebalance domain object encapsulates CancellationTokenSource
- ✅ **Thread-safe without locks** - no race conditions, tested under concurrent load
- ⚠️ **Intent does not guarantee execution** - execution is opportunistic
- ❌ **Does NOT**: Timing, scheduling, execution logic, CTS lifecycle management

**Concurrency Model**:
- Uses `Volatile.Read/Write` for safe memory visibility across threads
- No locks, no `lock` statements, no mutexes
- Memory barriers via `Volatile` operations ensure correct ordering
- PendingRebalance domain object owns CancellationTokenSource lifecycle
- Validated by `ConcurrencyStabilityTests` under concurrent load

**Ownership**: 
- Owned by WindowCache
- Composes with RebalanceScheduler

**Execution Context**: 
- **PublishIntent() executes synchronously in User Thread** (atomic intent storage + semaphore signal only; decision evaluation happens in background loop)
- **Only scheduled work (background task) executes in Background ThreadPool**

**State**: 
- `_pendingRebalance` (mutable, nullable, accessed via Volatile.Read/Write)
- Represents snapshot of current pending rebalance for Stage 2 validation

**Responsibilities**: 
- Intent lifecycle management
- Cancellation coordination
- Identity versioning
- Idle synchronization proxy (delegates to RebalanceScheduler for testing infrastructure)

**Invariants Enforced**:
- C.17: At most one active intent
- C.18: Previous intents become obsolete
- C.24: Intent does not guarantee execution

---

#### 🟦 RebalanceScheduler<TRange, TData, TDomain>
```csharp
internal sealed class RebalanceScheduler<TRange, TData, TDomain>
```

**File**: `src/SlidingWindowCache/CacheRebalance/RebalanceScheduler.cs`

**Type**: Class (sealed)

**Role**: Execution Scheduler (component 2 of 2 in Rebalance Intent Manager actor)

**Fields** (all readonly):
- `CacheState<TRange, TData, TDomain> _state`
- `RebalanceDecisionEngine<TRange, TDomain> _decisionEngine`
- `RebalanceExecutor<TRange, TData, TDomain> _executor`
- `TimeSpan _debounceDelay`
- `Task _idleTask` - Tracks latest background Task for deterministic synchronization

**Key Methods**:

**`ScheduleRebalance(RangeData<TRange, TData, TDomain> deliveredData, CancellationToken intentToken)`**:
```csharp
public void ScheduleRebalance(Range<TRange> requestedRange, CancellationToken intentToken)
{
    // Fire-and-forget: background execution with ConfigureAwait(false)
    pendingRebalance.ExecutionTask = RunAsync();
    
    async Task RunAsync()
    {
        try
        {
            await Task.Delay(_debounceDelay, intentToken)
                .ConfigureAwait(false);
            
            // Intent validity check
            if (intentToken.IsCancellationRequested)
                return;
            
            // Execute pipeline
            await ExecutePipelineAsync(requestedRange, intentToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when intent is cancelled
        }
    }
}
```

**`ExecutePipelineAsync(Range<TRange> requestedRange, CancellationToken cancellationToken)`** (private):
```csharp
private async Task ExecutePipelineAsync(...)
{
    // Final cancellation check
    if (cancellationToken.IsCancellationRequested)
        return;
    
    // Step 1: Decision logic
    var decision = _decisionEngine.ShouldExecuteRebalance(
        requestedRange, _state.NoRebalanceRange);
    
    // Step 2: If skip, return early
    if (!decision.ShouldExecute)
        return;
    
    // Step 3: Execute if allowed
    await _executor.ExecuteAsync(decision.DesiredRange!.Value, cancellationToken);
}
```

**`WaitForIdleAsync(TimeSpan? timeout = null)`** (Infrastructure/Testing):
```csharp
public async Task WaitForIdleAsync(TimeSpan? timeout = null)
{
    // Observe-and-stabilize pattern (all builds)
    // 1. Volatile.Read(_idleTask) → observe current Task
    // 2. await observedTask → wait for completion
    // 3. Re-check if _idleTask changed → detect new rebalance
    // 4. Loop until Task reference stabilizes
}
```

**Characteristics**:
- ✅ Executes in **Background / ThreadPool**
- ✅ Handles debounce delay
- ✅ Orchestrates Decision → Execution pipeline
- ✅ Checks intent validity before execution
- ✅ Ensures single-flight through cancellation
- ❌ **Does NOT**: Intent identity, cancellation management

**Ownership**: Owned by IntentController

**Execution Context**: Background / ThreadPool

**State**: Stateless (only readonly fields, plus `_idleTask` field for deterministic synchronization)

**Important Design Note**: RebalanceScheduler is intentionally stateless and does not own intent identity.
All intent lifecycle, superseding, and cancellation semantics are delegated to the Intent Controller (IntentController).
The scheduler receives a CancellationToken for each execution and simply checks its validity.

**Responsibilities**:
- Timing and debounce delay
- Pipeline orchestration (Decision → Execution)
- Validity checking before execution starts
- Task lifecycle tracking for deterministic synchronization (infrastructure/testing)

**Invariants Enforced**:
- C.20: Obsolete intents don't start execution
- C.21: At most one execution active (via cancellation)

---

### 6. Rebalance System - Decision & Policy

#### 🟦 RebalanceDecisionEngine<TRange, TDomain>
```csharp
internal sealed class RebalanceDecisionEngine<TRange, TDomain>
```

**File**: `src/SlidingWindowCache/CacheRebalance/RebalanceDecisionEngine.cs`

**Type**: Class (sealed)

**Role**: Pure Decision Logic - **SOLE AUTHORITY for Rebalance Necessity Determination**

**Fields** (all readonly, value types):
- `ThresholdRebalancePolicy<TRange, TDomain> _policy` (struct, copied)
- `ProportionalRangePlanner<TRange, TDomain> _planner` (struct, copied)

**Key Method**:
```csharp
public RebalanceDecision<TRange> ShouldExecuteRebalance(
    Range<TRange> requestedRange,
    Range<TRange>? noRebalanceRange)
{
    // Stage 1: Current Cache NoRebalanceRange validation (fast path)
    if (noRebalanceRange.HasValue && 
        !_policy.ShouldRebalance(noRebalanceRange.Value, requestedRange))
    {
        return RebalanceDecision<TRange>.Skip();
    }
    
    // Stage 3: Compute DesiredCacheRange and return for execution
    // (Stage 2 may be handled by cancellation timing optimization)
    var desiredRange = _planner.Plan(requestedRange);
    
    return RebalanceDecision<TRange>.Execute(desiredRange);
}
```

**Characteristics**:
- ✅ **Pure function** (no side effects, CPU-only, no I/O)
- ✅ **Deterministic** (same inputs → same outputs)
- ✅ **Stateless** (composes value-type policies)
- ✅ **THE authority** for rebalance necessity determination
- ✅ Invoked only in background
- ❌ Not visible to User Path

**Decision Authority**:
- **This component is the SOLE AUTHORITY** for determining whether rebalance is necessary
- All execution decisions flow from this component's analytical validation
- No other component may override or bypass these decisions
- Executor assumes necessity already validated when invoked

**Uses**:
- ◇ `_policy.ShouldRebalance()` - Stage 1: NoRebalanceRange containment check
- ◇ `_planner.Plan()` - Compute DesiredCacheRange for execution

**Returns**: `RebalanceDecision<TRange>` (struct)

**Ownership**: Owned by WindowCache, used by RebalanceScheduler

**Execution Context**: Background / ThreadPool

**Responsibilities**: 
- **THE authority** for rebalance necessity determination
- Evaluate if rebalance is needed through multi-stage validation
- Stage 1: Check NoRebalanceRange (fast path rejection)
- Stage 3: Compute DesiredCacheRange (execution parameters)
- Produce analytical decision (execute or skip)

**Invariants Enforced**:
- D.25: Decision path is purely analytical (CPU-only, no I/O)
- D.26: Never mutates cache state
- D.27: No rebalance if inside NoRebalanceRange (Stage 1 validation)
- D.28: No rebalance if DesiredCacheRange == CurrentCacheRange (Stage 3 validation)
- D.29: Rebalance executes ONLY if ALL stages confirm necessity

---

#### 🟩 ThresholdRebalancePolicy<TRange, TDomain>
```csharp
internal readonly struct ThresholdRebalancePolicy<TRange, TDomain>
```

**File**: `src/SlidingWindowCache/CacheRebalance/Policy/ThresholdRebalancePolicy.cs`

**Type**: Struct (readonly value type)

**Role**: Cache Geometry Policy - Threshold Rules (component 1 of 2)

**Fields** (all readonly):
- `WindowCacheOptions _options`
- `TDomain _domain`

**Key Methods**:

**`ShouldRebalance(Range<TRange> noRebalanceRange, Range<TRange> requested)`**:
```csharp
public bool ShouldRebalance(Range<TRange> noRebalanceRange, Range<TRange> requested)
    => !noRebalanceRange.Contains(requested);
```

**`GetNoRebalanceRange(Range<TRange> cacheRange)`**:
```csharp
public Range<TRange>? GetNoRebalanceRange(Range<TRange> cacheRange)
    => cacheRange.ExpandByRatio(
        domain: _domain,
        leftRatio: -(_options.LeftThreshold ?? 0),  // Negate to shrink
        rightRatio: -(_options.RightThreshold ?? 0)  // Negate to shrink
    );
```

**Characteristics**:
- ✅ **Value type** (struct, passed by value)
- ✅ **Pure functions** (no state mutation)
- ✅ **Configuration-driven** (uses WindowCacheOptions)
- ✅ **Stateless** (readonly fields)

**Ownership**: Value type, copied into RebalanceDecisionEngine and RebalanceExecutor

**Execution Context**: Background Thread (invoked by RebalanceDecisionEngine within intent processing loop - see IntentController.ProcessIntentsAsync)

**Responsibilities**:
- Compute NoRebalanceRange (shrinks cache by threshold ratios)
- Check if requested range falls outside no-rebalance zone
- Answers: **"When to rebalance"**

**Invariants Enforced**:
- 26: No rebalance if inside NoRebalanceRange
- 33: NoRebalanceRange derived from CurrentCacheRange + config

---

#### 🟩 ProportionalRangePlanner<TRange, TDomain>
```csharp
internal readonly struct ProportionalRangePlanner<TRange, TDomain>
```

**File**: `src/SlidingWindowCache/DesiredRangePlanner/ProportionalRangePlanner.cs`

**Type**: Struct (readonly value type)

**Role**: Cache Geometry Policy - Shape Planning (component 2 of 2)

**Fields** (all readonly):
- `WindowCacheOptions _options`
- `TDomain _domain`

**Key Method**:
```csharp
public Range<TRange> Plan(Range<TRange> requested)
{
    var size = requested.Span(_domain);
    
    var left = size.Value * _options.LeftCacheSize;
    var right = size.Value * _options.RightCacheSize;
    
    return requested.Expand(
        domain: _domain,
        left: (long)left,
        right: (long)right
    );
}
```

**Characteristics**:
- ✅ **Value type** (struct, passed by value)
- ✅ **Pure function** (no state)
- ✅ **Configuration-driven** (uses WindowCacheOptions)
- ✅ **Independent of current cache contents**
- ✅ **Stateless** (readonly fields)

**Ownership**: Value type, copied into RebalanceDecisionEngine

**Execution Context**: Background Thread (invoked by RebalanceDecisionEngine within intent processing loop - see IntentController.ProcessIntentsAsync)

**Responsibilities**:
- Compute DesiredCacheRange (expands requested by left/right coefficients)
- Define canonical cache geometry
- Answers: **"What shape to target"**

**Invariants Enforced**:
- 29: DesiredCacheRange computed from RequestedRange + config
- 30: Independent of current cache contents
- 31: Canonical target cache state
- 32: Sliding window geometry defined by configuration

---

#### 🟩 RebalanceDecision<TRange>
```csharp
internal readonly struct RebalanceDecision<TRange>
    where TRange : IComparable<TRange>
```

**File**: `src/SlidingWindowCache/CacheRebalance/RebalanceDecision.cs`

**Type**: Struct (readonly value type)

**Properties** (all readonly):
- `bool ShouldExecute` - Whether rebalance should proceed
- `Range<TRange>? DesiredRange` - Target cache range (if executing)

**Factory Methods**:
- `static Skip()` → Returns decision to skip rebalance
- `static Execute(Range<TRange> desiredRange)` → Returns decision to execute with target range

**Characteristics**:
- ✅ **Value type** (struct)
- ✅ **Immutable**
- ✅ Represents decision outcome

**Ownership**: Created by RebalanceDecisionEngine, consumed by RebalanceScheduler

**Mutability**: Immutable

**Lifetime**: Temporary (local variable in pipeline)

**Purpose**: Encapsulates decision result (skip or execute with target range)

---

### 7. Rebalance System - Execution

#### 🟦 RebalanceExecutor<TRange, TData, TDomain>
```csharp
internal sealed class RebalanceExecutor<TRange, TData, TDomain>
```

**File**: `src/SlidingWindowCache/Core/Rebalance/Execution/RebalanceExecutor.cs`

**Type**: Class (sealed)

**Role**: Mutating Actor (sole component responsible for cache normalization)

**Fields** (all readonly):
- `CacheState<TRange, TData, TDomain> _state`
- `CacheDataExtensionService<TRange, TData, TDomain> _cacheExtensionService`
- `ICacheDiagnostics _cacheDiagnostics`
- `SemaphoreSlim _executionSemaphore` (initialized to `new SemaphoreSlim(1, 1)`)

**Concurrency Model**:
- Uses `SemaphoreSlim(1, 1)` to serialize execution - ensures only one rebalance can write to cache state at a time
- Semaphore acquired at start of `ExecuteAsync()`, before any I/O operations
- Released in `finally` block to guarantee release even on cancellation or exception
- Works with `CancellationToken` - operations can be cancelled while waiting for semaphore
- WebAssembly-compatible, async, zero User Path blocking

**Key Method**:
```csharp
public async Task ExecuteAsync(
    Intent<TRange, TData, TDomain> intent,
    Range<TRange> desiredRange,
    Range<TRange>? desiredNoRebalanceRange,
    CancellationToken cancellationToken)
{
    // Acquire semaphore to serialize execution
    await _executionSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
    
    try
    {
        // Get delivered data from intent
        var baseRangeData = intent.AvailableRangeData;
        
        // Cancellation check after acquiring semaphore
        cancellationToken.ThrowIfCancellationRequested();
        
        // Phase 1: Extend to cover desired range
        var extended = await _cacheExtensionService.ExtendCacheAsync(
            baseRangeData, desiredRange, cancellationToken).ConfigureAwait(false);
        
        // Cancellation check after I/O
        cancellationToken.ThrowIfCancellationRequested();
        
        // Phase 2: Trim to desired range
        baseRangeData = extended[desiredRange];
        
        // Cancellation check before mutation
        cancellationToken.ThrowIfCancellationRequested();
        
        // Phase 3: Update cache state atomically
        UpdateCacheState(baseRangeData, intent.RequestedRange, desiredNoRebalanceRange);
    }
    finally
    {
        // Always release semaphore
        _executionSemaphore.Release();
    }
}
```

**Reads from**:
- ⊳ `intent.AvailableRangeData` (delivered data from User Path)

**Writes to**:
- ⊲ `_state.Cache` (via Rematerialize - normalizes to DesiredCacheRange)
- ⊲ `_state.LastRequested`
- ⊲ `_state.NoRebalanceRange`

**Uses**:
- ◇ `_cacheExtensionService.ExtendCacheAsync()` (fetch missing data)
- ◇ `_rebalancePolicy.GetNoRebalanceRange()` (compute new threshold zone)

**Characteristics**:
- ✅ Executes in **Background / ThreadPool**
- ✅ **Asynchronous** (performs I/O operations)
- ✅ **Cancellable** (checks token at multiple points)
- ✅ **Sole component** responsible for cache normalization
- ✅ Expands to DesiredCacheRange
- ✅ Trims excess data
- ✅ Updates NoRebalanceRange

**Ownership**: Owned by WindowCache, used by RebalanceScheduler

**Execution Context**: Background / ThreadPool

**Operations**: Mutates cache atomically (expand, trim, update metadata)

**Invariants Enforced**:
- 4: Rebalance is asynchronous
- 34: Supports cancellation at all stages
- 34a: Yields to User Path immediately upon cancellation
- 34b: Cancelled execution doesn't corrupt state
- 35: Only path responsible for cache normalization
- 35a: Mutates only for normalization (expand, trim, recompute NoRebalanceRange)
- 39-41: Upon completion, cache matches DesiredCacheRange

---

#### 🟦 CacheDataExtensionService<TRange, TData, TDomain>
```csharp
internal sealed class CacheDataExtensionService<TRange, TData, TDomain>
```

**File**: `src/SlidingWindowCache/Core/Rebalance/Execution/CacheDataExtensionService.cs`

**Type**: Class (sealed)

**Role**: Data Fetcher (used by both User Path and Rebalance Path)

**Fields** (all readonly):
- `IDataSource<TRange, TData> _dataSource` (user-provided)
- `TDomain _domain`

**Key Method**:
```csharp
public async Task<RangeData<TRange, TData, TDomain>> ExtendCacheAsync(
    RangeData<TRange, TData, TDomain> current,
    Range<TRange> requested,
    CancellationToken ct)
{
    // Step 1: Calculate missing ranges
    var missingRanges = CalculateMissingRanges(current.Range, requested);
    
    // Step 2: Fetch missing data from data source
    var fetchedResults = await _dataSource.FetchAsync(missingRanges, ct);
    
    // Step 3: Union fetched data with current cache
    return UnionAll(current, fetchedResults, _domain);
}
```

**Uses**:
- ◇ `_dataSource.FetchAsync()` - external I/O to fetch data

**Characteristics**:
- ✅ Calls external IDataSource
- ✅ Performs I/O operations
- ✅ Merges data **without trimming**
- ✅ Optimizes partial cache hits (only fetches missing ranges)
- ✅ **Shared by both paths**

**Ownership**: Owned by WindowCache, shared by UserRequestHandler and RebalanceExecutor

**Execution Context**: 
- User Thread (when called by UserRequestHandler)
- Background / ThreadPool (when called by RebalanceExecutor)

**External Dependencies**: IDataSource (user-provided)

**Operations**: 
- Fetches missing data
- Merges with existing cache
- **Never trims**

**Shared by**:
- UserRequestHandler (expand to cover requested range)
- RebalanceExecutor (expand to cover desired range)

---

### 8. Public Facade

#### 🟦 WindowCache<TRange, TData, TDomain>
```csharp
public sealed class WindowCache<TRange, TData, TDomain> : IWindowCache<TRange, TData, TDomain>, IAsyncDisposable
```

**File**: `src/SlidingWindowCache/WindowCache.cs`

**Type**: Class (sealed, public)

**Role**: Public Facade, Composition Root, Resource Manager

**Fields**:
- `UserRequestHandler<TRange, TData, TDomain> _userRequestHandler` (readonly, private)
- `AsyncActivityCounter _activityCounter` (readonly, private)
- `int _disposeState` (mutable, private) - Lock-free disposal state tracking (0=active, 1=disposing, 2=disposed)

**Constructor**: Creates and wires all internal components:
```csharp
public WindowCache(
    IDataSource<TRange, TData> dataSource,
    TDomain domain,
    WindowCacheOptions options,
    ICacheDiagnostics? cacheDiagnostics = null)
{
    var cacheStorage = CreateCacheStorage(domain, options);
    var state = new CacheState<TRange, TData, TDomain>(cacheStorage, domain);
    
    var rebalancePolicy = new ThresholdRebalancePolicy<TRange, TDomain>();
    var rangePlanner = new ProportionalRangePlanner<TRange, TDomain>(options, domain);
    var noRebalancePlanner = new NoRebalanceRangePlanner<TRange, TDomain>(options, domain);
    var cacheFetcher = new CacheDataExtensionService<TRange, TData, TDomain>(dataSource, domain, cacheDiagnostics);
    
    var decisionEngine = new RebalanceDecisionEngine<TRange, TDomain>(rebalancePolicy, rangePlanner, noRebalancePlanner);
    var executor = new RebalanceExecutor<TRange, TData, TDomain>(state, cacheFetcher, cacheDiagnostics);
    
    var executionController = new RebalanceExecutionController<TRange, TData, TDomain>(
        executor, options.DebounceDelay, cacheDiagnostics, _activityCounter);
    
    var intentController = new IntentController<TRange, TData, TDomain>(
        state, decisionEngine, executionController, cacheDiagnostics, _activityCounter);
    
    _userRequestHandler = new UserRequestHandler<TRange, TData, TDomain>(
        state, cacheFetcher, intentController, dataSource, cacheDiagnostics);
}
```

**Public API**:
```csharp
// Primary domain API
public ValueTask<ReadOnlyMemory<TData>> GetDataAsync(
    Range<TRange> requestedRange,
    CancellationToken cancellationToken)
{
    // Throws ObjectDisposedException if disposed
    if (Volatile.Read(ref _disposeState) != 0)
        throw new ObjectDisposedException(...);
    
    return _userRequestHandler.HandleRequestAsync(requestedRange, cancellationToken);
}

// Infrastructure API (synchronization)
public Task WaitForIdleAsync(CancellationToken cancellationToken = default)
{
    // Throws ObjectDisposedException if disposed
    if (Volatile.Read(ref _disposeState) != 0)
        throw new ObjectDisposedException(...);
    
    return _activityCounter.WaitForIdleAsync(cancellationToken);
}

// Resource management API
public async ValueTask DisposeAsync()
{
    // Three-state disposal: 0=active, 1=disposing, 2=disposed
    // Uses Interlocked.CompareExchange for lock-free idempotency
    var previousState = Interlocked.CompareExchange(ref _disposeState, 1, 0);
    
    if (previousState == 0)
    {
        // First disposal - perform cleanup
        await _userRequestHandler.DisposeAsync();
        Volatile.Write(ref _disposeState, 2);
    }
    else if (previousState == 1)
    {
        // Another thread is disposing - spin-wait until complete
        while (Volatile.Read(ref _disposeState) == 1)
            SpinWait.SpinOnce();
    }
    // previousState == 2: already disposed, return immediately
}
```

**Characteristics**:
- ✅ **Pure facade** (no business logic)
- ✅ **Composition root** (wires all components)
- ✅ **Public API** (single entry point)
- ✅ **Resource manager** (owns disposal lifecycle)
- ✅ **Delegates everything** to UserRequestHandler
- ✅ **Idempotent disposal** (safe to call multiple times)

**Ownership**: 
- Owns all internal components
- Created by user
- Should be disposed when no longer needed
- Disposal cascades: WindowCache → UserRequestHandler → IntentController → RebalanceExecutionController

**Execution Context**: Neutral (just delegates)

**Disposal Responsibilities**:
- Mark cache as disposed (blocks new operations)
- Dispose UserRequestHandler (cascades to all internal components)
- Use three-state pattern for concurrent disposal safety
- Ensure exactly-once disposal execution

**Public Operations**:
- `GetDataAsync`: Retrieve data for range (throws ObjectDisposedException if disposed)
- `WaitForIdleAsync`: Wait for background activity to complete (throws ObjectDisposedException if disposed)
- `DisposeAsync`: Release all resources and stop background processing (idempotent)

**Does NOT**:
- Implement business logic
- Directly access cache state
- Perform decision logic
- Force-terminate background tasks (disposal is graceful)

---

## Ownership & Data Flow Diagram

```
┌─────────────────────────────────────────────────────────────────────┐
│  USER (Consumer)                                                    │
└─────────────────────────────────────────────────────────────────────┘
                    │
                    │ GetDataAsync(range, ct)
                    ▼
┌─────────────────────────────────────────────────────────────────────┐
│  WindowCache<TRange, TData, TDomain>  [Public Facade]              │
│  🟦 CLASS (sealed, public)                                          │
│                                                                      │
│  Constructor creates and wires:                                     │
│   ├─ 🟦 CacheState ──────────────────────────┐ (shared mutable)   │
│   ├─ 🟦 UserRequestHandler ──────────────────┼───┐                 │
│   ├─ 🟦 CacheDataExtensionService ───────────┼───┼───┐             │
│   ├─ 🟦 RebalanceIntentManager ──────────────┼───┼───┼───┐         │
│   │   └─ 🟦 RebalanceScheduler ──────────────┼───┼───┼───┼───┐     │
│   ├─ 🟦 RebalanceDecisionEngine ─────────────┼───┼───┼───┼───┼───┐ │
│   │   ├─ 🟩 ThresholdRebalancePolicy         │   │   │   │   │   │ │
│   │   └─ 🟩 ProportionalRangePlanner          │   │   │   │   │   │ │
│   └─ 🟦 RebalanceExecutor ────────────────────┼───┼───┼───┼───┼───┤ │
│                                                │   │   │   │   │   │ │
│  GetDataAsync() → delegates to UserRequestHandler                   │
└────────────────────────────────────────────────┼───┼───┼───┼───┼───┼─┘
                                                 │   │   │   │   │   │
        ═════════════════════════════════════════╪═══╪═══╪═══╪═══╪═══╪═
        USER THREAD                              │   │   │   │   │   │
        ═════════════════════════════════════════╪═══╪═══╪═══╪═══╪═══╪═
                                                 │   │   │   │   │   │
┌────────────────────────────────────────────────▼───┼───┼───┼───┼───┤
│  UserRequestHandler  [Fast Path Actor]             │   │   │   │   │
│  🟦 CLASS (sealed)                                  │   │   │   │   │
│                                                     │   │   │   │   │
│  HandleRequestAsync(range, ct):                    │   │   │   │   │
│   1. _intentManager.CancelPendingRebalance() ──────┼───┼───┼───┼───┤
│   2. Check if cache covers range ──────────────────┼───┤   │   │   │
│   3. If not: _cacheFetcher.ExtendCacheAsync() ─────┼───┼───┤   │   │
│   4. If not: _state.Cache.Rematerialize() ─────────┼───┤   │   │   │
│   5. _state.LastRequested = range ─────────────────┼───┤   │   │   │
│   6. _intentManager.PublishIntent(range) ───────────┼───┼───┼───┼───┤
│   7. return _state.Cache.Read(range) ───────────────┼───┤   │   │   │
└─────────────────────────────────────────────────────┼───┼───┼───┼───┘
                                                      │   │   │   │
        ══════════════════════════════════════════════╪═══╪═══╪═══╪═══
        BACKGROUND / THREADPOOL                       │   │   │   │
        ══════════════════════════════════════════════╪═══╪═══╪═══╪═══
                                                      │   │   │   │
┌─────────────────────────────────────────────────────▼───┼───┼───┼───┐
│  RebalanceIntentManager  [Intent Controller]            │   │   │   │
│  🟦 CLASS (sealed)                                       │   │   │   │
│                                                          │   │   │   │
│  Fields:                                                 │   │   │   │
│   ├─ RebalanceScheduler _scheduler ─────────────────────▼───┼───┤   │
│   └─ CancellationTokenSource? _currentIntentCts ◄───────────┤   │   │
│                                                              │   │   │
│  PublishIntent(range):                                       │   │   │
│   1. Cancel & dispose old _currentIntentCts                  │   │   │
│   2. Create new CancellationTokenSource                      │   │   │
│   3. _scheduler.ScheduleRebalance(range, token) ─────────────┼───┤   │
│                                                              │   │   │
│  CancelPendingRebalance():                                   │   │   │
│   1. Cancel & dispose _currentIntentCts                      │   │   │
└──────────────────────────────────────────────────────────────┼───┼───┘
                                                               │   │
┌──────────────────────────────────────────────────────────────▼───┼───┐
│  RebalanceScheduler  [Execution Scheduler]                       │   │
│  🟦 CLASS (sealed)                                                │   │
│                                                                   │   │
│  ScheduleRebalance(range, intentToken):                          │   │
│   ExecutionTask = RunAsync();                                    │   │
│     async Task RunAsync() {                                      │   │
│       await Task.Delay(...).ConfigureAwait(false);               │   │
│       if (!intentToken.IsCancellationRequested)                  │   │
│         await ExecutePipelineAsync(...).ConfigureAwait(false); ──┼───┤
│     }                                                             │   │
│                                                                   │   │
│  ExecutePipelineAsync(range, ct):                                │   │
│   1. Check cancellation                                          │   │
│   2. decision = _decisionEngine.ShouldExecuteRebalance() ────────┼───┤
│   3. if (decision.ShouldExecute)                                 │   │
│        await _executor.ExecuteAsync(desiredRange, ct); ──────────┼───┤
└───────────────────────────────────────────────────────────────────┼───┘
                                                                    │
┌───────────────────────────────────────────────────────────────────▼──┐
│  RebalanceDecisionEngine  [Pure Decision Logic]                       │
│  🟦 CLASS (sealed)                                                     │
│                                                                        │
│  Fields (value types):                                                │
│   ├─ 🟩 ThresholdRebalancePolicy _policy                              │
│   └─ 🟩 ProportionalRangePlanner _planner                             │
│                                                                        │
│  ShouldExecuteRebalance(requested, noRebalanceRange):                 │
│   1. Check if _policy.ShouldRebalance() → may skip                    │
│   2. desiredRange = _planner.Plan(requested)                          │
│   3. return Execute(desiredRange) or Skip()                           │
│                                                                        │
│  Returns: 🟩 RebalanceDecision<TRange>                                │
└────────────────────────────────────────────────────────────────────────┘

┌────────────────────────────────────────────────────────────────────────┐
│  RebalanceExecutor  [Mutating Actor]                                   │
│  🟦 CLASS (sealed)                                                      │
│                                                                         │
│  ExecuteAsync(desiredRange, ct):                                       │
│   1. rangeData = _state.Cache.ToRangeData() ──────────┐               │
│   2. if (rangeData.Range == desiredRange) return      │               │
│   3. ct.ThrowIfCancellationRequested()                 │               │
│   4. extended = await _cacheFetcher.ExtendCacheAsync() ┼───────────┐  │
│   5. ct.ThrowIfCancellationRequested()                 │           │  │
│   6. rebalanced = extended[desiredRange] (trim)        │           │  │
│   7. ct.ThrowIfCancellationRequested()                 │           │  │
│   8. _state.Cache.Rematerialize(rebalanced) ───────────┼───────┐   │  │
│   9. _state.NoRebalanceRange = ... ────────────────────┼───────┤   │  │
└────────────────────────────────────────────────────────┼───────┼───┼──┘
                                                         │       │   │
┌────────────────────────────────────────────────────────▼───────┼───┼──┐
│  CacheState  [Shared Mutable State]                            │   │  │
│  🟦 CLASS (sealed)  ⚠️ SHARED                                   │   │  │
│                                                                 │   │  │
│  Properties:                                                    │   │  │
│   ├─ ICacheStorage Cache ◄──────────────────────────────────────┼───┤  │
│   ├─ Range? LastRequested ◄─ UserRequestHandler                │   │  │
│   ├─ Range? NoRebalanceRange ◄─ RebalanceExecutor              │   │  │
│   └─ TDomain Domain (readonly)                                  │   │  │
│                                                                 │   │  │
│  Shared by:                                                     │   │  │
│   ├─ UserRequestHandler (R/W)                                   │   │  │
│   ├─ RebalanceExecutor (R/W)                                    │   │  │
│   └─ RebalanceScheduler → DecisionEngine (R)                    │   │  │
└─────────────────────────────────────────────────────────────────┼───┼──┘
                                                                  │   │
┌─────────────────────────────────────────────────────────────────▼───┼──┐
│  ICacheStorage<TRange, TData, TDomain>                              │  │
│  🟧 INTERFACE                                                        │  │
│                                                                      │  │
│  Implementations:                                                   │  │
│   ├─ 🟦 SnapshotReadStorage (TData[] array)                         │  │
│   │   • Read: zero allocation (memory view)                         │  │
│   │   • Write: expensive (allocates new array)                      │  │
│   │                                                                  │  │
│   └─ 🟦 CopyOnReadStorage (List<TData>)                             │  │
│       • Read: allocates (copies to new array)                       │  │
│       • Write: cheap (list operations)                              │  │
│                                                                      │  │
│  Methods:                                                            │  │
│   ├─ void Rematerialize(RangeData) ⊲ WRITE                          │  │
│   ├─ ReadOnlyMemory<TData> Read(Range) ⊳ READ                       │  │
│   └─ RangeData ToRangeData() ⊳ READ                                 │  │
└──────────────────────────────────────────────────────────────────────┼──┘
                                                                       │
┌──────────────────────────────────────────────────────────────────────▼──┐
│  CacheDataExtensionService  [Data Fetcher]                              │
│  🟦 CLASS (sealed)                                                       │
│                                                                          │
│  ExtendCacheAsync(current, requested, ct):                              │
│   1. missingRanges = CalculateMissingRanges()                           │
│   2. fetched = await _dataSource.FetchAsync(missingRanges, ct) ◄────┐  │
│   3. return UnionAll(current, fetched) (merge, no trim)             │  │
│                                                                       │  │
│  Shared by:                                                           │  │
│   ├─ UserRequestHandler (expand to requested)                        │  │
│   └─ RebalanceExecutor (expand to desired)                           │  │
└───────────────────────────────────────────────────────────────────────┼──┘
                                                                        │
┌───────────────────────────────────────────────────────────────────────▼──┐
│  IDataSource<TRangeType, TDataType>  [External Data Source]             │
│  🟧 INTERFACE (user-implemented)                                         │
│                                                                           │
│  Methods:                                                                 │
│   ├─ FetchAsync(Range, CT) → Task<IEnumerable<TData>>                    │
│   └─ FetchAsync(IEnumerable<Range>, CT) → Task<IEnumerable<RangeChunk>> │
│                                                                           │
│  Characteristics:                                                         │
│   ├─ User-provided implementation                                        │
│   ├─ May perform I/O (network, disk, database)                           │
│   ├─ Read-only (fetches data)                                            │
│   └─ Should respect CancellationToken                                    │
└───────────────────────────────────────────────────────────────────────────┘
```

---

## Read/Write Patterns

### CacheState (⚠️ Shared Mutable State)

#### Writers

**RebalanceExecutor** (SOLE WRITER - single-writer architecture):
- ✏️ Writes `Cache` (via `Rematerialize()`)
  - **Purpose**: Normalize cache to DesiredCacheRange using delivered data from intent
  - **When**: Rebalance execution completes (background)
  - **Scope**: Expands, trims, or replaces cache as needed
- ✏️ Writes `LastRequested` property
  - **Purpose**: Record the range that triggered this rebalance
  - **When**: After successful rebalance execution
- ✏️ Writes `NoRebalanceRange` property
  - **Purpose**: Update threshold zone after normalization
  - **When**: After successful rebalance execution

**UserRequestHandler** (READ-ONLY):
- ❌ Does NOT write to CacheState
- ❌ Does NOT call `Cache.Rematerialize()`
- ❌ Does NOT write to `LastRequested` or `NoRebalanceRange`
- ✅ Only reads from cache and IDataSource
- ✅ Publishes intent with delivered data for Rebalance Execution to process

#### Readers

**UserRequestHandler**:
- 👁️ Reads `Cache.Range` - Check if cache covers requested range
- 👁️ Reads `Cache.Read(range)` - Return data to user
- 👁️ Reads `Cache.ToRangeData()` - Get snapshot before extending

**RebalanceScheduler** (via DecisionEngine):
- 👁️ Reads `NoRebalanceRange` - Decision logic (check if rebalance needed)

**RebalanceExecutor**:
- 👁️ Reads `Cache.Range` - Check if already at desired range
- 👁️ Reads `Cache.ToRangeData()` - Get snapshot before normalizing

#### Coordination

**No locks** (by design):
- **Single-writer architecture** - User Path is read-only, only Rebalance Execution writes
- **Single consumer model** - one logical user per cache instance
- Coordination via **validation-driven cancellation** (DecisionEngine confirms necessity, triggers cancellation)
- Rebalance **always checks** cancellation before mutations (yields to new rebalance if needed)

**Thread-Safety Through Architecture:**
- No write-write races (only one writer exists)
- Reference reads are atomic (User Path safely reads while Rebalance may execute)
- `Rematerialize()` performs atomic reference swaps (array/List assignment)
- `internal set` on CacheState properties restricts write access to internal components

**Atomic operations**:
- `Rematerialize()` replaces storage atomically (array/list assignment)
- Property writes are atomic (reference assignment)

---

### CancellationTokenSource (Intent Identity)

#### Owner: IntentController

**Creates**:
- In `PublishIntent()` - new CTS for each intent

**Cancels**:
- In `PublishIntent()` - cancels previous CTS (supersede old intent)
- In `CancelPendingRebalance()` - cancels current CTS (user priority)

**Disposes**:
- Immediately after cancellation (prevent resource leaks)
- Sets to null after disposal (clean state)

#### Users

**RebalanceScheduler**:
- 👁️ Receives token from IntentManager
- 👁️ Checks `IsCancellationRequested` after debounce delay
- 👁️ Passes token to `ExecutePipelineAsync()`
- 👁️ Passes token to `Task.Delay()` (cancellable debounce)

**RebalanceExecutor**:
- 👁️ Receives token from Scheduler
- 👁️ Calls `ThrowIfCancellationRequested()` at three points:
  1. After range equality check, before I/O
  2. After `ExtendCacheAsync()`, before trim
  3. Before `Rematerialize()` (prevent applying obsolete results)

**CacheDataExtensionService**:
- 👁️ Receives token from caller (UserRequestHandler or RebalanceExecutor)
- 👁️ Passes token to `IDataSource.FetchAsync()` (cancellable I/O)

---

## Thread Safety Model

### Concurrency Philosophy

The Sliding Window Cache follows a **single consumer model** as documented in `docs/concurrency-model.md`:

> "A cache instance is **not thread-safe**, is **not designed for concurrent access**, and assumes a single, coherent access pattern. This is an **ideological requirement**, not merely an architectural or technical limitation."

### Key Principles

1. **Single Logical Consumer**
   - One cache instance = one user
   - One access trajectory
   - One temporal sequence of requests

2. **Execution Serialization**
   - ✅ Uses `SemaphoreSlim(1, 1)` in `RebalanceExecutor` for execution serialization
   - ❌ No locks (`lock`, `Monitor`)
   - ❌ No concurrent collections
   - ✅ `CancellationToken` for coordination and signaling
   - ✅ `Interlocked.Exchange` for atomic pending rebalance cancellation

3. **Coordination Mechanism**
   - **Single-Writer Architecture** - User Path is read-only, only Rebalance Execution writes to CacheState
   - **Validation-driven cancellation** - DecisionEngine confirms necessity, then triggers cancellation of pending rebalance
   - **Atomic updates** - `Rematerialize()` performs atomic array/List reference swaps
   - **Execution serialization** - `SemaphoreSlim` ensures only one rebalance writes to cache at a time
   - **Atomic cancellation** - `Interlocked.Exchange` prevents race conditions during pending rebalance cancellation

### Thread Contexts

| Component                         | Thread Context    | Notes                                                     |
|-----------------------------------|-------------------|-----------------------------------------------------------|
| **WindowCache**                   | Neutral           | Just delegates                                            |
| **UserRequestHandler**                     | ⚡ **User Thread** | Synchronous, fast path (user request handling)            |
| **IntentController.PublishIntent()**       | ⚡ **User Thread** | Atomic intent storage + semaphore signal (fire-and-forget)|
| **IntentController.ProcessIntentsAsync()** | 🔄 **Background**  | Intent processing loop, invokes DecisionEngine            |
| **RebalanceDecisionEngine**                | 🔄 **Background**  | Invoked in intent processing loop, CPU-only logic         |
| **ProportionalRangePlanner**               | 🔄 **Background**  | Invoked by DecisionEngine in intent processing loop       |
| **NoRebalanceRangePlanner**                | 🔄 **Background**  | Invoked by DecisionEngine in intent processing loop       |
| **ThresholdRebalancePolicy**               | 🔄 **Background**  | Invoked by DecisionEngine in intent processing loop       |
| **RebalanceExecutionController.PublishExecutionRequest()** | 🔄 **Background** | Invoked by intent loop, enqueues to channel    |
| **RebalanceExecutionController.ProcessExecutionRequestsAsync()** | 🔄 **Background** | Execution loop, invokes Executor       |
| **RebalanceExecutor**             | 🔄 **Background** | ThreadPool, async, I/O                                    |
| **CacheDataExtensionService**     | Both ⚡🔄          | User Thread OR Background                                 |
| **CacheState**                    | Both ⚡🔄          | Shared mutable (no locks!)                                |
| **Storage (Snapshot/CopyOnRead)** | Both ⚡🔄          | Owned by CacheState                                       |

**Critical:** PublishIntent() is a **synchronous operation in user thread** (atomic ops only, no decision logic). Decision logic (DecisionEngine, Planners, Policy) executes in **background intent processing loop**. Rebalance execution (I/O) happens in **separate background execution loop**.

### Concurrency Invariants (from `docs/invariants.md`)

**A.1 Concurrency & Priority**:
- **-1**: User Path and Rebalance Execution **never write to cache concurrently** (User Path is read-only, single-writer architecture)
- **0**: User Path **always has higher priority** than Rebalance Execution (enforced via validation-driven cancellation)
- **0a**: User Request **MAY cancel** ongoing/pending Rebalance **ONLY when DecisionEngine validation confirms new rebalance is necessary**

**C. Rebalance Intent & Temporal Invariants**:
- **17**: At most **one active rebalance intent**
- **18**: Previous intents may become **logically superseded** when validation confirms new rebalance necessary
- **21**: At most **one rebalance execution** active at any time

**Key Correction:** User Path does NOT cancel before its own mutations. User Path is **read-only** - it never mutates cache. Cancellation is triggered by validation confirming necessity, not automatically by user requests.

### How It Works

---

### Threading Model - Complete Flow Diagram

```
┌─────────────────────────────────────────────────────────────────────────┐
│ PHASE 1: USER THREAD (Synchronous - Fast Path)                         │
├─────────────────────────────────────────────────────────────────────────┤
│ Component                  │ Operation                                  │
├────────────────────────────┼────────────────────────────────────────────┤
│ WindowCache.GetDataAsync() │ Entry point (user-facing API)              │
│           ↓                │                                            │
│ UserRequestHandler         │ • Read cache state (read-only)             │
│  .HandleRequestAsync()     │ • Fetch missing data from IDataSource      │
│                            │ • Assemble result data                     │
│                            │ • Call IntentController.PublishIntent()    │
│           ↓                │                                            │
│ IntentController           │ • Interlocked.Exchange(_pendingIntent)     │
│  .PublishIntent()          │ • _intentSignal.Release() (signal)         │
│                            │ • Return immediately (fire-and-forget)     │
│           ↓                │                                            │
│ Return data to user        │ ← USER THREAD BOUNDARY ENDS HERE           │
└─────────────────────────────────────────────────────────────────────────┘
                                      ↓ (semaphore signal)
┌─────────────────────────────────────────────────────────────────────────┐
│ PHASE 2: BACKGROUND THREAD #1 (Intent Processing Loop)                 │
├─────────────────────────────────────────────────────────────────────────┤
│ Component                        │ Operation                            │
├──────────────────────────────────┼──────────────────────────────────────┤
│ IntentController                 │ • await _intentSignal.WaitAsync()    │
│  .ProcessIntentsAsync()          │ • Interlocked.Exchange(_pendingIntent│
│  (infinite background loop)      │ • Read intent atomically             │
│           ↓                      │                                      │
│ RebalanceDecisionEngine          │ Stage 1: Current NoRebalanceRange chk│
│  .Evaluate()                     │ Stage 2: Pending NoRebalanceRange chk│
│     ├─ Stage 3 ────────────────→ │ • ProportionalRangePlanner.Plan()    │
│     │                            │ • NoRebalanceRangePlanner.Plan()     │
│     ├─ ThresholdRebalancePolicy  │ Stage 4: Equality check              │
│     └─ Return Decision           │ Stage 5: Return decision             │
│           ↓                      │                                      │
│ If Skip: continue loop           │ • Diagnostics event                  │
│ If Execute: ↓                    │                                      │
│           ↓                      │                                      │
│ Cancel previous execution        │ • lastExecutionRequest?.Cancel()     │
│           ↓                      │                                      │
│ RebalanceExecutionController     │ • Create ExecutionRequest            │
│  .PublishExecutionRequest()      │ • Channel.Writer.TryWrite(request)   │
└─────────────────────────────────────────────────────────────────────────┘
                                      ↓ (channel message)
┌─────────────────────────────────────────────────────────────────────────┐
│ PHASE 3: BACKGROUND THREAD #2 (Execution Loop)                         │
├─────────────────────────────────────────────────────────────────────────┤
│ Component                        │ Operation                            │
├──────────────────────────────────┼──────────────────────────────────────┤
│ RebalanceExecutionController     │ • await foreach (channel read)       │
│  .ProcessExecutionRequestsAsync()│ • await Task.Delay(debounce)         │
│  (infinite background loop)      │ • Cancellation check                 │
│           ↓                      │                                      │
│ RebalanceExecutor                │ • Extend cache data (I/O)            │
│  .ExecuteAsync()                 │ • Trim to desired range              │
│                                  │ • ┌──────────────────────────┐       │
│                                  │   │ CACHE MUTATION           │       │
│                                  │   │ (SINGLE WRITER)          │       │
│                                  │   │ • Cache.Rematerialize()  │       │
│                                  │   │ • LastRequested = ...    │       │
│                                  │   │ • NoRebalanceRange = ... │       │
│                                  │   └──────────────────────────┘       │
└─────────────────────────────────────────────────────────────────────────┘
```

**Key Threading Boundaries:**

1. **User Thread Boundary**: Ends at `PublishIntent()` return
   - Everything before: Synchronous, blocking user request
   - `PublishIntent()`: Atomic ops only (microseconds), returns immediately

2. **Background Thread #1**: Intent processing loop
   - Single dedicated thread via semaphore wait loop
   - Processes intents sequentially (one at a time)
   - CPU-only decision logic (microseconds)
   - No I/O operations

3. **Background Thread #2**: Execution loop
   - Single dedicated thread via channel reader loop
   - Processes execution requests sequentially (one at a time)
   - I/O operations (milliseconds to seconds)
   - SOLE writer to cache state (single-writer architecture)

**Concurrency Guarantees:**

- ✅ User requests NEVER block on decision evaluation
- ✅ User requests NEVER block on rebalance execution
- ✅ At most ONE decision evaluation active at a time (sequential loop processing)
- ✅ At most ONE rebalance execution active at a time (sequential loop processing)
- ✅ Cache mutations are SERIALIZED (single-writer via sequential processing)
- ✅ No race conditions on cache state (read-only user path + single writer)

---


#### User Request Flow (User Thread - Synchronous until PublishIntent returns)
```
1. UserRequestHandler.HandleRequestAsync() called
2. Read from cache or fetch missing data from IDataSource
3. Assemble data to return to user (NO cache mutation)
4. Return data to user immediately
5. Publish intent with delivered data (SYNCHRONOUS in user thread):
   └─> IntentController.PublishIntent(intent) ⚡ USER THREAD
       ├─> DecisionEngine.Evaluate() ⚡ USER THREAD
       │   └─> Multi-stage validation (CPU-only, side-effect free)
       │       - Stage 1: NoRebalanceRange check
       │       - Stage 2: Pending coverage check
       │       - Stage 3: Desired==Current check
       ├─> If validation rejects: return immediately (work avoidance)
       ├─> If validation confirms: oldPending?.Cancel() ⚡ USER THREAD
       └─> Scheduler.ScheduleRebalance() ⚡ USER THREAD
           ├─> Create PendingRebalance (synchronous)
           └─> Schedule background task ← HERE background starts 🔄
               └─> Debounce delay 🔄 BACKGROUND
               └─> RebalanceExecutor.ExecuteAsync() 🔄 BACKGROUND
                   └─> I/O operations, cache mutations
```

**Key:** Everything up to background task scheduling happens **synchronously in user thread**. 
Only debounce + actual execution happen in background.

**Why This Matters:**
- User request burst → immediate validation in user thread → work avoidance
- No background queue buildup with pending decisions
- Intent thrashing prevented by synchronous validation
- Lightweight CPU-only operations don't block user thread (microseconds)

#### Rebalance Flow (Background Thread)
```
1. RebalanceScheduler.ScheduleRebalance() in Task.Run()
2. await Task.Delay() - cancellable debounce
3. Check IsCancellationRequested - early exit if cancelled
4. DecisionEngine.ShouldExecuteRebalance() - pure logic
5. RebalanceExecutor.ExecuteAsync()
   ├─ ThrowIfCancellationRequested() before I/O
   ├─ await _dataSource.FetchAsync() - cancellable I/O
   ├─ ThrowIfCancellationRequested() after I/O
   ├─ Trim data
   ├─ ThrowIfCancellationRequested() before mutation
   └─ Rematerialize() - atomic cache update
```

### Multi-User Scenarios

**✅ Correct Approach**:
```csharp
// Create one cache instance per user
var userCache1 = new WindowCache<int, Data, IntDomain>(...);
var userCache2 = new WindowCache<int, Data, IntDomain>(...);
```

**❌ Incorrect Approach**:
```csharp
// DO NOT share cache across threads/users
var sharedCache = new WindowCache<int, Data, IntDomain>(...);
// Thread 1: sharedCache.GetDataAsync() - UNSAFE
// Thread 2: sharedCache.GetDataAsync() - UNSAFE
```

### Safety Guarantees

**Provided**:
- ✅ User Path never waits for rebalance
- ✅ User Path always has priority (cancels rebalance)
- ✅ At most one rebalance execution active
- ✅ Obsolete rebalance results are discarded
- ✅ Cache state remains consistent (atomic Rematerialize)

**Not Provided**:
- ❌ Thread-safe concurrent access (by design)
- ❌ Multiple consumers per cache (model violation)
- ❌ Cross-user sliding window arbitration (nonsensical)

---

## Type Summary Tables

### Reference Types (Classes)

| Component                 | Mutability                                   | Shared State | Ownership                | Lifetime       |
|---------------------------|----------------------------------------------|--------------|--------------------------|----------------|
| WindowCache               | Immutable (after ctor)                       | No           | User creates             | App lifetime   |
| UserRequestHandler        | Immutable                                    | No           | WindowCache owns         | Cache lifetime |
| CacheState                | **Mutable**                                  | **Yes** ⚠️   | WindowCache owns, shared | Cache lifetime |
| IntentController          | Mutable (_pendingRebalance)                  | No           | WindowCache owns         | Cache lifetime |
| RebalanceScheduler        | Immutable                                    | No           | IntentController owns    | Cache lifetime |
| RebalanceDecisionEngine   | Immutable                                    | No           | WindowCache owns         | Cache lifetime |
| RebalanceExecutor         | Immutable                                    | No           | WindowCache owns         | Cache lifetime |
| CacheDataExtensionService | Immutable                                    | No           | WindowCache owns         | Cache lifetime |
| SnapshotReadStorage       | **Mutable** (_storage array)                 | No           | CacheState owns          | Cache lifetime |
| CopyOnReadStorage         | **Mutable** (_activeStorage, _stagingBuffer) | No           | CacheState owns          | Cache lifetime |

### Value Types (Structs)

| Component                | Mutability | Ownership              | Lifetime           |
|--------------------------|------------|------------------------|--------------------|
| ThresholdRebalancePolicy | Readonly   | Copied into components | Component lifetime |
| ProportionalRangePlanner | Readonly   | Copied into components | Component lifetime |
| RebalanceDecision        | Readonly   | Local variable         | Method scope       |

### Other Types

| Component          | Type         | Purpose                | Mutability |
|--------------------|--------------|------------------------|------------|
| WindowCacheOptions | 🟨 Record    | Configuration          | Immutable  |
| RangeChunk         | 🟨 Record    | Data transfer          | Immutable  |
| UserCacheReadMode  | 🟪 Enum      | Configuration option   | Immutable  |
| ICacheStorage      | 🟧 Interface | Storage abstraction    | -          |
| IDataSource        | 🟧 Interface | External data contract | -          |

---

## Component Responsibilities Summary

### By Execution Context

**User Thread (Synchronous, Fast)**:
- WindowCache - Facade, delegates
- UserRequestHandler - Serve requests, trigger intents
- IntentController - Intent lifecycle, decision orchestration (synchronous methods)
- RebalanceDecisionEngine - Pure decision logic (CPU-only, synchronous)
- ThresholdRebalancePolicy - Threshold validation (value type, inline)
- ProportionalRangePlanner - Cache geometry planning (value type, inline)

**Background / ThreadPool (Asynchronous, Heavy)**:
- RebalanceScheduler - Timing, debounce, orchestration (execution only, scheduling is sync)
- RebalanceExecutor - Cache normalization, I/O

**Both Contexts**:
- CacheDataExtensionService - Data fetching (called by both paths)
- CacheState - Shared mutable state (accessed by both)

### By Responsibility

**Data Serving**:
- WindowCache (facade)
- UserRequestHandler (implementation)
- CacheState (storage)
- ICacheStorage implementations (actual data)

**Intent Management**:
- IntentController (lifecycle)
- RebalanceScheduler (execution)

**Decision Making**:
- RebalanceDecisionEngine (orchestrator)
- ThresholdRebalancePolicy (thresholds)
- ProportionalRangePlanner (geometry)

**Mutation**:
- UserRequestHandler (expand only)
- RebalanceExecutor (normalize: expand + trim)

**Data Fetching**:
- CacheDataExtensionService (internal)
- IDataSource (external, user-provided)

---

## Architectural Patterns Used

### 1. Facade Pattern
**WindowCache** acts as a facade that hides internal complexity and provides a simple public API.

### 2. Composition Root
**WindowCache** constructor wires all components together in one place.

### 3. Actor Model (Conceptual)
Components follow actor-like patterns with clear responsibilities and message passing (method calls).

### 4. Intent Controller Pattern
**IntentController** manages versioned, cancellable operations through CancellationTokenSource identity.

### 5. Strategy Pattern
**ICacheStorage** with two implementations (SnapshotReadStorage, CopyOnReadStorage) allows runtime selection of storage strategy.

### 6. Value Object Pattern
**ThresholdRebalancePolicy**, **ProportionalRangePlanner**, **RebalanceDecision** are immutable value types with pure behavior.

### 7. Shared Mutable State (Controlled)
**CacheState** is intentionally shared mutable state, coordinated via CancellationToken (not locks).

### 8. Single Consumer Model
Entire architecture assumes one logical consumer, avoiding traditional concurrency primitives.

---

## Related Documentation

- **Architecture Overview**: `docs/actors-to-components-mapping.md`
- **Responsibilities**: `docs/actors-and-responsibilities.md`
- **Invariants**: `docs/invariants.md`
- **Scenarios**: `docs/scenario-model.md`
- **State Machine**: `docs/cache-state-machine.md`
- **Concurrency Model**: `docs/concurrency-model.md`
- **Storage Strategies**: `docs/storage-strategies.md`
- **Cache Hit/Miss Tracking**: `docs/cache-hit-miss-tracking-implementation.md`

---

## Conclusion

The Sliding Window Cache is composed of **19 components** working together to provide fast, cache-aware data access with automatic rebalancing:

- **10 classes** (reference types) provide the runtime behavior
- **3 structs** (value types) provide pure, stateless logic
- **2 interfaces** define contracts for extensibility
- **2 records** provide immutable configuration and data transfer
- **1 enum** defines storage strategy options

The architecture follows a **single consumer model** with **no traditional synchronization primitives**, relying instead on **CancellationToken** for coordination between the fast User Path and the async Rebalance Path.

All components are designed with **clear ownership**, **explicit read/write patterns**, and **well-defined responsibilities**, making the system predictable, testable, and maintainable.
