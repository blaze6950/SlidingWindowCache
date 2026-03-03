# Sliding Window Cache

A read-only, range-based, sequential-optimized cache with decision-driven background rebalancing, three consistency modes (eventual/hybrid/strong), and intelligent work avoidance.

[![CI/CD](https://github.com/blaze6950/SlidingWindowCache/actions/workflows/slidingwindowcache.yml/badge.svg)](https://github.com/blaze6950/SlidingWindowCache/actions/workflows/slidingwindowcache.yml)
[![NuGet](https://img.shields.io/nuget/v/SlidingWindowCache.svg)](https://www.nuget.org/packages/SlidingWindowCache/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/SlidingWindowCache.svg)](https://www.nuget.org/packages/SlidingWindowCache/)
[![codecov](https://codecov.io/gh/blaze6950/SlidingWindowCache/graph/badge.svg?token=RFQBNX7MMD)](https://codecov.io/gh/blaze6950/SlidingWindowCache)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![.NET 8.0](https://img.shields.io/badge/.NET-8.0-blue.svg)](https://dotnet.microsoft.com/download/dotnet/8.0)

## What It Is

Optimized for access patterns that move predictably across a domain (scrolling, playback, time-series inspection):

- Serves `GetDataAsync` immediately; background work converges the cache window asynchronously
- Single-writer architecture: only rebalance execution mutates shared cache state
- Decision-driven execution: multi-stage analytical validation prevents thrashing and unnecessary I/O
- Smart eventual consistency: cache converges to optimal configuration while avoiding unnecessary work
- Opt-in hybrid or strong consistency via extension methods (`GetDataAndWaitOnMissAsync`, `GetDataAndWaitForIdleAsync`)

For the canonical architecture docs, see `docs/architecture.md`.

## Install

```bash
dotnet add package SlidingWindowCache
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

For detailed comparison and guidance, see `docs/storage-strategies.md`.

## Quick Start

```csharp
using SlidingWindowCache;
using SlidingWindowCache.Configuration;
using Intervals.NET;
using Intervals.NET.Domain.Default.Numeric;

var options = new WindowCacheOptions(
    leftCacheSize: 1.0,    // Cache 100% of requested range size to the left
    rightCacheSize: 2.0,   // Cache 200% of requested range size to the right
    leftThreshold: 0.2,    // Rebalance if <20% left buffer remains
    rightThreshold: 0.2    // Rebalance if <20% right buffer remains
);

var cache = WindowCache<int, string, IntegerFixedStepDomain>.Create(
    dataSource: myDataSource,
    domain: new IntegerFixedStepDomain(),
    options: options,
    readMode: UserCacheReadMode.Snapshot
);

var result = await cache.GetDataAsync(Range.Closed(100, 200), cancellationToken);

foreach (var item in result.Data.Span)
    Console.WriteLine(item);
```

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

Canonical guide: `docs/boundary-handling.md`.

## Resource Management

`WindowCache` implements `IAsyncDisposable`. Always dispose when done:

```csharp
// Recommended: await using
await using var cache = new WindowCache<int, string, IntegerFixedStepDomain>(
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
var options = new WindowCacheOptions(
    leftCacheSize: 0.5,
    rightCacheSize: 3.0,
    leftThreshold: 0.25,
    rightThreshold: 0.15
);
```

**Bidirectional navigation:**
```csharp
var options = new WindowCacheOptions(
    leftCacheSize: 1.5,
    rightCacheSize: 1.5,
    leftThreshold: 0.2,
    rightThreshold: 0.2
);
```

**High-latency data source with stability:**
```csharp
var options = new WindowCacheOptions(
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

**`LayeredWindowCache`** delegates `UpdateRuntimeOptions` to the outermost (user-facing) layer. To update a specific inner layer, use the `Layers` property (see Multi-Layer Cache below).

## Reading Current Runtime Options

Use `CurrentRuntimeOptions` to inspect the live option values on any cache instance. It returns a `RuntimeOptionsSnapshot` — a read-only point-in-time copy of the five runtime-updatable values.

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

⚠️ **CRITICAL: You MUST handle `RebalanceExecutionFailed` in production.** Rebalance operations run in background tasks. Without handling this event, failures are silently swallowed and the cache stops rebalancing with no indication.

```csharp
public class LoggingCacheDiagnostics : ICacheDiagnostics
{
    private readonly ILogger _logger;

    public LoggingCacheDiagnostics(ILogger logger) => _logger = logger;

    public void RebalanceExecutionFailed(Exception ex)
    {
        // CRITICAL: always log rebalance failures
        _logger.LogError(ex, "Cache rebalance failed. Cache may not be optimally sized.");
    }

    // Other methods can be no-op if you only care about failures
}
```

If no diagnostics instance is provided, the cache uses `NoOpDiagnostics` — zero overhead, JIT-optimized away completely.

Canonical guide: `docs/diagnostics.md`.

## Performance Considerations

- Snapshot mode: O(1) reads, O(n) rebalance (array allocation)
- CopyOnRead mode: O(n) reads (copy cost), cheaper rebalance operations
- Rebalancing is asynchronous — does not block user reads
- Debouncing: multiple rapid requests trigger only one rebalance operation
- Diagnostics overhead: zero when not used (NoOpDiagnostics); ~1–5 ns per event when enabled

## Documentation

### Path 1: Quick Start

1. `README.md` — you are here
2. `docs/boundary-handling.md` — RangeResult usage, bounded data sources
3. `docs/storage-strategies.md` — choose Snapshot vs CopyOnRead for your use case
4. `docs/glossary.md` — canonical term definitions and common misconceptions
5. `docs/diagnostics.md` — optional instrumentation

### Path 2: Architecture Deep Dive

1. `docs/glossary.md` — start here for canonical terminology
2. `docs/architecture.md` — single-writer, decision-driven execution, disposal
3. `docs/invariants.md` — formal system invariants
4. `docs/components/overview.md` — component catalog with invariant implementation mapping
5. `docs/scenarios.md` — temporal behavior walkthroughs
6. `docs/state-machine.md` — formal state transitions and mutation ownership
7. `docs/actors.md` — actor responsibilities and execution contexts

## Consistency Modes

By default, `GetDataAsync` is **eventually consistent**: data is returned immediately while the cache window converges asynchronously in the background. Two opt-in extension methods provide stronger consistency guarantees. Both require a `using SlidingWindowCache.Public;` import.

> **Serialized access requirement:** The hybrid and strong consistency modes provide their warm-cache guarantee only when requests are made one at a time (serialized). Under concurrent/parallel callers they remain safe (no crashes or hangs) but the guarantee degrades — due to `AsyncActivityCounter`'s "was idle at some point" semantics (Invariant H.49) and a brief gap between the counter increment and TCS publication in `IncrementActivity`, a concurrent waiter may observe a previously completed idle TCS and return without waiting for the new rebalance.

### Eventual Consistency (Default)

```csharp
// Returns immediately; rebalance converges asynchronously in background
var result = await cache.GetDataAsync(Range.Closed(100, 200), cancellationToken);
```

Use for all hot paths and rapid sequential access. No latency beyond data assembly.

### Hybrid Consistency — `GetDataAndWaitOnMissAsync`

```csharp
using SlidingWindowCache.Public;

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
using SlidingWindowCache.Public;

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

`WaitForIdleAsync()` provides race-free synchronization with background operations for tests. Uses "was idle at some point" semantics — does not guarantee still idle after completion. See `docs/invariants.md` (Activity tracking invariants).

### CacheInteraction on RangeResult

Every `RangeResult` carries a `CacheInteraction` property classifying the request:

| Value        | Meaning                                                                         |
|--------------|---------------------------------------------------------------------------------|
| `FullHit`    | Entire requested range was served from cache                                    |
| `PartialHit` | Request partially overlapped the cache; missing part fetched from `IDataSource` |
| `FullMiss`   | No overlap (cold start or jump); full range fetched from `IDataSource`          |

This is the per-request programmatic alternative to the `UserRequestFullCacheHit` / `UserRequestPartialCacheHit` / `UserRequestFullCacheMiss` diagnostics callbacks.

## Multi-Layer Cache

For workloads with high-latency data sources, you can compose multiple `WindowCache` instances into a layered stack. Each layer uses the layer below it as its data source, allowing you to trade memory for reduced data-source I/O.

```csharp
await using var cache = LayeredWindowCacheBuilder<int, byte[], IntegerFixedStepDomain>
    .Create(realDataSource, domain)
    .AddLayer(new WindowCacheOptions(        // L2: deep background cache
        leftCacheSize: 10.0,
        rightCacheSize: 10.0,
        readMode: UserCacheReadMode.CopyOnRead,
        leftThreshold: 0.3,
        rightThreshold: 0.3))
    .AddLayer(new WindowCacheOptions(        // L1: user-facing cache
        leftCacheSize: 0.5,
        rightCacheSize: 0.5,
        readMode: UserCacheReadMode.Snapshot))
    .Build();

var result = await cache.GetDataAsync(range, ct);
```

`LayeredWindowCache` implements `IWindowCache` and is `IAsyncDisposable` — it owns and disposes all layers when you dispose it.

**Accessing and updating individual layers:**

Use the `Layers` property to access any specific layer by index (0 = innermost, last = outermost). Each layer exposes the full `IWindowCache` interface:

```csharp
// Update options on the innermost (deep background) layer
layeredCache.Layers[0].UpdateRuntimeOptions(u => u.WithLeftCacheSize(8.0));

// Inspect the outermost (user-facing) layer's current options
var outerOptions = layeredCache.Layers[^1].CurrentRuntimeOptions;

// cache.UpdateRuntimeOptions() is shorthand for Layers[^1].UpdateRuntimeOptions()
layeredCache.UpdateRuntimeOptions(u => u.WithRightCacheSize(1.0));
```

**Recommended layer configuration pattern:**
- **Inner layers** (closest to the data source): `CopyOnRead`, large buffer sizes (5–10×), handles the heavy prefetching
- **Outer (user-facing) layer**: `Snapshot`, small buffer sizes (0.3–1.0×), zero-allocation reads

> **Important — buffer ratio requirement:** Inner layer buffers must be **substantially** larger
> than outer layer buffers, not merely slightly larger. When the outer layer rebalances, it
> fetches missing ranges from the inner layer via `GetDataAsync`. Each fetch publishes a
> rebalance intent on the inner layer. If the inner layer's `NoRebalanceRange` is not wide
> enough to contain the outer layer's full `DesiredCacheRange`, the inner layer will also
> rebalance — and re-center toward only one side of the outer layer's gap, leaving it poorly
> positioned for the next rebalance. With undersized inner buffers this becomes a continuous
> cycle (cascading rebalance thrashing). Use a 5–10× ratio and `leftThreshold`/`rightThreshold`
> of 0.2–0.3 on inner layers to ensure the inner layer's stability zone absorbs the outer
> layer's rebalance fetches. See `docs/architecture.md` (Cascading Rebalance Behavior) and
> `docs/scenarios.md` (Scenarios L6 and L7) for the full explanation.

**Three-layer example:**
```csharp
await using var cache = LayeredWindowCacheBuilder<int, byte[], IntegerFixedStepDomain>
    .Create(realDataSource, domain)
    .AddLayer(l3Options)   // L3: 10× CopyOnRead — network/disk absorber
    .AddLayer(l2Options)   // L2: 2× CopyOnRead  — mid-level buffer
    .AddLayer(l1Options)   // L1: 0.5× Snapshot  — user-facing
    .Build();
```

For detailed guidance see `docs/storage-strategies.md`.

## License

MIT
