using Intervals.NET;
using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Domain.Extensions.Fixed;
using Moq;
using SlidingWindowCache.Infrastructure.Instrumentation;
using SlidingWindowCache.Public;
using SlidingWindowCache.Public.Configuration;
using SlidingWindowCache.Public.Dto;

namespace SlidingWindowCache.Invariants.Tests.TestInfrastructure;

/// <summary>
/// Helper methods for creating test components.
/// Uses Intervals.NET packages for proper range handling and domain calculations.
/// </summary>
public static class TestHelpers
{
    /// <summary>
    /// Creates a standard integer fixed-step domain for testing.
    /// </summary>
    public static IntegerFixedStepDomain CreateIntDomain() => new();

    /// <summary>
    /// Creates a closed range [start, end] (both boundaries inclusive) using Intervals.NET factory.
    /// This is the standard range type used throughout the WindowCache system.
    /// </summary>
    /// <param name="start">The start value (inclusive).</param>
    /// <param name="end">The end value (inclusive).</param>
    /// <returns>A closed range [start, end].</returns>
    public static Range<int> CreateRange(int start, int end) => Intervals.NET.Factories.Range.Closed<int>(start, end);

    /// <summary>
    /// Creates default cache options for testing.
    /// </summary>
    public static WindowCacheOptions CreateDefaultOptions(
        double leftCacheSize = 1.0, // The left cache size equals to the requested range size
        double rightCacheSize = 1.0, // The right cache size equals to the requested range size
        double? leftThreshold = 0.2, // 20% threshold on the left side
        double? rightThreshold = 0.2, // 20% threshold on the right side
        TimeSpan? debounceDelay = null, // Default debounce delay of 50ms
        UserCacheReadMode readMode = UserCacheReadMode.Snapshot,
        int? rebalanceQueueCapacity = null // null = task-based (unbounded), >= 1 = channel-based (bounded)
    ) => new(
        leftCacheSize: leftCacheSize,
        rightCacheSize: rightCacheSize,
        readMode: readMode,
        leftThreshold: leftThreshold,
        rightThreshold: rightThreshold,
        debounceDelay: debounceDelay ?? TimeSpan.FromMilliseconds(50),
        rebalanceQueueCapacity: rebalanceQueueCapacity
    );

    /// <summary>
    /// Calculates the expected desired cache range using the same logic as ProportionalRangePlanner.
    /// This helper ensures tests verify the actual planner behavior rather than hardcoding imagined values.
    /// </summary>
    /// <param name="requestedRange">The range requested by the user.</param>
    /// <param name="options">The cache options containing leftCacheSize and rightCacheSize.</param>
    /// <param name="domain">The domain for range calculations.</param>
    /// <returns>The expected desired cache range after expansion.</returns>
    public static Range<int> CalculateExpectedDesiredRange(
        Range<int> requestedRange,
        WindowCacheOptions options,
        IntegerFixedStepDomain domain)
    {
        // Mimic ProportionalRangePlanner.Plan() logic
        var size = requestedRange.Span(domain);
        var left = (long)(size.Value * options.LeftCacheSize);
        var right = (long)(size.Value * options.RightCacheSize);

        return requestedRange.Expand(domain, left, right);
    }

