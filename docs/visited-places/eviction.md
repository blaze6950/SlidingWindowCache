# Eviction — VisitedPlaces Cache

This document describes the eviction architecture of `VisitedPlacesCache`: how capacity limits are defined, how eviction is triggered, and how eviction candidates are selected and removed.

For the surrounding execution context, see `docs/visited-places/scenarios.md` (Section III). For formal invariants, see `docs/visited-places/invariants.md` (Section VPC.E).

---

## Overview

VPC eviction is a **constraint satisfaction** system with three decoupled components:

| Component             | Role                 | Question answered                                               |
|-----------------------|----------------------|-----------------------------------------------------------------|
| **Eviction Policy**   | Constraint evaluator | "Is my constraint currently violated?"                          |
| **Eviction Pressure** | Constraint tracker   | "Is the constraint still violated after removing this segment?" |
| **Eviction Selector** | Candidate orderer    | "In what order should candidates be considered?"                |

These components are composed by a single **Eviction Executor** that runs a constraint satisfaction loop: remove segments in selector order until all pressures are satisfied.

### Execution Flow

```
Policies → Pressure Objects → CompositePressure → Executor → Selector → Storage
```

---

## Component 1 — Eviction Policy (`IEvictionPolicy`)

### Purpose

An Eviction Policy answers a single question after every storage step: **"Does the current state of `CachedSegments` violate my configured constraint?"**

If yes, it produces an `IEvictionPressure` that tracks constraint satisfaction as segments are removed. If no, it returns `NoPressure<TRange,TData>.Instance` (a singleton with `IsExceeded = false`).

### Architectural Constraints

Policies must NOT:
- Know about eviction strategy (selector order)
- Estimate how many segments to remove
- Make assumptions about which segments will be removed

### Multiple Policies

Multiple Policies may be active simultaneously. Eviction is triggered when **ANY** Policy produces an exceeded pressure (OR semantics). All Policies are checked after every storage step. If two Policies produce exceeded pressures, they are combined into a `CompositePressure` and the executor satisfies all constraints in a single pass.

### Built-in Policies

#### MaxSegmentCountPolicy

Fires when the total number of segments in `CachedSegments` exceeds a configured limit.

```
Fires when: CachedSegments.Count > MaxCount
Produces:   SegmentCountPressure (count-based, order-independent)
```

**Configuration parameter**: `maxCount: int` (must be >= 1)

**Use case**: Controlling memory usage when all segments are approximately the same size, or when the absolute number of cache entries is the primary concern.

**Note**: Count-based eviction is order-independent — removing any segment equally satisfies the constraint by decrementing the count by 1.

#### MaxTotalSpanPolicy

Fires when the sum of all segment spans (total coverage width) exceeds a configured limit.

```
Fires when: sum(S.Range.Span(domain) for S in CachedSegments) > MaxTotalSpan
Produces:   TotalSpanPressure (span-aware, order-dependent satisfaction)
```

**Configuration parameter**: `maxTotalSpan: TRange` (domain-specific span unit)

**Use case**: Controlling the total domain coverage cached, regardless of how many segments it is split into. More meaningful than segment count when segments vary significantly in span.

**Key design improvement**: The old `MaxTotalSpanEvaluator` estimated removal counts using a greedy algorithm (sort by span descending, count how many need removing). This estimate could mismatch the actual executor order (LRU, FIFO, etc.), leading to under-eviction. The new `TotalSpanPressure` tracks actual span reduction as segments are removed, guaranteeing correctness regardless of selector order.

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
- `Reduce(segment)` — called by the executor after each segment removal; updates internal tracking

### Pressure Implementations

| Type                   | Visibility | Produced by                 | `Reduce` behavior                              |
|------------------------|------------|-----------------------------|------------------------------------------------|
| `NoPressure`           | public     | All policies (no violation) | No-op (singleton, `IsExceeded` always `false`) |
| `SegmentCountPressure` | internal   | `MaxSegmentCountPolicy`     | Decrements current count by 1                  |
| `TotalSpanPressure`    | internal   | `MaxTotalSpanPolicy`        | Subtracts removed segment's span from total    |
| `CompositePressure`    | internal   | Executor (aggregation)      | Calls `Reduce` on all child pressures          |

### CompositePressure

When multiple policies produce exceeded pressures, the executor wraps them in a `CompositePressure`:
- `IsExceeded = any child.IsExceeded` (OR semantics)
- `Reduce(segment)` calls `Reduce` on all children

