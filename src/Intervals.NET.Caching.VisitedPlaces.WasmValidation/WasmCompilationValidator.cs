using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Caching.Dto;
using Intervals.NET.Caching.Extensions;
using Intervals.NET.Caching.Layered;
using Intervals.NET.Caching.VisitedPlaces.Core;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Policies;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Selectors;
using Intervals.NET.Caching.VisitedPlaces.Public.Cache;
using Intervals.NET.Caching.VisitedPlaces.Public.Configuration;
using Intervals.NET.Caching.VisitedPlaces.Public.Extensions;

namespace Intervals.NET.Caching.VisitedPlaces.WasmValidation;

/// <summary>
/// Minimal IDataSource implementation for WebAssembly compilation validation.
/// This is NOT a demo or test - it exists purely to ensure the library compiles for net8.0-browser.
/// </summary>
internal sealed class SimpleDataSource : IDataSource<int, int>
{
    public Task<RangeChunk<int, int>> FetchAsync(Range<int> range, CancellationToken cancellationToken)
    {
        var start = range.Start.Value;
        var end = range.End.Value;
        var data = Enumerable.Range(start, end - start + 1).ToArray();
        return Task.FromResult(new RangeChunk<int, int>(range, data));
    }

    public Task<IEnumerable<RangeChunk<int, int>>> FetchAsync(
        IEnumerable<Range<int>> ranges,
        CancellationToken cancellationToken)
    {
        var chunks = ranges.Select(r =>
        {
            var start = r.Start.Value;
            var end = r.End.Value;
            return new RangeChunk<int, int>(r, Enumerable.Range(start, end - start + 1).ToArray());
        }).ToArray();
        return Task.FromResult<IEnumerable<RangeChunk<int, int>>>(chunks);
    }
}

/// <summary>
/// WebAssembly compilation validator for Intervals.NET.Caching.VisitedPlaces.
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
/// <strong>StorageStrategy</strong>: SnapshotAppendBuffer (default) vs LinkedListStrideIndex
/// </description></item>
/// <item><description>
/// <strong>EventChannelCapacity</strong>: null (unbounded) vs bounded
/// </description></item>
/// <item><description>
/// <strong>SegmentTtl</strong>: null (no TTL) vs with TTL
/// </description></item>
/// </list>
/// <para>This ensures all storage strategies and channel configurations are WebAssembly-compatible.</para>
/// </remarks>
public static class WasmCompilationValidator
{
    private static readonly IReadOnlyList<IEvictionPolicy<int, int>> Policies =
        [new MaxSegmentCountPolicy<int, int>(maxCount: 100)];

    private static readonly IEvictionSelector<int, int> Selector =
        new LruEvictionSelector<int, int>();

    /// <summary>
    /// Validates Configuration 1: SnapshotAppendBuffer storage + unbounded event channel.
    /// Default configuration — no TTL.
    /// </summary>
    public static async Task ValidateConfiguration1_SnapshotStorage_UnboundedChannel()
    {
        var dataSource = new SimpleDataSource();
        var domain = new IntegerFixedStepDomain();

        var options = new VisitedPlacesCacheOptions<int, int>(
            storageStrategy: SnapshotAppendBufferStorageOptions<int, int>.Default,
            eventChannelCapacity: null  // unbounded
        );

        await using var cache = (VisitedPlacesCache<int, int, IntegerFixedStepDomain>)
            VisitedPlacesCacheBuilder
                .For<int, int, IntegerFixedStepDomain>(dataSource, domain)
                .WithOptions(options)
                .WithEviction(Policies, Selector)
                .Build();

        var range = Factories.Range.Closed<int>(0, 10);
        var result = await cache.GetDataAsync(range, CancellationToken.None);
        await cache.WaitForIdleAsync();
        _ = result.Data.Length;
    }

    /// <summary>
    /// Validates Configuration 2: SnapshotAppendBuffer storage + bounded event channel.
    /// </summary>
    public static async Task ValidateConfiguration2_SnapshotStorage_BoundedChannel()
    {
        var dataSource = new SimpleDataSource();
        var domain = new IntegerFixedStepDomain();

        var options = new VisitedPlacesCacheOptions<int, int>(
            storageStrategy: SnapshotAppendBufferStorageOptions<int, int>.Default,
            eventChannelCapacity: 64
        );

        await using var cache = (VisitedPlacesCache<int, int, IntegerFixedStepDomain>)
            VisitedPlacesCacheBuilder
                .For<int, int, IntegerFixedStepDomain>(dataSource, domain)
                .WithOptions(options)
                .WithEviction(Policies, Selector)
                .Build();

        var range = Factories.Range.Closed<int>(0, 10);
        var result = await cache.GetDataAsync(range, CancellationToken.None);
        await cache.WaitForIdleAsync();
        _ = result.Data.Length;
    }

