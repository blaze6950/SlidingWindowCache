# Eviction — VisitedPlaces Cache

This document describes the eviction architecture of `VisitedPlacesCache`: how capacity limits are defined, how eviction is triggered, and how eviction candidates are selected and removed.

For the surrounding execution context, see `docs/visited-places/scenarios.md` (Section III). For formal invariants, see `docs/visited-places/invariants.md` (Section VPC.E).

---

## Overview

VPC eviction is a **two-phase, pluggable** system:

| Phase                  | Role                               | Question answered                        |
|------------------------|------------------------------------|------------------------------------------|
| **Eviction Evaluator** | Capacity watchdog                  | "Should we evict right now?"             |
| **Eviction Executor**  | Strategy engine + statistics owner | "Which segments to evict, and how many?" |

The two phases are decoupled by design. A single Evaluator can be paired with any Executor strategy; multiple Evaluators can coexist with a single Executor.

---

## Phase 1 — Eviction Evaluator

### Purpose

The Eviction Evaluator answers a single yes/no question after every storage step: **"Does the current state of `CachedSegments` violate my configured constraint?"**

If the answer is yes ("I fire"), the Background Path invokes the Eviction Executor to reduce the cache back to within-policy state.

### Multiple Evaluators

Multiple Evaluators may be active simultaneously. Eviction is triggered when **ANY** Evaluator fires (OR semantics). All Evaluators are checked after every storage step, regardless of whether a previous Evaluator already fired. If two Evaluators fire simultaneously, the Executor must satisfy both constraints in a single pass.

### Built-in Evaluators

#### MaxSegmentCountEvaluator

Fires when the total number of segments in `CachedSegments` exceeds a configured limit.

```
Fires when: CachedSegments.Count > MaxCount
```

**Configuration parameter**: `maxCount: int`

**Use case**: Controlling memory usage when all segments are approximately the same size, or when the absolute number of cache entries is the primary concern.

#### MaxTotalSpanEvaluator

Fires when the sum of all segment spans (total coverage width) exceeds a configured limit.

```
Fires when: sum(S.Range.Span(domain) for S in CachedSegments) > MaxTotalSpan
```

**Configuration parameter**: `maxTotalSpan: TRange` (domain-specific span unit)

**Use case**: Controlling the total domain coverage cached, regardless of how many segments it is split into. More meaningful than segment count when segments vary significantly in span.

#### MaxMemoryEvaluator (planned)

Fires when the estimated total memory used by all segment data exceeds a configured limit.

```
Fires when: sum(S.Data.Length * sizeof(TData) for S in CachedSegments) > MaxBytes
```

**Configuration parameter**: `maxBytes: long`

**Use case**: Direct memory budget enforcement.

---

## Phase 2 — Eviction Executor

### Purpose

The Eviction Executor is the single authority for:

1. **Statistics maintenance** — defines the `SegmentStatistics` schema and updates it when the Background Path reports segment accesses
2. **Candidate selection** — determines which segments are eligible for eviction and in what priority order, according to its configured strategy
3. **Eviction execution** — removes selected segments from `CachedSegments`

### Statistics Schema

Every segment stored in `CachedSegments` has an associated `SegmentStatistics` record. The Executor defines which fields exist and are maintained.

| Field            | Type       | Set at         | Updated when                                            |
|------------------|------------|----------------|---------------------------------------------------------|
| `CreatedAt`      | `DateTime` | Segment stored | Never (immutable)                                       |
| `LastAccessedAt` | `DateTime` | Segment stored | Each time segment appears in `UsedSegments`             |
| `HitCount`       | `int`      | 0 at storage   | Incremented each time segment appears in `UsedSegments` |

Not all strategies use all fields. The FIFO strategy only uses `CreatedAt`; the LRU strategy primarily uses `LastAccessedAt`. Statistics fields are always maintained by the Background Path regardless of which strategy is configured, since the same segment may be served to the user before the strategy is changed (and statistics must remain accurate for a potential future switch).

### Statistics Lifecycle

```
Segment stored (Background Path, step 2):
  statistics.CreatedAt     = now
  statistics.LastAccessedAt = now
  statistics.HitCount       = 0

Segment used (BackgroundEvent.UsedSegments, Background Path, step 1):
  statistics.LastAccessedAt = now
  statistics.HitCount      += 1

Segment evicted (Background Path, step 4):
  statistics record destroyed
```

