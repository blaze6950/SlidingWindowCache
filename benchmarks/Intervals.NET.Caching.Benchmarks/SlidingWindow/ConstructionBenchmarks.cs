using BenchmarkDotNet.Attributes;
using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Caching.Benchmarks.Infrastructure;
using Intervals.NET.Caching.SlidingWindow.Public.Cache;
using Intervals.NET.Caching.SlidingWindow.Public.Configuration;

namespace Intervals.NET.Caching.Benchmarks.SlidingWindow;

/// <summary>
/// Construction Benchmarks for SlidingWindow Cache.
/// Measures two distinct costs:
/// (A) Builder pipeline cost — full fluent builder API overhead
/// (B) Raw constructor cost — pre-built options, direct instantiation
/// 
/// Each storage mode (Snapshot, CopyOnRead) is measured independently.
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
    private SlidingWindowCacheOptions _snapshotOptions = null!;
    private SlidingWindowCacheOptions _copyOnReadOptions = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _domain = new IntegerFixedStepDomain();
        _dataSource = new SynchronousDataSource(_domain);

        // Pre-build options for raw constructor benchmarks
        _snapshotOptions = new SlidingWindowCacheOptions(
            leftCacheSize: 2.0,
            rightCacheSize: 2.0,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0.2,
            rightThreshold: 0.2);

        _copyOnReadOptions = new SlidingWindowCacheOptions(
            leftCacheSize: 2.0,
            rightCacheSize: 2.0,
            readMode: UserCacheReadMode.CopyOnRead,
            leftThreshold: 0.2,
            rightThreshold: 0.2);
    }

    #region Builder Pipeline

    /// <summary>
    /// Measures full builder pipeline cost for Snapshot mode.
    /// Includes: builder allocation, options builder, options construction, cache construction.
    /// </summary>
    [Benchmark]
    public SlidingWindowCache<int, int, IntegerFixedStepDomain> Builder_Snapshot()
    {
        return (SlidingWindowCache<int, int, IntegerFixedStepDomain>)SlidingWindowCacheBuilder
            .For<int, int, IntegerFixedStepDomain>(_dataSource, _domain)
            .WithOptions(o => o
                .WithCacheSize(2.0)
                .WithReadMode(UserCacheReadMode.Snapshot)
                .WithThresholds(0.2))
            .Build();
    }

    /// <summary>
    /// Measures full builder pipeline cost for CopyOnRead mode.
    /// </summary>
    [Benchmark]
    public SlidingWindowCache<int, int, IntegerFixedStepDomain> Builder_CopyOnRead()
    {
        return (SlidingWindowCache<int, int, IntegerFixedStepDomain>)SlidingWindowCacheBuilder
            .For<int, int, IntegerFixedStepDomain>(_dataSource, _domain)
            .WithOptions(o => o
                .WithCacheSize(2.0)
                .WithReadMode(UserCacheReadMode.CopyOnRead)
                .WithThresholds(0.2))
            .Build();
    }

    #endregion

    #region Raw Constructor

    /// <summary>
    /// Measures raw constructor cost with pre-built options for Snapshot mode.
    /// Isolates constructor overhead from builder pipeline.
    /// </summary>
    [Benchmark]
    public SlidingWindowCache<int, int, IntegerFixedStepDomain> Constructor_Snapshot()
    {
        return new SlidingWindowCache<int, int, IntegerFixedStepDomain>(
            _dataSource, _domain, _snapshotOptions);
    }

    /// <summary>
    /// Measures raw constructor cost with pre-built options for CopyOnRead mode.
    /// </summary>
    [Benchmark]
    public SlidingWindowCache<int, int, IntegerFixedStepDomain> Constructor_CopyOnRead()
    {
        return new SlidingWindowCache<int, int, IntegerFixedStepDomain>(
            _dataSource, _domain, _copyOnReadOptions);
    }

    #endregion
}
