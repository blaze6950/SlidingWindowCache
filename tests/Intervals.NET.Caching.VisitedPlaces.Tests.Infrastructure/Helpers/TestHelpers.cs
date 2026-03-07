using Intervals.NET.Domain.Default.Numeric;
using Moq;
using Intervals.NET.Caching.Dto;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Evaluators;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Executors;
using Intervals.NET.Caching.VisitedPlaces.Public.Cache;
using Intervals.NET.Caching.VisitedPlaces.Public.Configuration;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure.DataSources;
using Intervals.NET.Domain.Extensions.Fixed;

namespace Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure.Helpers;

/// <summary>
/// Helper methods for creating VPC test components.
/// Uses <see cref="IntegerFixedStepDomain"/> for range handling and domain calculations.
/// </summary>
public static class TestHelpers
{
    // ============================================================
    // DOMAIN & RANGE FACTORIES
    // ============================================================

    /// <summary>Creates a standard integer fixed-step domain for testing.</summary>
    public static IntegerFixedStepDomain CreateIntDomain() => new();

    /// <summary>
    /// Creates a closed range [start, end] (both boundaries inclusive) using Intervals.NET factory.
    /// </summary>
    public static Range<int> CreateRange(int start, int end) =>
        Factories.Range.Closed<int>(start, end);

    // ============================================================
    // OPTIONS FACTORIES
    // ============================================================

    /// <summary>
    /// Creates default cache options suitable for most tests.
    /// </summary>
    public static VisitedPlacesCacheOptions CreateDefaultOptions(
        StorageStrategy storageStrategy = StorageStrategy.SnapshotAppendBuffer,
        int eventChannelCapacity = 128) =>
        new(storageStrategy, eventChannelCapacity);

    // ============================================================
    // CACHE FACTORIES
    // ============================================================

    /// <summary>
    /// Creates a <see cref="VisitedPlacesCache{TRange,TData,TDomain}"/> with default options,
    /// a mock data source, MaxSegmentCount(100) evaluator, and LRU executor.
    /// Returns both the cache and the mock for setup/verification.
    /// </summary>
    public static (VisitedPlacesCache<int, int, IntegerFixedStepDomain> cache,
        Mock<IDataSource<int, int>> mockDataSource)
        CreateCacheWithMock(
            IntegerFixedStepDomain domain,
            EventCounterCacheDiagnostics diagnostics,
            VisitedPlacesCacheOptions? options = null,
            int maxSegmentCount = 100,
            TimeSpan? fetchDelay = null)
    {
        var mock = CreateMockDataSource(fetchDelay);
        var cache = CreateCache(mock.Object, domain, options ?? CreateDefaultOptions(), diagnostics, maxSegmentCount);
        return (cache, mock);
    }

    /// <summary>
    /// Creates a cache backed by the given data source and a MaxSegmentCount(maxSegmentCount) + LRU eviction policy.
    /// </summary>
    public static VisitedPlacesCache<int, int, IntegerFixedStepDomain> CreateCache(
        IDataSource<int, int> dataSource,
        IntegerFixedStepDomain domain,
        VisitedPlacesCacheOptions options,
        EventCounterCacheDiagnostics diagnostics,
        int maxSegmentCount = 100)
    {
        IReadOnlyList<IEvictionEvaluator<int, int>> evaluators =
            [new MaxSegmentCountEvaluator<int, int>(maxSegmentCount)];
        IEvictionExecutor<int, int> executor = new LruEvictionExecutor<int, int>();

        return new VisitedPlacesCache<int, int, IntegerFixedStepDomain>(
            dataSource, domain, options, evaluators, executor, diagnostics);
    }

    /// <summary>
    /// Creates a <see cref="VisitedPlacesCache{TRange,TData,TDomain}"/> backed by a <see cref="SimpleTestDataSource"/>.
    /// </summary>
    public static VisitedPlacesCache<int, int, IntegerFixedStepDomain> CreateCacheWithSimpleSource(
        IntegerFixedStepDomain domain,
        EventCounterCacheDiagnostics diagnostics,
        VisitedPlacesCacheOptions? options = null,
        int maxSegmentCount = 100)
    {
        var dataSource = new SimpleTestDataSource();
        return CreateCache(dataSource, domain, options ?? CreateDefaultOptions(), diagnostics, maxSegmentCount);
    }

    /// <summary>
    /// Creates a mock <see cref="IDataSource{TRange,TData}"/> that generates sequential integer data.
    /// </summary>
    public static Mock<IDataSource<int, int>> CreateMockDataSource(TimeSpan? fetchDelay = null)
    {
        var mock = new Mock<IDataSource<int, int>>();

        mock.Setup(ds => ds.FetchAsync(It.IsAny<Range<int>>(), It.IsAny<CancellationToken>()))
            .Returns<Range<int>, CancellationToken>(async (range, ct) =>
            {
                if (fetchDelay.HasValue)
                {
                    await Task.Delay(fetchDelay.Value, ct);
                }

                var data = DataGenerationHelpers.GenerateDataForRange(range);
                return new RangeChunk<int, int>(range, data);
            });

        return mock;
    }

