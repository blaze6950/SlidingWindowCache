using BenchmarkDotNet.Attributes;
using Intervals.NET;
using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Domain.Extensions.Fixed;
using SlidingWindowCache.Benchmarks.Infrastructure;
using SlidingWindowCache.Public;
using SlidingWindowCache.Public.Configuration;

namespace SlidingWindowCache.Benchmarks.Benchmarks;

/// <summary>
/// Rebalance/Maintenance Flow Benchmarks
/// Measures ONLY window maintenance and rebalance operation costs.
/// Uses zero-latency SynchronousDataSource to isolate cache mechanics from I/O.
/// 
/// EXECUTION FLOW: Trigger mutation → WaitForIdleAsync → Measure rebalance cost
/// 
/// Methodology:
/// - Fresh cache per iteration
/// - SynchronousDataSource (zero latency) isolates cache mechanics
/// - Trigger rebalance by moving outside thresholds
/// - WaitForIdleAsync INSIDE benchmark methods (measuring rebalance)
/// - Aggressive thresholds ensure rebalancing occurs
/// </summary>
[MemoryDiagnoser]
[MarkdownExporter]
public class RebalanceFlowBenchmarks
{
    private WindowCache<int, int, IntegerFixedStepDomain>? _snapshotCache;
    private WindowCache<int, int, IntegerFixedStepDomain>? _copyOnReadCache;
    private SynchronousDataSource _dataSource = default!;
    private IntegerFixedStepDomain _domain = default!;

    private const int InitialStart = 1000;
    private const int InitialEnd = 2000;

    private Range<int> InitialCacheRange =>
        Intervals.NET.Factories.Range.Closed<int>(InitialStart, InitialEnd);

    private Range<int> InitialCacheRangeAfterRebalance => InitialCacheRange
        .ExpandByRatio(_domain, 1, 1);

    private Range<int> PartialHitRange => InitialCacheRangeAfterRebalance
        .Shift(_domain, InitialCacheRangeAfterRebalance.Span(_domain).Value / 2);

    private Range<int> FullMissRange => InitialCacheRangeAfterRebalance
        .Shift(_domain, InitialCacheRangeAfterRebalance.Span(_domain).Value * 3);

    private Range<int> _partialHitRange;
    private Range<int> _fullMissRange;
    private WindowCacheOptions _snapshotOptions;
    private WindowCacheOptions _copyOnReadOptions;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _domain = new IntegerFixedStepDomain();
        _dataSource = new SynchronousDataSource(_domain);

        // Pre-calculate rebalance triggering ranges
        _partialHitRange = PartialHitRange;

        _fullMissRange = FullMissRange;

        _snapshotOptions = new WindowCacheOptions(
            leftCacheSize: 1,
            rightCacheSize: 1,
            UserCacheReadMode.Snapshot,
            leftThreshold: 0,
            rightThreshold: 0
        );

        _copyOnReadOptions = new WindowCacheOptions(
            leftCacheSize: 1,
            rightCacheSize: 1,
            UserCacheReadMode.CopyOnRead,
            leftThreshold: 0,
            rightThreshold: 0
        );
    }

    [IterationSetup]
    public void IterationSetup()
    {
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

        // Prime both caches with initial window
        var initialRange = Intervals.NET.Factories.Range.Closed<int>(InitialStart, InitialEnd);
        _snapshotCache.GetDataAsync(initialRange, CancellationToken.None).GetAwaiter().GetResult();
        _copyOnReadCache.GetDataAsync(initialRange, CancellationToken.None).GetAwaiter().GetResult();

        // Wait for initial rebalancing to complete
        _snapshotCache.WaitForIdleAsync().GetAwaiter().GetResult();
        _copyOnReadCache.WaitForIdleAsync().GetAwaiter().GetResult();
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        // Final stabilization before next iteration
        _snapshotCache?.WaitForIdleAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        _copyOnReadCache?.WaitForIdleAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    }

    [Benchmark(Baseline = true)]
    public async Task Rebalance_AfterPartialHit_Snapshot()
    {
        // Trigger rebalance with partial overlap [1500, 2500] vs cached [1000, 2000]
        await _snapshotCache!.GetDataAsync(_partialHitRange, CancellationToken.None);

        // Explicitly measure rebalance cycle completion
        // This is the cost center we're measuring
        await _snapshotCache.WaitForIdleAsync(timeout: TimeSpan.FromSeconds(10));
    }

    [Benchmark]
    public async Task Rebalance_AfterPartialHit_CopyOnRead()
    {
        // Trigger rebalance with partial overlap [1500, 2500] vs cached [1000, 2000]
        await _copyOnReadCache!.GetDataAsync(_partialHitRange, CancellationToken.None);

        // Explicitly measure rebalance cycle completion
        // This is the cost center we're measuring
        await _copyOnReadCache.WaitForIdleAsync(timeout: TimeSpan.FromSeconds(10));
    }

    [Benchmark]
    public async Task Rebalance_AfterFullMiss_Snapshot()
    {
        // Trigger rebalance with no overlap [5000, 6000] vs cached [1000, 2000]
        await _snapshotCache!.GetDataAsync(_fullMissRange, CancellationToken.None);

        // Explicitly measure rebalance cycle completion
        // Full cache replacement cost
        await _snapshotCache.WaitForIdleAsync(timeout: TimeSpan.FromSeconds(10));
    }

    [Benchmark]
    public async Task Rebalance_AfterFullMiss_CopyOnRead()
    {
        // Trigger rebalance with no overlap [5000, 6000] vs cached [1000, 2000]
        await _copyOnReadCache!.GetDataAsync(_fullMissRange, CancellationToken.None);

        // Explicitly measure rebalance cycle completion
        // Full cache replacement cost
        await _copyOnReadCache.WaitForIdleAsync(timeout: TimeSpan.FromSeconds(10));
    }
}