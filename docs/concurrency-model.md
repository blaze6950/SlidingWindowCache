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

### Execution Serialization

While the single-writer architecture eliminates write-write races between User Path and Rebalance Execution, multiple rebalance operations can be scheduled concurrently. To guarantee that only one rebalance execution writes to cache state at a time, the system uses two layers of serialization:

1. **Execution Controller Layer**: Serializes rebalance execution requests using one of two strategies (configured via `WindowCacheOptions.RebalanceQueueCapacity`)
2. **Executor Layer**: `RebalanceExecutor` uses `SemaphoreSlim(1, 1)` for mutual exclusion during cache mutations

**Execution Controller Strategies:**

The system supports two strategies for serializing rebalance execution requests:

| Strategy                 | Configuration                  | Mechanism                                            | Backpressure                            | Use Case                                                      |
|--------------------------|--------------------------------|------------------------------------------------------|-----------------------------------------|---------------------------------------------------------------|
| **Task-based** (default) | `rebalanceQueueCapacity: null` | Lock-free task chaining with `ChainExecutionAsync()` | None (completes synchronously)          | Recommended for most scenarios - minimal overhead             |
| **Channel-based**        | `rebalanceQueueCapacity: >= 1` | `System.Threading.Channels` with bounded capacity    | Async await on `WriteAsync()` when full | High-frequency scenarios or resource-constrained environments |

**Task-Based Strategy (Default - Unbounded):**

```csharp
// Implementation: TaskBasedRebalanceExecutionController
// Serialization: Lock-free task chaining using volatile write (single-writer pattern)
// Backpressure: None - returns ValueTask.CompletedTask immediately
// Overhead: Minimal - single Task reference + volatile write
// Pattern: ChainExecutionAsync(previousTask, request) ensures sequential execution
```

- **Single-Writer Pattern**: Lock-free using volatile write (only intent processing loop writes)
- **Execution**: Fire-and-forget (returns `ValueTask.CompletedTask` immediately, executes on ThreadPool)
- **Cancellation**: Previous request cancelled before chaining new execution
- **Task Chaining**: `await previousTask; await ExecuteRequestAsync(request);` ensures serial execution
- **Disposal**: Captures task chain via volatile read and awaits completion for graceful shutdown

**Channel-Based Strategy (Bounded):**

```csharp
// Implementation: ChannelBasedRebalanceExecutionController
// Serialization: Bounded channel with single reader/writer
// Backpressure: Async await on WriteAsync() - blocks intent loop when full
// Overhead: Channel infrastructure + background processing loop
// Pattern: await WriteAsync(request) creates proper backpressure
```

- **Capacity Control**: Strict limit on pending rebalance operations (bounded channel capacity)
- **Backpressure**: `await WriteAsync()` blocks intent processing loop when channel is full (intentional throttling)
- **Execution**: Background loop processes requests sequentially from channel (one at a time)
- **Cancellation**: Superseded operations cancelled before new ones are enqueued
- **Disposal**: Completes channel writer and awaits loop completion for graceful shutdown

**Executor Layer (Both Strategies):**

Regardless of the controller strategy, `RebalanceExecutor.ExecuteAsync()` uses `SemaphoreSlim(1, 1)` for mutual exclusion:

- **`SemaphoreSlim`**: Ensures only one rebalance execution can proceed through cache mutation at a time
- **Cancellation Token**: Provides early exit signaling - operations can be cancelled while waiting for the semaphore
- **Ordering**: New rebalance scheduled AFTER old one is cancelled, ensuring proper semaphore acquisition order
- **Atomic cancellation**: `Interlocked.Exchange` prevents race where multiple threads call `Cancel()` on same `PendingRebalance`

**Why Both CTS and SemaphoreSlim:**

- **CTS**: Lightweight signaling mechanism for cooperative cancellation (intent obsolescence, user cancellation)
- **SemaphoreSlim**: Mutual exclusion for cache writes (prevents concurrent execution)
- Together: CTS signals "don't do this work anymore", semaphore enforces "only one at a time"