    // ============================================================
    // ASSERTION HELPERS
    // ============================================================

    /// <summary>
    /// Asserts that the returned data matches the expected sequential integers for the given range.
    /// </summary>
    public static void AssertUserDataCorrect(ReadOnlyMemory<int> data, Range<int> range)
    {
        var domain = CreateIntDomain();
        var expectedLength = (int)range.Span(domain).Value;

        Assert.Equal(expectedLength, data.Length);

        var span = data.Span;
        var start = (int)range.Start;

        switch (range)
        {
            case { IsStartInclusive: true, IsEndInclusive: true }:
                for (var i = 0; i < span.Length; i++)
                    Assert.Equal(start + i, span[i]);
                break;

            case { IsStartInclusive: true, IsEndInclusive: false }:
                for (var i = 0; i < span.Length; i++)
                    Assert.Equal(start + i, span[i]);
                break;

            case { IsStartInclusive: false, IsEndInclusive: true }:
                for (var i = 0; i < span.Length; i++)
                    Assert.Equal(start + 1 + i, span[i]);
                break;

            default:
                for (var i = 0; i < span.Length; i++)
                    Assert.Equal(start + 1 + i, span[i]);
                break;
        }
    }

    /// <summary>
    /// Asserts that at least one user request was served.
    /// </summary>
    public static void AssertUserRequestServed(EventCounterCacheDiagnostics diagnostics, int expectedCount = 1)
    {
        Assert.Equal(expectedCount, diagnostics.UserRequestServed);
    }

    /// <summary>
    /// Asserts a full cache hit occurred.
    /// </summary>
    public static void AssertFullCacheHit(EventCounterCacheDiagnostics diagnostics, int expectedCount = 1)
    {
        Assert.Equal(expectedCount, diagnostics.UserRequestFullCacheHit);
    }

    /// <summary>
    /// Asserts a partial cache hit occurred.
    /// </summary>
    public static void AssertPartialCacheHit(EventCounterCacheDiagnostics diagnostics, int expectedCount = 1)
    {
        Assert.Equal(expectedCount, diagnostics.UserRequestPartialCacheHit);
    }

    /// <summary>
    /// Asserts a full cache miss occurred.
    /// </summary>
    public static void AssertFullCacheMiss(EventCounterCacheDiagnostics diagnostics, int expectedCount = 1)
    {
        Assert.Equal(expectedCount, diagnostics.UserRequestFullCacheMiss);
    }

    /// <summary>
    /// Asserts that background events were processed.
    /// </summary>
    public static void AssertBackgroundEventsProcessed(EventCounterCacheDiagnostics diagnostics, int minExpected = 1)
    {
        Assert.True(diagnostics.BackgroundEventProcessed >= minExpected,
            $"Expected at least {minExpected} background events processed, but found {diagnostics.BackgroundEventProcessed}.");
    }

    /// <summary>
    /// Asserts that a segment was stored in the background.
    /// </summary>
    public static void AssertSegmentStored(EventCounterCacheDiagnostics diagnostics, int minExpected = 1)
    {
        Assert.True(diagnostics.BackgroundSegmentStored >= minExpected,
            $"Expected at least {minExpected} segment(s) stored, but found {diagnostics.BackgroundSegmentStored}.");
    }

    /// <summary>
    /// Asserts that eviction was triggered.
    /// </summary>
    public static void AssertEvictionTriggered(EventCounterCacheDiagnostics diagnostics, int minExpected = 1)
    {
        Assert.True(diagnostics.EvictionTriggered >= minExpected,
            $"Expected eviction to be triggered at least {minExpected} time(s), but found {diagnostics.EvictionTriggered}.");
    }

    /// <summary>
    /// Asserts that segments were removed during eviction.
    /// </summary>
    public static void AssertSegmentsEvicted(EventCounterCacheDiagnostics diagnostics, int minExpected = 1)
    {
        Assert.True(diagnostics.EvictionSegmentRemoved >= minExpected,
            $"Expected at least {minExpected} segment(s) evicted, but found {diagnostics.EvictionSegmentRemoved}.");
    }

    /// <summary>
    /// Asserts that background event processing lifecycle is consistent:
    /// Received == Processed + Failed.
    /// </summary>
    public static void AssertBackgroundLifecycleIntegrity(EventCounterCacheDiagnostics diagnostics)
    {
        var received = diagnostics.BackgroundEventReceived;
        var processed = diagnostics.BackgroundEventProcessed;
        var failed = diagnostics.BackgroundEventProcessingFailed;
        Assert.Equal(received, processed + failed);
    }

    /// <summary>
    /// Asserts that no background event processing failures occurred.
    /// </summary>
    public static void AssertNoBackgroundFailures(EventCounterCacheDiagnostics diagnostics)
    {
        Assert.Equal(0, diagnostics.BackgroundEventProcessingFailed);
    }
}
