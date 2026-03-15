using BenchmarkDotNet.Attributes;
using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Caching.Benchmarks.Infrastructure;
using Intervals.NET.Caching.VisitedPlaces.Public.Cache;

namespace Intervals.NET.Caching.Benchmarks.VisitedPlaces;

/// <summary>
/// Scenario Benchmarks for VisitedPlaces Cache.
/// End-to-end scenario testing with deterministic burst patterns.
/// NOT microbenchmarks - measures complete workflows.
/// 
/// Three scenarios:
/// - ColdStart: All misses on empty cache (initial population cost)
/// - AllHits: All hits on pre-populated cache (steady-state read cost)
/// - Churn: All misses at capacity — each request triggers fetch + store + eviction
/// 
/// Methodology:
/// - Learning pass in GlobalSetup exercises all three scenario code paths on throwaway
///   caches so the data source can be frozen before measurement iterations begin.
/// - Deterministic burst of BurstSize sequential requests.
/// - Each request targets a distinct non-overlapping range.
/// - WaitForIdleAsync INSIDE benchmark (measuring complete workflow cost).
/// - Fresh cache per iteration.
/// 
/// Parameters:
/// - BurstSize: {10, 50, 100} — number of sequential requests in burst
/// - StorageStrategy: Snapshot vs LinkedList
/// - SchedulingStrategy: Unbounded vs Bounded(10) event channel
/// </summary>
[MemoryDiagnoser]
[MarkdownExporter]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
public class ScenarioBenchmarks
{
    /// <summary>
    /// Scheduling strategy: Unbounded (null capacity) vs Bounded (capacity=10).
    /// </summary>
    public enum SchedulingStrategyType
    {
        Unbounded,
        Bounded
    }

    private FrozenDataSource _frozenDataSource = null!;
    private IntegerFixedStepDomain _domain;
    private VisitedPlacesCache<int, int, IntegerFixedStepDomain>? _cache;

    private const int SegmentSpan = 10;

    // Precomputed request sequences
    private Range<int>[] _requestSequence = null!;

    /// <summary>
    /// Number of sequential requests in the burst.
    /// </summary>
    [Params(10, 50, 100)]
    public int BurstSize { get; set; }

    /// <summary>
    /// Storage strategy — Snapshot vs LinkedList.
    /// </summary>
    [Params(StorageStrategyType.Snapshot, StorageStrategyType.LinkedList)]
    public StorageStrategyType StorageStrategy { get; set; }

    /// <summary>
    /// Event channel scheduling strategy — Unbounded vs Bounded(10).
    /// </summary>
    [Params(SchedulingStrategyType.Unbounded, SchedulingStrategyType.Bounded)]
    public SchedulingStrategyType SchedulingStrategy { get; set; }

    private int? EventChannelCapacity => SchedulingStrategy switch
    {
        SchedulingStrategyType.Unbounded => null,
        SchedulingStrategyType.Bounded => 10,
        _ => throw new ArgumentOutOfRangeException()
    };

    [GlobalSetup]
    public void GlobalSetup()
    {
        _domain = new IntegerFixedStepDomain();

        // Build request sequence: BurstSize non-overlapping ranges
        _requestSequence = new Range<int>[BurstSize];
        for (var i = 0; i < BurstSize; i++)
        {
            var start = i * SegmentSpan;
            var end = start + SegmentSpan - 1;
            _requestSequence[i] = Factories.Range.Closed<int>(start, end);
        }

        var farStart = BurstSize * SegmentSpan + 10000;

        // Learning pass: exercise all three scenario paths on throwaway caches.
        var learningSource = new SynchronousDataSource(_domain);

        // ColdStart path: fire request sequence on empty cache (all misses)
        var throwaway1 = VpcCacheHelpers.CreateCache(
            learningSource, _domain, StorageStrategy,
            maxSegmentCount: BurstSize + 100,
            eventChannelCapacity: EventChannelCapacity);
        foreach (var range in _requestSequence)
        {
            throwaway1.GetDataAsync(range, CancellationToken.None).GetAwaiter().GetResult();
        }
        throwaway1.WaitForIdleAsync().GetAwaiter().GetResult();

        // Churn path: populate far-away segments (at capacity), then fire request sequence
        var throwaway2 = VpcCacheHelpers.CreateCache(
            learningSource, _domain, StorageStrategy,
            maxSegmentCount: BurstSize,
            eventChannelCapacity: EventChannelCapacity);
        VpcCacheHelpers.PopulateSegments(throwaway2, BurstSize, SegmentSpan, farStart);
        foreach (var range in _requestSequence)
        {
            throwaway2.GetDataAsync(range, CancellationToken.None).GetAwaiter().GetResult();
        }
        throwaway2.WaitForIdleAsync().GetAwaiter().GetResult();

        // AllHits path: populate with request sequence, then fire hits
        // (request sequence ranges already learned by ColdStart pass above)
        var throwaway3 = VpcCacheHelpers.CreateCache(
            learningSource, _domain, StorageStrategy,
            maxSegmentCount: BurstSize + 100,
            eventChannelCapacity: EventChannelCapacity);
        VpcCacheHelpers.PopulateSegments(throwaway3, BurstSize, SegmentSpan);
        foreach (var range in _requestSequence)
        {
            throwaway3.GetDataAsync(range, CancellationToken.None).GetAwaiter().GetResult();
        }
        throwaway3.WaitForIdleAsync().GetAwaiter().GetResult();

        _frozenDataSource = learningSource.Freeze();
    }