When only a single policy is exceeded, its pressure is used directly (no composite wrapping) to avoid unnecessary allocation.

---

## Component 3 — Eviction Selector (`IEvictionSelector`)

### Purpose

An Eviction Selector determines the **order** in which eviction candidates are considered. It does NOT decide how many to remove or whether to evict at all — those are the pressure's and policy's responsibilities.

### Architectural Constraints

Selectors must NOT:
- Know about eviction policies or constraints
- Decide when or whether to evict
- Filter candidates based on immunity rules (immunity is handled by the executor)

### Built-in Selectors

#### LruEvictionSelector — Least Recently Used

**Orders candidates ascending by `LastAccessedAt`** — the least recently accessed segment is first (highest eviction priority).

- Optimizes for temporal locality: segments accessed recently are retained
- Best for workloads where re-access probability correlates with recency

**Example**: Segments `S1(t=5), S2(t=1), S3(t=8)`:
- Ordered: `[S2(t=1), S1(t=5), S3(t=8)]`
- Executor removes from front until pressure is satisfied

#### FifoEvictionSelector — First In, First Out

**Orders candidates ascending by `CreatedAt`** — the oldest segment is first.

- Treats the cache as a fixed-size sliding window over time
- Does not reflect access patterns; simpler and more predictable than LRU
- Best for workloads where all segments have similar re-access probability

#### SmallestFirstEvictionSelector — Smallest Span First

**Orders candidates ascending by span** — the narrowest segment is first.

- Optimizes for total domain coverage: retains large (wide) segments over small ones
- Best for workloads where wide segments are more valuable
- Captures `TDomain` internally for span computation

#### Farthest-From-Access (planned)

**Orders candidates by distance from the most recently accessed range** — farthest segments first.

- Spatial analogue of LRU: retains segments near the current access pattern

#### Oldest-First (planned)

**Orders candidates by a hybrid of age and access frequency** — old, neglected segments first.

---

## Eviction Executor

The Eviction Executor is an internal component that ties policies, pressures, and selectors together in a **constraint satisfaction loop**:

```
1. Receive all segments + just-stored segments from Background Path
2. Filter out immune (just-stored) segments from candidates
3. Pass eligible candidates to selector for ordering
4. Iterate ordered candidates:
   a. Remove segment from storage
   b. Call pressure.Reduce(segment)
   c. Report removal via diagnostics
   d. If !pressure.IsExceeded → stop (constraint satisfied)
5. Return list of removed segments
```

### Just-Stored Segment Immunity

The just-stored segment (added in step 2 of event processing) is **always excluded** from the candidate set before candidates are passed to the selector. See Invariant VPC.E.3.

The immunity filtering is performed by the Executor, not the Selector.

---

## Statistics

### Schema

Every segment stored in `CachedSegments` has an associated `SegmentStatistics` record.

| Field            | Type       | Set at         | Updated when                                            |
|------------------|------------|----------------|---------------------------------------------------------|
| `CreatedAt`      | `DateTime` | Segment stored | Never (immutable)                                       |
| `LastAccessedAt` | `DateTime` | Segment stored | Each time segment appears in `UsedSegments`             |
| `HitCount`       | `int`      | 0 at storage   | Incremented each time segment appears in `UsedSegments` |

### Ownership

Statistics are updated by the **Background Event Processor** directly (step 1 of event processing). This is a private concern of the Background Path, not owned by any eviction component.

Not all selectors use all fields. The FIFO selector only uses `CreatedAt`; the LRU selector primarily uses `LastAccessedAt`. Statistics fields are always maintained regardless of which selector is configured, since the same segment may be served to the user before the selector is changed.

### Lifecycle

```
Segment stored (Background Path, step 2):
  statistics.CreatedAt      = now
  statistics.LastAccessedAt = now
  statistics.HitCount       = 0

Segment used (BackgroundEvent.UsedSegments, Background Path, step 1):
  statistics.LastAccessedAt = now
  statistics.HitCount      += 1

Segment evicted (Background Path, step 4):
  statistics record destroyed
```

---

## Eviction and Storage: Interaction

Eviction never happens in isolation — it is always the tail of a storage step in background event processing. The full sequence:

```
Background event received
  |
Step 1: Update statistics for UsedSegments      (Background Path directly)
  |
Step 2: Store FetchedData as new segment(s)      (Storage Strategy)
  |                                              <- Only if FetchedData != null
Step 3: Evaluate all Eviction Policies           (Eviction Policies)
  |                                              <- Only if step 2 ran
Step 4: Execute eviction if any policy exceeded  (Eviction Executor)
         - Filter out immune (just-stored) segments
         - Order candidates via Selector
         - Remove in order until all pressures satisfied
```

Steps 3 and 4 are **skipped entirely** for stats-only events (full-hit events where `FetchedData == null`). This means reads never trigger eviction.

---

## Configuration Example

```csharp
// VPC with LRU eviction, max 50 segments, max total span of 5000 units
var vpc = VisitedPlacesCacheBuilder
    .Create(dataSource, domain)
    .WithEviction(
        policies: [
            new MaxSegmentCountPolicy<int, MyData>(maxCount: 50),
            new MaxTotalSpanPolicy<int, MyData, IntegerFixedStepDomain>(
                maxTotalSpan: 5000, domain)
        ],
        selector: new LruEvictionSelector<int, MyData>()
    )
    .Build();
```

Both policies are active. The LRU Selector determines eviction order; the Executor removes segments until all pressures are satisfied.

---

## Edge Cases

### All Segments Are Immune

If the just-stored segment is the **only** segment in `CachedSegments` when eviction is triggered, the Executor has no eligible candidates after immunity filtering. The eviction is a no-op for this event; the cache temporarily remains above-limit. The next storage event will add another segment, giving the Executor a non-immune candidate to evict.

This is expected behavior for very low-capacity configurations (e.g., `maxCount: 1`). In such configurations, the cache effectively evicts the oldest segment on every new storage, except for a brief window where both the old and new segments coexist.

### Constraint Satisfaction May Exhaust Candidates

If the Executor removes all eligible candidates but the pressure's `IsExceeded` is still `true` (e.g., the remaining immune segment is very large and keeps total span above the limit), the constraint remains violated. The next storage event will trigger another eviction pass.

This is mathematically inevitable for sufficiently tight constraints combined with large individual segments. It is not an error; it is eventual convergence.

### Eviction of a Segment Currently in Transit

A segment may be referenced in the User Path's current in-memory assembly (i.e., its data is currently being served to a user) while the Background Path is evicting it. This is safe:

- The User Path holds a reference to the segment's data (a `ReadOnlyMemory<TData>` slice); the data object's lifetime is reference-counted by the GC
- Eviction only removes the segment from `CachedSegments` (the searchable index); it does not free or corrupt the segment's data
- The user's in-flight response completes normally; the segment simply becomes unavailable for future User Path reads after eviction

---

## Alignment with Invariants

| Invariant                                          | Enforcement                                                                    |
|----------------------------------------------------|--------------------------------------------------------------------------------|
| VPC.E.1 — Pluggable policy                         | Policies are injected at construction; `IEvictionPolicy` is a public interface |
| VPC.E.1a — ANY policy exceeded triggers eviction   | Background Path OR-combines all policy pressures                               |
| VPC.E.2 — Constraint satisfaction loop             | Executor removes in selector order until all pressures satisfied               |
| VPC.E.2a — Single loop per event                   | CompositePressure aggregates all exceeded pressures; one iteration             |
| VPC.E.3 — Just-stored immunity                     | Executor filters out just-stored segments before passing to selector           |
| VPC.E.3a — No-op when only immune candidate        | Executor receives empty candidate set after filtering; does nothing            |
| VPC.E.4 — Statistics maintained by Background Path | Background Event Processor updates statistics directly (private static method) |
| VPC.E.5 — Eviction only in Background Path         | User Path has no reference to policies, selectors, or executor                 |
| VPC.E.6 — Consistency after eviction               | Evicted segments and their statistics are atomically removed together          |
| VPC.B.3b — No eviction on stats-only events        | Steps 3-4 gated on `FetchedData != null`                                       |

---

## See Also

- `docs/visited-places/scenarios.md` — Eviction scenarios (E1-E6) and Background Path scenarios (B1-B5)
- `docs/visited-places/invariants.md` — VPC.E eviction invariants
- `docs/visited-places/actors.md` — Eviction Policy, Eviction Selector, and Eviction Executor actor catalog
- `docs/visited-places/storage-strategies.md` — Soft delete pattern; interaction between storage and eviction
- `docs/shared/glossary.md` — CacheInteraction, WaitForIdleAsync
