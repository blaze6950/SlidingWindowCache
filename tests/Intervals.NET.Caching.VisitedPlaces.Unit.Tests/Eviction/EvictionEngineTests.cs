using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Caching.VisitedPlaces.Core;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Policies;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Selectors;
using Intervals.NET.Caching.VisitedPlaces.Infrastructure.Storage;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure.Helpers;

namespace Intervals.NET.Caching.VisitedPlaces.Unit.Tests.Eviction;

/// <summary>
/// Unit tests for <see cref="EvictionEngine{TRange,TData}"/>.
/// Validates constructor validation, metadata delegation to the selector,
/// segment initialization (selector + stateful policy), and evaluate-and-execute
/// (no eviction, eviction triggered, diagnostics).
/// </summary>
public sealed class EvictionEngineTests
{
    private readonly IntegerFixedStepDomain _domain = TestHelpers.CreateIntDomain();
    private readonly EventCounterCacheDiagnostics _diagnostics = new();

    #region Constructor Tests

    [Fact]
    public void Constructor_WithNullPolicies_ThrowsArgumentNullException()
    {
        // ARRANGE & ACT
        var exception = Record.Exception(() =>
            new EvictionEngine<int, int>(
                null!,
                new LruEvictionSelector<int, int>(),
                _diagnostics));

        // ASSERT
        Assert.IsType<ArgumentNullException>(exception);
    }

    [Fact]
    public void Constructor_WithNullSelector_ThrowsArgumentNullException()
    {
        // ARRANGE & ACT
        var exception = Record.Exception(() =>
            new EvictionEngine<int, int>(
                [new MaxSegmentCountPolicy<int, int>(10)],
                null!,
                _diagnostics));

        // ASSERT
        Assert.IsType<ArgumentNullException>(exception);
    }

    [Fact]
    public void Constructor_WithNullDiagnostics_ThrowsArgumentNullException()
    {
        // ARRANGE & ACT
        var exception = Record.Exception(() =>
            new EvictionEngine<int, int>(
                [new MaxSegmentCountPolicy<int, int>(10)],
                new LruEvictionSelector<int, int>(),
                null!));

        // ASSERT
        Assert.IsType<ArgumentNullException>(exception);
    }

    [Fact]
    public void Constructor_WithValidParameters_DoesNotThrow()
    {
        // ARRANGE & ACT
        var exception = Record.Exception(() =>
            new EvictionEngine<int, int>(
                [new MaxSegmentCountPolicy<int, int>(10)],
                new LruEvictionSelector<int, int>(),
                _diagnostics));

        // ASSERT
        Assert.Null(exception);
    }

    [Fact]
    public void Constructor_WithEmptyPolicies_DoesNotThrow()
    {
        // ARRANGE & ACT
        var exception = Record.Exception(() =>
            new EvictionEngine<int, int>(
                [],
                new LruEvictionSelector<int, int>(),
                _diagnostics));

        // ASSERT
        Assert.Null(exception);
    }

    #endregion

    #region UpdateMetadata — Delegates to Selector

    [Fact]
    public void UpdateMetadata_WithUsedSegments_UpdatesLruMetadata()
    {
        // ARRANGE — LRU selector tracks LastAccessedAt
        var engine = CreateEngine(maxSegmentCount: 100);
        var segment = CreateSegment(0, 9);

        // Initialize metadata so the segment has LRU state to update
        engine.InitializeSegment(segment);

        var beforeUpdate = DateTime.UtcNow;

        // ACT
        engine.UpdateMetadata([segment]);

        // ASSERT — LastAccessedAt must have been refreshed
        var meta = Assert.IsType<LruEvictionSelector<int, int>.LruMetadata>(segment.EvictionMetadata);
        Assert.True(meta.LastAccessedAt >= beforeUpdate);
    }

    [Fact]
    public void UpdateMetadata_WithEmptyUsedSegments_DoesNotThrow()
    {
        // ARRANGE
        var engine = CreateEngine(maxSegmentCount: 100);

        // ACT & ASSERT
        var exception = Record.Exception(() => engine.UpdateMetadata([]));
        Assert.Null(exception);
    }

    #endregion

    #region InitializeSegment — Selector Metadata + Stateful Policy Notification