    /// <summary>
    /// Verifies that the data matches the expected range values using Intervals.NET domain calculations.
    /// Properly handles range inclusivity.
    /// </summary>
    public static void VerifyDataMatchesRange(ReadOnlyMemory<int> data, Range<int> expectedRange)
    {
        var span = data.Span;

        // Use Intervals.NET domain to calculate expected length
        var domain = new IntegerFixedStepDomain();
        var expectedLength = (int)expectedRange.Span(domain);

        Assert.Equal(expectedLength, span.Length);

        // Verify data values match the range
        var start = (int)expectedRange.Start;

        switch (expectedRange)
        {
            // For closed ranges [start, end], data should be sequential from start
            case { IsStartInclusive: true, IsEndInclusive: true }:
                {
                    for (var i = 0; i < span.Length; i++)
                    {
                        Assert.Equal(start + i, span[i]);
                    }

                    break;
                }
            case { IsStartInclusive: true, IsEndInclusive: false }:
                {
                    // [start, end) - start inclusive, end exclusive
                    for (var i = 0; i < span.Length; i++)
                    {
                        Assert.Equal(start + i, span[i]);
                    }

                    break;
                }
            case { IsStartInclusive: false, IsEndInclusive: true }:
                {
                    // (start, end] - start exclusive, end inclusive
                    for (var i = 0; i < span.Length; i++)
                    {
                        Assert.Equal(start + 1 + i, span[i]);
                    }

                    break;
                }
            default:
                {
                    // (start, end) - both exclusive
                    for (var i = 0; i < span.Length; i++)
                    {
                        Assert.Equal(start + 1 + i, span[i]);
                    }

                    break;
                }
        }
    }

