# Eviction — VisitedPlaces Cache

This document describes the eviction architecture of `VisitedPlacesCache`: how capacity limits are defined, how eviction is triggered, and how eviction candidates are selected and removed.

For the surrounding execution context, see `docs/visited-places/scenarios.md` (Section III). For formal invariants, see `docs/visited-places/invariants.md` (Section VPC.E).

---

## Overview

VPC eviction is a **constraint satisfaction** system with five decoupled components:

| Component                     | Role                     | Question answered                                                         |
|-------------------------------|--------------------------|---------------------------------------------------------------------------|
| **Eviction Policy**           | Constraint evaluator     | "Is my constraint currently violated?"                                    |
| **Eviction Pressure**         | Constraint tracker       | "Is the constraint still violated after removing this segment?"           |
| **Eviction Selector**         | Candidate sampler        | "Which candidate is the worst in a random sample?"                        |
| **Eviction Engine**           | Eviction facade          | Orchestrates selector, evaluator, and executor; owns eviction diagnostics |
| **Eviction Policy Evaluator** | Policy lifecycle manager | Maintains stateful policy aggregates; constructs composite pressure       |

The **Eviction Engine** mediates all interactions between these components. `CacheNormalizationExecutor` depends only on the engine — it has no direct reference to the evaluator, selector, or executor.

### Execution Flow

```
CacheNormalizationExecutor
  │
  ├─ engine.UpdateMetadata(usedSegments)
  │    └─ selector.UpdateMetadata(...)
  │
  ├─ storage.TryAdd(segment)                           ← processor is sole storage writer
  ├─ engine.InitializeSegment(segment)
  │    ├─ selector.InitializeMetadata(...)
  │    └─ evaluator.OnSegmentAdded(...)
  │
  ├─ engine.EvaluateAndExecute(allSegments, justStored)
  │    ├─ evaluator.Evaluate(allSegments)  →  pressure
  │    │    └─ each policy.Evaluate(...)  (O(1) via running aggregates)
  │    └─ [if pressure.IsExceeded]
  │         executor.Execute(pressure, allSegments, justStored)
  │              └─ selector.TrySelectCandidate(...)  [loop until satisfied]
  │
  ├─ [for each toRemove]: storage.TryRemove(segment)   ← processor is sole storage writer
  └─ engine.OnSegmentRemoved(segment)  per removed segment
       └─ evaluator.OnSegmentRemoved(...)  per segment
```

---

## Component 1 — Eviction Policy (`IEvictionPolicy`)

### Purpose

An Eviction Policy answers a single question after every storage step: **"Does the current state of `CachedSegments` violate my configured constraint?"**

If yes, it produces an `IEvictionPressure` that tracks constraint satisfaction as segments are removed. If no, it returns `NoPressure<TRange,TData>.Instance` (a singleton with `IsExceeded = false`).

### Architectural Constraints

Policies must NOT:
- Know about eviction strategy (selector sampling order)
- Estimate how many segments to remove
- Make assumptions about which segments will be removed

### Multiple Policies

Multiple Policies may be active simultaneously. Eviction is triggered when **ANY** Policy produces an exceeded pressure (OR semantics). All Policies are checked after every storage step. If two Policies produce exceeded pressures, they are combined into a `CompositePressure` and the executor satisfies all constraints in a single pass.

### Built-in Policies

#### MaxSegmentCountPolicy

Fires when the total number of segments in `CachedSegments` exceeds a configured limit.

```
Fires when: CachedSegments.Count > MaxCount
Produces:   SegmentCountPressure (nested in MaxSegmentCountPolicy, count-based, order-independent)
```

**Configuration parameter**: `maxCount: int` (must be >= 1)

**Use case**: Controlling memory usage when all segments are approximately the same size, or when the absolute number of cache entries is the primary concern.

**Note**: Count-based eviction is order-independent — removing any segment equally satisfies the constraint by decrementing the count by 1. This policy tracks segment count via `Interlocked.Increment`/`Decrement` in `OnSegmentAdded`/`OnSegmentRemoved`, keeping `Evaluate` at O(1).

#### MaxTotalSpanPolicy

Fires when the sum of all segment spans (total coverage width) exceeds a configured limit.

