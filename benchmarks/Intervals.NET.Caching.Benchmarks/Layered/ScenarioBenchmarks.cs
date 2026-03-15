using BenchmarkDotNet.Attributes;
using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Domain.Extensions.Fixed;
using Intervals.NET.Caching.Benchmarks.Infrastructure;

namespace Intervals.NET.Caching.Benchmarks.Layered;

/// <summary>
/// Scenario Benchmarks for Layered Cache.
/// End-to-end scenario testing for each layered topology.
/// NOT microbenchmarks — measures complete workflows.
/// 
/// 6 methods: 3 topologies × 2 scenarios (ColdStart, SequentialLocality).
/// 
/// ColdStart: First request on empty cache + WaitForIdleAsync.
///   Measures complete initialization cost including layer propagation.
/// 
/// SequentialLocality: 10 sequential requests with small shift + WaitForIdleAsync after each.
///   Measures steady-state throughput with sequential access pattern exploiting prefetch.
/// 
/// Methodology:
/// - Fresh cache per iteration via [IterationSetup]
/// - WaitForIdleAsync INSIDE benchmark method (measuring complete workflow cost)
/// - Zero-latency SynchronousDataSource isolates cache mechanics
/// </summary>
[MemoryDiagnoser]
[MarkdownExporter]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
public class ScenarioBenchmarks
{
    private SynchronousDataSource _dataSource = null!;
    private IntegerFixedStepDomain _domain;
    private IRangeCache<int, int, IntegerFixedStepDomain>? _cache;

    private const int InitialStart = 10000;
    private const int SequentialRequestCount = 10;

    // Precomputed ranges
    private Range<int> _coldStartRange;
    private Range<int>[] _sequentialSequence = null!;

    /// <summary>
    /// Requested range span size — tests scaling behavior.
    /// </summary>
    [Params(100, 1_000)]
    public int RangeSpan { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _domain = new IntegerFixedStepDomain();
        _dataSource = new SynchronousDataSource(_domain);

        _coldStartRange = Factories.Range.Closed<int>(InitialStart, InitialStart + RangeSpan - 1);

        // Sequential locality: 10 requests shifted by 10% of RangeSpan each
        var shiftSize = Math.Max(1, RangeSpan / 10);
        _sequentialSequence = new Range<int>[SequentialRequestCount];
        for (var i = 0; i < SequentialRequestCount; i++)
        {
            var start = InitialStart + (i * shiftSize);
            _sequentialSequence[i] = Factories.Range.Closed<int>(start, start + RangeSpan - 1);
        }
    }

    #region ColdStart — SwcSwc

    [IterationSetup(Target = nameof(ColdStart_SwcSwc))]
    public void IterationSetup_ColdStart_SwcSwc()
    {
        _cache = LayeredCacheHelpers.BuildSwcSwc(_dataSource, _domain);
    }

    /// <summary>
    /// Cold start on SwcSwc topology: first request on empty cache + WaitForIdleAsync.
    /// Measures complete initialization including layer propagation and rebalance.
    /// </summary>
    [Benchmark(Baseline = true)]
    [BenchmarkCategory("ColdStart")]
    public async Task ColdStart_SwcSwc()
    {
        await _cache!.GetDataAsync(_coldStartRange, CancellationToken.None);
        await _cache.WaitForIdleAsync();
    }

    #endregion

    #region ColdStart — VpcSwc

    [IterationSetup(Target = nameof(ColdStart_VpcSwc))]
    public void IterationSetup_ColdStart_VpcSwc()
    {
        _cache = LayeredCacheHelpers.BuildVpcSwc(_dataSource, _domain);
    }

    /// <summary>
    /// Cold start on VpcSwc topology: first request on empty cache + WaitForIdleAsync.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("ColdStart")]
    public async Task ColdStart_VpcSwc()
    {
        await _cache!.GetDataAsync(_coldStartRange, CancellationToken.None);
        await _cache.WaitForIdleAsync();
    }

    #endregion

    #region ColdStart — VpcSwcSwc

    [IterationSetup(Target = nameof(ColdStart_VpcSwcSwc))]
    public void IterationSetup_ColdStart_VpcSwcSwc()
    {
        _cache = LayeredCacheHelpers.BuildVpcSwcSwc(_dataSource, _domain);
    }

    /// <summary>
    /// Cold start on VpcSwcSwc topology: first request on empty cache + WaitForIdleAsync.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("ColdStart")]
    public async Task ColdStart_VpcSwcSwc()
    {
        await _cache!.GetDataAsync(_coldStartRange, CancellationToken.None);
        await _cache.WaitForIdleAsync();
    }

    #endregion

    #region SequentialLocality — SwcSwc

    [IterationSetup(Target = nameof(SequentialLocality_SwcSwc))]
    public void IterationSetup_SequentialLocality_SwcSwc()
    {
        _cache = LayeredCacheHelpers.BuildSwcSwc(_dataSource, _domain);
    }

    /// <summary>
    /// Sequential locality on SwcSwc topology: 10 sequential requests with small shift.
    /// Exploits SWC prefetch — later requests should hit cached prefetched data.
    /// </summary>
    [Benchmark(Baseline = true)]
    [BenchmarkCategory("SequentialLocality")]
    public async Task SequentialLocality_SwcSwc()
    {
        foreach (var range in _sequentialSequence)
        {
            await _cache!.GetDataAsync(range, CancellationToken.None);
            await _cache.WaitForIdleAsync();
        }
    }

    #endregion

    #region SequentialLocality — VpcSwc

    [IterationSetup(Target = nameof(SequentialLocality_VpcSwc))]
    public void IterationSetup_SequentialLocality_VpcSwc()
    {
        _cache = LayeredCacheHelpers.BuildVpcSwc(_dataSource, _domain);
    }

    /// <summary>
    /// Sequential locality on VpcSwc topology: 10 sequential requests with small shift.
    /// VPC inner stores visited segments; SWC outer provides sliding window view.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("SequentialLocality")]
    public async Task SequentialLocality_VpcSwc()
    {
        foreach (var range in _sequentialSequence)
        {
            await _cache!.GetDataAsync(range, CancellationToken.None);
            await _cache.WaitForIdleAsync();
        }
    }

    #endregion

    #region SequentialLocality — VpcSwcSwc

    [IterationSetup(Target = nameof(SequentialLocality_VpcSwcSwc))]
    public void IterationSetup_SequentialLocality_VpcSwcSwc()
    {
        _cache = LayeredCacheHelpers.BuildVpcSwcSwc(_dataSource, _domain);
    }

    /// <summary>
    /// Sequential locality on VpcSwcSwc topology: 10 sequential requests with small shift.
    /// Three-layer deep stack — measures overhead of additional layer propagation.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("SequentialLocality")]
    public async Task SequentialLocality_VpcSwcSwc()
    {
        foreach (var range in _sequentialSequence)
        {
            await _cache!.GetDataAsync(range, CancellationToken.None);
            await _cache.WaitForIdleAsync();
        }
    }

    #endregion
}
