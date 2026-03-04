using Intervals.NET.Domain.Default.Numeric;
using Moq;
using Intervals.NET.Caching.Public;
using Intervals.NET.Caching.Public.Cache;
using Intervals.NET.Caching.Public.Configuration;
using Intervals.NET.Caching.Public.Dto;

namespace Intervals.NET.Caching.Unit.Tests.Public.Cache;

/// <summary>
/// Unit tests for <see cref="LayeredWindowCache{TRange,TData,TDomain}"/>.
/// Validates delegation to the outermost layer for data operations, correct layer count,
/// and disposal ordering. Uses mocked <see cref="IWindowCache{TRange,TData,TDomain}"/> instances
/// to isolate the wrapper from real cache behavior.
/// </summary>
public sealed class LayeredWindowCacheTests
{
    #region Test Infrastructure

    private static Mock<IWindowCache<int, int, IntegerFixedStepDomain>> CreateLayerMock() => new(MockBehavior.Strict);

    private static LayeredWindowCache<int, int, IntegerFixedStepDomain> CreateLayeredCache(
        params IWindowCache<int, int, IntegerFixedStepDomain>[] layers)
    {
        // The internal constructor is accessible via InternalsVisibleTo.
        // Integration tests use the builder with real caches; here we test the wrapper directly.
        return CreateLayeredCacheFromList(layers.ToList());
    }

    private static LayeredWindowCache<int, int, IntegerFixedStepDomain> CreateLayeredCacheFromList(
        IReadOnlyList<IWindowCache<int, int, IntegerFixedStepDomain>> layers)
    {
        // Instantiate via the internal constructor using the test project's InternalsVisibleTo access.
        return new LayeredWindowCache<int, int, IntegerFixedStepDomain>(layers);
    }

    private static Intervals.NET.Range<int> MakeRange(int start, int end)
        => Intervals.NET.Factories.Range.Closed<int>(start, end);

    private static RangeResult<int, int> MakeResult(int start, int end)
    {
        var range = MakeRange(start, end);
        var data = new ReadOnlyMemory<int>(Enumerable.Range(start, end - start + 1).ToArray());
        return new RangeResult<int, int>(range, data, CacheInteraction.FullHit);
    }

    #endregion

    #region LayerCount Tests

    [Fact]
    public void LayerCount_SingleLayer_ReturnsOne()
    {
        // ARRANGE
        var layer = CreateLayerMock();
        var cache = CreateLayeredCache(layer.Object);

        // ASSERT
        Assert.Equal(1, cache.LayerCount);
    }

    [Fact]
    public void LayerCount_TwoLayers_ReturnsTwo()
    {
        // ARRANGE
        var layer1 = CreateLayerMock();
        var layer2 = CreateLayerMock();
        var cache = CreateLayeredCache(layer1.Object, layer2.Object);

        // ASSERT
        Assert.Equal(2, cache.LayerCount);
    }

    [Fact]
    public void LayerCount_ThreeLayers_ReturnsThree()
    {
        // ARRANGE
        var layer1 = CreateLayerMock();
        var layer2 = CreateLayerMock();
        var layer3 = CreateLayerMock();
        var cache = CreateLayeredCache(layer1.Object, layer2.Object, layer3.Object);

        // ASSERT
        Assert.Equal(3, cache.LayerCount);
    }

    #endregion

    #region GetDataAsync Delegation Tests

    [Fact]
    public async Task GetDataAsync_DelegatesToOutermostLayer()
    {
        // ARRANGE
        var innerLayer = CreateLayerMock();
        var outerLayer = CreateLayerMock();
        var range = MakeRange(100, 110);
        var expectedResult = MakeResult(100, 110);

        outerLayer.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);

        var cache = CreateLayeredCache(innerLayer.Object, outerLayer.Object);

        // ACT
        var result = await cache.GetDataAsync(range, CancellationToken.None);