```
Fires when: sum(S.Range.Span(domain) for S in CachedSegments) > MaxTotalSpan
Produces:   TotalSpanPressure (nested in MaxTotalSpanPolicy, span-aware, order-dependent satisfaction)
```

**Configuration parameter**: `maxTotalSpan: TRange` (domain-specific span unit)

**Use case**: Controlling the total domain coverage cached, regardless of how many segments it is split into. More meaningful than segment count when segments vary significantly in span.

**Design note**: `MaxTotalSpanPolicy` implements `IEvictionPolicy` — it maintains a running total span aggregate updated via `OnSegmentAdded`/`OnSegmentRemoved`. This keeps its `Evaluate` at O(1) rather than requiring an O(N) re-scan of all segments. The `TotalSpanPressure` it produces tracks actual span reduction as segments are removed, guaranteeing correctness regardless of selector order.

#### MaxMemoryPolicy (planned)

Fires when the estimated total memory used by all segment data exceeds a configured limit.

```
Fires when: sum(S.Data.Length * sizeof(TData) for S in CachedSegments) > MaxBytes
Produces:   MemoryPressure (byte-aware)
```

**Configuration parameter**: `maxBytes: long`

**Use case**: Direct memory budget enforcement.

---

## Component 2 — Eviction Pressure (`IEvictionPressure`)

### Purpose

A Pressure object tracks whether a constraint is still violated as the executor removes segments one by one. It provides:

- `IsExceeded` — `true` while the constraint remains violated; `false` once satisfied
- `Reduce(segment)` — called by the executor after each candidate is selected; updates internal tracking

### Pressure Implementations

| Type                                         | Visibility        | Produced by                 | `Reduce` behavior                              |
|----------------------------------------------|-------------------|-----------------------------|------------------------------------------------|
| `NoPressure`                                 | public            | All policies (no violation) | No-op (singleton, `IsExceeded` always `false`) |
| `MaxSegmentCountPolicy.SegmentCountPressure` | internal (nested) | `MaxSegmentCountPolicy`     | Decrements current count by 1                  |
| `MaxTotalSpanPolicy.TotalSpanPressure`       | internal (nested) | `MaxTotalSpanPolicy`        | Subtracts removed segment's span from total    |
| `CompositePressure`                          | internal          | `EvictionPolicyEvaluator`   | Calls `Reduce` on all child pressures          |

### CompositePressure

When multiple policies produce exceeded pressures, the `EvictionPolicyEvaluator` wraps them in a `CompositePressure`:
- `IsExceeded = any child.IsExceeded` (OR semantics)
- `Reduce(segment)` calls `Reduce` on all children

When only a single policy is exceeded, its pressure is used directly (no composite wrapping) to avoid unnecessary allocation.

---

## Component 3 — Eviction Selector (`IEvictionSelector`)

### Purpose

An Eviction Selector **selects the single worst eviction candidate** from a random sample of segments, **owns the per-segment metadata** required to implement that strategy, and is responsible for creating and updating that metadata.

It does NOT decide how many segments to remove or whether to evict at all — those are the pressure's and policy's responsibilities. It does NOT pre-filter candidates for immunity — it skips immune segments inline during sampling.

### Sampling Contract

Rather than sorting all segments (O(N log N)), selectors use **random sampling**: they randomly examine a fixed number of segments (O(SampleSize), controlled by `EvictionSamplingOptions.SampleSize`) and return the worst candidate found in that sample. This keeps eviction cost at O(SampleSize) regardless of total cache size.

The core selector API is:

```csharp
bool TrySelectCandidate(
    IReadOnlySet<CachedSegment<TRange, TData>> immuneSegments,
    out CachedSegment<TRange, TData> candidate);
```

The selector obtains segments from the `ISegmentStorage` instance injected at initialization (via `IStorageAwareEvictionSelector.Initialize`), not from a parameter. This keeps the public API clean and avoids exposing storage internals to callers.

Returns `true` and sets `candidate` if an eligible candidate was found; returns `false` if no eligible candidate exists (all immune or pool exhausted).

### Immunity Collaboration

Immunity filtering is a **collaboration** between the `EvictionExecutor` and the `IEvictionSelector`:

- The executor builds and maintains the immune `HashSet<CachedSegment>` (seeded with just-stored segments; extended with each selected candidate).
- The selector receives the immune set and skips immune segments inline during sampling — no separate pre-filtering pass.

This avoids an O(N) allocation for an eligible-candidates list and keeps eviction cost at O(SampleSize).

### Metadata Ownership

Each selector defines its own metadata type (a nested `internal sealed class` implementing `IEvictionMetadata`) and stores it on `CachedSegment.EvictionMetadata`. The `EvictionEngine` delegates:

- `engine.InitializeSegment(segment)` → `selector.InitializeMetadata(segment)` — immediately after each segment is stored
- `engine.UpdateMetadata(usedSegments)` → `selector.UpdateMetadata(usedSegments)` — at the start of each event cycle for segments accessed by the User Path

### `SamplingEvictionSelector` Base Class

All built-in selectors extend `SamplingEvictionSelector<TRange, TData>` (a `public abstract` class), which implements `TrySelectCandidate` and provides two extension points for derived classes:

- **`EnsureMetadata(segment)`** — Called inside the sampling loop **before every `IsWorse` comparison**. If the segment's metadata is null or belongs to a different selector type, this method creates and attaches the correct metadata. Repaired metadata persists permanently on the segment; future sampling passes skip the repair.
- **`IsWorse(candidate, current)`** — Pure comparison of two segments with guaranteed-valid metadata. Implementations can safely cast `segment.EvictionMetadata` without null checks or type-mismatch guards because `EnsureMetadata` has already run on both segments.

**`TimeProvider` injection:** `SamplingEvictionSelector` accepts an optional `TimeProvider` (defaulting to `TimeProvider.System`). Time-aware selectors (LRU, FIFO) use `TimeProvider.GetUtcNow().UtcDateTime` internally; time-agnostic selectors (SmallestFirst) ignore it entirely.

**Timestamp nuance during metadata repair:** When `EnsureMetadata` creates metadata for a segment that was stored before the current selector was configured (e.g., after a selector switch at runtime), each repaired segment receives a per-call timestamp from `TimeProvider`. These timestamps may differ by microseconds across segments in the same sampling pass. This is acceptable: among segments repaired in the same pass, selection order is determined by random sampling, not by these micro-differences. The tiny spread creates no meaningful bias in eviction decisions.

### Architectural Constraints

Selectors must NOT:
- Know about eviction policies or constraints
- Decide when or whether to evict
- Sort or scan the entire segment collection (O(SampleSize) only)

### Built-in Selectors

#### LruEvictionSelector — Least Recently Used

**Selects the worst candidate (by `LruMetadata.LastAccessedAt`) from a random sample** — the least recently accessed segment in the sample is the candidate.

- Metadata type: `LruEvictionSelector<TRange,TData>.LruMetadata` with field `DateTime LastAccessedAt`
- `InitializeMetadata`: creates `LruMetadata` with `LastAccessedAt = TimeProvider.GetUtcNow().UtcDateTime`
- `UpdateMetadata`: sets `meta.LastAccessedAt = TimeProvider.GetUtcNow().UtcDateTime` on each used segment
- `EnsureMetadata`: repairs missing or stale metadata using the current `TimeProvider` timestamp
- `TrySelectCandidate`: samples O(SampleSize) segments (skipping immune), returns the one with the smallest `LastAccessedAt`
- Optimizes for temporal locality: segments accessed recently are retained
- Best for workloads where re-access probability correlates with recency

**Example**: Sampling `S1(t=5), S2(t=1), S3(t=8)` with no immunity:
- Worst in sample: `S2(t=1)` → selected as candidate

#### FifoEvictionSelector — First In, First Out

**Selects the worst candidate (by `FifoMetadata.CreatedAt`) from a random sample** — the oldest segment in the sample is the candidate.

- Metadata type: `FifoEvictionSelector<TRange,TData>.FifoMetadata` with field `DateTime CreatedAt`
- `InitializeMetadata`: creates `FifoMetadata` with `CreatedAt = TimeProvider.GetUtcNow().UtcDateTime` (immutable after creation)
- `UpdateMetadata`: no-op — FIFO ignores access patterns
- `EnsureMetadata`: repairs missing or stale metadata using the current `TimeProvider` timestamp
- `TrySelectCandidate`: samples O(SampleSize) segments (skipping immune), returns the one with the smallest `CreatedAt`
- Treats the cache as a fixed-size sliding window over time
- Does not reflect access patterns; simpler and more predictable than LRU
- Best for workloads where all segments have similar re-access probability

