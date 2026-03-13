# Intervals.NET.Caching
## Sliding Window Cache

A read-only, range-based, sequential-optimized cache with decision-driven background rebalancing, three consistency modes (eventual/hybrid/strong), and intelligent work avoidance.

[![CI/CD (SlidingWindow)](https://github.com/blaze6950/Intervals.NET.Caching/actions/workflows/intervals-net-caching-swc.yml/badge.svg)](https://github.com/blaze6950/Intervals.NET.Caching/actions/workflows/intervals-net-caching-swc.yml)
[![CI/CD (VisitedPlaces)](https://github.com/blaze6950/Intervals.NET.Caching/actions/workflows/intervals-net-caching-vpc.yml/badge.svg)](https://github.com/blaze6950/Intervals.NET.Caching/actions/workflows/intervals-net-caching-vpc.yml)
[![NuGet](https://img.shields.io/nuget/v/Intervals.NET.Caching.SlidingWindow.svg)](https://www.nuget.org/packages/Intervals.NET.Caching.SlidingWindow/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Intervals.NET.Caching.SlidingWindow.svg)](https://www.nuget.org/packages/Intervals.NET.Caching.SlidingWindow/)
[![codecov](https://codecov.io/gh/blaze6950/Intervals.NET.Caching/graph/badge.svg?token=RFQBNX7MMD)](https://codecov.io/gh/blaze6950/Intervals.NET.Caching)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![.NET 8.0](https://img.shields.io/badge/.NET-8.0-blue.svg)](https://dotnet.microsoft.com/download/dotnet/8.0)

## Packages

- **`Intervals.NET.Caching`** — shared interfaces, DTOs, layered cache infrastructure
- **`Intervals.NET.Caching.SlidingWindow`** — sliding window cache implementation (sequential-access optimized)
- **`Intervals.NET.Caching.VisitedPlaces`** — visited places cache implementation (random-access optimized, with eviction and TTL)

## What It Is

Optimized for access patterns that move predictably across a domain (scrolling, playback, time-series inspection):

- Serves `GetDataAsync` immediately; background work converges the cache window asynchronously
- Single-writer architecture: only rebalance execution mutates shared cache state
- Decision-driven execution: multi-stage analytical validation prevents thrashing and unnecessary I/O
- Smart eventual consistency: cache converges to optimal configuration while avoiding unnecessary work
- Opt-in hybrid or strong consistency via extension methods (`GetDataAndWaitOnMissAsync`, `GetDataAndWaitForIdleAsync`)

For the canonical architecture docs, see `docs/sliding-window/architecture.md`.

## Install

```bash
dotnet add package Intervals.NET.Caching.SlidingWindow
```

## Sliding Window Cache Concept

Traditional caches work with individual keys. A sliding window cache operates on **continuous ranges** of data:

1. User requests a range (e.g., records 100–200)
2. Cache fetches more than requested (e.g., records 50–300) based on left/right cache coefficients
3. Subsequent requests within the window are served instantly from materialized data
4. Window automatically rebalances when the user moves outside threshold boundaries

### Visual: Requested Range vs. Cache Window

```
Requested Range (what user asks for):
                         [======== USER REQUEST ========]

Actual Cache Window (what cache stores):
    [=== LEFT BUFFER ===][======== USER REQUEST ========][=== RIGHT BUFFER ===]
     ← leftCacheSize      requestedRange size              rightCacheSize →
```

### Visual: Rebalance Trigger

```
Current Cache Window:
[========*===================== CACHE ======================*=======]
         ↑                                                  ↑
 Left Threshold (20%)                              Right Threshold (20%)

Scenario 1: Request within thresholds → No rebalance
[========*===================== CACHE ======================*=======]
              [---- new request ----]  ✓ Served from cache

Scenario 2: Request outside threshold → Rebalance triggered
[========*===================== CACHE ======================*=======]
                                          [---- new request ----]
                                                     ↓
                            🔄 Rebalance: Shift window right
```

### Visual: Configuration Impact

```
Example: User requests range of size 100

leftCacheSize = 1.0, rightCacheSize = 2.0
[==== 100 ====][======= 100 =======][============ 200 ============]
 Left Buffer    Requested Range       Right Buffer

Total Cache Window = 100 + 100 + 200 = 400 items

leftThreshold = 0.2 (20% of 400 = 80 items)
rightThreshold = 0.2 (20% of 400 = 80 items)
```

**Key insight:** Threshold percentages are calculated based on the **total cache window size**, not individual buffer sizes.

## Decision-Driven Rebalance Execution

The cache uses a **decision-driven model** where rebalance necessity is determined by analytical validation, not by blindly executing every user request. This prevents thrashing, reduces unnecessary I/O, and maintains stability under rapid access pattern changes.

```
User Request
     │
     ▼
┌─────────────────────────────────────────────────┐
│  User Path (User Thread — Synchronous)          │
│  • Read from cache or fetch missing data        │
│  • Return data immediately to user              │
│  • Publish intent with delivered data           │
└────────────┬────────────────────────────────────┘
             │
             ▼
┌─────────────────────────────────────────────────┐
│  Decision Engine (Background Loop — CPU-only)   │
│  Stage 1: Current NoRebalanceRange check        │
│  Stage 2: Pending coverage check                │
│  Stage 3: DesiredCacheRange computation         │
│  Stage 4: Desired == Current check              │
│  → Decision: SKIP or SCHEDULE                   │
└────────────┬────────────────────────────────────┘
             │
             ├─── If SKIP: return (work avoidance) ✓
             │
             └─── If SCHEDULE:
                       │
                       ▼
             ┌─────────────────────────────────────┐
             │  Background Rebalance (ThreadPool)  │
             │  • Debounce delay                   │
             │  • Fetch missing data (I/O)         │
             │  • Normalize cache to desired range │
             │  • Update cache state atomically    │
             └─────────────────────────────────────┘
```

Key points:
1. **User requests never block** — data returned immediately, rebalance happens later
2. **Decision happens in background** — CPU-only validation (microseconds) in the intent processing loop
3. **Work avoidance prevents thrashing** — validation may skip rebalance entirely if unnecessary
4. **Only I/O happens asynchronously** — debounce + data fetching + cache updates run in background
5. **Smart eventual consistency** — cache converges to optimal state while avoiding unnecessary operations; opt-in hybrid or strong consistency via extension methods

## Materialization for Fast Access

The cache always materializes data in memory. Two storage strategies are available:

| Strategy                                        | Read                                               | Write                            | Best For                                 |
|-------------------------------------------------|----------------------------------------------------|----------------------------------|------------------------------------------|
| **Snapshot** (`UserCacheReadMode.Snapshot`)     | Zero-allocation (`ReadOnlyMemory<TData>` directly) | Expensive (new array allocation) | Read-heavy workloads                     |
| **CopyOnRead** (`UserCacheReadMode.CopyOnRead`) | Allocates per read (copy)                          | Cheap (`List<T>` operations)     | Frequent rebalancing, memory-constrained |

For detailed comparison and guidance, see `docs/sliding-window/storage-strategies.md`.

## Quick Start

```csharp
using Intervals.NET.Caching;
using Intervals.NET.Caching.SlidingWindow.Public.Cache;
using Intervals.NET.Caching.SlidingWindow.Public.Configuration;
using Intervals.NET;
using Intervals.NET.Domain.Default.Numeric;

await using var cache = SlidingWindowCacheBuilder.For(myDataSource, new IntegerFixedStepDomain())
    .WithOptions(o => o
        .WithCacheSize(left: 1.0, right: 2.0)   // 100% left / 200% right of requested range
        .WithReadMode(UserCacheReadMode.Snapshot)
        .WithThresholds(0.2))                    // rebalance if <20% buffer remains
    .Build();

var result = await cache.GetDataAsync(Range.Closed(100, 200), cancellationToken);

foreach (var item in result.Data.Span)
    Console.WriteLine(item);
```

## Implementing a Data Source

Implement `IDataSource<TRange, TData>` to connect the cache to your backing store. The `FetchAsync` single-range overload is the only method you must provide; the batch overload has a default implementation that parallelizes single-range calls.

### FuncDataSource — inline without a class

`FuncDataSource<TRange, TData>` wraps an async delegate so you can create a data source in one expression:

```csharp
using Intervals.NET.Caching;
using Intervals.NET.Caching.Dto;

// Unbounded source — always returns data for any range
IDataSource<int, string> source = new FuncDataSource<int, string>(
    async (range, ct) =>
    {
        var data = await myService.QueryAsync(range, ct);
        return new RangeChunk<int, string>(range, data);
    });
```

For **bounded** sources (database with min/max IDs, time-series with temporal limits), return a `RangeChunk` with `Range = null` when no data is available — never throw:

```csharp
IDataSource<int, Record> bounded = new FuncDataSource<int, Record>(
    async (range, ct) =>
    {
        var available = range.Intersect(Range.Closed(minId, maxId));
        if (available is null)
            return new RangeChunk<int, Record>(null, []);

        var records = await db.FetchAsync(available, ct);
        return new RangeChunk<int, Record>(available, records);
    });
```

For sources where a dedicated class is warranted (custom batch optimization, retry logic, dependency injection), implement `IDataSource<TRange, TData>` directly. See `docs/shared/boundary-handling.md` for the full boundary contract.

## Boundary Handling

`GetDataAsync` returns `RangeResult<TRange, TData>` where `Range` may be `null` when the data source has no data for the requested range, and `CacheInteraction` indicates whether the request was a `FullHit`, `PartialHit`, or `FullMiss`. Always check `Range` before accessing data:

```csharp
var result = await cache.GetDataAsync(Range.Closed(100, 200), ct);

if (result.Range != null)
{
    // Data available
    foreach (var item in result.Data.Span)
        ProcessItem(item);
}
else
{
    // No data available for this range
}
```

Canonical guide: `docs/shared/boundary-handling.md`.

## Resource Management

`SlidingWindowCache` implements `IAsyncDisposable`. Always dispose when done:

```csharp
// Recommended: await using
await using var cache = new SlidingWindowCache<int, string, IntegerFixedStepDomain>(
    dataSource, domain, options, cacheDiagnostics
);

var data = await cache.GetDataAsync(Range.Closed(0, 100), ct);
// DisposeAsync called automatically at end of scope
```

After disposal, all operations throw `ObjectDisposedException`. Disposal is idempotent and concurrent-safe. Background operations are cancelled gracefully, not forcibly terminated.

## Configuration

### Cache Size Coefficients

**`leftCacheSize`** — multiplier of requested range size for left buffer. `1.0` = cache as much to the left as the user requested.

**`rightCacheSize`** — multiplier of requested range size for right buffer. `2.0` = cache twice as much to the right.

### Threshold Policies

**`leftThreshold`** / **`rightThreshold`** — percentage of the **total cache window size** that triggers rebalancing when crossed. E.g., with a total window of 400 items and `rightThreshold: 0.2`, rebalance triggers when the request moves within 80 items of the right edge.

**⚠️ Threshold Sum Constraint:** `leftThreshold + rightThreshold` must not exceed `1.0` when both are specified. Exceeding this creates overlapping stability zones (impossible geometry). Examples:
- ✅ `leftThreshold: 0.3, rightThreshold: 0.3` (sum = 0.6)
- ✅ `leftThreshold: 0.5, rightThreshold: 0.5` (sum = 1.0)
- ❌ `leftThreshold: 0.6, rightThreshold: 0.6` (sum = 1.2 — throws `ArgumentException`)

### Debouncing

**`debounceDelay`** (default: 100ms) — delay before background rebalance executes. Prevents thrashing when the user rapidly changes access patterns.

### Execution Strategy

**`rebalanceQueueCapacity`** (default: `null`) — controls rebalance serialization strategy:

| Value            | Strategy                             | Backpressure     | Use Case                                |
|------------------|--------------------------------------|------------------|-----------------------------------------|
| `null` (default) | Task-based (lock-free task chaining) | None             | Recommended for 99% of scenarios        |
| `>= 1`           | Channel-based (bounded queue)        | Blocks when full | Extreme high-frequency with I/O latency |

### Configuration Examples

**Forward-heavy scrolling:**
```csharp
var options = new SlidingWindowCacheOptions(
    leftCacheSize: 0.5,
    rightCacheSize: 3.0,
    leftThreshold: 0.25,
    rightThreshold: 0.15
);
```

**Bidirectional navigation:**
```csharp
var options = new SlidingWindowCacheOptions(
    leftCacheSize: 1.5,
    rightCacheSize: 1.5,
    leftThreshold: 0.2,
    rightThreshold: 0.2
);
```

**High-latency data source with stability:**
```csharp
var options = new SlidingWindowCacheOptions(
    leftCacheSize: 2.0,
    rightCacheSize: 3.0,
    leftThreshold: 0.1,
    rightThreshold: 0.1,
    debounceDelay: TimeSpan.FromMilliseconds(150)
);
```

## Runtime Options Update

Cache sizing, threshold, and debounce options can be changed on a live cache instance without recreation. Updates take effect on the **next rebalance decision/execution cycle**.

```csharp
// Change left and right cache sizes at runtime
cache.UpdateRuntimeOptions(update =>
    update.WithLeftCacheSize(3.0)
          .WithRightCacheSize(3.0));

// Change debounce delay
cache.UpdateRuntimeOptions(update =>
    update.WithDebounceDelay(TimeSpan.Zero));

// Change thresholds — or clear a threshold to null
cache.UpdateRuntimeOptions(update =>
    update.WithLeftThreshold(0.15)
          .ClearRightThreshold());
```

`UpdateRuntimeOptions` uses a **fluent builder** (`RuntimeOptionsUpdateBuilder`). Only fields explicitly set via builder calls are changed — all other options remain at their current values.

**Constraints:**
- `ReadMode` and `RebalanceQueueCapacity` are creation-time only and cannot be changed at runtime.
- All validation rules from construction still apply (`ArgumentOutOfRangeException` for negative sizes, `ArgumentException` for threshold sum > 1.0, etc.). A failed update leaves the current options unchanged — no partial application.
- Calling `UpdateRuntimeOptions` on a disposed cache throws `ObjectDisposedException`.

**Note:** `UpdateRuntimeOptions` and `CurrentRuntimeOptions` are `ISlidingWindowCache`-specific — they exist only on individual `SlidingWindowCache` instances. `LayeredRangeCache` implements `IRangeCache` only and does not expose these methods. To update runtime options on a layer, access it via the `Layers` property and cast to `ISlidingWindowCache` (see Multi-Layer Cache section for details).

## Reading Current Runtime Options

Use `CurrentRuntimeOptions` on a `SlidingWindowCache` instance to inspect the live option values. It returns a `RuntimeOptionsSnapshot` — a read-only point-in-time copy of the five runtime-updatable values.

```csharp
var snapshot = cache.CurrentRuntimeOptions;
Console.WriteLine($"Left: {snapshot.LeftCacheSize}, Right: {snapshot.RightCacheSize}");

// Useful for relative updates — double the current left size:
var current = cache.CurrentRuntimeOptions;
cache.UpdateRuntimeOptions(u => u.WithLeftCacheSize(current.LeftCacheSize * 2));
```

The snapshot is immutable. Subsequent calls to `UpdateRuntimeOptions` do not affect previously obtained snapshots — obtain a new snapshot to see updated values.

- Calling `CurrentRuntimeOptions` on a disposed cache throws `ObjectDisposedException`.
## Diagnostics

⚠️ **CRITICAL: You MUST handle `BackgroundOperationFailed` in production.** Rebalance operations run in background tasks. Without handling this event, failures are silently swallowed and the cache stops rebalancing with no indication.

```csharp
public class LoggingCacheDiagnostics : ISlidingWindowCacheDiagnostics
{
    private readonly ILogger _logger;

    public LoggingCacheDiagnostics(ILogger logger) => _logger = logger;

    public void BackgroundOperationFailed(Exception ex)
    {
        // CRITICAL: always log background failures
        _logger.LogError(ex, "Cache background operation failed. Cache may not be optimally sized.");
    }

    // Other methods can be no-op if you only care about failures
}
```

**Threading:** All diagnostic hooks are called **synchronously** on the thread that triggers the event (User Thread or a Background Thread — see `docs/shared/diagnostics.md` for the full thread-context table).

`ExecutionContext` (including `AsyncLocal<T>` values, `Activity`, and ambient culture) flows from the publishing thread into each hook. You can safely read ambient context in hooks.

If no diagnostics instance is provided, the cache uses `NoOpDiagnostics` — zero overhead, JIT-optimized away completely.

Canonical guide: `docs/shared/diagnostics.md`.

## Performance Considerations

- Snapshot mode: O(1) reads, O(n) rebalance (array allocation)
- CopyOnRead mode: O(n) reads (copy cost), cheaper rebalance operations
- Rebalancing is asynchronous — does not block user reads
- Debouncing: multiple rapid requests trigger only one rebalance operation
- Diagnostics overhead: zero when not used (NoOpDiagnostics); ~1–5 ns per event when enabled

## Documentation

### Path 1: Quick Start

1. `README.md` — you are here
2. `docs/shared/boundary-handling.md` — RangeResult usage, bounded data sources
3. `docs/sliding-window/storage-strategies.md` — choose Snapshot vs CopyOnRead for your use case
4. `docs/shared/glossary.md` — canonical term definitions and common misconceptions
5. `docs/shared/diagnostics.md` — optional instrumentation

### Path 2: Architecture Deep Dive

1. `docs/shared/glossary.md` — start here for canonical terminology
2. `docs/sliding-window/architecture.md` — single-writer, decision-driven execution, disposal
3. `docs/sliding-window/invariants.md` — formal system invariants
4. `docs/sliding-window/components/overview.md` — component catalog with invariant implementation mapping
5. `docs/sliding-window/scenarios.md` — temporal behavior walkthroughs
6. `docs/sliding-window/state-machine.md` — formal state transitions and mutation ownership
7. `docs/sliding-window/actors.md` — actor responsibilities and execution contexts

## Consistency Modes

By default, `GetDataAsync` is **eventually consistent**: data is returned immediately while the cache window converges asynchronously in the background. Two opt-in extension methods provide stronger consistency guarantees. Both require a `using Intervals.NET.Caching;` import.

> **Serialized access requirement:** The hybrid and strong consistency modes provide their warm-cache guarantee only when requests are made one at a time (serialized). Under concurrent/parallel callers they remain safe (no crashes or hangs) but the guarantee degrades — due to `AsyncActivityCounter`'s "was idle at some point" semantics (Invariant S.H.3) and a brief gap between the counter increment and TCS publication in `IncrementActivity`, a concurrent waiter may observe a previously completed idle TCS and return without waiting for the new rebalance.

### Eventual Consistency (Default)

```csharp
// Returns immediately; rebalance converges asynchronously in background
var result = await cache.GetDataAsync(Range.Closed(100, 200), cancellationToken);
```

Use for all hot paths and rapid sequential access. No latency beyond data assembly.

### Hybrid Consistency — `GetDataAndWaitOnMissAsync`

```csharp
using Intervals.NET.Caching;

// Waits for idle only if the request was a PartialHit or FullMiss; returns immediately on FullHit
var result = await cache.GetDataAndWaitOnMissAsync(
    Range.Closed(100, 200),
    cancellationToken);

// result.CacheInteraction tells you which path was taken:
// CacheInteraction.FullHit     → returned immediately (no wait)
// CacheInteraction.PartialHit  → waited for cache to converge
// CacheInteraction.FullMiss    → waited for cache to converge
if (result.Range.HasValue)
    ProcessData(result.Data);
```

**When to use:**
- Warm-cache fast path: pays no penalty on cache hits, still waits on misses
- Access patterns where most requests are hits but you want convergence on misses

**When NOT to use:**
- First request (always a miss — pays full debounce + I/O wait)
- Hot paths with many misses

> **Cancellation:** If the cancellation token fires during the idle wait (after `GetDataAsync` has already returned data), the method catches `OperationCanceledException` and returns the already-obtained result gracefully — degrading to eventual consistency for that call. The background rebalance is not affected.

### Strong Consistency — `GetDataAndWaitForIdleAsync`

```csharp
using Intervals.NET.Caching;

// Returns only after cache has converged to its desired window geometry
var result = await cache.GetDataAndWaitForIdleAsync(
    Range.Closed(100, 200),
    cancellationToken);

// Cache geometry is now stable — safe to inspect, assert, or rely on
if (result.Range.HasValue)
    ProcessData(result.Data);
```

This is a thin composition of `GetDataAsync` followed by `WaitForIdleAsync`. The returned `RangeResult` is identical to what `GetDataAsync` would return.

**When to use:**
- Cold start synchronization: waiting for the initial cache window to be built before proceeding
- Integration testing: asserting on cache geometry after a request
- Any scenario where you want to know the cache has finished rebalancing before moving on

**When NOT to use:**
- Hot paths or rapid sequential requests — each call waits for full rebalance, which includes the debounce delay plus data fetching. For normal usage, the default eventual consistency model is faster.

> **Cancellation:** If the cancellation token fires during the idle wait (after `GetDataAsync` has already returned data), the method catches `OperationCanceledException` and returns the already-obtained result gracefully — degrading to eventual consistency for that call. The background rebalance is not affected.

### Deterministic Testing

`WaitForIdleAsync()` provides race-free synchronization with background operations for tests. Uses "was idle at some point" semantics — does not guarantee still idle after completion. See `docs/sliding-window/invariants.md` (Activity tracking invariants).

### CacheInteraction on RangeResult

Every `RangeResult` carries a `CacheInteraction` property classifying the request:

| Value        | Meaning                                                                         |
|--------------|---------------------------------------------------------------------------------|
| `FullHit`    | Entire requested range was served from cache                                    |
| `PartialHit` | Request partially overlapped the cache; missing part fetched from `IDataSource` |
| `FullMiss`   | No overlap (cold start or jump); full range fetched from `IDataSource`          |

This is the per-request programmatic alternative to the `UserRequestFullCacheHit` / `UserRequestPartialCacheHit` / `UserRequestFullCacheMiss` diagnostics callbacks.

---

# Visited Places Cache

A read-only, range-based, **random-access-optimized** cache with capacity-based eviction, pluggable eviction policies and selectors, optional TTL expiration, and multi-layer composition support.

## Visited Places Cache Concept

Where the Sliding Window Cache is optimized for a single coherent viewport moving predictably through a domain, the Visited Places Cache is optimized for **random-access patterns** — users jumping to arbitrary locations with no predictable direction or stride.

Key design choices:

- Stores **non-contiguous, independent segments** (not a single contiguous window)
- Each segment is a fetched range; the collection grows as the user visits new areas
- **Eviction** enforces capacity limits, removing the least valuable segments when limits are exceeded
- **TTL expiration** optionally removes stale segments after a configurable duration
- No rebalancing, no threshold geometry — each segment lives independently until evicted or expired

### Visual: Segment Collection

```
Domain:   [0 ──────────────────────────────────────────────────────────── 1000]

Cached segments (visited areas, non-contiguous):
            [══100-150══]  [═220-280═]   [═══500-600═══]    [═850-900═]
                  ↑             ↑               ↑                ↑
              segment 1     segment 2        segment 3       segment 4

New request to [400, 450] → full miss   → fetch, store as new segment
New request to [120, 140] → full hit    → serve immediately from segment 1
New request to [500, 900] → partial hit → calculate gaps, fetch, serve assembled, store as new segment
```

## Install

```bash
dotnet add package Intervals.NET.Caching.VisitedPlaces
```

## Quick Start

```csharp
using Intervals.NET.Caching;
using Intervals.NET.Caching.VisitedPlaces.Public.Cache;
using Intervals.NET.Caching.VisitedPlaces.Public.Configuration;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Policies;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Selectors;
using Intervals.NET;
using Intervals.NET.Domain.Default.Numeric;

await using var cache = VisitedPlacesCacheBuilder.For(myDataSource, new IntegerFixedStepDomain())
    .WithOptions(o => o)   // use defaults; or .WithOptions(o => o.WithSegmentTtl(TimeSpan.FromMinutes(10)))
    .WithEviction(e => e
        .AddPolicy(MaxSegmentCountPolicy.Create<int, MyData>(maxCount: 50))
        .WithSelector(LruEvictionSelector.Create<int, MyData>()))
    .Build();

var result = await cache.GetDataAsync(Range.Closed(100, 200), cancellationToken);

foreach (var item in result.Data.Span)
    Console.WriteLine(item);
```

## Eviction Policies

Eviction is triggered when **any** configured policy produces a violated constraint (OR semantics). Multiple policies may be active simultaneously; all violated pressures are satisfied in a single eviction pass.

### MaxSegmentCountPolicy

Fires when the total number of cached segments exceeds a limit.

```csharp
MaxSegmentCountPolicy.Create<int, MyData>(maxCount: 50)
```

Best for: workloads where all segments are approximately the same size, or where total segment count is the primary memory concern.

### MaxTotalSpanPolicy

Fires when the sum of all segment spans (total domain discrete points) exceeds a limit.

```csharp
MaxTotalSpanPolicy.Create<int, MyData, IntegerFixedStepDomain>(
    maxTotalSpan: 5000,
    domain: new IntegerFixedStepDomain())
```

Best for: workloads where segments vary significantly in size and total coverage is more meaningful than segment count.

### Combining Policies

```csharp
.WithEviction(e => e
    .AddPolicy(MaxSegmentCountPolicy.Create<int, MyData>(maxCount: 50))
    .AddPolicy(MaxTotalSpanPolicy.Create<int, MyData, IntegerFixedStepDomain>(maxTotalSpan: 10_000, domain))
    .WithSelector(LruEvictionSelector.Create<int, MyData>()))
```

Eviction fires when either policy is violated. Both constraints are satisfied in a single pass.

## Eviction Selectors

The selector determines **which segment** to evict from a random sample. All built-in selectors use **random sampling** (O(SampleSize)) rather than sorting the full collection (O(N log N)), keeping eviction cost constant regardless of cache size.

### LruEvictionSelector — Least Recently Used

Evicts the segment from the sample that was **least recently accessed**. Retains recently-used segments.

```csharp
LruEvictionSelector.Create<int, MyData>()
```

Best for: workloads where re-access probability correlates with recency (most interactive workloads).

### FifoEvictionSelector — First In, First Out

Evicts the segment from the sample that was **stored earliest**. Ignores access patterns.

```csharp
FifoEvictionSelector.Create<int, MyData>()
```

Best for: workloads where all segments have similar re-access probability and simplicity is valued.

### SmallestFirstEvictionSelector — Smallest Span First

Evicts the segment from the sample with the **narrowest domain span**. Retains wide (high-coverage) segments.

```csharp
SmallestFirstEvictionSelector.Create<int, MyData, IntegerFixedStepDomain>(
    new IntegerFixedStepDomain())
```

Best for: workloads where wider segments are more valuable (e.g., broader time ranges, larger geographic areas).

## TTL Expiration

Enable automatic expiration of cached segments after a configurable duration:

```csharp
await using var cache = VisitedPlacesCacheBuilder.For(dataSource, domain)
    .WithOptions(o => o.WithSegmentTtl(TimeSpan.FromMinutes(10)))
    .WithEviction(e => e
        .AddPolicy(MaxSegmentCountPolicy.Create<int, MyData>(maxCount: 100))
        .WithSelector(LruEvictionSelector.Create<int, MyData>()))
    .Build();
```

When `SegmentTtl` is set, each segment is scheduled for automatic removal after the TTL elapses from the moment it was stored. TTL removal and eviction are independent — a segment may be removed by either mechanism, whichever fires first.

**Idempotent removal:** if a segment is evicted before its TTL fires, the scheduled TTL removal is a no-op.

## Storage Strategy

Two internal storage strategies are available. The default (`SnapshotAppendBufferStorage`) is appropriate for most use cases.

| Strategy                                | Best For                                   | LOH Risk              |
|-----------------------------------------|--------------------------------------------|-----------------------|
| `SnapshotAppendBufferStorage` (default) | < 85KB the main array size, < 50K segments | High for large caches |
| `LinkedListStrideIndexStorage`          | > 50K segments                             | Low (no large array)  |

```csharp
// Explicit LinkedList strategy for large caches
.WithOptions(o => o.WithStorageStrategy(LinkedListStrideIndexStorageOptions<int, MyData>.Default))
```

For detailed guidance, see `docs/visited-places/storage-strategies.md`.

## Diagnostics

⚠️ **CRITICAL: You MUST handle `BackgroundOperationFailed` in production.** Background normalization runs on the thread pool. Without handling this event, failures are silently swallowed.

```csharp
public class LoggingVpcDiagnostics : IVisitedPlacesCacheDiagnostics
{
    private readonly ILogger _logger;

    public LoggingVpcDiagnostics(ILogger logger) => _logger = logger;

    public void BackgroundOperationFailed(Exception ex)
    {
        // CRITICAL: always log background failures
        _logger.LogError(ex, "VPC background operation failed.");
    }

    // All other methods can be no-op if not needed
}
```

If no diagnostics instance is provided, `NoOpDiagnostics` is used — zero overhead, JIT-optimized away completely.

Canonical guide: `docs/shared/diagnostics.md`.

## VPC Documentation

- `docs/visited-places/eviction.md` — eviction architecture, policies, selectors, metadata lifecycle
- `docs/visited-places/storage-strategies.md` — storage strategy comparison, tuning guide
- `docs/visited-places/invariants.md` — formal system invariants
- `docs/visited-places/scenarios.md` — temporal behavior walkthroughs
- `docs/visited-places/actors.md` — actor responsibilities and execution contexts

---

# Multi-Layer Cache

For workloads with high-latency data sources, compose multiple cache instances into a layered stack. Each layer uses the layer below it as its data source. **Layers can be mixed** — a `VisitedPlacesCache` at the bottom provides random-access buffering while `SlidingWindowCache` layers above serve the sequential user path.

### Visual: Mixed Three-Layer Stack

```
User
 │
 ▼
┌──────────────────────────────────────────────────────────┐
│  L1: SlidingWindowCache — 0.5× Snapshot                  │
│  Small, zero-allocation reads, user-facing               │
└────────────────────────┬─────────────────────────────────┘
                         │ cache miss → fetches from L2
                         ▼
┌──────────────────────────────────────────────────────────┐
│  L2: SlidingWindowCache — 10× CopyOnRead                 │
│  Large prefetch buffer, absorbs L1 rebalance fetches     │
└────────────────────────┬─────────────────────────────────┘
                         │ cache miss → fetches from L3
                         ▼
┌──────────────────────────────────────────────────────────┐
│  L3: VisitedPlacesCache — random-access buffer           │
│  Absorbs random jumps; eviction-based capacity control   │
└────────────────────────┬─────────────────────────────────┘
                         │ cache miss → fetches from data source
                         ▼
                   Real Data Source
```

### Mixed-Type Three-Layer Example

```csharp
using Intervals.NET.Caching.SlidingWindow.Public.Cache;
using Intervals.NET.Caching.SlidingWindow.Public.Configuration;
using Intervals.NET.Caching.SlidingWindow.Public.Extensions;
using Intervals.NET.Caching.VisitedPlaces.Public.Cache;
using Intervals.NET.Caching.VisitedPlaces.Public.Configuration;
using Intervals.NET.Caching.VisitedPlaces.Public.Extensions;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Policies;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Selectors;

await using var cache = await VisitedPlacesCacheBuilder.Layered(realDataSource, domain)
    .AddVisitedPlacesLayer(e => e                                    // L3: random-access absorber
        .AddPolicy(MaxSegmentCountPolicy.Create<int, MyData>(200))
        .WithSelector(LruEvictionSelector.Create<int, MyData>()))
    .AddSlidingWindowLayer(o => o                                    // L2: large sequential buffer
        .WithCacheSize(left: 10.0, right: 10.0)
        .WithReadMode(UserCacheReadMode.CopyOnRead)
        .WithThresholds(0.3))
    .AddSlidingWindowLayer(o => o                                    // L1: user-facing
        .WithCacheSize(left: 0.5, right: 0.5)
        .WithReadMode(UserCacheReadMode.Snapshot))
    .BuildAsync();

var result = await cache.GetDataAsync(range, ct);
```

`LayeredRangeCache` implements `IRangeCache` and is `IAsyncDisposable` — it owns and disposes all layers when you dispose it.

**Accessing and updating individual layers:**

Use the `Layers` property to access any layer by index (0 = innermost, last = outermost). `Layers[i]` is typed as `IRangeCache` — cast to `ISlidingWindowCache` to access `UpdateRuntimeOptions` or `CurrentRuntimeOptions` on a SlidingWindow layer:

```csharp
// Update options on L2 (index 1 — second innermost)
((ISlidingWindowCache<int, string, IntegerFixedStepDomain>)layeredCache.Layers[1])
    .UpdateRuntimeOptions(u => u.WithLeftCacheSize(8.0));

// Inspect L1 (outermost) current options
var outerOptions = ((ISlidingWindowCache<int, string, IntegerFixedStepDomain>)layeredCache.Layers[^1])
    .CurrentRuntimeOptions;
```

**Recommended layer configuration pattern:**
- **Innermost layer** (closest to data source): random-access `VisitedPlacesCache` for arbitrary-jump workloads, or large `CopyOnRead` SlidingWindowCache for pure sequential workloads
- **Middle layers**: `CopyOnRead`, large buffer sizes (5–10×), absorb the layer above's rebalance fetches
- **Outer (user-facing) layer**: `Snapshot`, small buffer sizes (0.3–1.0×), zero-allocation reads

> **Important — buffer ratio requirement for SlidingWindow layers:** Inner SlidingWindow layer
> buffers must be **substantially** larger than outer layer buffers. When the outer layer
> rebalances, it fetches missing ranges from the inner layer — if the inner layer's
> `NoRebalanceRange` is not wide enough to contain the outer layer's full `DesiredCacheRange`,
> the inner layer also rebalances, potentially in the wrong direction. Use a 5–10× ratio and
> `leftThreshold`/`rightThreshold` of 0.2–0.3 on inner SlidingWindow layers.
> See `docs/sliding-window/architecture.md` (Cascading Rebalance Behavior) and
> `docs/sliding-window/scenarios.md` (Scenarios L6 and L7) for the full explanation.

## Key Differences: SlidingWindow vs. VisitedPlaces

| Aspect                | SlidingWindowCache               | VisitedPlacesCache            |
|-----------------------|----------------------------------|-------------------------------|
| **Access pattern**    | Sequential, coherent viewport    | Random, non-sequential jumps  |
| **Cache structure**   | Single contiguous window         | Multiple independent segments |
| **Cache growth**      | Rebalances window position       | Adds new segments per visit   |
| **Memory control**    | Window size (coefficients)       | Eviction policies             |
| **Stale data**        | Rebalance replaces window        | TTL expiration per segment    |
| **Runtime updates**   | `UpdateRuntimeOptions` available | Construction-time only        |
| **Consistency modes** | Eventual / hybrid / strong       | Eventual only                 |
| **Best for**          | Time-series, scrollable grids    | Maps, jump navigation, lookup |

When the user has a **single coherent viewport** moving through data, use `SlidingWindowCache`. When the user **jumps freely** to arbitrary locations with no predictable pattern, use `VisitedPlacesCache`.

---

## License

MIT
