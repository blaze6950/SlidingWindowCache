# Concurrency Model

## Core Principle

This library is built around a **single logical consumer per cache instance** with a **single-writer architecture**.

A cache instance:
- is **not thread-safe for shared access**
- is **designed for concurrent reads** (User Path is read-only)
- assumes a single, coherent access pattern
- enforces single-writer for all mutations (Rebalance Execution only)

This is an **ideological requirement**, not merely an architectural or technical limitation.

The architecture of the library reflects and enforces this principle.

---

## Single-Writer Architecture

### Core Design

The cache implements a **single-writer** concurrency model:

- **One Writer:** Rebalance Execution Path exclusively
- **Read-Only User Path:** User Path never mutates cache state
- **Coordination via Cancellation:** Cancellation prevents concurrent executions (mechanical coordination), not duplicate decision-making
- **Rebalance Decision Validation:** Multi-stage analytical pipeline determines rebalance necessity (CPU-only, no I/O)
- **Eventual Consistency:** Cache state converges asynchronously to optimal configuration

### Write Ownership

Only `RebalanceExecutor` may write to `CacheState` fields:
- Cache data and range (via `Cache.Rematerialize()` atomic swap)
- `LastRequested` property (via `internal set` - restricted to rebalance execution)
- `NoRebalanceRange` property (via `internal set` - restricted to rebalance execution)

All other components have read-only access to cache state (public getters only).

### Read Safety

User Path safely reads cache state without locks because:
- **User Path never writes to CacheState** (architectural invariant, no write access)
- **Rebalance Execution is sole writer** (single-writer architecture eliminates write-write races)
- **Cache storage performs atomic updates** via `Rematerialize()` (array/List reference assignment is atomic)
- **Property reads are safe** - reference reads are atomic on all supported platforms
- **Cancellation coordination** - Rebalance Execution checks cancellation before mutations
- **No read-write races** - User Path may read while Rebalance executes, but User Path sees consistent state (old or new, never partial)

**Key Insight:** Thread-safety is achieved through **architectural constraints** (single-writer) and **coordination** (cancellation), not through locks or volatile keywords on CacheState fields.

### Rebalance Validation vs Cancellation

**Key Distinction:**
- **Rebalance Validation** = Decision mechanism (analytical, CPU-only, determines necessity) - **THE authority**
- **Cancellation** = Coordination mechanism (mechanical, prevents concurrent executions) - coordination tool only

**Decision-Driven Execution Model:**
1. User Path publishes intent with delivered data (signal, not command)
2. **Rebalance Decision Engine validates necessity** via multi-stage analytical pipeline (THE sole authority)
3. **Validation confirms necessity** → pending rebalance cancelled + new execution scheduled (coordination via cancellation)
4. **Validation rejects necessity** → no cancellation, work avoidance (skip entirely: NoRebalanceRange containment, pending coverage, Desired==Current)

**Smart Eventual Consistency Principle:**

Cancellation does NOT drive decisions; **validated rebalance necessity drives cancellation**.

The Decision Engine determines necessity through analytical validation (work avoidance authority). Cancellation is merely the coordination tool that prevents concurrent executions (single-writer enforcement). This separation enables smart eventual consistency: the system converges to optimal configuration while avoiding unnecessary work (thrashing prevention, redundant I/O elimination, oscillation avoidance).

### Smart Eventual Consistency Model

Cache state converges to optimal configuration asynchronously through **decision-driven rebalance execution**:

1. **User Path** returns correct data immediately (from cache or IDataSource)
2. **User Path** publishes intent with delivered data (**synchronously in user thread**)
3. **Rebalance Decision Engine** validates rebalance necessity through multi-stage analytical pipeline (**synchronously in user thread - CPU-only, side-effect free, lightweight**)
4. **Scheduling** creates PendingRebalance and schedules background Task (**synchronously in user thread**)
5. **Work avoidance**: Rebalance skipped if validation determines it's unnecessary (NoRebalanceRange containment, Desired==Current, pending rebalance coverage) - **all happens synchronously before Task.Run**
6. **Background execution** (only part that runs in ThreadPool): debounce delay + actual rebalance I/O operations
7. **Debounce delay** controls convergence timing and prevents thrashing (background)
8. **User correctness** never depends on cache state being up-to-date

**Key insight:** User always receives correct data, regardless of whether cache has converged yet.

**"Smart" characteristic:** The system avoids unnecessary work through multi-stage validation rather than blindly executing every intent. This prevents thrashing, reduces redundant I/O, and maintains stability under rapidly changing access patterns while ensuring eventual convergence to optimal configuration.

**Critical Architectural Detail - Intent Processing is Synchronous:**

The decision logic (multi-stage validation) and scheduling are **NOT background operations**. They execute **synchronously in the user thread** before returning control to the user. Only the actual rebalance execution (I/O operations) happens in background via `Task.Run`.

This design is intentional and critical for handling user request bursts:
- ✅ **CPU-only validation** in user thread (math, conditions, no I/O)
- ✅ **Side-effect free** - just calculations
- ✅ **Lightweight** - completes in microseconds
- ✅ **Prevents intent thrashing** - validates necessity immediately, skips if not needed
- ✅ **No background queue buildup** - decisions made synchronously
- ⚠️ Only actual **I/O operations** (data fetching, cache mutation) happen in background

---

## Single Cache Instance = Single Consumer

A sliding window cache models the behavior of **one observer moving through data**.

Each cache instance represents:
- one user
- one access trajectory
- one temporal sequence of requests

Attempting to share a single cache instance across multiple users or threads
violates this fundamental assumption.

