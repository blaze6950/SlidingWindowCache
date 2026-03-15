using Intervals.NET.Caching.VisitedPlaces.Core;
using Intervals.NET.Caching.VisitedPlaces.Infrastructure.Storage;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure.Helpers;

namespace Intervals.NET.Caching.VisitedPlaces.Unit.Tests.Storage;

/// <summary>
/// Unit tests for <see cref="LinkedListStrideIndexStorage{TRange,TData}"/>.
/// Covers Count, Add, TryRemove, TryGetRandomSegment, FindIntersecting, stride normalization.
/// </summary>
public sealed class LinkedListStrideIndexStorageTests
{
    /// <summary>
    /// Number of <see cref="ISegmentStorage{TRange,TData}.TryGetRandomSegment"/> calls used in
    /// statistical coverage assertions. With N segments and this many draws, the probability
    /// that any specific segment is never selected is (1 - 1/N)^Trials ≈ e^(-Trials/N).
    /// For N=10, Trials=1000: p(miss) ≈ e^(-100) ≈ 0 — effectively impossible.
    /// </summary>
    private const int StatisticalTrials = 1000;

    #region Constructor Tests

    [Fact]
    public void Constructor_WithDefaultParameters_DoesNotThrow()
    {
        // ACT
        var exception = Record.Exception(() => new LinkedListStrideIndexStorage<int, int>());

        // ASSERT
        Assert.Null(exception);
    }

