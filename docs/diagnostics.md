# Cache Diagnostics - Instrumentation and Observability

## Overview

The Sliding Window Cache provides optional diagnostics instrumentation for monitoring cache behavior, measuring performance, validating system invariants, and understanding operational characteristics. The diagnostics system is designed as a **zero-cost abstraction** - when not used, it adds absolutely no runtime overhead.

---

## Purpose and Use Cases

### Primary Use Cases

1. **Testing and Validation**
   - Verify cache behavior matches expected patterns
   - Validate system invariants during test execution
   - Assert specific cache scenarios (hit/miss patterns, rebalance lifecycle)
   - Enable deterministic testing with observable state

2. **Performance Monitoring**
   - Track cache hit/miss ratios in production or staging
   - Measure rebalance frequency and patterns
   - Identify access pattern inefficiencies
   - Quantify data source interaction costs

3. **Debugging and Development**
   - Understand cache lifecycle events during development
   - Trace User Path vs. Rebalance Execution behavior
   - Identify unexpected cancellation patterns
   - Verify optimization effectiveness (skip conditions)

4. **Production Observability** (Optional)
   - Export metrics to monitoring systems
   - Track cache efficiency over time
   - Correlate cache behavior with application performance
   - Identify degradation patterns

---

## Architecture

### Interface: `ICacheDiagnostics`

The diagnostics system is built around the `ICacheDiagnostics` interface, which defines 18 event recording methods corresponding to key cache behavioral events:

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

### Implementations

#### `EventCounterCacheDiagnostics` - Default Implementation

Thread-safe counter-based implementation that tracks all events using `Interlocked.Increment` for atomicity:

```csharp
var diagnostics = new EventCounterCacheDiagnostics();

// Pass to cache constructor
var cache = new WindowCache<int, string, IntegerFixedStepDomain>(
    dataSource: myDataSource,
    domain: new IntegerFixedStepDomain(),
    options: options,
    cacheDiagnostics: diagnostics
);

// Read counters
Console.WriteLine($"Cache hits: {diagnostics.UserRequestFullCacheHit}");
Console.WriteLine($"Rebalances: {diagnostics.RebalanceExecutionCompleted}");
```

**Features:**
- ✅ Thread-safe (uses `Interlocked.Increment`)
- ✅ Low overhead (integer increment per event)
- ✅ Read-only properties for all 18 counters (17 counters + 1 exception event)
- ✅ `Reset()` method for test isolation
- ✅ Instance-based (multiple caches can have separate diagnostics)
- ⚠️ **Warning**: Default implementation only writes RebalanceExecutionFailed to Debug output

**Use for:**
- Testing and validation
- Development and debugging
- Production monitoring (acceptable overhead)

**⚠️ CRITICAL: Production Usage Requirement**

The default `EventCounterCacheDiagnostics` implementation of `RebalanceExecutionFailed` only writes to Debug output. **For production use, you MUST create a custom implementation that logs to your logging infrastructure.**

```csharp
public class ProductionCacheDiagnostics : ICacheDiagnostics
{
    private readonly ILogger<ProductionCacheDiagnostics> _logger;
    private int _userRequestServed;
    // ...other counters...
    
    public ProductionCacheDiagnostics(ILogger<ProductionCacheDiagnostics> logger)
    {
        _logger = logger;
    }
    
    public void RebalanceExecutionFailed(Exception ex)
    {
        // CRITICAL: Always log rebalance failures with full context
        _logger.LogError(ex, 
            "Cache rebalance execution failed. Cache may not be optimally sized. " +
            "Subsequent user requests will still be served but rebalancing has stopped.");
    }
    
    // ...implement other diagnostic methods...
}
```

**Why this is critical:**

Rebalance operations run in fire-and-forget background tasks. When exceptions occur:
1. The exception is caught and recorded via `RebalanceExecutionFailed`
2. The exception is swallowed to prevent application crashes
3. Without logging, failures are **completely silent**

