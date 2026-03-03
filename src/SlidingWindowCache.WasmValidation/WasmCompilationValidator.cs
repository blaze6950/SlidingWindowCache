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
        var data = Enumerable.Range(start, end - start + 1).ToArray();
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
            return new RangeChunk<int, int>(r, Enumerable.Range(start, end - start + 1).ToArray());
        }).ToList();
        return Task.FromResult<IEnumerable<RangeChunk<int, int>>>(chunks);
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
/// <para><strong>Opt-In Consistency Modes:</strong></para>
/// <para>
/// The validator also covers the <see cref="WindowCacheConsistencyExtensions"/> extension methods
/// for hybrid and strong consistency modes, including the cancellation graceful degradation
/// path (<c>OperationCanceledException</c> from <c>WaitForIdleAsync</c> caught, result returned):
/// </para>
/// <list type="bullet">
/// <item><description>
/// <see cref="WindowCacheConsistencyExtensions.GetDataAndWaitForIdleAsync{TRange,TData,TDomain}"/> —
/// strong consistency (always waits for idle)
/// </description></item>
/// <item><description>
/// <see cref="WindowCacheConsistencyExtensions.GetDataAndWaitOnMissAsync{TRange,TData,TDomain}"/> —
/// hybrid consistency (waits on miss/partial hit, returns immediately on full hit)
/// </description></item>
/// </list>
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

    /// <summary>
    /// Validates strong consistency mode: <see cref="WindowCacheConsistencyExtensions.GetDataAndWaitForIdleAsync{TRange,TData,TDomain}"/>
    /// compiles for net8.0-browser. Exercises both the normal path (idle wait completes) and the
    /// cancellation graceful degradation path (OperationCanceledException from WaitForIdleAsync is
    /// caught and the already-obtained result is returned).
    /// </summary>
    /// <remarks>
    /// <para><strong>Types Validated:</strong></para>
    /// <list type="bullet">
    /// <item><description>
/// <see cref="WindowCacheConsistencyExtensions.GetDataAndWaitForIdleAsync{TRange,TData,TDomain}"/> —
/// strong consistency extension method; composes GetDataAsync + unconditional WaitForIdleAsync
    /// </description></item>
    /// <item><description>
    /// The <c>try { await WaitForIdleAsync } catch (OperationCanceledException) { }</c> pattern
    /// inside the extension method — validates that exception handling compiles on WASM
    /// </description></item>
    /// </list>
    /// <para><strong>Why One Configuration Is Sufficient:</strong></para>
    /// <para>
    /// The extension method introduces no new strategy axes (storage or serialization). It is a
    /// thin wrapper over GetDataAsync + WaitForIdleAsync; the four internal strategy combinations
    /// are already covered by Configurations 1–4.
    /// </para>
    /// </remarks>
    public static async Task ValidateStrongConsistencyMode_GetDataAndWaitForIdleAsync()
    {
        var dataSource = new SimpleDataSource();
        var domain = new IntegerFixedStepDomain();

        var options = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0.2,
            rightThreshold: 0.2
        );

        var cache = new WindowCache<int, int, IntegerFixedStepDomain>(
            dataSource,
            domain,
            options
        );

        var range = Intervals.NET.Factories.Range.Closed<int>(0, 10);

        // Normal path: waits for idle and returns the result
        var result = await cache.GetDataAndWaitForIdleAsync(range, CancellationToken.None);
        _ = result.Data.Length;
        _ = result.CacheInteraction;

        // Cancellation graceful degradation path: pre-cancelled token; WaitForIdleAsync
        // throws OperationCanceledException which is caught — result returned gracefully
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var degradedResult = await cache.GetDataAndWaitForIdleAsync(range, cts.Token);
        _ = degradedResult.Data.Length;
        _ = degradedResult.CacheInteraction;
    }

    /// <summary>
    /// Validates hybrid consistency mode: <see cref="WindowCacheConsistencyExtensions.GetDataAndWaitOnMissAsync{TRange,TData,TDomain}"/>
    /// compiles for net8.0-browser. Exercises the FullHit path (no idle wait), the FullMiss path
    /// (conditional idle wait), and the cancellation graceful degradation path.
    /// </summary>
    /// <remarks>
    /// <para><strong>Types Validated:</strong></para>
    /// <list type="bullet">
    /// <item><description>
