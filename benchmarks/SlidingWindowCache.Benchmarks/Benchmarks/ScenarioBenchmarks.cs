using BenchmarkDotNet.Attributes;
using Intervals.NET;
using Intervals.NET.Domain.Default.Numeric;
using SlidingWindowCache.Benchmarks.Infrastructure;
using SlidingWindowCache.Public;
using SlidingWindowCache.Public.Configuration;

namespace SlidingWindowCache.Benchmarks.Benchmarks;

/// <summary>
/// Scenario Benchmarks
/// End-to-end scenario testing including cold start and locality patterns.
/// NOT microbenchmarks - measures complete workflows.
/// 
/// EXECUTION FLOW: Simulates realistic usage patterns
/// 
/// Methodology:
/// - Fresh cache per iteration
/// - Cold start: Measures initial cache population (includes WaitForIdleAsync)
/// - Compares cached vs uncached approaches
/// </summary>
[MemoryDiagnoser]
[MarkdownExporter]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
public class ScenarioBenchmarks
{
    private SynchronousDataSource _dataSource = null!;
    private IntegerFixedStepDomain _domain;
    private WindowCache<int, int, IntegerFixedStepDomain>? _snapshotCache;
    private WindowCache<int, int, IntegerFixedStepDomain>? _copyOnReadCache;
    private WindowCacheOptions _snapshotOptions = null!;
    private WindowCacheOptions _copyOnReadOptions = null!;
    private Range<int> _coldStartRange;

    /// <summary>
    /// Requested range size - varies from small (100) to large (10,000) to test scenario scaling behavior.
    /// </summary>
    [Params(100, 1_000, 10_000)]
    public int RangeSpan { get; set; }

    /// <summary>
    /// Cache coefficient size for left/right prefetch - varies from minimal (1) to aggressive (100).
    /// Combined with RangeSpan, determines total materialized cache size in scenarios.
    /// </summary>
    [Params(1, 10, 100)]
    public int CacheCoefficientSize { get; set; }

    private int ColdStartRangeStart => 10000;
    private int ColdStartRangeEnd => ColdStartRangeStart + RangeSpan - 1;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _domain = new IntegerFixedStepDomain();
        _dataSource = new SynchronousDataSource(_domain);

        // Cold start configuration
        _coldStartRange = Intervals.NET.Factories.Range.Closed<int>(
            ColdStartRangeStart,
            ColdStartRangeEnd
        );

        _snapshotOptions = new WindowCacheOptions(
            leftCacheSize: CacheCoefficientSize,
            rightCacheSize: CacheCoefficientSize,
            UserCacheReadMode.Snapshot,
            leftThreshold: 0.2,
            rightThreshold: 0.2
        );

        _copyOnReadOptions = new WindowCacheOptions(
            leftCacheSize: CacheCoefficientSize,
            rightCacheSize: CacheCoefficientSize,
            UserCacheReadMode.CopyOnRead,
            leftThreshold: 0.2,
            rightThreshold: 0.2
        );
    }

    #region Cold Start Benchmarks

    [IterationSetup(Target = nameof(ColdStart_Rebalance_Snapshot) + "," + nameof(ColdStart_Rebalance_CopyOnRead))]
    public void ColdStartIterationSetup()
    {
        // Create fresh caches for cold start measurement
        _snapshotCache = new WindowCache<int, int, IntegerFixedStepDomain>(
            _dataSource,
            _domain,
            _snapshotOptions
        );

        _copyOnReadCache = new WindowCache<int, int, IntegerFixedStepDomain>(
            _dataSource,
            _domain,
            _copyOnReadOptions
        );
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("ColdStart")]
    public async Task ColdStart_Rebalance_Snapshot()
    {
        // Measure complete cold start: initial fetch + rebalance
        // WaitForIdleAsync is PART of cold start cost
        await _snapshotCache!.GetDataAsync(_coldStartRange, CancellationToken.None);
        await _snapshotCache.WaitForIdleAsync();
    }

    [Benchmark]
    [BenchmarkCategory("ColdStart")]
    public async Task ColdStart_Rebalance_CopyOnRead()
    {
        // Measure complete cold start: initial fetch + rebalance
        // WaitForIdleAsync is PART of cold start cost
        await _copyOnReadCache!.GetDataAsync(_coldStartRange, CancellationToken.None);
        await _copyOnReadCache.WaitForIdleAsync();
    }

    #endregion
}
