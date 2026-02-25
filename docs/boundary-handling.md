# Boundary Handling & Data Availability

---

## Table of Contents

- [Overview](#overview)
- [RangeResult Structure](#rangeresult-structure)
- [IDataSource Contract](#idatasource-contract)
- [Usage Patterns](#usage-patterns)
- [Bounded Data Sources](#bounded-data-sources)
- [Testing](#testing)
- [Architectural Considerations](#architectural-considerations)

---

## Overview

The Sliding Window Cache provides explicit boundary handling through the `RangeResult<TRange, TData>` type returned by `GetDataAsync()`. This design allows data sources to communicate data availability, partial fulfillment, and physical boundaries to consumers.

### Why RangeResult?

**Previous API (Implicit):**
```csharp
ReadOnlyMemory<TData> data = await cache.GetDataAsync(range, ct);
// Problem: No way to know if this is the full requested range or truncated
```

**Current API (Explicit):**
```csharp
RangeResult<TRange, TData> result = await cache.GetDataAsync(range, ct);
Range<TRange>? actualRange = result.Range;  // The ACTUAL range returned
ReadOnlyMemory<TData> data = result.Data;   // The data for that range
```

**Benefits:**
- **Explicit Contracts**: Consumers know exactly what range was fulfilled
- **Boundary Awareness**: Data sources can signal truncation at physical boundaries
- **No Exceptions for Normal Cases**: Out-of-bounds is not exceptional—it's expected
- **Future Extensibility**: Foundation for features like sparse data, tombstones, metadata

---

## RangeResult Structure

```csharp
public sealed record RangeResult<TRange, TData>(
    Range<TRange>? Range,
    ReadOnlyMemory<TData> Data
) where TRange : IComparable<TRange>;
```

### Properties

| Property | Type                    | Description                                                                                      |
|----------|-------------------------|--------------------------------------------------------------------------------------------------|
| `Range`  | `Range<TRange>?`        | **Nullable**. The actual range covered by the returned data. `null` indicates no data available. |
| `Data`   | `ReadOnlyMemory<TData>` | The materialized data elements. May be empty if `Range` is `null`.                               |

### Invariants

1. **Range-Data Consistency**: When `Range` is non-null, `Data.Length` MUST equal `Range.Span(domain)`
2. **Empty Data Semantics**: `Data.IsEmpty` when `Range` is `null` (no data available)
3. **Contiguity**: `Data` contains sequential elements matching the boundaries of `Range`

---

## IDataSource Contract

Data sources implement `IDataSource<TRange, TData>` and return `RangeChunk<TRange, TData>` from `FetchAsync`:

```csharp
public interface IDataSource<TRangeType, TDataType> 
    where TRangeType : IComparable<TRangeType>
{
    Task<RangeChunk<TRangeType, TDataType>> FetchAsync(
        Range<TRangeType> range, 
        CancellationToken cancellationToken
    );
}
```

### RangeChunk Structure

```csharp
public record RangeChunk<TRange, TData>(
    Range<TRange>? Range,
    IEnumerable<TData> Data
) where TRange : IComparable<TRange>;
```

**Important:** `RangeChunk.Range` is **nullable**. IDataSource implementations MUST return `null` Range (not empty Range) to signal that no data is available for the requested range. The cache uses this to distinguish between "empty result" vs "unavailable data".

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

// Process elements
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
        {
            Console.WriteLine("Data truncated at start");
        }
        if (result.Range.End < requestedRange.End)
        {
            Console.WriteLine("Data truncated at end");
        }
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

For data sources with physical boundaries (databases with min/max IDs, time-series with temporal limits, paginated APIs):

### Implementation Guidelines

1. **No Exceptions**: Never throw for out-of-bounds requests
2. **Truncate Gracefully**: Return intersection of requested and available
3. **Consistent Span**: Ensure `Data.Count()` matches `Range.Span(domain)`
4. **Empty Result**: Return empty enumerable when no data available

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
                requested,  // Echo back requested range
                Array.Empty<Record>()  // Empty data
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

```csharp
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
    private readonly DateTime _dataEnd = new DateTime(2024, 12, 31);
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
                requested,
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

The cache includes comprehensive boundary handling tests in `BoundaryHandlingTests.cs`:

### Test Coverage (15 tests)

**RangeResult Structure Tests:**
- ✅ Full data returns range and data
- ✅ Data property contains correct elements
- ✅ Multiple requests each return correct range

**Cached Data Tests:**
- ✅ Cached data still returns correct range
- ✅ Subset of cache returns requested range (not full cache)
- ✅ Overlapping cache returns merged range

**Range Property Validation:**
- ✅ Range matches data length
- ✅ Data boundaries match range boundaries

**Edge Cases:**
- ✅ Single element range
- ✅ Large ranges (10,000+ elements)
- ✅ Disposed cache throws ObjectDisposedException

**Sequential Access Patterns:**
- ✅ Forward scrolling pattern
- ✅ Backward scrolling pattern

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

**Design Decision**: `RangeResult.Range` is nullable to signal data unavailability at the **user-facing API level**.

**Alternatives Considered:**
1. ❌ **Exception-based**: Throw `DataUnavailableException` → Makes unavailability exceptional (it's not)
2. ❌ **Sentinel ranges**: Use special range like `[int.MinValue, int.MinValue]` → Ambiguous and error-prone
3. ✅ **Nullable Range**: Explicit unavailability signal, type-safe, idiomatic C#

### Cache Behavior with Partial Data

**Question**: What happens when data source returns truncated range?

**Answer**: Cache stores and returns **exactly what the data source provides**. If data source returns `[1000..1500]` when requested `[500..1500]`, the cache:
1. Stores `[1000..1500]` internally
2. Returns `RangeResult` with `Range = [1000..1500]`
3. Future requests for `[500..1500]` will fetch `[500..999]` (gap filling)

**Invariant Preservation**: Cache maintains **contiguity** invariant—no gaps in cached ranges. Partial fulfillment is handled by:
- Storing only the fulfilled portion
- Fetching missing portions on subsequent requests
- Never creating gaps in the cache

### User Path vs Background Path

**Critical Distinction**:
- **User Path**: Returns data immediately (synchronous with respect to user request)
  - User requests `[100..200]`
  - Cache returns `RangeResult` with `Range = [100..200]` or truncated
  - Intent published for background rebalancing
  
- **Background Path**: Expands cache window asynchronously
  - Decision engine evaluates intent
  - Rebalance executor fetches expansion ranges
  - User is NEVER blocked by rebalance operations

**RangeResult at Both Paths**:
- User Path: `GetDataAsync()` returns `RangeResult` to user
- Background Path: Rebalance execution receives `RangeChunk` from data source
- Cache internally converts `RangeChunk` → cached state → `RangeResult` for users

### Thread Safety

**RangeResult is immutable** (`readonly record struct`), making it inherently thread-safe:
- No mutable state
- Value semantics (struct)
- `ReadOnlyMemory<TData>` is safe to share across threads
- Multiple threads can hold references to the same `RangeResult` safely

**Cache Thread Safety**:
- Single logical consumer (one user, one viewport)
- Internal concurrency (User thread + Background threads) is fully thread-safe
- NOT designed for multiple independent consumers sharing one cache

---

## Summary

**Key Takeaways:**

✅ **RangeResult provides explicit boundary contracts** between cache and consumers  
✅ **Range property indicates actual data returned** (may differ from requested)  
✅ **Nullable Range signals data unavailability** without exceptions  
✅ **Data sources truncate gracefully** at physical boundaries  
✅ **Comprehensive test coverage** validates all boundary scenarios  
✅ **Thread-safe immutable design** with value semantics

---

**For More Information:**
- [Architecture Model](architecture-model.md) - System design and concurrency model
- [Invariants](invariants.md) - System constraints and guarantees
- [README.md](../README.md) - Usage examples and getting started
- [Component Map](component-map.md) - Detailed component catalog

