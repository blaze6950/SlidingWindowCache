using BenchmarkDotNet.Attributes;
using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Caching.Benchmarks.Infrastructure;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Policies;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Selectors;
using Intervals.NET.Caching.VisitedPlaces.Public.Cache;
using Intervals.NET.Caching.VisitedPlaces.Public.Configuration;

namespace Intervals.NET.Caching.Benchmarks.VisitedPlaces;

/// <summary>
/// Construction Benchmarks for VisitedPlaces Cache.
/// Measures two distinct costs:
/// (A) Builder pipeline cost — full fluent builder API overhead
/// (B) Raw constructor cost — pre-built options, direct instantiation
/// 
/// Each storage mode (Snapshot, LinkedList) is measured independently.
/// 
/// Methodology:
/// - No state reuse: each invocation constructs a fresh cache
/// - Zero-latency SynchronousDataSource
/// - No cache priming — measures pure construction cost
/// - MemoryDiagnoser tracks allocation overhead of construction path
/// </summary>
[MemoryDiagnoser]
[MarkdownExporter]
public class ConstructionBenchmarks
{
    private SynchronousDataSource _dataSource = null!;
    private IntegerFixedStepDomain _domain;

    // Pre-built options for raw constructor benchmarks
    private VisitedPlacesCacheOptions<int, int> _snapshotOptions = null!;
    private VisitedPlacesCacheOptions<int, int> _linkedListOptions = null!;
    private IReadOnlyList<Caching.VisitedPlaces.Core.Eviction.IEvictionPolicy<int, int>> _policies = null!;
    private Caching.VisitedPlaces.Core.Eviction.IEvictionSelector<int, int> _selector = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _domain = new IntegerFixedStepDomain();
        _dataSource = new SynchronousDataSource(_domain);

        _snapshotOptions = new VisitedPlacesCacheOptions<int, int>(
            storageStrategy: SnapshotAppendBufferStorageOptions<int, int>.Default,
            eventChannelCapacity: 128);

        _linkedListOptions = new VisitedPlacesCacheOptions<int, int>(
            storageStrategy: LinkedListStrideIndexStorageOptions<int, int>.Default,
            eventChannelCapacity: 128);

        _policies = [MaxSegmentCountPolicy.Create<int, int>(1000)];
        _selector = LruEvictionSelector.Create<int, int>();
    }

    #region Builder Pipeline

    /// <summary>
    /// Measures full builder pipeline cost for Snapshot storage.
    /// Includes: builder allocation, options builder, eviction config builder, cache construction.
    /// </summary>
    [Benchmark]
    public VisitedPlacesCache<int, int, IntegerFixedStepDomain> Builder_Snapshot()
    {
        return (VisitedPlacesCache<int, int, IntegerFixedStepDomain>)VisitedPlacesCacheBuilder
            .For<int, int, IntegerFixedStepDomain>(_dataSource, _domain)
            .WithOptions(o => o
                .WithStorageStrategy(SnapshotAppendBufferStorageOptions<int, int>.Default)
                .WithEventChannelCapacity(128))
            .WithEviction(e => e
                .AddPolicy(MaxSegmentCountPolicy.Create<int, int>(1000))
                .WithSelector(LruEvictionSelector.Create<int, int>()))
            .Build();
    }

    /// <summary>
    /// Measures full builder pipeline cost for LinkedList storage.
    /// </summary>
    [Benchmark]
    public VisitedPlacesCache<int, int, IntegerFixedStepDomain> Builder_LinkedList()
    {
        return (VisitedPlacesCache<int, int, IntegerFixedStepDomain>)VisitedPlacesCacheBuilder
            .For<int, int, IntegerFixedStepDomain>(_dataSource, _domain)
            .WithOptions(o => o
                .WithStorageStrategy(LinkedListStrideIndexStorageOptions<int, int>.Default)
                .WithEventChannelCapacity(128))
            .WithEviction(e => e
                .AddPolicy(MaxSegmentCountPolicy.Create<int, int>(1000))
                .WithSelector(LruEvictionSelector.Create<int, int>()))
            .Build();
    }

    #endregion

    #region Raw Constructor

    /// <summary>
    /// Measures raw constructor cost with pre-built options for Snapshot storage.
    /// Isolates constructor overhead from builder pipeline.
    /// </summary>
    [Benchmark]
    public VisitedPlacesCache<int, int, IntegerFixedStepDomain> Constructor_Snapshot()
    {
        return new VisitedPlacesCache<int, int, IntegerFixedStepDomain>(
            _dataSource, _domain, _snapshotOptions, _policies, _selector);
    }

    /// <summary>
    /// Measures raw constructor cost with pre-built options for LinkedList storage.
    /// </summary>
    [Benchmark]
    public VisitedPlacesCache<int, int, IntegerFixedStepDomain> Constructor_LinkedList()
    {
        return new VisitedPlacesCache<int, int, IntegerFixedStepDomain>(
            _dataSource, _domain, _linkedListOptions, _policies, _selector);
    }

    #endregion
}
