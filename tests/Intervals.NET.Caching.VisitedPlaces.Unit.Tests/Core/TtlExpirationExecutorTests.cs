using Intervals.NET.Caching.VisitedPlaces.Core;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Policies;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Selectors;
using Intervals.NET.Caching.VisitedPlaces.Core.Ttl;
using Intervals.NET.Caching.VisitedPlaces.Infrastructure.Storage;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure.Helpers;

namespace Intervals.NET.Caching.VisitedPlaces.Unit.Tests.Core;

/// <summary>
/// Unit tests for <see cref="TtlExpirationExecutor{TRange,TData}"/>.
/// Verifies that the executor correctly delays until expiry, removes the segment directly via
/// storage and eviction engine, fires diagnostics, and aborts cleanly on cancellation.
/// </summary>
public sealed class TtlExpirationExecutorTests
{
    private readonly SnapshotAppendBufferStorage<int, int> _storage = new();
    private readonly EventCounterCacheDiagnostics _diagnostics = new();

    #region ExecuteAsync — Immediate Expiry

    [Fact]
    public async Task ExecuteAsync_AlreadyExpired_RemovesSegmentImmediately()
    {
        // ARRANGE — ExpiresAt is in the past
        var (executor, segment) = CreateExecutorWithSegment(0, 9);
        var workItem = new TtlExpirationWorkItem<int, int>(
            segment,
            expiresAt: DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1),
            CancellationToken.None);

        // ACT
        await executor.ExecuteAsync(workItem, CancellationToken.None);

        // ASSERT
        Assert.True(segment.IsRemoved);
        Assert.Equal(0, _storage.Count);
        Assert.Equal(1, _diagnostics.TtlSegmentExpired);
    }

    [Fact]
    public async Task ExecuteAsync_ExactlyAtExpiry_RemovesSegment()
    {
        // ARRANGE — ExpiresAt == UtcNow (zero remaining delay)
        var (executor, segment) = CreateExecutorWithSegment(0, 9);
        var workItem = new TtlExpirationWorkItem<int, int>(
            segment,
            expiresAt: DateTimeOffset.UtcNow,
            CancellationToken.None);

        // ACT
        await executor.ExecuteAsync(workItem, CancellationToken.None);

        // ASSERT
        Assert.True(segment.IsRemoved);
        Assert.Equal(1, _diagnostics.TtlSegmentExpired);
    }

    #endregion

    #region ExecuteAsync — Short Future Expiry

    [Fact]
    public async Task ExecuteAsync_ShortFutureExpiry_WaitsAndThenRemoves()
    {
        // ARRANGE — 80 ms delay
        var (executor, segment) = CreateExecutorWithSegment(0, 9);
        var workItem = new TtlExpirationWorkItem<int, int>(
            segment,
            expiresAt: DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(80),
            CancellationToken.None);

        // ACT
        var before = DateTimeOffset.UtcNow;
        await executor.ExecuteAsync(workItem, CancellationToken.None);
        var elapsed = DateTimeOffset.UtcNow - before;

        // ASSERT — waited at least ~80ms and then removed
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(60),
            $"Expected elapsed >= 60ms but got {elapsed.TotalMilliseconds:F0}ms");
        Assert.True(segment.IsRemoved);
        Assert.Equal(0, _storage.Count);
        Assert.Equal(1, _diagnostics.TtlSegmentExpired);
    }

    #endregion

    #region ExecuteAsync — Segment Already Evicted (Idempotency)

    [Fact]
    public async Task ExecuteAsync_SegmentAlreadyEvicted_IsNoOpAndDoesNotFireDiagnostic()
    {
        // ARRANGE — segment evicted before TTL fires (TryMarkAsRemoved already claimed)
        var (executor, segment) = CreateExecutorWithSegment(0, 9);
        segment.TryMarkAsRemoved(); // simulates eviction that beat the TTL

        var workItem = new TtlExpirationWorkItem<int, int>(
            segment,
            expiresAt: DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1),
            CancellationToken.None);

        // ACT
        await executor.ExecuteAsync(workItem, CancellationToken.None);

        // ASSERT — no second removal; TtlSegmentExpired does NOT fire (already-removed is a no-op)
        Assert.Equal(1, _storage.Count); // storage not touched (MarkAsRemoved returned false)
        Assert.Equal(0, _diagnostics.TtlSegmentExpired);
    }

    #endregion

    #region ExecuteAsync — Cancellation

    [Fact]
    public async Task ExecuteAsync_CancelledBeforeExpiry_ThrowsOperationCanceledException()
    {
        // ARRANGE — long delay; we cancel immediately
        var (executor, segment) = CreateExecutorWithSegment(0, 9);
        using var cts = new CancellationTokenSource();
        var workItem = new TtlExpirationWorkItem<int, int>(
            segment,
            expiresAt: DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30),
            CancellationToken.None);

        // ACT — cancel before the delay completes
        var executeTask = executor.ExecuteAsync(workItem, cts.Token);
        await cts.CancelAsync();

        var ex = await Record.ExceptionAsync(() => executeTask);

        // ASSERT — OperationCanceledException propagated (not swallowed by executor)
        Assert.NotNull(ex);
        Assert.IsAssignableFrom<OperationCanceledException>(ex);

        // segment NOT removed
        Assert.False(segment.IsRemoved);
        Assert.Equal(1, _storage.Count);
        Assert.Equal(0, _diagnostics.TtlSegmentExpired);
    }

    [Fact]
    public async Task ExecuteAsync_AlreadyCancelledToken_ThrowsOperationCanceledException()
    {
        // ARRANGE — already-cancelled token with future expiry
        var (executor, segment) = CreateExecutorWithSegment(0, 9);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var workItem = new TtlExpirationWorkItem<int, int>(
            segment,
            expiresAt: DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30),
            CancellationToken.None);

        // ACT
        var ex = await Record.ExceptionAsync(() =>
            executor.ExecuteAsync(workItem, cts.Token));

        // ASSERT
        Assert.NotNull(ex);
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.False(segment.IsRemoved);
    }

    #endregion

    #region Helpers

    private (TtlExpirationExecutor<int, int> executor,
             CachedSegment<int, int> segment)
        CreateExecutorWithSegment(int start, int end)
    {
        var range = TestHelpers.CreateRange(start, end);
        var segment = new CachedSegment<int, int>(
            range,
            new ReadOnlyMemory<int>(new int[end - start + 1]));
        _storage.Add(segment);

        var evictionEngine = new EvictionEngine<int, int>(
            [new MaxSegmentCountPolicy<int, int>(100)],
            new LruEvictionSelector<int, int>(),
            _diagnostics);
        var executor = new TtlExpirationExecutor<int, int>(_storage, evictionEngine, _diagnostics);

        return (executor, segment);
    }

    #endregion
}
