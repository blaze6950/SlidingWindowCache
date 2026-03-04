using Intervals.NET.Domain.Default.Numeric;
using Moq;
using Intervals.NET.Caching.Public;
using Intervals.NET.Caching.Public.Dto;
using Intervals.NET.Caching.Public.Extensions;

namespace Intervals.NET.Caching.Unit.Tests.Public.Extensions;

/// <summary>
/// Unit tests for <see cref="WindowCacheConsistencyExtensions.GetDataAndWaitForIdleAsync{TRange,TData,TDomain}"/>
/// and <see cref="WindowCacheConsistencyExtensions.GetDataAndWaitOnMissAsync{TRange,TData,TDomain}"/>.
/// Validates the composition contracts, conditional idle-wait behaviour, result passthrough,
/// cancellation propagation, and exception semantics.
/// Uses mocked <see cref="IWindowCache{TRange, TData, TDomain}"/> to isolate the extension methods
/// from any real cache implementation.
/// </summary>
public sealed class WindowCacheConsistencyExtensionsTests
{
    #region Test Infrastructure

    private static Mock<IWindowCache<int, int, IntegerFixedStepDomain>> CreateMock() => new(MockBehavior.Strict);

    private static Intervals.NET.Range<int> CreateRange(int start, int end)
        => Intervals.NET.Factories.Range.Closed<int>(start, end);

    private static RangeResult<int, int> CreateRangeResult(int start, int end,
        CacheInteraction interaction = CacheInteraction.FullHit)
    {
        var range = CreateRange(start, end);
        var data = new ReadOnlyMemory<int>(Enumerable.Range(start, end - start + 1).ToArray());
        return new RangeResult<int, int>(range, data, interaction);
    }

    private static RangeResult<int, int> CreateNullRangeResult(CacheInteraction interaction) =>
        new(null, ReadOnlyMemory<int>.Empty, interaction);

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
        var nullRangeResult = new RangeResult<int, int>(null, ReadOnlyMemory<int>.Empty, CacheInteraction.FullMiss);

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
    public async Task GetDataAndWaitForIdleAsync_WaitForIdleAsyncCancelled_ReturnsResultGracefully()
    {
        // ARRANGE — cancelling during the idle wait must NOT discard the obtained data;
        // the method degrades gracefully to eventual consistency and returns the result.
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

        // ASSERT — no exception; result returned gracefully
        Assert.Null(exception);
    }

    [Fact]
    public async Task GetDataAndWaitForIdleAsync_WaitForIdleAsyncCancelled_ReturnsOriginalData()
    {
        // ARRANGE — the returned result must be identical to what GetDataAsync produced,
        // preserving Range, Data, and CacheInteraction unchanged.
        var mock = CreateMock();
        var range = CreateRange(100, 110);
        var expectedResult = CreateRangeResult(100, 110, CacheInteraction.FullHit);
        var cts = new CancellationTokenSource();

        mock.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);

        mock.Setup(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        // ACT
        var result = await mock.Object.GetDataAndWaitForIdleAsync(range, cts.Token);

        // ASSERT — same Range, Data, and CacheInteraction as GetDataAsync returned
        Assert.Equal(expectedResult.Range, result.Range);
        Assert.True(expectedResult.Data.Span.SequenceEqual(result.Data.Span));
        Assert.Equal(expectedResult.CacheInteraction, result.CacheInteraction);
    }

    [Fact]
    public async Task GetDataAndWaitForIdleAsync_WaitForIdleAsyncCancelled_DoesNotCallWaitForIdleTwice()
    {
        // ARRANGE — on cancellation, the method must not retry WaitForIdleAsync;
        // it must be called exactly once and then give up gracefully.
        var mock = CreateMock();
        var range = CreateRange(100, 110);
        var expectedResult = CreateRangeResult(100, 110);
        var cts = new CancellationTokenSource();

        mock.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);

        mock.Setup(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        // ACT
        await mock.Object.GetDataAndWaitForIdleAsync(range, cts.Token);

        // ASSERT — WaitForIdleAsync called exactly once, no retry
        mock.Verify(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()), Times.Once);
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
        var cacheInterface = mock.Object;
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

    // =========================================================================
    // GetDataAndWaitOnMissAsync — Hybrid Consistency Mode Tests
    // =========================================================================

    #region GetDataAndWaitOnMissAsync — Conditional Wait Behaviour Tests

    [Fact]
    public async Task GetDataAndWaitOnMissAsync_FullHit_DoesNotWaitForIdle()
    {
        // ARRANGE — full hit: cache was already warm; no idle wait should occur
        var mock = CreateMock();
        var range = CreateRange(100, 110);
        var fullHitResult = CreateRangeResult(100, 110, CacheInteraction.FullHit);

        mock.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fullHitResult);