/// <see cref="WindowCacheConsistencyExtensions.GetDataAndWaitOnMissAsync{TRange,TData,TDomain}"/> —
/// hybrid consistency extension method; composes GetDataAsync + conditional WaitForIdleAsync
    /// gated on <see cref="CacheInteraction"/>
    /// </description></item>
    /// <item><description>
    /// <see cref="CacheInteraction"/> enum — read from <see cref="RangeResult{TRange,TData}.CacheInteraction"/>
    /// on the returned result
    /// </description></item>
    /// <item><description>
    /// The <c>try { await WaitForIdleAsync } catch (OperationCanceledException) { }</c> pattern
    /// inside the extension method — validates that exception handling compiles on WASM
    /// </description></item>
    /// </list>
    /// <para><strong>Why One Configuration Is Sufficient:</strong></para>
    /// <para>
    /// The extension method introduces no new strategy axes. The four internal strategy
    /// combinations are already covered by Configurations 1–4.
    /// </para>
    /// </remarks>
    public static async Task ValidateHybridConsistencyMode_GetDataAndWaitOnMissAsync()
    {
        var dataSource = new SimpleDataSource();
        var domain = new IntegerFixedStepDomain();

        var options = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0.2,
            rightThreshold: 0.2
        );

        var cache = new WindowCache<int, int, IntegerFixedStepDomain>(
            dataSource,
            domain,
            options
        );

        var range = Intervals.NET.Factories.Range.Closed<int>(0, 10);

        // FullMiss path (first request — cold cache): idle wait is triggered
        var missResult = await cache.GetDataAndWaitOnMissAsync(range, CancellationToken.None);
        _ = missResult.Data.Length;
        _ = missResult.CacheInteraction; // FullMiss

        // FullHit path (warm cache): no idle wait, returns immediately
        var hitResult = await cache.GetDataAndWaitOnMissAsync(range, CancellationToken.None);
        _ = hitResult.Data.Length;
        _ = hitResult.CacheInteraction; // FullHit

        // Cancellation graceful degradation path: pre-cancelled token on a miss scenario;
        // WaitForIdleAsync throws OperationCanceledException which is caught — result returned gracefully
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var degradedResult = await cache.GetDataAndWaitOnMissAsync(range, cts.Token);
        _ = degradedResult.Data.Length;
        _ = degradedResult.CacheInteraction;
    }

    /// <summary>
    /// Validates layered cache: <see cref="LayeredWindowCacheBuilder{TRange,TData,TDomain}"/>,
    /// <see cref="WindowCacheDataSourceAdapter{TRange,TData,TDomain}"/>, and
    /// <see cref="LayeredWindowCache{TRange,TData,TDomain}"/> compile for net8.0-browser.
    /// Uses the recommended configuration: CopyOnRead inner layer (large buffers) +
    /// Snapshot outer layer (small buffers).
    /// </summary>
    /// <remarks>
    /// <para><strong>Types Validated:</strong></para>
    /// <list type="bullet">
    /// <item><description>
    /// <see cref="LayeredWindowCacheBuilder{TRange,TData,TDomain}"/> — fluent builder
    /// wiring layers together via <see cref="WindowCacheDataSourceAdapter{TRange,TData,TDomain}"/>
    /// </description></item>
    /// <item><description>
    /// <see cref="WindowCacheDataSourceAdapter{TRange,TData,TDomain}"/> — adapter bridging
    /// <see cref="IWindowCache{TRange,TData,TDomain}"/> to <see cref="IDataSource{TRange,TData}"/>
    /// </description></item>
    /// <item><description>
    /// <see cref="LayeredWindowCache{TRange,TData,TDomain}"/> — wrapper that delegates
    /// <see cref="IWindowCache{TRange,TData,TDomain}.GetDataAsync"/> to the outermost layer and
    /// awaits all layers sequentially on <see cref="IWindowCache{TRange,TData,TDomain}.WaitForIdleAsync"/>
    /// </description></item>
    /// </list>
    /// <para><strong>Why One Method Is Sufficient:</strong></para>
    /// <para>
    /// The layered cache types introduce no new strategy axes: they delegate to underlying
    /// <see cref="WindowCache{TRange,TData,TDomain}"/> instances whose internal strategies
    /// are already covered by Configurations 1–4. A single method proving all three new
    /// public types compile on WASM is therefore sufficient.
    /// </para>
    /// </remarks>
    public static async Task ValidateLayeredCache_TwoLayer_RecommendedConfig()
    {
        var domain = new IntegerFixedStepDomain();

        // Inner layer: CopyOnRead + large buffers (recommended for deep/backing layers)
        var innerOptions = new WindowCacheOptions(
            leftCacheSize: 5.0,
            rightCacheSize: 5.0,
            readMode: UserCacheReadMode.CopyOnRead,
            leftThreshold: 0.3,
            rightThreshold: 0.3
        );

        // Outer (user-facing) layer: Snapshot + small buffers (recommended for user-facing layer)
        var outerOptions = new WindowCacheOptions(
            leftCacheSize: 0.5,
            rightCacheSize: 0.5,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0.2,
            rightThreshold: 0.2
        );

        // Build the layered cache — exercises LayeredWindowCacheBuilder,
        // WindowCacheDataSourceAdapter, and LayeredWindowCache
        await using var cache = LayeredWindowCacheBuilder<int, int, IntegerFixedStepDomain>
            .Create(new SimpleDataSource(), domain)
            .AddLayer(innerOptions)
            .AddLayer(outerOptions)
            .Build();

        var range = Intervals.NET.Factories.Range.Closed<int>(0, 10);
        var result = await cache.GetDataAsync(range, CancellationToken.None);

        // WaitForIdleAsync on LayeredWindowCache awaits all layers (outermost to innermost)
        await cache.WaitForIdleAsync();

        _ = result.Data.Length;
        _ = cache.LayerCount;
    }
}