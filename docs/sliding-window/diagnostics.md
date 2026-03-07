# Diagnostics — SlidingWindow Cache

For the shared diagnostics pattern (two-tier design, zero-cost abstraction, `RebalanceExecutionFailed` critical requirement), see `docs/shared/diagnostics.md`. This document covers the `ICacheDiagnostics` interface, all 18 events, and SWC-specific usage patterns.

---

## Interface: `ICacheDiagnostics`

```csharp
public interface ICacheDiagnostics
{
    // User Path Events
    void UserRequestServed();
    void CacheExpanded();
    void CacheReplaced();
    void UserRequestFullCacheHit();
    void UserRequestPartialCacheHit();
    void UserRequestFullCacheMiss();

    // Data Source Access Events
    void DataSourceFetchSingleRange();
    void DataSourceFetchMissingSegments();
    void DataSegmentUnavailable();

    // Rebalance Intent Lifecycle Events
    void RebalanceIntentPublished();

    // Rebalance Execution Lifecycle Events
    void RebalanceExecutionStarted();
    void RebalanceExecutionCompleted();
    void RebalanceExecutionCancelled();

    // Rebalance Skip / Schedule Optimization Events
    void RebalanceSkippedCurrentNoRebalanceRange();   // Stage 1: current NoRebalanceRange
    void RebalanceSkippedPendingNoRebalanceRange();   // Stage 2: pending NoRebalanceRange
    void RebalanceSkippedSameRange();                 // Stage 4: desired == current range
    void RebalanceScheduled();                        // Stage 5: execution scheduled

    // Failure Events
    void RebalanceExecutionFailed(Exception ex);
}
```

---

## Implementations

### `EventCounterCacheDiagnostics` — Default Implementation

Thread-safe counter-based implementation using `Interlocked.Increment`:

```csharp
var diagnostics = new EventCounterCacheDiagnostics();

var cache = new SlidingWindowCache<int, string, IntegerFixedStepDomain>(
    dataSource: myDataSource,
    domain: new IntegerFixedStepDomain(),
    options: options,
    cacheDiagnostics: diagnostics
);

Console.WriteLine($"Cache hits: {diagnostics.UserRequestFullCacheHit}");
Console.WriteLine($"Rebalances: {diagnostics.RebalanceExecutionCompleted}");
```

Features:
- Thread-safe (`Interlocked.Increment`)
- Low overhead (~1–5 ns per event)
- Read-only properties for all 18 counters
- `Reset()` method for test isolation
- Instance-based (multiple caches can have separate diagnostics)

**WARNING**: The default `EventCounterCacheDiagnostics` implementation of `RebalanceExecutionFailed` only writes to Debug output. For production use, you MUST create a custom implementation that logs to your logging infrastructure. See `docs/shared/diagnostics.md` for requirements.

### `NoOpDiagnostics` — Zero-Cost Implementation

Empty implementation with no-op methods that the JIT eliminates completely. Automatically used when the `cacheDiagnostics` parameter is omitted.

### Custom Implementations

```csharp
public class PrometheusMetricsDiagnostics : ICacheDiagnostics
{
    private readonly Counter _requestsServed;
    private readonly Counter _cacheHits;
    private readonly Counter _cacheMisses;

    public PrometheusMetricsDiagnostics(IMetricFactory metricFactory)
    {
        _requestsServed = metricFactory.CreateCounter("cache_requests_total");
        _cacheHits = metricFactory.CreateCounter("cache_hits_total");
        _cacheMisses = metricFactory.CreateCounter("cache_misses_total");
    }

    public void UserRequestServed() => _requestsServed.Inc();
    public void UserRequestFullCacheHit() => _cacheHits.Inc();
    public void UserRequestPartialCacheHit() => _cacheHits.Inc();
    public void UserRequestFullCacheMiss() => _cacheMisses.Inc();

    // ... implement other methods
}
```

---

## Diagnostic Events Reference

