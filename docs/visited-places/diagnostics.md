# Diagnostics — VisitedPlaces Cache

For the shared diagnostics pattern (two-tier design, zero-cost abstraction, `BackgroundOperationFailed` critical requirement), see `docs/shared/diagnostics.md`. This document covers the two-level diagnostics hierarchy, all 15 events (5 shared + 10 VPC-specific), and VPC-specific usage patterns.

---

## Interfaces: `ICacheDiagnostics` and `IVisitedPlacesCacheDiagnostics`

The diagnostics system uses a two-level hierarchy. The shared `ICacheDiagnostics` interface (in `Intervals.NET.Caching`) defines 5 events common to all cache implementations. `IVisitedPlacesCacheDiagnostics` (in `Intervals.NET.Caching.VisitedPlaces`) extends it with 10 VPC-specific events.

```csharp
// Shared foundation — Intervals.NET.Caching
public interface ICacheDiagnostics
{
    // User Path Events
    void UserRequestServed();
    void UserRequestFullCacheHit();
    void UserRequestPartialCacheHit();
    void UserRequestFullCacheMiss();

    // Failure Events
    void BackgroundOperationFailed(Exception ex);
}

// VisitedPlaces-specific — Intervals.NET.Caching.VisitedPlaces
public interface IVisitedPlacesCacheDiagnostics : ICacheDiagnostics
{
    // Data Source Access Events
    void DataSourceFetchGap();

    // Background Processing Events
    void NormalizationRequestReceived();
    void NormalizationRequestProcessed();
    void BackgroundStatisticsUpdated();
    void BackgroundSegmentStored();

    // Eviction Events
    void EvictionEvaluated();
    void EvictionTriggered();
    void EvictionExecuted();
    void EvictionSegmentRemoved();

    // TTL Events
    void TtlSegmentExpired();
}
```

---

## Implementations

### `EventCounterCacheDiagnostics` — Test Infrastructure Implementation

Located in `tests/Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure/EventCounterCacheDiagnostics.cs`.

Thread-safe counter-based implementation using `Interlocked.Increment` / `Volatile.Read`:

```csharp
var diagnostics = new EventCounterCacheDiagnostics();

await using var vpc = VisitedPlacesCacheBuilder
    .For(dataSource, domain)
    .WithEviction(e => e
        .AddPolicy(MaxSegmentCountPolicy.Create<int, MyData>(maxCount: 50))
        .WithSelector(LruEvictionSelector.Create<int, MyData>()))
    .Build(diagnostics);

Console.WriteLine($"Cache hits: {diagnostics.UserRequestFullCacheHit}");
Console.WriteLine($"Segments stored: {diagnostics.BackgroundSegmentStored}");
Console.WriteLine($"Eviction passes: {diagnostics.EvictionEvaluated}");
```

Features:
- Thread-safe (`Interlocked.Increment`, `Volatile.Read`)
- Low overhead (~1–5 ns per event)
- Read-only properties for all 15 counters (5 shared + 10 VPC-specific)
- `Reset()` method for test isolation
- `AssertBackgroundLifecycleIntegrity()` helper: verifies `Received == Processed + Failed`

**WARNING**: The `EventCounterCacheDiagnostics` implementation of `BackgroundOperationFailed` only increments a counter — it does not log. For production use, you MUST create a custom implementation that logs to your logging infrastructure. See `docs/shared/diagnostics.md` for requirements.

### `NoOpDiagnostics` — Zero-Cost Implementation

Empty implementation with no-op methods that the JIT eliminates completely. Automatically used when the diagnostics parameter is omitted from the constructor or builder.

### Custom Implementations

```csharp
public class PrometheusMetricsDiagnostics : IVisitedPlacesCacheDiagnostics
{
    private readonly Counter _requestsServed;
    private readonly Counter _cacheHits;
    private readonly Counter _segmentsStored;
    private readonly Counter _evictionPasses;

    void ICacheDiagnostics.UserRequestServed() => _requestsServed.Inc();
    void ICacheDiagnostics.UserRequestFullCacheHit() => _cacheHits.Inc();
    void ICacheDiagnostics.BackgroundOperationFailed(Exception ex) =>
        _logger.LogError(ex, "VPC background operation failed.");

    void IVisitedPlacesCacheDiagnostics.BackgroundSegmentStored() => _segmentsStored.Inc();
    void IVisitedPlacesCacheDiagnostics.EvictionEvaluated() => _evictionPasses.Inc();
    // ...
}
```

