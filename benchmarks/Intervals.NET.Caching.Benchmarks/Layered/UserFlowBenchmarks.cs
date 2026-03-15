using BenchmarkDotNet.Attributes;
using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Domain.Extensions.Fixed;
using Intervals.NET.Caching.Benchmarks.Infrastructure;

namespace Intervals.NET.Caching.Benchmarks.Layered;

/// <summary>
/// User Flow Benchmarks for Layered Cache.
/// Measures user-facing request latency across three topologies and three interaction scenarios.
/// 
/// 9 methods: 3 topologies (SwcSwc, VpcSwc, VpcSwcSwc) × 3 scenarios (FullHit, PartialHit, FullMiss).
/// 
/// Methodology:
/// - Learning pass in GlobalSetup: one throwaway cache per topology exercises all benchmark
///   code paths so the data source can be frozen before measurement begins.
/// - Fresh cache per iteration via [IterationSetup]
/// - Cache primed with initial range + WaitForIdleAsync to establish deterministic state
/// - Benchmark methods measure ONLY GetDataAsync cost
/// - WaitForIdleAsync in [IterationCleanup] to drain background activity
/// - Zero-latency FrozenDataSource isolates cache mechanics
/// </summary>
[MemoryDiagnoser]
[MarkdownExporter]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
public class UserFlowBenchmarks
{
    private FrozenDataSource _frozenDataSource = null!;
    private IntegerFixedStepDomain _domain;
    private IRangeCache<int, int, IntegerFixedStepDomain>? _cache;

    private const int InitialStart = 10000;

    // Precomputed ranges (set in GlobalSetup based on RangeSpan)
    private Range<int> _initialRange;
    private Range<int> _fullHitRange;
    private Range<int> _partialHitRange;
    private Range<int> _fullMissRange;

    /// <summary>
    /// Requested range span size — tests scaling behavior.
    /// </summary>
    [Params(100, 1_000, 10_000)]
    public int RangeSpan { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _domain = new IntegerFixedStepDomain();

        // Initial range used to prime the cache
        _initialRange = Factories.Range.Closed<int>(InitialStart, InitialStart + RangeSpan - 1);

        // SWC layers use leftCacheSize=2.0, rightCacheSize=2.0
        // After rebalance, cached range ≈ [InitialStart - 2*RangeSpan, InitialStart + 3*RangeSpan]
        // FullHit: well within the cached window
        _fullHitRange = Factories.Range.Closed<int>(
            InitialStart + RangeSpan / 4,
            InitialStart + RangeSpan / 4 + RangeSpan - 1);

        // PartialHit: overlaps ~50% of cached range by shifting forward
        var cachedEnd = InitialStart + 3 * RangeSpan;
        _partialHitRange = Factories.Range.Closed<int>(
            cachedEnd - RangeSpan / 2,
            cachedEnd - RangeSpan / 2 + RangeSpan - 1);

        // FullMiss: far beyond cached range
        _fullMissRange = Factories.Range.Closed<int>(
            InitialStart + 100 * RangeSpan,
            InitialStart + 100 * RangeSpan + RangeSpan - 1);

        // Learning pass: one throwaway cache per topology exercises all benchmark code paths
        // so every range the data source will be asked for during measurement is pre-learned.
        var learningSource = new SynchronousDataSource(_domain);

        foreach (var topology in new[] { LayeredTopology.SwcSwc, LayeredTopology.VpcSwc, LayeredTopology.VpcSwcSwc })
        {
            var throwaway = LayeredCacheHelpers.Build(topology, learningSource, _domain);
            throwaway.GetDataAsync(_initialRange, CancellationToken.None).GetAwaiter().GetResult();
            throwaway.WaitForIdleAsync().GetAwaiter().GetResult();
            throwaway.GetDataAsync(_fullHitRange, CancellationToken.None).GetAwaiter().GetResult();
            throwaway.WaitForIdleAsync().GetAwaiter().GetResult();
            throwaway.GetDataAsync(_partialHitRange, CancellationToken.None).GetAwaiter().GetResult();
            throwaway.WaitForIdleAsync().GetAwaiter().GetResult();
            throwaway.GetDataAsync(_fullMissRange, CancellationToken.None).GetAwaiter().GetResult();
            throwaway.WaitForIdleAsync().GetAwaiter().GetResult();
        }

        _frozenDataSource = learningSource.Freeze();
    }

    #region SwcSwc