### User Path Events

#### `UserRequestServed()`
**Tracks:** Completion of user request (data returned to caller)  
**Location:** `UserRequestHandler.HandleRequestAsync` (final step, inside `!exceptionOccurred` block)  
**Scenarios:** All user scenarios (U1–U5) and physical boundary miss (full vacuum)  
**Fires when:** No exception occurred — regardless of whether a rebalance intent was published  
**Does NOT fire when:** An exception propagated out of `HandleRequestAsync`  
**Interpretation:** Total number of user requests that completed without exception (including boundary misses where `Range == null`)

```csharp
await cache.GetDataAsync(Range.Closed(100, 200), ct);
Assert.Equal(1, diagnostics.UserRequestServed);
```

---

#### `CacheExpanded()`
**Tracks:** Cache expansion during partial cache hit  
**Location:** `CacheDataExtensionService.CalculateMissingRanges` (intersection path)  
**Scenarios:** U4 (partial cache hit)  
**Invariant:** SWC.A.12b (Cache Contiguity Rule — preserves contiguity)

```csharp
await cache.GetDataAsync(Range.Closed(100, 200), ct);
await cache.GetDataAsync(Range.Closed(150, 250), ct);  // overlapping
Assert.Equal(1, diagnostics.CacheExpanded);
```

---

#### `CacheReplaced()`
**Tracks:** Cache replacement during non-intersecting jump  
**Location:** `CacheDataExtensionService.CalculateMissingRanges` (no intersection path)  
**Scenarios:** U5 (full cache miss — jump)  
**Invariant:** SWC.A.12b (Cache Contiguity Rule — prevents gaps)

```csharp
await cache.GetDataAsync(Range.Closed(100, 200), ct);
await cache.GetDataAsync(Range.Closed(500, 600), ct);  // non-intersecting
Assert.Equal(1, diagnostics.CacheReplaced);
```

---

#### `UserRequestFullCacheHit()`
**Tracks:** Request served entirely from cache (no data source access)  
**Location:** `UserRequestHandler.HandleRequestAsync` (Scenario 2)  
**Scenarios:** U2, U3 (full cache hit)

**Per-request programmatic alternative:** `result.CacheInteraction == CacheInteraction.FullHit` on the returned `RangeResult`. `ICacheDiagnostics` callbacks are aggregate counters; `CacheInteraction` is the per-call value for branching logic (e.g., `GetDataAndWaitOnMissAsync` uses it to skip `WaitForIdleAsync` on full hits).

```csharp
await cache.GetDataAsync(Range.Closed(100, 200), ct);
await cache.GetDataAsync(Range.Closed(120, 180), ct);  // fully within [100, 200]
Assert.Equal(1, diagnostics.UserRequestFullCacheHit);
```

---

#### `UserRequestPartialCacheHit()`
**Tracks:** Request with partial cache overlap (fetch missing segments)  
**Location:** `UserRequestHandler.HandleRequestAsync` (Scenario 3)  
**Scenarios:** U4 (partial cache hit)

**Per-request programmatic alternative:** `result.CacheInteraction == CacheInteraction.PartialHit`

```csharp
await cache.GetDataAsync(Range.Closed(100, 200), ct);
await cache.GetDataAsync(Range.Closed(150, 250), ct);  // overlaps
Assert.Equal(1, diagnostics.UserRequestPartialCacheHit);
```

---

#### `UserRequestFullCacheMiss()`
**Tracks:** Request requiring complete fetch from data source  
**Location:** `UserRequestHandler.HandleRequestAsync` (Scenarios 1 and 4)  
**Scenarios:** U1 (cold start), U5 (non-intersecting jump)

**Per-request programmatic alternative:** `result.CacheInteraction == CacheInteraction.FullMiss`

