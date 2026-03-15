using Intervals.NET.Caching.VisitedPlaces.Core;
using Intervals.NET.Caching.VisitedPlaces.Infrastructure.Storage;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure.Helpers;

namespace Intervals.NET.Caching.VisitedPlaces.Unit.Tests.Storage;

/// <summary>
/// Unit tests for <see cref="SegmentStorageBase{TRange,TData}"/> invariant-enforcement logic,
/// parameterised over both concrete strategies.
/// <para>
/// Every test in this class targets behaviour owned by the base class:
/// VPC.C.3 overlap guard (<see cref="ISegmentStorage{TRange,TData}.TryAdd"/> /
/// <see cref="ISegmentStorage{TRange,TData}.TryAddRange"/>),
/// VPC.T.1 idempotent removal (<see cref="ISegmentStorage{TRange,TData}.TryRemove"/>),
/// retry/filter contract (<see cref="ISegmentStorage{TRange,TData}.TryGetRandomSegment"/>),
/// normalization threshold check (<see cref="ISegmentStorage{TRange,TData}.TryNormalize"/>),
/// and <see cref="ISegmentStorage{TRange,TData}.Count"/> consistency.
/// </para>
/// <para>
/// Data-structure-specific mechanics (stride index rebuild, append buffer merge, etc.) are
/// tested in the per-strategy test classes.
/// </para>
/// </summary>
public sealed class SegmentStorageBaseTests
{
    // -------------------------------------------------------------------------
    // Strategy factories — parameterize every test over both strategies
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns one factory per concrete storage strategy. Each factory produces a fresh
    /// <see cref="ISegmentStorage{TRange,TData}"/> instance and optionally accepts a
    /// <see cref="TimeProvider"/> for TTL tests.
    /// </summary>
    /// <remarks>
    /// The factory is boxed as <see cref="object"/> to avoid an accessibility mismatch:
    /// <see cref="ISegmentStorage{TRange,TData}"/> is internal, so it cannot appear in a public
    /// method signature (CS0051). Each test method unboxes the factory via
    /// <c>(Func&lt;TimeProvider?, ISegmentStorage&lt;int,int&gt;&gt;)factoryObj</c>.
    /// </remarks>
    public static IEnumerable<object[]> AllStrategies()
    {
        // SnapshotAppendBufferStorage with a tiny append buffer so normalization fires early.
        Func<TimeProvider?, ISegmentStorage<int, int>> snapshotFactory =
            tp => new SnapshotAppendBufferStorage<int, int>(appendBufferSize: 2, tp);
        yield return new object[] { (object)snapshotFactory, "Snapshot" };

        // LinkedListStrideIndexStorage with a tiny append buffer and stride = 2.
        Func<TimeProvider?, ISegmentStorage<int, int>> linkedListFactory =
            tp => new LinkedListStrideIndexStorage<int, int>(appendBufferSize: 2, stride: 2, tp);
        yield return new object[] { (object)linkedListFactory, "LinkedList" };
    }

    // -------------------------------------------------------------------------
    // Count Tests
    // -------------------------------------------------------------------------

    #region Count Tests

    [Theory]
    [MemberData(nameof(AllStrategies))]
    public void Count_WhenEmpty_ReturnsZero(object factoryObj, string strategyName)
    {
        // ARRANGE
        _ = strategyName;
        var factory = (Func<TimeProvider?, ISegmentStorage<int, int>>)factoryObj;
        var storage = factory(null);

        // ASSERT
        Assert.Equal(0, storage.Count);
    }

    [Theory]
    [MemberData(nameof(AllStrategies))]
    public void Count_AfterTryAdd_IncrementsPerStoredSegment(object factoryObj, string strategyName)
    {
        // ARRANGE
        _ = strategyName;
        var factory = (Func<TimeProvider?, ISegmentStorage<int, int>>)factoryObj;
        var storage = factory(null);

        // ACT
        storage.TryAdd(MakeSegment(0, 9));
        storage.TryAdd(MakeSegment(20, 29));

        // ASSERT
        Assert.Equal(2, storage.Count);
    }

    [Theory]
    [MemberData(nameof(AllStrategies))]
    public void Count_AfterTryRemove_Decrements(object factoryObj, string strategyName)
    {
        // ARRANGE
        _ = strategyName;
        var factory = (Func<TimeProvider?, ISegmentStorage<int, int>>)factoryObj;
        var storage = factory(null);
        var seg = MakeSegment(0, 9);
        storage.TryAdd(seg);
        storage.TryAdd(MakeSegment(20, 29));

        // ACT
        storage.TryRemove(seg);

        // ASSERT
        Assert.Equal(1, storage.Count);
    }

