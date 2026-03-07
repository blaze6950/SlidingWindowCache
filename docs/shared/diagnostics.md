# Diagnostics — Shared Pattern

This document covers the diagnostics pattern that applies across all cache implementations. Implementation-specific diagnostics (specific callbacks, event meanings) are documented in each implementation's docs.

---

## Design Philosophy

Diagnostics are an optional observability layer with **zero cost when not used**. The default implementation (`NoOpDiagnostics`) has no-op methods that the JIT eliminates entirely — no branching, no allocation, no overhead.

When diagnostics are wired, each event is a simple method call. Implementations are user-provided and may fan out to counters, metrics systems, loggers, or test assertions.

---

## Two-Tier Pattern

Every cache implementation exposes a diagnostics interface with two default implementations:

### NoOpDiagnostics (default)

Empty implementation. Methods are empty and get inlined/eliminated by the JIT.

- **Zero overhead** — no performance impact whatsoever
- **No memory allocations**
- Used automatically when no diagnostics instance is provided

### EventCounterCacheDiagnostics (built-in counter)

Thread-safe atomic counter implementation using `Interlocked.Increment`.

- ~1–5 nanoseconds per event
- No locks, no allocations
- `Reset()` method for test isolation
- Use for testing, development, and production monitoring

---

## Critical: RebalanceExecutionFailed

Every cache implementation has a `RebalanceExecutionFailed(Exception ex)` callback. This is the **only signal** for silent background failures.

Background rebalance operations run fire-and-forget. When they fail:
1. The exception is caught
2. `RebalanceExecutionFailed(ex)` is called
3. The exception is **swallowed** to prevent application crashes
4. The cache continues serving user requests (but rebalancing stops)

**Without handling this event, failures are completely silent.**

Minimum production implementation:

```csharp
public void RebalanceExecutionFailed(Exception ex)
{
    _logger.LogError(ex,
        "Cache rebalance execution failed. Cache will continue serving user requests " +
        "but rebalancing has stopped. Investigate data source health and cache configuration.");
}
```

---

## Custom Implementations

Implement the diagnostics interface for custom observability:

```csharp
public class PrometheusMetricsDiagnostics : ICacheDiagnostics   // SWC example
{
    private readonly Counter _requestsServed;
    private readonly Counter _cacheHits;

    public void UserRequestServed() => _requestsServed.Inc();
    public void UserRequestFullCacheHit() => _cacheHits.Inc();
    // ...
}
```

---

## See Also

- `docs/sliding-window/diagnostics.md` — full `ICacheDiagnostics` event reference (18 events, test patterns, layered cache diagnostics)