#### SmallestFirstEvictionSelector — Smallest Span First

**Selects the worst candidate (by span) from a random sample** — the narrowest segment in the sample is the candidate.

- Metadata type: `SmallestFirstEvictionSelector<TRange,TData,TDomain>.SmallestFirstMetadata` with field `long Span`
- `InitializeMetadata`: creates `SmallestFirstMetadata` with `Span = segment.Range.Span(domain).Value`
- `UpdateMetadata`: no-op — span is immutable after creation
- `EnsureMetadata`: repairs missing or stale metadata by recomputing `Span` from `segment.Range.Span(domain).Value`
- `TrySelectCandidate`: samples O(SampleSize) segments (skipping immune), returns the one with the smallest `Span`
- Optimizes for total domain coverage: retains large (wide) segments over small ones
- Best for workloads where wide segments are more valuable
- Captures `TDomain` internally for span computation; does not use `TimeProvider`
- **Non-finite span fallback:** If `segment.Range.Span(domain)` is not finite, a span of `0` is stored as a safe fallback — the segment will be treated as the worst eviction candidate (smallest span)

#### Farthest-From-Access (planned)

**Selects candidates by distance from the most recently accessed range** — farthest segments first.

- Spatial analogue of LRU: retains segments near the current access pattern

#### Oldest-First (planned)

**Selects candidates by a hybrid of age and access frequency** — old, neglected segments first.

---

## Eviction Executor

The Eviction Executor is an **internal component of the Eviction Engine**. It executes the constraint satisfaction loop by repeatedly calling the selector until all pressures are satisfied or no eligible candidates remain.

### Execution Flow

```
1. Build immune HashSet from justStoredSegments (Invariant VPC.E.3)
2. Loop while pressure.IsExceeded:
   a. selector.TrySelectCandidate(immune, out candidate)
      → returns false if no eligible candidates remain → break
   b. toRemove.Add(candidate)
   c. immune.Add(candidate)     ← prevents re-selecting same segment
   d. pressure.Reduce(candidate)
3. Return toRemove list to EvictionEngine (and then to processor for storage removal)
```

### Key Properties

- The executor has **no reference to `ISegmentStorage`** — it returns a list; the processor removes from storage.
- The executor fires **no diagnostics** — diagnostics are fired by `EvictionEngine.EvaluateAndExecute`.
- The executor relies on **pressure objects for termination** — it does not know in advance how many segments to remove.
- The immune set is passed to the selector per call; the selector skips immune segments during sampling.

### Just-Stored Segment Immunity

The just-stored segments are **always excluded** from the candidate set. The executor seeds the immune set from `justStoredSegments` before the loop begins (Invariant VPC.E.3).

---

## Eviction Engine

The Eviction Engine (`EvictionEngine<TRange, TData>`) is the **single eviction facade** exposed to `CacheNormalizationExecutor`. It encapsulates the `EvictionPolicyEvaluator`, `EvictionExecutor`, and `IEvictionSelector` — the executor has no direct reference to any of these.

### Responsibilities

- Delegates selector metadata operations (`UpdateMetadata`, `InitializeSegment`) to `IEvictionSelector`.
- Notifies the `EvictionPolicyEvaluator` of segment lifecycle events via `InitializeSegment` and `OnSegmentRemoved`, keeping stateful policy aggregates consistent.
- Evaluates all policies and executes the constraint satisfaction loop via `EvaluateAndExecute`. Returns the list of segments the processor must remove from storage.
- Fires eviction-specific diagnostics internally.

### API

