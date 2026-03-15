using BenchmarkDotNet.Attributes;
using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Caching.Benchmarks.Infrastructure;
using Intervals.NET.Caching.VisitedPlaces.Public.Cache;

namespace Intervals.NET.Caching.Benchmarks.VisitedPlaces;

/// <summary>
/// Partial Hit Benchmarks for VisitedPlaces Cache.
/// Measures the cost of requests that partially overlap cached segments (gaps must be fetched).
/// 
/// Two methods split to decouple read-side vs write-side scaling:
/// - SingleGap: K adjacent segments + 1 gap at edge. Isolates read-cost scaling with K.
/// - MultipleGaps: K+1 non-adjacent segments with K internal gaps. K stores → K/AppendBufferSize normalizations.
/// 
/// Methodology:
/// - Pre-populated cache with specific segment layouts
/// - Request range designed to hit existing segments and miss gaps
/// - WaitForIdleAsync INSIDE benchmark (measuring complete partial hit + normalization cost)
/// - Fresh cache per iteration
/// </summary>
[MemoryDiagnoser]
[MarkdownExporter]
public class PartialHitBenchmarks
{
    private VisitedPlacesCache<int, int, IntegerFixedStepDomain>? _cache;
    private SynchronousDataSource _dataSource = null!;
    private IntegerFixedStepDomain _domain;

    private const int SegmentSpan = 10;

    #region SingleGap Parameters and Setup

    /// <summary>
    /// Number of existing segments the request intersects — measures read-side scaling.
    /// </summary>
    [Params(1, 10, 100, 1_000)]
    public int IntersectingSegments { get; set; }

    /// <summary>
    /// Total segments in cache — measures storage size impact on FindIntersecting.
    /// </summary>
    [Params(1_000, 100_000)]
    public int TotalSegments { get; set; }

    /// <summary>
    /// Storage strategy — Snapshot vs LinkedList.
    /// </summary>
    [Params(StorageStrategyType.Snapshot, StorageStrategyType.LinkedList)]
    public StorageStrategyType StorageStrategy { get; set; }

    private Range<int> _singleGapRange;

    [IterationSetup(Target = nameof(PartialHit_SingleGap))]
    public void IterationSetup_SingleGap()
    {
        _domain = new IntegerFixedStepDomain();
        _dataSource = new SynchronousDataSource(_domain);

        // Create cache with ample capacity
        _cache = VpcCacheHelpers.CreateCache(
            _dataSource, _domain, StorageStrategy,
            maxSegmentCount: TotalSegments + 1000,
            appendBufferSize: 8);

        // Populate TotalSegments adjacent segments
        VpcCacheHelpers.PopulateSegments(_cache, TotalSegments, SegmentSpan);

        // SingleGap: request spans IntersectingSegments existing segments + 1 gap at the right edge
        // Existing segments: [0,9], [10,19], ..., [(IntersectingSegments-1)*10, IntersectingSegments*10-1]
        // Request extends SegmentSpan beyond the last intersecting segment into uncached territory
        const int requestStart = 0;
        var requestEnd = (IntersectingSegments * SegmentSpan) + SegmentSpan - 1;
        _singleGapRange = Factories.Range.Closed<int>(requestStart, requestEnd);
    }

    /// <summary>
    /// Measures partial hit cost with a single gap.
    /// K existing segments are hit, 1 gap is fetched from data source.
    /// Isolates read-side scaling: how does FindIntersecting + ComputeGaps cost scale with K?
    /// </summary>
    [Benchmark]
    public async Task PartialHit_SingleGap()
    {
        await _cache!.GetDataAsync(_singleGapRange, CancellationToken.None);
        await _cache.WaitForIdleAsync();
    }

    #endregion

    #region MultipleGaps Parameters and Setup

    // MultipleGaps reuses StorageStrategy from above but adds GapCount and AppendBufferSize

    /// <summary>
    /// Number of internal gaps — each gap produces one data source fetch and one store.
    /// K stores → K/AppendBufferSize normalizations. Potential quadratic cost with large gap counts.
    /// </summary>
    [Params(1, 10, 100, 1_000)]
    public int GapCount { get; set; }

    /// <summary>
    /// Append buffer size — controls normalization frequency.
    /// 1 = normalize every store, 8 = normalize every 8 stores (default).
    /// </summary>
    [Params(1, 8)]
    public int AppendBufferSize { get; set; }

    /// <summary>
    /// Total segments for MultipleGaps variant. Larger values needed to accommodate gap layout.
    /// </summary>
    [Params(10_000, 100_000)]
    public int MultiGapTotalSegments { get; set; }

    private Range<int> _multipleGapsRange;

    [IterationSetup(Target = nameof(PartialHit_MultipleGaps))]
    public void IterationSetup_MultipleGaps()
    {
        _domain = new IntegerFixedStepDomain();
        _dataSource = new SynchronousDataSource(_domain);

        // Layout: alternating segments and gaps
        // segments at positions 0, 20, 40, ... (span=10, gap=10)
        // Total non-adjacent segments = GapCount + 1 (K gaps between K+1 segments)
        var nonAdjacentCount = GapCount + 1;

        // Create cache with ample capacity
        _cache = VpcCacheHelpers.CreateCache(
            _dataSource, _domain, StorageStrategy,
            maxSegmentCount: MultiGapTotalSegments + 1000,
            appendBufferSize: AppendBufferSize);

        // First populate the non-adjacent segments (these create the gap pattern)
        const int gapSize = SegmentSpan; // Gap size = segment span for uniform layout
        VpcCacheHelpers.PopulateWithGaps(_cache, nonAdjacentCount, SegmentSpan, gapSize);

        // Then populate remaining segments beyond the gap pattern to reach MultiGapTotalSegments
        var remainingCount = MultiGapTotalSegments - nonAdjacentCount;
        if (remainingCount > 0)
        {
            var startAfterPattern = nonAdjacentCount * (SegmentSpan + gapSize) + gapSize;
            VpcCacheHelpers.PopulateSegments(_cache, remainingCount, SegmentSpan, startAfterPattern);
        }

        // Request spans all non-adjacent segments (hitting all gaps)
        var stride = SegmentSpan + gapSize;
        var requestStart = 0;
        var requestEnd = (nonAdjacentCount - 1) * stride + SegmentSpan - 1;
        _multipleGapsRange = Factories.Range.Closed<int>(requestStart, requestEnd);
    }

    /// <summary>
    /// Measures partial hit cost with multiple gaps.
    /// K+1 existing segments hit, K gaps fetched. K stores → K/AppendBufferSize normalizations.
    /// Tests write-side scaling: how does normalization cost scale with gap count?
    /// </summary>
    [Benchmark]
    public async Task PartialHit_MultipleGaps()
    {
        await _cache!.GetDataAsync(_multipleGapsRange, CancellationToken.None);
        await _cache.WaitForIdleAsync();
    }

    #endregion
}