---

## Execution Context Summary

| Thread                                     | Events fired                                                                                                                                                                                                                                                        |
|--------------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **User Thread**                            | `UserRequestServed`, `UserRequestFullCacheHit`, `UserRequestPartialCacheHit`, `UserRequestFullCacheMiss`, `DataSourceFetchGap`                                                                                                                                      |
| **Background Thread (Normalization Loop)** | `NormalizationRequestReceived`, `NormalizationRequestProcessed`, `BackgroundStatisticsUpdated`, `BackgroundSegmentStored`, `EvictionEvaluated`, `EvictionTriggered`, `EvictionExecuted`, `EvictionSegmentRemoved`, `TtlSegmentExpired`, `BackgroundOperationFailed` |

All hooks execute **synchronously** on the thread that triggers the event. See `docs/shared/diagnostics.md` for threading rules and what NOT to do inside hooks.

---

## Diagnostic Events Reference

### User Path Events

#### `UserRequestServed()`
**Tracks:** Completion of a user request (data returned to caller)  
**Location:** `UserRequestHandler.HandleRequestAsync` (final step)  
**Context:** User Thread  
**Fires when:** No exception occurred — regardless of `CacheInteraction`  
**Does NOT fire when:** An exception propagated out of `HandleRequestAsync`  
**Interpretation:** Total user requests completed without exception (including physical boundary misses where `Range == null`)

```csharp
await cache.GetDataAsync(Range.Closed(100, 200), ct);
Assert.Equal(1, diagnostics.UserRequestServed);
```

---

#### `UserRequestFullCacheHit()`
**Tracks:** Request served entirely from cache (no data source access)  
**Location:** `UserRequestHandler.HandleRequestAsync`  
**Context:** User Thread  
**Scenarios:** U2 (single segment hit), U3 (multi-segment assembly)

**Per-request programmatic alternative:** `result.CacheInteraction == CacheInteraction.FullHit`

```csharp
await cache.GetDataAsync(Range.Closed(100, 200), ct);
await cache.WaitForIdleAsync();
await cache.GetDataAsync(Range.Closed(120, 180), ct);  // fully within [100, 200]
Assert.Equal(1, diagnostics.UserRequestFullCacheHit);
```

---

#### `UserRequestPartialCacheHit()`
**Tracks:** Request with partial cache overlap (gap fetch required)  
**Location:** `UserRequestHandler.HandleRequestAsync`  
**Context:** User Thread  
**Scenarios:** U4 (partial hit)

**Per-request programmatic alternative:** `result.CacheInteraction == CacheInteraction.PartialHit`

```csharp
await cache.GetDataAsync(Range.Closed(100, 200), ct);
await cache.WaitForIdleAsync();
await cache.GetDataAsync(Range.Closed(150, 250), ct);  // overlaps — [201,250] is a gap
Assert.Equal(1, diagnostics.UserRequestPartialCacheHit);
```

---

#### `UserRequestFullCacheMiss()`
**Tracks:** Request requiring complete fetch from data source  
**Location:** `UserRequestHandler.HandleRequestAsync`  
**Context:** User Thread  
**Scenarios:** U1 (cold cache), U5 (full miss / no overlap)

**Per-request programmatic alternative:** `result.CacheInteraction == CacheInteraction.FullMiss`

```csharp
await cache.GetDataAsync(Range.Closed(100, 200), ct);   // cold cache
Assert.Equal(1, diagnostics.UserRequestFullCacheMiss);
await cache.GetDataAsync(Range.Closed(500, 600), ct);   // non-overlapping range
Assert.Equal(2, diagnostics.UserRequestFullCacheMiss);
```

---

### Data Source Access Events

#### `DataSourceFetchGap()`
**Tracks:** A single gap-range fetch from `IDataSource` (partial hit gap or full miss)  
**Location:** `UserRequestHandler.HandleRequestAsync` — called once per gap range fetched  
**Context:** User Thread  
**Invariant:** VPC.F.1 (User Path calls `IDataSource` only for true gaps)  
**Note:** On a full miss (U1, U5), one `DataSourceFetchGap` fires. On a partial hit with N gaps, N fires.

```csharp
// Cold cache — 1 gap fetch (the full range)
await cache.GetDataAsync(Range.Closed(100, 200), ct);
Assert.Equal(1, diagnostics.DataSourceFetchGap);
Assert.Equal(0, diagnostics.UserRequestFullCacheHit);
```