    [IterationSetup(Target = nameof(FullHit_SwcSwc) + "," + nameof(PartialHit_SwcSwc) + "," + nameof(FullMiss_SwcSwc))]
    public void IterationSetup_SwcSwc()
    {
        _cache = LayeredCacheHelpers.BuildSwcSwc(_frozenDataSource, _domain);
        _cache.GetDataAsync(_initialRange, CancellationToken.None).GetAwaiter().GetResult();
        _cache.WaitForIdleAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Full cache hit on SwcSwc topology — request entirely within cached window.
    /// </summary>
    [Benchmark(Baseline = true)]
    [BenchmarkCategory("FullHit")]
    public async Task<ReadOnlyMemory<int>> FullHit_SwcSwc()
    {
        return (await _cache!.GetDataAsync(_fullHitRange, CancellationToken.None)).Data;
    }

    /// <summary>
    /// Partial hit on SwcSwc topology — request overlaps ~50% of cached window.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("PartialHit")]
    public async Task<ReadOnlyMemory<int>> PartialHit_SwcSwc()
    {
        return (await _cache!.GetDataAsync(_partialHitRange, CancellationToken.None)).Data;
    }

    /// <summary>
    /// Full miss on SwcSwc topology — request far beyond cached window.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("FullMiss")]
    public async Task<ReadOnlyMemory<int>> FullMiss_SwcSwc()
    {
        return (await _cache!.GetDataAsync(_fullMissRange, CancellationToken.None)).Data;
    }

    #endregion

    #region VpcSwc

    [IterationSetup(Target = nameof(FullHit_VpcSwc) + "," + nameof(PartialHit_VpcSwc) + "," + nameof(FullMiss_VpcSwc))]
    public void IterationSetup_VpcSwc()
    {
        _cache = LayeredCacheHelpers.BuildVpcSwc(_frozenDataSource, _domain);
        _cache.GetDataAsync(_initialRange, CancellationToken.None).GetAwaiter().GetResult();
        _cache.WaitForIdleAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Full cache hit on VpcSwc topology — request entirely within cached window.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("FullHit")]
    public async Task<ReadOnlyMemory<int>> FullHit_VpcSwc()
    {
        return (await _cache!.GetDataAsync(_fullHitRange, CancellationToken.None)).Data;
    }

    /// <summary>
    /// Partial hit on VpcSwc topology — request overlaps ~50% of cached window.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("PartialHit")]
    public async Task<ReadOnlyMemory<int>> PartialHit_VpcSwc()
    {
        return (await _cache!.GetDataAsync(_partialHitRange, CancellationToken.None)).Data;
    }

    /// <summary>
    /// Full miss on VpcSwc topology — request far beyond cached window.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("FullMiss")]
    public async Task<ReadOnlyMemory<int>> FullMiss_VpcSwc()
    {
        return (await _cache!.GetDataAsync(_fullMissRange, CancellationToken.None)).Data;
    }

    #endregion

    #region VpcSwcSwc

    [IterationSetup(Target = nameof(FullHit_VpcSwcSwc) + "," + nameof(PartialHit_VpcSwcSwc) + "," + nameof(FullMiss_VpcSwcSwc))]
    public void IterationSetup_VpcSwcSwc()
    {
        _cache = LayeredCacheHelpers.BuildVpcSwcSwc(_frozenDataSource, _domain);
        _cache.GetDataAsync(_initialRange, CancellationToken.None).GetAwaiter().GetResult();
        _cache.WaitForIdleAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Full cache hit on VpcSwcSwc topology — request entirely within cached window.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("FullHit")]
    public async Task<ReadOnlyMemory<int>> FullHit_VpcSwcSwc()
    {
        return (await _cache!.GetDataAsync(_fullHitRange, CancellationToken.None)).Data;
    }

    /// <summary>
    /// Partial hit on VpcSwcSwc topology — request overlaps ~50% of cached window.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("PartialHit")]
    public async Task<ReadOnlyMemory<int>> PartialHit_VpcSwcSwc()
    {
        return (await _cache!.GetDataAsync(_partialHitRange, CancellationToken.None)).Data;
    }

    /// <summary>
    /// Full miss on VpcSwcSwc topology — request far beyond cached window.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("FullMiss")]
    public async Task<ReadOnlyMemory<int>> FullMiss_VpcSwcSwc()
    {
        return (await _cache!.GetDataAsync(_fullMissRange, CancellationToken.None)).Data;
    }

    #endregion

    [IterationCleanup]
    public void IterationCleanup()
    {
        // Drain any triggered background activity before next iteration
        _cache?.WaitForIdleAsync().GetAwaiter().GetResult();
    }
}