**Note:** The single-consumer constraint exists for coherent access patterns,
not for mutation safety (User Path is read-only, so parallel reads would be safe
from a mutation perspective, but would still violate the single-consumer model).

---

## Why This Is a Requirement (Not a Limitation)

### 1. Sliding Window Requires a Unified Access Pattern

The cache continuously adapts its window based on observed access.

If multiple consumers request unrelated ranges:
- there is no single `DesiredCacheRange`
- the window oscillates or becomes unstable
- cache efficiency collapses

This is not a concurrency bug — it is a **model mismatch**.

---

### 2. Rebalance Logic Depends on a Single Timeline

Rebalance behavior relies on:
- ordered intents representing sequential access observations
- multi-stage validation determining rebalance necessity
- cancellation of pending work when validation confirms new rebalance needed
- "latest validated decision wins" semantics
- eventual stabilization through work avoidance (NoRebalanceRange, Desired==Current checks)

These guarantees require a **single temporal sequence of access events**.

Multiple consumers introduce conflicting timelines that cannot be meaningfully
merged without fundamentally changing the model.

---

### 3. Architecture Reflects the Ideology

The system architecture:
- enforces single-thread access
- isolates rebalance logic from user code
- assumes coherent access intent

These choices do not define the constraint —  
they **exist to preserve it**.

---

## How to Use This Library in Multi-User Environments

### ✅ Correct Approach

If your system has multiple users or concurrent consumers:

> **Create one cache instance per user (or per logical consumer).**

Each cache instance:
- operates independently
- maintains its own sliding window
- runs its own rebalance lifecycle

This preserves correctness, performance, and predictability.

---

### ❌ Incorrect Approach

Do **not**:
- share a cache instance across threads
- multiplex multiple users through a single cache
- attempt to synchronize access externally

External synchronization does not solve the underlying model conflict and will
result in inefficient or unstable behavior.

---

## Deterministic Background Job Synchronization

### Testing Infrastructure API

The cache provides a `WaitForIdleAsync()` method for deterministic synchronization with
background rebalance operations. This is **infrastructure/testing API**, not part of normal
usage patterns or domain semantics.

### Implementation

**Mechanism**: Task lifecycle tracking via observe-and-stabilize pattern

- `RebalanceScheduler` maintains `_idleTask` field tracking latest background Task
- `WaitForIdleAsync()` implements:
  ```
  1. Volatile.Read(_idleTask) → observe current Task
  2. await observedTask → wait for completion
  3. Re-check if _idleTask changed → detect new rebalance
  4. Loop until Task reference stabilizes
  ```
- Guarantees: No rebalance execution running when method returns
- Safety: Handles concurrent intent cancellation and rescheduling correctly
- Use cases: Testing, graceful shutdown, health checks, integration scenarios

### Use Cases

- **Test stabilization**: Ensure cache has converged before assertions
- **Integration testing**: Synchronize with background work completion
- **Diagnostic scenarios**: Verify rebalance execution finished

### Architectural Preservation

This synchronization mechanism does **not** alter actor responsibilities:

- UserRequestHandler remains sole intent publisher
- IntentController remains lifecycle authority
- RebalanceScheduler remains execution authority
- WindowCache remains pure facade

Method exists only to expose idle synchronization through public API for testing purposes.

### Lock-Free Implementation

**IntentController** uses lock-free synchronization:
- **No locks, no `lock` statements, no mutexes**
- Uses `Volatile.Read` and `Volatile.Write` for safe field access across threads
- `_pendingRebalance` field accessed with memory barriers via `Volatile` operations
- Encapsulates `CancellationTokenSource` within `PendingRebalance` domain object (DDD-style)
- Thread-safe without blocking - guaranteed progress
- Zero contention overhead

**Safe Visibility Pattern:**
```csharp
// Read with memory barrier for safe observation
var pending = Volatile.Read(ref _pendingRebalance);

// Write with memory barrier for safe publication
Volatile.Write(ref _pendingRebalance, newPending);
```

**Domain-Driven Cancellation:**
- `PendingRebalance` domain object owns `CancellationTokenSource` lifecycle
- Cancellation invoked through domain object's `Cancel()` method
- Eliminates direct CTS management in IntentController (better encapsulation)

**Testing Coverage:**
- Lock-free behavior validated by `ConcurrencyStabilityTests`
- Tested under concurrent load (100+ simultaneous operations)
- No deadlocks, no race conditions, no data corruption observed

This lightweight synchronization approach using `Volatile` operations ensures thread-safety 
without the overhead and complexity of traditional locking mechanisms, while the DDD-style 
domain object pattern provides clean encapsulation of cancellation infrastructure.

### Relation to Concurrency Model

The observe-and-stabilize pattern:
- Does not introduce locking or mutual exclusion
- Leverages existing single-writer architecture
- Provides visibility through volatile reads
- Maintains eventual consistency model

This is synchronization **with** background work, not synchronization **of** concurrent writers.

---

## What Is Supported

- Single logical consumer per cache instance (coherent access pattern)
- Single-writer architecture (Rebalance Execution only)
- Read-only User Path (safe for repeated calls from same consumer)
- Background asynchronous rebalance
- Cancellation and debouncing of rebalance execution
- High-frequency access from one logical consumer
- Eventual consistency model (cache converges asynchronously)
- Intent-based data delivery (delivered data in intent avoids duplicate fetches)

---

## What Is Explicitly Not Supported

- Multiple concurrent consumers per cache instance
- Thread-safe shared access
- Cross-user sliding window arbitration

---

## Design Philosophy

This library prioritizes:
- conceptual clarity
- predictable behavior
- cache efficiency
- correctness of temporal and spatial logic

Instead of providing superficial thread safety,
it enforces a model that remains stable, explainable, and performant.
