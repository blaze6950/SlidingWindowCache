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
    public Task<RangeChunk<int, int>> FetchAsync(Range<int> range, CancellationToken cancellationToken)
    {
        // Generate deterministic sequential data for the range
        // Range.Start and Range.End are RangeValue<int>, use implicit conversion to int
        var start = range.Start.Value;
        var end = range.End.Value;
        var data = Enumerable.Range(start, end - start + 1);
        return Task.FromResult(new RangeChunk<int, int>(range, data));
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
/// <remarks>
/// <para><strong>Strategy Coverage:</strong></para>
/// <para>
/// The validator exercises all combinations of internal strategy-determining configurations:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <strong>ReadMode</strong>: Snapshot (array-based) vs CopyOnRead (List-based)
/// </description></item>
/// <item><description>
/// <strong>RebalanceQueueCapacity</strong>: null (task-based) vs bounded (channel-based)
/// </description></item>
/// </list>
/// <para>
/// This ensures all storage strategies (SnapshotReadStorage, CopyOnReadStorage) and
/// serialization strategies (task-based, channel-based) are WebAssembly-compatible.
/// </para>
/// </remarks>
public static class WasmCompilationValidator
{
    /// <summary>
    /// Validates Configuration 1: SnapshotReadStorage + Task-based serialization.
    /// Tests: Array-based storage with unbounded task-based execution queue.
    /// </summary>
    /// <remarks>
    /// <para><strong>Internal Strategies:</strong></para>
    /// <list type="bullet">
    /// <item><description>Storage: SnapshotReadStorage (contiguous array)</description></item>
    /// <item><description>Serialization: Task-based (unbounded queue)</description></item>
    /// </list>
    /// </remarks>
    public static async Task ValidateConfiguration1_SnapshotMode_UnboundedQueue()
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
            rightThreshold: 0.2,
            rebalanceQueueCapacity: null  // Task-based serialization
        );

        // Instantiate WindowCache with concrete generic types
        var cache = new WindowCache<int, int, IntegerFixedStepDomain>(
            dataSource,
            domain,
            options
        );

        // Perform a GetDataAsync call with Range from Intervals.NET
        var range = Intervals.NET.Factories.Range.Closed<int>(0, 10);
        var result = await cache.GetDataAsync(range, CancellationToken.None);

        // Wait for background operations to complete
        await cache.WaitForIdleAsync();

        // Use result to avoid unused variable warning
        _ = result.Data.Length;

        // Compilation successful if this code builds for net8.0-browser
    }

    /// <summary>
    /// Validates Configuration 2: CopyOnReadStorage + Task-based serialization.
    /// Tests: List-based storage with unbounded task-based execution queue.
    /// </summary>
    /// <remarks>
    /// <para><strong>Internal Strategies:</strong></para>
    /// <list type="bullet">
    /// <item><description>Storage: CopyOnReadStorage (growable List)</description></item>
    /// <item><description>Serialization: Task-based (unbounded queue)</description></item>
    /// </list>
    /// </remarks>
    public static async Task ValidateConfiguration2_CopyOnReadMode_UnboundedQueue()
    {
        var dataSource = new SimpleDataSource();
        var domain = new IntegerFixedStepDomain();

        var options = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.CopyOnRead,  // CopyOnReadStorage
            leftThreshold: 0.2,
            rightThreshold: 0.2,
            rebalanceQueueCapacity: null  // Task-based serialization
        );

        var cache = new WindowCache<int, int, IntegerFixedStepDomain>(
            dataSource,
            domain,
            options
        );

        var range = Intervals.NET.Factories.Range.Closed<int>(0, 10);
        var result = await cache.GetDataAsync(range, CancellationToken.None);
        await cache.WaitForIdleAsync();
        _ = result.Data.Length;
    }

    /// <summary>
    /// Validates Configuration 3: SnapshotReadStorage + Channel-based serialization.
    /// Tests: Array-based storage with bounded channel-based execution queue.
    /// </summary>
    /// <remarks>
    /// <para><strong>Internal Strategies:</strong></para>
    /// <list type="bullet">
    /// <item><description>Storage: SnapshotReadStorage (contiguous array)</description></item>
    /// <item><description>Serialization: Channel-based (bounded queue with backpressure)</description></item>
    /// </list>
    /// </remarks>
    public static async Task ValidateConfiguration3_SnapshotMode_BoundedQueue()
    {
        var dataSource = new SimpleDataSource();
        var domain = new IntegerFixedStepDomain();

        var options = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot,  // SnapshotReadStorage
            leftThreshold: 0.2,
            rightThreshold: 0.2,
            rebalanceQueueCapacity: 5  // Channel-based serialization
        );

        var cache = new WindowCache<int, int, IntegerFixedStepDomain>(
            dataSource,
            domain,
            options
        );

        var range = Intervals.NET.Factories.Range.Closed<int>(0, 10);
        var result = await cache.GetDataAsync(range, CancellationToken.None);
        await cache.WaitForIdleAsync();
        _ = result.Data.Length;
    }

    /// <summary>
    /// Validates Configuration 4: CopyOnReadStorage + Channel-based serialization.
    /// Tests: List-based storage with bounded channel-based execution queue.
    /// </summary>
    /// <remarks>
    /// <para><strong>Internal Strategies:</strong></para>
    /// <list type="bullet">
    /// <item><description>Storage: CopyOnReadStorage (growable List)</description></item>
    /// <item><description>Serialization: Channel-based (bounded queue with backpressure)</description></item>
    /// </list>
    /// </remarks>
    public static async Task ValidateConfiguration4_CopyOnReadMode_BoundedQueue()
    {
        var dataSource = new SimpleDataSource();
        var domain = new IntegerFixedStepDomain();

        var options = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.CopyOnRead,  // CopyOnReadStorage
            leftThreshold: 0.2,
            rightThreshold: 0.2,
            rebalanceQueueCapacity: 5  // Channel-based serialization
        );

        var cache = new WindowCache<int, int, IntegerFixedStepDomain>(
            dataSource,
            domain,
            options
        );

        var range = Intervals.NET.Factories.Range.Closed<int>(0, 10);
        var result = await cache.GetDataAsync(range, CancellationToken.None);
        await cache.WaitForIdleAsync();
        _ = result.Data.Length;
    }
}