Ignoring this event means:
- ❌ Data source errors go unnoticed
- ❌ Cache stops rebalancing with no indication
- ❌ Performance degrades silently
- ❌ No diagnostics for troubleshooting

**Recommended production implementation:**
- Always log with full exception details (message, stack trace, inner exceptions)
- Include structured context (cache instance ID, requested range if available)
- Consider alerting for repeated failures (circuit breaker pattern)
- Track failure rate metrics for monitoring dashboards

#### `NoOpDiagnostics` - Zero-Cost Implementation

Empty implementation with no-op methods that the JIT can optimize away completely:

```csharp
// Automatically used when cacheDiagnostics parameter is omitted
var cache = new WindowCache<int, string, IntegerFixedStepDomain>(
    dataSource: myDataSource,
    domain: new IntegerFixedStepDomain(),
    options: options
    // cacheDiagnostics: null (default) -> uses NoOpDiagnostics
);
```

**Features:**
- ✅ **Absolute zero overhead** - methods are empty and get inlined/eliminated
- ✅ No memory allocations
- ✅ No performance impact whatsoever
- ✅ Default when diagnostics not provided

**Use for:**
- Production deployments where diagnostics are not needed
- Performance-critical scenarios
- When observability is handled externally

---

## Diagnostic Events Reference

### User Path Events

#### `UserRequestServed()`
**Tracks:** Completion of user request (data returned to caller)  
**Location:** `UserRequestHandler.HandleRequestAsync` (final step, inside `!exceptionOccurred` block)  
**Scenarios:** All user scenarios (U1-U5) and physical boundary miss (full vacuum)  
**Fires when:** No exception occurred — regardless of whether a rebalance intent was published  
**Does NOT fire when:** An exception propagated out of `HandleRequestAsync`  
**Interpretation:** Total number of user requests that completed without exception (including boundary misses where `Range == null`)

**Example Usage:**
```csharp
await cache.GetDataAsync(Range.Closed(100, 200), ct);
Assert.Equal(1, diagnostics.UserRequestServed);
```

---

#### `CacheExpanded()`
**Tracks:** Cache expansion during partial cache hit  
**Location:** `CacheDataExtensionService.CalculateMissingRanges` (intersection path)  
**Scenarios:** User Scenario U4 (partial cache hit)  
**Invariant:** Invariant A.12b (Cache Contiguity Rule - preserves contiguity)  
**Interpretation:** Number of times cache grew while maintaining contiguity

**Example Usage:**
```csharp
// Initial request: [100, 200]
await cache.GetDataAsync(Range.Closed(100, 200), ct);

// Overlapping request: [150, 250] - triggers expansion
await cache.GetDataAsync(Range.Closed(150, 250), ct);

Assert.Equal(1, diagnostics.CacheExpanded);
```

---

#### `CacheReplaced()`
**Tracks:** Cache replacement during non-intersecting jump  
**Location:** `CacheDataExtensionService.CalculateMissingRanges` (no intersection path)  
**Scenarios:** User Scenario U5 (full cache miss - jump)  
**Invariant:** Invariant A.12b (Cache Contiguity Rule - prevents gaps)  
**Interpretation:** Number of times cache was fully replaced to maintain contiguity

**Example Usage:**
```csharp
// Initial request: [100, 200]
await cache.GetDataAsync(Range.Closed(100, 200), ct);

// Non-intersecting request: [500, 600] - triggers replacement
await cache.GetDataAsync(Range.Closed(500, 600), ct);

Assert.Equal(1, diagnostics.CacheReplaced);
```

---

#### `UserRequestFullCacheHit()`
**Tracks:** Request served entirely from cache (no data source access)  
**Location:** `UserRequestHandler.HandleRequestAsync` (Scenario 2)  
**Scenarios:** User Scenarios U2, U3 (full cache hit)  
**Interpretation:** Optimal performance - requested range fully contained in cache

**Per-request programmatic alternative:** `result.CacheInteraction == CacheInteraction.FullHit` on the returned `RangeResult`. `ICacheDiagnostics` callbacks are aggregate counters; `CacheInteraction` is the per-call value for branching logic (e.g., `GetDataAndWaitOnMissAsync` uses it to skip `WaitForIdleAsync` on full hits).