    [Theory]
    [MemberData(nameof(AllStrategies))]
    public void Count_AfterTryRemoveSameSegmentTwice_DecrementsOnlyOnce(object factoryObj, string strategyName)
    {
        // ARRANGE
        _ = strategyName;
        var factory = (Func<TimeProvider?, ISegmentStorage<int, int>>)factoryObj;
        var storage = factory(null);
        var seg = MakeSegment(0, 9);
        storage.TryAdd(seg);

        // ACT — second Remove is a no-op (VPC.T.1)
        storage.TryRemove(seg);
        storage.TryRemove(seg);

        // ASSERT
        Assert.Equal(0, storage.Count);
    }

    #endregion

    // -------------------------------------------------------------------------
    // TryAdd / VPC.C.3 Tests
    // -------------------------------------------------------------------------

    #region TryAdd — VPC.C.3 Tests

    [Theory]
    [MemberData(nameof(AllStrategies))]
    public void TryAdd_WithNoOverlap_ReturnsTrueAndStoresSegment(object factoryObj, string strategyName)
    {
        // ARRANGE
        _ = strategyName;
        var factory = (Func<TimeProvider?, ISegmentStorage<int, int>>)factoryObj;
        var storage = factory(null);
        var seg = MakeSegment(0, 9);

        // ACT
        var result = storage.TryAdd(seg);

        // ASSERT
        Assert.True(result);
        Assert.Equal(1, storage.Count);
    }

    [Theory]
    [MemberData(nameof(AllStrategies))]
    public void TryAdd_WithExactOverlap_ReturnsFalseAndDoesNotIncreaseCount(object factoryObj, string strategyName)
    {
        // ARRANGE
        _ = strategyName;
        var factory = (Func<TimeProvider?, ISegmentStorage<int, int>>)factoryObj;
        var storage = factory(null);
        storage.TryAdd(MakeSegment(0, 9));

        // ACT — attempt to add a segment with the same range (VPC.C.3)
        var result = storage.TryAdd(MakeSegment(0, 9));

        // ASSERT
        Assert.False(result);
        Assert.Equal(1, storage.Count);
    }

    [Theory]
    [MemberData(nameof(AllStrategies))]
    public void TryAdd_WithPartialOverlap_ReturnsFalseAndDoesNotIncreaseCount(object factoryObj, string strategyName)
    {
        // ARRANGE
        _ = strategyName;
        var factory = (Func<TimeProvider?, ISegmentStorage<int, int>>)factoryObj;
        var storage = factory(null);
        storage.TryAdd(MakeSegment(0, 20));

        // ACT — [10, 30] overlaps [0, 20] (VPC.C.3)
        var result = storage.TryAdd(MakeSegment(10, 30));

        // ASSERT
        Assert.False(result);
        Assert.Equal(1, storage.Count);
    }

    [Theory]
    [MemberData(nameof(AllStrategies))]
    public void TryAdd_AdjacentSegment_Succeeds(object factoryObj, string strategyName)
    {
        // ARRANGE — [0, 9] and [10, 19] are adjacent but do not share any domain point
        _ = strategyName;
        var factory = (Func<TimeProvider?, ISegmentStorage<int, int>>)factoryObj;
        var storage = factory(null);
        storage.TryAdd(MakeSegment(0, 9));

        // ACT
        var result = storage.TryAdd(MakeSegment(10, 19));

        // ASSERT
        Assert.True(result);
        Assert.Equal(2, storage.Count);
    }

    #endregion

    // -------------------------------------------------------------------------
    // TryAddRange / VPC.C.3 Tests
    // -------------------------------------------------------------------------

    #region TryAddRange — VPC.C.3 Tests

    [Theory]
    [MemberData(nameof(AllStrategies))]
    public void TryAddRange_EmptyInput_ReturnsEmptyAndDoesNotChangeCount(object factoryObj, string strategyName)
    {
        // ARRANGE
        _ = strategyName;
        var factory = (Func<TimeProvider?, ISegmentStorage<int, int>>)factoryObj;
        var storage = factory(null);

        // ACT
        var stored = storage.TryAddRange([]);

        // ASSERT
        Assert.Empty(stored);
        Assert.Equal(0, storage.Count);
    }

