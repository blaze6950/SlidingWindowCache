# Boundary Handling — Sliding Window Cache

This document covers `RangeResult` structure and invariants, SlidingWindow-specific usage patterns, bounded data source implementations, test coverage, and architectural considerations specific to the Sliding Window Cache.

For the shared `IDataSource` boundary contract and nullable `Range` semantics that apply to all cache implementations, see [`docs/shared/boundary-handling.md`](../shared/boundary-handling.md).

---

## Table of Contents

- [RangeResult Structure](#rangeresult-structure)
- [Usage Patterns](#usage-patterns)
- [Bounded Data Sources](#bounded-data-sources)
- [Testing](#testing)
- [Architectural Considerations](#architectural-considerations)

---

## RangeResult Structure

`GetDataAsync` returns `RangeResult<TRange, TData>`, which carries the actual range fulfilled, the materialized data, and the cache interaction classification.

```csharp
// RangeResult is a sealed record (reference type) with an internal constructor.
// Instances are created exclusively by UserRequestHandler and RangeCacheDataSourceAdapter.
public sealed record RangeResult<TRange, TData>
    where TRange : IComparable<TRange>
{
    public Range<TRange>?        Range              { get; }
    public ReadOnlyMemory<TData> Data               { get; }
    public CacheInteraction      CacheInteraction   { get; }
}
```

### Properties

| Property           | Type                    | Description                                                                                                                 |
|--------------------|-------------------------|-----------------------------------------------------------------------------------------------------------------------------|
| `Range`            | `Range<TRange>?`        | **Nullable**. The actual range covered by the returned data. `null` indicates no data available.                            |
| `Data`             | `ReadOnlyMemory<TData>` | The materialized data elements. Empty when `Range` is `null`.                                                               |
| `CacheInteraction` | `CacheInteraction`      | How the request was served: `FullHit` (from cache), `PartialHit` (cache + fetch), or `FullMiss` (cold start or jump fetch). |

### Invariants

1. **Range-Data Consistency**: When `Range` is non-null, `Data.Length` MUST equal `Range.Span(domain)`
2. **Empty Data Semantics**: `Data.IsEmpty` when `Range` is `null` (no data available)
3. **Contiguity**: `Data` contains sequential elements matching the boundaries of `Range`
4. **CacheInteraction Accuracy**: `CacheInteraction` accurately reflects the cache scenario — `FullMiss` on cold start or jump, `FullHit` when fully cached, `PartialHit` on partial overlap (Invariant SWC.A.10b)

---

## Usage Patterns

### Pattern 1: Basic Access

```csharp
var result = await cache.GetDataAsync(
    Intervals.NET.Factories.Range.Closed(100, 200),
    ct
);

// Always check Range before using Data
if (result.Range != null)
{
    Console.WriteLine($"Received {result.Data.Length} elements");
    Console.WriteLine($"Range: {result.Range}");

    foreach (var item in result.Data.Span)
    {
        ProcessItem(item);
    }
}
else
{
    Console.WriteLine("No data available for requested range");
}
```

### Pattern 2: Accessing Data Directly

```csharp
// When you know data is available (e.g., infinite data source)
var result = await cache.GetDataAsync(range, ct);
var data = result.Data;  // Access data directly

foreach (var item in data.Span)
{
    ProcessItem(item);
}
```

### Pattern 3: Handling Partial Fulfillment

```csharp
var requestedRange = Intervals.NET.Factories.Range.Closed(50, 150);
var result = await cache.GetDataAsync(requestedRange, ct);

if (result.Range != null)
{
    // Check if we got the full requested range
    if (result.Range.Equals(requestedRange))
    {
        Console.WriteLine("Full range fulfilled");
    }
    else
    {
        Console.WriteLine($"Requested: {requestedRange}");
        Console.WriteLine($"Received: {result.Range} (truncated)");

        // Handle truncation
        if (result.Range.Start > requestedRange.Start)
            Console.WriteLine("Data truncated at start");
        if (result.Range.End < requestedRange.End)
            Console.WriteLine("Data truncated at end");
    }
}
```

### Pattern 4: Subset Requests from Cache

```csharp
// Prime cache with large range
await cache.GetDataAsync(Intervals.NET.Factories.Range.Closed(0, 1000), ct);

// Request subset (served from cache)
var subsetResult = await cache.GetDataAsync(
    Intervals.NET.Factories.Range.Closed(100, 200),
    ct
);

// Result contains ONLY the requested subset, not full cache
Assert.Equal(101, subsetResult.Data.Length); // [100, 200] = 101 elements
Assert.Equal(100, subsetResult.Data.Span[0]);
Assert.Equal(200, subsetResult.Data.Span[100]);
```

---

## Bounded Data Sources

For data sources with physical boundaries (databases with min/max IDs, time-series with temporal limits, paginated APIs).

### Implementation Guidelines

1. **No Exceptions**: Never throw for out-of-bounds requests
2. **Truncate Gracefully**: Return intersection of requested and available
3. **Consistent Span**: Ensure `Data.Count()` matches `Range.Span(domain)`
4. **Empty Result**: Return `RangeChunk(null, [])` when no data is available

### Example: Database with Bounded Records

```csharp
public class BoundedDatabaseSource : IDataSource<int, Record>
{
    private const int MinId = 1000;
    private const int MaxId = 9999;
    private readonly IDatabase _db;

    public async Task<RangeChunk<int, Record>> FetchAsync(
        Range<int> requested,
        CancellationToken ct)
    {
        // Define available range
        var availableRange = Intervals.NET.Factories.Range.Closed(MinId, MaxId);

        // Compute intersection with requested range
        var fulfillable = requested.Intersect(availableRange);

        // No data available for this request
        if (fulfillable == null)
        {
            return new RangeChunk<int, Record>(
                null,  // Range must be null (not requested) to signal no data available
                Array.Empty<Record>()
            );
        }

        // Fetch available portion
        var data = await _db.FetchRecordsAsync(
            fulfillable.LowerBound.Value,
            fulfillable.UpperBound.Value,
            ct
        );

        return new RangeChunk<int, Record>(fulfillable, data);
    }
}
```

### Example Scenarios

```
// Database has records with IDs [1000..9999]

// Scenario 1: Request within bounds
Request:  [2000..3000]
Response: Range = [2000..3000], Data = 1001 records ✓

// Scenario 2: Request overlaps lower boundary
Request:  [500..1500]
Response: Range = [1000..1500], Data = 501 records (truncated at lower) ✓

// Scenario 3: Request overlaps upper boundary
Request:  [9500..10500]
Response: Range = [9500..9999], Data = 500 records (truncated at upper) ✓

// Scenario 4: Request completely out of bounds (too low)
Request:  [0..999]
Response: Range = null, Data = empty ✓

// Scenario 5: Request completely out of bounds (too high)
Request:  [10000..11000]
Response: Range = null, Data = empty ✓
```

### Time-Series Example

```csharp
public class TimeSeriesSource : IDataSource<DateTime, Measurement>
{
    private readonly DateTime _dataStart = new DateTime(2020, 1, 1);
    private readonly DateTime _dataEnd   = new DateTime(2024, 12, 31);
    private readonly ITimeSeriesDatabase _db;

    public async Task<RangeChunk<DateTime, Measurement>> FetchAsync(
        Range<DateTime> requested,
        CancellationToken ct)
    {
        var availableRange = Intervals.NET.Factories.Range.Closed(_dataStart, _dataEnd);
        var fulfillable = requested.Intersect(availableRange);

        if (fulfillable == null)
        {
            return new RangeChunk<DateTime, Measurement>(
                null,  // Range must be null (not requested) to signal no data available
                Array.Empty<Measurement>()
            );
        }

        var measurements = await _db.QueryAsync(
            fulfillable.LowerBound.Value,
            fulfillable.UpperBound.Value,
            ct
        );

        return new RangeChunk<DateTime, Measurement>(fulfillable, measurements);
    }
}
```

---

## Testing

Boundary handling tests are in `BoundaryHandlingTests.cs` in the integration test project.

### Test Coverage (15 tests)

**RangeResult Structure Tests:**
- Full data returns range and data
- Data property contains correct elements
- Multiple requests each return correct range

**Cached Data Tests:**
- Cached data still returns correct range
- Subset of cache returns requested range (not full cache)
- Overlapping cache returns merged range

**Range Property Validation:**
- Range matches data length
- Data boundaries match range boundaries

**Edge Cases:**
- Single element range
- Large ranges (10,000+ elements)
- Disposed cache throws `ObjectDisposedException`

**Sequential Access Patterns:**
- Forward scrolling pattern
- Backward scrolling pattern

### Running Boundary Handling Tests

```bash
# Run all boundary handling tests
dotnet test --filter "FullyQualifiedName~BoundaryHandlingTests"

# Run specific test
dotnet test --filter "FullyQualifiedName~RangeResult_WithFullData_ReturnsRangeAndData"
```

---

## Architectural Considerations

### Why Range is Nullable in RangeResult

`RangeResult.Range` is nullable to signal data unavailability at the user-facing API level without exceptions.

**Alternatives considered:**
1. **Exception-based** — throw `DataUnavailableException` → makes unavailability exceptional (it is not)
2. **Sentinel ranges** — use a special range like `[int.MinValue, int.MinValue]` → ambiguous and error-prone
3. **Nullable Range** (chosen) — explicit unavailability signal, type-safe, idiomatic C#

### Cache Behavior with Partial Data

When the data source returns a truncated range, the cache stores and returns exactly what the data source provided. If the data source returns `[1000..1500]` when `[500..1500]` was requested, the cache:

1. Stores `[1000..1500]` internally
2. Returns `RangeResult` with `Range = [1000..1500]`
3. Fetches `[500..999]` on the next request for `[500..1500]` (gap filling)

Cache contiguity is preserved — no gaps are created in the cached range. Partial fulfillment is handled by storing only the fulfilled portion and fetching missing portions on subsequent requests.

### User Path vs Background Path

**User Path** — returns data immediately:
- User requests `[100..200]`
- Cache returns `RangeResult` with `Range = [100..200]` (or truncated if data source boundary applies)
- Intent published for background rebalancing
- User is never blocked by rebalance operations

**Background Path** — expands the cache window asynchronously:
- Decision engine evaluates intent
- Rebalance executor fetches expansion ranges via `IDataSource`
- Results stored as `RangeChunk`, converted to internal cache state

`RangeResult` is the user-facing response type; `RangeChunk` is the data source response type used by the background path. The cache converts `RangeChunk` → cached state → `RangeResult`.

### Thread Safety

`RangeResult` is a `sealed record` (reference type) with `init`-only properties, making it immutable and inherently thread-safe:

- No mutable state — all properties are read-only after construction
- `ReadOnlyMemory<TData>` is safe to share across threads
- Multiple threads can hold references to the same `RangeResult` safely

The cache itself is safe for its internal concurrency model (one user thread + background threads), but is not designed for multiple independent consumers sharing one cache instance. See [`docs/sliding-window/architecture.md`](architecture.md) for the threading model.

---

## See Also

- [`docs/shared/boundary-handling.md`](../shared/boundary-handling.md) — `IDataSource` contract and nullable Range semantics (shared)
- [`docs/sliding-window/architecture.md`](architecture.md) — threading model and concurrency
- [`docs/sliding-window/invariants.md`](invariants.md) — cache contiguity and Invariant A.10b
- [`docs/sliding-window/components/user-path.md`](components/user-path.md) — `UserRequestHandler` and `RangeResult` construction
