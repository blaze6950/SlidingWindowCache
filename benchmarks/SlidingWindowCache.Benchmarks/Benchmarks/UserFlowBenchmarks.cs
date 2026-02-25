using BenchmarkDotNet.Attributes;
using Intervals.NET;
using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Domain.Extensions.Fixed;
using SlidingWindowCache.Benchmarks.Infrastructure;
using SlidingWindowCache.Public;
using SlidingWindowCache.Public.Configuration;

namespace SlidingWindowCache.Benchmarks.Benchmarks;

/// <summary>
/// User Request Flow Benchmarks
/// Measures ONLY user-facing request latency/cost.
/// Rebalance/background activity is EXCLUDED from measurements via cleanup phase.
/// 
/// EXECUTION FLOW: User Request > Measures direct API call cost
/// 
/// Methodology:
/// - Fresh cache per iteration
/// - Benchmark methods measure ONLY GetDataAsync cost
/// - Rebalance triggered by mutations, but NOT included in measurement
/// - WaitForIdleAsync moved to [IterationCleanup]
/// - Deterministic overlap patterns (no randomness)
/// </summary>
[MemoryDiagnoser]
[MarkdownExporter]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
public class UserFlowBenchmarks
{
    private WindowCache<int, int, IntegerFixedStepDomain>? _snapshotCache;
    private WindowCache<int, int, IntegerFixedStepDomain>? _copyOnReadCache;
    private SynchronousDataSource _dataSource = null!;
    private IntegerFixedStepDomain _domain;

    /// <summary>
    /// Requested range size - varies from small (100) to large (10,000) to test scaling behavior.
    /// </summary>
    [Params(100, 1_000, 10_000)]
    public int RangeSpan { get; set; }

    /// <summary>
    /// Cache coefficient size for left/right prefetch - varies from minimal (1) to aggressive (100).
    /// Combined with RangeSpan, determines total materialized cache size.
    /// </summary>
    [Params(1, 10, 100)]
    public int CacheCoefficientSize { get; set; }

    // Range will be calculated based on RangeSpan parameter
    private int CachedStart => 10000;
    private int CachedEnd => CachedStart + RangeSpan;

    private Range<int> InitialCacheRange =>
        Intervals.NET.Factories.Range.Closed<int>(CachedStart, CachedEnd);

    private Range<int> InitialCacheRangeAfterRebalance => InitialCacheRange
        .ExpandByRatio(_domain, CacheCoefficientSize, CacheCoefficientSize);

    private Range<int> FullHitRange => InitialCacheRangeAfterRebalance
        .ExpandByRatio(_domain, -0.2, -0.2); // 20% inside cached window

    private Range<int> FullMissRange => InitialCacheRangeAfterRebalance
        .Shift(_domain, InitialCacheRangeAfterRebalance.Span(_domain).Value * 3); // Shift far outside cached window

    private Range<int> PartialHitForwardRange => InitialCacheRangeAfterRebalance
        .Shift(_domain, InitialCacheRangeAfterRebalance.Span(_domain).Value / 2); // Shift forward by 50% of cached span

    private Range<int> PartialHitBackwardRange => InitialCacheRangeAfterRebalance
        .Shift(_domain, -InitialCacheRangeAfterRebalance.Span(_domain).Value / 2); // Shift backward by 50% of cached

    // Pre-calculated ranges
    private Range<int> _fullHitRange;
    private Range<int> _partialHitForwardRange;
    private Range<int> _partialHitBackwardRange;
    private Range<int> _fullMissRange;

    private WindowCacheOptions? _snapshotOptions;
    private WindowCacheOptions? _copyOnReadOptions;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _domain = new IntegerFixedStepDomain();
        _dataSource = new SynchronousDataSource(_domain);

        // Pre-calculate all deterministic ranges
        // Full hit: request entirely within cached window
        _fullHitRange = FullHitRange;

        // Partial hit forward
        _partialHitForwardRange = PartialHitForwardRange;

        // Partial hit backward
        _partialHitBackwardRange = PartialHitBackwardRange;

        // Full miss: no overlap with cached window
        _fullMissRange = FullMissRange;

        // Configure cache options
        _snapshotOptions = new WindowCacheOptions(
            leftCacheSize: CacheCoefficientSize,
            rightCacheSize: CacheCoefficientSize,
            UserCacheReadMode.Snapshot,
            leftThreshold: 0,
            rightThreshold: 0
        );