**Example Usage:**
```csharp
// Request 1: [100, 200] - cache miss, cache becomes [100, 200]
await cache.GetDataAsync(Range.Closed(100, 200), ct);

// Request 2: [120, 180] - fully within [100, 200]
await cache.GetDataAsync(Range.Closed(120, 180), ct);

Assert.Equal(1, diagnostics.UserRequestFullCacheHit);
```

---

#### `UserRequestPartialCacheHit()`
**Tracks:** Request with partial cache overlap (fetch missing segments)  
**Location:** `UserRequestHandler.HandleRequestAsync` (Scenario 3)  
**Scenarios:** User Scenario U4 (partial cache hit)  
**Interpretation:** Efficient cache extension - some data reused, missing parts fetched

**Per-request programmatic alternative:** `result.CacheInteraction == CacheInteraction.PartialHit` on the returned `RangeResult`.

**Example Usage:**
```csharp
// Request 1: [100, 200]
await cache.GetDataAsync(Range.Closed(100, 200), ct);

// Request 2: [150, 250] - overlaps with [100, 200]
await cache.GetDataAsync(Range.Closed(150, 250), ct);

Assert.Equal(1, diagnostics.UserRequestPartialCacheHit);
```

---

#### `UserRequestFullCacheMiss()`
**Tracks:** Request requiring complete fetch from data source  
**Location:** `UserRequestHandler.HandleRequestAsync` (Scenarios 1 and 4)  
**Scenarios:** U1 (cold start), U5 (non-intersecting jump)  
**Interpretation:** Most expensive path - no cache reuse

**Per-request programmatic alternative:** `result.CacheInteraction == CacheInteraction.FullMiss` on the returned `RangeResult`.

**Example Usage:**
```csharp
// Cold start - no cache
await cache.GetDataAsync(Range.Closed(100, 200), ct);
Assert.Equal(1, diagnostics.UserRequestFullCacheMiss);

// Jump to non-intersecting range
await cache.GetDataAsync(Range.Closed(500, 600), ct);
Assert.Equal(2, diagnostics.UserRequestFullCacheMiss);
```

---

### Data Source Access Events

#### `DataSourceFetchSingleRange()`
**Tracks:** Single contiguous range fetch from `IDataSource`  
**Location:** `UserRequestHandler.HandleRequestAsync` (cold start or jump)  
**API Called:** `IDataSource.FetchAsync(Range<TRange>, CancellationToken)`  
**Interpretation:** Complete range fetched as single operation

**Example Usage:**
```csharp
// Cold start or jump - fetches entire range as one operation
await cache.GetDataAsync(Range.Closed(100, 200), ct);
Assert.Equal(1, diagnostics.DataSourceFetchSingleRange);
```

---

#### `DataSourceFetchMissingSegments()`
**Tracks:** Missing segments fetch (gap filling optimization)  
**Location:** `CacheDataExtensionService.ExtendCacheAsync`  
**API Called:** `IDataSource.FetchAsync(IEnumerable<Range<TRange>>, CancellationToken)`  
**Interpretation:** Optimized fetch of only missing data segments

**Example Usage:**
```csharp
// Request 1: [100, 200]
await cache.GetDataAsync(Range.Closed(100, 200), ct);

// Request 2: [150, 250] - fetches only [201, 250]
await cache.GetDataAsync(Range.Closed(150, 250), ct);

Assert.Equal(1, diagnostics.DataSourceFetchMissingSegments);
```

---

#### `DataSegmentUnavailable()`
**Tracks:** A fetched chunk returned a `null` Range — the requested segment does not exist in the data source  
**Location:** `CacheDataExtensionService.UnionAll` (when a `RangeChunk.Range` is null)  
**Context:** User Thread (Partial Cache Hit — Scenario 3) **and** Background Thread (Rebalance Execution)  
**Invariants:** G.5 (IDataSource Boundary Semantics), A.12b (Cache Contiguity)  
**Interpretation:** Physical boundary encountered; the unavailable segment is silently skipped to preserve cache contiguity

