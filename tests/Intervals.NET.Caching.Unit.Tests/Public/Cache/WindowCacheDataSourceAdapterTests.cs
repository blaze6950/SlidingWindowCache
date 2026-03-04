using Intervals.NET.Domain.Default.Numeric;
using Moq;
using Intervals.NET.Caching.Infrastructure.Collections;
using Intervals.NET.Caching.Public;
using Intervals.NET.Caching.Public.Cache;
using Intervals.NET.Caching.Public.Dto;

namespace Intervals.NET.Caching.Unit.Tests.Public.Cache;

/// <summary>
/// Unit tests for <see cref="WindowCacheDataSourceAdapter{TRange,TData,TDomain}"/>.
/// Validates the adapter's contract: correct conversion of <see cref="RangeResult{TRange,TData}"/>
/// to <see cref="RangeChunk{TRange,TData}"/>, boundary semantics, cancellation propagation,
/// and exception forwarding. Uses a mocked <see cref="IWindowCache{TRange,TData,TDomain}"/> to
/// isolate the adapter from any real cache implementation.
/// </summary>
public sealed class WindowCacheDataSourceAdapterTests
{
    #region Test Infrastructure

    private static Mock<IWindowCache<int, int, IntegerFixedStepDomain>> CreateCacheMock() => new(MockBehavior.Strict);

    private static WindowCacheDataSourceAdapter<int, int, IntegerFixedStepDomain> CreateAdapter(
        IWindowCache<int, int, IntegerFixedStepDomain> cache)
        => new(cache);

    private static Intervals.NET.Range<int> MakeRange(int start, int end)
        => Intervals.NET.Factories.Range.Closed<int>(start, end);

    private static RangeResult<int, int> MakeResult(int start, int end)
    {
        var range = MakeRange(start, end);
        var data = new ReadOnlyMemory<int>(Enumerable.Range(start, end - start + 1).ToArray());
        return new RangeResult<int, int>(range, data, CacheInteraction.FullHit);
    }

    #endregion

    #region Constructor Tests

    [Fact]
    public void Constructor_WithNullCache_ThrowsArgumentNullException()
    {
        // ACT
        var exception = Record.Exception(() =>
            new WindowCacheDataSourceAdapter<int, int, IntegerFixedStepDomain>(null!));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentNullException>(exception);
        Assert.Contains("innerCache", ((ArgumentNullException)exception).ParamName);
    }

    [Fact]
    public void Constructor_WithValidCache_DoesNotThrow()
    {
        // ARRANGE
        var mock = CreateCacheMock();

        // ACT
        var exception = Record.Exception(() => CreateAdapter(mock.Object));

        // ASSERT
        Assert.Null(exception);
    }

    #endregion

    #region FetchAsync — Data Conversion Tests

    [Fact]
    public async Task FetchAsync_WithFullResult_ReturnsChunkWithSameRange()
    {
        // ARRANGE
        var mock = CreateCacheMock();
        var range = MakeRange(100, 110);
        var result = MakeResult(100, 110);

        mock.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

        var adapter = CreateAdapter(mock.Object);

        // ACT
        var chunk = await adapter.FetchAsync(range, CancellationToken.None);

        // ASSERT
        Assert.NotNull(chunk);
        Assert.Equal(result.Range, chunk.Range);
    }

    [Fact]
    public async Task FetchAsync_WithFullResult_ReturnsChunkWithCorrectData()
    {
        // ARRANGE
        var mock = CreateCacheMock();
        var range = MakeRange(100, 105);
        var result = MakeResult(100, 105);
        var adapter = CreateAdapter(mock.Object);

        mock.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

        // ACT
        var chunk = await adapter.FetchAsync(range, CancellationToken.None);

        // ASSERT
        var chunkData = chunk.Data.ToArray();
        var expectedData = result.Data.ToArray();
        Assert.Equal(expectedData.Length, chunkData.Length);
        Assert.Equal(expectedData, chunkData);
    }

    [Fact]
    public async Task FetchAsync_DataIsLazyEnumerable_NotEagerCopy()
    {
        // ARRANGE — adapter wraps ReadOnlyMemory lazily; no intermediate array is allocated
        var mock = CreateCacheMock();
        var range = MakeRange(1, 5);
        var innerArray = new[] { 1, 2, 3, 4, 5 };
        var result = new RangeResult<int, int>(range, new ReadOnlyMemory<int>(innerArray), CacheInteraction.FullHit);
        var adapter = CreateAdapter(mock.Object);

        mock.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

        // ACT
        var chunk = await adapter.FetchAsync(range, CancellationToken.None);

        // ASSERT — Data is a lazy ReadOnlyMemoryEnumerable, not a materialized copy
        Assert.IsType<ReadOnlyMemoryEnumerable<int>>(chunk.Data);
        Assert.Equal(innerArray, chunk.Data.ToArray());
    }