    [Fact]
    public void Constructor_WithValidAppendBufferSizeAndStride_DoesNotThrow()
    {
        // ACT
        var exception = Record.Exception(
            () => new LinkedListStrideIndexStorage<int, int>(appendBufferSize: 4, stride: 4));

        // ASSERT
        Assert.Null(exception);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Constructor_WithInvalidAppendBufferSize_ThrowsArgumentOutOfRangeException(int appendBufferSize)
    {
        // ACT
        var exception = Record.Exception(
            () => new LinkedListStrideIndexStorage<int, int>(appendBufferSize, stride: 16));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentOutOfRangeException>(exception);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Constructor_WithInvalidStride_ThrowsArgumentOutOfRangeException(int stride)
    {
        // ACT
        var exception = Record.Exception(
            () => new LinkedListStrideIndexStorage<int, int>(appendBufferSize: 8, stride));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentOutOfRangeException>(exception);
    }

    #endregion

    #region Count Tests

    [Fact]
    public void Count_WhenEmpty_ReturnsZero()
    {
        // ARRANGE
        var storage = new LinkedListStrideIndexStorage<int, int>();

        // ASSERT
        Assert.Equal(0, storage.Count);
    }

    [Fact]
    public void Count_AfterAddingSegments_ReturnsCorrectCount()
    {
        // ARRANGE
        var storage = new LinkedListStrideIndexStorage<int, int>();
        AddSegment(storage, 0, 9);
        AddSegment(storage, 20, 29);

        // ASSERT
        Assert.Equal(2, storage.Count);
    }

    [Fact]
    public void Count_AfterRemovingSegment_DecrementsCorrectly()
    {
        // ARRANGE
        var storage = new LinkedListStrideIndexStorage<int, int>();
        var seg = AddSegment(storage, 0, 9);
        AddSegment(storage, 20, 29);

        // ACT
        storage.TryRemove(seg);

        // ASSERT
        Assert.Equal(1, storage.Count);
    }

    [Fact]
    public void Count_AfterAddAndRemoveAll_ReturnsZero()
    {
        // ARRANGE
        var storage = new LinkedListStrideIndexStorage<int, int>();
        var seg1 = AddSegment(storage, 0, 9);
        var seg2 = AddSegment(storage, 20, 29);

        // ACT
        storage.TryRemove(seg1);
        storage.TryRemove(seg2);

        // ASSERT
        Assert.Equal(0, storage.Count);
    }

    #endregion

    #region Add / TryGetRandomSegment Tests

    [Fact]
    public void TryGetRandomSegment_WhenEmpty_ReturnsNull()
    {
        // ARRANGE
        var storage = new LinkedListStrideIndexStorage<int, int>();

        // ASSERT — empty storage must return null every time
        for (var i = 0; i < 10; i++)
        {
            Assert.Null(storage.TryGetRandomSegment());
        }
    }

    [Fact]
    public void TryGetRandomSegment_AfterAdding_EventuallyReturnsAddedSegment()
    {
        // ARRANGE
        var storage = new LinkedListStrideIndexStorage<int, int>();
        var seg = AddSegment(storage, 0, 9);

        // ACT — with a single live segment, every non-null result must be that segment
        CachedSegment<int, int>? found = null;
        for (var i = 0; i < StatisticalTrials && found is null; i++)
        {
            found = storage.TryGetRandomSegment();
        }

        // ASSERT
        Assert.NotNull(found);
        Assert.Same(seg, found);
    }

    [Fact]
    public void TryGetRandomSegment_AfterRemove_NeverReturnsRemovedSegment()
    {
        // ARRANGE
        var storage = new LinkedListStrideIndexStorage<int, int>();
        var seg1 = AddSegment(storage, 0, 9);
        var seg2 = AddSegment(storage, 20, 29);

        // ACT
        storage.TryRemove(seg1);

        // ASSERT — seg1 must never be returned; seg2 must eventually be returned
        var foundSeg2 = false;
        for (var i = 0; i < StatisticalTrials; i++)
        {
            var result = storage.TryGetRandomSegment();
            Assert.NotSame(seg1, result); // removed segment must never appear
            if (result is not null && ReferenceEquals(result, seg2))
            {
                foundSeg2 = true;
            }
        }

        Assert.True(foundSeg2, "seg2 should have been returned at least once in 1000 trials");
    }

    [Fact]
    public void TryGetRandomSegment_AfterAddingMoreThanStrideAppendBufferSize_EventuallyReturnsAllSegments()
    {
        // ARRANGE — default AppendBufferSize is 8; add 10 to trigger normalization
        var storage = new LinkedListStrideIndexStorage<int, int>(appendBufferSize: 8, stride: 4);
        var segments = new List<CachedSegment<int, int>>();

        for (var i = 0; i < 10; i++)
        {
            segments.Add(AddSegment(storage, i * 10, i * 10 + 5));
        }

        // ACT — sample enough times for every segment to be returned at least once
        var seen = new HashSet<CachedSegment<int, int>>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < StatisticalTrials; i++)
        {
            var result = storage.TryGetRandomSegment();
            if (result is not null)
            {
                seen.Add(result);
            }
        }

        // ASSERT — every added segment must have been returned at least once
        Assert.Equal(10, seen.Count);
        foreach (var seg in segments)
        {
            Assert.Contains(seg, seen);
        }
    }

    #endregion

    #region FindIntersecting Tests

    [Fact]
    public void FindIntersecting_WhenNoSegments_ReturnsEmpty()
    {
        // ARRANGE
        var storage = new LinkedListStrideIndexStorage<int, int>();
        var range = TestHelpers.CreateRange(0, 10);

        // ASSERT
        Assert.Empty(storage.FindIntersecting(range));
    }

    [Fact]
    public void FindIntersecting_WithExactMatch_ReturnsSegment()
    {
        // ARRANGE
        var storage = new LinkedListStrideIndexStorage<int, int>();
        var seg = AddSegment(storage, 5, 15);

        // ACT
        var result = storage.FindIntersecting(TestHelpers.CreateRange(5, 15));

        // ASSERT
        Assert.Contains(seg, result);
    }

    [Fact]
    public void FindIntersecting_WithPartialOverlap_ReturnsSegment()
    {
        // ARRANGE
        var storage = new LinkedListStrideIndexStorage<int, int>();
        var seg = AddSegment(storage, 5, 15);

        // ACT — query [10, 20] overlaps [5, 15]
        var result = storage.FindIntersecting(TestHelpers.CreateRange(10, 20));

        // ASSERT
        Assert.Contains(seg, result);
    }

    [Fact]
    public void FindIntersecting_WithNonIntersectingRange_ReturnsEmpty()
    {
        // ARRANGE
        var storage = new LinkedListStrideIndexStorage<int, int>();
        AddSegment(storage, 0, 9);

        // ACT — query [20, 30] does not overlap [0, 9]
        var result = storage.FindIntersecting(TestHelpers.CreateRange(20, 30));

        // ASSERT
        Assert.Empty(result);
    }

    [Fact]
    public void FindIntersecting_WithMultipleSegments_ReturnsOnlyIntersecting()
    {
        // ARRANGE
        var storage = new LinkedListStrideIndexStorage<int, int>();
        var seg1 = AddSegment(storage, 0, 9);
        AddSegment(storage, 50, 59);  // no overlap with [5, 15]

        // ACT
        var result = storage.FindIntersecting(TestHelpers.CreateRange(5, 15));

        // ASSERT
        Assert.Contains(seg1, result);
        Assert.Single(result);
    }

    [Fact]
    public void FindIntersecting_AfterNormalization_StillFindsSegments()
    {
        // ARRANGE — add >8 segments to trigger normalization (default AppendBufferSize=8)
        var storage = new LinkedListStrideIndexStorage<int, int>(appendBufferSize: 8, stride: 4);
        for (var i = 0; i < 9; i++)
        {
            AddSegment(storage, i * 10, i * 10 + 5);
        }

        // ACT — query middle of the range
        var result = storage.FindIntersecting(TestHelpers.CreateRange(40, 45));

        // ASSERT
        Assert.NotEmpty(result);
    }

    [Fact]
    public void FindIntersecting_AfterRemove_DoesNotReturnRemovedSegment()
    {
        // ARRANGE
        var storage = new LinkedListStrideIndexStorage<int, int>();
        var seg = AddSegment(storage, 0, 9);
        storage.TryRemove(seg);

        // ACT
        var result = storage.FindIntersecting(TestHelpers.CreateRange(0, 9));

        // ASSERT
        Assert.DoesNotContain(seg, result);
    }

    [Fact]
    public void FindIntersecting_WithManySegments_ReturnsAllIntersecting()
    {
        // ARRANGE — use small stride to exercise stride index; add 20 segments
        var storage = new LinkedListStrideIndexStorage<int, int>(appendBufferSize: 8, stride: 4);
        var addedSegments = new List<CachedSegment<int, int>>();

        for (var i = 0; i < 20; i++)
        {
            addedSegments.Add(AddSegment(storage, i * 10, i * 10 + 5));
        }

        // ACT — query range that overlaps segments at [30,35], [40,45], [50,55]
        var result = storage.FindIntersecting(TestHelpers.CreateRange(32, 52));

        // ASSERT
        Assert.Equal(3, result.Count);
        Assert.Contains(addedSegments[3], result);  // [30,35]
        Assert.Contains(addedSegments[4], result);  // [40,45]
        Assert.Contains(addedSegments[5], result);  // [50,55]
    }

    [Fact]
    public void FindIntersecting_QueriedBeforeNormalization_FindsSegmentsInAppendBuffer()
    {
        // ARRANGE — add fewer than 8 (default AppendBufferSize) segments so no normalization occurs
        var storage = new LinkedListStrideIndexStorage<int, int>();
        var seg = AddSegment(storage, 10, 20);

        // ACT — query while segment is still in the stride append buffer
        var result = storage.FindIntersecting(TestHelpers.CreateRange(10, 20));

        // ASSERT
        Assert.Contains(seg, result);
    }

    #endregion

    #region Stride Normalization Tests

    [Fact]
    public void NormalizationTriggered_AfterEightAdds_CountRemainsCorrect()
    {
        // ARRANGE — add exactly 8 segments to trigger normalization on the 8th add
        var storage = new LinkedListStrideIndexStorage<int, int>();

        for (var i = 0; i < 8; i++)
        {
            AddSegment(storage, i * 10, i * 10 + 5);
        }

        // ASSERT — normalization should have run; count still correct
        Assert.Equal(8, storage.Count);
    }

    [Fact]
    public void NormalizationTriggered_SoftDeletedSegments_ArePhysicallyRemovedFromList()
    {
        // ARRANGE — add 7 segments, remove one, then add 1 more to trigger normalization
        var storage = new LinkedListStrideIndexStorage<int, int>();
        for (var i = 0; i < 7; i++)
        {
            AddSegment(storage, i * 10, i * 10 + 5);
        }

        var toRemove = AddSegment(storage, 200, 205); // 8th add — normalization fires
        storage.TryRemove(toRemove);

        // Normalization already ran on the 8th add above (before Remove).
        // Now add 8 more to trigger a second normalization, which should physically unlink toRemove.
        for (var i = 10; i < 18; i++)
        {
            AddSegment(storage, i * 10, i * 10 + 5);
        }

        // ASSERT — toRemove's range is no longer findable via FindIntersecting after normalization
        var found = storage.FindIntersecting(TestHelpers.CreateRange(200, 205));
        Assert.Empty(found);

        // ASSERT — Count reflects the correct live count (7 original + 8 new = 15)
        Assert.Equal(15, storage.Count);
    }

    [Fact]
    public void NormalizationTriggered_ManyAddsWithRemoves_CountRemainConsistent()
    {
        // ARRANGE — interleave adds and removes to exercise normalization across multiple cycles
        var storage = new LinkedListStrideIndexStorage<int, int>(appendBufferSize: 8, stride: 4);
        var added = new List<CachedSegment<int, int>>();

        for (var i = 0; i < 20; i++)
        {
            added.Add(AddSegment(storage, i * 10, i * 10 + 5));
        }

        // Remove half
        for (var i = 0; i < 10; i++)
        {
            storage.TryRemove(added[i]);
        }

        // ASSERT — Count is correct
        Assert.Equal(10, storage.Count);

        // ASSERT — removed segments are not findable
        for (var i = 0; i < 10; i++)
        {
            var start = i * 10;
            var found = storage.FindIntersecting(TestHelpers.CreateRange(start, start + 5));
            Assert.Empty(found);
        }

        // ASSERT — remaining segments are still findable
        for (var i = 10; i < 20; i++)
        {
            var start = i * 10;
            var found = storage.FindIntersecting(TestHelpers.CreateRange(start, start + 5));
            Assert.NotEmpty(found);
        }

        // ASSERT — statistical sampling covers all surviving segments
        var seen = new HashSet<CachedSegment<int, int>>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < StatisticalTrials; i++)
        {
            var result = storage.TryGetRandomSegment();
            if (result is not null)
            {
                seen.Add(result);
            }
        }

        Assert.Equal(10, seen.Count);
        for (var i = 10; i < 20; i++)
        {
            Assert.Contains(added[i], seen);
        }
    }

