using BenchmarkDotNet.Attributes;
using Intervals.NET;
using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Domain.Extensions.Fixed;
using SlidingWindowCache.Benchmarks.Infrastructure;
using SlidingWindowCache.Public;
using SlidingWindowCache.Public.Configuration;

namespace SlidingWindowCache.Benchmarks.Benchmarks;

/// <summary>
/// Execution Strategy Benchmarks
/// Comparative benchmarking suite focused on unbounded vs bounded execution queue performance
/// under rapid user request bursts with cache-hit pattern.
/// 
/// BENCHMARK PHILOSOPHY:
/// This suite compares execution queue configurations across three orthogonal dimensions:
/// ✔ Execution Queue Capacity (Unbounded/Bounded) - core comparison axis via separate benchmark methods
/// ✔ Data Source Latency (0ms/50ms/100ms) - realistic I/O simulation for rebalance operations
/// ✔ Burst Size (10/100/1000) - sequential request load creating intent accumulation
/// 
/// PUBLIC API TERMS:
/// This benchmark uses public-facing terminology (NoCapacity/WithCapacity) to reflect
/// the WindowCacheOptions.RebalanceQueueCapacity configuration:
/// - NoCapacity = null (unbounded execution queue) - BASELINE
/// - WithCapacity = 10 (bounded execution queue with capacity of 10)
/// 
/// IMPLEMENTATION DETAILS:
/// Internally, these configurations map to execution controller implementations:
/// - Unbounded (NoCapacity) → Task-based execution with unbounded task chaining
/// - Bounded (WithCapacity) → Channel-based execution with bounded queue and backpressure
/// 
/// BASELINE RATIO CALCULATIONS:
/// BenchmarkDotNet automatically calculates performance ratios using NoCapacity as the baseline:
/// - Ratio Column: Shows WithCapacity performance relative to NoCapacity (baseline = 1.00)
/// - Ratio &lt; 1.0 = WithCapacity is faster (e.g., 0.012 = 83× faster)
/// - Ratio &gt; 1.0 = WithCapacity is slower (e.g., 1.44 = 44% slower)
/// - Ratios are calculated per (DataSourceLatencyMs, BurstSize) parameter combination
/// 
/// CRITICAL METHODOLOGY - Cache Hit Pattern for Intent Accumulation:
/// The benchmark uses a cold start prepopulation strategy to ensure ALL burst requests are cache hits:
/// 1. Cold Start Phase (IterationSetup):
///    - Prepopulate cache with oversized range covering all burst request ranges
///    - Wait for rebalance to complete (cache fully populated)
/// 2. Measurement Phase (BurstPattern methods):
///    - Submit BurstSize sequential requests (await each - WindowCache is single consumer)
///    - Each request is a CACHE HIT in User Path (returns instantly, ~microseconds)
///    - Each request shifts range right by +1 (triggers rebalance intent due to leftThreshold=1.0)
///    - Intents publish rapidly (no User Path I/O blocking)
///    - Rebalance executions accumulate in queue (DataSource latency slows execution)
///    - Measure convergence time (until all rebalances complete via WaitForIdleAsync)
/// 
/// WHY CACHE HITS ARE ESSENTIAL:
/// Without cache hits, User Path blocks on DataSource.FetchAsync, creating natural throttling
/// (50-100ms gaps between intent publications). This prevents queue accumulation and makes
/// execution strategy behavior unmeasurable (results dominated by I/O latency).
/// With cache hits, User Path returns instantly, allowing rapid intent publishing and queue accumulation.
/// 
/// PERFORMANCE MODEL:
/// Strategy performance depends on:
/// ✔ Execution serialization overhead (Task chaining vs Channel queue management)
/// ✔ Cancellation effectiveness (how many obsolete rebalances are cancelled vs executed)
/// ✔ Backpressure handling (Channel bounded queue vs Task unbounded chaining)
/// ✔ Memory pressure (allocations, GC collections)
/// ✔ Convergence time (how fast system reaches idle after burst)
/// 
/// DEBOUNCE DELAY = 0ms (CRITICAL):
/// DebounceDelay MUST be 0ms to prevent cancellation during debounce phase.
/// With debounce > 0ms:
/// - New execution request cancels previous request's CancellationToken
/// - Previous execution is likely still in Task.Delay(debounceDelay, cancellationToken)
/// - Cancellation triggers OperationCanceledException during delay
/// - Execution never reaches actual work (cancelled before I/O)
/// - Result: Almost all executions cancelled during debounce, not during I/O phase
/// - Benchmark would measure debounce delay × cancellation rate, NOT strategy behavior
/// 
/// EXPECTED BEHAVIOR:
/// - Unbounded (NoCapacity): Unbounded task chaining, effective cancellation during I/O
/// - Bounded (WithCapacity): Bounded queue (capacity=10), backpressure on intent processing loop
/// - With 0ms latency: Minimal queue accumulation, strategy overhead measurable (~1.4× slower for bounded)
/// - With 50-100ms latency, Burst ≤100: Similar performance (~1.0× ratio, both strategies handle well)
/// - With 50-100ms latency, Burst=1000: Bounded dramatically faster (0.012× ratio = 83× speedup)
///   - Unbounded: Queue accumulation, many cancelled executions still consume I/O time
///   - Bounded: Backpressure limits queue depth, prevents accumulation
/// 
/// CONFIGURATION:
/// - BaseSpanSize: Fixed at 100 (user requested range span, constant)
/// - InitialStart: Fixed at 10000 (starting position)
/// - Channel Capacity: Fixed at 10 (bounded queue size for WithCapacity configuration)
/// - RightCacheSize: Calculated dynamically to guarantee cache hits (>= BurstSize discrete points)
/// - LeftCacheSize: Fixed at 1 (minimal, only shifting right)
/// - LeftThreshold: 1.0 (always trigger rebalance, even on cache hit)
/// - RightThreshold: 0.0 (no right-side tolerance)
/// - DebounceDelay: 0ms (MANDATORY - see explanation above)
/// - Storage: Snapshot mode (consistent across runs)
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
    /// User always requests ranges of this size (constant span, shifting position).
    /// </summary>
    private const int BaseSpanSize = 100;

    /// <summary>
    /// Initial range start position for first request and cold start prepopulation.
    /// </summary>
    private const int InitialStart = 10000;

    /// <summary>
    /// Channel capacity for bounded strategy (ignored for Task strategy).
    /// Fixed at 10 to test backpressure behavior under queue accumulation.
    /// </summary>
    private const int ChannelCapacity = 10;

    // Infrastructure

    private WindowCache<int, int, IntegerFixedStepDomain>? _cache;
    private IDataSource<int, int> _dataSource = null!;
    private IntegerFixedStepDomain _domain;

    // Deterministic Workload Storage

    /// <summary>
    /// Precomputed request sequence for current iteration.
    /// Each request shifts by +1 to guarantee rebalance with leftThreshold=1.
    /// All requests are cache hits due to cold start prepopulation.
    /// </summary>
    private Range<int>[] _requestSequence = null!;

    /// <summary>
    /// Calculates the right cache coefficient needed to guarantee cache hits for all burst requests.
    /// </summary>
    /// <param name="burstSize">Number of requests in the burst.</param>
    /// <param name="baseSpanSize">User requested range span (constant).</param>
    /// <returns>Right cache coefficient (applied to baseSpanSize to get rightCacheSize).</returns>
    /// <remarks>
    /// <para><strong>Calculation Logic:</strong></para>
    /// <para>
    /// Each request shifts right by +1. With BurstSize requests, we shift right by BurstSize discrete points.
    /// Right cache must contain at least BurstSize discrete points.
    /// rightCacheSize = coefficient × baseSpanSize
    /// Therefore: coefficient = ceil(BurstSize / baseSpanSize)
    /// Add +1 buffer for safety margin.
    /// </para>
    /// <para><strong>Examples:</strong></para>
    /// <list type="bullet">
    /// <item><description>BurstSize=10,   BaseSpanSize=100 → coeff=1  (rightCacheSize=100 covers 10 shifts)</description></item>
    /// <item><description>BurstSize=100,  BaseSpanSize=100 → coeff=2  (rightCacheSize=200 covers 100 shifts)</description></item>
    /// <item><description>BurstSize=1000, BaseSpanSize=100 → coeff=11 (rightCacheSize=1100 covers 1000 shifts)</description></item>
    /// </list>
    /// </remarks>
    private static int CalculateRightCacheCoefficient(int burstSize, int baseSpanSize)
    {
        // We need rightCacheSize >= burstSize discrete points
        // rightCacheSize = coefficient * baseSpanSize
        // Therefore: coefficient = ceil(burstSize / baseSpanSize)
        var coefficient = (int)Math.Ceiling((double)burstSize / baseSpanSize);

        // Add buffer for safety
        return coefficient + 1;
    }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _domain = new IntegerFixedStepDomain();

        // Create data source with configured latency
        // For rebalance operations, latency simulates network/database I/O
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
    /// <param name="rebalanceQueueCapacity">
    /// Rebalance queue capacity configuration:
    /// - null = Unbounded (Task-based execution)
    /// - 10 = Bounded (Channel-based execution)
    /// </param>
    private void SetupCache(int? rebalanceQueueCapacity)
    {
        // Calculate cache coefficients based on burst size
        // Right cache must be large enough to cover all burst request shifts
        var rightCoefficient = CalculateRightCacheCoefficient(BurstSize, BaseSpanSize);
        var leftCoefficient = 1; // Minimal, only shifting right

        // Configure cache with aggressive thresholds and calculated cache sizes
        var options = new WindowCacheOptions(
            leftCacheSize: leftCoefficient,
            rightCacheSize: rightCoefficient,
            readMode: UserCacheReadMode.Snapshot, // Fixed for consistency
            leftThreshold: 1.0, // Always trigger rebalance (even on cache hit)
            rightThreshold: 0.0, // No right-side tolerance
            debounceDelay: TimeSpan.Zero, // CRITICAL: 0ms to prevent cancellation during debounce
            rebalanceQueueCapacity: rebalanceQueueCapacity
        );

        // Create fresh cache for this iteration
        _cache = new WindowCache<int, int, IntegerFixedStepDomain>(
            _dataSource,
            _domain,
            options
        );

        // Build initial range for first request
        var initialRange = Intervals.NET.Factories.Range.Closed<int>(
            InitialStart, 
            InitialStart + BaseSpanSize - 1
        );

        // Calculate cold start range that covers ALL burst requests
        // We need to prepopulate: InitialStart to (InitialStart + BaseSpanSize - 1 + BurstSize)
        // This ensures all shifted requests (up to +BurstSize) are cache hits
        var coldStartEnd = InitialStart + BaseSpanSize - 1 + BurstSize;
        var coldStartRange = Intervals.NET.Factories.Range.Closed<int>(InitialStart, coldStartEnd);

        // Cold Start Phase: Prepopulate cache with oversized range
        // This makes all subsequent burst requests cache hits in User Path
        _cache.GetDataAsync(coldStartRange, CancellationToken.None).GetAwaiter().GetResult();
        _cache.WaitForIdleAsync().GetAwaiter().GetResult();

        // Build deterministic request sequence (all will be cache hits)
        _requestSequence = BuildRequestSequence(initialRange);
    }

    /// <summary>
    /// Builds a deterministic request sequence with fixed span, shifting by +1 each time.
    /// This guarantees rebalance on every request when leftThreshold=1.0.
    /// All requests will be cache hits due to cold start prepopulation.
    /// </summary>
    private Range<int>[] BuildRequestSequence(Range<int> initialRange)
    {
        var sequence = new Range<int>[BurstSize];

        for (var i = 0; i < BurstSize; i++)
        {
            // Fixed span, shift right by (i+1) to trigger rebalance each time
            // Data already in cache (cache hit in User Path)
            // But range shift triggers rebalance intent (leftThreshold=1.0)
            sequence[i] = initialRange.Shift(_domain, i + 1);
        }

        return sequence;
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        // Ensure cache is idle before next iteration
        _cache?.WaitForIdleAsync().GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        // Dispose cache to release resources
        _cache?.DisposeAsync().GetAwaiter().GetResult();

        // Dispose data source if it implements IAsyncDisposable or IDisposable
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
    /// <remarks>
    /// <para><strong>Public API Configuration:</strong></para>
    /// <para>RebalanceQueueCapacity = null (unbounded execution queue)</para>
    /// 
    /// <para><strong>Implementation Details:</strong></para>
    /// <para>Uses Task-based execution controller with unbounded task chaining.</para>
    /// 
    /// <para><strong>Baseline Designation:</strong></para>
    /// <para>This method is marked with [Baseline = true], making it the reference point for 
    /// ratio calculations within each (DataSourceLatencyMs, BurstSize) parameter combination.
    /// The WithCapacity method's performance will be shown relative to this baseline.</para>
    /// 
    /// <para><strong>Execution Flow:</strong></para>
    /// <list type="number">
    /// <item><description>Submit BurstSize requests sequentially (await each - WindowCache is single consumer)</description></item>
    /// <item><description>Each request is a cache HIT (returns instantly, ~microseconds)</description></item>
    /// <item><description>Intent published BEFORE GetDataAsync returns (in UserRequestHandler finally block)</description></item>
    /// <item><description>Intents accumulate rapidly (no User Path I/O blocking)</description></item>
    /// <item><description>Rebalance executions chain via Task continuation (unbounded accumulation)</description></item>
    /// <item><description>Wait for convergence (all rebalances complete via WaitForIdleAsync)</description></item>
    /// </list>
    /// 
    /// <para><strong>What This Measures:</strong></para>
    /// <list type="bullet">
    /// <item><description>Total time from first request to system idle</description></item>
    /// <item><description>Task-based execution serialization overhead</description></item>
    /// <item><description>Cancellation effectiveness under unbounded accumulation</description></item>
    /// <item><description>Memory allocations (via MemoryDiagnoser)</description></item>
    /// </list>
    /// </remarks>
    [Benchmark(Baseline = true)]
    public async Task BurstPattern_NoCapacity()
    {
        // Submit all requests sequentially (NOT Task.WhenAll - WindowCache is single consumer)
        // Each request completes instantly (cache hit) and publishes intent before return
        for (var i = 0; i < BurstSize; i++)
        {
            var range = _requestSequence[i];
            _ = await _cache!.GetDataAsync(range, CancellationToken.None);
            // At this point:
            // - User Path completed (cache hit, ~microseconds)
            // - Intent published (in UserRequestHandler finally block)
            // - Rebalance queued via Task continuation (unbounded)
        }

        // All intents now published rapidly (total time ~milliseconds for all requests)
        // Rebalance queue has accumulated via Task chaining (unbounded)
        // Wait for all rebalances to complete (measures convergence time)
        await _cache!.WaitForIdleAsync();
    }

    /// <summary>
    /// Measures bounded execution (WithCapacity) performance with burst request pattern.
    /// Performance is compared against the NoCapacity baseline.
    /// </summary>
    /// <remarks>
    /// <para><strong>Public API Configuration:</strong></para>
    /// <para>RebalanceQueueCapacity = 10 (bounded execution queue with capacity of 10)</para>
    /// 
    /// <para><strong>Implementation Details:</strong></para>
    /// <para>Uses Channel-based execution controller with bounded queue and backpressure.
    /// When the queue reaches capacity, the intent processing loop blocks until space becomes available,
    /// applying backpressure to prevent unbounded accumulation.</para>
    /// 
    /// <para><strong>Ratio Comparison:</strong></para>
    /// <para>Performance is compared against NoCapacity (baseline) within each 
    /// (DataSourceLatencyMs, BurstSize) parameter combination. BenchmarkDotNet automatically 
    /// calculates the ratio column:
    /// - Ratio &lt; 1.0 = WithCapacity is faster (e.g., 0.012 = 83× faster)
    /// - Ratio &gt; 1.0 = WithCapacity is slower (e.g., 1.44 = 44% slower)</para>
    /// 
    /// <para><strong>Execution Flow:</strong></para>
    /// <list type="number">
    /// <item><description>Submit BurstSize requests sequentially (await each - WindowCache is single consumer)</description></item>
    /// <item><description>Each request is a cache HIT (returns instantly, ~microseconds)</description></item>
    /// <item><description>Intent published BEFORE GetDataAsync returns (in UserRequestHandler finally block)</description></item>
    /// <item><description>Intents accumulate rapidly (no User Path I/O blocking)</description></item>
    /// <item><description>Rebalance executions queue via Channel (bounded at capacity=10 with backpressure)</description></item>
    /// <item><description>Wait for convergence (all rebalances complete via WaitForIdleAsync)</description></item>
    /// </list>
    /// 
    /// <para><strong>What This Measures:</strong></para>
    /// <list type="bullet">
    /// <item><description>Total time from first request to system idle</description></item>
    /// <item><description>Channel-based execution serialization overhead</description></item>
    /// <item><description>Backpressure effectiveness under bounded accumulation</description></item>
    /// <item><description>Memory allocations (via MemoryDiagnoser)</description></item>
    /// </list>
    /// </remarks>
    [Benchmark]
    public async Task BurstPattern_WithCapacity()
    {
        // Submit all requests sequentially (NOT Task.WhenAll - WindowCache is single consumer)
        // Each request completes instantly (cache hit) and publishes intent before return
        for (var i = 0; i < BurstSize; i++)
        {
            var range = _requestSequence[i];
            _ = await _cache!.GetDataAsync(range, CancellationToken.None);
            // At this point:
            // - User Path completed (cache hit, ~microseconds)
            // - Intent published (in UserRequestHandler finally block)
            // - Rebalance queued via Channel (bounded with backpressure)
        }

        // All intents now published rapidly (total time ~milliseconds for all requests)
        // Rebalance queue has accumulated in Channel (bounded at capacity=10)
        // Wait for all rebalances to complete (measures convergence time)
        await _cache!.WaitForIdleAsync();
    }
}
