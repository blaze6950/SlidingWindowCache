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
- **No Locks:** Coordination via cancellation, not mutual exclusion
- **Eventual Consistency:** Cache state converges asynchronously to optimal configuration

### Write Ownership

Only `RebalanceExecutor` may write to:
- Cache data and range (via `Cache.Rematerialize()`)
- `LastRequested` field
- `NoRebalanceRange` field

All other components have read-only access to cache state.

### Read Safety

User Path safely reads cache state without locks because:
- User Path never writes to cache (read-only guarantee)
- Rebalance Execution performs atomic updates via `Rematerialize()`
- Cancellation ensures Rebalance Execution yields before User Path operations
- Single-writer eliminates race conditions

### Eventual Consistency Model

Cache state converges to optimal configuration asynchronously:

1. **User Path** returns correct data immediately (from cache or IDataSource)
2. **User Path** publishes intent with delivered data
3. **Cache state** updates occur in background via Rebalance Execution
4. **Debounce delay** controls convergence timing
5. **User correctness** never depends on cache state being up-to-date

**Key insight:** User always receives correct data, regardless of whether cache has converged yet.

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
- ordered intents
- cancellation of obsolete work
- "latest access wins" semantics
- eventual stabilization

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

**DEBUG builds:**
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

**RELEASE builds:**
- `WaitForIdleAsync()` returns `Task.CompletedTask` immediately
- No `_idleTask` field exists (zero overhead)
- Conditional compilation ensures production builds unaffected

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
- Uses `Interlocked.Exchange` for atomic field replacement
- `_currentIntentCts` field atomically swapped during intent operations
- Thread-safe without blocking - guaranteed progress
- Zero contention overhead

**Race Condition Prevention:**
```csharp
// Atomic replacement ensures no race conditions
var oldCts = Interlocked.Exchange(ref _currentIntentCts, newCts);
```

**Testing Coverage:**
- Lock-free behavior validated by `ConcurrencyStabilityTests`
- Tested under concurrent load (100+ simultaneous operations)
- No deadlocks, no race conditions, no data corruption observed

This lightweight synchronization primitive ensures thread-safety without the overhead
and complexity of traditional locking mechanisms.

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