```csharp
await cache.GetDataAsync(Range.Closed(100, 200), ct);   // cold start
Assert.Equal(1, diagnostics.UserRequestFullCacheMiss);
await cache.GetDataAsync(Range.Closed(500, 600), ct);   // jump
Assert.Equal(2, diagnostics.UserRequestFullCacheMiss);
```

---

### Data Source Access Events

#### `DataSourceFetchSingleRange()`
**Tracks:** Single contiguous range fetch from `IDataSource`  
**Location:** `UserRequestHandler.HandleRequestAsync` (cold start or jump)  
**API Called:** `IDataSource.FetchAsync(Range<TRange>, CancellationToken)`

```csharp
await cache.GetDataAsync(Range.Closed(100, 200), ct);
Assert.Equal(1, diagnostics.DataSourceFetchSingleRange);
```

---

#### `DataSourceFetchMissingSegments()`
**Tracks:** Missing segments fetch (gap filling optimization)  
**Location:** `CacheDataExtensionService.ExtendCacheAsync`  
**API Called:** `IDataSource.FetchAsync(IEnumerable<Range<TRange>>, CancellationToken)`

```csharp
await cache.GetDataAsync(Range.Closed(100, 200), ct);
await cache.GetDataAsync(Range.Closed(150, 250), ct);  // fetches only [201, 250]
Assert.Equal(1, diagnostics.DataSourceFetchMissingSegments);
```

---

#### `DataSegmentUnavailable()`
**Tracks:** A fetched chunk returned a `null` Range — the requested segment does not exist in the data source  
**Location:** `CacheDataExtensionService.UnionAll` (when a `RangeChunk.Range` is null)  
**Context:** User Thread (Partial Cache Hit — Scenario U4) **and** Background Thread (Rebalance Execution)  
**Invariants:** SWC.G.5 (`IDataSource` Boundary Semantics), SWC.A.12b (Cache Contiguity)  
**Interpretation:** Physical boundary encountered; the unavailable segment is silently skipped to preserve cache contiguity

Typical scenarios: database with min/max ID bounds, time-series data with temporal limits, paginated API with maximum pages.

This is purely informational. The system gracefully skips unavailable segments during `UnionAll`, and cache contiguity is preserved.

```csharp
// BoundedDataSource has data in [1000, 9999]
// Request [500, 1500] overlaps lower boundary — partial cache hit fetches [500, 999] which returns null
var result = await cache.GetDataAsync(Range.Closed(500, 1500), ct);
await cache.WaitForIdleAsync();
Assert.True(diagnostics.DataSegmentUnavailable >= 1);
Assert.Equal(Range.Closed(1000, 1500), result.Range);
```

---

### Rebalance Intent Lifecycle Events

#### `RebalanceIntentPublished()`
**Tracks:** Rebalance intent publication by User Path  
**Location:** `IntentController.PublishIntent` (after scheduler receives intent)  
**Invariants:** SWC.A.5 (User Path is sole source of intent), SWC.C.8e (Intent contains delivered data)  
**Note:** Intent publication does NOT guarantee execution (opportunistic)

```csharp
await cache.GetDataAsync(Range.Closed(100, 200), ct);
Assert.Equal(1, diagnostics.RebalanceIntentPublished);
```

---