    [Theory]
    [MemberData(nameof(AllStrategies))]
    public void TryAddRange_NonOverlappingSegments_AllStored(object factoryObj, string strategyName)
    {
        // ARRANGE
        _ = strategyName;
        var factory = (Func<TimeProvider?, ISegmentStorage<int, int>>)factoryObj;
        var storage = factory(null);
        var input = new[]
        {
            MakeSegment(0, 9),
            MakeSegment(20, 29),
            MakeSegment(40, 49),
        };

        // ACT
        var stored = storage.TryAddRange(input);

        // ASSERT
        Assert.Equal(3, stored.Length);
        Assert.Equal(3, storage.Count);
    }

    [Theory]
    [MemberData(nameof(AllStrategies))]
    public void TryAddRange_OverlapsExistingSegment_OverlappingOneSkipped(object factoryObj, string strategyName)
    {
        // ARRANGE — [10, 20] already in storage; [15, 25] overlaps it
        _ = strategyName;
        var factory = (Func<TimeProvider?, ISegmentStorage<int, int>>)factoryObj;
        var storage = factory(null);
        storage.TryAdd(MakeSegment(10, 20));

        var input = new[]
        {
            MakeSegment(0, 9),     // no overlap — should be stored
            MakeSegment(15, 25),   // overlaps [10, 20] — should be skipped (VPC.C.3)
            MakeSegment(30, 39),   // no overlap — should be stored
        };

        // ACT
        var stored = storage.TryAddRange(input);

        // ASSERT
        Assert.Equal(2, stored.Length);
        Assert.Equal(3, storage.Count); // 1 pre-existing + 2 new
        Assert.DoesNotContain(input[1], stored);
    }

    [Theory]
    [MemberData(nameof(AllStrategies))]
    public void TryAddRange_IntraBatchOverlap_AllAcceptedBecausePeersNotYetVisible(object factoryObj, string strategyName)
    {
        // ARRANGE — [10, 20] and [15, 25] overlap each other (intra-batch).
        // VPC.C.3 is enforced against already-stored segments; intra-batch overlap between
        // incoming segments is NOT detected because AddRangeCore is called after all validation,
        // so peers are not yet visible to FindIntersecting during the validation loop.
        // Both strategies store all three segments when the storage is empty beforehand.
        _ = strategyName;
        var factory = (Func<TimeProvider?, ISegmentStorage<int, int>>)factoryObj;
        var storage = factory(null);
        var seg1 = MakeSegment(10, 20);
        var seg2 = MakeSegment(15, 25);
        var seg3 = MakeSegment(30, 39);

        // ACT
        var stored = storage.TryAddRange([seg1, seg2, seg3]);

        // ASSERT — intra-batch overlap is NOT caught (peers not yet in storage during validation);
        // all three are accepted because none overlaps anything already stored.
        Assert.Equal(3, stored.Length);
        Assert.Equal(3, storage.Count);
    }

    [Theory]
    [MemberData(nameof(AllStrategies))]
    public void TryAddRange_UnsortedInput_SegmentsAreStored(object factoryObj, string strategyName)
    {
        // ARRANGE — pass in reverse order; base sorts before VPC.C.3 check
        _ = strategyName;
        var factory = (Func<TimeProvider?, ISegmentStorage<int, int>>)factoryObj;
        var storage = factory(null);
        var input = new[]
        {
            MakeSegment(40, 49),
            MakeSegment(0, 9),
            MakeSegment(20, 29),
        };

        // ACT
        var stored = storage.TryAddRange(input);

        // ASSERT
        Assert.Equal(3, stored.Length);
        Assert.Equal(3, storage.Count);
    }

    [Theory]
    [MemberData(nameof(AllStrategies))]
    public void TryAddRange_AllOverlapExisting_ReturnsEmptyAndCountUnchanged(object factoryObj, string strategyName)
    {
        // ARRANGE — storage already has [5, 15]; try to add [5, 10] and [10, 15]
        _ = strategyName;
        var factory = (Func<TimeProvider?, ISegmentStorage<int, int>>)factoryObj;
        var storage = factory(null);
        storage.TryAdd(MakeSegment(5, 15));

        // ACT
        var stored = storage.TryAddRange([MakeSegment(5, 10), MakeSegment(10, 15)]);

        // ASSERT
        Assert.Empty(stored);
        Assert.Equal(1, storage.Count);
    }

    #endregion

    // -------------------------------------------------------------------------
    // TryRemove / VPC.T.1 Tests
    // -------------------------------------------------------------------------