| Method                                                | Delegates to                                                                                                 | Called in                                |
|-------------------------------------------------------|--------------------------------------------------------------------------------------------------------------|------------------------------------------|
| `UpdateMetadata(usedSegments)`                        | `selector.UpdateMetadata`                                                                                    | Step 1                                   |
| `InitializeSegment(segment)`                          | `selector.InitializeMetadata` + `evaluator.OnSegmentAdded`                                                   | Step 2 (per segment)                     |
| `EvaluateAndExecute(allSegments, justStoredSegments)` | `evaluator.Evaluate` → if exceeded: `executor.Execute` → returns to-remove list + fires eviction diagnostics | Step 3+4                                 |
| `OnSegmentRemoved(segment)`                           | `evaluator.OnSegmentRemoved(segment)`                                                                        | After processor's storage.TryRemove loop |

### Storage Ownership

The engine holds **no reference to `ISegmentStorage`**. All `storage.TryAdd` and `storage.TryRemove` calls remain exclusively in `CacheNormalizationExecutor` (Invariant VPC.A.10).

### Diagnostics Split

The engine fires eviction-specific diagnostics:
- `ICacheDiagnostics.EvictionEvaluated` — unconditionally on every `EvaluateAndExecute` call
- `ICacheDiagnostics.EvictionTriggered` — when at least one policy fires
- `ICacheDiagnostics.EvictionExecuted` — after the removal loop completes

The processor retains ownership of storage-level diagnostics (`BackgroundSegmentStored`, `BackgroundStatisticsUpdated`, etc.).

### Internal Components (hidden from processor)

- **`EvictionPolicyEvaluator<TRange, TData>`** — stateful policy lifecycle and multi-policy pressure aggregation
- **`EvictionExecutor<TRange, TData>`** — constraint satisfaction loop

---

## Eviction Policy Evaluator

`EvictionPolicyEvaluator<TRange, TData>` is an **internal component of the Eviction Engine**. It manages the full policy evaluation pipeline.

### Responsibilities

- Maintains the list of `IEvictionPolicy` instances registered at construction.
- Notifies all policies of segment lifecycle events (`OnSegmentAdded`, `OnSegmentRemoved`), enabling O(1) `Evaluate` calls via running aggregates.
- Evaluates all registered policies after each storage step and aggregates results into a single `IEvictionPressure`.
- Constructs a `CompositePressure` when multiple policies fire simultaneously; returns the single pressure directly when only one fires; returns `NoPressure<TRange,TData>.Instance` when none fire.

### Policy Lifecycle Participation

All policies implement `IEvictionPolicy<TRange, TData>`, which includes `OnSegmentAdded`,
`OnSegmentRemoved`, and `Evaluate`. Each policy maintains its own running aggregate updated
incrementally via the lifecycle methods, keeping `Evaluate` at O(1). The evaluator forwards
all `OnSegmentAdded`/`OnSegmentRemoved` calls to every registered policy.

---

## Eviction Metadata

### Overview

Per-segment eviction metadata is **owned by the Eviction Selector**, not by a shared statistics record. Each segment carries an `IEvictionMetadata? EvictionMetadata` reference. The selector that is currently configured defines, creates, updates, and interprets this metadata.

All built-in selectors use metadata. Time-aware selectors (LRU, FIFO) capture timestamps via an injected `TimeProvider`; the segment-derived selector (SmallestFirst) computes a pre-cached `Span` value.

### Selector-Specific Metadata Types

| Selector                        | Metadata Class          | Fields                    | Notes                                                        |
|---------------------------------|-------------------------|---------------------------|--------------------------------------------------------------|
| `LruEvictionSelector`           | `LruMetadata`           | `DateTime LastAccessedAt` | Updated on each `UsedSegments` entry                         |
| `FifoEvictionSelector`          | `FifoMetadata`          | `DateTime CreatedAt`      | Immutable after creation                                     |
| `SmallestFirstEvictionSelector` | `SmallestFirstMetadata` | `long Span`               | Immutable after creation; computed from `Range.Span(domain)` |

Metadata classes are nested `internal sealed` classes inside their respective selector classes.

### Ownership

Metadata is managed exclusively by the configured selector via two methods called by the `EvictionEngine` (which in turn is called by `CacheNormalizationExecutor`):

- `InitializeMetadata(segment)` — called immediately after each segment is stored (step 2); selector attaches its metadata to `segment.EvictionMetadata`; time-aware selectors obtain the current timestamp from their injected `TimeProvider`
- `UpdateMetadata(usedSegments)` — called at the start of each event cycle for segments accessed by the User Path (step 1); selector updates its metadata on each used segment

