using Intervals.NET.Domain.Default.Numeric;
using Moq;
using SlidingWindowCache.Public;
using SlidingWindowCache.Public.Dto;

namespace SlidingWindowCache.Unit.Tests.Public;

/// <summary>
/// Unit tests for <see cref="WindowCacheExtensions.GetDataAndWaitForIdleAsync{TRange,TData,TDomain}"/>.
/// Validates the composition contract: GetDataAsync followed by WaitForIdleAsync,
/// with correct result passthrough, cancellation propagation, and exception semantics.
/// Uses mocked <see cref="IWindowCache{TRange, TData, TDomain}"/> to isolate the extension method
/// from any real cache implementation.
/// </summary>
public sealed class WindowCacheExtensionsTests
{
    #region Test Infrastructure

    private static Mock<IWindowCache<int, int, IntegerFixedStepDomain>> CreateMock()
        => new Mock<IWindowCache<int, int, IntegerFixedStepDomain>>(MockBehavior.Strict);

    private static Intervals.NET.Range<int> CreateRange(int start, int end)
        => Intervals.NET.Factories.Range.Closed<int>(start, end);

    private static RangeResult<int, int> CreateRangeResult(int start, int end)
    {
        var range = CreateRange(start, end);
        var data = new ReadOnlyMemory<int>(Enumerable.Range(start, end - start + 1).ToArray());
        return new RangeResult<int, int>(range, data);
    }

    #endregion

    #region Composition Contract Tests

