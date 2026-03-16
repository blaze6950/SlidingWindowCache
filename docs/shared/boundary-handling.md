# Boundary Handling — Shared Concepts

This document covers the nullable `Range` semantics and `IDataSource` boundary contract that apply to all cache implementations.

---

## The Nullable Range Contract

`RangeResult.Range` is **nullable**. A `null` range means the data source has no data for the requested range — a **physical boundary miss**.

Always check `Range` before accessing data:

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
    // No data available for this range (physical boundary)
}
```

---

## IDataSource Boundary Contract

`IDataSource.FetchAsync` must never throw when a requested range is outside the data source's physical boundaries. Instead, return a `RangeChunk` with `Range = null`:

```csharp
// Bounded source — database with min/max ID bounds
IDataSource<int, Record> bounded = new FuncDataSource<int, Record>(
    async (range, ct) =>
    {
        var available = range.Intersect(Range.Closed(minId, maxId));
        if (available is null)
            return new RangeChunk<int, Record>(null, []);   // <-- null range: no data

        var records = await db.FetchAsync(available, ct);
        return new RangeChunk<int, Record>(available, records);
    });
```

**Rule: never throw from `IDataSource` for out-of-bounds requests.** Return `null` range instead. Throwing from `IDataSource` on boundary misses is a bug — the cache cannot distinguish a data source failure from a boundary condition.

---

## Typical Boundary Scenarios

| Scenario         | Example                                          | Correct IDataSource behavior                        |
|------------------|--------------------------------------------------|-----------------------------------------------------|
| Below minimum    | Request `[-100, 50]` when data starts at `0`     | Return `RangeChunk(null, [])`                       |
| Above maximum    | Request `[9990, 10100]` when data ends at `9999` | Return `RangeChunk(Range.Closed(9990, 9999), data)` |
| Entirely outside | Request `[5000, 6000]` when data is `[0, 1000]`  | Return `RangeChunk(null, [])`                       |
| Partial overlap  | Request `[-50, 200]` when data starts at `0`     | Return `RangeChunk(Range.Closed(0, 200), data)`     |

---

## FuncDataSource

`FuncDataSource<TRange, TData>` wraps an async delegate for inline data source creation without a full class:

```csharp
IDataSource<int, string> source = new FuncDataSource<int, string>(
    async (range, ct) =>
    {
        var data = await myService.QueryAsync(range, ct);
        return new RangeChunk<int, string>(range, data);
    });
```

For bounded sources:

```csharp
IDataSource<int, string> bounded = new FuncDataSource<int, string>(
    async (range, ct) =>
    {
        var available = range.Intersect(Range.Closed(minId, maxId));
        if (available is null)
            return new RangeChunk<int, string>(null, []);
        var data = await myService.QueryAsync(available, ct);
        return new RangeChunk<int, string>(available, data);
    });
```

---

## Batch Fetch

`IDataSource` also has a batch overload:

```csharp
Task<IEnumerable<RangeChunk<TRange, TData>>> FetchAsync(
    IEnumerable<Range<TRange>> ranges,
    CancellationToken cancellationToken)
```

The default implementation parallelizes single-range `FetchAsync` calls. Override for custom batching (e.g., a single SQL query with multiple ranges, or a custom retry strategy).

---

## See Also

- `docs/shared/glossary.md` — `RangeResult`, `RangeChunk`, `IDataSource` definitions
- `docs/sliding-window/boundary-handling.md` — SlidingWindow-specific boundary examples
- `docs/visited-places/scenarios.md` — VisitedPlaces boundary behavior (physical boundary miss in U1/U5, non-contiguous segment handling)
