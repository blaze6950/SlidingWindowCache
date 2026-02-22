# System Architecture Model

## What This Document Covers

This document describes the **complete architectural model** of SlidingWindowCache, including:

1. **Threading Model** — Single consumer principle, internal concurrency, execution contexts
2. **Single-Writer Architecture** — Read-only User Path, exclusive writer pattern, lock-free coordination
3. **Decision-Driven Execution** — Multi-stage validation pipeline, work avoidance, smart consistency
4. **Resource Management** — Disposal, graceful shutdown, lock-free coordination mechanisms

**Note**: This document was previously titled "Concurrency Model" but has been renamed to better reflect its broader scope beyond just threading concerns. It covers the fundamental architectural patterns that define how SlidingWindowCache operates.

**Related Documentation**:
- [invariants.md](invariants.md) — Formal specifications for architectural concepts described here
- [component-map.md](component-map.md) — Implementation details and component structure
- [scenario-model.md](scenario-model.md) — Temporal behavior and execution flows
- [glossary.md](glossary.md) — Canonical term definitions

---

## Core Principle

This library is built around a **single logical consumer per cache instance** with a **single-writer architecture**.

A cache instance:
- is designed for **one logical consumer** (one user, one viewport, one coherent access pattern)
- is **logically single-threaded** from the user's perspective (one conceptual access stream)
- **internally supports concurrent threads** (User thread + Intent processing loop + Rebalance execution loop)
- is **designed for concurrent reads** (User Path is read-only, safe for repeated calls)
- enforces **single-writer** for all mutations (Rebalance Execution only)

**Important Distinction:**
- **User-facing model**: One logical consumer per cache (coherent access pattern from one source)
- **Internal implementation**: Multiple threads operate concurrently within the cache pipeline
- WindowCache **IS thread-safe** for its internal concurrency (user thread + background threads)
- WindowCache is **NOT designed for multiple users sharing one cache instance** (violates coherent access pattern)

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
2. **User Path** publishes intent with delivered data (**synchronously in user thread** — lightweight signal only)
3. **Intent processing loop** (background) wakes on semaphore signal, reads latest intent via `Interlocked.Exchange`
4. **Rebalance Decision Engine** validates rebalance necessity through multi-stage analytical pipeline (**in background intent loop — CPU-only, side-effect free, lightweight**)
5. **Work avoidance**: Rebalance skipped if validation determines it's unnecessary (NoRebalanceRange containment, Desired==Current, pending rebalance coverage) — **all happens in background intent loop before scheduling**
6. **Scheduling**: if execution required, cancels prior execution request and publishes new one (**in background intent loop**)
7. **Background execution** (rebalance loop): debounce delay + actual rebalance I/O operations
8. **Debounce delay** controls convergence timing and prevents thrashing (background)
9. **User correctness** never depends on cache state being up-to-date

**Key insight:** User always receives correct data, regardless of whether cache has converged yet.

**"Smart" characteristic:** The system avoids unnecessary work through multi-stage validation rather than blindly executing every intent. This prevents thrashing, reduces redundant I/O, and maintains stability under rapidly changing access patterns while ensuring eventual convergence to optimal configuration.

**Critical Architectural Detail - Intent Processing is in Background Loop:**

The decision logic (multi-stage validation) and scheduling execute in a **dedicated background intent processing loop** (`IntentController.ProcessIntentsAsync`), NOT synchronously in the user thread. The user thread only performs a lightweight `Interlocked.Exchange` + semaphore release when publishing an intent, then returns immediately.

This design is intentional and critical for handling user request bursts:
- ✅ **User thread returns immediately** after publishing intent (signal only)
- ✅ **CPU-only validation** in background loop (math, conditions, no I/O)
- ✅ **Side-effect free** decision — just calculations
- ✅ **Lightweight** — completes in microseconds
- ✅ **Prevents intent thrashing** — validates necessity before scheduling, skips if not needed
- ✅ **Latest-wins** — `Interlocked.Exchange` ensures only the most recent intent is acted upon
- ⚠️ Only actual **I/O operations** (data fetching, cache mutation) happen in the rebalance execution loop

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

**Mechanism**: `AsyncActivityCounter` — TCS-based lock-free idle detection

