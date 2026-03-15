using Intervals.NET.Caching.Extensions;
using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Policies;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Selectors;
using Intervals.NET.Caching.VisitedPlaces.Public.Cache;
using Intervals.NET.Caching.VisitedPlaces.Public.Configuration;

namespace Intervals.NET.Caching.Benchmarks.Infrastructure;

/// <summary>
/// BenchmarkDotNet parameter enum for VPC storage strategy selection.
/// Maps to concrete <see cref="StorageStrategyOptions{TRange,TData}"/> instances.
/// </summary>
public enum StorageStrategyType
{
    Snapshot,
    LinkedList
}

/// <summary>
/// BenchmarkDotNet parameter enum for VPC eviction selector selection.
/// Maps to concrete <see cref="IEvictionSelector{TRange,TData}"/> instances.
/// </summary>
public enum EvictionSelectorType
{
    Lru,
    Fifo
}

/// <summary>
/// Shared helpers for VPC benchmark setup: factory methods, cache population, and parameter mapping.
/// All operations use public API only (no InternalsVisibleTo, no reflection).
/// </summary>
public static class VpcCacheHelpers
{
    /// <summary>
    /// Creates a <see cref="StorageStrategyOptions{TRange,TData}"/> for the given strategy type and append buffer size.
    /// </summary>
    public static StorageStrategyOptions<int, int> CreateStorageOptions(
        StorageStrategyType strategyType,
        int appendBufferSize = 8)
    {
        return strategyType switch
        {
            StorageStrategyType.Snapshot => new SnapshotAppendBufferStorageOptions<int, int>(appendBufferSize),
            StorageStrategyType.LinkedList => new LinkedListStrideIndexStorageOptions<int, int>(appendBufferSize),
            _ => throw new ArgumentOutOfRangeException(nameof(strategyType))
        };
    }

    /// <summary>
    /// Creates an <see cref="IEvictionSelector{TRange,TData}"/> for the given selector type.
    /// </summary>
    public static IEvictionSelector<int, int> CreateSelector(EvictionSelectorType selectorType)
    {
        return selectorType switch
        {
            EvictionSelectorType.Lru => LruEvictionSelector.Create<int, int>(),
            EvictionSelectorType.Fifo => FifoEvictionSelector.Create<int, int>(),
            _ => throw new ArgumentOutOfRangeException(nameof(selectorType))
        };
    }

    /// <summary>
    /// Creates a MaxSegmentCountPolicy with the specified max count.
    /// </summary>
    public static IReadOnlyList<IEvictionPolicy<int, int>> CreateMaxSegmentCountPolicies(int maxCount)
    {
        return [MaxSegmentCountPolicy.Create<int, int>(maxCount)];
    }

    /// <summary>
    /// Creates a VPC cache with the specified configuration using the public constructor.
    /// </summary>
    public static VisitedPlacesCache<int, int, IntegerFixedStepDomain> CreateCache(
        IDataSource<int, int> dataSource,
        IntegerFixedStepDomain domain,
        StorageStrategyType strategyType,
        int maxSegmentCount,
        EvictionSelectorType selectorType = EvictionSelectorType.Lru,
        int appendBufferSize = 8,
        int? eventChannelCapacity = 128)
    {
        var options = new VisitedPlacesCacheOptions<int, int>(
            storageStrategy: CreateStorageOptions(strategyType, appendBufferSize),
            eventChannelCapacity: eventChannelCapacity);

        var policies = CreateMaxSegmentCountPolicies(maxSegmentCount);
        var selector = CreateSelector(selectorType);

        return new VisitedPlacesCache<int, int, IntegerFixedStepDomain>(
            dataSource, domain, options, policies, selector);
    }

    /// <summary>
    /// Populates a VPC cache with the specified number of adjacent, non-overlapping segments.
    /// Each segment has the specified span, placed adjacently starting from startPosition.
    /// Uses strong consistency (GetDataAndWaitForIdleAsync) to guarantee segments are stored.
    /// </summary>
    /// <param name="cache">The cache to populate.</param>
    /// <param name="segmentCount">Number of segments to create.</param>
    /// <param name="segmentSpan">Span of each segment (number of discrete domain points).</param>
    /// <param name="startPosition">Starting position for the first segment.</param>
    public static void PopulateSegments(
        IRangeCache<int, int, IntegerFixedStepDomain> cache,
        int segmentCount,
        int segmentSpan,
        int startPosition = 0)
    {
        for (var i = 0; i < segmentCount; i++)
        {
            var start = startPosition + (i * segmentSpan);
            var end = start + segmentSpan - 1;
            var range = Factories.Range.Closed<int>(start, end);
            cache.GetDataAndWaitForIdleAsync(range).GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Populates a VPC cache with segments that have gaps between them.
    /// Each segment has the specified span, separated by gaps of the specified size.
    /// </summary>
    /// <param name="cache">The cache to populate.</param>
    /// <param name="segmentCount">Number of segments to create.</param>
    /// <param name="segmentSpan">Span of each segment.</param>
    /// <param name="gapSize">Size of the gap between consecutive segments.</param>
    /// <param name="startPosition">Starting position for the first segment.</param>
    public static void PopulateWithGaps(
        IRangeCache<int, int, IntegerFixedStepDomain> cache,
        int segmentCount,
        int segmentSpan,
        int gapSize,
        int startPosition = 0)
    {
        var stride = segmentSpan + gapSize;
        for (var i = 0; i < segmentCount; i++)
        {
            var start = startPosition + (i * stride);
            var end = start + segmentSpan - 1;
            var range = Factories.Range.Closed<int>(start, end);
            cache.GetDataAndWaitForIdleAsync(range).GetAwaiter().GetResult();
        }
    }
}
