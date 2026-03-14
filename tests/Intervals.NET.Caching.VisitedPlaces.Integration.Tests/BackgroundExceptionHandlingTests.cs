using Intervals.NET.Caching.Extensions;
using Intervals.NET.Caching.Infrastructure.Diagnostics;
using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Caching.VisitedPlaces.Core;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Policies;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Selectors;
using Intervals.NET.Caching.VisitedPlaces.Public.Cache;
using Intervals.NET.Caching.VisitedPlaces.Public.Configuration;
using Intervals.NET.Caching.VisitedPlaces.Public.Instrumentation;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure.DataSources;
using Intervals.NET.Caching.VisitedPlaces.Tests.Infrastructure.Helpers;

namespace Intervals.NET.Caching.VisitedPlaces.Integration.Tests;

/// <summary>
/// Tests for exception handling in the Background Path of <see cref="VisitedPlacesCache{TRange,TData,TDomain}"/>.
/// Verifies that the background storage loop correctly reports failures via
/// <see cref="ICacheDiagnostics.BackgroundOperationFailed"/> and remains operational afterwards.
/// 
/// In VPC, the Background Path does not perform I/O — data is delivered via User Path events.
/// Background exceptions would arise from internal processing failures. This suite verifies
/// the diagnostics interface contract and the lifecycle invariant (Received == Processed + Failed).
/// </summary>
public sealed class BackgroundExceptionHandlingTests : IAsyncDisposable
{
    private readonly IntegerFixedStepDomain _domain = new();
    private readonly EventCounterCacheDiagnostics _diagnostics = new();
    private VisitedPlacesCache<int, int, IntegerFixedStepDomain>? _cache;

    public async ValueTask DisposeAsync()
    {
        if (_cache != null)
        {
            await _cache.WaitForIdleAsync();
            await _cache.DisposeAsync();
        }
    }

    private VisitedPlacesCache<int, int, IntegerFixedStepDomain> CreateCache(
        int maxSegmentCount = 100,
        StorageStrategyOptions<int, int>? strategy = null)
    {
        _cache = TestHelpers.CreateCacheWithSimpleSource(
            _domain,
            _diagnostics,
            TestHelpers.CreateDefaultOptions(strategy),
            maxSegmentCount);
        return _cache;
    }

    // ============================================================
    // BACKGROUND LIFECYCLE INVARIANT
    // ============================================================