`AsyncActivityCounter` tracks all in-flight activity (user requests + background loops). When the counter reaches zero, the current `TaskCompletionSource` is completed, unblocking all waiters:

```
WaitForIdleAsync():
  1. Volatile.Read(_idleTcs) → observe current TCS
  2. await observedTcs.Task → wait for idle signal
  3. (Re-entry prevention handled by TCS completion semantics)
```

- Guarantees: System **was idle at some point** when method returns (eventual consistency semantics)
- Safety: Lock-free — uses only `Interlocked` and `Volatile` operations; no deadlocks
- Multiple waiters supported: all await the same TCS
- See "AsyncActivityCounter - Lock-Free Idle Detection" section for full architecture details

### Use Cases

- **Test stabilization**: Ensure cache has converged before assertions
- **Integration testing**: Synchronize with background work completion
- **Diagnostic scenarios**: Verify rebalance execution finished

### Architectural Preservation

This synchronization mechanism does **not** alter actor responsibilities:

- `UserRequestHandler` remains sole intent publisher
- `IntentController` remains lifecycle authority for intent cancellation
- `IRebalanceExecutionController` remains execution authority
- `WindowCache` remains pure facade

Method exists only to expose idle synchronization through public API for testing purposes.

### Lock-Free Implementation

The system uses lock-free synchronization throughout:

**IntentController** - Lock-free intent management:
- **No locks, no `lock` statements, no mutexes**
- `_pendingIntent` field updated via `Interlocked.Exchange` — atomic latest-wins semantics
- Prior intent replaced atomically; no `Volatile.Read/Write` loop needed
- `SemaphoreSlim` used as lightweight signal for background processing loop
- Thread-safe without blocking — guaranteed progress
- Zero contention overhead

**AsyncActivityCounter** - Lock-free idle detection:
- **Fully lock-free**: Uses only `Interlocked` and `Volatile` operations
- `Interlocked.Increment/Decrement` for atomic counter operations
- `Volatile.Write/Read` for TaskCompletionSource reference with proper memory barriers
- State-based completion primitive (TaskCompletionSource, not event-based like SemaphoreSlim)
- Multiple awaiter support without coordination overhead
- See "AsyncActivityCounter - Lock-Free Idle Detection" section for detailed architecture

**Safe Visibility Pattern:**
```csharp
// IntentController - Interlocked.Exchange for atomic intent replacement (latest-wins)
var previousIntent = Interlocked.Exchange(ref _pendingIntent, newIntent);
// (previousIntent is superseded; background loop picks up newIntent via another Exchange)

// AsyncActivityCounter - Volatile + Interlocked for idle detection
var newCount = Interlocked.Increment(ref _activityCount);  // Atomic counter
Volatile.Write(ref _idleTcs, newTcs);  // Publish TCS with release fence
var tcs = Volatile.Read(ref _idleTcs);  // Observe TCS with acquire fence
```

**Testing Coverage:**
- Lock-free behavior validated by `ConcurrencyStabilityTests`
- Tested under concurrent load (100+ simultaneous operations)
- No deadlocks, no race conditions, no data corruption observed

This lightweight synchronization approach using `Volatile` and `Interlocked` operations ensures thread-safety without the overhead and complexity of traditional locking mechanisms.

### Relation to Concurrency Model

The `AsyncActivityCounter` idle detection:
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

### AsyncActivityCounter - Lock-Free Idle Detection

**Purpose:**
`AsyncActivityCounter` provides lock-free, thread-safe idle state detection for background operations. It tracks active work (intent processing, rebalance execution) and provides an awaitable notification when all work completes.

**Architecture:**
- **Fully lock-free**: Uses only `Interlocked` and `Volatile` operations
- **State-based semantics**: TaskCompletionSource provides persistent idle state (not event-based)
- **Multiple awaiter support**: All threads awaiting idle state complete when signaled
- **Eventual consistency**: "Was idle at some point" semantics (not "is idle now")

**Implementation Details:**

```csharp
// Activity counter - atomic operations via Interlocked
private int _activityCount;

// TaskCompletionSource - published/observed via Volatile operations
private TaskCompletionSource<bool> _idleTcs;
```