        // WaitForIdleAsync is NOT set up — MockBehavior.Strict will fail if called

        // ACT
        var result = await mock.Object.GetDataAndWaitOnMissAsync(range, CancellationToken.None);

        // ASSERT
        Assert.Equal(fullHitResult.Range, result.Range);
        mock.Verify(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetDataAndWaitOnMissAsync_FullMiss_WaitsForIdle()
    {
        // ARRANGE — full miss: range had no overlap with cache; idle wait must occur
        var mock = CreateMock();
        var range = CreateRange(5000, 5100);
        var fullMissResult = CreateRangeResult(5000, 5100, CacheInteraction.FullMiss);

        mock.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fullMissResult);

        mock.Setup(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // ACT
        var result = await mock.Object.GetDataAndWaitOnMissAsync(range, CancellationToken.None);

        // ASSERT
        Assert.Equal(fullMissResult.Range, result.Range);
        mock.Verify(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetDataAndWaitOnMissAsync_PartialHit_WaitsForIdle()
    {
        // ARRANGE — partial hit: some segments were missing; idle wait must occur
        var mock = CreateMock();
        var range = CreateRange(90, 120);
        var partialHitResult = CreateRangeResult(90, 120, CacheInteraction.PartialHit);

        mock.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .ReturnsAsync(partialHitResult);

        mock.Setup(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // ACT
        var result = await mock.Object.GetDataAndWaitOnMissAsync(range, CancellationToken.None);

        // ASSERT
        Assert.Equal(partialHitResult.Range, result.Range);
        mock.Verify(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetDataAndWaitOnMissAsync_FullMiss_WaitsForIdleExactlyOnce()
    {
        // ARRANGE
        var mock = CreateMock();
        var range = CreateRange(1, 10);
        var fullMissResult = CreateRangeResult(1, 10, CacheInteraction.FullMiss);

        mock.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fullMissResult);

        mock.Setup(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // ACT
        await mock.Object.GetDataAndWaitOnMissAsync(range, CancellationToken.None);

        // ASSERT — WaitForIdleAsync called exactly once, not zero, not twice
        mock.Verify(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetDataAndWaitOnMissAsync_ReturnsResultFromGetDataAsync_OnFullMiss()
    {
        // ARRANGE — returned RangeResult must be the exact object from GetDataAsync
        var mock = CreateMock();
        var range = CreateRange(200, 300);
        var expectedResult = CreateRangeResult(200, 300, CacheInteraction.FullMiss);

        mock.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);

        mock.Setup(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // ACT
        var result = await mock.Object.GetDataAndWaitOnMissAsync(range, CancellationToken.None);

        // ASSERT — same range, same data, same CacheInteraction
        Assert.Equal(expectedResult.Range, result.Range);
        Assert.Equal(expectedResult.Data.Length, result.Data.Length);
        Assert.True(expectedResult.Data.Span.SequenceEqual(result.Data.Span));
        Assert.Equal(CacheInteraction.FullMiss, result.CacheInteraction);
    }

    [Fact]
    public async Task GetDataAndWaitOnMissAsync_ReturnsResultFromGetDataAsync_OnFullHit()
    {
        // ARRANGE — on full hit, result is passed through without WaitForIdleAsync
        var mock = CreateMock();
        var range = CreateRange(50, 60);
        var expectedResult = CreateRangeResult(50, 60, CacheInteraction.FullHit);

        mock.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);

        // WaitForIdleAsync NOT set up — MockBehavior.Strict guards against any call

        // ACT
        var result = await mock.Object.GetDataAndWaitOnMissAsync(range, CancellationToken.None);

        // ASSERT
        Assert.Equal(expectedResult.Range, result.Range);
        Assert.Equal(CacheInteraction.FullHit, result.CacheInteraction);
    }

    [Fact]
    public async Task GetDataAndWaitOnMissAsync_NullRange_FullMiss_WaitsForIdle()
    {
        // ARRANGE — physical boundary miss: DataSource returned null (out-of-bounds)
        // Still a FullMiss interaction; idle wait must occur so the cache rebalances
        var mock = CreateMock();
        var range = CreateRange(9000, 9999);
        var boundaryMissResult = CreateNullRangeResult(CacheInteraction.FullMiss);

        mock.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .ReturnsAsync(boundaryMissResult);

        mock.Setup(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // ACT
        var result = await mock.Object.GetDataAndWaitOnMissAsync(range, CancellationToken.None);

        // ASSERT
        Assert.Null(result.Range);
        Assert.Equal(CacheInteraction.FullMiss, result.CacheInteraction);
        mock.Verify(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region GetDataAndWaitOnMissAsync — Cancellation Token Propagation Tests

    [Fact]
    public async Task GetDataAndWaitOnMissAsync_PropagatesCancellationTokenToGetDataAsync()
    {
        // ARRANGE
        var mock = CreateMock();
        var range = CreateRange(100, 110);
        var fullMissResult = CreateRangeResult(100, 110, CacheInteraction.FullMiss);
        var cts = new CancellationTokenSource();
        var capturedToken = CancellationToken.None;

        mock.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .Returns<Intervals.NET.Range<int>, CancellationToken>((_, ct) =>
            {
                capturedToken = ct;
                return ValueTask.FromResult(fullMissResult);
            });

        mock.Setup(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // ACT
        await mock.Object.GetDataAndWaitOnMissAsync(range, cts.Token);

        // ASSERT
        Assert.Equal(cts.Token, capturedToken);
    }

    [Fact]
    public async Task GetDataAndWaitOnMissAsync_PropagatesCancellationTokenToWaitForIdleAsync_OnMiss()
    {
        // ARRANGE
        var mock = CreateMock();
        var range = CreateRange(100, 110);
        var fullMissResult = CreateRangeResult(100, 110, CacheInteraction.FullMiss);
        var cts = new CancellationTokenSource();
        var capturedToken = CancellationToken.None;

        mock.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fullMissResult);

        mock.Setup(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct =>
            {
                capturedToken = ct;
                return Task.CompletedTask;
            });

        // ACT
        await mock.Object.GetDataAndWaitOnMissAsync(range, cts.Token);

        // ASSERT
        Assert.Equal(cts.Token, capturedToken);
    }

    #endregion

    #region GetDataAndWaitOnMissAsync — Exception Propagation Tests

    [Fact]
    public async Task GetDataAndWaitOnMissAsync_GetDataAsyncThrows_ExceptionPropagatesWithoutCallingWaitForIdleAsync()
    {
        // ARRANGE
        var mock = CreateMock();
        var range = CreateRange(100, 110);
        var expectedException = new InvalidOperationException("GetDataAsync failed");

        mock.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .ThrowsAsync(expectedException);

        // WaitForIdleAsync NOT set up — MockBehavior.Strict guards against any call

        // ACT
        var exception = await Record.ExceptionAsync(
            async () => await mock.Object.GetDataAndWaitOnMissAsync(range, CancellationToken.None));

        // ASSERT
        Assert.NotNull(exception);
        Assert.Same(expectedException, exception);
        mock.Verify(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetDataAndWaitOnMissAsync_GetDataAsyncCancelled_Propagates()
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
            async () => await mock.Object.GetDataAndWaitOnMissAsync(range, cts.Token));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<OperationCanceledException>(exception);
        mock.Verify(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetDataAndWaitOnMissAsync_WaitForIdleAsyncCancelled_ReturnsResultGracefully()
    {
        // ARRANGE — cancelling the wait stops the wait, not the background rebalance;
        // the already-obtained result must be returned gracefully.
        var mock = CreateMock();
        var range = CreateRange(100, 110);
        var fullMissResult = CreateRangeResult(100, 110, CacheInteraction.FullMiss);
        var cts = new CancellationTokenSource();

        mock.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fullMissResult);

        mock.Setup(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        // ACT
        var exception = await Record.ExceptionAsync(
            async () => await mock.Object.GetDataAndWaitOnMissAsync(range, cts.Token));

        // ASSERT — no exception; WaitForIdleAsync was still attempted (for the FullMiss)
        Assert.Null(exception);
        mock.Verify(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetDataAndWaitOnMissAsync_WaitForIdleAsyncCancelled_ReturnsOriginalData()
    {
        // ARRANGE — the returned result must be identical to what GetDataAsync produced,
        // preserving Range, Data, and CacheInteraction unchanged.
        var mock = CreateMock();
        var range = CreateRange(200, 300);
        var expectedResult = CreateRangeResult(200, 300, CacheInteraction.FullMiss);
        var cts = new CancellationTokenSource();

        mock.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);

        mock.Setup(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        // ACT
        var result = await mock.Object.GetDataAndWaitOnMissAsync(range, cts.Token);

        // ASSERT — same Range, Data, and CacheInteraction as GetDataAsync returned
        Assert.Equal(expectedResult.Range, result.Range);
        Assert.True(expectedResult.Data.Span.SequenceEqual(result.Data.Span));
        Assert.Equal(CacheInteraction.FullMiss, result.CacheInteraction);
    }

    [Fact]
    public async Task GetDataAndWaitOnMissAsync_WaitForIdleAsyncCancelled_OnPartialHit_ReturnsResultGracefully()
    {
        // ARRANGE — graceful degradation must also work for PartialHit, not just FullMiss
        var mock = CreateMock();
        var range = CreateRange(90, 120);
        var partialHitResult = CreateRangeResult(90, 120, CacheInteraction.PartialHit);
        var cts = new CancellationTokenSource();

        mock.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .ReturnsAsync(partialHitResult);

        mock.Setup(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        // ACT
        var exception = await Record.ExceptionAsync(
            async () => await mock.Object.GetDataAndWaitOnMissAsync(range, cts.Token));

        // ASSERT — no exception; WaitForIdleAsync was still attempted (for the PartialHit)
        Assert.Null(exception);
        mock.Verify(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region GetDataAndWaitOnMissAsync — Call Order Tests

    [Fact]
    public async Task GetDataAndWaitOnMissAsync_OnFullMiss_CallsGetDataAsyncBeforeWaitForIdleAsync()
    {
        // ARRANGE
        var mock = CreateMock();
        var callOrder = new List<string>();
        var range = CreateRange(100, 110);
        var fullMissResult = CreateRangeResult(100, 110, CacheInteraction.FullMiss);

        mock.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callOrder.Add("GetDataAsync");
                return ValueTask.FromResult(fullMissResult);
            });

        mock.Setup(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callOrder.Add("WaitForIdleAsync");
                return Task.CompletedTask;
            });

        // ACT
        await mock.Object.GetDataAndWaitOnMissAsync(range, CancellationToken.None);

        // ASSERT
        Assert.Equal(2, callOrder.Count);
        Assert.Equal("GetDataAsync", callOrder[0]);
        Assert.Equal("WaitForIdleAsync", callOrder[1]);
    }

    [Fact]
    public async Task GetDataAndWaitOnMissAsync_OnFullHit_CallsOnlyGetDataAsync()
    {
        // ARRANGE
        var mock = CreateMock();
        var callOrder = new List<string>();
        var range = CreateRange(100, 110);
        var fullHitResult = CreateRangeResult(100, 110, CacheInteraction.FullHit);

        mock.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callOrder.Add("GetDataAsync");
                return ValueTask.FromResult(fullHitResult);
            });

        // WaitForIdleAsync NOT set up — MockBehavior.Strict guards against any call

        // ACT
        await mock.Object.GetDataAndWaitOnMissAsync(range, CancellationToken.None);

        // ASSERT
        Assert.Single(callOrder);
        Assert.Equal("GetDataAsync", callOrder[0]);
    }

    #endregion

    #region GetDataAndWaitOnMissAsync — CacheInteraction Property Tests

    [Theory]
    [InlineData(CacheInteraction.FullHit)]
    [InlineData(CacheInteraction.PartialHit)]
    [InlineData(CacheInteraction.FullMiss)]
    public async Task GetDataAndWaitOnMissAsync_PreservesCacheInteractionOnResult(CacheInteraction interaction)
    {
        // ARRANGE
        var mock = CreateMock();
        var range = CreateRange(100, 110);
        var sourceResult = CreateRangeResult(100, 110, interaction);

        mock.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .ReturnsAsync(sourceResult);

        if (interaction != CacheInteraction.FullHit)
        {
            mock.Setup(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        }

        // ACT
        var result = await mock.Object.GetDataAndWaitOnMissAsync(range, CancellationToken.None);

        // ASSERT — CacheInteraction is passed through unchanged
        Assert.Equal(interaction, result.CacheInteraction);
    }

    [Fact]
    public void RangeResult_CacheInteraction_IsAccessibleOnPublicRecord()
    {
        // ARRANGE — verify the property is publicly readable
        var range = CreateRange(1, 10);
        var data = new ReadOnlyMemory<int>(new[] { 1, 2, 3 });
        var result = new RangeResult<int, int>(range, data, CacheInteraction.PartialHit);

        // ASSERT
        Assert.Equal(CacheInteraction.PartialHit, result.CacheInteraction);
    }

    [Theory]
    [InlineData(CacheInteraction.FullHit)]
    [InlineData(CacheInteraction.PartialHit)]
    [InlineData(CacheInteraction.FullMiss)]
    public void RangeResult_CacheInteraction_RoundtripsAllValues(CacheInteraction interaction)
    {
        // ARRANGE
        var range = CreateRange(0, 1);
        var data = new ReadOnlyMemory<int>(new[] { 0, 1 });
        var result = new RangeResult<int, int>(range, data, interaction);

        // ASSERT
        Assert.Equal(interaction, result.CacheInteraction);
    }

    #endregion
}
