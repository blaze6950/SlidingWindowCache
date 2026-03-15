using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Caching.Layered;
using Intervals.NET.Caching.SlidingWindow.Public.Configuration;
using Intervals.NET.Caching.SlidingWindow.Public.Extensions;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Policies;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Selectors;
using Intervals.NET.Caching.VisitedPlaces.Public.Configuration;
using Intervals.NET.Caching.VisitedPlaces.Public.Extensions;

namespace Intervals.NET.Caching.Benchmarks.Infrastructure;

/// <summary>
/// BenchmarkDotNet parameter enum for layered cache topology selection.
/// </summary>
public enum LayeredTopology
{
    /// <summary>SWC inner + SWC outer (homogeneous sliding window stack)</summary>
    SwcSwc,
    /// <summary>VPC inner + SWC outer (random-access backed by sequential-access)</summary>
    VpcSwc,
    /// <summary>VPC inner + SWC middle + SWC outer (three-layer deep stack)</summary>
    VpcSwcSwc
}

/// <summary>
/// Factory methods for building layered cache instances for benchmarks.
/// Uses public builder API with deterministic, zero-latency configuration.
/// </summary>
public static class LayeredCacheHelpers
{
    // Default SWC options for layered benchmarks: symmetric prefetch, zero debounce
    private static readonly SlidingWindowCacheOptions DefaultSwcOptions = new(
        leftCacheSize: 2.0,
        rightCacheSize: 2.0,
        readMode: UserCacheReadMode.Snapshot,
        leftThreshold: 0,
        rightThreshold: 0,
        debounceDelay: TimeSpan.Zero);

    /// <summary>
    /// Builds a layered cache with the specified topology.
    /// All layers use deterministic configuration suitable for benchmarks.
    /// </summary>
    public static IRangeCache<int, int, IntegerFixedStepDomain> Build(
        LayeredTopology topology,
        IDataSource<int, int> dataSource,
        IntegerFixedStepDomain domain)
    {
        return topology switch
        {
            LayeredTopology.SwcSwc => BuildSwcSwc(dataSource, domain),
            LayeredTopology.VpcSwc => BuildVpcSwc(dataSource, domain),
            LayeredTopology.VpcSwcSwc => BuildVpcSwcSwc(dataSource, domain),
            _ => throw new ArgumentOutOfRangeException(nameof(topology))
        };
    }

    /// <summary>
    /// Builds a SWC + SWC layered cache (homogeneous sliding window stack).
    /// Inner SWC acts as data source for outer SWC.
    /// </summary>
    public static IRangeCache<int, int, IntegerFixedStepDomain> BuildSwcSwc(
        IDataSource<int, int> dataSource,
        IntegerFixedStepDomain domain)
    {
        return new LayeredRangeCacheBuilder<int, int, IntegerFixedStepDomain>(dataSource, domain)
            .AddSlidingWindowLayer(DefaultSwcOptions)
            .AddSlidingWindowLayer(DefaultSwcOptions)
            .BuildAsync()
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>
    /// Builds a VPC + SWC layered cache (random-access inner, sequential-access outer).
    /// VPC provides cached segments, SWC provides sliding window view.
    /// </summary>
    public static IRangeCache<int, int, IntegerFixedStepDomain> BuildVpcSwc(
        IDataSource<int, int> dataSource,
        IntegerFixedStepDomain domain)
    {
        var vpcOptions = new VisitedPlacesCacheOptions<int, int>(
            storageStrategy: SnapshotAppendBufferStorageOptions<int, int>.Default,
            eventChannelCapacity: 128);

        var policies = new[] { MaxSegmentCountPolicy.Create<int, int>(1000) };
        var selector = LruEvictionSelector.Create<int, int>();

        return new LayeredRangeCacheBuilder<int, int, IntegerFixedStepDomain>(dataSource, domain)
            .AddVisitedPlacesLayer(policies, selector, vpcOptions)
            .AddSlidingWindowLayer(DefaultSwcOptions)
            .BuildAsync()
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>
    /// Builds a VPC + SWC + SWC layered cache (three-layer deep stack).
    /// VPC innermost, two SWC layers on top.
    /// </summary>
    public static IRangeCache<int, int, IntegerFixedStepDomain> BuildVpcSwcSwc(
        IDataSource<int, int> dataSource,
        IntegerFixedStepDomain domain)
    {
        var vpcOptions = new VisitedPlacesCacheOptions<int, int>(
            storageStrategy: SnapshotAppendBufferStorageOptions<int, int>.Default,
            eventChannelCapacity: 128);

        var policies = new[] { MaxSegmentCountPolicy.Create<int, int>(1000) };
        var selector = LruEvictionSelector.Create<int, int>();

        return new LayeredRangeCacheBuilder<int, int, IntegerFixedStepDomain>(dataSource, domain)
            .AddVisitedPlacesLayer(policies, selector, vpcOptions)
            .AddSlidingWindowLayer(DefaultSwcOptions)
            .AddSlidingWindowLayer(DefaultSwcOptions)
            .BuildAsync()
            .GetAwaiter()
            .GetResult();
    }
}