    /// <summary>
    /// Validates Configuration 3: LinkedListStrideIndex storage + unbounded event channel.
    /// </summary>
    public static async Task ValidateConfiguration3_LinkedListStorage_UnboundedChannel()
    {
        var dataSource = new SimpleDataSource();
        var domain = new IntegerFixedStepDomain();

        var options = new VisitedPlacesCacheOptions<int, int>(
            storageStrategy: LinkedListStrideIndexStorageOptions<int, int>.Default,
            eventChannelCapacity: null
        );

        await using var cache = (VisitedPlacesCache<int, int, IntegerFixedStepDomain>)
            VisitedPlacesCacheBuilder
                .For<int, int, IntegerFixedStepDomain>(dataSource, domain)
                .WithOptions(options)
                .WithEviction(Policies, Selector)
                .Build();

        var range = Factories.Range.Closed<int>(0, 10);
        var result = await cache.GetDataAsync(range, CancellationToken.None);
        await cache.WaitForIdleAsync();
        _ = result.Data.Length;
    }

    /// <summary>
    /// Validates Configuration 4: LinkedListStrideIndex storage + bounded event channel.
    /// </summary>
    public static async Task ValidateConfiguration4_LinkedListStorage_BoundedChannel()
    {
        var dataSource = new SimpleDataSource();
        var domain = new IntegerFixedStepDomain();

        var options = new VisitedPlacesCacheOptions<int, int>(
            storageStrategy: LinkedListStrideIndexStorageOptions<int, int>.Default,
            eventChannelCapacity: 64
        );

        await using var cache = (VisitedPlacesCache<int, int, IntegerFixedStepDomain>)
            VisitedPlacesCacheBuilder
                .For<int, int, IntegerFixedStepDomain>(dataSource, domain)
                .WithOptions(options)
                .WithEviction(Policies, Selector)
                .Build();

        var range = Factories.Range.Closed<int>(0, 10);
        var result = await cache.GetDataAsync(range, CancellationToken.None);
        await cache.WaitForIdleAsync();
        _ = result.Data.Length;
    }

    /// <summary>
    /// Validates Configuration 5: SnapshotAppendBuffer storage + SegmentTtl enabled.
    /// Exercises the TTL subsystem WASM compatibility.
    /// </summary>
    public static async Task ValidateConfiguration5_SnapshotStorage_WithTtl()
    {
        var dataSource = new SimpleDataSource();
        var domain = new IntegerFixedStepDomain();

        var options = new VisitedPlacesCacheOptions<int, int>(
            storageStrategy: SnapshotAppendBufferStorageOptions<int, int>.Default,
            segmentTtl: TimeSpan.FromMinutes(5)
        );

        await using var cache = (VisitedPlacesCache<int, int, IntegerFixedStepDomain>)
            VisitedPlacesCacheBuilder
                .For<int, int, IntegerFixedStepDomain>(dataSource, domain)
                .WithOptions(options)
                .WithEviction(Policies, Selector)
                .Build();

        var range = Factories.Range.Closed<int>(0, 10);
        var result = await cache.GetDataAsync(range, CancellationToken.None);
        await cache.WaitForIdleAsync();
        _ = result.Data.Length;
    }

    /// <summary>
    /// Validates strong consistency mode:
    /// <see cref="RangeCacheConsistencyExtensions.GetDataAndWaitForIdleAsync{TRange,TData,TDomain}"/>
    /// compiles for net8.0-browser.
    /// </summary>
    public static async Task ValidateStrongConsistencyMode_GetDataAndWaitForIdleAsync()
    {
        var dataSource = new SimpleDataSource();
        var domain = new IntegerFixedStepDomain();

        var options = new VisitedPlacesCacheOptions<int, int>();

        await using var cache = (VisitedPlacesCache<int, int, IntegerFixedStepDomain>)
            VisitedPlacesCacheBuilder
                .For<int, int, IntegerFixedStepDomain>(dataSource, domain)
                .WithOptions(options)
                .WithEviction(Policies, Selector)
                .Build();

        var range = Factories.Range.Closed<int>(0, 10);

        var result = await cache.GetDataAndWaitForIdleAsync(range, CancellationToken.None);
        _ = result.Data.Length;
        _ = result.CacheInteraction;

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var degradedResult = await cache.GetDataAndWaitForIdleAsync(range, cts.Token);
        _ = degradedResult.Data.Length;
        _ = degradedResult.CacheInteraction;
    }