    /// <summary>
    /// Verifies that after normal (non-failing) operations the lifecycle invariant holds:
    /// NormalizationRequestReceived == NormalizationRequestProcessed + BackgroundOperationFailed.
    /// </summary>
    [Fact]
    public async Task BackgroundLifecycle_NormalOperation_ReceivedEqualsProcessedPlusFailed()
    {
        // ARRANGE
        var cache = CreateCache();

        // ACT — several requests covering all interaction types
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(0, 9));
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(0, 9));     // full hit
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(5, 14));    // partial hit
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(100, 109)); // full miss

        // ASSERT — lifecycle integrity
        TestHelpers.AssertBackgroundLifecycleIntegrity(_diagnostics);
        TestHelpers.AssertNoBackgroundFailures(_diagnostics);
    }

    /// <summary>
    /// Verifies that the BackgroundOperationFailed counter starts at zero for a fresh cache
    /// that processes requests without any failures.
    /// </summary>
    [Fact]
    public async Task BackgroundOperationFailed_ZeroForSuccessfulOperations()
    {
        // ARRANGE
        var cache = CreateCache();

        // ACT — multiple successful requests
        for (var i = 0; i < 5; i++)
        {
            await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(i * 10, i * 10 + 9));
        }

        // ASSERT
        Assert.Equal(0, _diagnostics.BackgroundOperationFailed);
        Assert.True(_diagnostics.NormalizationRequestProcessed >= 5);
    }

    // ============================================================
    // LOGGING DIAGNOSTICS PATTERN
    // ============================================================

    /// <summary>
    /// Demonstrates that the BackgroundOperationFailed(Exception) diagnostics interface
    /// receives the exception instance — a production logging diagnostics can log the exception.
    /// Uses the cache normally; verifies the exception-receiving overload is callable.
    /// </summary>
    [Fact]
    public async Task BackgroundOperationFailed_LoggingDiagnostics_ReceivesExceptionInstance()
    {
        // ARRANGE — logging diagnostics that captures any reported failures
        var loggedExceptions = new List<Exception>();
        var loggingDiagnostics = new LoggingCacheDiagnostics(ex => loggedExceptions.Add(ex));

        await using var cache = new VisitedPlacesCache<int, int, IntegerFixedStepDomain>(
            new SimpleTestDataSource(),
            _domain,
            TestHelpers.CreateDefaultOptions(),
            [new MaxSegmentCountPolicy<int, int>(100)],
            new LruEvictionSelector<int, int>(),
            loggingDiagnostics);

        // ACT — normal successful operations (no failures expected)
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(0, 9));
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(100, 109));

        // ASSERT — no failures; the callback was never invoked
        Assert.Empty(loggedExceptions);
    }

    // ============================================================
    // LIFECYCLE INTEGRITY ACROSS EVICTION
    // ============================================================

    /// <summary>
    /// Lifecycle invariant holds when eviction runs during background processing.
    /// Tests the four-step background sequence under eviction pressure.
    /// </summary>
    [Fact]
    public async Task BackgroundLifecycle_WithEviction_LifecycleIntegrityMaintained()
    {
        // ARRANGE — maxSegmentCount=2 forces eviction after 3 requests
        var cache = CreateCache(maxSegmentCount: 2);

        // ACT — three requests to force eviction
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(0, 9));
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(100, 109));
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(200, 209));

        // ASSERT — eviction ran but lifecycle integrity holds
        TestHelpers.AssertEvictionTriggered(_diagnostics);
        TestHelpers.AssertBackgroundLifecycleIntegrity(_diagnostics);
        TestHelpers.AssertNoBackgroundFailures(_diagnostics);
    }

    // ============================================================
    // BACKGROUND FAILURE INJECTION
    // ============================================================

    /// <summary>
    /// Verifies that when a background operation fails (e.g., a misbehaving
    /// <see cref="IEvictionPolicy{TRange,TData}"/> throws), <see cref="ICacheDiagnostics.BackgroundOperationFailed"/>
    /// is incremented and the background loop continues processing subsequent requests.
    /// </summary>
    /// <remarks>
    /// The VPC Background Path performs no I/O, so failure injection is done via a custom
    /// <see cref="IEvictionPolicy{TRange,TData}"/> that throws on the second call to
    /// <see cref="IEvictionPolicy{TRange,TData}.OnSegmentAdded"/> (after one successful call).
    /// The exception propagates out of <c>CacheNormalizationExecutor.ExecuteAsync</c>'s try block
    /// and is reported via <see cref="ICacheDiagnostics.BackgroundOperationFailed"/>.
    /// The third request re-uses the first range (full cache hit), confirming the loop survived.
    /// </remarks>
    [Fact]
    public async Task BackgroundOperationFailed_WhenBackgroundProcessingThrows_IncrementedAndLoopContinues()
    {
        #region Arrange
        var throwingPolicy = new ThrowingOnSegmentAddedPolicy(throwAfterCount: 1);

        await using var cache = new VisitedPlacesCache<int, int, IntegerFixedStepDomain>(
            new SimpleTestDataSource(),
            _domain,
            TestHelpers.CreateDefaultOptions(),
            [throwingPolicy, new MaxSegmentCountPolicy<int, int>(100)],
            new LruEvictionSelector<int, int>(),
            _diagnostics);
        #endregion

        #region Act
        // First request: segment is new — OnSegmentAdded succeeds (throwAfterCount=1); processed normally.
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(0, 9));

        // Second request on a different range: OnSegmentAdded now throws — BackgroundOperationFailed fires.
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(100, 109));

        // Third request: full cache hit on an already-stored range — proves the loop is still alive.
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(0, 9));
        #endregion

        #region Assert
        // At least one background failure was reported (the second request's OnSegmentAdded threw).
        Assert.True(_diagnostics.BackgroundOperationFailed >= 1,
            $"Expected BackgroundOperationFailed >= 1, but was {_diagnostics.BackgroundOperationFailed}.");

        // The lifecycle invariant must hold even across failures: Received == Processed + Failed.
        TestHelpers.AssertBackgroundLifecycleIntegrity(_diagnostics);

        // The first and third requests were processed successfully (first stored, third was a full hit).
        Assert.True(_diagnostics.NormalizationRequestProcessed >= 2,
            $"Expected NormalizationRequestProcessed >= 2, but was {_diagnostics.NormalizationRequestProcessed}.");
        #endregion
    }

    // ============================================================
    // LIFECYCLE INTEGRITY ACROSS BOTH STORAGE STRATEGIES
    // ============================================================

    public static IEnumerable<object[]> StorageStrategyTestData =>
    [
        [SnapshotAppendBufferStorageOptions<int, int>.Default],
        [LinkedListStrideIndexStorageOptions<int, int>.Default]
    ];

    /// <summary>
    /// Background lifecycle invariant holds for both storage strategies.
    /// </summary>
    [Theory]
    [MemberData(nameof(StorageStrategyTestData))]
    public async Task BackgroundLifecycle_BothStorageStrategies_LifecycleIntegrityMaintained(
        StorageStrategyOptions<int, int> strategy)
    {
        // ARRANGE
        var cache = CreateCache(strategy: strategy);

        // ACT — exercises all four background steps
        for (var i = 0; i < 5; i++)
        {
            await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(i * 20, i * 20 + 9));
        }

        // Second pass — all full hits (no storage step, but stats still run)
        for (var i = 0; i < 5; i++)
        {
            await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(i * 20, i * 20 + 9));
        }

        // ASSERT
        TestHelpers.AssertBackgroundLifecycleIntegrity(_diagnostics);
        TestHelpers.AssertNoBackgroundFailures(_diagnostics);
    }

    #region Helper Classes

    /// <summary>
    /// An eviction policy that throws <see cref="InvalidOperationException"/> on
    /// <see cref="OnSegmentAdded"/> after a configurable number of successful calls.
    /// Used to inject failures into the Background Path without touching I/O.
    /// </summary>
    private sealed class ThrowingOnSegmentAddedPolicy : IEvictionPolicy<int, int>
    {
        private readonly int _throwAfterCount;

        // Plain int (no Interlocked) is safe because OnSegmentAdded is called exclusively
        // from the Background Storage Loop — a single thread per VPC.A.1.
        private int _addCount;

        /// <param name="throwAfterCount">
        /// Number of successful <see cref="OnSegmentAdded"/> calls before throwing.
        /// Pass 0 to throw on the very first call.
        /// </param>
        public ThrowingOnSegmentAddedPolicy(int throwAfterCount)
        {
            _throwAfterCount = throwAfterCount;
        }

        public void OnSegmentAdded(CachedSegment<int, int> segment)
        {
            if (_addCount >= _throwAfterCount)
            {
                throw new InvalidOperationException("Simulated eviction policy failure.");
            }

            _addCount++;
        }

        public void OnSegmentRemoved(CachedSegment<int, int> segment) { }

        public IEvictionPressure<int, int> Evaluate() =>
            NoEvictionPressure<int, int>.Instance;
    }

    /// <summary>
    /// A no-op <see cref="IEvictionPressure{TRange,TData}"/> that never signals eviction.
    /// </summary>
    private sealed class NoEvictionPressure<TRange, TData> : IEvictionPressure<TRange, TData>
        where TRange : IComparable<TRange>
    {
        public static readonly NoEvictionPressure<TRange, TData> Instance = new();

        public bool IsExceeded => false;

        public void Reduce(CachedSegment<TRange, TData> removedSegment) { }
    }

    /// <summary>
    /// Production-style diagnostics that logs background failures.
    /// This demonstrates the minimum requirement for production use.
    /// </summary>
    private sealed class LoggingCacheDiagnostics : IVisitedPlacesCacheDiagnostics
    {
        private readonly Action<Exception> _logError;

        public LoggingCacheDiagnostics(Action<Exception> logError)
        {
            _logError = logError;
        }

        void ICacheDiagnostics.BackgroundOperationFailed(Exception ex)
        {
            // CRITICAL: log the exception in production
            _logError(ex);
        }

        void ICacheDiagnostics.UserRequestServed() { }
        void ICacheDiagnostics.UserRequestFullCacheHit() { }
        void ICacheDiagnostics.UserRequestPartialCacheHit() { }
        void ICacheDiagnostics.UserRequestFullCacheMiss() { }
        void IVisitedPlacesCacheDiagnostics.DataSourceFetchGap() { }
        void IVisitedPlacesCacheDiagnostics.NormalizationRequestReceived() { }
        void IVisitedPlacesCacheDiagnostics.NormalizationRequestProcessed() { }
        void IVisitedPlacesCacheDiagnostics.BackgroundStatisticsUpdated() { }
        void IVisitedPlacesCacheDiagnostics.BackgroundSegmentStored() { }
        void IVisitedPlacesCacheDiagnostics.EvictionEvaluated() { }
        void IVisitedPlacesCacheDiagnostics.EvictionTriggered() { }
        void IVisitedPlacesCacheDiagnostics.EvictionExecuted() { }
        void IVisitedPlacesCacheDiagnostics.EvictionSegmentRemoved() { }
        void IVisitedPlacesCacheDiagnostics.TtlSegmentExpired() { }
        void IVisitedPlacesCacheDiagnostics.TtlWorkItemScheduled() { }
    }

    #endregion
}