#### `RebalanceIntentCancelled()`
**Tracks:** Intent cancellation before or during execution  
**Location:** `IntentController.ProcessIntentsAsync` (background loop — when new intent supersedes pending intent)  
**Invariants:** SWC.A.2 (User Path priority), SWC.A.2a (User cancels rebalance), SWC.C.4 (Obsolete intent doesn't start)

```csharp
var options = new SlidingWindowCacheOptions(debounceDelay: TimeSpan.FromSeconds(1));
var cache = TestHelpers.CreateCache(domain, diagnostics, options);

var task1 = cache.GetDataAsync(Range.Closed(100, 200), ct);
var task2 = cache.GetDataAsync(Range.Closed(300, 400), ct);  // cancels previous

await Task.WhenAll(task1, task2);
await cache.WaitForIdleAsync();
Assert.True(diagnostics.RebalanceIntentCancelled >= 1);
```

---

### Rebalance Execution Lifecycle Events

#### `RebalanceExecutionStarted()`
**Tracks:** Rebalance execution start after decision approval  
**Location:** `IntentController.ProcessIntentsAsync` (after `RebalanceDecisionEngine` approves execution)  
**Scenarios:** D3 (rebalance required)  
**Invariant:** SWC.D.5 (Rebalance triggered only if confirmed necessary)

```csharp
await cache.GetDataAsync(Range.Closed(100, 200), ct);
await cache.WaitForIdleAsync();
Assert.Equal(1, diagnostics.RebalanceExecutionStarted);
```

---

#### `RebalanceExecutionCompleted()`
**Tracks:** Successful rebalance completion  
**Location:** `RebalanceExecutor.ExecuteAsync` (after `UpdateCacheState`)  
**Scenarios:** R1, R2 (build from scratch, expand cache)  
**Invariants:** SWC.F.2 (Only Rebalance writes to cache), SWC.B.2 (Cache updates are atomic)

```csharp
await cache.GetDataAsync(Range.Closed(100, 200), ct);
await cache.WaitForIdleAsync();
Assert.Equal(1, diagnostics.RebalanceExecutionCompleted);
```

---

#### `RebalanceExecutionCancelled()`
**Tracks:** Rebalance cancellation mid-flight  
**Location:** `RebalanceExecutor.ExecuteAsync` (catch `OperationCanceledException`)  
**Invariant:** SWC.F.1a (Rebalance yields to User Path immediately)

```csharp
await cache.GetDataAsync(Range.Closed(100, 200), ct);
await cache.GetDataAsync(Range.Closed(300, 400), ct);  // new request while rebalance executing
await cache.WaitForIdleAsync();
Assert.True(diagnostics.RebalanceExecutionCancelled >= 1);
```

---

#### `RebalanceExecutionFailed(Exception ex)` — CRITICAL

**Tracks:** Rebalance execution failure due to exception  
**Location:** `RebalanceExecutor.ExecuteAsync` (catch `Exception`)

**This event MUST be handled in production applications.** See `docs/shared/diagnostics.md` for the full production requirements. Summary:

- Rebalance operations run in fire-and-forget background tasks
- When an exception occurs, it is caught and swallowed to prevent crashes
- Without a proper implementation, failures are completely silent
- Cache stops rebalancing with no indication

```csharp
public void RebalanceExecutionFailed(Exception ex)
{
    _logger.LogError(ex,
        "Cache rebalance execution failed. Cache will continue serving user requests " +
        "but rebalancing has stopped. Investigate data source health and cache configuration.");
}
```

Recommended: log with full context, track metrics, alert on consecutive failures (circuit breaker).

---

### Rebalance Skip / Schedule Optimization Events

#### `RebalanceSkippedCurrentNoRebalanceRange()`
**Tracks:** Rebalance skipped — last requested position is within the current `NoRebalanceRange`  
**Location:** `RebalanceDecisionEngine.Evaluate` (Stage 1 early exit)  
**Scenarios:** D1 (inside current no-rebalance threshold)  
**Invariants:** SWC.D.3, SWC.C.8b

```csharp
var options = new SlidingWindowCacheOptions(leftThreshold: 0.3, rightThreshold: 0.3);
await cache.GetDataAsync(Range.Closed(100, 200), ct);
await cache.WaitForIdleAsync();
await cache.GetDataAsync(Range.Closed(120, 180), ct);  // inside NoRebalanceRange
await cache.WaitForIdleAsync();
Assert.True(diagnostics.RebalanceSkippedCurrentNoRebalanceRange >= 1);
```

---

#### `RebalanceSkippedPendingNoRebalanceRange()`
**Tracks:** Rebalance skipped — last requested position is within the *pending* (desired) `NoRebalanceRange` of an already-scheduled execution  
**Location:** `RebalanceDecisionEngine.Evaluate` (Stage 2 early exit)  
**Scenarios:** D1b (pending rebalance covers the request — anti-thrashing)  
**Invariants:** SWC.D.2a

```csharp
var _ = cache.GetDataAsync(Range.Closed(100, 200), ct);
await cache.GetDataAsync(Range.Closed(110, 190), ct);  // pending execution already covers it
await cache.WaitForIdleAsync();
Assert.True(diagnostics.RebalanceSkippedPendingNoRebalanceRange >= 1);
```

---

#### `RebalanceSkippedSameRange()`
**Tracks:** Rebalance skipped because desired cache range equals current cache range  
**Location:** `RebalanceDecisionEngine.Evaluate` (Stage 4 early exit)  
**Scenarios:** D2 (`DesiredCacheRange == CurrentCacheRange`)  
**Invariants:** SWC.D.4, SWC.C.8c

---

#### `RebalanceScheduled()`
**Tracks:** Rebalance execution successfully scheduled after all decision stages approved  
**Location:** `IntentController.ProcessIntentsAsync` (after `RebalanceDecisionEngine` returns `ShouldSchedule=true`)  
**Invariant:** SWC.D.5 (Rebalance triggered only if confirmed necessary)

```csharp
await cache.GetDataAsync(Range.Closed(100, 200), ct);
await cache.WaitForIdleAsync();
Assert.True(diagnostics.RebalanceScheduled >= diagnostics.RebalanceExecutionCompleted);
```

---

## Testing Patterns

### Test Isolation with Reset()

```csharp
[Fact]
public async Task Test_CacheHitPattern()
{
    var diagnostics = new EventCounterCacheDiagnostics();
    var cache = CreateCache(diagnostics);

    // Setup
    await cache.GetDataAsync(Range.Closed(100, 200), ct);
    await cache.WaitForIdleAsync();

    diagnostics.Reset();  // isolate test scenario

    // Test
    await cache.GetDataAsync(Range.Closed(120, 180), ct);

    Assert.Equal(1, diagnostics.UserRequestFullCacheHit);
    Assert.Equal(0, diagnostics.UserRequestPartialCacheHit);
    Assert.Equal(0, diagnostics.UserRequestFullCacheMiss);
}
```

### Invariant Validation

```csharp
public static void AssertRebalanceLifecycleIntegrity(EventCounterCacheDiagnostics d)
{
    // Published >= Started (some intents may be cancelled before execution)
    Assert.True(d.RebalanceIntentPublished >= d.RebalanceExecutionStarted);

    // Started == Completed + Cancelled
    Assert.Equal(d.RebalanceExecutionStarted,
                 d.RebalanceExecutionCompleted + d.RebalanceExecutionCancelled);
}
```

### User Path Scenario Verification

```csharp
public static void AssertPartialCacheHit(EventCounterCacheDiagnostics d, int expectedCount = 1)
{
    Assert.Equal(expectedCount, d.UserRequestPartialCacheHit);
    Assert.Equal(expectedCount, d.CacheExpanded);
    Assert.Equal(expectedCount, d.DataSourceFetchMissingSegments);
}
```

---

## Performance Considerations

| Implementation | Per-Event Cost | Memory |
|---|---|---|
| `EventCounterCacheDiagnostics` | ~1–5 ns (`Interlocked.Increment`, no alloc) | 72 bytes (18 integers) |
| `NoOpDiagnostics` | Zero (JIT-eliminated) | 0 bytes |

Recommendation:
- **Development/Testing**: Always use `EventCounterCacheDiagnostics`
- **Production**: Use `EventCounterCacheDiagnostics` if monitoring is needed, omit otherwise
- **Performance-critical paths**: Omit diagnostics entirely

---

## Per-Layer Diagnostics in Layered Caches

When using `LayeredRangeCacheBuilder`, each layer can have its own independent `ICacheDiagnostics` instance.

### Attaching Diagnostics to Individual Layers

```csharp
var l2Diagnostics = new EventCounterCacheDiagnostics();
var l1Diagnostics = new EventCounterCacheDiagnostics();

await using var cache = SlidingWindowCacheBuilder.Layered(realDataSource, domain)
    .AddSlidingWindowLayer(deepOptions, l2Diagnostics)   // L2: inner / deep layer
    .AddSlidingWindowLayer(userOptions, l1Diagnostics)   // L1: outermost / user-facing layer
    .Build();
```

Omit the second argument (or pass `null`) to use the default `NoOpDiagnostics` for that layer.

### What Each Layer's Diagnostics Report

| Event | Meaning in a layered context |
|---|---|
| `UserRequestServed` | A request was served by **this layer** (whether from cache or via adapter) |
| `UserRequestFullCacheHit` | The request was served entirely from **this layer's** window |
| `UserRequestPartialCacheHit` | This layer partially served the request; the rest was fetched from the layer below |
| `UserRequestFullCacheMiss` | This layer had no data; the full request was delegated to the layer below |
| `DataSourceFetchSingleRange` | This layer called the layer below (via the adapter) for a single range |
| `DataSourceFetchMissingSegments` | This layer called the layer below for gap-filling segments only |
| `RebalanceExecutionCompleted` | This layer completed a background rebalance (window expansion/shrink) |
| `RebalanceSkippedCurrentNoRebalanceRange` | This layer's rebalance was skipped — still within its stability zone |

### Detecting Cascading Rebalances

A **cascading rebalance** occurs when the outer layer's rebalance fetches ranges from the inner layer that fall outside the inner layer's `NoRebalanceRange`. Under correct configuration this should be rare; under misconfiguration it becomes continuous.

**Primary indicator — compare rebalance completion counts:**

```csharp
var l1Rate = l1Diagnostics.RebalanceExecutionCompleted;
var l2Rate = l2Diagnostics.RebalanceExecutionCompleted;

// Healthy: l2Rate << l1Rate
// Unhealthy: l2Rate ≈ l1Rate → cascading rebalance thrashing
```

**Secondary confirmation — check skip counts on the inner layer:**

```csharp
// Under correct configuration, Stage 1 rejections should dominate:
var l2SkippedStage1 = l2Diagnostics.RebalanceSkippedCurrentNoRebalanceRange;
// Healthy: l2SkippedStage1 >> l2Rate
// Unhealthy: l2SkippedStage1 ≈ 0 while l2Rate is high
```

**Confirming the data source is being hit too frequently:**

```csharp
var dataSourceFetches = lInnerDiagnostics.DataSourceFetchMissingSegments
                      + lInnerDiagnostics.DataSourceFetchSingleRange;
```

**Resolution checklist when cascading is detected:**

1. Increase inner layer `leftCacheSize` and `rightCacheSize` to 5–10× the outer layer's values
2. Set inner layer `leftThreshold` and `rightThreshold` to 0.2–0.3
3. Re-run the access pattern and verify `l2.RebalanceSkippedCurrentNoRebalanceRange` dominates
4. See `docs/sliding-window/architecture.md` (Cascading Rebalance Behavior) and `docs/sliding-window/scenarios.md` (L6, L7)

### Production Guidance for Layered Caches

- Always handle `RebalanceExecutionFailed` on each layer independently.
- Use separate `EventCounterCacheDiagnostics` instances per layer during development and staging.
- Layer diagnostics are completely independent — there is no aggregate or combined diagnostics object.

---

## See Also

- `docs/shared/diagnostics.md` — shared diagnostics pattern, `RebalanceExecutionFailed` production requirements
- `docs/sliding-window/invariants.md` — invariants tracked by diagnostics events
- `docs/sliding-window/scenarios.md` — user/decision/rebalance scenarios referenced in event descriptions
- `docs/sliding-window/components/overview.md` — component locations where events are recorded
