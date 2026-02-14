using BenchmarkDotNet.Attributes;
using Intervals.NET;
using Intervals.NET.Domain.Default.Numeric;
using SlidingWindowCache.Benchmarks.Infrastructure;
using SlidingWindowCache.Public;
using SlidingWindowCache.Public.Configuration;

namespace SlidingWindowCache.Benchmarks.Benchmarks;

/// <summary>
/// Scenario Benchmarks
/// End-to-end scenario testing including cold start and locality patterns.
/// NOT microbenchmarks - measures complete workflows.
/// 
/// EXECUTION FLOW: Simulates realistic usage patterns
/// 
/// Methodology:
/// - Fresh cache per iteration
/// - Cold start: Measures initial cache population (includes WaitForIdleAsync)
/// - Locality: Simulates sequential access patterns (cleanup handles stabilization)
/// - Compares cached vs uncached approaches
/// </summary>
[MemoryDiagnoser]
[MarkdownExporter]
public class ScenarioBenchmarks
{
    private SynchronousDataSource _dataSource = default!;
    private IntegerFixedStepDomain _domain = default!;
    private WindowCache<int, int, IntegerFixedStepDomain>? _snapshotCache;
    private WindowCache<int, int, IntegerFixedStepDomain>? _copyOnReadCache;
    private WindowCacheOptions _snapshotOptions = default!;
    private WindowCacheOptions _copyOnReadOptions = default!;
    private List<Range<int>> _sequentialRanges = default!;
    private Range<int> _coldStartRange;

    private const int ColdStartRangeStart = 1000;
    private const int ColdStartRangeEnd = 2000;
    private const int LocalityStartPosition = 1000;
    private const int LocalityRangeSize = 100;
    private const int LocalityNumberOfRequests = 10;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _domain = new IntegerFixedStepDomain();
        _dataSource = new SynchronousDataSource(_domain);

        // Cold start configuration
        _coldStartRange = Intervals.NET.Factories.Range.Closed<int>(
            ColdStartRangeStart,
            ColdStartRangeEnd
        );

        _snapshotOptions = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            UserCacheReadMode.Snapshot,
            leftThreshold: 0.2,
            rightThreshold: 0.2
        );

        _copyOnReadOptions = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            UserCacheReadMode.CopyOnRead,
            leftThreshold: 0.2,
            rightThreshold: 0.2
        );

        // Generate sequential ranges for locality simulation
        // Simulates forward pagination pattern
        _sequentialRanges = new List<Range<int>>(LocalityNumberOfRequests);
        for (var i = 0; i < LocalityNumberOfRequests; i++)
        {
            var start = LocalityStartPosition + (i * LocalityRangeSize);
            var end = start + LocalityRangeSize - 1;
            _sequentialRanges.Add(Intervals.NET.Factories.Range.Closed<int>(start, end));
        }
    }

    #region Cold Start Benchmarks

    [IterationSetup(Target = nameof(ColdStart_Rebalance_Snapshot) + "," + nameof(ColdStart_Rebalance_CopyOnRead))]
    public void ColdStartIterationSetup()
    {
        // Create fresh caches for cold start measurement
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
    }

    [Benchmark(Baseline = true)]
    public async Task ColdStart_Rebalance_Snapshot()
    {
        // Measure complete cold start: initial fetch + rebalance
        // WaitForIdleAsync is PART of cold start cost
        await _snapshotCache!.GetDataAsync(_coldStartRange, CancellationToken.None);
        await _snapshotCache.WaitForIdleAsync(timeout: TimeSpan.FromSeconds(5));
    }

    [Benchmark]
    public async Task ColdStart_Rebalance_CopyOnRead()
    {
        // Measure complete cold start: initial fetch + rebalance
        // WaitForIdleAsync is PART of cold start cost
        await _copyOnReadCache!.GetDataAsync(_coldStartRange, CancellationToken.None);
        await _copyOnReadCache.WaitForIdleAsync(timeout: TimeSpan.FromSeconds(5));
    }

    #endregion

    #region Locality Scenario Benchmarks

    [IterationSetup(Target = nameof(User_LocalityScenario_DirectDataSource) + "," + 
                            nameof(User_LocalityScenario_Snapshot) + "," + 
                            nameof(User_LocalityScenario_CopyOnRead))]
    public void LocalityIterationSetup()
    {
        // Create fresh caches for locality scenario
        var localitySnapshotOptions = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 9, // Aggressive prefetch for sequential access
            UserCacheReadMode.Snapshot,
            leftThreshold: 0,
            rightThreshold: 0
        );

        _snapshotCache = new WindowCache<int, int, IntegerFixedStepDomain>(
            _dataSource,
            _domain,
            localitySnapshotOptions
        );

        var localityCopyOnReadOptions = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 9, // Moderate prefetch for sequential access
            UserCacheReadMode.CopyOnRead,
            leftThreshold: 0.2,
            rightThreshold: 0.2
        );

        _copyOnReadCache = new WindowCache<int, int, IntegerFixedStepDomain>(
            _dataSource,
            _domain,
            localityCopyOnReadOptions
        );

        // Prime initial window in setup phase
        var firstRange = _sequentialRanges[0];
        _snapshotCache.GetDataAsync(firstRange, CancellationToken.None).GetAwaiter().GetResult();
        _copyOnReadCache.GetDataAsync(firstRange, CancellationToken.None).GetAwaiter().GetResult();

        // Wait for initial priming to complete
        _snapshotCache.WaitForIdleAsync().GetAwaiter().GetResult();
        _copyOnReadCache.WaitForIdleAsync().GetAwaiter().GetResult();
    }

    [IterationCleanup(Target = nameof(User_LocalityScenario_DirectDataSource) + "," + 
                              nameof(User_LocalityScenario_Snapshot) + "," + 
                              nameof(User_LocalityScenario_CopyOnRead))]
    public void LocalityIterationCleanup()
    {
        // Wait for final rebalancing to complete after scenario
        _snapshotCache?.WaitForIdleAsync(timeout: TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        _copyOnReadCache?.WaitForIdleAsync(timeout: TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
    }

    [Benchmark]
    public async Task User_LocalityScenario_DirectDataSource()
    {
        // Baseline: Direct data source access - no caching
        // Every request hits data source (10x calls)
        foreach (var range in _sequentialRanges)
        {
            await _dataSource.FetchAsync(range, CancellationToken.None);
        }
    }

    [Benchmark]
    public async Task User_LocalityScenario_Snapshot()
    {
        // Cached sequential access with Snapshot mode
        // NO WaitForIdleAsync in loop - measures user-facing latency only
        // Prefetching should reduce data source calls significantly
        foreach (var range in _sequentialRanges)
        {
            await _snapshotCache!.GetDataAsync(range, CancellationToken.None);
        }
    }

    [Benchmark]
    public async Task User_LocalityScenario_CopyOnRead()
    {
        // Cached sequential access with CopyOnRead mode
        // NO WaitForIdleAsync in loop - measures user-facing latency only
        // Prefetching should reduce data source calls significantly
        foreach (var range in _sequentialRanges)
        {
            await _copyOnReadCache!.GetDataAsync(range, CancellationToken.None);
        }
    }

    #endregion
}
