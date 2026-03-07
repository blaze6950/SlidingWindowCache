using Intervals.NET.Caching.VisitedPlaces.Core;
using Intervals.NET.Caching.VisitedPlaces.Infrastructure.Storage;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure.Helpers;

namespace Intervals.NET.Caching.VisitedPlaces.Unit.Tests.Storage;

/// <summary>
/// Unit tests for <see cref="LinkedListStrideIndexStorage{TRange,TData}"/>.
/// Covers Count, Add, Remove, GetAllSegments, FindIntersecting, stride normalization.
/// </summary>
public sealed class LinkedListStrideIndexStorageTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_WithDefaultStride_DoesNotThrow()
    {
        // ACT
        var exception = Record.Exception(() => new LinkedListStrideIndexStorage<int, int>());

        // ASSERT
        Assert.Null(exception);
    }

    [Fact]
    public void Constructor_WithValidStride_DoesNotThrow()
    {
        // ACT
        var exception = Record.Exception(() => new LinkedListStrideIndexStorage<int, int>(stride: 4));

        // ASSERT
        Assert.Null(exception);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Constructor_WithInvalidStride_ThrowsArgumentOutOfRangeException(int stride)
    {
        // ACT
        var exception = Record.Exception(() => new LinkedListStrideIndexStorage<int, int>(stride));

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
        storage.Remove(seg);

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
        storage.Remove(seg1);
        storage.Remove(seg2);

        // ASSERT
        Assert.Equal(0, storage.Count);
    }

    #endregion

    #region Add / GetAllSegments Tests

    [Fact]
    public void GetAllSegments_WhenEmpty_ReturnsEmptyList()
    {
        // ARRANGE
        var storage = new LinkedListStrideIndexStorage<int, int>();

        // ASSERT
        Assert.Empty(storage.GetAllSegments());
    }

    [Fact]
    public void GetAllSegments_AfterAdding_ContainsAddedSegment()
    {
        // ARRANGE
        var storage = new LinkedListStrideIndexStorage<int, int>();
        var seg = AddSegment(storage, 0, 9);

        // ACT
        var all = storage.GetAllSegments();

        // ASSERT
        Assert.Contains(seg, all);
    }

    [Fact]
    public void GetAllSegments_AfterRemove_DoesNotContainRemovedSegment()
    {
        // ARRANGE
        var storage = new LinkedListStrideIndexStorage<int, int>();
        var seg1 = AddSegment(storage, 0, 9);
        var seg2 = AddSegment(storage, 20, 29);

        // ACT
        storage.Remove(seg1);
        var all = storage.GetAllSegments();

        // ASSERT
        Assert.DoesNotContain(seg1, all);
        Assert.Contains(seg2, all);
    }

    [Fact]
    public void GetAllSegments_ReturnsSortedByRangeStart()
    {
        // ARRANGE — add segments out of order
        var storage = new LinkedListStrideIndexStorage<int, int>();
        var seg3 = AddSegment(storage, 40, 49);
        var seg1 = AddSegment(storage, 0, 9);
        var seg2 = AddSegment(storage, 20, 29);

        // ACT
        var all = storage.GetAllSegments();

        // ASSERT — list is sorted by Start
        Assert.Equal(3, all.Count);
        Assert.Equal(0, (int)all[0].Range.Start);
        Assert.Equal(20, (int)all[1].Range.Start);
        Assert.Equal(40, (int)all[2].Range.Start);
    }

    [Fact]
    public void GetAllSegments_AfterAddingMoreThanStrideAppendBufferSize_ContainsAll()
    {
        // ARRANGE — StrideAppendBufferSize is 8; add 10 to trigger normalization
        var storage = new LinkedListStrideIndexStorage<int, int>(stride: 4);
        var segments = new List<CachedSegment<int, int>>();

        for (var i = 0; i < 10; i++)
        {
            segments.Add(AddSegment(storage, i * 10, i * 10 + 5));
        }

        // ACT
        var all = storage.GetAllSegments();

        // ASSERT
        Assert.Equal(10, all.Count);
        foreach (var seg in segments)
        {
            Assert.Contains(seg, all);
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
        // ARRANGE — add >8 segments to trigger normalization (StrideAppendBufferSize=8)
        var storage = new LinkedListStrideIndexStorage<int, int>(stride: 4);
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
        storage.Remove(seg);

        // ACT
        var result = storage.FindIntersecting(TestHelpers.CreateRange(0, 9));

        // ASSERT
        Assert.DoesNotContain(seg, result);
    }

    [Fact]
    public void FindIntersecting_WithManySegments_ReturnsAllIntersecting()
    {
        // ARRANGE — use small stride to exercise stride index; add 20 segments
        var storage = new LinkedListStrideIndexStorage<int, int>(stride: 4);
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
        // ARRANGE — add fewer than 8 (StrideAppendBufferSize) segments so no normalization occurs
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
        storage.Remove(toRemove);

        // Normalization already ran on the 8th add above (before Remove).
        // Now add 8 more to trigger a second normalization, which should physically unlink toRemove.
        for (var i = 10; i < 18; i++)
        {
            AddSegment(storage, i * 10, i * 10 + 5);
        }

        // ASSERT — toRemove no longer in GetAllSegments after second normalization
        var all = storage.GetAllSegments();
        Assert.DoesNotContain(toRemove, all);
    }

    [Fact]
    public void NormalizationTriggered_ManyAddsWithRemoves_CountRemainConsistent()
    {
        // ARRANGE — interleave adds and removes to exercise normalization across multiple cycles
        var storage = new LinkedListStrideIndexStorage<int, int>(stride: 4);
        var added = new List<CachedSegment<int, int>>();

        for (var i = 0; i < 20; i++)
        {
            added.Add(AddSegment(storage, i * 10, i * 10 + 5));
        }

        // Remove half
        for (var i = 0; i < 10; i++)
        {
            storage.Remove(added[i]);
        }

        // ASSERT
        Assert.Equal(10, storage.Count);
        var all = storage.GetAllSegments();
        Assert.Equal(10, all.Count);

        for (var i = 0; i < 10; i++)
        {
            Assert.DoesNotContain(added[i], all);
        }

        for (var i = 10; i < 20; i++)
        {
            Assert.Contains(added[i], all);
        }
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

    #endregion
}
