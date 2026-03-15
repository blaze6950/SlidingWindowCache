using BenchmarkDotNet.Attributes;
using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Caching.Benchmarks.Infrastructure;

namespace Intervals.NET.Caching.Benchmarks.Layered;

/// <summary>
/// Construction Benchmarks for Layered Cache.
/// Measures pure construction cost for each layered topology.
/// 
/// Three topologies:
/// - SwcSwc: SWC inner + SWC outer (homogeneous sliding window stack)
/// - VpcSwc: VPC inner + SWC outer (random-access backed by sequential-access)
/// - VpcSwcSwc: VPC inner + SWC middle + SWC outer (three-layer deep stack)
/// 
/// Methodology:
/// - No state reuse: each invocation constructs a fresh cache
/// - Zero-latency SynchronousDataSource
/// - No cache priming — measures pure construction cost
/// - MemoryDiagnoser tracks allocation overhead of construction path
/// - BuildAsync().GetAwaiter().GetResult() is safe (completes synchronously on success path)
/// </summary>
[MemoryDiagnoser]
[MarkdownExporter]
public class ConstructionBenchmarks
{
    private SynchronousDataSource _dataSource = null!;
    private IntegerFixedStepDomain _domain;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _domain = new IntegerFixedStepDomain();
        _dataSource = new SynchronousDataSource(_domain);
    }

    /// <summary>
    /// Measures construction cost for SWC + SWC layered topology.
    /// Two sliding window layers with default symmetric prefetch.
    /// </summary>
    [Benchmark]
    public IRangeCache<int, int, IntegerFixedStepDomain> Construction_SwcSwc()
    {
        return LayeredCacheHelpers.BuildSwcSwc(_dataSource, _domain);
    }

    /// <summary>
    /// Measures construction cost for VPC + SWC layered topology.
    /// VPC inner (Snapshot storage, LRU eviction, MaxSegmentCount=1000) + SWC outer.
    /// </summary>
    [Benchmark]
    public IRangeCache<int, int, IntegerFixedStepDomain> Construction_VpcSwc()
    {
        return LayeredCacheHelpers.BuildVpcSwc(_dataSource, _domain);
    }

    /// <summary>
    /// Measures construction cost for VPC + SWC + SWC layered topology.
    /// Three-layer deep stack: VPC innermost + two SWC layers on top.
    /// </summary>
    [Benchmark]
    public IRangeCache<int, int, IntegerFixedStepDomain> Construction_VpcSwcSwc()
    {
        return LayeredCacheHelpers.BuildVpcSwcSwc(_dataSource, _domain);
    }
}