    #endregion

    #region AddRange Tests

    [Fact]
    public void AddRange_WithEmptyArray_DoesNotChangeCount()
    {
        // ARRANGE
        var storage = new LinkedListStrideIndexStorage<int, int>();

        // ACT
        storage.AddRange([]);

        // ASSERT
        Assert.Equal(0, storage.Count);
    }

    [Fact]
    public void AddRange_WithMultipleSegments_UpdatesCountCorrectly()
    {
        // ARRANGE
        var storage = new LinkedListStrideIndexStorage<int, int>();
        var segments = new[]
        {
            CreateSegment(0, 9),
            CreateSegment(20, 29),
            CreateSegment(40, 49),
        };

        // ACT
        storage.AddRange(segments);

        // ASSERT
        Assert.Equal(3, storage.Count);
    }

    [Fact]
    public void AddRange_WithMultipleSegments_AllSegmentsFoundByFindIntersecting()
    {
        // ARRANGE
        var storage = new LinkedListStrideIndexStorage<int, int>();
        var seg1 = CreateSegment(0, 9);
        var seg2 = CreateSegment(20, 29);
        var seg3 = CreateSegment(40, 49);

        // ACT
        storage.AddRange([seg1, seg2, seg3]);

        // ASSERT
        Assert.Single(storage.FindIntersecting(TestHelpers.CreateRange(0, 9)));
        Assert.Single(storage.FindIntersecting(TestHelpers.CreateRange(20, 29)));
        Assert.Single(storage.FindIntersecting(TestHelpers.CreateRange(40, 49)));
    }

