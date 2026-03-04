using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Caching.Tests.Infrastructure.DataSources;
using Intervals.NET.Caching.Public;
using Intervals.NET.Caching.Public.Cache;
using Intervals.NET.Caching.Public.Configuration;
using Intervals.NET.Caching.Public.Instrumentation;

namespace Intervals.NET.Caching.Integration.Tests;

/// <summary>
/// Tests for validating proper exception handling in background rebalance operations.
/// Demonstrates the critical importance of handling RebalanceExecutionFailed events.
/// </summary>
public class RebalanceExceptionHandlingTests : IDisposable
{
    private readonly EventCounterCacheDiagnostics _diagnostics;

    public RebalanceExceptionHandlingTests()
    {
        _diagnostics = new EventCounterCacheDiagnostics();
    }

    public void Dispose()
    {
        _diagnostics.Reset();
    }

    /// <summary>
    /// Demonstrates that RebalanceExecutionFailed is properly recorded when data source throws during rebalance.
    /// This validates that exceptions in background operations are caught and reported.
    /// </summary>
    [Fact]
    public async Task RebalanceExecutionFailed_IsRecorded_WhenDataSourceThrowsDuringRebalance()
    {
        // Arrange: Create a data source that throws on the second fetch (during rebalance)
        var callCount = 0;
        var faultyDataSource = new FaultyDataSource<int, string>(
            fetchSingleRange: range =>
            {
                callCount++;
                if (callCount == 1)
                {
                    // First call (user request) succeeds
                    return FaultyDataSource<int, string>.GenerateStringData(range);
                }

                // Second call (rebalance) fails
                throw new InvalidOperationException("Simulated data source failure during rebalance");
            }
        );

        var options = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0.0, // Trigger rebalance immediately
            rightThreshold: 0.0,
            debounceDelay: TimeSpan.FromMilliseconds(10)
        );

        await using var cache = new WindowCache<int, string, IntegerFixedStepDomain>(
            faultyDataSource,
            new IntegerFixedStepDomain(),
            options,
            _diagnostics
        );

        // Act: Make a request that will trigger a rebalance
        await cache.GetDataAsync(Intervals.NET.Factories.Range.Closed<int>(100, 110),
            CancellationToken.None);

        // Wait for background rebalance to fail
        await cache.WaitForIdleAsync();

