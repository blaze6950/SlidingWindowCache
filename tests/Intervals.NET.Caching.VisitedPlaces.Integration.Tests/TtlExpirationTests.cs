using Intervals.NET.Caching.Dto;
using Intervals.NET.Caching.Extensions;
using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Caching.VisitedPlaces.Public.Cache;
using Intervals.NET.Caching.VisitedPlaces.Public.Configuration;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure.Helpers;

namespace Intervals.NET.Caching.VisitedPlaces.Integration.Tests;

/// <summary>
/// Integration tests for the TTL expiration mechanism.
/// Validates end-to-end segment expiry, idempotency with concurrent eviction,
/// TTL-disabled behaviour, and diagnostics counters.
/// </summary>
public sealed class TtlExpirationTests : IAsyncDisposable
{
    private readonly IntegerFixedStepDomain _domain = new();
    private readonly EventCounterCacheDiagnostics _diagnostics = new();
    private VisitedPlacesCache<int, int, IntegerFixedStepDomain>? _cache;

    public async ValueTask DisposeAsync()
    {
        if (_cache != null)
        {
            await _cache.DisposeAsync();
        }
    }

    // ============================================================
    // TTL DISABLED — baseline behaviour unchanged
    // ============================================================

    [Fact]
    public async Task TtlDisabled_SegmentIsNeverExpired()
    {
        // ARRANGE — no TTL configured; segment should stay in cache indefinitely
        var options = new VisitedPlacesCacheOptions<int, int>(eventChannelCapacity: 128, segmentTtl: null);
        _cache = TestHelpers.CreateCacheWithSimpleSource(_domain, _diagnostics, options);

        var range = TestHelpers.CreateRange(0, 9);
        await _cache.GetDataAndWaitForIdleAsync(range);

        // ASSERT — segment stored; no TTL work items scheduled
        Assert.Equal(1, _diagnostics.BackgroundSegmentStored);
        Assert.Equal(0, _diagnostics.TtlWorkItemScheduled);
        Assert.Equal(0, _diagnostics.TtlSegmentExpired);

        // Give ample time for any spurious TTL expiry to fire (it should not)
        await Task.Delay(150);
        Assert.Equal(0, _diagnostics.TtlSegmentExpired);
    }

    // ============================================================
    // TTL ENABLED — end-to-end expiration
    // ============================================================

    [Fact]
    public async Task TtlEnabled_SegmentExpiresAfterTtl()
    {
        // ARRANGE — 100 ms TTL
        var options = new VisitedPlacesCacheOptions<int, int>(
            eventChannelCapacity: 128,
            segmentTtl: TimeSpan.FromMilliseconds(100));
        _cache = TestHelpers.CreateCacheWithSimpleSource(_domain, _diagnostics, options);

        var range = TestHelpers.CreateRange(0, 9);

        // ACT — store segment
        await _cache.GetDataAndWaitForIdleAsync(range);

        Assert.Equal(1, _diagnostics.BackgroundSegmentStored);
        Assert.Equal(1, _diagnostics.TtlWorkItemScheduled);

        // Wait for TTL to fire (with generous headroom)
        await Task.Delay(350);

        // ASSERT — TTL expiry fired
        Assert.Equal(1, _diagnostics.TtlSegmentExpired);
    }