If a selector encounters metadata from a previously-configured selector (runtime selector switching), `EnsureMetadata` replaces it with the correct type during the next sampling pass:

```csharp
if (segment.EvictionMetadata is not LruMetadata meta)
{
    meta = new LruMetadata(TimeProvider.GetUtcNow().UtcDateTime);
    segment.EvictionMetadata = meta;
}
```

### Lifecycle

```
Segment stored (Background Path, step 2):
  engine.InitializeSegment(segment)
    → selector.InitializeMetadata(segment)
      → e.g., LruMetadata { LastAccessedAt = TimeProvider.GetUtcNow().UtcDateTime }
      → e.g., FifoMetadata { CreatedAt = TimeProvider.GetUtcNow().UtcDateTime }
      → e.g., SmallestFirstMetadata { Span = segment.Range.Span(domain).Value }

Segment used (CacheNormalizationRequest.UsedSegments, Background Path, step 1):
  engine.UpdateMetadata(usedSegments)
    → selector.UpdateMetadata(usedSegments)
      → e.g., LruMetadata.LastAccessedAt = TimeProvider.GetUtcNow().UtcDateTime
      → no-op for Fifo, SmallestFirst

Segment sampled during eviction (Background Path, step 3):
  SamplingEvictionSelector.TrySelectCandidate — sampling loop
    → EnsureMetadata(segment)  ← repairs null/stale metadata if needed (persists permanently)
    → IsWorse(candidate, current)  ← pure comparison; metadata guaranteed valid

Segment evicted (Background Path, step 4):
  segment removed from storage; metadata reference is GC'd with the segment
```

---

## Eviction and Storage: Interaction

Eviction never happens in isolation — it is always the tail of a storage step in background event processing. For the complete four-step background sequence see `docs/visited-places/architecture.md` — Threading Model, Context 2. Eviction occupies steps 3 and 4:

```
... (Steps 1–2: metadata update + storage — see architecture.md)
   |
Step 3+4: EvaluateAndExecute                     (EvictionEngine)
   |        → evaluator.Evaluate(allSegments)     ← Only if step 2 ran (FetchedData != null)
   |          → [if pressure.IsExceeded]
   |            executor.Execute(...)
   |              → selector.TrySelectCandidate(...)  [loop until pressure satisfied]
   |        Returns: toRemove list
   |
Step 4 (storage): TryRemove evicted segments      (CacheNormalizationExecutor, sole storage writer)
   |      + engine.OnSegmentRemoved(segment) per removed segment
   |        → evaluator.OnSegmentRemoved(...)  per segment
```

Steps 3 and 4 are **skipped entirely** for stats-only events (full-hit events where `FetchedData == null`). This means reads never trigger eviction.

---

## Configuration Example

**Using factory methods (recommended for readability):**

```csharp
// VPC with LRU eviction, max 50 segments, max total span of 5000 units
await using var vpc = VisitedPlacesCacheBuilder
    .For(dataSource, domain)
    .WithOptions(o => o.WithSegmentTtl(TimeSpan.FromHours(1)))
    .WithEviction(e => e
        .AddPolicy(MaxSegmentCountPolicy.Create<int, MyData>(maxCount: 50))
        .AddPolicy(MaxTotalSpanPolicy.Create<int, MyData, IntegerFixedStepDomain>(
            maxTotalSpan: 5000, domain))
        .WithSelector(LruEvictionSelector.Create<int, MyData>()))
    .Build();
```

**Using explicit generic constructors (alternative, fully equivalent):**

```csharp
await using var vpc = VisitedPlacesCacheBuilder
    .For(dataSource, domain)
    .WithOptions(o => o.WithSegmentTtl(TimeSpan.FromHours(1)))
    .WithEviction(
        policies: [
            new MaxSegmentCountPolicy<int, MyData>(maxCount: 50),
            new MaxTotalSpanPolicy<int, MyData, IntegerFixedStepDomain>(
                maxTotalSpan: 5000, domain)
        ],
        selector: new LruEvictionSelector<int, MyData>())
    .Build();
```