    /// <summary>
    /// Creates a mock IDataSource that generates sequential integer data for any requested range.
    /// Properly handles range inclusivity using Intervals.NET domain calculations.
    /// </summary>
    public static Mock<IDataSource<int, int>> CreateMockDataSource(IntegerFixedStepDomain domain,
        TimeSpan? fetchDelay = null)
    {
        var mock = new Mock<IDataSource<int, int>>();

        mock.Setup(ds => ds.FetchAsync(It.IsAny<Range<int>>(), It.IsAny<CancellationToken>()))
            .Returns<Range<int>, CancellationToken>(async (range, ct) =>
            {
                if (fetchDelay.HasValue)
                {
                    await Task.Delay(fetchDelay.Value, ct);
                }

                // Use Intervals.NET domain to properly calculate range span
                var span = range.Span(domain);
                var data = new List<int>((int)span);

                // Generate data respecting range inclusivity
                var start = (int)range.Start;
                var end = (int)range.End;

                switch (range)
                {
                    case { IsStartInclusive: true, IsEndInclusive: true }:
                        for (var i = start; i <= end; i++)
                        {
                            data.Add(i);
                        }

                        break;
                    case { IsStartInclusive: true, IsEndInclusive: false }:
                        for (var i = start; i < end; i++)
                        {
                            data.Add(i);
                        }

                        break;
                    case { IsStartInclusive: false, IsEndInclusive: true }:
                        for (var i = start + 1; i <= end; i++)
                        {
                            data.Add(i);
                        }

                        break;
                    default:
                        for (var i = start + 1; i < end; i++)
                        {
                            data.Add(i);
                        }

                        break;
                }

                return new RangeChunk<int, int>(range, data);
            });

        mock.Setup(ds => ds.FetchAsync(It.IsAny<IEnumerable<Range<int>>>(), It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<Range<int>>, CancellationToken>(async (ranges, ct) =>
            {
                var chunks = new List<RangeChunk<int, int>>();

                foreach (var range in ranges)
                {
                    var chunk = await mock.Object.FetchAsync(range, ct);
                    chunks.Add(chunk);
                }

                return chunks;
            });

        return mock;
    }

    /// <summary>
    /// Creates a mock IDataSource with fetch tracking to verify which ranges were requested.
    /// Used for testing incremental fetch optimization and data preservation invariants.
    /// </summary>
    public static (Mock<IDataSource<int, int>> mock, List<Range<int>> fetchedRanges) CreateTrackingMockDataSource(
        IntegerFixedStepDomain domain,
        TimeSpan? fetchDelay = null)
    {
        var fetchedRanges = new List<Range<int>>();
        var mock = new Mock<IDataSource<int, int>>();

        mock.Setup(ds => ds.FetchAsync(It.IsAny<Range<int>>(), It.IsAny<CancellationToken>()))
            .Returns<Range<int>, CancellationToken>(async (range, ct) =>
            {
                lock (fetchedRanges)
                {
                    fetchedRanges.Add(range);
                }

                if (fetchDelay.HasValue)
                {
                    await Task.Delay(fetchDelay.Value, ct);
                }

                var span = range.Span(domain);
                var data = new List<int>((int)span);
                var start = (int)range.Start;
                var end = (int)range.End;

                switch (range)
                {
                    case { IsStartInclusive: true, IsEndInclusive: true }:
                        for (var i = start; i <= end; i++)
                        {
                            data.Add(i);
                        }
                        break;
                    case { IsStartInclusive: true, IsEndInclusive: false }:
                        for (var i = start; i < end; i++)
                        {
                            data.Add(i);
                        }
                        break;
                    case { IsStartInclusive: false, IsEndInclusive: true }:
                        for (var i = start + 1; i <= end; i++)
                        {
                            data.Add(i);
                        }
                        break;
                    default:
                        for (var i = start + 1; i < end; i++)
                        {
                            data.Add(i);
                        }
                        break;
                }

                return new RangeChunk<int, int>(range, data);
            });

        mock.Setup(ds => ds.FetchAsync(It.IsAny<IEnumerable<Range<int>>>(), It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<Range<int>>, CancellationToken>(async (ranges, ct) =>
            {
                var chunks = new List<RangeChunk<int, int>>();

                foreach (var range in ranges)
                {
                    var chunk = await mock.Object.FetchAsync(range, ct);
                    chunks.Add(chunk);
                }

                return chunks;
            });

        return (mock, fetchedRanges);
    }

    /// <summary>
    /// Creates a WindowCache instance with the specified options.
    /// </summary>
    public static WindowCache<int, int, IntegerFixedStepDomain> CreateCache(
        Mock<IDataSource<int, int>> mockDataSource,
        IntegerFixedStepDomain domain,
        WindowCacheOptions options,
        EventCounterCacheDiagnostics cacheDiagnostics) =>
        new(mockDataSource.Object, domain, options, cacheDiagnostics);

    /// <summary>
    /// Creates a WindowCache with default options and returns both cache and mock data source.
    /// </summary>
    public static (WindowCache<int, int, IntegerFixedStepDomain> cache, Mock<IDataSource<int, int>> mock)
        CreateCacheWithDefaults(
            IntegerFixedStepDomain domain,
            EventCounterCacheDiagnostics cacheDiagnostics,
            WindowCacheOptions? options = null,
            TimeSpan? fetchDelay = null
        )
    {
        var mock = CreateMockDataSource(domain, fetchDelay);
        var cache = CreateCache(mock, domain, options ?? CreateDefaultOptions(), cacheDiagnostics);
        return (cache, mock);
    }

    /// <summary>
    /// Executes a request and waits for rebalance to complete before returning.
    /// </summary>
    public static async Task<ReadOnlyMemory<int>> ExecuteRequestAndWaitForRebalance(
        WindowCache<int, int, IntegerFixedStepDomain> cache,
        Range<int> range)
    {
        var result = await cache.GetDataAsync(range, CancellationToken.None);
        await cache.WaitForIdleAsync();
        return result.Data;
    }

    /// <summary>
    /// Asserts that user received correct data matching the requested range.
    /// </summary>
    public static void AssertUserDataCorrect(ReadOnlyMemory<int> data, Range<int> range)
    {
        VerifyDataMatchesRange(data, range);
    }

    /// <summary>
    /// Asserts that User Path did not trigger cache extension analysis (single-writer architecture).
    /// </summary>
    /// <remarks>
    /// Note: CacheExpanded and CacheReplaced counters are incremented by the shared CacheDataExtensionService
    /// during range analysis (when determining what data needs to be fetched). They track planning, not actual
    /// cache mutations. This assertion verifies that User Path didn't call ExtendCacheAsync, which would
    /// increment these counters. Actual cache mutations (via Rematerialize) only occur in Rebalance Execution.
    /// 
    /// In test scenarios, prior rebalance operations typically expand the cache enough that subsequent
    /// User Path requests are full hits, avoiding calls to ExtendCacheAsync entirely.
    /// </remarks>
    public static void AssertNoUserPathMutations(EventCounterCacheDiagnostics cacheDiagnostics)
    {
        Assert.Equal(0, cacheDiagnostics.CacheExpanded);
        Assert.Equal(0, cacheDiagnostics.CacheReplaced);
    }

    /// <summary>
    /// Asserts that rebalance intent was published.
    /// </summary>
    public static void AssertIntentPublished(EventCounterCacheDiagnostics cacheDiagnostics, int expectedCount = -1)
    {
        if (expectedCount >= 0)
        {
            Assert.Equal(expectedCount, cacheDiagnostics.RebalanceIntentPublished);
        }
        else
        {
            Assert.True(cacheDiagnostics.RebalanceIntentPublished > 0,
                $"Intent should be published, but actual count was {cacheDiagnostics.RebalanceIntentPublished}");
        }
    }

    /// <summary>
    /// Asserts that rebalance was cancelled (at either intent or execution stage).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Cancellation is a coordination mechanism triggered by scheduling decisions, not automatic
    /// request-driven behavior.</strong> Cancellation occurs ONLY when the Decision Engine validates that
    /// a new rebalance is necessary. This method verifies that IF cancellation occurred, it was properly
    /// tracked in the lifecycle.
    /// </para>
    /// <para>
    /// Due to timing, cancellation can occur at two distinct lifecycle points:
    /// </para>
    /// <list type="number">
    /// <item>
    /// <description><strong>Intent-level cancellation</strong>: When a new request arrives while the previous
    /// rebalance is still in debounce delay (before execution starts). This increments
    /// <see cref="EventCounterCacheDiagnostics.RebalanceIntentCancelled"/>.</description>
    /// </item>
    /// <item>
    /// <description><strong>Execution-level cancellation</strong>: When a new request arrives after the debounce
    /// delay completed and execution has started. This increments
    /// <see cref="EventCounterCacheDiagnostics.RebalanceExecutionCancelled"/>.</description>
    /// </item>
    /// </list>
    /// <para>
    /// This method checks the <strong>total</strong> cancellations across both stages, making assertions
    /// stable regardless of timing variations. Most tests care that cancellation occurred, not the
    /// specific lifecycle stage where it happened.
    /// </para>
    /// </remarks>
    /// <param name="cacheDiagnostics">
    /// The diagnostics instance to check for cancellation counts. The method will sum both intent and execution cancellations to determine if the expected number of cancellations occurred.
    /// </param>
    /// <param name="minExpected">Minimum number of total cancellations expected (default: 1).</param>
    public static void AssertRebalancePathCancelled(EventCounterCacheDiagnostics cacheDiagnostics, int minExpected = 1)
    {
        var totalCancelled = cacheDiagnostics.RebalanceIntentCancelled +
                             cacheDiagnostics.RebalanceExecutionCancelled;
        Assert.True(totalCancelled >= minExpected,
            $"At least {minExpected} cancellation(s) expected (intent or execution), but actual count was {totalCancelled} " +
            $"(IntentCancelled: {cacheDiagnostics.RebalanceIntentCancelled}, " +
            $"ExecutionCancelled: {cacheDiagnostics.RebalanceExecutionCancelled})");
    }

    /// <summary>
    /// Asserts rebalance execution lifecycle integrity: Started == Completed + Cancelled.
    /// </summary>
    public static void AssertRebalanceLifecycleIntegrity(EventCounterCacheDiagnostics cacheDiagnostics)
    {
        var started = cacheDiagnostics.RebalanceExecutionStarted;
        var completed = cacheDiagnostics.RebalanceExecutionCompleted;
        var executionsCancelled = cacheDiagnostics.RebalanceExecutionCancelled;
        Assert.Equal(started, completed + executionsCancelled);
    }

    /// <summary>
    /// Asserts that rebalance was skipped due to current cache NoRebalanceRange (Stage 1).
    /// </summary>
    public static void AssertRebalanceSkippedDueToPolicyStage1(EventCounterCacheDiagnostics cacheDiagnostics)
    {
        var skipped = cacheDiagnostics.RebalanceSkippedCurrentNoRebalanceRange;
        Assert.True(skipped > 0,
            $"Expected at least one rebalance to be skipped due to current NoRebalanceRange (Stage 1), but found {skipped}.");
        Assert.Equal(0, cacheDiagnostics.RebalanceExecutionStarted);
        Assert.Equal(0, cacheDiagnostics.RebalanceExecutionCompleted);
    }

    /// <summary>
    /// Asserts that rebalance was skipped due to pending rebalance NoRebalanceRange (Stage 2).
    /// </summary>
    public static void AssertRebalanceSkippedDueToPolicyStage2(EventCounterCacheDiagnostics cacheDiagnostics)
    {
        var skipped = cacheDiagnostics.RebalanceSkippedPendingNoRebalanceRange;
        Assert.True(skipped > 0,
            $"Expected at least one rebalance to be skipped due to pending NoRebalanceRange (Stage 2), but found {skipped}.");
        Assert.Equal(0, cacheDiagnostics.RebalanceExecutionStarted);
        Assert.Equal(0, cacheDiagnostics.RebalanceExecutionCompleted);
    }

    /// <summary>
    /// Asserts that rebalance was skipped due to NoRebalanceRange policy (either stage).
    /// </summary>
    public static void AssertRebalanceSkippedDueToPolicy(EventCounterCacheDiagnostics cacheDiagnostics)
    {
        var skippedStage1 = cacheDiagnostics.RebalanceSkippedCurrentNoRebalanceRange;
        var skippedStage2 = cacheDiagnostics.RebalanceSkippedPendingNoRebalanceRange;
        var totalSkipped = skippedStage1 + skippedStage2;

        Assert.True(totalSkipped > 0,
            $"Expected at least one rebalance to be skipped due to NoRebalanceRange policy, but found Stage1={skippedStage1}, Stage2={skippedStage2}.");
        Assert.Equal(0, cacheDiagnostics.RebalanceExecutionStarted);
        Assert.Equal(0, cacheDiagnostics.RebalanceExecutionCompleted);
    }

    /// <summary>
    /// Asserts that rebalance execution completed successfully.
    /// </summary>
    public static void AssertRebalanceCompleted(EventCounterCacheDiagnostics cacheDiagnostics, int minExpected = 1)
    {
        Assert.True(cacheDiagnostics.RebalanceExecutionCompleted >= minExpected,
            $"Rebalance should have completed at least {minExpected} time(s), but actual count was {cacheDiagnostics.RebalanceExecutionCompleted}");
    }

    /// <summary>
    /// Asserts that the request resulted in a full cache hit (all data served from cache).
    /// </summary>
    public static void AssertFullCacheHit(EventCounterCacheDiagnostics cacheDiagnostics, int expectedCount = 1)
    {
        Assert.Equal(expectedCount, cacheDiagnostics.UserRequestFullCacheHit);
    }

    /// <summary>
    /// Asserts that the request resulted in a partial cache hit (some data from cache, some from data source).
    /// </summary>
    public static void AssertPartialCacheHit(EventCounterCacheDiagnostics cacheDiagnostics, int expectedCount = 1)
    {
        Assert.Equal(expectedCount, cacheDiagnostics.UserRequestPartialCacheHit);
    }

    /// <summary>
    /// Asserts that the request resulted in a full cache miss (all data fetched from data source).
    /// </summary>
    public static void AssertFullCacheMiss(EventCounterCacheDiagnostics cacheDiagnostics, int expectedCount = 1)
    {
        Assert.Equal(expectedCount, cacheDiagnostics.UserRequestFullCacheMiss);
    }

    /// <summary>
    /// Asserts that data was fetched from data source for a complete range (cold start or full miss).
    /// </summary>
    public static void AssertDataSourceFetchedFullRange(EventCounterCacheDiagnostics cacheDiagnostics,
        int expectedCount = 1)
    {
        Assert.Equal(expectedCount, cacheDiagnostics.DataSourceFetchSingleRange);
    }

    /// <summary>
    /// Asserts that data was fetched from data source for missing segments only (partial hit optimization).
    /// </summary>
    public static void AssertDataSourceFetchedMissingSegments(EventCounterCacheDiagnostics cacheDiagnostics,
        int expectedCount = 1)
    {
        Assert.Equal(expectedCount, cacheDiagnostics.DataSourceFetchMissingSegments);
    }

    /// <summary>
    /// Asserts that rebalance was scheduled (decision engine validated rebalance as necessary).
    /// </summary>
    /// <param name="cacheDiagnostics">The diagnostics instance to check.</param>
    /// <param name="expectedCount">Expected number of times rebalance was scheduled (default: 1).</param>
    public static void AssertRebalanceScheduled(EventCounterCacheDiagnostics cacheDiagnostics, int expectedCount = 1)
    {
        Assert.Equal(expectedCount, cacheDiagnostics.RebalanceScheduled);
    }

    /// <summary>
    /// Asserts that rebalance was skipped because DesiredCacheRange equals CurrentCacheRange (Stage 3 / D.28).
    /// </summary>
    /// <param name="cacheDiagnostics">The diagnostics instance to check.</param>
    /// <param name="minExpected">Minimum number of same-range skips expected (default: 1).</param>
    public static void AssertRebalanceSkippedSameRange(EventCounterCacheDiagnostics cacheDiagnostics,
        int minExpected = 1)
    {
        Assert.True(cacheDiagnostics.RebalanceSkippedSameRange >= minExpected,
            $"Expected at least {minExpected} rebalance skip(s) due to same range (DesiredCacheRange == CurrentCacheRange), " +
            $"but found {cacheDiagnostics.RebalanceSkippedSameRange}.");
        Assert.Equal(0, cacheDiagnostics.RebalanceExecutionStarted);
        Assert.Equal(0, cacheDiagnostics.RebalanceExecutionCompleted);
    }

    /// <summary>
    /// Asserts the complete rebalance decision-to-execution pipeline lifecycle integrity.
    /// Validates that all intents are accounted for across decision stages and execution.
    /// </summary>
    /// <remarks>
    /// Decision Pipeline Stages:
    /// - Stage 1: Current NoRebalanceRange check → SkippedCurrentNoRebalanceRange
    /// - Stage 2: Pending NoRebalanceRange check → SkippedPendingNoRebalanceRange  
    /// - Stage 3: DesiredCacheRange == CurrentCacheRange → SkippedSameRange
    /// - All stages pass → RebalanceScheduled
    /// - Intent superseded before decision → IntentCancelled
    /// 
    /// Execution Lifecycle:
    /// - Scheduled → ExecutionStarted (unless cancelled between scheduling and execution)
    /// - Started → (Completed | ExecutionCancelled | ExecutionFailed)
    /// </remarks>
    public static void AssertRebalancePipelineIntegrity(EventCounterCacheDiagnostics cacheDiagnostics)
    {
        var intentPublished = cacheDiagnostics.RebalanceIntentPublished;
        var scheduled = cacheDiagnostics.RebalanceScheduled;
        var skippedStage1 = cacheDiagnostics.RebalanceSkippedCurrentNoRebalanceRange;
        var skippedStage2 = cacheDiagnostics.RebalanceSkippedPendingNoRebalanceRange;
        var skippedStage3 = cacheDiagnostics.RebalanceSkippedSameRange;
        var intentCancelled = cacheDiagnostics.RebalanceIntentCancelled;

        // Decision phase: All intents must be accounted for
        var totalDecisionOutcomes = scheduled + skippedStage1 + skippedStage2 + skippedStage3 + intentCancelled;
        Assert.True(totalDecisionOutcomes <= intentPublished,
            $"Decision outcomes ({totalDecisionOutcomes}) cannot exceed intents published ({intentPublished}). " +
            $"Breakdown: Scheduled={scheduled}, SkippedStage1={skippedStage1}, SkippedStage2={skippedStage2}, " +
            $"SkippedStage3={skippedStage3}, IntentCancelled={intentCancelled}");

        // Execution phase: Verify lifecycle integrity
        AssertRebalanceLifecycleIntegrity(cacheDiagnostics);
    }
}