---

### Background Processing Events

#### `NormalizationRequestReceived()`
**Tracks:** A `CacheNormalizationRequest` dequeued and started processing by the Background Path  
**Location:** `CacheNormalizationExecutor.ExecuteAsync` (entry)  
**Context:** Background Thread (Normalization Loop)  
**Invariant:** VPC.B.2 (every published event is eventually processed)  
**Interpretation:** Total normalization events consumed. Equals `UserRequestServed` in steady state (one event per user request).

```csharp
await cache.GetDataAsync(Range.Closed(100, 200), ct);
await cache.WaitForIdleAsync();
Assert.Equal(1, diagnostics.NormalizationRequestReceived);
```

---

#### `NormalizationRequestProcessed()`
**Tracks:** A normalization request that completed all four processing steps successfully  
**Location:** `CacheNormalizationExecutor.ExecuteAsync` (exit)  
**Context:** Background Thread (Normalization Loop)  
**Invariant:** VPC.B.3 (fixed event processing sequence)  
**Lifecycle invariant:** `NormalizationRequestReceived == NormalizationRequestProcessed + BackgroundOperationFailed`

```csharp
await cache.GetDataAsync(Range.Closed(100, 200), ct);
await cache.WaitForIdleAsync();
Assert.Equal(1, diagnostics.NormalizationRequestProcessed);
TestHelpers.AssertBackgroundLifecycleIntegrity(diagnostics);
```

---

#### `BackgroundStatisticsUpdated()`
**Tracks:** Eviction metadata updated for used segments (Background Path step 1)  
**Location:** `CacheNormalizationExecutor.ExecuteAsync` (step 1 — `engine.UpdateMetadata`)  
**Context:** Background Thread (Normalization Loop)  
**Invariant:** VPC.E.4b (metadata updated on `UsedSegments` events)  
**Fires when:** `UsedSegments` is non-empty (partial hit, full hit)  
**Does NOT fire when:** Full miss with no previously used segments

```csharp
// Full hit — UsedSegments is non-empty → statistics updated
await cache.GetDataAsync(Range.Closed(100, 200), ct);
await cache.WaitForIdleAsync();
await cache.GetDataAsync(Range.Closed(120, 180), ct);
await cache.WaitForIdleAsync();
Assert.Equal(1, diagnostics.BackgroundStatisticsUpdated);
```

---

#### `BackgroundSegmentStored()`
**Tracks:** A new segment stored in the cache (Background Path step 2)  
**Location:** `CacheNormalizationExecutor.ExecuteAsync` (step 2 — per segment stored)  
**Context:** Background Thread (Normalization Loop)  
**Invariants:** VPC.B.3, VPC.C.1  
**Fires when:** `FetchedData` is non-null (full miss or partial hit with gap data)  
**Does NOT fire on stats-only events** (full hits where no new data was fetched)

```csharp
await cache.GetDataAsync(Range.Closed(100, 200), ct);   // cold cache, FetchedData != null
await cache.WaitForIdleAsync();
Assert.Equal(1, diagnostics.BackgroundSegmentStored);
```

---

#### `BackgroundOperationFailed(Exception ex)` — CRITICAL

**Tracks:** Background normalization failure due to unhandled exception  
**Context:** Background Thread (Normalization Loop)

**This event MUST be handled in production applications.** See `docs/shared/diagnostics.md` for full production requirements. Summary:

- Normalization runs in a fire-and-forget background loop
- When an exception occurs, it is caught and swallowed to prevent application crashes
- Without a proper implementation, failures are completely silent
- The normalization loop stops processing new events after a failure

```csharp
void ICacheDiagnostics.BackgroundOperationFailed(Exception ex)
{
    _logger.LogError(ex,
        "VPC background normalization failed. Cache will continue serving user requests " +
        "but background processing has stopped. Investigate data source health and cache configuration.");
}
```

---

### Eviction Events

#### `EvictionEvaluated()`
**Tracks:** An eviction evaluation pass (Background Path step 3)  
**Location:** `CacheNormalizationExecutor.ExecuteAsync` (step 3 — `engine.EvaluateAndExecute`)  
**Context:** Background Thread (Normalization Loop)  
**Invariant:** VPC.E.1a  
**Fires once per storage step** — regardless of whether any policy fired  
**Does NOT fire on stats-only events** (no storage step → no evaluation step)