    #region ColdStart

    [IterationSetup(Target = nameof(Scenario_ColdStart))]
    public void IterationSetup_ColdStart()
    {
        // Empty cache — all requests will be misses
        _cache = VpcCacheHelpers.CreateCache(
            _frozenDataSource, _domain, StorageStrategy,
            maxSegmentCount: BurstSize + 100,
            eventChannelCapacity: EventChannelCapacity);
    }

    /// <summary>
    /// Cold start: BurstSize requests on empty cache.
    /// Every request is a miss → fetch + store + normalization.
    /// Measures initial population cost.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("ColdStart")]
    public async Task Scenario_ColdStart()
    {
        foreach (var range in _requestSequence)
        {
            await _cache!.GetDataAsync(range, CancellationToken.None);
        }

        await _cache!.WaitForIdleAsync();
    }

    #endregion

    #region AllHits

    [IterationSetup(Target = nameof(Scenario_AllHits))]
    public void IterationSetup_AllHits()
    {
        // Pre-populated cache — all requests will be hits
        _cache = VpcCacheHelpers.CreateCache(
            _frozenDataSource, _domain, StorageStrategy,
            maxSegmentCount: BurstSize + 100,
            eventChannelCapacity: EventChannelCapacity);

        // Populate with exactly the segments that will be requested
        VpcCacheHelpers.PopulateSegments(_cache, BurstSize, SegmentSpan);
    }

    /// <summary>
    /// All hits: BurstSize requests on pre-populated cache.
    /// Every request is a hit → no fetch, no normalization.
    /// Measures steady-state read throughput.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("AllHits")]
    public async Task Scenario_AllHits()
    {
        foreach (var range in _requestSequence)
        {
            await _cache!.GetDataAsync(range, CancellationToken.None);
        }

        await _cache!.WaitForIdleAsync();
    }

    #endregion

    #region Churn

    [IterationSetup(Target = nameof(Scenario_Churn))]
    public void IterationSetup_Churn()
    {
        // Cache at capacity with segments that do NOT overlap the request sequence.
        // This ensures every request is a miss AND triggers eviction.
        _cache = VpcCacheHelpers.CreateCache(
            _frozenDataSource, _domain, StorageStrategy,
            maxSegmentCount: BurstSize,
            eventChannelCapacity: EventChannelCapacity);

        // Populate with segments far away from the request sequence
        var farStart = BurstSize * SegmentSpan + 10000;
        VpcCacheHelpers.PopulateSegments(_cache, BurstSize, SegmentSpan, farStart);
    }

    /// <summary>
    /// Churn: BurstSize requests at capacity with non-overlapping existing segments.
    /// Every request is a miss → fetch + store + eviction evaluation + eviction execution.
    /// Measures worst-case throughput under constant eviction pressure.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("Churn")]
    public async Task Scenario_Churn()
    {
        foreach (var range in _requestSequence)
        {
            await _cache!.GetDataAsync(range, CancellationToken.None);
        }

        await _cache!.WaitForIdleAsync();
    }

    #endregion
}