    #region TryRemove — VPC.T.1 Tests

    [Theory]
    [MemberData(nameof(AllStrategies))]
    public void TryRemove_LiveSegment_ReturnsTrueAndMarksRemoved(object factoryObj, string strategyName)
    {
        // ARRANGE
        _ = strategyName;
        var factory = (Func<TimeProvider?, ISegmentStorage<int, int>>)factoryObj;
        var storage = factory(null);
        var seg = MakeSegment(0, 9);
        storage.TryAdd(seg);

        // ACT
        var result = storage.TryRemove(seg);

        // ASSERT
        Assert.True(result);
        Assert.True(seg.IsRemoved);
    }

    [Theory]
    [MemberData(nameof(AllStrategies))]
    public void TryRemove_AlreadyRemovedSegment_ReturnsFalse(object factoryObj, string strategyName)
    {
        // ARRANGE
        _ = strategyName;
        var factory = (Func<TimeProvider?, ISegmentStorage<int, int>>)factoryObj;
        var storage = factory(null);
        var seg = MakeSegment(0, 9);
        storage.TryAdd(seg);
        storage.TryRemove(seg); // first removal

        // ACT — VPC.T.1: second removal must be a no-op
        var result = storage.TryRemove(seg);

        // ASSERT
        Assert.False(result);
    }

    [Theory]
    [MemberData(nameof(AllStrategies))]
    public void TryRemove_DoesNotAffectOtherSegments(object factoryObj, string strategyName)
    {
        // ARRANGE
        _ = strategyName;
        var factory = (Func<TimeProvider?, ISegmentStorage<int, int>>)factoryObj;
        var storage = factory(null);
        var seg1 = MakeSegment(0, 9);
        var seg2 = MakeSegment(20, 29);
        storage.TryAdd(seg1);
        storage.TryAdd(seg2);

        // ACT
        storage.TryRemove(seg1);

        // ASSERT
        Assert.True(seg1.IsRemoved);
        Assert.False(seg2.IsRemoved);
        Assert.Equal(1, storage.Count);
    }

    #endregion

    // -------------------------------------------------------------------------
    // TryGetRandomSegment — retry/filter contract
    // -------------------------------------------------------------------------

    #region TryGetRandomSegment — filter contract

    [Theory]
    [MemberData(nameof(AllStrategies))]
    public void TryGetRandomSegment_WhenEmpty_ReturnsNull(object factoryObj, string strategyName)
    {
        // ARRANGE
        _ = strategyName;
        var factory = (Func<TimeProvider?, ISegmentStorage<int, int>>)factoryObj;
        var storage = factory(null);

        // ASSERT
        Assert.Null(storage.TryGetRandomSegment());
    }

    [Theory]
    [MemberData(nameof(AllStrategies))]
    public void TryGetRandomSegment_NeverReturnsRemovedSegment(object factoryObj, string strategyName)
    {
        // ARRANGE
        _ = strategyName;
        var factory = (Func<TimeProvider?, ISegmentStorage<int, int>>)factoryObj;
        var storage = factory(null);
        var removed = MakeSegment(0, 9);
        var live = MakeSegment(20, 29);
        storage.TryAdd(removed);
        storage.TryAdd(live);
        storage.TryRemove(removed);

        // ACT — sample many times
        for (var i = 0; i < 200; i++)
        {
            var result = storage.TryGetRandomSegment();
            Assert.NotSame(removed, result);
        }
    }

    [Theory]
    [MemberData(nameof(AllStrategies))]
    public void TryGetRandomSegment_NeverReturnsExpiredSegment(object factoryObj, string strategyName)
    {
        // ARRANGE — add one segment that has already expired and one live segment
        _ = strategyName;
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var factory = (Func<TimeProvider?, ISegmentStorage<int, int>>)factoryObj;
        var storage = factory(fakeTime);

        var expiredSeg = MakeSegment(0, 9, expiresAt: fakeTime.GetUtcNow().UtcTicks - 1);
        var liveSeg = MakeSegment(20, 29);
        storage.TryAdd(expiredSeg);
        storage.TryAdd(liveSeg);

        // ACT — sample many times
        for (var i = 0; i < 200; i++)
        {
            var result = storage.TryGetRandomSegment();
            Assert.NotSame(expiredSeg, result);
        }
    }