        // ASSERT
        Assert.Equal(expectedResult.Range, result.Range);
        outerLayer.Verify(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()), Times.Once);
        // Inner layer must NOT be called — outer layer is user-facing
        innerLayer.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetDataAsync_SingleLayer_DelegatesToThatLayer()
    {
        // ARRANGE
        var onlyLayer = CreateLayerMock();
        var range = MakeRange(1, 10);
        var expectedResult = MakeResult(1, 10);

        onlyLayer.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);

        var cache = CreateLayeredCache(onlyLayer.Object);

        // ACT
        var result = await cache.GetDataAsync(range, CancellationToken.None);

        // ASSERT
        Assert.Equal(expectedResult.Range, result.Range);
        onlyLayer.Verify(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetDataAsync_PropagatesCancellationToken()
    {
        // ARRANGE
        var outerLayer = CreateLayerMock();
        var range = MakeRange(10, 20);
        var cts = new CancellationTokenSource();
        var capturedToken = CancellationToken.None;
        var expectedResult = MakeResult(10, 20);

        outerLayer.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .Returns<Intervals.NET.Range<int>, CancellationToken>((_, ct) =>
            {
                capturedToken = ct;
                return ValueTask.FromResult(expectedResult);
            });

        var cache = CreateLayeredCache(outerLayer.Object);

        // ACT
        await cache.GetDataAsync(range, cts.Token);

        // ASSERT
        Assert.Equal(cts.Token, capturedToken);
    }

    [Fact]
    public async Task GetDataAsync_WhenOutermostLayerThrows_PropagatesException()
    {
        // ARRANGE
        var outerLayer = CreateLayerMock();
        var range = MakeRange(10, 20);
        var expectedException = new InvalidOperationException("Cache failed");

        outerLayer.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .ThrowsAsync(expectedException);

        var cache = CreateLayeredCache(outerLayer.Object);

        // ACT
        var exception = await Record.ExceptionAsync(
            async () => await cache.GetDataAsync(range, CancellationToken.None));

        // ASSERT
        Assert.Same(expectedException, exception);
    }

    #endregion

    #region WaitForIdleAsync Tests

    [Fact]
    public async Task WaitForIdleAsync_TwoLayers_AwaitsOuterLayer()
    {
        // ARRANGE
        var innerLayer = CreateLayerMock();
        var outerLayer = CreateLayerMock();
        var outerLayerWasCalled = false;

        innerLayer.Setup(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        outerLayer.Setup(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                outerLayerWasCalled = true;
                return Task.CompletedTask;
            });

        var cache = CreateLayeredCache(innerLayer.Object, outerLayer.Object);

        // ACT
        await cache.WaitForIdleAsync();

        // ASSERT
        Assert.True(outerLayerWasCalled);
        outerLayer.Verify(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WaitForIdleAsync_TwoLayers_AwaitsInnerLayer()
    {
        // ARRANGE
        var innerLayer = CreateLayerMock();
        var outerLayer = CreateLayerMock();
        var innerLayerWasCalled = false;

        innerLayer.Setup(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                innerLayerWasCalled = true;
                return Task.CompletedTask;
            });
        outerLayer.Setup(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var cache = CreateLayeredCache(innerLayer.Object, outerLayer.Object);

        // ACT
        await cache.WaitForIdleAsync();

        // ASSERT
        Assert.True(innerLayerWasCalled);
        innerLayer.Verify(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WaitForIdleAsync_TwoLayers_AwaitsOuterBeforeInner()
    {
        // ARRANGE
        var innerLayer = CreateLayerMock();
        var outerLayer = CreateLayerMock();
        var callOrder = new List<string>();

        innerLayer.Setup(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callOrder.Add("inner");
                return Task.CompletedTask;
            });
        outerLayer.Setup(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callOrder.Add("outer");
                return Task.CompletedTask;
            });

        var cache = CreateLayeredCache(innerLayer.Object, outerLayer.Object);

        // ACT
        await cache.WaitForIdleAsync();

        // ASSERT — outer must be awaited before inner
        Assert.Equal(2, callOrder.Count);
        Assert.Equal("outer", callOrder[0]);
        Assert.Equal("inner", callOrder[1]);
    }

    [Fact]
    public async Task WaitForIdleAsync_ThreeLayers_AwaitsAllInOuterToInnerOrder()
    {
        // ARRANGE
        var layer1 = CreateLayerMock(); // deepest (index 0)
        var layer2 = CreateLayerMock(); // middle  (index 1)
        var layer3 = CreateLayerMock(); // outer   (index 2)
        var callOrder = new List<string>();

        layer1.Setup(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()))
            .Returns(() => { callOrder.Add("L1"); return Task.CompletedTask; });
        layer2.Setup(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()))
            .Returns(() => { callOrder.Add("L2"); return Task.CompletedTask; });
        layer3.Setup(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()))
            .Returns(() => { callOrder.Add("L3"); return Task.CompletedTask; });

        var cache = CreateLayeredCache(layer1.Object, layer2.Object, layer3.Object);

        // ACT
        await cache.WaitForIdleAsync();

        // ASSERT — outermost (L3) first, then L2, then deepest (L1)
        Assert.Equal(new[] { "L3", "L2", "L1" }, callOrder);
    }

    [Fact]
    public async Task WaitForIdleAsync_SingleLayer_AwaitsIt()
    {
        // ARRANGE
        var onlyLayer = CreateLayerMock();
        var wasCalled = false;

        onlyLayer.Setup(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                wasCalled = true;
                return Task.CompletedTask;
            });

        var cache = CreateLayeredCache(onlyLayer.Object);

        // ACT
        await cache.WaitForIdleAsync();

        // ASSERT
        Assert.True(wasCalled);
        onlyLayer.Verify(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WaitForIdleAsync_PropagatesCancellationTokenToAllLayers()
    {
        // ARRANGE
        var innerLayer = CreateLayerMock();
        var outerLayer = CreateLayerMock();
        var cts = new CancellationTokenSource();
        var capturedTokens = new List<CancellationToken>();

        innerLayer.Setup(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct => { capturedTokens.Add(ct); return Task.CompletedTask; });
        outerLayer.Setup(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct => { capturedTokens.Add(ct); return Task.CompletedTask; });

        var cache = CreateLayeredCache(innerLayer.Object, outerLayer.Object);

        // ACT
        await cache.WaitForIdleAsync(cts.Token);

        // ASSERT — same token forwarded to both layers
        Assert.Equal(2, capturedTokens.Count);
        Assert.All(capturedTokens, t => Assert.Equal(cts.Token, t));
    }

    [Fact]
    public async Task WaitForIdleAsync_DefaultToken_IsNoneForAllLayers()
    {
        // ARRANGE
        var innerLayer = CreateLayerMock();
        var outerLayer = CreateLayerMock();
        var capturedTokens = new List<CancellationToken>();

        innerLayer.Setup(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct => { capturedTokens.Add(ct); return Task.CompletedTask; });
        outerLayer.Setup(c => c.WaitForIdleAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct => { capturedTokens.Add(ct); return Task.CompletedTask; });

        var cache = CreateLayeredCache(innerLayer.Object, outerLayer.Object);

        // ACT
        await cache.WaitForIdleAsync(); // default token

        // ASSERT
        Assert.Equal(2, capturedTokens.Count);
        Assert.All(capturedTokens, t => Assert.Equal(CancellationToken.None, t));
    }

    #endregion

    #region DisposeAsync Tests

    [Fact]
    public async Task DisposeAsync_SingleLayer_DisposesIt()
    {
        // ARRANGE
        var layer = CreateLayerMock();
        layer.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var cache = CreateLayeredCache(layer.Object);

        // ACT
        await cache.DisposeAsync();

        // ASSERT
        layer.Verify(c => c.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task DisposeAsync_TwoLayers_DisposesAllLayers()
    {
        // ARRANGE
        var innerLayer = CreateLayerMock();
        var outerLayer = CreateLayerMock();
        innerLayer.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
        outerLayer.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var cache = CreateLayeredCache(innerLayer.Object, outerLayer.Object);

        // ACT
        await cache.DisposeAsync();

        // ASSERT
        innerLayer.Verify(c => c.DisposeAsync(), Times.Once);
        outerLayer.Verify(c => c.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task DisposeAsync_TwoLayers_DisposesOutermostFirst()
    {
        // ARRANGE — outermost should be disposed before innermost
        var innerLayer = CreateLayerMock();
        var outerLayer = CreateLayerMock();
        var disposalOrder = new List<string>();

        innerLayer.Setup(c => c.DisposeAsync()).Returns(() =>
        {
            disposalOrder.Add("inner");
            return ValueTask.CompletedTask;
        });
        outerLayer.Setup(c => c.DisposeAsync()).Returns(() =>
        {
            disposalOrder.Add("outer");
            return ValueTask.CompletedTask;
        });

        var cache = CreateLayeredCache(innerLayer.Object, outerLayer.Object);

        // ACT
        await cache.DisposeAsync();

        // ASSERT
        Assert.Equal(2, disposalOrder.Count);
        Assert.Equal("outer", disposalOrder[0]);
        Assert.Equal("inner", disposalOrder[1]);
    }

    [Fact]
    public async Task DisposeAsync_ThreeLayers_DisposesOuterToInner()
    {
        // ARRANGE
        var layer1 = CreateLayerMock(); // deepest
        var layer2 = CreateLayerMock(); // middle
        var layer3 = CreateLayerMock(); // outermost
        var disposalOrder = new List<string>();

        layer1.Setup(c => c.DisposeAsync()).Returns(() =>
        {
            disposalOrder.Add("L1");
            return ValueTask.CompletedTask;
        });
        layer2.Setup(c => c.DisposeAsync()).Returns(() =>
        {
            disposalOrder.Add("L2");
            return ValueTask.CompletedTask;
        });
        layer3.Setup(c => c.DisposeAsync()).Returns(() =>
        {
            disposalOrder.Add("L3");
            return ValueTask.CompletedTask;
        });

        var cache = CreateLayeredCache(layer1.Object, layer2.Object, layer3.Object);

        // ACT
        await cache.DisposeAsync();

        // ASSERT — outermost first, innermost last
        Assert.Equal(new[] { "L3", "L2", "L1" }, disposalOrder);
    }

    #endregion

    #region Layers Property Tests

    [Fact]
    public void Layers_SingleLayer_ReturnsSingleElementList()
    {
        // ARRANGE
        var layer = CreateLayerMock();
        var cache = CreateLayeredCache(layer.Object);

        // ACT
        var layers = cache.Layers;

        // ASSERT
        Assert.NotNull(layers);
        Assert.Single(layers);
        Assert.Same(layer.Object, layers[0]);
    }

    [Fact]
    public void Layers_TwoLayers_ReturnsBothLayersInOrder()
    {
        // ARRANGE
        var layer0 = CreateLayerMock(); // deepest (index 0)
        var layer1 = CreateLayerMock(); // outermost (index 1)
        var cache = CreateLayeredCache(layer0.Object, layer1.Object);

        // ACT
        var layers = cache.Layers;

        // ASSERT
        Assert.Equal(2, layers.Count);
        Assert.Same(layer0.Object, layers[0]);
        Assert.Same(layer1.Object, layers[1]);
    }

    [Fact]
    public void Layers_ThreeLayers_ReturnsAllThreeInOrder()
    {
        // ARRANGE
        var layer0 = CreateLayerMock(); // deepest
        var layer1 = CreateLayerMock(); // middle
        var layer2 = CreateLayerMock(); // outermost
        var cache = CreateLayeredCache(layer0.Object, layer1.Object, layer2.Object);

        // ACT
        var layers = cache.Layers;

        // ASSERT
        Assert.Equal(3, layers.Count);
        Assert.Same(layer0.Object, layers[0]);
        Assert.Same(layer1.Object, layers[1]);
        Assert.Same(layer2.Object, layers[2]);
    }

    [Fact]
    public void Layers_CountMatchesLayerCount()
    {
        // ARRANGE
        var layer0 = CreateLayerMock();
        var layer1 = CreateLayerMock();
        var cache = CreateLayeredCache(layer0.Object, layer1.Object);

        // ASSERT
        Assert.Equal(cache.LayerCount, cache.Layers.Count);
    }

    [Fact]
    public async Task Layers_OutermostLayerIsUserFacing()
    {
        // ARRANGE — the outermost layer (last index) should be the one that GetDataAsync delegates to
        var innerLayer = CreateLayerMock();
        var outerLayer = CreateLayerMock();
        var range = MakeRange(1, 10);
        var expectedResult = MakeResult(1, 10);

        outerLayer.Setup(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);

        var cache = CreateLayeredCache(innerLayer.Object, outerLayer.Object);

        // ACT
        var result = await cache.GetDataAsync(range, CancellationToken.None);

        // ASSERT — Layers[^1] is the outermost (user-facing) layer that received the call
        Assert.Same(outerLayer.Object, cache.Layers[^1]);
        outerLayer.Verify(c => c.GetDataAsync(range, It.IsAny<CancellationToken>()), Times.Once);
        innerLayer.VerifyNoOtherCalls();
    }

    #endregion

    #region CurrentRuntimeOptions Delegation Tests

    [Fact]
    public void CurrentRuntimeOptions_DelegatesToOutermostLayer()
    {
        // ARRANGE
        var innerLayer = CreateLayerMock();
        var outerLayer = CreateLayerMock();
        var expectedSnapshot = new RuntimeOptionsSnapshot(1.5, 2.0, 0.3, 0.4,
            TimeSpan.FromMilliseconds(100));

        outerLayer.Setup(c => c.CurrentRuntimeOptions).Returns(expectedSnapshot);

        var cache = CreateLayeredCache(innerLayer.Object, outerLayer.Object);

        // ACT
        var result = cache.CurrentRuntimeOptions;

        // ASSERT
        Assert.Same(expectedSnapshot, result);
        outerLayer.Verify(c => c.CurrentRuntimeOptions, Times.Once);
        innerLayer.VerifyNoOtherCalls();
    }

    [Fact]
    public void CurrentRuntimeOptions_SingleLayer_DelegatesToThatLayer()
    {
        // ARRANGE
        var onlyLayer = CreateLayerMock();
        var expectedSnapshot = new RuntimeOptionsSnapshot(1.0, 1.0, null, null, TimeSpan.Zero);

        onlyLayer.Setup(c => c.CurrentRuntimeOptions).Returns(expectedSnapshot);

        var cache = CreateLayeredCache(onlyLayer.Object);

        // ACT
        var result = cache.CurrentRuntimeOptions;

        // ASSERT
        Assert.Same(expectedSnapshot, result);
        onlyLayer.Verify(c => c.CurrentRuntimeOptions, Times.Once);
    }

    [Fact]
    public void CurrentRuntimeOptions_DoesNotReadInnerLayers()
    {
        // ARRANGE — only the outermost layer should be queried
        var innerLayer = CreateLayerMock();
        var middleLayer = CreateLayerMock();
        var outerLayer = CreateLayerMock();
        var expectedSnapshot = new RuntimeOptionsSnapshot(2.0, 3.0, null, null, TimeSpan.Zero);

        outerLayer.Setup(c => c.CurrentRuntimeOptions).Returns(expectedSnapshot);

        var cache = CreateLayeredCache(innerLayer.Object, middleLayer.Object, outerLayer.Object);

        // ACT
        _ = cache.CurrentRuntimeOptions;

        // ASSERT — inner and middle layers must not be touched
        innerLayer.VerifyNoOtherCalls();
        middleLayer.VerifyNoOtherCalls();
    }

    #endregion

    #region IWindowCache Interface Tests

    [Fact]
    public void LayeredWindowCache_ImplementsIWindowCache()
    {
        // ARRANGE
        var layer = CreateLayerMock();
        layer.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);

        // ACT
        var cache = CreateLayeredCache(layer.Object);

        // ASSERT
        Assert.IsAssignableFrom<IWindowCache<int, int, IntegerFixedStepDomain>>(cache);
    }

    [Fact]
    public void LayeredWindowCache_ImplementsIAsyncDisposable()
    {
        // ARRANGE
        var layer = CreateLayerMock();
        layer.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
        var cache = CreateLayeredCache(layer.Object);

        // ASSERT
        Assert.IsAssignableFrom<IAsyncDisposable>(cache);
    }

    #endregion
}