**Typical Scenarios:**
- Database with min/max ID bounds — extension tries to expand beyond available range
- Time-series data with temporal limits — requesting future/past data not yet/no longer available
- Paginated API with maximum pages — attempting to fetch beyond last page

**Important:** This is purely informational. The system gracefully skips unavailable segments during `UnionAll`, and cache contiguity is preserved. No action is required by the caller.

**Example Usage:**
```csharp
// BoundedDataSource has data in [1000, 9999]
// Request [500, 1500] overlaps lower boundary — partial cache hit fetches [500, 999] which returns null
var result = await cache.GetDataAsync(Range.Closed(500, 1500), ct);
await cache.WaitForIdleAsync();

// At least one unavailable segment was encountered during extension
Assert.True(diagnostics.DataSegmentUnavailable >= 1);

// Cache contiguity preserved — result is the intersection of requested and available
Assert.Equal(Range.Closed(1000, 1500), result.Range);
```

---

### Rebalance Intent Lifecycle Events

#### `RebalanceIntentPublished()`
**Tracks:** Rebalance intent publication by User Path  
**Location:** `IntentController.PublishIntent` (after scheduler receives intent)  
**Invariants:** A.5 (User Path is sole source of intent), C.8e (Intent contains delivered data)  
**Note:** Intent publication does NOT guarantee execution (opportunistic)

**Example Usage:**
```csharp
await cache.GetDataAsync(Range.Closed(100, 200), ct);

// Intent is published when data was successfully assembled (not on physical boundary misses)
Assert.Equal(1, diagnostics.RebalanceIntentPublished);
```

---

