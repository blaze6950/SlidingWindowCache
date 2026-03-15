using BenchmarkDotNet.Attributes;
using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Domain.Extensions.Fixed;
using Intervals.NET.Caching.Benchmarks.Infrastructure;

namespace Intervals.NET.Caching.Benchmarks.Layered;

/// <summary>
/// Rebalance Benchmarks for Layered Cache.
/// Measures rebalance/maintenance cost for each layered topology under sequential shift patterns.
/// 
/// 3 methods: one per topology (SwcSwc, VpcSwc, VpcSwcSwc).
/// Same pattern as SWC RebalanceFlowBenchmarks: 10 sequential requests with shift,
/// each followed by WaitForIdleAsync.
/// 
/// Methodology:
/// - Fresh cache per iteration via [IterationSetup]
/// - Cache primed with initial range + WaitForIdleAsync
/// - Deterministic request sequence: 10 requests, each shifted by +1
/// - WaitForIdleAsync INSIDE benchmark method (measuring rebalance completion)
/// - Zero-latency SynchronousDataSource isolates cache mechanics
/// </summary>
[MemoryDiagnoser]
[MarkdownExporter]
public class RebalanceBenchmarks
{
    private SynchronousDataSource _dataSource = null!;
    private IntegerFixedStepDomain _domain;
    private IRangeCache<int, int, IntegerFixedStepDomain>? _cache;

    private const int InitialStart = 10000;
    private const int RequestsPerInvocation = 10;

    // Precomputed request sequence
    private Range<int>[] _requestSequence = null!;

    /// <summary>
    /// Base span size for requested ranges — tests scaling behavior.
    /// </summary>
    [Params(100, 1_000)]
    public int BaseSpanSize { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _domain = new IntegerFixedStepDomain();
        _dataSource = new SynchronousDataSource(_domain);
    }

    /// <summary>
    /// Builds a deterministic request sequence: 10 fixed-span ranges shifted by +1 each.
    /// </summary>
    private Range<int>[] BuildRequestSequence(Range<int> initialRange)
    {
        var sequence = new Range<int>[RequestsPerInvocation];
        for (var i = 0; i < RequestsPerInvocation; i++)
        {
            sequence[i] = initialRange.Shift(_domain, i + 1);
        }

        return sequence;
    }

    /// <summary>
    /// Common setup: build topology, prime cache, precompute request sequence.
    /// </summary>
    private void SetupTopology(LayeredTopology topology)
    {
        _cache = LayeredCacheHelpers.Build(topology, _dataSource, _domain);

        var initialRange = Factories.Range.Closed<int>(InitialStart, InitialStart + BaseSpanSize - 1);
        _cache.GetDataAsync(initialRange, CancellationToken.None).GetAwaiter().GetResult();
        _cache.WaitForIdleAsync().GetAwaiter().GetResult();

        _requestSequence = BuildRequestSequence(initialRange);
    }

    #region SwcSwc

    [IterationSetup(Target = nameof(Rebalance_SwcSwc))]
    public void IterationSetup_SwcSwc()
    {
        SetupTopology(LayeredTopology.SwcSwc);
    }

    /// <summary>
    /// Measures rebalance cost for SwcSwc topology.
    /// 10 sequential requests with shift, each followed by rebalance completion.
    /// </summary>
    [Benchmark]
    public async Task Rebalance_SwcSwc()
    {
        foreach (var requestRange in _requestSequence)
        {
            await _cache!.GetDataAsync(requestRange, CancellationToken.None);
            await _cache.WaitForIdleAsync();
        }
    }

    #endregion

    #region VpcSwc

    [IterationSetup(Target = nameof(Rebalance_VpcSwc))]
    public void IterationSetup_VpcSwc()
    {
        SetupTopology(LayeredTopology.VpcSwc);
    }

    /// <summary>
    /// Measures rebalance cost for VpcSwc topology.
    /// 10 sequential requests with shift, each followed by rebalance completion.
    /// </summary>
    [Benchmark]
    public async Task Rebalance_VpcSwc()
    {
        foreach (var requestRange in _requestSequence)
        {
            await _cache!.GetDataAsync(requestRange, CancellationToken.None);
            await _cache.WaitForIdleAsync();
        }
    }

    #endregion

    #region VpcSwcSwc

    [IterationSetup(Target = nameof(Rebalance_VpcSwcSwc))]
    public void IterationSetup_VpcSwcSwc()
    {
        SetupTopology(LayeredTopology.VpcSwcSwc);
    }

    /// <summary>
    /// Measures rebalance cost for VpcSwcSwc topology.
    /// 10 sequential requests with shift, each followed by rebalance completion.
    /// </summary>
    [Benchmark]
    public async Task Rebalance_VpcSwcSwc()
    {
        foreach (var requestRange in _requestSequence)
        {
            await _cache!.GetDataAsync(requestRange, CancellationToken.None);
            await _cache.WaitForIdleAsync();
        }
    }

    #endregion
}
