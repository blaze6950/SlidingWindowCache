using Intervals.NET;
using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Domain.Extensions.Fixed;
using SlidingWindowCache.Configuration;
using SlidingWindowCache.DTO;
using Moq;
using SlidingWindowCache.Instrumentation;
using SlidingWindowCache.Extensions;

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
        UserCacheReadMode readMode = UserCacheReadMode.Snapshot
    ) => new(
        leftCacheSize: leftCacheSize,
        rightCacheSize: rightCacheSize,
        readMode: readMode,
        leftThreshold: leftThreshold,
        rightThreshold: rightThreshold,
        debounceDelay: debounceDelay ?? TimeSpan.FromMilliseconds(50)
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
    /// Waits for background rebalance to complete with timeout.
    /// </summary>
    public static async Task WaitForRebalanceAsync(int timeoutMs = 500)
    {
        await Task.Delay(timeoutMs);
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
                        for (var i = start; i <= end; i++) data.Add(i);
                        break;
                    case { IsStartInclusive: true, IsEndInclusive: false }:
                        for (var i = start; i < end; i++) data.Add(i);
                        break;
                    case { IsStartInclusive: false, IsEndInclusive: true }:
                        for (var i = start + 1; i <= end; i++) data.Add(i);
                        break;
                    default:
                        for (var i = start + 1; i < end; i++) data.Add(i);
                        break;
                }

                return data;
            });

        mock.Setup(ds => ds.FetchAsync(It.IsAny<IEnumerable<Range<int>>>(), It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<Range<int>>, CancellationToken>(async (ranges, ct) =>
            {
                var chunks = new List<RangeChunk<int, int>>();

                foreach (var range in ranges)
                {
                    var data = await mock.Object.FetchAsync(range, ct);
                    chunks.Add(new RangeChunk<int, int>(range, data));
                }

                return chunks;
            });

        return mock;
    }

    /// <summary>
    /// Creates a WindowCache instance with the specified options.
    /// </summary>
    public static WindowCache<int, int, IntegerFixedStepDomain> CreateCache(
        Mock<IDataSource<int, int>> mockDataSource,
        IntegerFixedStepDomain domain,
        WindowCacheOptions options) =>
        new(mockDataSource.Object, domain, options);

    /// <summary>
    /// Creates a WindowCache with default options and returns both cache and mock data source.
    /// </summary>
    public static (WindowCache<int, int, IntegerFixedStepDomain> cache, Mock<IDataSource<int, int>> mock)
        CreateCacheWithDefaults(IntegerFixedStepDomain domain, WindowCacheOptions? options = null,
            TimeSpan? fetchDelay = null)
    {
        var mock = CreateMockDataSource(domain, fetchDelay);
        var cache = CreateCache(mock, domain, options ?? CreateDefaultOptions());
        return (cache, mock);
    }

    /// <summary>
    /// Executes a request and waits for rebalance to complete.
    /// </summary>
    public static async Task<ReadOnlyMemory<int>> ExecuteRequestAndWaitForRebalance(
        WindowCache<int, int, IntegerFixedStepDomain> cache,
        Range<int> range,
        int rebalanceWaitMs = 200)
    {
        var data = await cache.GetDataAsync(range, CancellationToken.None);
        await WaitForRebalanceAsync(rebalanceWaitMs);
        return data;
    }

    /// <summary>
    /// Asserts that user received correct data matching the requested range.
    /// </summary>
    public static void AssertUserDataCorrect(ReadOnlyMemory<int> data, Range<int> range)
    {
        VerifyDataMatchesRange(data, range);
    }

    /// <summary>
    /// Asserts that User Path did not mutate cache (single-writer architecture).
    /// </summary>
    public static void AssertNoUserPathMutations()
    {
        Assert.Equal(0, CacheInstrumentationCounters.CacheExpanded);
        Assert.Equal(0, CacheInstrumentationCounters.CacheReplaced);
    }

    /// <summary>
    /// Asserts that rebalance intent was published.
    /// </summary>
    public static void AssertIntentPublished(int expectedCount = -1)
    {
        if (expectedCount >= 0)
        {
            Assert.Equal(expectedCount, CacheInstrumentationCounters.RebalanceIntentPublished);
        }
        else
        {
            Assert.True(CacheInstrumentationCounters.RebalanceIntentPublished > 0,
                $"Intent should be published, but actual count was {CacheInstrumentationCounters.RebalanceIntentPublished}");
        }
    }

    /// <summary>
    /// Asserts that rebalance was cancelled (at either intent or execution stage).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Due to timing, cancellation can occur at two distinct lifecycle points:
    /// </para>
    /// <list type="number">
    /// <item>
    /// <description><strong>Intent-level cancellation</strong>: When a new request arrives while the previous
    /// rebalance is still in debounce delay (before execution starts). This increments
    /// <see cref="CacheInstrumentationCounters.RebalanceIntentCancelled"/>.</description>
    /// </item>
    /// <item>
    /// <description><strong>Execution-level cancellation</strong>: When a new request arrives after the debounce
    /// delay completed and execution has started. This increments
    /// <see cref="CacheInstrumentationCounters.RebalanceExecutionCancelled"/>.</description>
    /// </item>
    /// </list>
    /// <para>
    /// This method checks the <strong>total</strong> cancellations across both stages, making assertions
    /// stable regardless of timing variations. Most tests care that cancellation occurred, not the
    /// specific lifecycle stage where it happened.
    /// </para>
    /// </remarks>
    /// <param name="minExpected">Minimum number of total cancellations expected (default: 1).</param>
    public static void AssertRebalancePathCancelled(int minExpected = 1)
    {
        var totalCancelled = CacheInstrumentationCounters.RebalanceIntentCancelled +
                             CacheInstrumentationCounters.RebalanceExecutionCancelled;
        Assert.True(totalCancelled >= minExpected,
            $"At least {minExpected} cancellation(s) expected (intent or execution), but actual count was {totalCancelled} " +
            $"(IntentCancelled: {CacheInstrumentationCounters.RebalanceIntentCancelled}, " +
            $"ExecutionCancelled: {CacheInstrumentationCounters.RebalanceExecutionCancelled})");
    }

    /// <summary>
    /// Asserts rebalance execution lifecycle integrity: Started == Completed + Cancelled.
    /// </summary>
    public static void AssertRebalanceLifecycleIntegrity()
    {
        var started = CacheInstrumentationCounters.RebalanceExecutionStarted;
        var completed = CacheInstrumentationCounters.RebalanceExecutionCompleted;
        var executionsCancelled = CacheInstrumentationCounters.RebalanceExecutionCancelled;
        Assert.Equal(started, completed + executionsCancelled);
    }

    /// <summary>
    /// Asserts that rebalance was skipped due to NoRebalanceRange policy.
    /// </summary>
    public static void AssertRebalanceSkippedDueToPolicy()
    {
        var skipped = CacheInstrumentationCounters.RebalanceSkippedNoRebalanceRange;
        Assert.True(skipped > 0, $"Expected at least one rebalance to be skipped due to NoRebalanceRange policy, but found {skipped}.");

        Assert.Equal(0, CacheInstrumentationCounters.RebalanceExecutionStarted);
        Assert.Equal(0, CacheInstrumentationCounters.RebalanceExecutionCompleted);
    }

    /// <summary>
    /// Asserts that rebalance execution completed successfully.
    /// </summary>
    public static void AssertRebalanceCompleted(int minExpected = 1)
    {
        Assert.True(CacheInstrumentationCounters.RebalanceExecutionCompleted >= minExpected,
            $"Rebalance should have completed at least {minExpected} time(s), but actual count was {CacheInstrumentationCounters.RebalanceExecutionCompleted}");
    }

    /// <summary>
    /// Asserts that the request resulted in a full cache hit (all data served from cache).
    /// </summary>
    public static void AssertFullCacheHit(int expectedCount = 1)
    {
        Assert.Equal(expectedCount, CacheInstrumentationCounters.UserRequestFullCacheHit);
    }

    /// <summary>
    /// Asserts that the request resulted in a partial cache hit (some data from cache, some from data source).
    /// </summary>
    public static void AssertPartialCacheHit(int expectedCount = 1)
    {
        Assert.Equal(expectedCount, CacheInstrumentationCounters.UserRequestPartialCacheHit);
    }

    /// <summary>
    /// Asserts that the request resulted in a full cache miss (all data fetched from data source).
    /// </summary>
    public static void AssertFullCacheMiss(int expectedCount = 1)
    {
        Assert.Equal(expectedCount, CacheInstrumentationCounters.UserRequestFullCacheMiss);
    }
}