**Design Properties (Both Strategies):**

- ✅ **WebAssembly compatible** - async, no blocking threads
- ✅ **Zero User Path blocking** - User Path never acquires semaphore, only rebalance execution does
- ✅ **Production-grade** - prevents data corruption from parallel cache writes
- ✅ **Lightweight** - semaphore rarely contended (rebalance is rare operation)
- ✅ **Cancellation-friendly** - `WaitAsync(cancellationToken)` exits cleanly if cancelled
- ✅ **Single-writer guarantee** - Only one rebalance executes at a time (architectural invariant)

**Acquisition Point:**

The semaphore is acquired at the start of `RebalanceExecutor.ExecuteAsync()`, before any I/O operations. This prevents queue buildup while allowing cancellation to propagate immediately. If cancelled during wait, the operation exits without acquiring the semaphore.

**Strategy Selection Guidance:**

- **Use Task-based (default)** for:
  - Normal operation with typical rebalance frequencies
  - Maximum performance with minimal overhead
  - Fire-and-forget execution model
  
- **Use Channel-based (bounded)** for:
  - High-frequency rebalance scenarios requiring backpressure
  - Memory-constrained environments where queue growth must be limited
  - Testing scenarios requiring deterministic queue behavior

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
5. **Work avoidance**: Rebalance skipped if validation determines it's unnecessary (NoRebalanceRange containment, Desired==Current, pending rebalance coverage) - **all happens synchronously before background scheduling**
6. **Background execution** (only part that runs in ThreadPool): debounce delay + actual rebalance I/O operations
7. **Debounce delay** controls convergence timing and prevents thrashing (background)
8. **User correctness** never depends on cache state being up-to-date

**Key insight:** User always receives correct data, regardless of whether cache has converged yet.

**"Smart" characteristic:** The system avoids unnecessary work through multi-stage validation rather than blindly executing every intent. This prevents thrashing, reduces redundant I/O, and maintains stability under rapidly changing access patterns while ensuring eventual convergence to optimal configuration.

**Critical Architectural Detail - Intent Processing is Synchronous:**

The decision logic (multi-stage validation) and scheduling are **NOT background operations**. They execute **synchronously in the user thread** before returning control to the user. Only the actual rebalance execution (I/O operations) happens in background via background task scheduling.

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

## Disposal and Resource Management

### Disposal Architecture

WindowCache implements `IAsyncDisposable` to ensure proper cleanup of background processing resources. The disposal mechanism follows the same concurrency principles as the rest of the system: **lock-free synchronization** with graceful coordination.

### Disposal State Machine

Disposal uses a **three-state pattern** with lock-free transitions:

```
States:
  0 = Active (accepting operations)
  1 = Disposing (disposal in progress)
  2 = Disposed (cleanup complete)

Transitions:
  0 → 1: First DisposeAsync() call wins via Interlocked.CompareExchange
  1 → 2: Disposal completes, state updated via Volatile.Write
  
Concurrent Calls:
  - First call (0→1): Performs actual disposal
  - Concurrent calls (1): Spin-wait until state becomes 2
  - Subsequent calls (2): Return immediately (idempotent)
```

### Disposal Sequence

When `DisposeAsync()` is called, cleanup cascades through the ownership hierarchy:

```
WindowCache.DisposeAsync()
  └─> UserRequestHandler.DisposeAsync()
      └─> IntentController.DisposeAsync()
          ├─> Cancel intent processing loop (CancellationTokenSource)
          ├─> Wait for processing loop to exit (Task.Wait)
          ├─> IRebalanceExecutionController.DisposeAsync()
          │   ├─> Task-based: Capture task chain (volatile read) + await completion
          │   └─> Channel-based: Complete channel writer + await loop completion
          └─> Dispose coordination resources (SemaphoreSlim, CancellationTokenSource)
```