### Just-Stored Segment Immunity

The just-stored segment (the segment added in step 2 of the current event's processing sequence) is **always excluded** from the eviction candidate set. See Invariant VPC.E.3 and Scenario E4 in `docs/visited-places/scenarios.md`.

The immunity rule is enforced by the Background Path before invoking the Executor: the just-stored segment reference is passed as an exclusion parameter to the Executor's selection method.

---

## Built-in Eviction Strategies

### LRU — Least Recently Used

**Evicts the segment(s) with the oldest `LastAccessedAt`.**

- Optimizes for temporal locality: segments accessed recently are retained
- Best for workloads where re-access probability correlates with recency
- Requires `LastAccessedAt` field (updated on every access)

**Selection algorithm**: Sort eligible segments ascending by `LastAccessedAt`; remove from the front until all evaluator constraints are satisfied.

**Example**: Segments `S₁(t=5), S₂(t=1), S₃(t=8)`, limit = 2, new segment `S₄` just stored (immune):
- Eligible: `{S₁, S₂, S₃}` (S₄ immune)
- Sort by `LastAccessedAt` ascending: `[S₂(t=1), S₁(t=5), S₃(t=8)]`
- Remove `S₂` — one slot freed, limit satisfied

---

### FIFO — First In, First Out

**Evicts the segment(s) with the oldest `CreatedAt`.**

- Treats the cache as a fixed-size sliding window over time
- Does not reflect access patterns; simpler and more predictable than LRU
- Best for workloads where all segments have similar re-access probability over time
- Requires only `CreatedAt` field

**Selection algorithm**: Sort eligible segments ascending by `CreatedAt`; remove from the front until all constraints are satisfied.

**Example**: Segments `S₁(created: t=3), S₂(created: t=1), S₃(created: t=7)`, limit = 2, `S₄` immune:
- Sort by `CreatedAt` ascending: `[S₂(t=1), S₁(t=3), S₃(t=7)]`
- Remove `S₂` — limit satisfied

---

### Smallest-First

**Evicts the segment(s) with the smallest span (narrowest range coverage).**

- Optimizes for total domain coverage: retains large (wide) segments over small ones
- Best for workloads where wide segments are more valuable (they cover more of the domain and are more likely to be reused)
- Does not directly use any statistics field; uses `S.Range.Span(domain)` computed at selection time

**Selection algorithm**: Sort eligible segments ascending by span; remove from the front until all constraints are satisfied.

**Use case**: When maximizing total cached domain coverage per segment count.

---

### Farthest-From-Access (planned)

**Evicts segments whose range center is farthest from the most recently accessed range.**

- Spatial analogue of LRU: retains segments near the current access pattern
- Best for workloads with strong spatial locality (e.g., user browsing a region of the domain)

---

### Oldest-First (planned)

**Evicts segments with the smallest `HitCount` among those with the oldest `CreatedAt`.**

- Hybrid strategy: combines age and access frequency
- Retains frequently-accessed old segments while evicting neglected old ones

---

## Single-Pass Eviction

The Eviction Executor always runs in a **single pass** per background event, regardless of how many Evaluators fired simultaneously. The pass removes enough segments to satisfy all active evaluator constraints simultaneously.

**Why single-pass matters:**

If two Evaluators fire (e.g., segment count AND total span both exceeded), a naive approach would run the Executor twice — once per evaluator. This is wasteful: the first pass may already satisfy both constraints, and a second pass would either be a no-op or remove more than necessary.

Single-pass is implemented by computing the combined eviction target before selection:
1. For each fired evaluator, compute: "how much do I need to remove to satisfy this constraint?"
2. Take the maximum (most demanding removal requirement across all fired evaluators)
3. Remove exactly that much in one ordered scan

---

## Configuration Example

```csharp
// VPC with LRU eviction, max 50 segments, max total span of 5000 units
var vpc = VisitedPlacesCacheBuilder
    .Create(dataSource, domain)
    .WithEviction(
        evaluators: [
            new MaxSegmentCountEvaluator(maxCount: 50),
            new MaxTotalSpanEvaluator(maxTotalSpan: 5000)
        ],
        executor: new LruEvictionExecutor<int, MyData>()
    )
    .Build();
```

Both evaluators are active. The LRU Executor handles eviction whenever either fires.

---

## Eviction and Storage: Interaction

Eviction never happens in isolation — it is always the tail of a storage step in background event processing. The full sequence:

```
Background event received
  ↓
Step 1: Update statistics for UsedSegments      (Eviction Executor)
  ↓
Step 2: Store FetchedData as new segment(s)      (Storage Strategy)
  ↓                                              ← Only if FetchedData != null
Step 3: Check all Eviction Evaluators            (Eviction Evaluators)
  ↓                                              ← Only if step 2 ran
Step 4: Execute eviction if any evaluator fired  (Eviction Executor)
         - Exclude just-stored segment
         - Single pass; satisfy all constraints
```

Steps 3 and 4 are **skipped entirely** for stats-only events (full-hit events where `FetchedData == null`). This means reads never trigger eviction.

---

## Edge Cases

### All Segments Are Immune

If the just-stored segment is the **only** segment in `CachedSegments` when eviction is triggered, the Executor has no eligible candidates. The eviction is a no-op for this event; the cache temporarily remains above-limit. The next storage event will add another segment, giving the Executor a non-immune candidate to evict.

This is expected behavior for very low-capacity configurations (e.g., `maxCount: 1`). In such configurations, the cache effectively evicts the oldest segment on every new storage, except for a brief window where both the old and new segments coexist.

### Partial Constraint Satisfaction

If the Executor removes the maximum eligible candidates but still cannot satisfy all constraints (e.g., the single remaining non-immune segment's removal would bring the count to within-limit, but the total span still exceeds the span limit because the single remaining segment is very large), the constraints remain violated. The next storage event will trigger another eviction pass.

This is mathematically inevitable for sufficiently tight constraints combined with large individual segments. It is not an error; it is eventual convergence.

### Eviction of a Segment Currently in Transit

A segment may be referenced in the User Path's current in-memory assembly (i.e., its data is currently being served to a user) while the Background Path is evicting it. This is safe:

- The User Path holds a reference to the segment's data (a `ReadOnlyMemory<TData>` slice); the data object's lifetime is reference-counted by the GC
- Eviction only removes the segment from `CachedSegments` (the searchable index); it does not free or corrupt the segment's data
- The user's in-flight response completes normally; the segment simply becomes unavailable for future User Path reads after eviction

---

## Alignment with Invariants

| Invariant                                        | Enforcement                                                                     |
|--------------------------------------------------|---------------------------------------------------------------------------------|
| VPC.E.1 — Pluggable evaluator                    | Evaluators are injected at construction; strategy is an interface               |
| VPC.E.1a — ANY evaluator fires triggers eviction | Background Path OR-combines all evaluator results                               |
| VPC.E.2 — Executor owns selection + statistics   | Executor is the only component that writes `SegmentStatistics`                  |
| VPC.E.2a — Single pass per event                 | Executor computes combined target before selection loop                         |
| VPC.E.3 — Just-stored immunity                   | Background Path passes just-stored segment reference as exclusion               |
| VPC.E.3a — No-op when only immune candidate      | Executor receives empty candidate set; does nothing                             |
| VPC.E.4 — Statistics schema owned by Executor    | Statistics fields defined by Executor; Background Path calls Executor to update |
| VPC.E.5 — Eviction only in Background Path       | User Path has no reference to Evaluators or Executor                            |
| VPC.E.6 — Consistency after eviction             | Evicted segments and their statistics are atomically removed together           |
| VPC.B.3b — No eviction on stats-only events      | Steps 3–4 gated on `FetchedData != null`                                        |

---

## See Also

- `docs/visited-places/scenarios.md` — Eviction scenarios (E1–E6) and Background Path scenarios (B1–B5)
- `docs/visited-places/invariants.md` — VPC.E eviction invariants
- `docs/visited-places/actors.md` — Eviction Evaluator and Eviction Executor actor catalog
- `docs/visited-places/storage-strategies.md` — Soft delete pattern; interaction between storage and eviction
- `docs/shared/glossary.md` — CacheInteraction, WaitForIdleAsync
