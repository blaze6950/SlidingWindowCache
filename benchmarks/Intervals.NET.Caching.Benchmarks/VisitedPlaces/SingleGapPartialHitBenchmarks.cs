using BenchmarkDotNet.Attributes;
using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Caching.Benchmarks.Infrastructure;
using Intervals.NET.Caching.VisitedPlaces.Public.Cache;

namespace Intervals.NET.Caching.Benchmarks.VisitedPlaces;

/// <summary>
/// Single-Gap Partial Hit Benchmarks for VisitedPlaces Cache.
/// Measures partial hit cost when a request crosses exactly one cached/uncached boundary.
///
/// Layout uses alternating [gap][segment] pattern (stride = SegmentSpan + GapSize):
///   Gaps:     [0,4],  [15,19], [30,34], ...
///   Segments: [5,14], [20,29], [35,44], ...
/// (SegmentSpan=10, GapSize=5 — so a SegmentSpan-wide request can straddle any gap.)
///
/// Two benchmark methods isolate the two structural cases:
///   - OneHit:  request [0,9]   → 1 gap [0,4]   + 1 segment hit [5,9]  from [5,14]
///   - TwoHits: request [12,21] → 1 gap [15,19] + 2 segment hits [12,14]+[20,21]
///
/// Both trigger exactly one data source fetch and one normalization event per invocation.
///
/// Methodology:
/// - Learning pass in GlobalSetup: throwaway cache exercises PopulateWithGaps + both
///   benchmark request ranges so the data source can be frozen.
/// - Fresh cache per iteration via IterationSetup with FrozenDataSource.
///
/// Parameters:
///   - TotalSegments: {1_000, 10_000} — storage size (FindIntersecting cost)
///   - StorageStrategy: Snapshot vs LinkedList
/// </summary>
[MemoryDiagnoser]
[MarkdownExporter]
public class SingleGapPartialHitBenchmarks
{
    private VisitedPlacesCache<int, int, IntegerFixedStepDomain>? _cache;
    private FrozenDataSource _frozenDataSource = null!;
    private IntegerFixedStepDomain _domain;

    // Layout constants: SegmentSpan=10, GapSize=5 → stride=15, segments start at offset GapSize=5
    private const int SegmentSpan = 10;
    private const int GapSize = SegmentSpan / 2; // = 5
    private const int Stride = SegmentSpan + GapSize; // = 15
    private const int SegmentStart = GapSize; // = 5, so gaps come first

    // Precomputed request ranges (set in GlobalSetup once TotalSegments is known)
    private Range<int> _oneHitRange;
    private Range<int> _twoHitsRange;

    /// <summary>
    /// Total segments in cache — measures storage size impact on FindIntersecting.
    /// </summary>
    [Params(1_000, 10_000)]
    public int TotalSegments { get; set; }

    /// <summary>
    /// Storage strategy — Snapshot vs LinkedList.
    /// </summary>
    [Params(StorageStrategyType.Snapshot, StorageStrategyType.LinkedList)]
    public StorageStrategyType StorageStrategy { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _domain = new IntegerFixedStepDomain();

        // OneHit: request [0,9] → gap [0,4], hit [5,9] from segment [5,14]
        _oneHitRange = Factories.Range.Closed<int>(0, SegmentSpan - 1);

        // TwoHits: request [12,21] → hit [12,14] from [5,14], gap [15,19], hit [20,21] from [20,29]
        _twoHitsRange = Factories.Range.Closed<int>(
            SegmentSpan + GapSize / 2,                    // = 12
            SegmentSpan + GapSize / 2 + SegmentSpan - 1); // = 21

        // Learning pass: exercise PopulateWithGaps and both benchmark request ranges.
        var learningSource = new SynchronousDataSource(_domain);
        var throwaway = VpcCacheHelpers.CreateCache(
            learningSource, _domain, StorageStrategy,
            maxSegmentCount: TotalSegments + 100,
            appendBufferSize: 8);
        VpcCacheHelpers.PopulateWithGaps(throwaway, TotalSegments, SegmentSpan, GapSize, SegmentStart);
        throwaway.GetDataAsync(_oneHitRange, CancellationToken.None).GetAwaiter().GetResult();
        throwaway.GetDataAsync(_twoHitsRange, CancellationToken.None).GetAwaiter().GetResult();
        throwaway.WaitForIdleAsync().GetAwaiter().GetResult();

        _frozenDataSource = learningSource.Freeze();
    }

    #region OneHit

    [IterationSetup(Target = nameof(PartialHit_SingleGap_OneHit))]
    public void IterationSetup_OneHit()
    {
        // Fresh cache per iteration: the benchmark stores the gap segment each time.
        _cache = VpcCacheHelpers.CreateCache(
            _frozenDataSource, _domain, StorageStrategy,
            maxSegmentCount: TotalSegments + 100,
            appendBufferSize: 8);

        // Populate with TotalSegments segments in alternating gap/segment layout.
        // Segments at: SegmentStart + k*Stride = 5, 20, 35, ...
        VpcCacheHelpers.PopulateWithGaps(_cache, TotalSegments, SegmentSpan, GapSize, SegmentStart);
    }

    /// <summary>
    /// Partial hit: request [0,9] crosses the initial gap [0,4] into segment [5,14].
    /// Produces 1 gap fetch + 1 cache hit. Measures single boundary crossing cost.
    /// </summary>
    [Benchmark]
    public async Task PartialHit_SingleGap_OneHit()
    {
        await _cache!.GetDataAsync(_oneHitRange, CancellationToken.None);
        await _cache.WaitForIdleAsync();
    }

    #endregion

    #region TwoHits

    [IterationSetup(Target = nameof(PartialHit_SingleGap_TwoHits))]
    public void IterationSetup_TwoHits()
    {
        // Fresh cache per iteration: the benchmark stores the gap segment each time.
        _cache = VpcCacheHelpers.CreateCache(
            _frozenDataSource, _domain, StorageStrategy,
            maxSegmentCount: TotalSegments + 100,
            appendBufferSize: 8);

        VpcCacheHelpers.PopulateWithGaps(_cache, TotalSegments, SegmentSpan, GapSize, SegmentStart);
    }

    /// <summary>
    /// Partial hit: request [12,21] spans across gap [15,19] touching segments [5,14] and [20,29].
    /// Produces 1 gap fetch + 2 cache hits. Measures double boundary crossing cost.
    /// </summary>
    [Benchmark]
    public async Task PartialHit_SingleGap_TwoHits()
    {
        await _cache!.GetDataAsync(_twoHitsRange, CancellationToken.None);
        await _cache.WaitForIdleAsync();
    }

    #endregion
}