    [Fact]
    public void InitializeSegment_AttachesLruMetadataToSegment()
    {
        // ARRANGE
        var engine = CreateEngine(maxSegmentCount: 100);
        var segment = CreateSegment(0, 9);

        // ACT
        engine.InitializeSegment(segment);

        // ASSERT — LRU selector must have set metadata
        Assert.IsType<LruEvictionSelector<int, int>.LruMetadata>(segment.EvictionMetadata);
    }

    [Fact]
    public void InitializeSegment_NotifiesStatefulPolicy()
    {
        // ARRANGE — stateful span policy with max 5; segment span=10 will push it over
        var spanPolicy = new MaxTotalSpanPolicy<int, int, IntegerFixedStepDomain>(5, _domain);
        var (selector, storage) = CreateSelectorWithStorage();
        var engine = new EvictionEngine<int, int>(
            [spanPolicy],
            selector,
            _diagnostics);
        var segment = CreateSegment(0, 9); // span 10 > 5

        // Before initialize: policy has _totalSpan=0 → EvaluateAndExecute returns empty
        Assert.Empty(engine.EvaluateAndExecute([]).ToList());
        Assert.Equal(1, _diagnostics.EvictionEvaluated);
        Assert.Equal(0, _diagnostics.EvictionTriggered);

        // ACT
        engine.InitializeSegment(segment);
        storage.TryAdd(segment);

        // ASSERT — stateful policy now knows about the segment → evaluates as exceeded
        var toRemove = engine.EvaluateAndExecute([segment]).ToList(); // immune → empty result
        Assert.Empty(toRemove); // all immune, so nothing removed
        Assert.Equal(2, _diagnostics.EvictionEvaluated);
        Assert.Equal(1, _diagnostics.EvictionTriggered); // triggered but immune
    }

    #endregion

    #region EvaluateAndExecute — No Eviction Needed

    [Fact]
    public void EvaluateAndExecute_WhenNoPolicyFires_ReturnsEmptyList()
    {
        // ARRANGE — limit 10; only 3 segments
        var engine = CreateEngine(maxSegmentCount: 10);
        var segments = CreateSegments(3);
        foreach (var seg in segments) engine.InitializeSegment(seg);

        // ACT
        var toRemove = engine.EvaluateAndExecute([]).ToList();

        // ASSERT
        Assert.Empty(toRemove);
    }

    [Fact]
    public void EvaluateAndExecute_WhenNoPolicyFires_FiresOnlyEvictionEvaluatedDiagnostic()
    {
        // ARRANGE
        var engine = CreateEngine(maxSegmentCount: 10);
        var segments = CreateSegments(3);
        foreach (var seg in segments) engine.InitializeSegment(seg);

        // ACT
        engine.EvaluateAndExecute([]).ToList();

        // ASSERT
        Assert.Equal(1, _diagnostics.EvictionEvaluated);
        Assert.Equal(0, _diagnostics.EvictionTriggered);
        Assert.Equal(0, _diagnostics.EvictionExecuted);
    }

    #endregion

    #region EvaluateAndExecute — Eviction Triggered

    [Fact]
    public void EvaluateAndExecute_WhenPolicyFires_ReturnsCandidatesToRemove()
    {
        // ARRANGE — limit 2; 3 segments stored → 1 must be evicted
        var engine = CreateEngine(maxSegmentCount: 2);
        var segments = CreateSegmentsWithLruMetadata(engine, 3);

        // ACT — none are immune (empty justStored)
        var toRemove = engine.EvaluateAndExecute([]).ToList();

        // ASSERT — exactly 1 removed to bring count from 3 → 2
        Assert.Single(toRemove);
    }

    [Fact]
    public void EvaluateAndExecute_WhenPolicyFires_FiresEvictionEvaluatedAndTriggeredDiagnostics()
    {
        // ARRANGE
        var engine = CreateEngine(maxSegmentCount: 2);
        var segments = CreateSegmentsWithLruMetadata(engine, 3);

        // ACT — force enumeration so all candidates are yielded
        engine.EvaluateAndExecute([]).ToList();

        // ASSERT — engine fires Evaluated and Triggered; EvictionExecuted is the consumer's responsibility
        Assert.Equal(1, _diagnostics.EvictionEvaluated);
        Assert.Equal(1, _diagnostics.EvictionTriggered);
        Assert.Equal(0, _diagnostics.EvictionExecuted);
    }