```csharp
// First request: stores 1 segment → 1 evaluation pass
await cache.GetDataAsync(Range.Closed(100, 200), ct);
await cache.WaitForIdleAsync();
Assert.Equal(1, diagnostics.EvictionEvaluated);
Assert.Equal(0, diagnostics.EvictionTriggered);   // no policy fired (below limit)
```

---

#### `EvictionTriggered()`
**Tracks:** At least one eviction policy fired (constraint violated) — eviction will execute  
**Location:** `CacheNormalizationExecutor.ExecuteAsync` (step 3 — after evaluator fires)  
**Context:** Background Thread (Normalization Loop)  
**Invariants:** VPC.E.1a, VPC.E.2a  
**Relationship:** `EvictionTriggered <= EvictionEvaluated` always; `EvictionTriggered == EvictionExecuted` always

```csharp
// Build cache to just below limit
// ... fill to limit - 1 segments ...

// This request triggers eviction
await cache.GetDataAsync(newRange, ct);
await cache.WaitForIdleAsync();
Assert.Equal(1, diagnostics.EvictionTriggered);
Assert.Equal(1, diagnostics.EvictionExecuted);
```

---

#### `EvictionExecuted()`
**Tracks:** Eviction execution pass completed (Background Path step 4)  
**Location:** `CacheNormalizationExecutor.ExecuteAsync` (step 4 — after removal loop)  
**Context:** Background Thread (Normalization Loop)  
**Invariant:** VPC.E.2a  
**Fires once per triggered eviction** — after all candidates have been removed from storage  
**Relationship:** `EvictionExecuted == EvictionTriggered` always

---

#### `EvictionSegmentRemoved()`
**Tracks:** A single segment removed from the cache during eviction  
**Location:** `CacheNormalizationExecutor.ExecuteAsync` (step 4 — per-segment removal loop)  
**Context:** Background Thread (Normalization Loop)  
**Invariant:** VPC.E.6  
**Fires once per segment physically removed** — segments where `TryRemove()` returns `false` (already claimed by TTL normalization) are not counted  
**Relationship:** `EvictionSegmentRemoved >= EvictionExecuted` (multiple segments may be removed per eviction pass)

```csharp
// MaxSegmentCount(3) with 4 total → 1 evicted
await cache.WaitForIdleAsync();
Assert.Equal(1, diagnostics.EvictionTriggered);
Assert.Equal(1, diagnostics.EvictionExecuted);
Assert.Equal(1, diagnostics.EvictionSegmentRemoved);
```

---

### TTL Events

#### `TtlSegmentExpired()`
**Tracks:** A segment successfully expired and removed during TTL normalization  
**Location:** `CacheNormalizationExecutor.ExecuteAsync` (step 2b — per expired segment discovered during `TryNormalize`)  
**Context:** Background Thread (Normalization Loop)  
**Invariant:** VPC.T.1  
**Fires only on actual removal** — if the segment was already evicted by a capacity policy before its TTL was discovered by `TryNormalize`, `TryRemove()` returns `false` and this event does NOT fire  

```csharp
// Advance fake time past TTL, trigger normalization, verify
fakeTime.Advance(ttl + TimeSpan.FromSeconds(1));
await cache.GetDataAsync(someRange, ct);  // triggers normalization
await cache.WaitForIdleAsync();
Assert.True(diagnostics.TtlSegmentExpired >= 1);
```

---

## Testing Patterns

### Test Isolation with Reset()

```csharp
[Fact]
public async Task Test_EvictionPattern()
{
    var diagnostics = new EventCounterCacheDiagnostics();
    await using var cache = TestHelpers.CreateCacheWithSimpleSource(
        TestHelpers.CreateIntDomain(), diagnostics, maxSegmentCount: 3);

    // Warm up (fill to limit)
    await cache.GetDataAsync(Range.Closed(0, 10), ct);
    await cache.GetDataAsync(Range.Closed(20, 30), ct);
    await cache.GetDataAsync(Range.Closed(40, 50), ct);
    await cache.WaitForIdleAsync();

    diagnostics.Reset();  // isolate the eviction scenario

    // This request exceeds the limit → eviction fires
    await cache.GetDataAsync(Range.Closed(60, 70), ct);
    await cache.WaitForIdleAsync();

    Assert.Equal(1, diagnostics.BackgroundSegmentStored);
    Assert.Equal(1, diagnostics.EvictionEvaluated);
    Assert.Equal(1, diagnostics.EvictionTriggered);
    Assert.Equal(1, diagnostics.EvictionExecuted);
    Assert.Equal(1, diagnostics.EvictionSegmentRemoved);
}
```

