using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Caching.SlidingWindow.Tests.Infrastructure.DataSources;
using Intervals.NET.Caching.SlidingWindow.Public.Cache;
using Intervals.NET.Caching.SlidingWindow.Public.Configuration;
using Intervals.NET.Caching.SlidingWindow.Public.Instrumentation;

namespace Intervals.NET.Caching.SlidingWindow.Integration.Tests;

/// <summary>
/// Tests for validating proper exception handling in User Path operations.
/// Verifies that exceptions from IDataSource during user requests:
///   - Propagate to the caller
///   - Do NOT increment UserRequestServed
///   - Do NOT publish a rebalance intent
///   - Leave the cache operational for subsequent requests
/// </summary>
public sealed class UserPathExceptionHandlingTests : IAsyncDisposable
{
    private readonly IntegerFixedStepDomain _domain;
    private readonly EventCounterCacheDiagnostics _diagnostics;
    private SlidingWindowCache<int, string, IntegerFixedStepDomain>? _cache;

    public UserPathExceptionHandlingTests()
    {
        _domain = new IntegerFixedStepDomain();
        _diagnostics = new EventCounterCacheDiagnostics();
    }

    public async ValueTask DisposeAsync()
    {
        if (_cache != null)
        {
            await _cache.WaitForIdleAsync();
            await _cache.DisposeAsync();
        }
    }

    /// <summary>
    /// When IDataSource.FetchAsync throws on the first (user-path) call:
    ///   - The exception propagates to the caller of GetDataAsync
    ///   - UserRequestServed is NOT incremented (incomplete request is not "served")
    ///   - RebalanceIntentPublished is NOT incremented (no intent from a failed request)
    /// </summary>
    [Fact]
    public async Task UserFetchException_PropagatesException_AndDoesNotCountAsServed_AndDoesNotPublishIntent()
    {
        // ARRANGE
        var dataSource = new FaultyDataSource<int, string>(
            fetchSingleRange: _ => throw new InvalidOperationException("Simulated user-path fetch failure")
        );

        var options = new SlidingWindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0.0,
            rightThreshold: 0.0,
            debounceDelay: TimeSpan.FromMilliseconds(10)
        );

        _cache = new SlidingWindowCache<int, string, IntegerFixedStepDomain>(
            dataSource,
            _domain,
            options,
            _diagnostics
        );

        // ACT
        var exception = await Record.ExceptionAsync(async () =>
            await _cache.GetDataAsync(
                Factories.Range.Closed<int>(100, 110),
                CancellationToken.None));

        // ASSERT - exception propagated
        Assert.NotNull(exception);
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("Simulated user-path fetch failure", exception.Message);

        // ASSERT - not counted as served
        Assert.Equal(0, _diagnostics.UserRequestServed);

        // ASSERT - no intent published
        Assert.Equal(0, _diagnostics.RebalanceIntentPublished);
    }

    /// <summary>
    /// After a user-path exception, the cache remains fully operational.
    /// A subsequent successful request is served normally and counts as served.
    /// </summary>
    [Fact]
    public async Task UserFetchException_CacheRemainsOperational_SubsequentRequestSucceeds()
    {
        // ARRANGE - first call throws, second call succeeds
        var callCount = 0;
        var dataSource = new FaultyDataSource<int, string>(
            fetchSingleRange: range =>
            {
                callCount++;
                if (callCount == 1)
                {
                    throw new InvalidOperationException("Simulated user-path fetch failure on first call");
                }

                // Second and subsequent calls succeed
                return FaultyDataSource<int, string>.GenerateStringData(range);
            }
        );

        var options = new SlidingWindowCacheOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            readMode: UserCacheReadMode.Snapshot,
            leftThreshold: 0.0,
            rightThreshold: 0.0,
            debounceDelay: TimeSpan.FromMilliseconds(10)
        );

        _cache = new SlidingWindowCache<int, string, IntegerFixedStepDomain>(
            dataSource,
            _domain,
            options,
            _diagnostics
        );

        // ACT - first call: expect exception
        var firstException = await Record.ExceptionAsync(async () =>
            await _cache.GetDataAsync(
                Factories.Range.Closed<int>(100, 110),
                CancellationToken.None));

        // ACT - second call: expect success
        var secondResult = await _cache.GetDataAsync(
            Factories.Range.Closed<int>(100, 110),
            CancellationToken.None);

        // ASSERT - first call threw
        Assert.NotNull(firstException);
        Assert.IsType<InvalidOperationException>(firstException);

        // ASSERT - second call succeeded
        Assert.NotNull(secondResult.Range);
        Assert.Equal(11, secondResult.Data.Length);

        // ASSERT - only the successful second request was counted as served
        Assert.Equal(1, _diagnostics.UserRequestServed);
    }
}