    [Fact]
    public async Task FetchAsync_DataEnumeratesFromMemory_ReflectsContentAtEnumerationTime()
    {
        // ARRANGE — lazy enumeration reads from the captured ReadOnlyMemory backing array;
        // mutations to the source array before enumeration are visible (lazy semantics)
        var mock = CreateCacheMock();
        var range = MakeRange(1, 5);
        var innerArray = new[] { 1, 2, 3, 4, 5 };
        var result = new RangeResult<int, int>(range, new ReadOnlyMemory<int>(innerArray), CacheInteraction.FullHit);
        var adapter = CreateAdapter(mock.Object);

        mock.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

        // ACT — fetch the chunk but do NOT enumerate yet
        var chunk = await adapter.FetchAsync(range, CancellationToken.None);

        // Mutate the source array before enumeration
        innerArray[0] = 999;

        // Enumerate now — lazy read picks up the mutation (expected: 999, not 1)
        var enumeratedData = chunk.Data.ToArray();

        // ASSERT
        Assert.Equal(999, enumeratedData[0]);
        Assert.Equal(2, enumeratedData[1]);
    }

    [Fact]
    public async Task FetchAsync_CallsGetDataAsyncOnce()
    {
        // ARRANGE
        var mock = CreateCacheMock();
        var range = MakeRange(10, 20);
        var result = MakeResult(10, 20);
        var adapter = CreateAdapter(mock.Object);

        mock.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

        // ACT
        await adapter.FetchAsync(range, CancellationToken.None);

        // ASSERT
        mock.Verify(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FetchAsync_PassesCorrectRangeToGetDataAsync()
    {
        // ARRANGE
        var mock = CreateCacheMock();
        var requestedRange = MakeRange(200, 300);
        var result = MakeResult(200, 300);
        Intervals.NET.Range<int>? capturedRange = null;
        var adapter = CreateAdapter(mock.Object);

        mock.Setup(c => c.GetDataAsync(It.IsAny<Intervals.NET.Range<int>>(), It.IsAny<CancellationToken>()))
            .Returns<Intervals.NET.Range<int>, CancellationToken>((r, _) =>
            {
                capturedRange = r;
                return ValueTask.FromResult(result);
            });

        // ACT
        await adapter.FetchAsync(requestedRange, CancellationToken.None);

        // ASSERT
        Assert.Equal(requestedRange, capturedRange);
    }

    #endregion

    #region FetchAsync — Boundary Semantics Tests

    [Fact]
    public async Task FetchAsync_WithNullRangeResult_ReturnsChunkWithNullRange()
    {
        // ARRANGE — inner cache returns null range (out-of-bounds boundary miss)
        var mock = CreateCacheMock();
        var range = MakeRange(9000, 9999);
        var boundaryResult = new RangeResult<int, int>(null, ReadOnlyMemory<int>.Empty, CacheInteraction.FullMiss);
        var adapter = CreateAdapter(mock.Object);

        mock.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .ReturnsAsync(boundaryResult);

        // ACT
        var chunk = await adapter.FetchAsync(range, CancellationToken.None);

        // ASSERT
        Assert.Null(chunk.Range);
    }

    [Fact]
    public async Task FetchAsync_WithNullRangeResult_ReturnsEmptyData()
    {
        // ARRANGE
        var mock = CreateCacheMock();
        var range = MakeRange(9000, 9999);
        var boundaryResult = new RangeResult<int, int>(null, ReadOnlyMemory<int>.Empty, CacheInteraction.FullMiss);
        var adapter = CreateAdapter(mock.Object);

        mock.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .ReturnsAsync(boundaryResult);

        // ACT
        var chunk = await adapter.FetchAsync(range, CancellationToken.None);

        // ASSERT
        Assert.Empty(chunk.Data);
    }

    [Fact]
    public async Task FetchAsync_WithTruncatedRangeResult_ReturnsChunkWithTruncatedRange()
    {
        // ARRANGE — inner cache returns a truncated range (partial boundary)
        var mock = CreateCacheMock();
        var requestedRange = MakeRange(900, 1100);
        var truncatedRange = MakeRange(900, 999);           // truncated at upper bound
        var truncatedData = new ReadOnlyMemory<int>(Enumerable.Range(900, 100).ToArray());
        var truncatedResult = new RangeResult<int, int>(truncatedRange, truncatedData, CacheInteraction.PartialHit);
        var adapter = CreateAdapter(mock.Object);

        mock.Setup(c => c.GetDataAsync(requestedRange, It.IsAny<CancellationToken>()))
            .ReturnsAsync(truncatedResult);

        // ACT
        var chunk = await adapter.FetchAsync(requestedRange, CancellationToken.None);

        // ASSERT
        Assert.Equal(truncatedRange, chunk.Range);
        Assert.Equal(100, chunk.Data.Count());
    }

    #endregion

    #region FetchAsync — Cancellation Propagation Tests

    [Fact]
    public async Task FetchAsync_PropagatesCancellationTokenToGetDataAsync()
    {
        // ARRANGE
        var mock = CreateCacheMock();
        var range = MakeRange(10, 20);
        var result = MakeResult(10, 20);
        var cts = new CancellationTokenSource();
        var capturedToken = CancellationToken.None;
        var adapter = CreateAdapter(mock.Object);

        mock.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .Returns<Intervals.NET.Range<int>, CancellationToken>((_, ct) =>
            {
                capturedToken = ct;
                return ValueTask.FromResult(result);
            });

        // ACT
        await adapter.FetchAsync(range, cts.Token);

        // ASSERT
        Assert.Equal(cts.Token, capturedToken);
    }

    [Fact]
    public async Task FetchAsync_WhenCancelled_PropagatesOperationCanceledException()
    {
        // ARRANGE
        var mock = CreateCacheMock();
        var range = MakeRange(10, 20);
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var adapter = CreateAdapter(mock.Object);

        mock.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        // ACT
        var exception = await Record.ExceptionAsync(
            async () => await adapter.FetchAsync(range, cts.Token));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<OperationCanceledException>(exception);
    }

    #endregion

    #region FetchAsync — Exception Propagation Tests

    [Fact]
    public async Task FetchAsync_WhenGetDataAsyncThrows_PropagatesException()
    {
        // ARRANGE
        var mock = CreateCacheMock();
        var range = MakeRange(10, 20);
        var expectedException = new InvalidOperationException("Inner cache failed");
        var adapter = CreateAdapter(mock.Object);

        mock.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .ThrowsAsync(expectedException);

        // ACT
        var exception = await Record.ExceptionAsync(
            async () => await adapter.FetchAsync(range, CancellationToken.None));

        // ASSERT
        Assert.NotNull(exception);
        Assert.Same(expectedException, exception);
    }

    [Fact]
    public async Task FetchAsync_WhenGetDataAsyncThrowsObjectDisposedException_Propagates()
    {
        // ARRANGE
        var mock = CreateCacheMock();
        var range = MakeRange(10, 20);
        var adapter = CreateAdapter(mock.Object);

        mock.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ObjectDisposedException("inner-cache"));

        // ACT
        var exception = await Record.ExceptionAsync(
            async () => await adapter.FetchAsync(range, CancellationToken.None));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ObjectDisposedException>(exception);
    }

    #endregion

    #region IDataSource Contract Tests

    [Fact]
    public async Task FetchAsync_ImplementsIDataSourceInterface()
    {
        // ARRANGE — verify via interface reference
        var mock = CreateCacheMock();
        var range = MakeRange(10, 20);
        var result = MakeResult(10, 20);

        mock.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

        IDataSource<int, int> dataSource = CreateAdapter(mock.Object);

        // ACT
        var chunk = await dataSource.FetchAsync(range, CancellationToken.None);

        // ASSERT
        Assert.NotNull(chunk);
        Assert.Equal(result.Range, chunk.Range);
    }

    [Fact]
    public async Task BatchFetchAsync_UsesDefaultParallelImplementation()
    {
        // ARRANGE — the default batch FetchAsync calls single-range FetchAsync in parallel
        var mock = CreateCacheMock();
        var range1 = MakeRange(1, 5);
        var range2 = MakeRange(100, 105);
        var result1 = MakeResult(1, 5);
        var result2 = MakeResult(100, 105);

        mock.Setup(c => c.GetDataAsync(range1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(result1);
        mock.Setup(c => c.GetDataAsync(range2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(result2);

        IDataSource<int, int> dataSource = CreateAdapter(mock.Object);
        var ranges = new[] { range1, range2 };

        // ACT
        var chunks = (await dataSource.FetchAsync(ranges, CancellationToken.None)).ToArray();

        // ASSERT
        Assert.Equal(2, chunks.Length);
        mock.Verify(c => c.GetDataAsync(range1, It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(c => c.GetDataAsync(range2, It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion
}