    [Fact]
    public async Task TtlEnabled_MultipleSegments_AllExpire()
    {
        // ARRANGE — 100 ms TTL; two non-overlapping ranges
        var options = new VisitedPlacesCacheOptions<int, int>(
            eventChannelCapacity: 128,
            segmentTtl: TimeSpan.FromMilliseconds(100));
        _cache = TestHelpers.CreateCacheWithSimpleSource(_domain, _diagnostics, options);

        // ACT
        await _cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(0, 9));
        await _cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(20, 29));

        Assert.Equal(2, _diagnostics.BackgroundSegmentStored);
        Assert.Equal(2, _diagnostics.TtlWorkItemScheduled);

        await Task.Delay(350);

        // ASSERT — both TTL expirations fired
        Assert.Equal(2, _diagnostics.TtlSegmentExpired);
    }

    [Fact]
    public async Task TtlEnabled_AfterExpiry_SubsequentRequestRefetchesFromDataSource()
    {
        // ARRANGE — 100 ms TTL
        var options = new VisitedPlacesCacheOptions<int, int>(
            eventChannelCapacity: 128,
            segmentTtl: TimeSpan.FromMilliseconds(100));
        _cache = TestHelpers.CreateCacheWithSimpleSource(_domain, _diagnostics, options);

        var range = TestHelpers.CreateRange(0, 9);

        // First fetch — populates cache
        var result1 = await _cache.GetDataAndWaitForIdleAsync(range);
        Assert.Equal(CacheInteraction.FullMiss, result1.CacheInteraction);
        Assert.Equal(1, _diagnostics.BackgroundSegmentStored);

        // Wait for TTL expiry
        await Task.Delay(350);
        Assert.Equal(1, _diagnostics.TtlSegmentExpired);

        _diagnostics.Reset();

        // Second fetch — segment gone, must re-fetch from data source
        var result2 = await _cache.GetDataAndWaitForIdleAsync(range);

        // ASSERT — full miss again (segment was evicted by TTL)
        Assert.Equal(CacheInteraction.FullMiss, result2.CacheInteraction);
        Assert.Equal(1, _diagnostics.BackgroundSegmentStored);
        Assert.Equal(1, _diagnostics.TtlWorkItemScheduled);
    }

    // ============================================================
    // TTL + EVICTION — idempotency when eviction beats TTL
    // ============================================================

    [Fact]
    public async Task TtlEnabled_SegmentEvictedBeforeTtlFires_NoDoubleRemoval()
    {
        // ARRANGE — 200 ms TTL; MaxSegmentCount(1) so the second request evicts the first
        var options = new VisitedPlacesCacheOptions<int, int>(
            eventChannelCapacity: 128,
            segmentTtl: TimeSpan.FromMilliseconds(200));
        _cache = TestHelpers.CreateCacheWithSimpleSource(_domain, _diagnostics, options, maxSegmentCount: 1);

        // ACT — store first segment, then second (evicts first)
        await _cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(0, 9));
        await _cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(20, 29));

        Assert.Equal(2, _diagnostics.BackgroundSegmentStored);
        Assert.Equal(2, _diagnostics.TtlWorkItemScheduled);
        Assert.Equal(1, _diagnostics.EvictionTriggered); // first segment was evicted

        // Wait for both TTL expirations to fire
        await Task.Delay(500);

        // ASSERT — only the real removal fires TtlSegmentExpired; the already-evicted no-op is silent
        Assert.Equal(1, _diagnostics.TtlSegmentExpired);
        Assert.Equal(0, _diagnostics.BackgroundOperationFailed);
    }

    // ============================================================
    // DISPOSAL — pending TTL work items are cancelled
    // ============================================================

    [Fact]
    public async Task Disposal_PendingTtlWorkItems_AreCancelledCleanly()
    {
        // ARRANGE — very long TTL so it won't fire before disposal
        var options = new VisitedPlacesCacheOptions<int, int>(
            eventChannelCapacity: 128,
            segmentTtl: TimeSpan.FromMinutes(10));
        _cache = TestHelpers.CreateCacheWithSimpleSource(_domain, _diagnostics, options);

        await _cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(0, 9));
        Assert.Equal(1, _diagnostics.TtlWorkItemScheduled);
        Assert.Equal(0, _diagnostics.TtlSegmentExpired);

        // ACT — dispose cache while TTL is still pending
        await _cache.DisposeAsync();
        _cache = null; // prevent DisposeAsync() from being called again in IAsyncDisposable

        // ASSERT — no crash, TTL did not fire, no background operation failure
        Assert.Equal(0, _diagnostics.TtlSegmentExpired);
        Assert.Equal(0, _diagnostics.BackgroundOperationFailed);
    }

    // ============================================================
    // DIAGNOSTICS — TtlWorkItemScheduled counter
    // ============================================================

    [Fact]
    public async Task TtlEnabled_DiagnosticsCounters_AreCorrect()
    {
        // ARRANGE
        var options = new VisitedPlacesCacheOptions<int, int>(
            eventChannelCapacity: 128,
            segmentTtl: TimeSpan.FromMilliseconds(100));
        _cache = TestHelpers.CreateCacheWithSimpleSource(_domain, _diagnostics, options);

        // ACT — three separate non-overlapping requests
        await _cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(0, 9));
        await _cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(20, 29));
        await _cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(40, 49));

        // ASSERT — one TtlWorkItemScheduled per segment stored
        Assert.Equal(3, _diagnostics.BackgroundSegmentStored);
        Assert.Equal(3, _diagnostics.TtlWorkItemScheduled);

        // Wait and verify all three expire
        await Task.Delay(400);
        Assert.Equal(3, _diagnostics.TtlSegmentExpired);
    }
}
