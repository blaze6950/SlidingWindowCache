using BenchmarkDotNet.Attributes;
using Intervals.NET;
using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Domain.Extensions.Fixed;
using SlidingWindowCache.Benchmarks.Infrastructure;
using SlidingWindowCache.Public;
using SlidingWindowCache.Public.Configuration;

namespace SlidingWindowCache.Benchmarks.Benchmarks;

/// <summary>
/// Rebalance Flow Benchmarks
/// Behavior-driven benchmarking suite focused exclusively on rebalance mechanics and storage rematerialization cost.
/// 
/// BENCHMARK PHILOSOPHY:
/// This suite models system behavior through three orthogonal axes:
/// ✔ RequestedRange Span Behavior (Fixed/Growing/Shrinking) - models requested range span dynamics
/// ✔ Storage Strategy (Snapshot/CopyOnRead) - measures rematerialization tradeoffs
/// ✔ Base RequestedRange Span Size (100/1000/10000) - tests scaling behavior
/// 
/// PERFORMANCE MODEL:
/// Rebalance cost depends primarily on:
/// ✔ Span stability/volatility (behavior axis)
/// ✔ Buffer reuse feasibility (storage axis)
/// ✔ Capacity growth patterns (size axis)
/// 
/// NOT on:
/// ✖ Cache hit/miss classification (irrelevant for rebalance cost)
/// ✖ DataSource performance (isolated via SynchronousDataSource)
/// ✖ Decision logic (covered by tests, not benchmarked)
/// 
/// EXECUTION MODEL: Deterministic multi-request sequence → Measure cumulative rebalance cost
/// 
/// Methodology:
/// - Fresh cache per iteration
/// - Zero-latency SynchronousDataSource isolates cache mechanics
/// - Deterministic request sequence precomputed in IterationSetup (RequestsPerInvocation = 10)
/// - Each request guarantees rebalance via range shift and aggressive thresholds
/// - WaitForIdleAsync after EACH request (measuring rebalance completion)
/// - Benchmark method contains ZERO workload logic, ZERO branching, ZERO allocations
/// 
/// Workload Generation:
/// - ALL span calculations occur in BuildRequestSequence()
/// - ALL branching occurs in BuildRequestSequence()
/// - Benchmark method only iterates precomputed array and awaits results
/// 
/// EXPECTED BEHAVIOR:
/// - Fixed RequestedRange Span: CopyOnRead optimal (buffer reuse), Snapshot consistent (always allocates)
/// - Growing RequestedRange Span: CopyOnRead capacity growth penalty, Snapshot stable cost
/// - Shrinking RequestedRange Span: Both strategies handle well, CopyOnRead may over-allocate
/// </summary>
[MemoryDiagnoser]
[MarkdownExporter]
public class RebalanceFlowBenchmarks
{
    /// <summary>
    /// RequestedRange Span behavior model: Fixed (stable), Growing (increasing), Shrinking (decreasing)
    /// </summary>
    public enum SpanBehavior
    {
        Fixed,
        Growing,
        Shrinking
    }

    /// <summary>
    /// Storage strategy: Snapshot (array-based) vs CopyOnRead (list-based)
    /// </summary>
    public enum StorageStrategy
    {
        Snapshot,
        CopyOnRead
    }

    // Benchmark Parameters - 3 Orthogonal Axes

    /// <summary>
    /// RequestedRange Span behavior model determining how requested range span evolves across iterations
    /// </summary>
    [Params(SpanBehavior.Fixed, SpanBehavior.Growing, SpanBehavior.Shrinking)]
    public SpanBehavior Behavior { get; set; }

    /// <summary>
    /// Storage strategy for cache rematerialization
    /// </summary>
    [Params(StorageStrategy.Snapshot, StorageStrategy.CopyOnRead)]
    public StorageStrategy Strategy { get; set; }

    /// <summary>
    /// Base span size for requested ranges - tests scaling behavior from small to large data volumes
    /// </summary>
    [Params(100, 1_000, 10_000)]
    public int BaseSpanSize { get; set; }

    // Configuration Constants

    /// <summary>
    /// Cache coefficient for left/right prefetch - fixed to isolate span behavior effects
    /// </summary>
    private const int CacheCoefficientSize = 10;

    /// <summary>
    /// Growth factor per iteration for Growing RequestedRange span behavior
    /// </summary>
    private const int GrowthFactor = 100;

    /// <summary>
    /// Shrink factor per iteration for Shrinking RequestedRange span behavior
    /// </summary>
    private const int ShrinkFactor = 100;

    /// <summary>
    /// Initial range start position - arbitrary but consistent across all benchmarks
    /// </summary>
    private const int InitialStart = 10000;

    /// <summary>
    /// Number of requests executed per benchmark invocation - deterministic workload size
    /// </summary>
    private const int RequestsPerInvocation = 10;

    // Infrastructure

