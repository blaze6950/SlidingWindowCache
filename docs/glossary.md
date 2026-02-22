# Glossary

This document provides canonical definitions for technical terms used throughout the SlidingWindowCache project. All documentation should reference these definitions to maintain consistency.

---

## Before You Read

**This glossary is a reference, not a tutorial.** Definitions are intentionally concise and assume you've read foundational documentation.

**Recommended Learning Path:**

1. **Start here** → [README.md](../README.md) - Overview, quick start, basic examples
2. **Architecture fundamentals** → [Architecture Model](architecture-model.md) - Threading, single-writer, decision-driven execution
3. **Dive deeper** → [Invariants](invariants.md) - System guarantees and constraints
4. **Implementation details** → [Component Map](component-map.md) - Component catalog with source references

**Using this glossary:**
- Terms link to detailed docs where applicable (click through for full context)
- Grouped by category for faster lookup
- Cross-referenced heavily - follow links for related concepts

---

## Core Concepts

### Cache
In-memory storage of contiguous range data. No gaps allowed ([Invariants B](invariants.md#b-cache-state--consistency-invariants)).

### Range
Interval with start/end boundaries. Uses `Intervals.NET` library.

### Range Domain
Mathematical domain for range operations. Must implement `IRangeDomain<TRange>`. Examples: `IntegerRangeDomain`, `DateTimeRangeDomain`.

---

## Range Types

**Requested Range**: User requests in `GetDataAsync()`.  
**Current Cache Range**: Currently stored (`CacheState.Cache.Range`).  
**Desired Cache Range**: Target computed by `ProportionalRangePlanner`. See [Component Map](component-map.md#desired-range-computation).  
**Available Range**: Intersection of Requested ∩ Current (immediately returnable).  
**Missing Range**: Requested \ Current (must fetch).  
**NoRebalanceRange**: Stability zone. Requests within skip rebalancing. See [Architecture Model](architecture-model.md#burst-resistance).

---

## Architectural Patterns

### Single-Writer Architecture
Only ONE component (`RebalanceExecutor`) mutates shared state (Cache, LastRequested, NoRebalanceRange). All others read-only. Eliminates write-write conflicts. See [Architecture Model](architecture-model.md#single-writer-architecture) | [Component Map - Implementation](component-map.md#single-writer-architecture).

### Decision-Driven Execution
Multi-stage validation pipeline separating decisions from execution. `RebalanceDecisionEngine` is sole authority for rebalance necessity. Execution proceeds only if all stages pass. Prevents thrashing. See [Architecture Model](architecture-model.md#decision-driven-execution) | [Invariants D.29](invariants.md#d-rebalance-decision-path-invariants).

### Smart Eventual Consistency
Cache converges to optimal state without blocking user requests. May temporarily serve from non-optimal range, rebalancing in background. See [Architecture Model - Consistency](architecture-model.md#smart-eventual-consistency-model).

### Burst Resistance
Handles rapid request sequences without thrashing. Achieved via "latest intent wins" and NoRebalanceRange stability zones. See [Architecture Model](architecture-model.md#burst-resistance).

---

## Components & Actors

### WindowCache
Public API facade. Exposes `GetDataAsync()`.

### UserRequestHandler
Handles user requests on user thread. Assembles data, publishes intents. Never mutates cache ([Invariants A.7-A.8](invariants.md#a-user-path--fast-user-access-invariants)).

### IntentController
Manages rebalance intent lifecycle. Evaluates `RebalanceDecisionEngine`, coordinates execution. Single-threaded background loop. See [Component Map](component-map.md#intentcontroller).

### RebalanceDecisionEngine
Sole authority for rebalance necessity. 5-stage validation pipeline. Pure, deterministic, side-effect free. See [Invariants D.25-D.29](invariants.md#d-rebalance-decision-path-invariants).

### RebalanceExecutionController
Serializes/debounces executions. Implementations: `TaskBasedRebalanceExecutionController` (default), `ChannelBasedRebalanceExecutionController`. See [Component Map](component-map.md#rebalanceexecutioncontroller).

### RebalanceExecutor
Performs cache mutations. Fetches, merges, trims, updates state. Only mutator ([Invariant F.36](invariants.md#f-rebalance-execution-invariants)).

### CacheDataExtensionService
Extends cache by fetching missing ranges, merging. See [Component Map - Incremental Fetching](component-map.md#incremental-data-fetching).

### AsyncActivityCounter
Lock-free activity counter. Awaitable idle state. Tracks operations, signals "was idle". See [Invariants H.47-H.48](invariants.md#h-activity-tracking--idle-detection-invariants).

---

## Operations & Processes

### Intent
Signal containing requested range + delivered data. Published by `UserRequestHandler` for rebalance evaluation. Signals, not commands (may be skipped). "Latest wins" - newer replaces older atomically. See [Invariants C.17-C.24](invariants.md#c-intent--rebalance-lifecycle-invariants).

### Rebalance
Background process adjusting cache to desired range. Phases: (1) Decision (5-stage), (2) Execution (fetch/merge/trim), (3) Mutation (atomic). See [Architecture Model](architecture-model.md#rebalance-lifecycle).

### User Path
Handles user requests. Runs on user thread until intent published. Read-only. See [Invariants A.7-A.9](invariants.md#a-user-path--fast-user-access-invariants).

### Background Path
Rebalance processing. Runs on background threads (IntentController, RebalanceExecutionController, RebalanceExecutor). See [Architecture Model](architecture-model.md#execution-contexts).

### Debouncing
Delays execution (e.g., 100ms) to let bursts settle. Cancels previous if new scheduled during window. Prevents thrashing.

---

## Concurrency & State

**Activity**: Operation tracked by `AsyncActivityCounter`. System idle when count = 0.  
**Idle State**: No intents/rebalances executing. **"Was Idle" NOT "Is Idle"** - `WaitForIdleAsync()` = was idle at some point. See [Invariants H.49](invariants.md#h-activity-tracking--idle-detection-invariants).  
**Stabilization**: Reaching stable state (rebalances done, cache = desired, no pending intents). Not persistent.  
**Cache State**: Mutable container (`Cache`, `LastRequested`, `NoRebalanceRange`). Only mutated by `RebalanceExecutor`. See [Invariant F.36](invariants.md#f-rebalance-execution-invariants).  
**Execution Request**: Rebalance request from `IntentController` → `RebalanceExecutionController`. Contains desired ranges, intent data, cancellation token.

---

## Concurrency Primitives

**Volatile Read/Write**: Memory barriers. `Write` = release fence, `Read` = acquire fence. Lock-free publishing.  
**Interlocked Ops**: Atomic operations (`Increment`, `Decrement`, `Exchange`, `CompareExchange`).  
**Acquire-Release**: Memory ordering. Writes before "release" visible after "acquire". See [Architecture Model](architecture-model.md#memory-model).

---

## Testing & Diagnostics

**WaitForIdleAsync**: Returns `Task` when "was idle at some point". For testing convergence. NOT guaranteed still idle. See [Invariants - Testing](invariants.md#testing-infrastructure-deterministic-synchronization).  
**Cache Diagnostics**: Instrumentation interface (`ICacheDiagnostics`). Emits events for requests, decisions, completions, failures. See [Diagnostics](diagnostics.md).

---

## Invariants

**Architectural**: System truths that ALWAYS hold (Cache Contiguity, Single-Writer, User Path Priority). See [Invariants](invariants.md).  
**Behavioral**: Expected behaviors, testable via public API. See [Invariants - Behavioral](invariants.md#understanding-this-document).  
**Conceptual**: Design principles. See [Invariants - Conceptual](invariants.md#understanding-this-document).

---

## Configuration

**Window Size**: Total cache size (domain elements).  
**Left/Right Split**: Proportional division vs request. Example: 30%/70%.  
**Threshold %**: NoRebalanceRange zone. Example: 10% = skip if within 10% of boundary.  
**Debounce Delay**: Execution delay (e.g., 100ms). Settles bursts.  
**Storage Strategy**: **Snapshot** (immutable, WebAssembly-safe) or **CopyOnRead** (memory-efficient). See [Storage Strategies](storage-strategies.md).

---

## Common Misconceptions

**Intent vs Command**: Intents are signals (evaluation may skip), not commands (guaranteed execution).  
**Async Rebalancing**: `GetDataAsync` returns immediately, rebalancing happens in background.  
**"Was Idle" Semantics**: `WaitForIdleAsync` guarantees system was idle at some point, not still idle after.  
**NoRebalanceRange**: Stability zone around cache (may differ from actual cache range).

---

## Related Documentation

[README](../README.md) | [Architecture Model](architecture-model.md) | [Invariants](invariants.md) | [Component Map](component-map.md) | [Actor Responsibilities](actors-and-responsibilities.md) | [Scenarios](scenario-model.md) | [State Machine](cache-state-machine.md) | [Storage Strategies](storage-strategies.md) | [Diagnostics](diagnostics.md)