    [Fact]
    public void AddRange_WithUnsortedInput_SegmentsAreStillFindable()
    {
        // ARRANGE — pass segments in reverse order to verify AddRange sorts internally
        var storage = new LinkedListStrideIndexStorage<int, int>();
        var seg1 = CreateSegment(40, 49);
        var seg2 = CreateSegment(0, 9);
        var seg3 = CreateSegment(20, 29);

        // ACT
        storage.AddRange([seg1, seg2, seg3]);

        // ASSERT — all three must be findable regardless of insertion order
        Assert.Single(storage.FindIntersecting(TestHelpers.CreateRange(0, 9)));
        Assert.Single(storage.FindIntersecting(TestHelpers.CreateRange(20, 29)));
        Assert.Single(storage.FindIntersecting(TestHelpers.CreateRange(40, 49)));
    }

    [Fact]
    public void AddRange_AfterExistingSegments_AllSegmentsFoundByFindIntersecting()
    {
        // ARRANGE — add two segments individually first, then bulk-add two more
        var storage = new LinkedListStrideIndexStorage<int, int>();
        AddSegment(storage, 0, 9);
        AddSegment(storage, 20, 29);

        var newSegments = new[]
        {
            CreateSegment(40, 49),
            CreateSegment(60, 69),
        };

        // ACT
        storage.AddRange(newSegments);

        // ASSERT — all four segments findable
        Assert.Equal(4, storage.Count);
        Assert.Single(storage.FindIntersecting(TestHelpers.CreateRange(0, 9)));
        Assert.Single(storage.FindIntersecting(TestHelpers.CreateRange(20, 29)));
        Assert.Single(storage.FindIntersecting(TestHelpers.CreateRange(40, 49)));
        Assert.Single(storage.FindIntersecting(TestHelpers.CreateRange(60, 69)));
    }

