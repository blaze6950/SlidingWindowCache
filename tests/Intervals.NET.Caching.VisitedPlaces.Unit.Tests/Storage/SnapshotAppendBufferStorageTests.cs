using Intervals.NET.Caching.VisitedPlaces.Core;
using Intervals.NET.Caching.VisitedPlaces.Infrastructure.Storage;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure.Helpers;

namespace Intervals.NET.Caching.VisitedPlaces.Unit.Tests.Storage;

/// <summary>
/// Unit tests for <see cref="SnapshotAppendBufferStorage{TRange,TData}"/>.
/// Covers Add, Remove, Count, FindIntersecting, GetAllSegments.
/// </summary>
public sealed class SnapshotAppendBufferStorageTests
{
    #region Count Tests

    [Fact]
    public void Count_WhenEmpty_ReturnsZero()
    {
        // ARRANGE
        var storage = new SnapshotAppendBufferStorage<int, int>();

        // ASSERT
        Assert.Equal(0, storage.Count);
    }

    [Fact]
    public void Count_AfterAddingSegments_ReturnsCorrectCount()
    {
        // ARRANGE
        var storage = new SnapshotAppendBufferStorage<int, int>();
        AddSegment(storage, 0, 9);
        AddSegment(storage, 20, 29);

        // ASSERT
        Assert.Equal(2, storage.Count);
    }

    [Fact]
    public void Count_AfterRemovingSegment_DecrementsCorrectly()
    {
        // ARRANGE
        var storage = new SnapshotAppendBufferStorage<int, int>();
        var seg = AddSegment(storage, 0, 9);
        AddSegment(storage, 20, 29);

        // ACT
        storage.Remove(seg);

        // ASSERT
        Assert.Equal(1, storage.Count);
    }

    #endregion

    #region Add / GetAllSegments Tests

    [Fact]
    public void GetAllSegments_WhenEmpty_ReturnsEmptyList()
    {
        // ARRANGE
        var storage = new SnapshotAppendBufferStorage<int, int>();

        // ASSERT
        Assert.Empty(storage.GetAllSegments());
    }

    [Fact]
    public void GetAllSegments_AfterAdding_ContainsAddedSegment()
    {
        // ARRANGE
        var storage = new SnapshotAppendBufferStorage<int, int>();
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
        var storage = new SnapshotAppendBufferStorage<int, int>();
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
    public void GetAllSegments_AfterAddingMoreThanAppendBufferSize_ContainsAll()
    {
        // ARRANGE — AppendBufferSize is 8; add 10 to trigger normalization
        var storage = new SnapshotAppendBufferStorage<int, int>();
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
        var storage = new SnapshotAppendBufferStorage<int, int>();
        var range = TestHelpers.CreateRange(0, 10);

        // ASSERT
        Assert.Empty(storage.FindIntersecting(range));
    }

    [Fact]
    public void FindIntersecting_WithExactMatch_ReturnsSegment()
    {
        // ARRANGE
        var storage = new SnapshotAppendBufferStorage<int, int>();
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
        var storage = new SnapshotAppendBufferStorage<int, int>();
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
        var storage = new SnapshotAppendBufferStorage<int, int>();
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
        var storage = new SnapshotAppendBufferStorage<int, int>();
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
        // ARRANGE — add >8 segments to trigger normalization
        var storage = new SnapshotAppendBufferStorage<int, int>();
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
        var storage = new SnapshotAppendBufferStorage<int, int>();
        var seg = AddSegment(storage, 0, 9);
        storage.Remove(seg);

        // ACT
        var result = storage.FindIntersecting(TestHelpers.CreateRange(0, 9));

        // ASSERT
        Assert.DoesNotContain(seg, result);
    }

    #endregion

    #region Helpers

    private static CachedSegment<int, int> AddSegment(
        SnapshotAppendBufferStorage<int, int> storage,
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
