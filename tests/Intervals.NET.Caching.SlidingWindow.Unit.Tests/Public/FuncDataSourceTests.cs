using Intervals.NET.Caching.Dto;

namespace Intervals.NET.Caching.SlidingWindow.Unit.Tests.Public;

/// <summary>
/// Unit tests for <see cref="FuncDataSource{TRange,TData}"/>.
/// Validates constructor argument checking, delegation to the supplied func,
/// return-value forwarding, exception propagation, and the inherited batch overload.
/// </summary>
public sealed class FuncDataSourceTests
{
    #region Test Infrastructure

    private static Range<int> MakeRange(int start, int end)
        => Factories.Range.Closed<int>(start, end);

    private static RangeChunk<int, int> MakeChunk(Range<int> range, IEnumerable<int> data)
        => new(range, data);

    #endregion

    #region Constructor Tests

    [Fact]
    public void Constructor_WithNullFunc_ThrowsArgumentNullException()
    {
        // ARRANGE
        Func<Range<int>, CancellationToken, Task<RangeChunk<int, int>>>? nullFunc = null;

        // ACT
        var exception = Record.Exception(
            () => new FuncDataSource<int, int>(nullFunc!));

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<ArgumentNullException>(exception);
        Assert.Equal("fetchFunc", ((ArgumentNullException)exception).ParamName);
    }

    [Fact]
    public void Constructor_WithValidFunc_DoesNotThrow()
    {
        // ARRANGE
        static Task<RangeChunk<int, int>> Func(Range<int> r, CancellationToken ct)
            => Task.FromResult(new RangeChunk<int, int>(r, []));

        // ACT
        var exception = Record.Exception(() => new FuncDataSource<int, int>(Func));

        // ASSERT
        Assert.Null(exception);
    }

    #endregion

    #region FetchAsync Delegation Tests

    [Fact]
    public async Task FetchAsync_PassesRangeToDelegate()
    {
        // ARRANGE
        Range<int>? capturedRange = null;
        var expectedRange = MakeRange(10, 20);

        var source = new FuncDataSource<int, int>(
            (range, ct) =>
            {
                capturedRange = range;
                return Task.FromResult(MakeChunk(range, []));
            });

        // ACT
        await source.FetchAsync(expectedRange, CancellationToken.None);

        // ASSERT
        Assert.Equal(expectedRange, capturedRange);
    }

    [Fact]
    public async Task FetchAsync_PassesCancellationTokenToDelegate()
    {
        // ARRANGE
        CancellationToken capturedToken = default;
        using var cts = new CancellationTokenSource();
        var range = MakeRange(0, 5);

        var source = new FuncDataSource<int, int>(
            (r, ct) =>
            {
                capturedToken = ct;
                return Task.FromResult(MakeChunk(r, []));
            });

        // ACT
        await source.FetchAsync(range, cts.Token);

        // ASSERT
        Assert.Equal(cts.Token, capturedToken);
    }

    [Fact]
    public async Task FetchAsync_ReturnsDelegateResult()
    {
        // ARRANGE
        var range = MakeRange(1, 3);
        var expectedData = new[] { 10, 20, 30 };
        var expectedChunk = MakeChunk(range, expectedData);

        var source = new FuncDataSource<int, int>(
            (r, ct) => Task.FromResult(expectedChunk));

        // ACT
        var result = await source.FetchAsync(range, CancellationToken.None);

        // ASSERT
        Assert.Equal(expectedChunk, result);
    }

    [Fact]
    public async Task FetchAsync_ReturnsDelegateResult_WithNullRange()
    {
        // ARRANGE — simulates a bounded source returning no data
        var requestedRange = MakeRange(9000, 9999);
        var emptyChunk = new RangeChunk<int, int>(null, []);

        var source = new FuncDataSource<int, int>(
            (r, ct) => Task.FromResult(emptyChunk));

        // ACT
        var result = await source.FetchAsync(requestedRange, CancellationToken.None);

        // ASSERT
        Assert.Null(result.Range);
        Assert.Empty(result.Data);
    }

    [Fact]
    public async Task FetchAsync_PropagatesExceptionFromDelegate()
    {
        // ARRANGE
        var range = MakeRange(0, 10);
        var expected = new InvalidOperationException("source failure");

        var source = new FuncDataSource<int, int>(
            (r, ct) => Task.FromException<RangeChunk<int, int>>(expected));

        // ACT
        var exception = await Record.ExceptionAsync(
            () => source.FetchAsync(range, CancellationToken.None));

        // ASSERT
        Assert.NotNull(exception);
        Assert.Same(expected, exception);
    }

    [Fact]
    public async Task FetchAsync_InvokesDelegateOnEachCall()
    {
        // ARRANGE
        var callCount = 0;
        var range = MakeRange(0, 1);

        var source = new FuncDataSource<int, int>(
            (r, ct) =>
            {
                callCount++;
                return Task.FromResult(MakeChunk(r, []));
            });

        // ACT
        await source.FetchAsync(range, CancellationToken.None);
        await source.FetchAsync(range, CancellationToken.None);

        // ASSERT
        Assert.Equal(2, callCount);
    }

    #endregion

    #region Batch FetchAsync Tests (interface default)

    [Fact]
    public async Task BatchFetchAsync_CallsDelegateForEachRange()
    {
        // ARRANGE
        var invokedRanges = new List<Range<int>>();
        var ranges = new[]
        {
            MakeRange(0, 9),
            MakeRange(10, 19),
            MakeRange(20, 29),
        };

        var source = new FuncDataSource<int, int>(
            (r, ct) =>
            {
                lock (invokedRanges) invokedRanges.Add(r);
                return Task.FromResult(MakeChunk(r, []));
            });

        // ACT
        var results = await ((IDataSource<int, int>)source)
            .FetchAsync(ranges, CancellationToken.None);

        // ASSERT
        Assert.Equal(ranges.Length, results.Count());
        Assert.Equal(ranges.Length, invokedRanges.Count);
        Assert.All(ranges, r => Assert.Contains(r, invokedRanges));
    }

    [Fact]
    public async Task BatchFetchAsync_ReturnsChunkForEachRange()
    {
        // ARRANGE
        var ranges = new[]
        {
            MakeRange(0, 4),
            MakeRange(5, 9),
        };

        var source = new FuncDataSource<int, int>(
            (r, ct) => Task.FromResult(MakeChunk(r, [])));

        // ACT
        var results = (await ((IDataSource<int, int>)source)
            .FetchAsync(ranges, CancellationToken.None)).ToList();

        // ASSERT
        Assert.Equal(2, results.Count);
    }

    #endregion
}
