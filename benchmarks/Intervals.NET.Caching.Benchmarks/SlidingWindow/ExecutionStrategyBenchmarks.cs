using BenchmarkDotNet.Attributes;
using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Domain.Extensions.Fixed;
using Intervals.NET.Caching.Benchmarks.Infrastructure;
using Intervals.NET.Caching.SlidingWindow.Public.Cache;
using Intervals.NET.Caching.SlidingWindow.Public.Configuration;

namespace Intervals.NET.Caching.Benchmarks.SlidingWindow;

/// <summary>
/// Execution Strategy Benchmarks
/// Comparative benchmarking suite focused on unbounded vs bounded execution queue performance
/// under rapid user request bursts with cache-hit pattern.
/// 
/// BENCHMARK PHILOSOPHY:
/// This suite compares execution queue configurations across two orthogonal dimensions:
/// - Data Source Latency (0ms/50ms/100ms) - realistic I/O simulation for rebalance operations
/// - Burst Size (10/100/1000) - sequential request load creating intent accumulation
/// 
/// BASELINE RATIO CALCULATIONS:
/// BenchmarkDotNet automatically calculates performance ratios using NoCapacity as the baseline.
/// </summary>
[MemoryDiagnoser]
[MarkdownExporter]
public class ExecutionStrategyBenchmarks
{
    // Benchmark Parameters - 2 Orthogonal Axes (Execution strategy is now split into separate benchmark methods)

    /// <summary>
    /// Data source latency in milliseconds (simulates network/IO delay)
    /// </summary>
    [Params(0, 50, 100)]
    public int DataSourceLatencyMs { get; set; }

    /// <summary>
    /// Number of requests submitted in rapid succession (burst load).
    /// Determines intent accumulation pressure and required right cache size.
    /// </summary>
    [Params(10, 100, 1000)]
    public int BurstSize { get; set; }

    // Configuration Constants

    /// <summary>
    /// Base span size for requested ranges - fixed to isolate strategy effects.
    /// </summary>
    private const int BaseSpanSize = 100;

    /// <summary>
    /// Initial range start position for first request and cold start prepopulation.
    /// </summary>
    private const int InitialStart = 10000;

    /// <summary>
    /// Channel capacity for bounded strategy (ignored for Task strategy).
    /// </summary>
    private const int ChannelCapacity = 10;

    // Infrastructure

    private SlidingWindowCache<int, int, IntegerFixedStepDomain>? _cache;
    private IDataSource<int, int> _dataSource = null!;
    private IntegerFixedStepDomain _domain;

    // Deterministic Workload Storage

    /// <summary>
    /// Precomputed request sequence for current iteration.
    /// </summary>
    private Range<int>[] _requestSequence = null!;

    /// <summary>
    /// Calculates the right cache coefficient needed to guarantee cache hits for all burst requests.
    /// </summary>
    private static int CalculateRightCacheCoefficient(int burstSize, int baseSpanSize)
    {
        var coefficient = (int)Math.Ceiling((double)burstSize / baseSpanSize);
        return coefficient + 1;
    }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _domain = new IntegerFixedStepDomain();

        // Create data source with configured latency
        _dataSource = DataSourceLatencyMs == 0
            ? new SynchronousDataSource(_domain)
            : new SlowDataSource(_domain, TimeSpan.FromMilliseconds(DataSourceLatencyMs));
    }

    /// <summary>
    /// Setup for NoCapacity (unbounded) benchmark method.
    /// </summary>
    [IterationSetup(Target = nameof(BurstPattern_NoCapacity))]
    public void IterationSetup_NoCapacity()
    {
        SetupCache(rebalanceQueueCapacity: null);
    }

    /// <summary>
    /// Setup for WithCapacity (bounded) benchmark method.
    /// </summary>
    [IterationSetup(Target = nameof(BurstPattern_WithCapacity))]
    public void IterationSetup_WithCapacity()
    {
        SetupCache(rebalanceQueueCapacity: ChannelCapacity);
    }

    /// <summary>
    /// Shared cache setup logic for both benchmark methods.
    /// </summary>
    private void SetupCache(int? rebalanceQueueCapacity)
    {
        var rightCoefficient = CalculateRightCacheCoefficient(BurstSize, BaseSpanSize);
        var leftCoefficient = 1;

        var options = new SlidingWindowCacheOptions(
            leftCacheSize: leftCoefficient,
            rightCacheSize: rightCoefficient,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 1.0,
            rightThreshold: 0.0,
            debounceDelay: TimeSpan.Zero,
            rebalanceQueueCapacity: rebalanceQueueCapacity
        );

        _cache = new SlidingWindowCache<int, int, IntegerFixedStepDomain>(
            _dataSource,
            _domain,
            options
        );

        var initialRange = Factories.Range.Closed<int>(
            InitialStart,
            InitialStart + BaseSpanSize - 1
        );

        var coldStartEnd = InitialStart + BaseSpanSize - 1 + BurstSize;
        var coldStartRange = Factories.Range.Closed<int>(InitialStart, coldStartEnd);

        _cache.GetDataAsync(coldStartRange, CancellationToken.None).GetAwaiter().GetResult();
        _cache.WaitForIdleAsync().GetAwaiter().GetResult();

        _requestSequence = BuildRequestSequence(initialRange);
    }

    /// <summary>
    /// Builds a deterministic request sequence with fixed span, shifting by +1 each time.
    /// </summary>
    private Range<int>[] BuildRequestSequence(Range<int> initialRange)
    {
        var sequence = new Range<int>[BurstSize];

        for (var i = 0; i < BurstSize; i++)
        {
            sequence[i] = initialRange.Shift(_domain, i + 1);
        }

        return sequence;
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _cache?.WaitForIdleAsync().GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _cache?.DisposeAsync().GetAwaiter().GetResult();

        if (_dataSource is IAsyncDisposable asyncDisposable)
        {
            asyncDisposable.DisposeAsync().GetAwaiter().GetResult();
        }
        else if (_dataSource is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    /// <summary>
    /// Measures unbounded execution (NoCapacity) performance with burst request pattern.
    /// This method serves as the baseline for ratio calculations.
    /// </summary>
    [Benchmark(Baseline = true)]
    public async Task BurstPattern_NoCapacity()
    {
        for (var i = 0; i < BurstSize; i++)
        {
            var range = _requestSequence[i];
            _ = await _cache!.GetDataAsync(range, CancellationToken.None);
        }

        await _cache!.WaitForIdleAsync();
    }

    /// <summary>
    /// Measures bounded execution (WithCapacity) performance with burst request pattern.
    /// Performance is compared against the NoCapacity baseline.
    /// </summary>
    [Benchmark]
    public async Task BurstPattern_WithCapacity()
    {
        for (var i = 0; i < BurstSize; i++)
        {
            var range = _requestSequence[i];
            _ = await _cache!.GetDataAsync(range, CancellationToken.None);
        }

        await _cache!.WaitForIdleAsync();
    }
}