Both policies are active simultaneously. The LRU selector determines eviction order via sampling; the constraint satisfaction loop removes segments until all pressures are satisfied.

---

## Edge Cases

### All Segments Are Immune

If the just-stored segment is the **only** segment in `CachedSegments` when eviction is triggered, the selector will find no eligible candidates after skipping immune segments. `TrySelectCandidate` returns `false` immediately; the eviction is a no-op for this event; the cache temporarily remains above-limit. The next storage event will add another segment, giving the selector a non-immune candidate.

This is expected behavior for very low-capacity configurations (e.g., `maxCount: 1`). In such configurations, the cache effectively evicts the oldest segment on every new storage, except for a brief window where both the old and new segments coexist.

### Constraint Satisfaction May Exhaust Candidates

If all eligible candidates are removed but the pressure's `IsExceeded` is still `true` (e.g., the remaining immune segment is very large and keeps total span above the limit), the constraint remains violated. The next storage event will trigger another eviction pass.

This is mathematically inevitable for sufficiently tight constraints combined with large individual segments. It is not an error; it is eventual convergence.

### Eviction of a Segment Currently in Transit

A segment may be referenced in the User Path's current in-memory assembly (i.e., its data is currently being served to a user) while the Background Path is evicting it. This is safe:

- The User Path holds a reference to the segment's data (a `ReadOnlyMemory<TData>` slice); the data object's lifetime is reference-counted by the GC
- Eviction only removes the segment from `CachedSegments` (the searchable index); it does not free or corrupt the segment's data
- The user's in-flight response completes normally; the segment simply becomes unavailable for future User Path reads after eviction

---

## Alignment with Invariants

| Invariant                                        | Enforcement                                                                                                                            |
|--------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------|
| VPC.E.1 — Pluggable policy                       | Policies are injected at construction; `IEvictionPolicy` is a public interface                                                         |
| VPC.E.1a — ANY policy exceeded triggers eviction | `EvictionPolicyEvaluator.Evaluate` OR-combines all policy pressures                                                                    |
| VPC.E.2 — Constraint satisfaction loop           | `EvictionEngine` coordinates: evaluator produces pressure; executor loops via `TrySelectCandidate`                                     |
| VPC.E.2a — Single loop per event                 | `CompositePressure` aggregates all exceeded pressures; one `EvaluateAndExecute` call per event                                         |
| VPC.E.3 — Just-stored immunity                   | Executor seeds immune set from `justStoredSegments`; selector skips immune segments during sampling                                    |
| VPC.E.3a — No-op when only immune candidate      | `TrySelectCandidate` returns `false`; executor exits loop immediately                                                                  |
| VPC.E.4 — Metadata owned by Eviction Selector    | Selector owns `InitializeMetadata` / `UpdateMetadata`; `EvictionEngine` delegates                                                      |
| VPC.E.4a — Metadata initialized at storage time  | `engine.InitializeSegment` called immediately after `storage.TryAdd` returns `true` (or per segment returned by `storage.TryAddRange`) |
| VPC.E.4b — Metadata updated on UsedSegments      | `engine.UpdateMetadata` called in Step 1 of each event cycle                                                                           |
| VPC.E.4c — Metadata valid before every IsWorse   | `SamplingEvictionSelector` calls `EnsureMetadata` before each `IsWorse` comparison in sampling loop                                    |
| VPC.E.5 — Eviction only in Background Path       | User Path has no reference to engine, policies, selectors, or executor                                                                 |
| VPC.E.6 — Consistency after eviction             | Evicted segments (and their metadata) are removed together; no dangling references                                                     |
| VPC.B.3b — No eviction on stats-only events      | Steps 3-4 gated on `justStoredSegments.Count > 0`                                                                                      |

---

## See Also

- `docs/visited-places/scenarios.md` — Eviction scenarios (E1-E6) and Background Path scenarios (B1-B5)
- `docs/visited-places/invariants.md` — VPC.E eviction invariants
- `docs/visited-places/actors.md` — Eviction Policy, Eviction Selector, Eviction Engine, and Eviction Executor actor catalog
- `docs/visited-places/storage-strategies.md` — Soft delete pattern; interaction between storage and eviction
- `docs/shared/glossary.md` — CacheInteraction, WaitForIdleAsync