    [Fact]
    public void AddRange_NormalizesStrideIndexOnce_NotOncePerSegment()
    {
        // ARRANGE — use a stride threshold of 2 so normalization would fire after every 2 Add() calls;
        // AddRange with 4 segments should trigger exactly one NormalizeStrideIndex, not 4 separate ones.
        var storage = new LinkedListStrideIndexStorage<int, int>(appendBufferSize: 2, stride: 2);
        var segments = new[]
        {
            CreateSegment(0, 9),
            CreateSegment(20, 29),
            CreateSegment(40, 49),
            CreateSegment(60, 69),
        };

        // ACT — no exception means normalization completed without intermediate half-normalized states
        var exception = Record.Exception(() => storage.AddRange(segments));

        // ASSERT — all segments are findable after the single normalization pass
        Assert.Null(exception);
        Assert.Equal(4, storage.Count);
        Assert.Single(storage.FindIntersecting(TestHelpers.CreateRange(0, 9)));
        Assert.Single(storage.FindIntersecting(TestHelpers.CreateRange(20, 29)));
        Assert.Single(storage.FindIntersecting(TestHelpers.CreateRange(40, 49)));
        Assert.Single(storage.FindIntersecting(TestHelpers.CreateRange(60, 69)));
    }

    #endregion

    #region Helpers

    private static CachedSegment<int, int> AddSegment(
        LinkedListStrideIndexStorage<int, int> storage,
        int start,
        int end)
    {
        var range = TestHelpers.CreateRange(start, end);
        var segment = new CachedSegment<int, int>(
            range,
            new ReadOnlyMemory<int>(new int[end - start + 1]));
        storage.Add(segment);
        return segment;
    }

    /// <summary>
    /// Creates a <see cref="CachedSegment{TRange,TData}"/> without adding it to storage.
    /// Use this in <c>AddRange</c> tests to build the input array before calling
    /// <see cref="LinkedListStrideIndexStorage{TRange,TData}.AddRange"/>.
    /// </summary>
    private static CachedSegment<int, int> CreateSegment(int start, int end)
    {
        var range = TestHelpers.CreateRange(start, end);
        return new CachedSegment<int, int>(
            range,
            new ReadOnlyMemory<int>(new int[end - start + 1]));
    }

    #endregion
}