#### `RebalanceIntentCancelled()`
**Tracks:** Intent cancellation before or during execution  
**Location:** `IntentController.ProcessIntentsAsync` (background loop — when new intent supersedes pending intent)  
**Invariants:** A.2 (User Path priority), A.2a (User cancels rebalance), C.4 (Obsolete intent doesn't start)  
**Interpretation:** Single-flight execution - new request cancels previous intent

**Example Usage:**
```csharp
var options = new WindowCacheOptions(debounceDelay: TimeSpan.FromSeconds(1));
var cache = TestHelpers.CreateCache(domain, diagnostics, options);

// Request 1 - publishes intent, starts debounce delay
var task1 = cache.GetDataAsync(Range.Closed(100, 200), ct);

// Request 2 (before debounce completes) - cancels previous intent
var task2 = cache.GetDataAsync(Range.Closed(300, 400), ct);

await Task.WhenAll(task1, task2);
await cache.WaitForIdleAsync();

Assert.True(diagnostics.RebalanceIntentCancelled >= 1);
```

---

### Rebalance Execution Lifecycle Events

#### `RebalanceExecutionStarted()`
**Tracks:** Rebalance execution start after decision approval  
**Location:** `IntentController.ProcessIntentsAsync` (after `RebalanceDecisionEngine` approves execution)  
**Scenarios:** Decision Scenario D3 (rebalance required)  
**Invariant:** D.5 (Rebalance triggered only if confirmed necessary)

**Example Usage:**
```csharp
await cache.GetDataAsync(Range.Closed(100, 200), ct);
await cache.WaitForIdleAsync();

Assert.Equal(1, diagnostics.RebalanceExecutionStarted);
```

---

#### `RebalanceExecutionCompleted()`
**Tracks:** Successful rebalance completion  
**Location:** `RebalanceExecutor.ExecuteAsync` (after UpdateCacheState)  
**Scenarios:** Rebalance Scenarios R1, R2 (build from scratch, expand cache)  
**Invariants:** F.2 (Only Rebalance writes to cache), B.2 (Cache updates are atomic)

**Example Usage:**
```csharp
await cache.GetDataAsync(Range.Closed(100, 200), ct);
await cache.WaitForIdleAsync();

Assert.Equal(1, diagnostics.RebalanceExecutionCompleted);
```

---

#### `RebalanceExecutionCancelled()`
**Tracks:** Rebalance cancellation mid-flight  
**Location:** `RebalanceExecutor.ExecuteAsync` (catch `OperationCanceledException`)  
**Invariant:** F.1a (Rebalance yields to User Path immediately)  
**Interpretation:** User Path priority enforcement - rebalance interrupted

**Example Usage:**
```csharp
// Long-running rebalance scenario
await cache.GetDataAsync(Range.Closed(100, 200), ct);

// New request while rebalance is executing
await cache.GetDataAsync(Range.Closed(300, 400), ct);
await cache.WaitForIdleAsync();

// First rebalance was cancelled
Assert.True(diagnostics.RebalanceExecutionCancelled >= 1);
```

---

#### `RebalanceExecutionFailed(Exception ex)` ⚠️ CRITICAL
**Tracks:** Rebalance execution failure due to exception  
**Location:** `RebalanceExecutor.ExecuteAsync` (catch `Exception`)  
**Interpretation:** **CRITICAL ERROR** - background rebalance operation failed

**⚠️ WARNING: This event MUST be handled in production applications**

Rebalance operations execute in fire-and-forget background tasks. When an exception occurs:
1. The exception is caught and this event is recorded
2. The exception is silently swallowed to prevent application crashes
3. The cache continues serving user requests but rebalancing stops

**Consequences of ignoring this event:**
- ❌ Silent failures in background operations
- ❌ Cache stops rebalancing without any indication
- ❌ Performance degrades with no diagnostics
- ❌ Data source errors go completely unnoticed
- ❌ Impossible to troubleshoot production issues

**Minimum requirement: Always log**

```csharp
public void RebalanceExecutionFailed(Exception ex)
{
    _logger.LogError(ex, 
        "Cache rebalance execution failed. Cache will continue serving user requests " +
        "but rebalancing has stopped. Investigate data source health and cache configuration.");
}
```

**Recommended production implementation:**

```csharp
public class RobustCacheDiagnostics : ICacheDiagnostics
{
    private readonly ILogger _logger;
    private readonly IMetrics _metrics;
    private int _consecutiveFailures;
    
    public void RebalanceExecutionFailed(Exception ex)
    {
        // 1. Always log with full context
        _logger.LogError(ex, 
            "Cache rebalance execution failed. ConsecutiveFailures: {Failures}",
            Interlocked.Increment(ref _consecutiveFailures));
        
        // 2. Track metrics for monitoring
        _metrics.Counter("cache.rebalance.failures", 1);
        
        // 3. Alert on repeated failures (circuit breaker)
        if (_consecutiveFailures >= 5)
        {
            _logger.LogCritical(
                "Cache rebalancing has failed {Failures} times consecutively. " +
                "Consider investigating data source health or disabling cache.",
                _consecutiveFailures);
        }
    }
    
    public void RebalanceExecutionCompleted()
    {
        // Reset failure counter on success
        Interlocked.Exchange(ref _consecutiveFailures, 0);
    }
    
    // ...other methods...
}
```

**Common failure scenarios:**
- Data source timeouts or connectivity issues
- Data source throws exceptions for specific ranges
- Memory pressure during large cache expansions
- Serialization/deserialization failures
- Configuration errors (invalid ranges, domain issues)

**Example Usage (Testing):**
```csharp
// Simulate data source failure
var faultyDataSource = new FaultyDataSource();
var cache = new WindowCache<int, string, IntegerFixedStepDomain>(
    dataSource: faultyDataSource,
    domain: new IntegerFixedStepDomain(),
    options: options,
    cacheDiagnostics: diagnostics
);

await cache.GetDataAsync(Range.Closed(100, 200), ct);
await cache.WaitForIdleAsync();

// Verify failure was recorded
Assert.Equal(1, diagnostics.RebalanceExecutionFailed);
```

---

### Rebalance Skip / Schedule Optimization Events

#### `RebalanceSkippedCurrentNoRebalanceRange()`
**Tracks:** Rebalance skipped — last requested position is within the current `NoRebalanceRange`  
**Location:** `RebalanceDecisionEngine.Evaluate` (Stage 1 early exit)  
**Scenarios:** Decision Scenario D1 (inside current no-rebalance threshold)  
**Invariants:** D.3 (No rebalance if inside NoRebalanceRange), C.8b (RebalanceSkippedNoRebalanceRange counter semantics)

**Example Usage:**
```csharp
var options = new WindowCacheOptions(
    leftThreshold: 0.3,
    rightThreshold: 0.3
);

// Request 1 establishes cache and NoRebalanceRange
await cache.GetDataAsync(Range.Closed(100, 200), ct);
await cache.WaitForIdleAsync();

// Request 2 inside current NoRebalanceRange - skips rebalance (Stage 1)
await cache.GetDataAsync(Range.Closed(120, 180), ct);
await cache.WaitForIdleAsync();

Assert.True(diagnostics.RebalanceSkippedCurrentNoRebalanceRange >= 1);
```

---

#### `RebalanceSkippedPendingNoRebalanceRange()`
**Tracks:** Rebalance skipped — last requested position is within the *pending* (desired) `NoRebalanceRange` of an already-scheduled execution  
**Location:** `RebalanceDecisionEngine.Evaluate` (Stage 2 early exit)  
**Scenarios:** Decision Scenario D2 (pending rebalance covers the request — anti-thrashing)  
**Invariants:** D.2a (No rebalance if pending rebalance covers request)

**Example Usage:**
```csharp
// Request 1 publishes intent and schedules execution
var _ = cache.GetDataAsync(Range.Closed(100, 200), ct);

// Request 2 (before debounce completes) — pending execution already covers it
await cache.GetDataAsync(Range.Closed(110, 190), ct);
await cache.WaitForIdleAsync();

Assert.True(diagnostics.RebalanceSkippedPendingNoRebalanceRange >= 1);
```

---

#### `RebalanceSkippedSameRange()`
**Tracks:** Rebalance skipped because desired cache range equals current cache range  
**Location:** `RebalanceDecisionEngine.Evaluate` (Stage 4 early exit)  
**Scenarios:** Decision Scenario D3 (DesiredCacheRange == CurrentCacheRange)  
**Invariants:** D.4 (No rebalance if same range), C.8c (RebalanceSkippedSameRange counter semantics)

**Example Usage:**
```csharp
// Delivered data range already matches desired range
await cache.GetDataAsync(Range.Closed(100, 200), ct);
await cache.WaitForIdleAsync();

// Rebalance started but detected same-range condition
Assert.True(diagnostics.RebalanceSkippedSameRange >= 0); // May or may not occur
```

---

#### `RebalanceScheduled()`
**Tracks:** Rebalance execution successfully scheduled after all decision stages approved  
**Location:** `IntentController.ProcessIntentsAsync` (Stage 5 — after `RebalanceDecisionEngine` returns `ShouldSchedule=true`)  
**Scenarios:** Decision Scenario D4 (rebalance required)  
**Invariant:** D.5 (Rebalance triggered only if confirmed necessary)

**Example Usage:**
```csharp
await cache.GetDataAsync(Range.Closed(100, 200), ct);
await cache.WaitForIdleAsync();

// Every completed execution was preceded by a scheduling event
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
    
    // Reset to isolate test scenario
    diagnostics.Reset();
    
    // Test
    await cache.GetDataAsync(Range.Closed(120, 180), ct);
    
    // Assert only test scenario events
    Assert.Equal(1, diagnostics.UserRequestFullCacheHit);
    Assert.Equal(0, diagnostics.UserRequestPartialCacheHit);
    Assert.Equal(0, diagnostics.UserRequestFullCacheMiss);
}
```

---

### Invariant Validation

```csharp
public static void AssertRebalanceLifecycleIntegrity(EventCounterCacheDiagnostics d)
{
    // Published >= Started (some intents may be cancelled before execution)
    Assert.True(d.RebalanceIntentPublished >= d.RebalanceExecutionStarted);
    
    // Started == Completed + Cancelled (every started execution completes or is cancelled)
    Assert.Equal(d.RebalanceExecutionStarted, 
                 d.RebalanceExecutionCompleted + d.RebalanceExecutionCancelled);
}
```

---

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

### Runtime Overhead

**`EventCounterCacheDiagnostics` (when enabled):**
- ~1-5 nanoseconds per event (single `Interlocked.Increment`)
- Negligible compared to cache operations (microseconds to milliseconds)
- Thread-safe with no locks
- No allocations

**`NoOpDiagnostics` (default):**
- **Absolute zero overhead** - methods are inlined and eliminated by JIT
- No memory footprint
- No performance impact

### Memory Overhead

- `EventCounterCacheDiagnostics`: 72 bytes (18 integers)
- `NoOpDiagnostics`: 0 bytes (no state)

### Recommendation

- **Development/Testing**: Always use `EventCounterCacheDiagnostics`
- **Production**: Use `EventCounterCacheDiagnostics` if monitoring is needed, omit otherwise
- **Performance-critical paths**: Omit diagnostics entirely (uses `NoOpDiagnostics`)

---

## Custom Implementations

You can implement `ICacheDiagnostics` for custom observability scenarios:

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

## Per-Layer Diagnostics in Layered Caches

When using `LayeredWindowCacheBuilder`, each cache layer can be given its own independent
`ICacheDiagnostics` instance. This lets you observe the behavior of each layer in isolation,
which is the primary tool for tuning buffer sizes and thresholds in a multi-layer setup.

### Attaching Diagnostics to Individual Layers

Pass a diagnostics instance as the second argument to `AddLayer`:

```csharp
var l2Diagnostics = new EventCounterCacheDiagnostics();
var l1Diagnostics = new EventCounterCacheDiagnostics();

await using var cache = WindowCacheBuilder.Layered(realDataSource, domain)
    .AddLayer(deepOptions, l2Diagnostics)   // L2: inner / deep layer
    .AddLayer(userOptions, l1Diagnostics)   // L1: outermost / user-facing layer
    .Build();
```

Omit the second argument (or pass `null`) to use the default `NoOpDiagnostics` for that layer.

### What Each Layer's Diagnostics Report

Because each layer is a fully independent `WindowCache`, every `ICacheDiagnostics` event has
the same meaning as documented in the single-cache sections above — but scoped to that layer:

| Event                                     | Meaning in a layered context                                                       |
|-------------------------------------------|------------------------------------------------------------------------------------|
| `UserRequestServed`                       | A request was served by **this layer** (whether from cache or via adapter)         |
| `UserRequestFullCacheHit`                 | The request was served entirely from **this layer's** window                       |
| `UserRequestPartialCacheHit`              | This layer partially served the request; the rest was fetched from the layer below |
| `UserRequestFullCacheMiss`                | This layer had no data; the full request was delegated to the layer below          |
| `DataSourceFetchSingleRange`              | This layer called the layer below (via the adapter) for a single range             |
| `DataSourceFetchMissingSegments`          | This layer called the layer below for gap-filling segments only                    |
| `RebalanceExecutionCompleted`             | This layer completed a background rebalance (window expansion/shrink)              |
| `RebalanceSkippedCurrentNoRebalanceRange` | This layer's rebalance was skipped — still within its stability zone               |

### Detecting Cascading Rebalances

A **cascading rebalance** occurs when the outer layer's rebalance fetches ranges from the
inner layer that fall outside the inner layer's `NoRebalanceRange`, causing the inner layer
to also rebalance. Under correct configuration this should be rare. Under misconfiguration
it becomes continuous and defeats the purpose of layering.

**Primary indicator — compare rebalance completion counts:**

```csharp
// After a sustained sequential access session:
var l1Rate = l1Diagnostics.RebalanceExecutionCompleted;
var l2Rate = l2Diagnostics.RebalanceExecutionCompleted;

// Healthy: L2 rebalances much less often than L1
// l2Rate should be << l1Rate for normal sequential access

// Unhealthy: L2 rebalances nearly as often as L1
// l2Rate ≈ l1Rate  →  cascading rebalance thrashing
```

**Secondary confirmation — check skip counts on the inner layer:**

```csharp
// Under correct configuration, the inner layer's Decision Engine
// should reject most L1-driven intents at Stage 1 (NoRebalanceRange containment).
// This counter should be much higher than l2.RebalanceExecutionCompleted.
var l2SkippedStage1 = l2Diagnostics.RebalanceSkippedCurrentNoRebalanceRange;

// Healthy ratio: l2SkippedStage1 >> l2Rate
// Unhealthy ratio: l2SkippedStage1 ≈ 0 while l2Rate is high
```

**Confirming the data source is being hit too frequently:**

```csharp
// If the inner layer is rebalancing on every L1 rebalance,
// it will also be fetching from the real data source frequently.
// This counter on the innermost layer should grow slowly under correct config.
var dataSourceFetches = lInnerDiagnostics.DataSourceFetchMissingSegments
                      + lInnerDiagnostics.DataSourceFetchSingleRange;
```

**Resolution checklist when cascading is detected:**

1. Increase inner layer `leftCacheSize` and `rightCacheSize` to 5–10× the outer layer's values
2. Set inner layer `leftThreshold` and `rightThreshold` to 0.2–0.3
3. Re-run the access pattern and verify `l2.RebalanceSkippedCurrentNoRebalanceRange` dominates
4. See `docs/architecture.md` (Cascading Rebalance Behavior) and `docs/scenarios.md` (L6, L7)
   for a full explanation of the mechanics and the anti-pattern
```
l2Diagnostics.UserRequestFullCacheHit / l2Diagnostics.UserRequestServed
```
A low hit rate on the inner layer means L1 is frequently delegating to L2 — consider
increasing L2's buffer sizes (`leftCacheSize` / `rightCacheSize`).

**Outer layer hit rate:**
```
l1Diagnostics.UserRequestFullCacheHit / l1Diagnostics.UserRequestServed
```
The outer layer hit rate is what users directly experience. If it is low, consider increasing
L1's buffer size or tightening the `leftThreshold` / `rightThreshold` to reduce rebalancing.

**Real data source access rate (bypassing all layers):**

Monitor `l_innermost_diagnostics.DataSourceFetchSingleRange` or
`DataSourceFetchMissingSegments` on the innermost layer. These represent requests that went
all the way to the real data source. Reducing this rate (by widening inner layer buffers) is
the primary goal of a multi-layer setup.

**Rebalance frequency:**
```
l1Diagnostics.RebalanceExecutionCompleted   // How often L1 is re-centering
l2Diagnostics.RebalanceExecutionCompleted   // How often L2 is re-centering
```
If L1 rebalances much more frequently than L2, it is either too narrowly configured or the
access pattern has high variability. Consider loosening L1's thresholds or widening L2.

### Production Guidance for Layered Caches

- **Always handle `RebalanceExecutionFailed` on each layer.** Background rebalance failures
  on any layer are silent without a proper implementation. See the production requirements
  section above — they apply to every layer independently.

- **Use separate `EventCounterCacheDiagnostics` instances per layer** during development
  and staging to establish baseline metrics. In production, replace with custom
  implementations that export to your monitoring infrastructure.

- **Layer diagnostics are completely independent.** There is no aggregate or combined
  diagnostics object; you observe each layer separately and interpret the metrics in
  relation to each other.

---

## See Also

- **[Invariants](invariants.md)** - System invariants tracked by diagnostics
- **[Scenarios](scenarios.md)** - User/Decision/Rebalance scenarios referenced in event descriptions
- **[Invariant Test Suite](../tests/SlidingWindowCache.Invariants.Tests/README.md)** - Examples of diagnostic usage in tests
- **[Components](components/overview.md)** - Component locations where events are recorded