    private WindowCache<int, int, IntegerFixedStepDomain>? _cache;
    private SynchronousDataSource _dataSource = null!;
    private IntegerFixedStepDomain _domain;
    private WindowCacheOptions _options = null!;

    // Deterministic Workload Storage

    /// <summary>
    /// Precomputed request sequence for current iteration - generated in IterationSetup.
    /// Contains EXACTLY RequestsPerInvocation ranges with all span calculations completed.
    /// Benchmark methods iterate through this array without any workload logic.
    /// </summary>
    private Range<int>[] _requestSequence = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _domain = new IntegerFixedStepDomain();
        _dataSource = new SynchronousDataSource(_domain);

        // Configure cache with aggressive thresholds to guarantee rebalancing
        // leftThreshold=0, rightThreshold=0 means any request outside current window triggers rebalance
        var readMode = Strategy switch
        {
            StorageStrategy.Snapshot => UserCacheReadMode.Snapshot,
            StorageStrategy.CopyOnRead => UserCacheReadMode.CopyOnRead,
            _ => throw new ArgumentOutOfRangeException(nameof(Strategy))
        };

        _options = new WindowCacheOptions(
            leftCacheSize: CacheCoefficientSize,
            rightCacheSize: CacheCoefficientSize,
            readMode: readMode,
            leftThreshold: 1, // Set to 1 (100%) to ensure any request even the same range as previous triggers rebalance, isolating rebalance cost
            rightThreshold: 0,
            debounceDelay: TimeSpan.FromMilliseconds(10)
        );
    }

    [IterationSetup]
    public void IterationSetup()
    {
        // Create fresh cache for this iteration
        _cache = new WindowCache<int, int, IntegerFixedStepDomain>(
            _dataSource,
            _domain,
            _options
        );

        // Compute initial range for priming the cache
        var initialRange = Intervals.NET.Factories.Range.Closed<int>(InitialStart, InitialStart + BaseSpanSize - 1);

        // Prime cache with initial window
        _cache.GetDataAsync(initialRange, CancellationToken.None).GetAwaiter().GetResult();
        _cache.WaitForIdleAsync().GetAwaiter().GetResult();

        // Build deterministic request sequence with all workload logic
        _requestSequence = BuildRequestSequence(initialRange);
    }

    /// <summary>
    /// Builds a deterministic request sequence based on the configured span behavior.
    /// This method contains ALL workload generation logic, span calculations, and branching.
    /// The benchmark method will execute this precomputed sequence with zero overhead.
    /// </summary>
    /// <param name="initialRange">The initial primed range used to seed the sequence</param>
    /// <returns>Array of EXACTLY RequestsPerInvocation ranges, precomputed and ready to execute</returns>
    private Range<int>[] BuildRequestSequence(Range<int> initialRange)
    {
        var sequence = new Range<int>[RequestsPerInvocation];

        for (var i = 0; i < RequestsPerInvocation; i++)
        {
            Range<int> requestRange;

            switch (Behavior)
            {
                case SpanBehavior.Fixed:
                    // Fixed: Span remains constant, position shifts by +1 each request
                    requestRange = initialRange.Shift(_domain, i + 1);
                    break;

                case SpanBehavior.Growing:
                    // Growing: Span increases deterministically, position shifts slightly
                    var spanGrow = i * GrowthFactor;
                    requestRange = initialRange.Shift(_domain, i + 1).Expand(_domain, 0, spanGrow);
                    break;

                case SpanBehavior.Shrinking:
                    // Shrinking: Span decreases deterministically, respecting minimum
                    var spanShrink = i * ShrinkFactor;
                    var bigInitialRange = initialRange.Expand(_domain, 0, RequestsPerInvocation * ShrinkFactor); // Ensure we have room to shrink
                    requestRange = bigInitialRange.Shift(_domain, i + 1).Expand(_domain, 0, -spanShrink);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(Behavior), Behavior, "Unsupported span behavior");
            }

            sequence[i] = requestRange;
        }

        return sequence;
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        // Ensure cache is idle before next iteration
        _cache?.WaitForIdleAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Measures rebalance rematerialization cost for the configured span behavior and storage strategy.
    /// Executes a deterministic sequence of requests, each followed by rebalance completion.
    /// This benchmark measures ONLY the rebalance path - decision logic is excluded.
    /// Contains ZERO workload logic, ZERO branching, ZERO span calculations.
    /// </summary>
    [Benchmark]
    public async Task Rebalance()
    {
        // Execute precomputed request sequence
        // Each request triggers rebalance (guaranteed by leftThreshold=1 and range shift)
        // Measure complete rebalance cycle for each request
        foreach (var requestRange in _requestSequence)
        {
            await _cache!.GetDataAsync(requestRange, CancellationToken.None);

            // Explicitly measure rebalance cycle completion
            // This captures the rematerialization cost we're benchmarking
            await _cache.WaitForIdleAsync();
        }
    }
}