    [Fact]
    public async Task GetDataAndWaitForIdleAsync_CallsGetDataAsyncFirst()
    {
        // ARRANGE
        var mock = CreateMock();
        var callOrder = new List<string>();
        var range = CreateRange(100, 110);
        var expectedResult = CreateRangeResult(100, 110);

        mock.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callOrder.Add("GetDataAsync");
                return ValueTask.FromResult(expectedResult);
            });

        mock.Setup(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callOrder.Add("WaitForIdleAsync");
                return Task.CompletedTask;
            });

        // ACT
        await mock.Object.GetDataAndWaitForIdleAsync(range, CancellationToken.None);

        // ASSERT
        Assert.Equal(2, callOrder.Count);
        Assert.Equal("GetDataAsync", callOrder[0]);
        Assert.Equal("WaitForIdleAsync", callOrder[1]);
    }

    [Fact]
    public async Task GetDataAndWaitForIdleAsync_ReturnsResultFromGetDataAsync()
    {
        // ARRANGE
        var mock = CreateMock();
        var range = CreateRange(100, 110);
        var expectedResult = CreateRangeResult(100, 110);

        mock.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);

        mock.Setup(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // ACT
        var result = await mock.Object.GetDataAndWaitForIdleAsync(range, CancellationToken.None);

        // ASSERT
        Assert.Equal(expectedResult.Range, result.Range);
        Assert.Equal(expectedResult.Data.Length, result.Data.Length);
        Assert.True(expectedResult.Data.Span.SequenceEqual(result.Data.Span));
    }

    [Fact]
    public async Task GetDataAndWaitForIdleAsync_CallsBothMethodsExactlyOnce()
    {
        // ARRANGE
        var mock = CreateMock();
        var range = CreateRange(100, 110);
        var expectedResult = CreateRangeResult(100, 110);

        mock.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);

        mock.Setup(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // ACT
        await mock.Object.GetDataAndWaitForIdleAsync(range, CancellationToken.None);

        // ASSERT
        mock.Verify(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetDataAndWaitForIdleAsync_WithNullResultRange_ReturnsNullRange()
    {
        // ARRANGE
        var mock = CreateMock();
        var range = CreateRange(100, 110);
        var nullRangeResult = new RangeResult<int, int>(null, ReadOnlyMemory<int>.Empty);

        mock.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .ReturnsAsync(nullRangeResult);

        mock.Setup(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // ACT
        var result = await mock.Object.GetDataAndWaitForIdleAsync(range, CancellationToken.None);

        // ASSERT
        Assert.Null(result.Range);
        Assert.Equal(0, result.Data.Length);
    }

    #endregion

    #region Cancellation Token Propagation Tests

    [Fact]
    public async Task GetDataAndWaitForIdleAsync_PropagatesCancellationTokenToGetDataAsync()
    {
        // ARRANGE
        var mock = CreateMock();
        var range = CreateRange(100, 110);
        var expectedResult = CreateRangeResult(100, 110);
        var cts = new CancellationTokenSource();
        var capturedToken = CancellationToken.None;

        mock.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .Returns<Intervals.NET.Range<int>, CancellationToken>((_, ct) =>
            {
                capturedToken = ct;
                return ValueTask.FromResult(expectedResult);
            });

        mock.Setup(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // ACT
        await mock.Object.GetDataAndWaitForIdleAsync(range, cts.Token);

        // ASSERT
        Assert.Equal(cts.Token, capturedToken);
    }

    [Fact]
    public async Task GetDataAndWaitForIdleAsync_PropagatesCancellationTokenToWaitForIdleAsync()
    {
        // ARRANGE
        var mock = CreateMock();
        var range = CreateRange(100, 110);
        var expectedResult = CreateRangeResult(100, 110);
        var cts = new CancellationTokenSource();
        var capturedToken = CancellationToken.None;

        mock.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);

        mock.Setup(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct =>
            {
                capturedToken = ct;
                return Task.CompletedTask;
            });

        // ACT
        await mock.Object.GetDataAndWaitForIdleAsync(range, cts.Token);

        // ASSERT
        Assert.Equal(cts.Token, capturedToken);
    }

    [Fact]
    public async Task GetDataAndWaitForIdleAsync_UsesSameCancellationTokenForBothCalls()
    {
        // ARRANGE
        var mock = CreateMock();
        var range = CreateRange(100, 110);
        var expectedResult = CreateRangeResult(100, 110);
        var cts = new CancellationTokenSource();
        var capturedGetDataToken = CancellationToken.None;
        var capturedWaitToken = CancellationToken.None;

        mock.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .Returns<Intervals.NET.Range<int>, CancellationToken>((_, ct) =>
            {
                capturedGetDataToken = ct;
                return ValueTask.FromResult(expectedResult);
            });

        mock.Setup(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct =>
            {
                capturedWaitToken = ct;
                return Task.CompletedTask;
            });

        // ACT
        await mock.Object.GetDataAndWaitForIdleAsync(range, cts.Token);

        // ASSERT - Same token passed to both
        Assert.Equal(cts.Token, capturedGetDataToken);
        Assert.Equal(cts.Token, capturedWaitToken);
        Assert.Equal(capturedGetDataToken, capturedWaitToken);
    }

    [Fact]
    public async Task GetDataAndWaitForIdleAsync_DefaultCancellationToken_IsNone()
    {
        // ARRANGE
        var mock = CreateMock();
        var range = CreateRange(100, 110);
        var expectedResult = CreateRangeResult(100, 110);
        var capturedGetDataToken = new CancellationToken(true); // start with non-None value
        var capturedWaitToken = new CancellationToken(true);

        mock.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .Returns<Intervals.NET.Range<int>, CancellationToken>((_, ct) =>
            {
                capturedGetDataToken = ct;
                return ValueTask.FromResult(expectedResult);
            });

        mock.Setup(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct =>
            {
                capturedWaitToken = ct;
                return Task.CompletedTask;
            });

        // ACT — no cancellationToken argument (uses default)
        await mock.Object.GetDataAndWaitForIdleAsync(range);

        // ASSERT
        Assert.Equal(CancellationToken.None, capturedGetDataToken);
        Assert.Equal(CancellationToken.None, capturedWaitToken);
    }

    #endregion

    #region Exception Propagation Tests

    [Fact]
    public async Task GetDataAndWaitForIdleAsync_GetDataAsyncThrows_ExceptionPropagatesWithoutCallingWaitForIdleAsync()
    {
        // ARRANGE
        var mock = CreateMock();
        var range = CreateRange(100, 110);
        var expectedException = new InvalidOperationException("GetDataAsync failed");

        mock.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .ThrowsAsync(expectedException);

        // WaitForIdleAsync should NOT be set up — MockBehavior.Strict will throw if called

        // ACT
        var exception = await Record.ExceptionAsync(
            async () => await mock.Object.GetDataAndWaitForIdleAsync(range, CancellationToken.None));

        // ASSERT
        Assert.NotNull(exception);
        Assert.Same(expectedException, exception);
        mock.Verify(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetDataAndWaitForIdleAsync_GetDataAsyncThrowsObjectDisposedException_Propagates()
    {
        // ARRANGE
        var mock = CreateMock();
        var range = CreateRange(100, 110);

        mock.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ObjectDisposedException("cache"));

        // ACT
        var exception = await Record.ExceptionAsync(
            async () => await mock.Object.GetDataAndWaitForIdleAsync(range, CancellationToken.None));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ObjectDisposedException>(exception);
        mock.Verify(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetDataAndWaitForIdleAsync_GetDataAsyncCancelled_Propagates()
    {
        // ARRANGE
        var mock = CreateMock();
        var range = CreateRange(100, 110);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        mock.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        // ACT
        var exception = await Record.ExceptionAsync(
            async () => await mock.Object.GetDataAndWaitForIdleAsync(range, cts.Token));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<OperationCanceledException>(exception);
        mock.Verify(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetDataAndWaitForIdleAsync_WaitForIdleAsyncThrows_ExceptionPropagates()
    {
        // ARRANGE
        var mock = CreateMock();
        var range = CreateRange(100, 110);
        var expectedResult = CreateRangeResult(100, 110);
        var expectedException = new InvalidOperationException("WaitForIdleAsync failed");

        mock.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);

        mock.Setup(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(expectedException);

        // ACT
        var exception = await Record.ExceptionAsync(
            async () => await mock.Object.GetDataAndWaitForIdleAsync(range, CancellationToken.None));

        // ASSERT
        Assert.NotNull(exception);
        Assert.Same(expectedException, exception);
    }

    [Fact]
    public async Task GetDataAndWaitForIdleAsync_WaitForIdleAsyncCancelled_Propagates()
    {
        // ARRANGE
        var mock = CreateMock();
        var range = CreateRange(100, 110);
        var expectedResult = CreateRangeResult(100, 110);
        var cts = new CancellationTokenSource();

        mock.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);

        mock.Setup(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        // ACT
        var exception = await Record.ExceptionAsync(
            async () => await mock.Object.GetDataAndWaitForIdleAsync(range, cts.Token));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<OperationCanceledException>(exception);
    }

    [Fact]
    public async Task GetDataAndWaitForIdleAsync_WaitForIdleAsyncThrowsObjectDisposedException_Propagates()
    {
        // ARRANGE
        var mock = CreateMock();
        var range = CreateRange(100, 110);
        var expectedResult = CreateRangeResult(100, 110);

        mock.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);

        mock.Setup(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ObjectDisposedException("cache"));

        // ACT
        var exception = await Record.ExceptionAsync(
            async () => await mock.Object.GetDataAndWaitForIdleAsync(range, CancellationToken.None));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ObjectDisposedException>(exception);
    }

    #endregion

    #region Extension Method Target Tests

    [Fact]
    public async Task GetDataAndWaitForIdleAsync_WorksOnInterfaceReference()
    {
        // ARRANGE
        var mock = CreateMock();
        IWindowCache<int, int, IntegerFixedStepDomain> cacheInterface = mock.Object;
        var range = CreateRange(100, 110);
        var expectedResult = CreateRangeResult(100, 110);

        mock.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);

        mock.Setup(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // ACT — called via interface reference
        var result = await cacheInterface.GetDataAndWaitForIdleAsync(range, CancellationToken.None);

        // ASSERT
        Assert.Equal(expectedResult.Range, result.Range);
    }

    #endregion
}
