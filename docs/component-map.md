# Sliding Window Cache - Complete Component Map

> **📖 Cross-References:**  
> - For terminology definitions, see: [Glossary](glossary.md)  
> - For architectural overview, see: [Architecture Model](architecture-model.md)  
> - For detailed implementation mechanics, see: **Source code XML documentation** (each component has extensive inline docs)

## Document Purpose

This document provides a comprehensive catalog of all components in the Sliding Window Cache, focusing on:
- Component types and relationships
- Ownership hierarchy
- Read/write patterns
- Thread safety model

**Note:** Detailed implementation mechanics (method-level behavior, algorithms, memory model details) are documented in source code XML comments. This document focuses on architecture and component interactions.

**Last Updated**: February 16, 2026

---

## Table of Contents

1. [Component Statistics](#component-statistics)
2. [Component Type Legend](#component-type-legend)
3. [Component Hierarchy](#component-hierarchy)
4. [Invariant Implementation Mapping](#invariant-implementation-mapping)
5. [Detailed Component Catalog](#detailed-component-catalog)
6. [Ownership & Data Flow Diagram](#ownership--data-flow-diagram)
7. [Read/Write Patterns](#readwrite-patterns)
8. [Thread Safety Model](#thread-safety-model)
9. [Type Summary Tables](#type-summary-tables)

---

## Component Statistics

**Total Components**: 22 files in the codebase

**By Type**:
- 🟦 **Classes (Reference Types)**: 12
- 🟩 **Structs (Value Types)**: 3
- 🟧 **Interfaces**: 3
- 🟪 **Enums**: 1
- 🟨 **Records**: 2

**By Mutability**:
- **Immutable**: 12 components
- **Mutable**: 5 components (CacheState, IntentManager._currentIntentCts, Storage implementations)

**By Execution Context**:
- **User Thread**: 2 (UserRequestHandler, IntentController.PublishIntent)
- **Background / ThreadPool**: 4 (IntentController.ProcessIntentsAsync loop, DecisionEngine, IRebalanceExecutionController, Executor)
- **Both Contexts**: 1 (CacheDataExtensionService)
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
    │   └── uses → 🟧 IRebalanceExecutionController<TRange, TData, TDomain>
    │       ├── implements → 🟦 TaskBasedRebalanceExecutionController (default)
    │       └── implements → 🟦 ChannelBasedRebalanceExecutionController (optional)
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
| **IntentController** | Manages intent lifecycle; runs background processing loop | No decision authority |
| **IRebalanceExecutionController** | Debounce + execution serialization | No decision authority |
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
- See [Architecture Model - Smart Eventual Consistency](architecture-model.md#smart-eventual-consistency-model) for detailed consistency semantics
- See [Invariants - Section D](invariants.md#d-rebalance-decision-path-invariants) for multi-stage validation pipeline specification

---

## Invariant Implementation Mapping

This section bridges architectural invariants (documented in [invariants.md](invariants.md)) to their concrete implementations in the codebase. Each invariant is enforced through specific component interactions, code patterns, or architectural constraints.

**Purpose**: Provides implementation context for architectural invariants without duplicating specification details. See source code XML documentation for detailed implementation mechanics.

### Single-Writer Architecture

**Invariants**: A.-1, A.7, A.8, A.9, F.36

**Enforcement Mechanism**:
- **Component Design**: Only `RebalanceExecutor` has write access to `CacheState` internal setters
- **Access Control**: User Path components (UserRequestHandler) have read-only references to state
- **Type System**: Internal visibility modifiers prevent external mutations

**Source References**:
- `src/SlidingWindowCache/Core/State/CacheState.cs` - Internal setters restrict write access
- `src/SlidingWindowCache/Core/Rebalance/Execution/RebalanceExecutor.cs` - Exclusive mutation authority
- `src/SlidingWindowCache/Core/UserPath/UserRequestHandler.cs` - Read-only access pattern

### Priority and Cancellation

**Invariants**: A.0, A.0a, C.19, F.35a

**Enforcement Mechanism**:
- **Cancellation Protocol**: CancellationTokenSource coordination between intent publishing and execution
- **Decision-Driven Cancellation**: RebalanceDecisionEngine validates necessity before triggering cancellation
- **Cooperative Cancellation**: Multiple checkpoints in execution pipeline check for cancellation

**Source References**:
- `src/SlidingWindowCache/Core/Intent/IntentManager.cs` - Cancellation token lifecycle management
- `src/SlidingWindowCache/Core/Rebalance/Decision/RebalanceDecisionEngine.cs` - Multi-stage validation gates cancellation
- `src/SlidingWindowCache/Core/Rebalance/Execution/RebalanceExecutor.cs` - Cancellation checkpoints (ThrowIfCancellationRequested)

### Intent Management and Cancellation

**Invariants**: A.0a, C.17, C.20, C.21

**Enforcement Mechanism**:
- **Latest-Wins Semantics**: Interlocked.Exchange replaces previous intent atomically
- **Intent Singularity**: Single-writer architecture for intent state (IntentManager)
- **Early Exit Validation**: Cancellation checked after debounce delay before execution starts

**Source References**:
- `src/SlidingWindowCache/Core/Intent/IntentManager.cs` - Atomic intent replacement via Interlocked.Exchange
- `src/SlidingWindowCache/Core/Intent/IntentController.cs` - Intent processing loop with early exit on cancellation

### UserRequestHandler Responsibilities

**Invariants**: A.3, A.5

**Enforcement Mechanism**:
- **Encapsulation**: Only UserRequestHandler has access to IntentController.PublishIntent interface
- **Minimal Work Pattern**: UserRequestHandler scope limited to data assembly, no normalization logic

**Source References**:
- `src/SlidingWindowCache/Core/UserPath/UserRequestHandler.cs` - Exclusive intent publisher, minimal work implementation
- `src/SlidingWindowCache/Core/Intent/IntentController.cs` - Intent publication interface (internal visibility)

### Async Execution Model

**Invariants**: A.4, G.44

**Enforcement Mechanism**:
- **Fire-and-Forget Pattern**: UserRequestHandler publishes intent and returns immediately
- **Background Task Scheduling**: IRebalanceExecutionController schedules execution via Task.Run or channels
- **Thread Context Separation**: User thread vs ThreadPool thread isolation

**Source References**:
- `src/SlidingWindowCache/Core/Intent/IntentController.cs` - ProcessIntentsAsync loop runs on background thread
- `src/SlidingWindowCache/Infrastructure/Execution/TaskBasedRebalanceExecutionController.cs` - Task.Run scheduling
- `src/SlidingWindowCache/Infrastructure/Execution/ChannelBasedRebalanceExecutionController.cs` - Channel-based background execution

### Atomic Cache Updates

**Invariants**: B.12, B.13

**Enforcement Mechanism**:
- **Staging Buffer Pattern**: Storage strategies build new state before atomic swap
- **Volatile.Write**: Atomic publication of new cache state reference
- **All-or-Nothing Updates**: Rematerialize operation succeeds completely or not at all

**Source References**:
- `src/SlidingWindowCache/Infrastructure/Storage/ArrayCacheStorage.cs` - Array.Copy + Volatile.Write for atomic swap
- `src/SlidingWindowCache/Infrastructure/Storage/ListCacheStorage.cs` - List replacement + Volatile.Write
- `src/SlidingWindowCache/Core/State/CacheState.cs` - Rematerialize method ensures atomicity

### Consistency Under Cancellation

**Invariants**: B.13, B.15, F.35b

**Enforcement Mechanism**:
- **Cancellation Before Mutation**: Final cancellation check before applying cache updates
- **Atomic Application**: Results applied atomically or discarded entirely
- **Exception Safety**: Try-finally blocks ensure cleanup on cancellation

**Source References**:
- `src/SlidingWindowCache/Core/Rebalance/Execution/RebalanceExecutor.cs` - ThrowIfCancellationRequested before Rematerialize call

### Obsolete Result Prevention

**Invariants**: B.16, C.20

**Enforcement Mechanism**:
- **Cancellation Token Identity Tracking**: Each intent has unique CancellationToken
- **Pre-Application Validation**: Execution checks if cancellation requested before applying results
- **Latest-Wins Semantics**: Only results from latest non-cancelled intent are applied

**Source References**:
- `src/SlidingWindowCache/Core/Rebalance/Execution/RebalanceExecutor.cs` - Cancellation validation before cache mutation
- `src/SlidingWindowCache/Core/Intent/IntentManager.cs` - Token lifecycle management

### Intent Singularity

**Invariant**: C.17

**Enforcement Mechanism**:
- **Atomic Replacement**: Interlocked.Exchange ensures exactly one active intent
- **Supersession Pattern**: New intent atomically replaces previous one
- **No Queue Buildup**: At most one pending intent at any time

**Source References**:
- `src/SlidingWindowCache/Core/Intent/IntentManager.cs` - Interlocked.Exchange for atomic intent replacement

### Cancellation Protocol

**Invariant**: C.19

**Enforcement Mechanism**:
- **Cooperative Cancellation**: CancellationToken passed through entire pipeline
- **Multiple Checkpoints**: Checks before I/O, after I/O, before mutations
- **Result Discard**: Results from cancelled operations never applied

**Source References**:
- `src/SlidingWindowCache/Core/Rebalance/Execution/RebalanceExecutor.cs` - Multiple ThrowIfCancellationRequested calls
- `src/SlidingWindowCache/Infrastructure/Services/CacheDataExtensionService.cs` - Cancellation token propagation to IDataSource

### Early Exit Validation

**Invariants**: C.20, D.29

**Enforcement Mechanism**:
- **Post-Debounce Check**: Cancellation verified after debounce delay, before execution
- **Multi-Stage Pipeline**: Each validation stage can exit early without execution
- **Decision Engine Authority**: All stages must pass for execution to proceed

**Source References**:
- `src/SlidingWindowCache/Core/Intent/IntentController.cs` - Cancellation check in ProcessIntentsAsync after debounce
- `src/SlidingWindowCache/Core/Rebalance/Decision/RebalanceDecisionEngine.cs` - Multi-stage early exit logic

### Serial Execution Guarantee

**Invariant**: C.21

**Enforcement Mechanism**:
- **Cancellation Coordination**: Previous execution cancelled before starting new one
- **Single Execution Controller**: Only one IRebalanceExecutionController instance per cache
- **Sequential Processing**: Intent processing loop ensures serial execution

**Source References**:
- `src/SlidingWindowCache/Core/Intent/IntentController.cs` - Sequential intent processing loop
- `src/SlidingWindowCache/Core/Intent/IntentManager.cs` - Cancellation of previous execution before scheduling new

### Intent Data Contract

**Invariant**: C.24e

**Enforcement Mechanism**:
- **Interface Requirement**: PublishIntent method signature requires deliveredData parameter
- **Single Materialization**: UserRequestHandler materializes data once, passes to both user and intent
- **Type Safety**: Compiler enforces data presence

**Source References**:
- `src/SlidingWindowCache/Core/Intent/IntentController.cs` - PublishIntent(requestedRange, deliveredData) signature
- `src/SlidingWindowCache/Core/UserPath/UserRequestHandler.cs` - Single data materialization shared between paths

### Pure Decision Logic

**Invariants**: D.25, D.26

**Enforcement Mechanism**:
- **Stateless Design**: RebalanceDecisionEngine has no mutable fields
- **Value-Type Policies**: Decision policies are structs with no side effects
- **No I/O**: Decision path never calls IDataSource or modifies state
- **Functional Architecture**: Pure function: (state, intent, config) → decision

**Source References**:
- `src/SlidingWindowCache/Core/Rebalance/Decision/RebalanceDecisionEngine.cs` - Pure evaluation logic
- `src/SlidingWindowCache/Core/Planning/ThresholdRebalancePolicy.cs` - Stateless struct
- `src/SlidingWindowCache/Core/Planning/ProportionalRangePlanner.cs` - Stateless struct

### Decision-Execution Separation

**Invariant**: D.26

**Enforcement Mechanism**:
- **Component Boundaries**: Decision components have no references to mutable state setters
- **Read-Only Access**: Decision Engine reads CacheState but cannot modify it
- **Interface Segregation**: Decision and Execution interfaces are distinct

**Source References**:
- `src/SlidingWindowCache/Core/Rebalance/Decision/RebalanceDecisionEngine.cs` - Read-only state access
- `src/SlidingWindowCache/Core/Rebalance/Execution/RebalanceExecutor.cs` - Exclusive write access

### Multi-Stage Decision Pipeline

**Invariant**: D.29

**Enforcement Mechanism**:
- **Sequential Validation**: Five-stage pipeline with early exits
- **Stage 1**: Current NoRebalanceRange containment check (fast path)
- **Stage 2**: Pending NoRebalanceRange validation (thrashing prevention)
- **Stage 3**: DesiredCacheRange computation
- **Stage 4**: Equality check (DesiredCacheRange == CurrentCacheRange)
- **Stage 5**: Execution scheduling (only if all stages pass)

**Source References**:
- `src/SlidingWindowCache/Core/Rebalance/Decision/RebalanceDecisionEngine.cs` - Complete pipeline implementation with stage-by-stage validation

### Desired Range Computation

**Invariants**: E.30, E.31

**Enforcement Mechanism**:
- **Pure Function**: ProportionalRangePlanner.CalculateDesiredRange(requestedRange, config) → desiredRange
- **No State Access**: Range planner never reads CurrentCacheRange
- **Deterministic**: Same inputs always produce same output

**Source References**:
- `src/SlidingWindowCache/Core/Planning/ProportionalRangePlanner.cs` - Pure range calculation logic

### NoRebalanceRange Computation

**Invariant**: E.34

**Enforcement Mechanism**:
- **Pure Function**: ThresholdRebalancePolicy.GetNoRebalanceRange(currentCacheRange, thresholds) → noRebalanceRange
- **Range Shrinking**: Applies threshold percentages to current range boundaries
- **Configuration-Driven**: Uses WindowCacheOptions threshold values

**Source References**:
- `src/SlidingWindowCache/Core/Planning/ThresholdRebalancePolicy.cs` - NoRebalanceRange computation

### Cancellation Checkpoints

**Invariants**: F.35, F.35a

**Enforcement Mechanism**:
- **Before I/O**: ThrowIfCancellationRequested before calling IDataSource.FetchAsync
- **After I/O**: ThrowIfCancellationRequested after data fetching completes
- **Before Mutations**: ThrowIfCancellationRequested before Rematerialize call
- **Cooperative Pattern**: OperationCanceledException propagates to cleanup handlers

**Source References**:
- `src/SlidingWindowCache/Core/Rebalance/Execution/RebalanceExecutor.cs` - Multiple checkpoint locations (see XML comments for exact line references)

### Cache Normalization Operations

**Invariant**: F.37

**Enforcement Mechanism**:
- **Rematerialize Method**: CacheState.Rematerialize accepts arbitrary range and data
- **Full Replacement**: Can replace entire cache contents in single operation
- **Storage Abstraction**: ICacheStorage enables different normalization strategies

**Source References**:
- `src/SlidingWindowCache/Core/State/CacheState.cs` - Rematerialize method
- `src/SlidingWindowCache/Infrastructure/Storage/` - Storage strategy implementations

### Incremental Data Fetching

**Invariant**: F.38

**Enforcement Mechanism**:
- **Gap Analysis**: CacheDataExtensionService.ExtendCacheDataAsync computes missing ranges
- **Range Subtraction**: Uses range algebra to identify gaps (DesiredRange \ CachedRange)
- **Batch Fetching**: Fetches only missing subranges via IDataSource

**Source References**:
- `src/SlidingWindowCache/Infrastructure/Services/CacheDataExtensionService.cs` - ExtendCacheDataAsync implementation with range gap logic

### Data Preservation During Expansion

**Invariant**: F.39

**Enforcement Mechanism**:
- **Union Operation**: New data merged with existing data using range union
- **Storage Enumeration**: Existing data enumerated and preserved during rematerialization
- **No Overwrite**: New data only fills gaps, doesn't replace existing

**Source References**:
- `src/SlidingWindowCache/Infrastructure/Services/CacheDataExtensionService.cs` - Union logic in ExtendCacheDataAsync
- `src/SlidingWindowCache/Infrastructure/Storage/` - Storage strategies preserve existing data during enumeration

### I/O Isolation

**Invariant**: G.45

**Enforcement Mechanism**:
- **User Path Returns Early**: UserRequestHandler completes before any IDataSource.FetchAsync calls
- **Background I/O**: All IDataSource interactions happen in RebalanceExecutor on background thread
- **Fire-and-Forget**: Intent published without awaiting I/O completion

**Source References**:
- `src/SlidingWindowCache/Core/UserPath/UserRequestHandler.cs` - No IDataSource calls in user path
- `src/SlidingWindowCache/Core/Rebalance/Execution/RebalanceExecutor.cs` - IDataSource calls only in background execution

### Activity Counter Ordering

**Invariant**: H.47

**Enforcement Mechanism**:
- **Increment-Before-Publish**: Activity counter incremented BEFORE semaphore signal, channel write, or volatile write
- **Ordering Discipline**: All publication sites follow strict ordering pattern
- **Documentation**: XML comments verify ordering at each publication site

**Source References**:
- `src/SlidingWindowCache/Core/Intent/IntentController.cs` - Increment before semaphore.Release
- `src/SlidingWindowCache/Infrastructure/Execution/` - Increment before channel.Writer.WriteAsync or Task.Run

### Activity Counter Cleanup

**Invariant**: H.48

**Enforcement Mechanism**:
- **Finally Blocks**: Decrement in finally blocks ensures unconditional execution
- **Exception Safety**: Decrement occurs regardless of success, failure, or cancellation
- **Catch Blocks**: Manual decrement in catch blocks for pre-execution failures

**Source References**:
- `src/SlidingWindowCache/Core/Intent/IntentController.cs` - Finally block in ProcessIntentsAsync loop
- `src/SlidingWindowCache/Infrastructure/Execution/` - Finally blocks in execution controllers

---

## Detailed Component Catalog

### 1. Configuration & Data Transfer Types

#### 🟨 WindowCacheOptions
```csharp
public record WindowCacheOptions
```

**File**: `src/SlidingWindowCache/Configuration/WindowCacheOptions.cs`

**Type**: Record (reference type with value semantics)

**Configuration Aspects**:
- Cache size coefficients for left and right windows
- Rebalance threshold percentages (optional)
- Debounce delay for rebalance timing
- Cache read strategy selection (see UserCacheReadMode)

> **See**: `src/SlidingWindowCache/Public/Configuration/WindowCacheOptions.cs` for property details.

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

**File**: `src/SlidingWindowCache/Public/Configuration/UserCacheReadMode.cs`

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

**Contract**:
- Single range fetch (required)
- Batch range fetch (optional, with default parallel implementation)
- CancellationToken support for cooperative cancellation

> **See**: `src/SlidingWindowCache/IDataSource.cs` for method signatures.

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

**Storage Strategy**: Array-based with atomic replacement

**Operations**:
- **Rematerialize**: Allocates new array, replaces storage completely
- **Read**: Returns zero-allocation view over internal array (ReadOnlyMemory)
- **ToRangeData**: Creates snapshot from current array

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

**Storage Strategy**: Dual-buffer pattern (active storage + staging buffer)

**Staging Buffer Pattern**:
- Active storage: Never mutated during enumeration (immutable reads)
- Staging buffer: Used for building new state during rematerialization
- Swap mechanism: Staging becomes active after rematerialization completes
- Capacity reuse: Buffers grow but never shrink (amortized performance)

**Operations**:
- **Rematerialize**: Clears staging, fills with new data, swaps with active, updates range
- **Read**: Allocates and copies from active storage (returns ReadOnlyMemory)
- **ToRangeData**: Returns lazy enumerable over active storage

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

**File**: `src/SlidingWindowCache/Infrastructure/Instrumentation/EventCounterCacheDiagnostics.cs`

**Type**: Class (public, thread-safe)

**Purpose**: Default thread-safe implementation using atomic counters

**Fields** (18 private int counters):
- `_userRequestServed`, `_cacheExpanded`, `_cacheReplaced`
- `_userRequestFullCacheHit`, `_userRequestPartialCacheHit`, `_userRequestFullCacheMiss`
- `_dataSourceFetchSingleRange`, `_dataSourceFetchMissingSegments`
- `_rebalanceIntentPublished`, `_rebalanceIntentCancelled`
- `_rebalanceExecutionStarted`, `_rebalanceExecutionCompleted`, `_rebalanceExecutionCancelled`
- `_rebalanceSkippedCurrentNoRebalanceRange`, `_rebalanceSkippedPendingNoRebalanceRange`, `_rebalanceSkippedSameRange`
- `_rebalanceScheduled`
- `_rebalanceExecutionFailed`

**Properties**: 18 read-only properties exposing counter values

**Methods**:
- 18 event recording methods (explicit interface implementation)
- Thread-safe atomic counter updates
- `void Reset()` - Resets all counters (for test isolation)

**Characteristics**:
- ✅ Thread-safe (atomic operations, no locks)
- ✅ Low overhead (~72 bytes memory, <5ns per event)
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

**Methods**: All 18 interface methods implemented as empty method bodies

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

**File**: `src/SlidingWindowCache/Core/State/CacheState.cs`

**Type**: Class (sealed)

**State Components**:
- Cache storage instance (ICacheStorage implementation)
- Last requested range (tracks user's most recent request)
- No-rebalance range (stable region where rebalancing is suppressed)
- Domain instance (for range calculations)

**Ownership**: 
- Created by WindowCache constructor
- **Shared by reference** across multiple components

**Shared with** (read/write):
- **UserRequestHandler** ⊳ (READ-ONLY)
  - Reads: `Cache.Range`, `Cache.Read()`, `Cache.ToRangeData()`, `LastRequested`
  - ❌ Does NOT write to CacheState
- **RebalanceExecutor** ⊲⊳ (SOLE WRITER)
  - Reads: `Cache.Range`, `Cache.ToRangeData()`
  - Writes: `Cache.Rematerialize()`, `NoRebalanceRange`, `LastRequested`
- **RebalanceDecisionEngine** ⊳ (via IntentController.ProcessIntentsAsync)
  - Reads: `NoRebalanceRange`, `Cache.Range`

**Characteristics**:
- ⚠️ **Mutable shared state** (central coordination point)
- ❌ **No internal locking** (single-writer architecture by design)
- ✅ **Atomic operations** (Rematerialize replaces storage atomically)

**Thread Safety**: 
- Single-writer architecture: only RebalanceExecutor writes to CacheState
- User Path is strictly read-only — no coordination mechanism needed for reads
- Write-write races prevented by single-writer invariant (not by locks)

**Role**: Central point for cache data and metadata

---

### 5. User Path (Fast Path)

#### 🟦 UserRequestHandler<TRange, TData, TDomain>
```csharp
internal sealed class UserRequestHandler<TRange, TData, TDomain>
```

**File**: `src/SlidingWindowCache/Core/UserPath/UserRequestHandler.cs`

**Type**: Class (sealed)

**Dependencies**:
- CacheState (shared state, read-only access)
- CacheDataExtensionService (data fetching)
- IntentController (intent publishing)
- IDataSource (external data access)
- ICacheDiagnostics (instrumentation)

**Main Method**:
```csharp
public async ValueTask<ReadOnlyMemory<TData>> HandleRequestAsync(
    Range<TRange> requestedRange,
    CancellationToken cancellationToken)
```

**Operation Flow**:
1. **Check cold start** - `_state.LastRequested.HasValue`
2. **Serve from cache or data source** - varies by scenario (cold start / full hit / partial hit / full miss)
3. **Publish rebalance intent** - `_intentController.PublishIntent(intent)` with assembled data (fire-and-forget)
4. **Return data** - return assembled `ReadOnlyMemory<TData>`

**Reads from**:
- ⊳ `_state.Cache` (Range, Read, ToRangeData)
- ⊳ `_state.LastRequested` (cold-start detection)
- ⊳ `_state.Domain`

**Writes to**:
- ❌ Does NOT write to CacheState (read-only with respect to cache state)

**Uses**:
- ◇ `_cacheExtensionService` (to fetch missing data on partial/full miss)
- ◇ `_dataSource` (for cold start and full miss scenarios)
- ◇ `_intentController.PublishIntent()` (fire-and-forget, triggers background rebalance)

**Characteristics**:
- ✅ Executes in **User Thread**
- ✅ Always serves user requests (never waits for rebalance)
- ✅ **READ-ONLY with respect to CacheState** (never writes Cache, LastRequested, or NoRebalanceRange)
- ✅ Always triggers rebalance intent after serving
- ❌ **Never** trims or normalizes cache
- ❌ **Never** invokes decision logic
- ❌ **Never** blocks on rebalance
- ❌ **Never** calls `Cache.Rematerialize()`

**Ownership**: Owned by WindowCache

**Execution Context**: User Thread

**Responsibilities**: Serve user requests fast, trigger rebalance intents

**Invariants Enforced**:
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

**File**: `src/SlidingWindowCache/Core/Rebalance/Intent/IntentController.cs`

**Type**: Class (sealed)

**Role**: Intent Controller — manages intent lifecycle and background intent processing loop

**Dependencies**:
- Execution controller (rebalance execution serialization)
- Decision engine (rebalance decision logic)
- CacheState (shared state reference)
- AsyncActivityCounter (idle tracking)
- ICacheDiagnostics (instrumentation)

**Internal State**:
- Pending intent (latest unprocessed intent from user thread)
- Intent signal (synchronization primitive for processing loop)
- Background processing loop task
- Loop cancellation token

**Key Methods**:

**PublishIntent** (executes in User Thread):
- Atomically replaces pending intent (latest wins semantics)
- Increments activity counter
- Signals processing loop to wake up
- Records diagnostic event
- Returns immediately (fire-and-forget)
    // Returns immediately — decision happens in background loop
}
```

**ProcessIntentsAsync** (background processing loop):
- Evaluates DecisionEngine for each intent
- Cancels previous execution if needed
- Enqueues new execution via execution controller

**DisposeAsync**:
- Marks as disposed (idempotent)
- Cancels background loop
- Awaits processing loop completion
- Disposes execution controller and synchronization primitives

**Characteristics**:
- ✅ PublishIntent is minimal — atomic intent store + semaphore signal only
- ✅ Decision evaluation happens in background loop (NOT in user thread)
- ✅ "Latest intent wins" — rapid bursts naturally collapse
- ✅ Single-flight enforcement through cancellation
- ⚠️ **Intent does not guarantee execution** — execution is opportunistic
- ❌ **Does NOT**: Perform debounce delay, execute cache mutations

**Concurrency Model**:
- User thread writes intent atomically (no locks)
- Background loop reads intent atomically (also clears it)
- Semaphore prevents CPU spinning in the background loop
- AsyncActivityCounter tracks active operations for WaitForIdleAsync

**Ownership**: 
- Owned by UserRequestHandler (via WindowCache)
- Composes with `IRebalanceExecutionController`

**Execution Context**: 
- **`PublishIntent()` executes in User Thread** (minimal: atomic store + semaphore signal)
- **`ProcessIntentsAsync()` executes in Background Thread** (decision, cancellation, execution enqueue)

**State**: 
- Pending intent (mutable, nullable, written by user thread, cleared by background loop)

**Responsibilities**: 
- Intent lifecycle management
- Burst resistance (latest-intent-wins)
- Background loop orchestration (decision → cancel → enqueue)
- Idle synchronization (delegates to `AsyncActivityCounter`)

**Invariants Enforced**:
- C.17: At most one active intent (latest wins)
- C.18: Previous intents become obsolete
- C.24: Intent does not guarantee execution

---

---

#### IntentController — ProcessIntentsAsync (background loop)

The `RebalanceScheduler` class described in older documentation **does not exist**. The scheduling, debounce, and pipeline orchestration responsibilities are distributed between `IntentController.ProcessIntentsAsync` (decision + cancellation) and `IRebalanceExecutionController` implementations (debounce + execution).

See `IRebalanceExecutionController`, `TaskBasedRebalanceExecutionController`, and `ChannelBasedRebalanceExecutionController` in Section 7 for the execution side.

**ProcessIntentsAsync** (private background loop inside IntentController):

**Loop Structure**:
1. Wait on semaphore (blocks without CPU spinning)
2. Atomically read and clear pending intent (latest wins)
3. Evaluate DecisionEngine (CPU-only, lightweight, 5-stage validation)
4. Record decision reason; skip if decision says no rebalance needed
5. Cancel previous execution if new rebalance is needed
6. Enqueue execution request to execution controller

> **See**: `src/SlidingWindowCache/Core/Rebalance/Intent/IntentController.cs` for implementation details.

**Characteristics**:
- ✅ Runs in **Background Thread** (single dedicated loop task)
- ✅ Handles burst resistance via "latest intent wins" semantics
- ✅ Decision evaluation happens here (NOT in user thread)
- ✅ Cancels previous execution before enqueuing new one
- ✅ Semaphore prevents CPU spinning
- ❌ Does NOT perform debounce (handled by IRebalanceExecutionController implementations)

**Execution Context**: Background / ThreadPool (loop task started in constructor)

**Responsibilities**:
- Wait for intent signals
- Evaluate DecisionEngine (5-stage validation)
- Cancel previous execution if new rebalance needed
- Enqueue execution requests via `IRebalanceExecutionController`
- Signal idle state after each intent processed

**Invariants Enforced**:
- C.20: Obsolete intents don't start new executions (latest wins + cancellation)
- C.21: At most one active rebalance scheduled at a time (cancellation before enqueue)

---

### 6. Rebalance System - Decision & Policy

#### 🟦 RebalanceDecisionEngine<TRange, TDomain>
```csharp
internal sealed class RebalanceDecisionEngine<TRange, TDomain>
```

**File**: `src/SlidingWindowCache/Core/Rebalance/Decision/RebalanceDecisionEngine.cs`

**Type**: Class (sealed)

**Role**: Pure Decision Logic - **SOLE AUTHORITY for Rebalance Necessity Determination**

**Dependencies** (all readonly, value types):
- ThresholdRebalancePolicy (threshold validation logic)
- ProportionalRangePlanner (cache range planning)
- NoRebalanceRangePlanner (no-rebalance range planning)

**Key Method - Evaluate**:

**Five-Stage Decision Pipeline**:
1. **Current Cache Stability Check** (fast path): Skip if requested range within current NoRebalanceRange
2. **Pending Rebalance Stability Check** (anti-thrashing): Skip if requested range within pending NoRebalanceRange
3. **Desired Range Computation**: Calculate desired cache range and desired no-rebalance range
4. **Equality Short Circuit**: Skip if desired range equals current range
5. **Rebalance Required**: Return execute decision with desired ranges

> **See**: `src/SlidingWindowCache/Core/Rebalance/Decision/RebalanceDecisionEngine.cs` for implementation details.

**Characteristics**:
- ✅ **Pure function** (no side effects, CPU-only, no I/O)
- ✅ **Deterministic** (same inputs → same outputs)
- ✅ **Stateless** (composes value-type policies)
- ✅ **THE authority** for rebalance necessity determination
- ✅ Invoked only in background (inside `IntentController.ProcessIntentsAsync`)
- ❌ Not visible to User Path

**Decision Authority**:
- **This component is the SOLE AUTHORITY** for determining whether rebalance is necessary
- All execution decisions flow from this component's analytical validation
- No other component may override or bypass these decisions
- Executor assumes necessity already validated when invoked

**Uses**:
- ◇ `_policy.ShouldRebalance()` - Stage 1 & 2: NoRebalanceRange containment checks
- ◇ `_planner.Plan()` - Stage 3: Compute DesiredCacheRange
- ◇ `_noRebalancePlanner.Plan()` - Stage 3: Compute DesiredNoRebalanceRange

**Returns**: `RebalanceDecision<TRange>` (struct with `ShouldSchedule`, `DesiredRange`, `DesiredNoRebalanceRange`, `Reason`)

**Ownership**: Owned by IntentController, invoked exclusively in `IntentController.ProcessIntentsAsync`

**Execution Context**: Background Thread (intent processing loop)

**Responsibilities**: 
- **THE authority** for rebalance necessity determination
- 5-stage validation pipeline (stages 1–4 are guard/short-circuit stages; stage 5 is execute)
- Stage 1: Current NoRebalanceRange containment (fast path)
- Stage 2: Pending NoRebalanceRange containment (anti-thrashing)
- Stage 3: Compute DesiredCacheRange and DesiredNoRebalanceRange
- Stage 4: DesiredRange == CurrentRange equality short-circuit
- Stage 5: Return Schedule decision

**Invariants Enforced**:
- D.25: Decision path is purely analytical (CPU-only, no I/O)
- D.26: Never mutates cache state
- D.27: No rebalance if inside NoRebalanceRange (Stage 1 & 2 validation)
- D.28: No rebalance if DesiredCacheRange == CurrentCacheRange (Stage 4 validation)
- D.29: Rebalance executes ONLY if ALL stages confirm necessity

---

#### 🟩 ThresholdRebalancePolicy<TRange, TDomain>
```csharp
internal readonly struct ThresholdRebalancePolicy<TRange, TDomain>
```

**File**: `src/SlidingWindowCache/Core/Rebalance/Decision/ThresholdRebalancePolicy.cs`

**Type**: Struct (readonly value type)

**Role**: Cache Geometry Policy - Threshold Rules (component 1 of 2)

**Key Methods**:
- **ShouldRebalance**: Determines if requested range is outside no-rebalance range
- **GetNoRebalanceRange**: Computes no-rebalance range by shrinking cache range using threshold ratios

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

**File**: `src/SlidingWindowCache/Core/Planning/ProportionalRangePlanner.cs`

**Type**: Struct (readonly value type)

**Role**: Cache Geometry Policy - Shape Planning (component 2 of 2)

**Key Method - Plan**:
- Computes desired cache range by expanding requested range
- Uses left and right cache size coefficients from configuration
- Pure function: same input → same output

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

**File**: `src/SlidingWindowCache/Core/Rebalance/Decision/RebalanceDecision.cs`

**Type**: Struct (readonly value type)

**Properties** (all readonly):
- `bool ShouldSchedule` - Whether rebalance should be scheduled
- `Range<TRange>? DesiredRange` - Target cache range (if scheduling)
- `Range<TRange>? DesiredNoRebalanceRange` - Target no-rebalance zone (if scheduling)
- `RebalanceReason Reason` - Explicit reason for the decision outcome

**Factory Methods**:
- `static Skip(RebalanceReason reason)` → Returns decision to skip rebalance with reason
- `static Execute(Range<TRange> desiredRange, Range<TRange>? desiredNoRebalanceRange)` → Returns decision to schedule with target ranges (sets `Reason = RebalanceRequired`)

**Characteristics**:
- ✅ **Value type** (struct)
- ✅ **Immutable**
- ✅ Represents decision outcome

**Ownership**: Created by RebalanceDecisionEngine, consumed by IntentController.ProcessIntentsAsync

**Mutability**: Immutable

**Lifetime**: Temporary (local variable in intent processing loop)

**Purpose**: Encapsulates decision result (skip or schedule with target ranges and reason)

---

### 7. Rebalance System - Execution

#### 🟦 RebalanceExecutor<TRange, TData, TDomain>
```csharp
internal sealed class RebalanceExecutor<TRange, TData, TDomain>
```

**File**: `src/SlidingWindowCache/Core/Rebalance/Execution/RebalanceExecutor.cs`

**Type**: Class (sealed)

**Role**: Mutating Actor (sole component responsible for cache normalization)

**Dependencies** (all readonly):
- CacheState (shared state - ONLY component that writes to it)
- CacheDataExtensionService (data fetching)
- ICacheDiagnostics (instrumentation)

**Execution Serialization**:
- Provided by the active `IRebalanceExecutionController` implementation (NOT by RebalanceExecutor itself)
- **TaskBasedRebalanceExecutionController** (default): Lock-free task chaining ensures sequential execution
- **ChannelBasedRebalanceExecutionController** (optional): Bounded channel with single reader loop ensures sequential execution
- CancellationToken provides early exit signaling throughout execution phases
- WebAssembly-compatible, async, zero User Path blocking

**Key Method** (high-level description - see source code for implementation details):
- **ExecuteAsync**: Normalizes cache to desired range using delivered data from intent
  - Phase 1: Extend delivered data to cover desired range (may fetch missing segments via IDataSource)
  - Phase 2: Trim to desired range (discard excess data outside target)
  - Phase 3: Update cache state atomically (sole writer - single-writer architecture)
  - Multiple cancellation checks between phases (cancellation-safe)
  - Uses delivered data from intent as authoritative source (avoids redundant fetches)

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

**Ownership**: Owned by WindowCache, used by `IRebalanceExecutionController` implementations

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

#### 🟧 IRebalanceExecutionController<TRange, TData, TDomain>
```csharp
internal interface IRebalanceExecutionController<TRange, TData, TDomain> : IAsyncDisposable
```

**File**: `src/SlidingWindowCache/Core/Rebalance/Execution/IRebalanceExecutionController.cs`

**Type**: Interface

**Role**: Abstraction for rebalance execution serialization strategies

**Purpose**: Defines the contract for serializing rebalance execution requests. Implementations guarantee single-writer architecture by ensuring only one rebalance executes at a time.

**Methods**:
- **PublishExecutionRequest**: Enqueues execution request with intent, desired range, and no-rebalance range
- **LastExecutionRequest**: Property exposing most recent execution request (for decision engine validation)
- **DisposeAsync**: Async disposal for graceful shutdown

**Implementations**:
- `TaskBasedRebalanceExecutionController` - Unbounded task chaining (default, minimal overhead)
- `ChannelBasedRebalanceExecutionController` - Bounded channel with backpressure

**Strategy Selection**: Configured via `WindowCacheOptions.RebalanceQueueCapacity`
- `null` → Task-based strategy (recommended)
- `>= 1` → Channel-based strategy

**Characteristics**:
- ✅ Single-writer guarantee (both implementations)
- ✅ Cancellation support
- ✅ Async disposal for graceful shutdown
- ✅ Strategy pattern for execution serialization

---

#### 🟦 TaskBasedRebalanceExecutionController<TRange, TData, TDomain>
```csharp
internal sealed class TaskBasedRebalanceExecutionController<TRange, TData, TDomain> : 
    IRebalanceExecutionController<TRange, TData, TDomain>
```

**File**: `src/SlidingWindowCache/Core/Rebalance/Execution/TaskBasedRebalanceExecutionController.cs`

**Type**: Class (sealed)

**Role**: Unbounded execution serialization using lock-free task chaining (default strategy)

**Dependencies**:
- RebalanceExecutor (execution logic)
- Debounce delay (from configuration)
- AsyncActivityCounter (idle tracking)
- ICacheDiagnostics (instrumentation)

**Internal State**:
- Current execution task (for task chaining)
- Last execution request (for decision engine validation)
- Dispose state (for idempotent disposal)

**Serialization Mechanism**:
- Task chaining ensures sequential execution (previous task must complete before next starts)
- Lock-free coordination using atomic operations
- Unbounded queue (no backpressure, requests always accepted)

> **See**: `src/SlidingWindowCache/Core/Rebalance/Execution/TaskBasedRebalanceExecutionController.cs` for implementation details.

**Characteristics**:
- ✅ **Unbounded** - no queue capacity limit
- ✅ **Fire-and-forget** - returns completed ValueTask immediately
- ✅ **Minimal overhead** - single Task reference coordination
- ✅ **Sequential execution** - task chaining ensures one at a time
- ✅ **Cancellation** - integrated via CancellationToken
- ✅ **Graceful disposal** - awaits final task completion

**Use Cases**:
- Normal operation with typical rebalance frequencies
- Maximum performance with minimal overhead
- Default/recommended strategy

**Ownership**: Created by WindowCache factory method

**Execution Context**: Background / ThreadPool

---

#### 🟦 ChannelBasedRebalanceExecutionController<TRange, TData, TDomain>
```csharp
internal sealed class ChannelBasedRebalanceExecutionController<TRange, TData, TDomain> : 
    IRebalanceExecutionController<TRange, TData, TDomain>
```

**File**: `src/SlidingWindowCache/Core/Rebalance/Execution/ChannelBasedRebalanceExecutionController.cs`

**Type**: Class (sealed)

**Role**: Bounded execution serialization using System.Threading.Channels (optional strategy)

**Dependencies**:
- Bounded channel (execution queue)
- RebalanceExecutor (execution logic)
- Debounce delay (from configuration)
- AsyncActivityCounter (idle tracking)
- ICacheDiagnostics (instrumentation)

**Internal State**:
- Execution channel (bounded capacity queue)
- Background execution loop task
- Last execution request (for decision engine validation)
- Dispose state (for idempotent disposal)

**Serialization Mechanism**:
- Channel provides natural serialization (single reader loop)
- Bounded capacity creates backpressure when queue is full
- Async write blocks when capacity reached (backpressure signal)

> **See**: `src/SlidingWindowCache/Core/Rebalance/Execution/ChannelBasedRebalanceExecutionController.cs` for implementation details.

**Characteristics**:
- ✅ **Bounded capacity** - strict limit on pending operations
- ✅ **Backpressure** - async await blocks intent processing when full
- ✅ **Background loop** - processes requests sequentially
- ✅ **Cancellation** - superseded operations cancelled before queueing
- ✅ **Graceful disposal** - completes writer and drains remaining operations

**Use Cases**:
- High-frequency rebalance scenarios
- Memory-constrained environments
- Testing scenarios requiring deterministic queue behavior

**Ownership**: Created by WindowCache factory method

**Execution Context**: Background / ThreadPool

---

#### 🟦 CacheDataExtensionService<TRange, TData, TDomain>
```csharp
internal sealed class CacheDataExtensionService<TRange, TData, TDomain>
```

**File**: `src/SlidingWindowCache/Core/Rebalance/Execution/CacheDataExtensionService.cs`

**Type**: Class (sealed)

**Role**: Data Fetcher (used by both User Path and Rebalance Path)

**Dependencies** (all readonly):
- IDataSource (user-provided external data access)
- Domain instance (for range calculations)

**Key Method - ExtendCacheAsync**:
1. Calculate missing ranges (gaps between current and requested)
2. Fetch missing data from data source
3. Union fetched data with current cache (merge without trimming)

> **See**: `src/SlidingWindowCache/Core/Rebalance/Execution/CacheDataExtensionService.cs` for implementation details.

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

**Internal State**:
- UserRequestHandler (delegates user requests)
- AsyncActivityCounter (idle tracking)
- Dispose state (for idempotent disposal)

**Constructor - Composition Root**:
Creates and wires all internal components in dependency order:
1. Creates cache storage strategy (based on configuration)
2. Creates CacheState with storage and domain
3. Creates decision policies (threshold, range planner, no-rebalance planner)
4. Creates data fetcher (CacheDataExtensionService)
5. Creates decision engine (composes policies)
6. Creates rebalance executor
7. Selects and creates execution controller strategy (task-based or channel-based)
8. Creates intent controller (composes decision engine and execution controller)
9. Creates user request handler (composes state, fetcher, intent controller)

> **See**: `src/SlidingWindowCache/WindowCache.cs` constructor for wiring details.

**Public API**:

**GetDataAsync** (primary domain API):
- Validates cache not disposed
- Delegates to UserRequestHandler.HandleRequestAsync
- Throws ObjectDisposedException if disposed

**WaitForIdleAsync** (infrastructure API for synchronization):
- Validates cache not disposed
- Delegates to AsyncActivityCounter
- Completes when system was idle at some point
- Throws ObjectDisposedException if disposed

**DisposeAsync** (resource management):
- Three-state disposal pattern for concurrent safety (active → disposing → disposed)
- Idempotent (safe to call multiple times)
- Cascades disposal to all internal components
- Graceful shutdown (doesn't force-terminate tasks)

> **See**: `src/SlidingWindowCache/WindowCache.cs` for public API implementation details.

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
- Disposal cascades: WindowCache → UserRequestHandler → IntentController → IRebalanceExecutionController (Task-based or Channel-based)

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
│   ├─ 🟦 IntentController ────────────────────┼───┼───┼───┐         │
│   │   └─ 🟧 IRebalanceExecutionController ───┼───┼───┼───┼───┐     │
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
│  UserRequestHandler  [Fast Path Actor — READ-ONLY] │   │   │   │   │
│  🟦 CLASS (sealed)                                  │   │   │   │   │
│                                                     │   │   │   │   │
│  HandleRequestAsync(range, ct):                    │   │   │   │   │
│   1. Check cold start / cache coverage ────────────┼───┤   │   │   │
│   2. Fetch missing via _cacheExtensionService ─────┼───┼───┤   │   │
│      or _dataSource (cold start / full miss)        │   │   │   │   │
│   3. Publish intent with assembled data ────────────┼───┼───┼───┼───┤
│   4. Return ReadOnlyMemory<TData> to user           │   │   │   │   │
│                                                     │   │   │   │   │
│  ❌ NEVER writes to CacheState                      │   │   │   │   │
│  ❌ NEVER calls Cache.Rematerialize()               │   │   │   │   │
│  ❌ NEVER writes LastRequested or NoRebalanceRange  │   │   │   │   │
└─────────────────────────────────────────────────────┼───┼───┼───┼───┘
                                                      │   │   │   │
        ══════════════════════════════════════════════╪═══╪═══╪═══╪═══
        BACKGROUND / THREADPOOL                       │   │   │   │
        ══════════════════════════════════════════════╪═══╪═══╪═══╪═══
                                                      │   │   │   │
┌─────────────────────────────────────────────────────▼───┼───┼───┼───┐
│  IntentController  [Intent Lifecycle + Background Loop] │   │   │   │
│  🟦 CLASS (sealed)                                       │   │   │   │
│                                                          │   │   │   │
│  Fields:                                                 │   │   │   │
│   ├─ IRebalanceExecutionController _executionController ─▼───┼───┤   │
│   └─ Intent? _pendingIntent (Interlocked.Exchange)           │   │   │
│                                                              │   │   │
│  PublishIntent(intent)  [User Thread]:                       │   │   │
│   1. Interlocked.Exchange(_pendingIntent, intent)            │   │   │
│   2. _activityCounter.IncrementActivity()                    │   │   │
│   3. _intentSignal.Release()  → wakes ProcessIntentsAsync    │   │   │
│                                                              │   │   │
│  ProcessIntentsAsync()  [Background Loop]:                   │   │   │
│   1. await _intentSignal.WaitAsync()                         │   │   │
│   2. intent = Interlocked.Exchange(_pendingIntent, null)     │   │   │
│   3. decision = _decisionEngine.Evaluate(intent, ...) ───────┼───┤   │
│   4. if (!decision.ShouldSchedule) → skip                    │   │   │
│   5. lastRequest?.Cancel()                                   │   │   │
│   6. await _executionController.PublishExecutionRequest() ───┼───┤   │
└──────────────────────────────────────────────────────────────┼───┼───┘
                                                               │   │
┌──────────────────────────────────────────────────────────────▼───┼───┐
│  RebalanceDecisionEngine  [Pure Decision Logic]                  │   │
│  🟦 CLASS (sealed)                                                │   │
│                                                                   │   │
│  Fields (value types):                                           │   │
│   ├─ 🟩 ThresholdRebalancePolicy _policy                         │   │
│   ├─ 🟩 ProportionalRangePlanner _planner                        │   │
│   └─ 🟩 NoRebalanceRangePlanner _noRebalancePlanner              │   │
│                                                                   │   │
│  Evaluate(requested, cacheState, lastRequest):                   │   │
│   1. Stage 1: _policy.ShouldRebalance(noRebalanceRange) → skip  │   │
│   2. Stage 2: _policy.ShouldRebalance(pendingNRR) → skip        │   │
│   3. Stage 3: desiredRange = _planner.Plan(requested)            │   │
│   4. Stage 4: desiredRange == currentRange → skip                │   │
│   5. Stage 5: return Schedule(desiredRange, desiredNRR)          │   │
│                                                                   │   │
│  Returns: 🟩 RebalanceDecision<TRange>                           │   │
│    (ShouldSchedule, DesiredRange, DesiredNoRebalanceRange, Reason)│   │
└───────────────────────────────────────────────────────────────────┼───┘
                                                                    │
┌───────────────────────────────────────────────────────────────────▼──┐
│  IRebalanceExecutionController  [Execution Serialization]            │
│  🟧 INTERFACE                                                         │
│                                                                       │
│  Implementations:                                                     │
│   ├─ 🟦 TaskBasedRebalanceExecutionController (default)              │
│   │   • Lock-free task chaining (Volatile.Write for single-writer)   │
│   │   • Debounce via Task.Delay before executing                     │
│   │   • PublishExecutionRequest returns ValueTask.CompletedTask      │
│   └─ 🟦 ChannelBasedRebalanceExecutionController                     │
│       • Bounded Channel<ExecutionRequest> with backpressure          │
│       • Single reader loop processes requests sequentially           │
│                                                                       │
│  ChainExecutionAsync / channel read loop:                            │
│   1. await Task.Delay(debounceDelay, ct)  (cancellable)              │
│   2. await _executor.ExecuteAsync(desiredRange, ct) ─────────────┐  │
└──────────────────────────────────────────────────────────────────┼──┘
                                                                   │
┌──────────────────────────────────────────────────────────────────▼──┐
│  RebalanceExecutor  [Mutating Actor — SOLE WRITER]                   │
│  🟦 CLASS (sealed)                                                    │
│                                                                       │
│  ExecuteAsync(intent, desiredRange, desiredNRR, ct):                │
│   1. await _executionSemaphore.WaitAsync(ct)  (serialize)           │
│   2. baseRangeData = intent.AvailableRangeData                      │
│   3. ct.ThrowIfCancellationRequested()                               │
│   4. extended = await _cacheExtensionService.ExtendCacheAsync() ──┐ │
│   5. ct.ThrowIfCancellationRequested()                               │ │
│   6. rebalanced = extended[desiredRange] (trim)                     │ │
│   7. ct.ThrowIfCancellationRequested()                               │ │
│   8. UpdateCacheState(rebalanced, requestedRange, desiredNRR)      │ │
│      └─ _state.Cache.Rematerialize(rebalanced) ────────────────┐   │ │
│      └─ _state.NoRebalanceRange = desiredNRR ──────────────────┼───┤ │
│      └─ _state.LastRequested = requestedRange ─────────────────┼───┤ │
│   finally: _executionSemaphore.Release()                        │   │ │
└─────────────────────────────────────────────────────────────────┼───┼─┘
                                                                  │   │
┌─────────────────────────────────────────────────────────────────▼───┼──┐
│  CacheState  [Shared Mutable State]                                 │  │
│  🟦 CLASS (sealed)  ⚠️ SHARED                                        │  │
│                                                                      │  │
│  Properties:                                                         │  │
│   ├─ ICacheStorage Cache ◄─ RebalanceExecutor (SOLE WRITER) ─────────┤  │
│   ├─ Range? LastRequested ◄─ RebalanceExecutor                      │  │
│   ├─ Range? NoRebalanceRange ◄─ RebalanceExecutor                   │  │
│   └─ TDomain Domain (readonly)                                       │  │
│                                                                      │  │
│  Read by:                                                            │  │
│   ├─ UserRequestHandler (Cache.Range, Cache.Read, Cache.ToRangeData, LastRequested)
│   ├─ RebalanceExecutor (Cache.Range, Cache.ToRangeData)              │  │
│   └─ RebalanceDecisionEngine (NoRebalanceRange, Cache.Range)         │  │
└──────────────────────────────────────────────────────────────────────┼──┘
                                                                       │
┌──────────────────────────────────────────────────────────────────────▼──┐
│  ICacheStorage<TRange, TData, TDomain>                                  │
│  🟧 INTERFACE                                                            │
│                                                                          │
│  Implementations:                                                        │
│   ├─ 🟦 SnapshotReadStorage (TData[] array)                             │
│   │   • Read: zero allocation (memory view)                             │
│   │   • Write: expensive (allocates new array)                          │
│   │                                                                      │
│   └─ 🟦 CopyOnReadStorage (List<TData>)                                 │
│       • Read: allocates (copies to new array)                           │
│       • Write: cheap (list operations)                                  │
│                                                                          │
│  Methods:                                                                │
│   ├─ void Rematerialize(RangeData) ⊲ WRITE                              │
│   ├─ ReadOnlyMemory<TData> Read(Range) ⊳ READ                           │
│   └─ RangeData ToRangeData() ⊳ READ                                     │
└──────────────────────────────────────────────────────────────────────────┘
                                                                           │
┌──────────────────────────────────────────────────────────────────────────▼──┐
│  CacheDataExtensionService  [Data Fetcher]                                  │
│  🟦 CLASS (sealed)                                                           │
│                                                                              │
│  ExtendCacheAsync(current, requested, ct):                                  │
│   1. missingRanges = CalculateMissingRanges()                               │
│   2. fetched = await _dataSource.FetchAsync(missingRanges, ct) ◄────────┐  │
│   3. return UnionAll(current, fetched) (merge, no trim)                 │  │
│                                                                          │  │
│  Shared by:                                                              │  │
│   ├─ UserRequestHandler (extend to cover requested range — no mutation)  │  │
│   └─ RebalanceExecutor (extend to desired range — feeds mutation)        │  │
└───────────────────────────────────────────────────────────────────────────┼──┘
                                                                            │
┌───────────────────────────────────────────────────────────────────────────▼──┐
│  IDataSource<TRangeType, TDataType>  [External Data Source]                  │
│  🟧 INTERFACE (user-implemented)                                              │
│                                                                               │
│  Methods:                                                                     │
│   ├─ FetchAsync(Range, CT) → Task<IEnumerable<TData>>                        │
│   └─ FetchAsync(IEnumerable<Range>, CT) → Task<IEnumerable<RangeChunk>>     │
│                                                                               │
│  Characteristics:                                                             │
│   ├─ User-provided implementation                                            │
│   ├─ May perform I/O (network, disk, database)                               │
│   ├─ Read-only (fetches data)                                                │
│   └─ Should respect CancellationToken                                        │
└───────────────────────────────────────────────────────────────────────────────┘
```
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

**RebalanceDecisionEngine** (via IntentController.ProcessIntentsAsync):
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

### CancellationTokenSource (Execution Cancellation)

#### Owner: IntentController.ProcessIntentsAsync (via ExecutionRequest)

**Creates**:
- In `ProcessIntentsAsync()` — new `CancellationTokenSource` for each `ExecutionRequest` enqueued

**Cancels**:
- In `ProcessIntentsAsync()` — cancels `lastExecutionRequest` before enqueuing a new one
- Prevents stale execution from completing after a newer execution has been scheduled

**Disposes**:
- `ExecutionRequest` lifetime — disposed when execution completes or is superseded

#### Users

**IRebalanceExecutionController implementations**:
- 👁️ Receive `CancellationToken` via `ExecutionRequest`
- 👁️ Pass token to `Task.Delay()` (cancellable debounce)
- 👁️ Pass token to `RebalanceExecutor.ExecuteAsync()`

**RebalanceExecutor**:
- 👁️ Receives token from `IRebalanceExecutionController` (via `ExecutionRequest`)
- 👁️ Calls `ThrowIfCancellationRequested()` at multiple points:
  1. After acquiring semaphore, before I/O
  2. After `ExtendCacheAsync()`, before trim
  3. Before `Rematerialize()` (prevent applying obsolete results)

**CacheDataExtensionService**:
- 👁️ Receives token from caller (UserRequestHandler or RebalanceExecutor)
- 👁️ Passes token to `IDataSource.FetchAsync()` (cancellable I/O)

---

## Thread Safety Model

### Concurrency Philosophy

The Sliding Window Cache follows a **single consumer model** as documented in `docs/architecture-model.md`:

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

| Component                                                                    | Thread Context    | Notes                                                                 |
|------------------------------------------------------------------------------|-------------------|-----------------------------------------------------------------------|
| **WindowCache**                                                              | Neutral           | Just delegates                                                        |
| **UserRequestHandler**                                                       | ⚡ **User Thread** | Synchronous, fast path (user request handling)                        |
| **IntentController.PublishIntent()**                                         | ⚡ **User Thread** | Atomic intent storage + semaphore signal (fire-and-forget)            |
| **IntentController.ProcessIntentsAsync()**                                   | 🔄 **Background** | Intent processing loop, invokes DecisionEngine                        |
| **RebalanceDecisionEngine**                                                  | 🔄 **Background** | Invoked in intent processing loop, CPU-only logic                     |
| **ProportionalRangePlanner**                                                 | 🔄 **Background** | Invoked by DecisionEngine in intent processing loop                   |
| **NoRebalanceRangePlanner**                                                  | 🔄 **Background** | Invoked by DecisionEngine in intent processing loop                   |
| **ThresholdRebalancePolicy**                                                 | 🔄 **Background** | Invoked by DecisionEngine in intent processing loop                   |
| **IRebalanceExecutionController.PublishExecutionRequest()**                  | 🔄 **Background** | Invoked by intent loop (task-based: sync, channel-based: async await) |
| **TaskBasedRebalanceExecutionController.ChainExecutionAsync()**              | 🔄 **Background** | Task chain execution (sequential)                                     |
| **ChannelBasedRebalanceExecutionController.ProcessExecutionRequestsAsync()** | 🔄 **Background** | Channel loop execution                                                |
| **RebalanceExecutor**                                                        | 🔄 **Background** | ThreadPool, async, I/O                                                |
| **CacheDataExtensionService**                                                | Both ⚡🔄          | User Thread OR Background                                             |
| **CacheState**                                                               | Both ⚡🔄          | Shared mutable (no locks!)                                            |
| **Storage (Snapshot/CopyOnRead)**                                            | Both ⚡🔄          | Owned by CacheState                                                   |

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
│ PHASE 1: USER THREAD (Synchronous - Fast Path)                          │
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
│ PHASE 2: BACKGROUND THREAD #1 (Intent Processing Loop)                  │
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
│ IRebalanceExecutionController    │ • Create ExecutionRequest            │
│  .PublishExecutionRequest()      │ • Task-based: Volatile.Write (sync)  │
│                                  │ • Channel-based: await WriteAsync()  │
└─────────────────────────────────────────────────────────────────────────┘
                                      ↓ (strategy-specific)
┌─────────────────────────────────────────────────────────────────────────┐
│ PHASE 3: BACKGROUND EXECUTION (Strategy-Specific)                       │
├─────────────────────────────────────────────────────────────────────────┤
│ Component                        │ Operation                            │
├──────────────────────────────────┼──────────────────────────────────────┤
│ TASK-BASED STRATEGY:             │                                      │
│ ChainExecutionAsync()            │ • await previousTask                 │
│  (chained async method)          │ • await ExecuteRequestAsync()        │
│           ↓                      │                                      │
│ OR CHANNEL-BASED STRATEGY:       │                                      │
│ ProcessExecutionRequestsAsync()  │ • await foreach (channel read)       │
│  (infinite background loop)      │ • Sequential processing              │
│           ↓                      │                                      │
│ ExecuteRequestAsync()            │ • await Task.Delay(debounce)         │
│  (both strategies)               │ • Cancellation check                 │
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

3. **Background Execution**: Strategy-specific serialization
   - **Task-based**: Chained async methods on ThreadPool (await previousTask pattern)
   - **Channel-based**: Single dedicated loop via channel reader (sequential processing)
   - Both: Process execution requests sequentially (one at a time)
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


#### User Request Flow (User Thread — until PublishIntent returns)
```
1. UserRequestHandler.HandleRequestAsync() called
2. Read from cache or fetch missing data from IDataSource (READ-ONLY)
3. Assemble data to return to user (NO cache mutation)
4. PublishIntent(intent) in user thread:
   └─> IntentController.PublishIntent(intent) ⚡ USER THREAD
       ├─> Interlocked.Exchange(_pendingIntent, intent)  (atomic, O(1))
       ├─> _activityCounter.IncrementActivity()
       └─> _intentSignal.Release()  → wakes background loop
           └─> Returns immediately
5. Return assembled data to user

--- BACKGROUND LOOP (ProcessIntentsAsync) ---

6. _intentSignal.WaitAsync() unblocks 🔄 BACKGROUND
7. Interlocked.Exchange(_pendingIntent, null) → reads intent
8. DecisionEngine.Evaluate() 🔄 BACKGROUND
   └─> 5-stage validation (CPU-only, side-effect free)
       - Stage 1: CurrentNoRebalanceRange check
       - Stage 2: PendingNoRebalanceRange check  
       - Stage 3: Compute DesiredRange + DesiredNoRebalanceRange
       - Stage 4: DesiredRange == CurrentRange check
       - Stage 5: Schedule
9. If validation rejects: continue loop (work avoidance)
10. If schedule: lastRequest?.Cancel() + PublishExecutionRequest()

--- EXECUTION (IRebalanceExecutionController) ---

11. Debounce delay (Task.Delay) 🔄 BACKGROUND
12. RebalanceExecutor.ExecuteAsync() 🔄 BACKGROUND
    └─> I/O operations + atomic cache mutations
```

**Key:** Decision evaluation happens in **background loop** (NOT in user thread).
User thread only does atomic store + semaphore signal and returns immediately.

**Why This Matters:**
- User request burst → latest intent wins via `Interlocked.Exchange` → burst resistance
- Decision loop processes serially → no concurrent thrashing
- User thread is never blocked by decision evaluation or I/O

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
- WindowCache - Facade, delegates to UserRequestHandler
- UserRequestHandler - Serves user requests, publishes intents (fire-and-forget)
- IntentController.PublishIntent - Intent publishing (fire-and-forget, returns immediately)

**Background Thread (Intent Processing Loop)**:
- IntentController.ProcessIntentsAsync - Intent processing loop, decision orchestration
- RebalanceDecisionEngine - Pure decision logic (CPU-only, deterministic)
- ThresholdRebalancePolicy - Threshold validation (value type, inline)
- ProportionalRangePlanner - Cache geometry planning (value type, inline)

**Background ThreadPool (Execution)**:
- RebalanceExecutor - Cache normalization, I/O operations

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
- **Architecture Model**: `docs/architecture-model.md`
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