        _copyOnReadOptions = new WindowCacheOptions(
            leftCacheSize: CacheCoefficientSize,
            rightCacheSize: CacheCoefficientSize,
            UserCacheReadMode.CopyOnRead,
            leftThreshold: 0,
            rightThreshold: 0
        );
    }

    [IterationSetup]
    public void IterationSetup()
    {
        // Create fresh caches for each iteration - no state drift
        _snapshotCache = new WindowCache<int, int, IntegerFixedStepDomain>(
            _dataSource,
            _domain,
            _snapshotOptions!
        );

        _copyOnReadCache = new WindowCache<int, int, IntegerFixedStepDomain>(
            _dataSource,
            _domain,
            _copyOnReadOptions!
        );

        // Prime both caches with known initial window
        var initialRange = Intervals.NET.Factories.Range.Closed<int>(CachedStart, CachedEnd);
        _snapshotCache.GetDataAsync(initialRange, CancellationToken.None).GetAwaiter().GetResult();
        _copyOnReadCache.GetDataAsync(initialRange, CancellationToken.None).GetAwaiter().GetResult();

        // Wait for idle state - deterministic starting point
        _snapshotCache.WaitForIdleAsync().GetAwaiter().GetResult();
        _copyOnReadCache.WaitForIdleAsync().GetAwaiter().GetResult();
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        // Wait for any triggered rebalance to complete
        // This ensures measurements are NOT contaminated by background activity
        _snapshotCache?.WaitForIdleAsync().GetAwaiter().GetResult();
        _copyOnReadCache?.WaitForIdleAsync().GetAwaiter().GetResult();
    }

    #region Full Hit Benchmarks

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("FullHit")]
    public async Task<ReadOnlyMemory<int>> User_FullHit_Snapshot()
    {
        // No rebalance triggered
        return (await _snapshotCache!.GetDataAsync(_fullHitRange, CancellationToken.None)).Data;
    }

    [Benchmark]
    [BenchmarkCategory("FullHit")]
    public async Task<ReadOnlyMemory<int>> User_FullHit_CopyOnRead()
    {
        // No rebalance triggered
        return (await _copyOnReadCache!.GetDataAsync(_fullHitRange, CancellationToken.None)).Data;
    }

    #endregion

    #region Partial Hit Benchmarks

    [Benchmark]
    [BenchmarkCategory("PartialHit")]
    public async Task<ReadOnlyMemory<int>> User_PartialHit_ForwardShift_Snapshot()
    {
        // Rebalance triggered, handled in cleanup
        return (await _snapshotCache!.GetDataAsync(_partialHitForwardRange, CancellationToken.None)).Data;
    }

    [Benchmark]
    [BenchmarkCategory("PartialHit")]
    public async Task<ReadOnlyMemory<int>> User_PartialHit_ForwardShift_CopyOnRead()
    {
        // Rebalance triggered, handled in cleanup
        return (await _copyOnReadCache!.GetDataAsync(_partialHitForwardRange, CancellationToken.None)).Data;
    }

    [Benchmark]
    [BenchmarkCategory("PartialHit")]
    public async Task<ReadOnlyMemory<int>> User_PartialHit_BackwardShift_Snapshot()
    {
        // Rebalance triggered, handled in cleanup
        return (await _snapshotCache!.GetDataAsync(_partialHitBackwardRange, CancellationToken.None)).Data;
    }

    [Benchmark]
    [BenchmarkCategory("PartialHit")]
    public async Task<ReadOnlyMemory<int>> User_PartialHit_BackwardShift_CopyOnRead()
    {
        // Rebalance triggered, handled in cleanup
        return (await _copyOnReadCache!.GetDataAsync(_partialHitBackwardRange, CancellationToken.None)).Data;
    }

    #endregion

    #region Full Miss Benchmarks

    [Benchmark]
    [BenchmarkCategory("FullMiss")]
    public async Task<ReadOnlyMemory<int>> User_FullMiss_Snapshot()
    {
        // No overlap - full cache replacement
        // Rebalance triggered, handled in cleanup
        return (await _snapshotCache!.GetDataAsync(_fullMissRange, CancellationToken.None)).Data;
    }

    [Benchmark]
    [BenchmarkCategory("FullMiss")]
    public async Task<ReadOnlyMemory<int>> User_FullMiss_CopyOnRead()
    {
        // No overlap - full cache replacement
        // Rebalance triggered, handled in cleanup
        return (await _copyOnReadCache!.GetDataAsync(_fullMissRange, CancellationToken.None)).Data;
    }

    #endregion
}
