using BenchmarkDotNet.Attributes;
using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Caching.Benchmarks.Infrastructure;
using Intervals.NET.Caching.VisitedPlaces.Public.Cache;

namespace Intervals.NET.Caching.Benchmarks.VisitedPlaces;

/// <summary>
/// Cache Miss Benchmarks for VisitedPlaces Cache.
/// Measures the complete cost of a cache miss: data source fetch + background normalization.
/// 
/// Two methods:
/// - NoEviction: miss on a cache with ample capacity (no eviction triggered)
/// - WithEviction: miss on a cache at capacity (eviction triggered on normalization)
/// 
/// Methodology:
/// - Pre-populated cache with TotalSegments segments separated by gaps
/// - Request in a gap beyond all segments (guaranteed full miss)
/// - WaitForIdleAsync INSIDE benchmark (measuring complete miss + normalization cost)
/// - Fresh cache per iteration
/// 
/// Parameters:
/// - TotalSegments: {10, 1K, 100K, 1M} — straddles ~50K Snapshot/LinkedList crossover
/// - StorageStrategy: Snapshot vs LinkedList
/// - AppendBufferSize: {1, 8} — normalization frequency (every 1 vs every 8 stores)
/// </summary>
[MemoryDiagnoser]
[MarkdownExporter]
public class CacheMissBenchmarks
{
    private VisitedPlacesCache<int, int, IntegerFixedStepDomain>? _cache;
    private SynchronousDataSource _dataSource = null!;
    private IntegerFixedStepDomain _domain;
    private Range<int> _missRange;

    private const int SegmentSpan = 10;
    private const int GapSize = 10; // Gap between segments during population

    /// <summary>
    /// Total segments in cache — tests scaling from small to very large segment counts.
    /// Values straddle the ~50K crossover point between Snapshot and LinkedList strategies.
    /// </summary>
    [Params(10, 1_000, 100_000, 1_000_000)]
    public int TotalSegments { get; set; }

    /// <summary>
    /// Storage strategy — Snapshot vs LinkedList.
    /// </summary>
    [Params(StorageStrategyType.Snapshot, StorageStrategyType.LinkedList)]
    public StorageStrategyType StorageStrategy { get; set; }

    /// <summary>
    /// Append buffer size — controls normalization frequency.
    /// 1 = normalize every store, 8 = normalize every 8 stores (default).
    /// </summary>
    [Params(1, 8)]
    public int AppendBufferSize { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _domain = new IntegerFixedStepDomain();
        _dataSource = new SynchronousDataSource(_domain);

        // Miss range: far beyond all populated segments
        const int stride = SegmentSpan + GapSize;
        var beyondAll = TotalSegments * stride + 1000;
        _missRange = Factories.Range.Closed<int>(beyondAll, beyondAll + SegmentSpan - 1);
    }

    #region NoEviction

    [IterationSetup(Target = nameof(CacheMiss_NoEviction))]
    public void IterationSetup_NoEviction()
    {
        // Generous capacity — no eviction triggered on miss
        _cache = VpcCacheHelpers.CreateCache(
            _dataSource, _domain, StorageStrategy,
            maxSegmentCount: TotalSegments + 1000, // means no eviction during benchmark
            appendBufferSize: AppendBufferSize);

        VpcCacheHelpers.PopulateWithGaps(_cache, TotalSegments, SegmentSpan, GapSize);
    }

    /// <summary>
    /// Measures complete cache miss cost without eviction.
    /// Includes: data source fetch + normalization (store + metadata update).
    /// WaitForIdleAsync inside benchmark to capture full background processing cost.
    /// </summary>
    [Benchmark]
    public async Task CacheMiss_NoEviction()
    {
        await _cache!.GetDataAsync(_missRange, CancellationToken.None);
        await _cache.WaitForIdleAsync();
    }

    #endregion

    #region WithEviction

    [IterationSetup(Target = nameof(CacheMiss_WithEviction))]
    public void IterationSetup_WithEviction()
    {
        // At capacity — eviction triggered on miss (one segment evicted per new segment stored)
        _cache = VpcCacheHelpers.CreateCache(
            _dataSource, _domain, StorageStrategy,
            maxSegmentCount: TotalSegments, // means eviction during benchmark
            appendBufferSize: AppendBufferSize);

        VpcCacheHelpers.PopulateWithGaps(_cache, TotalSegments, SegmentSpan, GapSize);
    }

    /// <summary>
    /// Measures complete cache miss cost with eviction.
    /// Includes: data source fetch + normalization (store + eviction evaluation + eviction execution).
    /// </summary>
    [Benchmark]
    public async Task CacheMiss_WithEviction()
    {
        await _cache!.GetDataAsync(_missRange, CancellationToken.None);
        await _cache.WaitForIdleAsync();
    }

    #endregion
}