        // Assert: Verify the failure was recorded
        Assert.Equal(1, _diagnostics.UserRequestServed);
        Assert.Equal(1, _diagnostics.RebalanceIntentPublished);
        Assert.Equal(1, _diagnostics.RebalanceExecutionStarted);
        Assert.Equal(1, _diagnostics.RebalanceExecutionFailed); // ⚠️ This is the critical event
        Assert.Equal(0, _diagnostics.RebalanceExecutionCompleted); // Should not complete
    }

    /// <summary>
    /// Demonstrates that user requests continue to work even after rebalance failures.
    /// The cache remains operational despite background operation failures.
    /// </summary>
    [Fact]
    public async Task UserRequests_ContinueToWork_AfterRebalanceFailure()
    {
        // Arrange: Create a data source that fails only during rebalance (second call)
        var callCount = 0;
        var partiallyFaultyDataSource = new FaultyDataSource<int, string>(
            fetchSingleRange: range =>
            {
                callCount++;
                if (callCount == 2)
                {
                    // Second call (rebalance) fails
                    throw new InvalidOperationException("Rebalance fetch failed");
                }

                // Other calls succeed
                return FaultyDataSource<int, string>.GenerateStringData(range);
            }
        );

        var options = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0.0,
            rightThreshold: 0.0,
            debounceDelay: TimeSpan.FromMilliseconds(10)
        );

        await using var cache = new WindowCache<int, string, IntegerFixedStepDomain>(
            partiallyFaultyDataSource,
            new IntegerFixedStepDomain(),
            options,
            _diagnostics
        );

        // Act: First request succeeds, triggers failed rebalance
        var data1 = await cache.GetDataAsync(Intervals.NET.Factories.Range.Closed<int>(100, 110),
            CancellationToken.None);
        await cache.WaitForIdleAsync();

        // Second request should still work (user path bypasses failed rebalance)
        var data2 = await cache.GetDataAsync(Intervals.NET.Factories.Range.Closed<int>(200, 210),
            CancellationToken.None);
        await cache.WaitForIdleAsync();

        // Assert: Both requests succeeded despite rebalance failure
        Assert.Equal(2, _diagnostics.UserRequestServed);
        Assert.Equal(11, data1.Data.Length);
        Assert.Equal(11, data2.Data.Length);

        // Verify at least one rebalance failed
        Assert.True(_diagnostics.RebalanceExecutionFailed >= 1,
            "Expected at least one rebalance failure but got none. " +
            "Without proper exception handling, this would have crashed the application.");
    }

    /// <summary>
    /// Demonstrates a production-ready diagnostics implementation with proper logging.
    /// This is the recommended pattern for production applications.
    /// </summary>
    [Fact]
    public async Task ProductionDiagnostics_ProperlyLogsRebalanceFailures()
    {
        // Arrange: Create a logging diagnostics implementation
        var loggedExceptions = new List<Exception>();
        var loggingDiagnostics = new LoggingCacheDiagnostics(ex => loggedExceptions.Add(ex));

        var callCount = 0;
        var faultyDataSource = new FaultyDataSource<int, string>(
            fetchSingleRange: range =>
            {
                callCount++;
                if (callCount == 1)
                {
                    // First call (user request) succeeds
                    return FaultyDataSource<int, string>.GenerateStringData(range);
                }

                // Second call (rebalance) fails
                throw new InvalidOperationException("Data source is unhealthy");
            }
        );

        var options = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0.0,
            rightThreshold: 0.0,
            debounceDelay: TimeSpan.FromMilliseconds(10)
        );

        await using var cache = new WindowCache<int, string, IntegerFixedStepDomain>(
            faultyDataSource,
            new IntegerFixedStepDomain(),
            options,
            loggingDiagnostics
        );

        // Act: Trigger a rebalance failure
        await cache.GetDataAsync(Intervals.NET.Factories.Range.Closed<int>(100, 110), CancellationToken.None);
        await cache.WaitForIdleAsync();

        // Assert: Exception was properly logged
        Assert.True(loggedExceptions.Count >= 1,
            "Production implementations MUST log all rebalance failures. " +
            "Silent failures lead to degraded performance with no diagnostics.");

        var exception = loggedExceptions[0];
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("Data source is unhealthy", exception.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(3)]
    public async Task RebalanceFailure_IsRecorded_ForBothExecutionStrategies(int? rebalanceQueueCapacity)
    {
        _diagnostics.Reset();

        // Arrange: data source fails on second fetch (rebalance)
        var callCount = 0;
        var faultyDataSource = new FaultyDataSource<int, string>(
            fetchSingleRange: range =>
            {
                callCount++;
                if (callCount == 2)
                {
                    throw new InvalidOperationException("Simulated rebalance failure");
                }

                return FaultyDataSource<int, string>.GenerateStringData(range);
            }
        );

        var options = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0.0,
            rightThreshold: 0.0,
            debounceDelay: TimeSpan.FromMilliseconds(10),
            rebalanceQueueCapacity: rebalanceQueueCapacity
        );

        await using var cache = new WindowCache<int, string, IntegerFixedStepDomain>(
            faultyDataSource,
            new IntegerFixedStepDomain(),
            options,
            _diagnostics
        );

        // Act
        await cache.GetDataAsync(Intervals.NET.Factories.Range.Closed<int>(100, 110), CancellationToken.None);
        await cache.WaitForIdleAsync();

        // Assert
        Assert.Equal(1, _diagnostics.RebalanceExecutionFailed);
        Assert.Equal(1, _diagnostics.RebalanceExecutionStarted);
        Assert.Equal(0, _diagnostics.RebalanceExecutionCompleted);
    }

    [Fact]
    public async Task IntentProcessingLoop_ContinuesAfterRebalanceFailure()
    {
        _diagnostics.Reset();

        // Arrange: fail on second fetch (rebalance), succeed afterwards
        var callCount = 0;
        var faultyDataSource = new FaultyDataSource<int, string>(
            fetchSingleRange: range =>
            {
                callCount++;
                if (callCount == 2)
                {
                    throw new InvalidOperationException("Rebalance failed");
                }

                return FaultyDataSource<int, string>.GenerateStringData(range);
            }
        );

        var options = new WindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0.0,
            rightThreshold: 0.0,
            debounceDelay: TimeSpan.FromMilliseconds(10)
        );

        await using var cache = new WindowCache<int, string, IntegerFixedStepDomain>(
            faultyDataSource,
            new IntegerFixedStepDomain(),
            options,
            _diagnostics
        );

        // Act: trigger failure then continue with another request
        await cache.GetDataAsync(Intervals.NET.Factories.Range.Closed<int>(100, 110), CancellationToken.None);
        await cache.WaitForIdleAsync();

        await cache.GetDataAsync(Intervals.NET.Factories.Range.Closed<int>(200, 210), CancellationToken.None);
        await cache.WaitForIdleAsync();

        // Assert: intent processing loop stayed alive
        Assert.Equal(2, _diagnostics.UserRequestServed);
        Assert.True(_diagnostics.RebalanceIntentPublished >= 2,
            "Expected intents to continue publishing after a rebalance failure.");
        Assert.True(_diagnostics.RebalanceExecutionFailed >= 1,
            "Expected at least one rebalance failure to be recorded.");
    }

    #region Helper Classes

    /// <summary>
    /// Production-ready diagnostics implementation that logs failures.
    /// This demonstrates the minimum requirement for production use.
    /// </summary>
    private class LoggingCacheDiagnostics : ICacheDiagnostics
    {
        private readonly Action<Exception> _logError;

        public LoggingCacheDiagnostics(Action<Exception> logError)
        {
            _logError = logError;
        }

        public void RebalanceScheduled()
        {
        }

        public void RebalanceExecutionFailed(Exception ex)
        {
            // ⚠️ CRITICAL: This is the minimum requirement for production
            _logError(ex);
        }

        // All other methods can be no-op if you only care about failures
        public void UserRequestServed()
        {
        }

        public void CacheExpanded()
        {
        }

        public void CacheReplaced()
        {
        }

        public void UserRequestFullCacheHit()
        {
        }

        public void UserRequestPartialCacheHit()
        {
        }

        public void UserRequestFullCacheMiss()
        {
        }

        public void DataSourceFetchSingleRange()
        {
        }

        public void DataSourceFetchMissingSegments()
        {
        }

        public void RebalanceIntentPublished()
        {
        }

        public void RebalanceExecutionStarted()
        {
        }

        public void RebalanceExecutionCompleted()
        {
        }

        public void RebalanceExecutionCancelled()
        {
        }

        public void RebalanceSkippedCurrentNoRebalanceRange()
        {
        }

        public void RebalanceSkippedPendingNoRebalanceRange()
        {
        }

        public void RebalanceSkippedSameRange()
        {
        }

        public void DataSegmentUnavailable()
        {
        }
    }

    #endregion
}