    [Fact]
    public void EvaluateAndExecute_WhenAllCandidatesImmune_ReturnsEmpty()
    {
        // ARRANGE — limit 1; 2 segments but both are just-stored (immune)
        var engine = CreateEngine(maxSegmentCount: 1);
        var segments = CreateSegmentsWithLruMetadata(engine, 2);

        // ACT — both immune
        var toRemove = engine.EvaluateAndExecute(segments).ToList();

        // ASSERT — policy fires but no eligible candidates
        Assert.Empty(toRemove);
        Assert.Equal(1, _diagnostics.EvictionTriggered);
        Assert.Equal(0, _diagnostics.EvictionExecuted); // engine never fires EvictionExecuted
    }

    [Fact]
    public void EvaluateAndExecute_WithMultiplePoliciesFiring_RemovesUntilAllSatisfied()
    {
        // ARRANGE — count (max 1) and span (max 5); 3 segments → both fire
        var spanPolicy = new MaxTotalSpanPolicy<int, int, IntegerFixedStepDomain>(5, _domain);
        var countPolicy = new MaxSegmentCountPolicy<int, int>(1);
        var (selector, storage) = CreateSelectorWithStorage();
        var engine = new EvictionEngine<int, int>(
            [countPolicy, spanPolicy],
            selector,
            _diagnostics);

        var seg1 = CreateSegment(0, 9);   // span 10
        var seg2 = CreateSegment(20, 29); // span 10
        var seg3 = CreateSegment(40, 49); // span 10
        foreach (var s in new[] { seg1, seg2, seg3 })
        {
            engine.InitializeSegment(s);
            storage.TryAdd(s);
        }

        // ACT
        var toRemove = engine.EvaluateAndExecute([]).ToList();

        // ASSERT — must evict until count<=1 AND span<=5 are both satisfied;
        // all spans are 10>5 so all 3 would need to go to satisfy span — but immunity stops at 0 non-immune
        // In practice executor loops until both pressures satisfied or candidates exhausted.
        // With 3 segments all non-immune: removes 2 to satisfy count (1 remains); span still >5 but
        // the remaining seg has span 10 which still exceeds 5 — executor removes it too → all 3.
        Assert.Equal(3, toRemove.Count);
        Assert.Equal(1, _diagnostics.EvictionTriggered);
    }

    #endregion

    #region Helpers

    // Per-test storage backing the selector; reset each time CreateEngine is called.
    private SnapshotAppendBufferStorage<int, int> _storage = new(appendBufferSize: 64);

    private EvictionEngine<int, int> CreateEngine(int maxSegmentCount)
    {
        var (selector, storage) = CreateSelectorWithStorage();
        _storage = storage;
        return new EvictionEngine<int, int>(
            [new MaxSegmentCountPolicy<int, int>(maxSegmentCount)],
            selector,
            _diagnostics);
    }

    /// <summary>
    /// Creates an <see cref="LruEvictionSelector{TRange,TData}"/> that has been initialized
    /// with a fresh <see cref="SnapshotAppendBufferStorage{TRange,TData}"/>.
    /// </summary>
    private static (LruEvictionSelector<int, int> Selector, SnapshotAppendBufferStorage<int, int> Storage)
        CreateSelectorWithStorage()
    {
        var storage = new SnapshotAppendBufferStorage<int, int>(appendBufferSize: 64);
        var selector = new LruEvictionSelector<int, int>();
        ((IStorageAwareEvictionSelector<int, int>)selector).Initialize(storage);
        return (selector, storage);
    }

    private IReadOnlyList<CachedSegment<int, int>> CreateSegmentsWithLruMetadata(
        EvictionEngine<int, int> engine,
        int count)
    {
        var segments = CreateSegments(count);
        foreach (var seg in segments)
        {
            engine.InitializeSegment(seg);
            _storage.TryAdd(seg);
        }
        return segments;
    }

    private static IReadOnlyList<CachedSegment<int, int>> CreateSegments(int count)
    {
        var result = new List<CachedSegment<int, int>>();
        for (var i = 0; i < count; i++)
        {
            var start = i * 10;
            result.Add(CreateSegment(start, start + 5));
        }
        return result;
    }

    private static CachedSegment<int, int> CreateSegment(int start, int end)
    {
        var range = TestHelpers.CreateRange(start, end);
        return new CachedSegment<int, int>(
            range,
            new ReadOnlyMemory<int>(new int[end - start + 1]));
    }

    #endregion
}