    /// <summary>
    /// Validates the layered cache builder extension:
    /// <see cref="VisitedPlacesLayerExtensions.AddVisitedPlacesLayer{TRange,TData,TDomain}"/>
    /// compiles for net8.0-browser.
    /// </summary>
    public static async Task ValidateLayeredCache_TwoLayer()
    {
        var domain = new IntegerFixedStepDomain();

        await using var layered = (LayeredRangeCache<int, int, IntegerFixedStepDomain>)
            await VisitedPlacesCacheBuilder
                .Layered<int, int, IntegerFixedStepDomain>(new SimpleDataSource(), domain)
                .AddVisitedPlacesLayer(Policies, Selector)
                .AddVisitedPlacesLayer(Policies, Selector)
                .BuildAsync();

        var range = Factories.Range.Closed<int>(0, 10);
        var result = await layered.GetDataAsync(range, CancellationToken.None);
        await layered.WaitForIdleAsync();
        _ = result.Data.Length;
        _ = layered.LayerCount;
    }

    /// <summary>
    /// Validates that <see cref="FifoEvictionSelector{TRange,TData}"/> compiles for net8.0-browser.
    /// </summary>
    public static async Task ValidateFifoEvictionSelector()
    {
        var dataSource = new SimpleDataSource();
        var domain = new IntegerFixedStepDomain();

        IReadOnlyList<IEvictionPolicy<int, int>> policies =
            [new MaxSegmentCountPolicy<int, int>(maxCount: 10)];
        IEvictionSelector<int, int> selector = new FifoEvictionSelector<int, int>();

        await using var cache = (VisitedPlacesCache<int, int, IntegerFixedStepDomain>)
            VisitedPlacesCacheBuilder
                .For<int, int, IntegerFixedStepDomain>(dataSource, domain)
                .WithEviction(policies, selector)
                .Build();

        var range = Factories.Range.Closed<int>(0, 10);
        var result = await cache.GetDataAsync(range, CancellationToken.None);
        await cache.WaitForIdleAsync();
        _ = result.Data.Length;
    }

    /// <summary>
    /// Validates that <see cref="SmallestFirstEvictionSelector{TRange,TData,TDomain}"/> compiles for net8.0-browser.
    /// </summary>
    public static async Task ValidateSmallestFirstEvictionSelector()
    {
        var dataSource = new SimpleDataSource();
        var domain = new IntegerFixedStepDomain();

        IReadOnlyList<IEvictionPolicy<int, int>> policies =
            [new MaxSegmentCountPolicy<int, int>(maxCount: 10)];
        IEvictionSelector<int, int> selector = new SmallestFirstEvictionSelector<int, int, IntegerFixedStepDomain>(domain);

        await using var cache = (VisitedPlacesCache<int, int, IntegerFixedStepDomain>)
            VisitedPlacesCacheBuilder
                .For<int, int, IntegerFixedStepDomain>(dataSource, domain)
                .WithEviction(policies, selector)
                .Build();

        var range = Factories.Range.Closed<int>(0, 10);
        var result = await cache.GetDataAsync(range, CancellationToken.None);
        await cache.WaitForIdleAsync();
        _ = result.Data.Length;
    }

    /// <summary>
    /// Validates that <see cref="MaxTotalSpanPolicy{TRange,TData,TDomain}"/> compiles for net8.0-browser.
    /// </summary>
    public static async Task ValidateMaxTotalSpanPolicy()
    {
        var dataSource = new SimpleDataSource();
        var domain = new IntegerFixedStepDomain();

        IReadOnlyList<IEvictionPolicy<int, int>> policies =
            [new MaxTotalSpanPolicy<int, int, IntegerFixedStepDomain>(maxTotalSpan: 1000, domain)];
        IEvictionSelector<int, int> selector = new LruEvictionSelector<int, int>();

        await using var cache = (VisitedPlacesCache<int, int, IntegerFixedStepDomain>)
            VisitedPlacesCacheBuilder
                .For<int, int, IntegerFixedStepDomain>(dataSource, domain)
                .WithEviction(policies, selector)
                .Build();

        var range = Factories.Range.Closed<int>(0, 10);
        var result = await cache.GetDataAsync(range, CancellationToken.None);
        await cache.WaitForIdleAsync();
        _ = result.Data.Length;
    }
}