    [Theory]
    [MemberData(nameof(AllStrategies))]
    public void TryGetRandomSegment_WhenAllRemovedAndNoLive_ReturnsNull(object factoryObj, string strategyName)
    {
        // ARRANGE
        _ = strategyName;
        var factory = (Func<TimeProvider?, ISegmentStorage<int, int>>)factoryObj;
        var storage = factory(null);
        var seg = MakeSegment(0, 9);
        storage.TryAdd(seg);
        storage.TryRemove(seg);

        // ASSERT — no live segments; after exhausting retries the base returns null
        // Note: with a single removed segment in the pool, SampleRandomCore will keep returning it
        // and the base will exhaust all RandomRetryLimit attempts and return null.
        Assert.Null(storage.TryGetRandomSegment());
    }

    #endregion

    // -------------------------------------------------------------------------
    // TryNormalize — threshold check
    // -------------------------------------------------------------------------

    #region TryNormalize — threshold

    [Theory]
    [MemberData(nameof(AllStrategies))]
    public void TryNormalize_BelowThreshold_ReturnsFalse(object factoryObj, string strategyName)
    {
        // ARRANGE — appendBufferSize is 2; add only 1 segment (below threshold)
        _ = strategyName;
        var factory = (Func<TimeProvider?, ISegmentStorage<int, int>>)factoryObj;
        var storage = factory(null);
        storage.TryAdd(MakeSegment(0, 9));

        // ACT
        var result = storage.TryNormalize(out _);

        // ASSERT
        Assert.False(result);
    }

    [Theory]
    [MemberData(nameof(AllStrategies))]
    public void TryNormalize_AtThreshold_ReturnsTrueAndSegmentsStillFindable(object factoryObj, string strategyName)
    {
        // ARRANGE — appendBufferSize is 2; add exactly 2 segments to reach threshold
        _ = strategyName;
        var factory = (Func<TimeProvider?, ISegmentStorage<int, int>>)factoryObj;
        var storage = factory(null);
        var seg1 = MakeSegment(0, 9);
        var seg2 = MakeSegment(20, 29);
        storage.TryAdd(seg1);
        storage.TryAdd(seg2);

        // ACT
        var result = storage.TryNormalize(out _);

        // ASSERT
        Assert.True(result);
        Assert.NotEmpty(storage.FindIntersecting(TestHelpers.CreateRange(0, 9)));
        Assert.NotEmpty(storage.FindIntersecting(TestHelpers.CreateRange(20, 29)));
    }

    [Theory]
    [MemberData(nameof(AllStrategies))]
    public void TryNormalize_DiscoveresTtlExpiredSegments_ReturnsThemInOutParam(object factoryObj, string strategyName)
    {
        // ARRANGE — one segment with a past TTL, one live; trigger normalization
        _ = strategyName;
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var factory = (Func<TimeProvider?, ISegmentStorage<int, int>>)factoryObj;
        var storage = factory(fakeTime);

        var expiredSeg = MakeSegment(0, 9, expiresAt: fakeTime.GetUtcNow().UtcTicks - 1);
        storage.TryAdd(expiredSeg);
        storage.TryAdd(MakeSegment(20, 29)); // second add reaches threshold (bufferSize=2)

        // ACT
        var normalized = storage.TryNormalize(out var expiredSegments);

        // ASSERT
        Assert.True(normalized);
        Assert.NotNull(expiredSegments);
        Assert.Contains(expiredSeg, expiredSegments);
    }

    [Theory]
    [MemberData(nameof(AllStrategies))]
    public void TryNormalize_AfterNormalization_SubsequentCallBelowThreshold_ReturnsFalse(object factoryObj, string strategyName)
    {
        // ARRANGE — fill to threshold, normalize, then check without adding more
        _ = strategyName;
        var factory = (Func<TimeProvider?, ISegmentStorage<int, int>>)factoryObj;
        var storage = factory(null);
        storage.TryAdd(MakeSegment(0, 9));
        storage.TryAdd(MakeSegment(20, 29));
        storage.TryNormalize(out _);

        // ACT — threshold counter was reset by normalization; no new adds since
        var result = storage.TryNormalize(out _);

        // ASSERT
        Assert.False(result);
    }

    #endregion

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    #region Helpers

    private static CachedSegment<int, int> MakeSegment(int start, int end, long? expiresAt = null)
    {
        var range = TestHelpers.CreateRange(start, end);
        return new CachedSegment<int, int>(range, new ReadOnlyMemory<int>(new int[end - start + 1]))
        {
            ExpiresAt = expiresAt,
        };
    }

    #endregion
}