**Key Properties:**
- **Graceful shutdown**: Background tasks finish current work before exiting
- **No forced termination**: Cancellation signals used, not thread aborts
- **Resource cleanup**: All channels, semaphores, and cancellation tokens disposed
- **Cascading disposal**: Follows ownership hierarchy (parent disposes children)

### Operation Blocking After Disposal

All public operations check disposal state using lock-free reads:

```csharp
public ValueTask<ReadOnlyMemory<TData>> GetDataAsync(...)
{
    // Check disposal state (lock-free)
    if (Volatile.Read(ref _disposeState) != 0)
        throw new ObjectDisposedException(...);
    
    // Proceed with operation
}

public Task WaitForIdleAsync(...)
{
    // Check disposal state (lock-free)
    if (Volatile.Read(ref _disposeState) != 0)
        throw new ObjectDisposedException(...);
    
    // Proceed with operation
}
```

**Design Properties:**
- ✅ **Lock-free reads**: `Volatile.Read` ensures visibility without locks
- ✅ **Fail-fast**: Operations immediately throw `ObjectDisposedException`
- ✅ **No partial execution**: Disposal check happens before any work
- ✅ **Consistent behavior**: All operations blocked uniformly after disposal

### Concurrent Disposal Safety

The three-state disposal pattern handles concurrent disposal attempts safely:

```csharp
public async ValueTask DisposeAsync()
{
    // Atomic transition from active (0) to disposing (1)
    var previousState = Interlocked.CompareExchange(ref _disposeState, 1, 0);
    
    if (previousState == 0)
    {
        // This thread won the race - perform disposal
        try
        {
            await _userRequestHandler.DisposeAsync();
        }
        finally
        {
            // Mark disposal complete (transition to state 2)
            Volatile.Write(ref _disposeState, 2);
        }
    }
    else if (previousState == 1)
    {
        // Another thread is disposing - spin-wait until complete
        var spinWait = new SpinWait();
        while (Volatile.Read(ref _disposeState) == 1)
        {
            spinWait.SpinOnce();
        }
    }
    // If previousState == 2: already disposed, return immediately
}
```

**Guarantees:**
- ✅ **Exactly-once execution**: Only first call performs disposal
- ✅ **Concurrent safety**: Multiple threads can call simultaneously
- ✅ **Completion waiting**: Concurrent callers wait for disposal to finish
- ✅ **Idempotency**: Safe to call multiple times

### Disposal vs Active Operations

**Race Condition Handling:**

If `DisposeAsync()` is called while operations are in progress:
1. Disposal marks state as disposing (blocks new operations)
2. Background loops observe cancellation and exit gracefully
3. In-flight operations may complete or throw `ObjectDisposedException`
4. Disposal waits for background loops to exit
5. All resources released after loops exit

**User Experience:**
- Operations started **before** disposal: May complete successfully or throw `ObjectDisposedException`
- Operations started **after** disposal: Always throw `ObjectDisposedException`
- No undefined behavior or resource corruption

### Disposal and Single-Writer Architecture

Disposal respects the single-writer architecture:
- **User Path**: Read-only, disposal just blocks new reads
- **Rebalance Execution**: Single writer, disposal waits for current execution to finish
- **No race conditions**: Disposal does not introduce write-write races
- **Graceful coordination**: Uses same cancellation mechanism as rebalance operations

### Integration with AsyncActivityCounter

The `AsyncActivityCounter` tracks active background operations:
- Intent processing increments/decrements counter
- Rebalance execution increments/decrements counter
- `WaitForIdleAsync()` uses counter to detect idle state

**Disposal does not use AsyncActivityCounter** - it directly waits for background loops to exit via `Task.Wait()` on the loop tasks. This ensures disposal completes even if counter state is inconsistent.

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
- **Graceful disposal with resource cleanup** (lock-free, idempotent, concurrent-safe)
- **Background task coordination during disposal** (wait for loops to exit gracefully)

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
