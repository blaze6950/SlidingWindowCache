using Intervals.NET;
using Intervals.NET.Domain.Default.Numeric;
using SlidingWindowCache.Public;
using SlidingWindowCache.Public.Configuration;
using SlidingWindowCache.Public.Dto;

namespace SlidingWindowCache.WasmValidation;

/// <summary>
/// Minimal IDataSource implementation for WebAssembly compilation validation.
/// This is NOT a demo or test - it exists purely to ensure the library compiles for net8.0-browser.
/// </summary>
internal sealed class SimpleDataSource : IDataSource<int, int>
{
    public Task<IEnumerable<int>> FetchAsync(Range<int> range, CancellationToken cancellationToken)
    {
        // Generate deterministic sequential data for the range
        // Range.Start and Range.End are RangeValue<int>, use implicit conversion to int
        var start = range.Start.Value;
        var end = range.End.Value;
        var data = Enumerable.Range(start, end - start + 1);
        return Task.FromResult(data);
    }

    public Task<IEnumerable<RangeChunk<int, int>>> FetchAsync(
        IEnumerable<Range<int>> ranges,
        CancellationToken cancellationToken
    )
    {
        var chunks = ranges.Select(r =>
        {
            var start = r.Start.Value;
            var end = r.End.Value;
            return new RangeChunk<int, int>(r, Enumerable.Range(start, end - start + 1));
        });
        return Task.FromResult(chunks);
    }
}

/// <summary>
/// WebAssembly compilation validator for SlidingWindowCache.
/// This static class validates that the library can compile for net8.0-browser.
/// It is NOT intended to be executed - successful compilation is the validation.
/// </summary>
public static class WasmCompilationValidator
{
    /// <summary>
    /// Validates that WindowCache can be instantiated and used with all required types.
    /// This method demonstrates minimal usage of the public API to ensure WebAssembly compatibility.
    /// </summary>
    public static async Task ValidateCompilation()
    {
        // Create a simple data source
        var dataSource = new SimpleDataSource();

        // Create domain (IntegerFixedStepDomain from Intervals.NET)
        var domain = new IntegerFixedStepDomain();

        // Configure cache options
        var options = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0.2,
            rightThreshold: 0.2
        );

        // Instantiate WindowCache with concrete generic types
        var cache = new WindowCache<int, int, IntegerFixedStepDomain>(
            dataSource,
            domain,
            options
        );

        // Perform a GetDataAsync call with Range from Intervals.NET
        var range = Intervals.NET.Factories.Range.Closed<int>(0, 10);
        var data = await cache.GetDataAsync(range, CancellationToken.None);

        // Wait for background operations to complete
        await cache.WaitForIdleAsync();

        // Compilation successful if this code builds for net8.0-browser
    }
}