**Thread-Safety Model:**
- **IncrementActivity()**: `Interlocked.Increment` + `Volatile.Write` on 0→1 transition
- **DecrementActivity()**: `Interlocked.Decrement` + `Volatile.Read` + `TrySetResult` on N→0 transition
- **WaitForIdleAsync()**: `Volatile.Read` snapshot + `Task.WaitAsync()` for cancellation

**Memory Barriers:**
- `Volatile.Write` (release fence): Publishes fully-constructed TCS on 0→1 transition
- `Volatile.Read` (acquire fence): Observes published TCS on N→0 transition and in WaitForIdleAsync
- Ensures proper happens-before relationship: TCS construction visible before reference read

**Why TaskCompletionSource (Not SemaphoreSlim):**
| Primitive            | Semantics      | Idle State Behavior                                | Correct? |
|----------------------|----------------|----------------------------------------------------|----------|
| TaskCompletionSource | State-based    | All awaiters observe persistent idle state         | ✅ Yes   |
| SemaphoreSlim        | Event/token    | First awaiter consumes release, others block       | ❌ No    |

Idle detection requires state-based semantics: when system becomes idle, ALL current and future awaiters (until next busy period) should complete immediately. TCS provides this; SemaphoreSlim does not.

**Usage Pattern:**

```csharp
// Intent processing loop
try
{
    _activityCounter.IncrementActivity();  // Start work
    await ProcessIntentAsync(intent);
}
finally
{
    _activityCounter.DecrementActivity();  // End work (even on exception)
}

// Test or disposal wait for idle
await _activityCounter.WaitForIdleAsync(cancellationToken);  // Complete when system idle
```

**Idle State Semantics - "Was Idle" NOT "Is Idle":**

WaitForIdleAsync completes when the system **was idle at some point in time**. It does NOT guarantee the system is still idle after completion. This is correct behavior for eventual consistency models.

**Example Race (Correct Behavior):**
1. T1 decrements to 0, signals TCS_old (idle state achieved)
2. T2 increments to 1, creates TCS_new (new busy period starts)
3. T3 calls WaitForIdleAsync, reads TCS_old (already completed)
4. Result: WaitForIdleAsync completes immediately even though count=1

This is **not a bug** - the system WAS idle between steps 1 and 2. Callers requiring stronger guarantees must implement application-specific logic (e.g., re-check state after await).

**Call Sites:**
- **IntentController.PublishIntent()**: IncrementActivity when publishing intent
- **IntentController.ProcessIntentsAsync()**: DecrementActivity in finally block after processing
- **Execution controllers**: IncrementActivity on enqueue, DecrementActivity in finally after execution
- **WindowCache.WaitForIdleAsync()**: Exposes idle detection via public API for testing

**Disposal and AsyncActivityCounter:**

**Disposal does NOT use AsyncActivityCounter** - it directly waits for background loops to exit via `Task.Wait()` on the loop tasks. This ensures disposal completes even if counter state is inconsistent (e.g., leaked increment without matching decrement).

---

## What Is Supported

- Single logical consumer per cache instance (coherent access pattern)
- Single-writer architecture (Rebalance Execution only)
- Read-only User Path (safe for repeated calls from same consumer)
- **Internal concurrent threads** (user thread + intent processing loop + rebalance execution loop)
- **Thread-safe internal pipeline** (lock-free synchronization via Volatile/Interlocked)
- Background asynchronous rebalance
- Cancellation and debouncing of rebalance execution
- High-frequency access from one logical consumer
- Eventual consistency model (cache converges asynchronously)
- Intent-based data delivery (delivered data in intent avoids duplicate fetches)
- **Graceful disposal with resource cleanup** (lock-free, idempotent, concurrent-safe)
- **Background task coordination during disposal** (wait for loops to exit gracefully)

---

## What Is Explicitly Not Supported

- Multiple concurrent consumers per cache instance (multiple users sharing one cache)
- Multiple logical access patterns per cache instance (cross-user sliding window arbitration)
- User threads calling WindowCache methods concurrently from different logical consumers

**Note:** Internal concurrency (user thread + background threads within single cache) IS supported.
What is NOT supported is multiple users/consumers sharing the same cache instance.

---

## Design Philosophy

This library prioritizes:
- conceptual clarity
- predictable behavior
- cache efficiency
- correctness of temporal and spatial logic

Instead of providing superficial thread safety,
it enforces a model that remains stable, explainable, and performant.