### Background Lifecycle Integrity

```csharp
public static void AssertBackgroundLifecycleIntegrity(EventCounterCacheDiagnostics d)
{
    // Every received event must be either processed or failed
    Assert.Equal(d.NormalizationRequestReceived,
                 d.NormalizationRequestProcessed + d.BackgroundOperationFailed);
}
```

### Eviction Relationship Assertions

```csharp
public static void AssertEvictionLifecycleIntegrity(EventCounterCacheDiagnostics d)
{
    // Evaluation happens every storage step
    Assert.Equal(d.BackgroundSegmentStored, d.EvictionEvaluated);

    // Triggered implies executed
    Assert.Equal(d.EvictionTriggered, d.EvictionExecuted);

    // Triggered is a subset of evaluated
    Assert.True(d.EvictionTriggered <= d.EvictionEvaluated);

    // Multiple segments can be removed per eviction pass
    Assert.True(d.EvictionSegmentRemoved >= d.EvictionExecuted
                || d.EvictionExecuted == 0);
}
```

### TTL Idempotency Verification

```csharp
[Fact]
public async Task TtlAndEviction_BothClaimSegment_OnlyOneRemovalCounted()
{
    // A segment evicted by capacity BEFORE its TTL is discovered by TryNormalize should not count
    // in TtlSegmentExpired (TryRemove returns false for the second caller)
    var diagnostics = new EventCounterCacheDiagnostics();
    // ... scenario setup ...

    // Verify: only one of the two actors successfully removed the segment
    var totalRemovals = diagnostics.EvictionSegmentRemoved + diagnostics.TtlSegmentExpired;
    Assert.Equal(expectedRemovedCount, totalRemovals);
}
```

---

## Performance Considerations

| Implementation                 | Per-Event Cost                              | Memory                                              |
|--------------------------------|---------------------------------------------|-----------------------------------------------------|
| `EventCounterCacheDiagnostics` | ~1–5 ns (`Interlocked.Increment`, no alloc) | 60 bytes (15 integers: 5 shared + 10 VPC-specific)  |
| `NoOpDiagnostics`              | Zero (JIT-eliminated)                       | 0 bytes                                             |

Recommendation:
- **Development/Testing**: Always use `EventCounterCacheDiagnostics` (from test infrastructure)
- **Production**: Use a custom implementation with real logging; never use `EventCounterCacheDiagnostics` as a production logger
- **Performance-critical paths**: Omit diagnostics entirely (default `NoOpDiagnostics`)

---

## Per-Layer Diagnostics in Layered Caches

When using `VisitedPlacesCacheBuilder.Layered()`, each layer can have its own independent `IVisitedPlacesCacheDiagnostics` instance:

```csharp
var l2Diagnostics = new EventCounterCacheDiagnostics();
var l1Diagnostics = new EventCounterCacheDiagnostics();

await using var cache = VisitedPlacesCacheBuilder
    .Layered(realDataSource, domain)
    .AddVisitedPlacesLayer(deepOptions, deepEviction, l2Diagnostics)   // L2: inner / deep layer
    .AddVisitedPlacesLayer(userOptions, userEviction, l1Diagnostics)   // L1: outermost / user-facing layer
    .Build();
```

Layer diagnostics are completely independent — each layer reports only its own events. A full miss at L1 appears as `UserRequestFullCacheMiss` on `l1Diagnostics` and `UserRequestServed` on `l2Diagnostics` (L2 served the request for L1's data source adapter).

Always handle `BackgroundOperationFailed` on each layer independently.

---

## See Also

- `docs/shared/diagnostics.md` — shared diagnostics pattern, `BackgroundOperationFailed` production requirements
- `docs/visited-places/invariants.md` — invariants tracked by diagnostics events (VPC.B, VPC.E, VPC.T, VPC.F)
- `docs/visited-places/scenarios.md` — user/background/eviction/TTL scenarios referenced in event descriptions
- `docs/visited-places/actors.md` — actor responsibilities and component locations where events are recorded
- `docs/visited-places/eviction.md` — eviction architecture (policy-pressure-selector model)
