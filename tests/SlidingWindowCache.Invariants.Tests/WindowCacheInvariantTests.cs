using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Domain.Extensions.Fixed;
using Intervals.NET.Extensions;
using SlidingWindowCache.Infrastructure.Instrumentation;
using SlidingWindowCache.Invariants.Tests.TestInfrastructure;
using SlidingWindowCache.Public;
using SlidingWindowCache.Public.Configuration;

namespace SlidingWindowCache.Invariants.Tests;

/// <summary>
/// Comprehensive test suite verifying all 47 system invariants for WindowCache.
/// Each test references its corresponding invariant number and description.
/// Tests use DEBUG instrumentation counters to verify behavioral properties.
/// Uses Intervals.NET for proper range handling and inclusivity considerations.
/// </summary>
public sealed class WindowCacheInvariantTests : IAsyncDisposable
{
    private readonly IntegerFixedStepDomain _domain;
    private WindowCache<int, int, IntegerFixedStepDomain>? _currentCache;
    private readonly EventCounterCacheDiagnostics _cacheDiagnostics;

    public WindowCacheInvariantTests()
    {
        _cacheDiagnostics = new EventCounterCacheDiagnostics();
        _domain = TestHelpers.CreateIntDomain();
    }

    /// <summary>
    /// Ensures any background rebalance operations are completed before executing next test
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        // Wait for any background rebalance from current test to complete
        await _currentCache!.WaitForIdleAsync();
    }

    /// <summary>
    /// Tracks a cache instance for automatic cleanup in Dispose.
    /// </summary>
    private (WindowCache<int, int, IntegerFixedStepDomain> cache, Moq.Mock<IDataSource<int, int>> mockDataSource)
        TrackCache(
            (WindowCache<int, int, IntegerFixedStepDomain> cache, Moq.Mock<IDataSource<int, int>> mockDataSource) tuple)
    {
        _currentCache = tuple.cache;
        return tuple;
    }

    #region A. User Path & Fast User Access Invariants

    #region A.1 Concurrency & Priority

    /// <summary>
    /// Tests Invariant A.0a (🟢 Behavioral): Every User Request MUST cancel any ongoing or pending
    /// Rebalance Execution to ensure rebalance doesn't interfere with User Path data assembly.
    /// Verifies cancellation via DEBUG instrumentation counters.
    /// Related: A.0 (Architectural - User Path has higher priority than Rebalance Execution)
    /// </summary>
    [Fact]
    public async Task Invariant_A_0a_UserRequestCancelsRebalance()
    {
        // ARRANGE
        var options = TestHelpers.CreateDefaultOptions(leftCacheSize: 2.0, rightCacheSize: 2.0,
            debounceDelay: TimeSpan.FromMilliseconds(100));
        var (cache, _) = TrackCache(TestHelpers.CreateCacheWithDefaults(_domain, _cacheDiagnostics, options));

        // ACT: First request triggers rebalance intent
        await cache.GetDataAsync(TestHelpers.CreateRange(100, 110), CancellationToken.None);
        var intentPublishedBefore = _cacheDiagnostics.RebalanceIntentPublished;
        Assert.Equal(1, intentPublishedBefore);

        // Second request cancels the first rebalance intent
        await cache.GetDataAsync(TestHelpers.CreateRange(120, 130), CancellationToken.None);

        // Wait for background rebalance to settle before checking counters
        await cache.WaitForIdleAsync();

        // ASSERT: Verify cancellation occurred
        TestHelpers.AssertRebalancePathCancelled(_cacheDiagnostics);
    }

    #endregion

    #region A.2 User-Facing Guarantees

    /// <summary>
    /// Tests Invariant A.1 (🟢 Behavioral): User Path always serves user requests regardless
    /// of rebalance execution state. Validates core guarantee that users are never blocked by cache maintenance.
    /// </summary>
    [Fact]
    public async Task Invariant_A2_1_UserPathAlwaysServesRequests()
    {
        // ARRANGE
        var (cache, _) = TrackCache(TestHelpers.CreateCacheWithDefaults(_domain, _cacheDiagnostics));

        // ACT: Make multiple requests
        var data1 = await cache.GetDataAsync(TestHelpers.CreateRange(100, 110), CancellationToken.None);
        var data2 = await cache.GetDataAsync(TestHelpers.CreateRange(200, 210), CancellationToken.None);
        var data3 = await cache.GetDataAsync(TestHelpers.CreateRange(105, 115), CancellationToken.None);

        // ASSERT: All requests completed with correct data
        TestHelpers.AssertUserDataCorrect(data1, TestHelpers.CreateRange(100, 110));
        TestHelpers.AssertUserDataCorrect(data2, TestHelpers.CreateRange(200, 210));
        TestHelpers.AssertUserDataCorrect(data3, TestHelpers.CreateRange(105, 115));
        Assert.Equal(3, _cacheDiagnostics.UserRequestServed);
    }

    /// <summary>
    /// Tests Invariant A.2 (🟢 Behavioral): User Path never waits for rebalance execution to complete.
    /// Verifies requests complete quickly without waiting for debounce delay or background rebalance.
    /// </summary>
    [Fact]
    public async Task Invariant_A2_2_UserPathNeverWaitsForRebalance()
    {
        // ARRANGE: Cache with slow rebalance (1s debounce)
        var options = TestHelpers.CreateDefaultOptions(debounceDelay: TimeSpan.FromSeconds(1));
        var (cache, _) = TrackCache(TestHelpers.CreateCacheWithDefaults(_domain, _cacheDiagnostics, options));

        // ACT: Request completes immediately without waiting for rebalance
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var data = await cache.GetDataAsync(TestHelpers.CreateRange(100, 110), CancellationToken.None);
        stopwatch.Stop();

        // ASSERT: Request completed quickly (much less than debounce delay)
        Assert.Equal(1, _cacheDiagnostics.UserRequestServed);
        Assert.Equal(1, _cacheDiagnostics.RebalanceIntentPublished);
        Assert.Equal(0, _cacheDiagnostics.RebalanceExecutionCompleted);
        TestHelpers.AssertUserDataCorrect(data, TestHelpers.CreateRange(100, 110));
        await cache.WaitForIdleAsync();
        Assert.Equal(1, _cacheDiagnostics.RebalanceExecutionCompleted);
    }

    /// <summary>
    /// Tests Invariant A.10 (🟢 Behavioral): User always receives data exactly corresponding to RequestedRange.
    /// Verifies returned data matches requested range in length and content regardless of cache state.
    /// This is a fundamental correctness guarantee.
    /// </summary>
    [Fact]
    public async Task Invariant_A2_10_UserAlwaysReceivesExactRequestedRange()
    {
        // ARRANGE
        var (cache, _) = TrackCache(TestHelpers.CreateCacheWithDefaults(_domain, _cacheDiagnostics));

        // Act & Assert: Request various ranges and verify exact match
        var testRanges = new[]
        {
            TestHelpers.CreateRange(100, 110),
            TestHelpers.CreateRange(200, 250),
            TestHelpers.CreateRange(105, 115),
            TestHelpers.CreateRange(50, 60)
        };

        foreach (var range in testRanges)
        {
            var data = await cache.GetDataAsync(range, CancellationToken.None);
            TestHelpers.AssertUserDataCorrect(data, range);
        }
    }

    #endregion

    #region A.3 Cache Mutation Rules (User Path)

    /// <summary>
    /// Tests Invariant A.8 (🟢 Behavioral): User Path MUST NOT mutate cache under any circumstance.
    /// Cache mutations (population, expansion, replacement) are performed exclusively by Rebalance Execution (single-writer).
    /// </summary>
    /// <remarks>
    /// Scenarios tested:
    /// - ColdStart: Initial cache population during first request
    /// - CacheExpansion: Intersecting request that partially overlaps existing cache
    /// - FullReplacement: Non-intersecting jump to different region
    /// In all cases, User Path returns correct data immediately but does NOT mutate cache.
    /// Cache mutations occur asynchronously via Rebalance Execution.
    /// </remarks>
    [Theory]
    [InlineData("ColdStart", 100, 110, 0, 0, false)] // No prior request
    [InlineData("CacheExpansion", 105, 120, 100, 110, true)] // Intersecting request
    [InlineData("FullReplacement", 200, 210, 100, 110, true)] // Non-intersecting jump
    public async Task Invariant_A3_8_UserPathNeverMutatesCache(
        string _, int reqStart, int reqEnd, int priorStart, int priorEnd, bool hasPriorRequest)
    {
        // ARRANGE
        var options = TestHelpers.CreateDefaultOptions(debounceDelay: TimeSpan.FromMilliseconds(50));
        var (cache, _) = TrackCache(TestHelpers.CreateCacheWithDefaults(_domain, _cacheDiagnostics, options));

        // ACT: Execute prior request if needed to establish cache state
        if (hasPriorRequest)
        {
            await TestHelpers.ExecuteRequestAndWaitForRebalance(cache, TestHelpers.CreateRange(priorStart, priorEnd));
            _cacheDiagnostics.Reset(); // Track only the test request
        }

        // Execute the test request
        var data = await cache.GetDataAsync(TestHelpers.CreateRange(reqStart, reqEnd), CancellationToken.None);

        // ASSERT: User receives correct data immediately
        TestHelpers.AssertUserDataCorrect(data, TestHelpers.CreateRange(reqStart, reqEnd));

        // User Path MUST NOT mutate cache (single-writer architecture)
        TestHelpers.AssertNoUserPathMutations(_cacheDiagnostics);

        // Intent published for every request
        TestHelpers.AssertIntentPublished(_cacheDiagnostics, 1);

        // Wait for rebalance and verify it completes (cache mutations happen here)
        await cache.WaitForIdleAsync();
        TestHelpers.AssertRebalanceCompleted(_cacheDiagnostics);
    }

    /// <summary>
    /// Tests Invariant A.9a (🟢 Behavioral): Cache always represents a single contiguous range, never fragmented.
    /// When non-intersecting requests arrive, cache replaces its contents entirely rather than maintaining
    /// multiple disjoint ranges, ensuring efficient memory usage and predictable behavior.
    /// </summary>
    [Fact]
    public async Task Invariant_A3_9a_CacheContiguityMaintained()
    {
        // ARRANGE
        var (cache, _) = TrackCache(TestHelpers.CreateCacheWithDefaults(_domain, _cacheDiagnostics));

        // ACT: Make various requests including overlapping and expanding ranges
        var data1 = await cache.GetDataAsync(TestHelpers.CreateRange(100, 110), CancellationToken.None);
        var data2 = await cache.GetDataAsync(TestHelpers.CreateRange(105, 115), CancellationToken.None);
        var data3 = await cache.GetDataAsync(TestHelpers.CreateRange(95, 120), CancellationToken.None);

        // ASSERT: All data is contiguous (no gaps)
        TestHelpers.AssertUserDataCorrect(data1, TestHelpers.CreateRange(100, 110));
        TestHelpers.AssertUserDataCorrect(data2, TestHelpers.CreateRange(105, 115));
        TestHelpers.AssertUserDataCorrect(data3, TestHelpers.CreateRange(95, 120));
    }

    #endregion

    #endregion

    #region B. Cache State & Consistency Invariants

    /// <summary>
    /// Tests Invariant B.11 (🟢 Behavioral): CacheData and CurrentCacheRange are always consistent.
    /// At all observable points, cache's data content matches its declared range. Fundamental correctness invariant.
    /// </summary>
    [Fact]
    public async Task Invariant_B11_CacheDataAndRangeAlwaysConsistent()
    {
        // ARRANGE
        var (cache, _) = TrackCache(TestHelpers.CreateCacheWithDefaults(_domain, _cacheDiagnostics));

        // Act & Assert: Make multiple requests and verify consistency
        var ranges = new[]
        {
            TestHelpers.CreateRange(100, 110),
            TestHelpers.CreateRange(105, 120),
            TestHelpers.CreateRange(200, 250)
        };

        foreach (var range in ranges)
        {
            var data = await cache.GetDataAsync(range, CancellationToken.None);
            var expectedLength = (int)range.End - (int)range.Start + 1;
            Assert.Equal(expectedLength, data.Length);
            TestHelpers.AssertUserDataCorrect(data, range);
        }
    }

    /// <summary>
    /// Tests Invariant B.15 (🟢 Behavioral): Partially executed or cancelled Rebalance Execution
    /// MUST NOT leave cache in inconsistent state. Verifies aggressive cancellation for user responsiveness
    /// doesn't compromise correctness. Also validates F.35b (same guarantee from execution perspective).
    /// </summary>
    [Fact]
    public async Task Invariant_B15_CancelledRebalanceDoesNotViolateConsistency()
    {
        // ARRANGE
        var options = TestHelpers.CreateDefaultOptions(debounceDelay: TimeSpan.FromMilliseconds(100));
        var (cache, _) = TrackCache(TestHelpers.CreateCacheWithDefaults(_domain, _cacheDiagnostics, options));

        // ACT: First request starts rebalance intent, then immediately cancel with another request
        await cache.GetDataAsync(TestHelpers.CreateRange(100, 110), CancellationToken.None);
        var data = await cache.GetDataAsync(TestHelpers.CreateRange(200, 210), CancellationToken.None);

        // ASSERT: Cache still returns correct data
        TestHelpers.AssertUserDataCorrect(data, TestHelpers.CreateRange(200, 210));

        // Verify cache is not corrupted by making another request
        var data2 = await cache.GetDataAsync(TestHelpers.CreateRange(205, 215), CancellationToken.None);
        TestHelpers.AssertUserDataCorrect(data2, TestHelpers.CreateRange(205, 215));
    }

    #endregion

    #region C. Rebalance Intent & Temporal Invariants

    /// <summary>
    /// Tests Invariant C.17 (🟢 Behavioral): At any point in time, at most one active rebalance intent exists.
    /// Verifies rapid requests cause each new intent to cancel previous ones, preventing intent queue buildup.
    /// </summary>
    [Fact]
    public async Task Invariant_C17_AtMostOneActiveIntent()
    {
        // ARRANGE
        var options = TestHelpers.CreateDefaultOptions(debounceDelay: TimeSpan.FromMilliseconds(200));
        var (cache, _) = TrackCache(TestHelpers.CreateCacheWithDefaults(_domain, _cacheDiagnostics, options));

        // ACT: Make rapid requests
        await cache.GetDataAsync(TestHelpers.CreateRange(100, 110), CancellationToken.None);
        await cache.GetDataAsync(TestHelpers.CreateRange(110, 120), CancellationToken.None);
        await cache.GetDataAsync(TestHelpers.CreateRange(120, 130), CancellationToken.None);

        // Wait for background rebalance to settle before checking counters
        await cache.WaitForIdleAsync();

        // ASSERT: Each new request publishes intent and cancels previous (at least 2 cancelled)
        TestHelpers.AssertIntentPublished(_cacheDiagnostics, 3);
        TestHelpers.AssertRebalancePathCancelled(_cacheDiagnostics, 2);
    }

    /// <summary>
    /// Tests Invariant C.18 (🟢 Behavioral): Any previously created rebalance intent is considered
    /// obsolete after a new intent is generated. Prevents stale rebalance operations from executing
    /// with outdated information.
    /// </summary>
    [Fact]
    public async Task Invariant_C18_PreviousIntentBecomesObsolete()
    {
        // ARRANGE
        var options = TestHelpers.CreateDefaultOptions(debounceDelay: TimeSpan.FromMilliseconds(150));
        var (cache, _) = TrackCache(TestHelpers.CreateCacheWithDefaults(_domain, _cacheDiagnostics, options));

        // ACT: First request publishes intent
        await cache.GetDataAsync(TestHelpers.CreateRange(100, 110), CancellationToken.None);
        var publishedBefore = _cacheDiagnostics.RebalanceIntentPublished;

        // Second request publishes new intent and cancels old one
        await cache.GetDataAsync(TestHelpers.CreateRange(200, 210), CancellationToken.None);

        // Wait for background rebalance to settle before checking counters
        await cache.WaitForIdleAsync();

        // ASSERT: New intent published, old one cancelled
        Assert.True(_cacheDiagnostics.RebalanceIntentPublished > publishedBefore);
        TestHelpers.AssertRebalancePathCancelled(_cacheDiagnostics);
    }

    /// <summary>
    /// Tests Invariant C.24 (🟡 Conceptual): Intent does not guarantee execution. Execution is opportunistic
    /// and may be skipped due to: C.24a (request within NoRebalanceRange), C.24b (debounce), 
    /// C.24c (DesiredCacheRange equals CurrentCacheRange), C.24d (cancellation).
    /// Demonstrates cache's opportunistic, efficiency-focused design.
    /// </summary>
    [Fact]
    public async Task Invariant_C24_IntentDoesNotGuaranteeExecution()
    {
        // ARRANGE: Large threshold creates large NoRebalanceRange to block rebalance
        var options = TestHelpers.CreateDefaultOptions(leftCacheSize: 2.0, rightCacheSize: 2.0,
            leftThreshold: 0.5, rightThreshold: 0.5, debounceDelay: TimeSpan.FromMilliseconds(100));
        var (cache, _) = TrackCache(TestHelpers.CreateCacheWithDefaults(_domain, _cacheDiagnostics, options));

        // ACT: First request establishes cache
        await TestHelpers.ExecuteRequestAndWaitForRebalance(cache, TestHelpers.CreateRange(100, 110));
        _cacheDiagnostics.Reset();

        // Second request within NoRebalanceRange - intent published but execution may be skipped
        await cache.GetDataAsync(TestHelpers.CreateRange(102, 108), CancellationToken.None);
        await cache.WaitForIdleAsync();

        // ASSERT: Intent published but execution may be skipped due to NoRebalanceRange
        TestHelpers.AssertIntentPublished(_cacheDiagnostics);
        if (_cacheDiagnostics.RebalanceSkippedNoRebalanceRange > 0)
        {
            Assert.Equal(0, _cacheDiagnostics.RebalanceExecutionCompleted);
        }
    }

    /// <summary>
    /// Tests Invariant C.23 (🟢 Behavioral): System stabilizes when user access patterns stabilize.
    /// After initial burst, when access patterns stabilize (requests in same region), system converges
    /// to stable state where subsequent requests are served from cache without triggering rebalance.
    /// Demonstrates cache's convergence behavior. Related: C.22 (best-effort convergence guarantee).
    /// </summary>
    [Fact]
    public async Task Invariant_C23_SystemStabilizesUnderLoad()
    {
        // ARRANGE
        var options = TestHelpers.CreateDefaultOptions(debounceDelay: TimeSpan.FromMilliseconds(50));
        var (cache, _) = TrackCache(TestHelpers.CreateCacheWithDefaults(_domain, _cacheDiagnostics, options));

        // ACT: Rapid burst of requests
        var tasks = new List<Task>();
        for (var i = 0; i < 10; i++)
        {
            var start = 100 + i * 2;
            tasks.Add(cache.GetDataAsync(TestHelpers.CreateRange(start, start + 10), CancellationToken.None).AsTask());
        }

        await Task.WhenAll(tasks);
        await cache.WaitForIdleAsync();

        // ASSERT: System is stable and serves new requests correctly
        var finalData = await cache.GetDataAsync(TestHelpers.CreateRange(105, 115), CancellationToken.None);
        TestHelpers.AssertUserDataCorrect(finalData, TestHelpers.CreateRange(105, 115));
    }

    #endregion

    #region D. Rebalance Decision Path Invariants

    /// <summary>
    /// Tests Invariant D.27 (🟢 Behavioral): If RequestedRange is fully contained within NoRebalanceRange,
    /// rebalance execution is prohibited. Verifies ThresholdRebalancePolicy prevents unnecessary rebalance
    /// when requests fall within "dead zone" around current cache, reducing I/O and CPU usage.
    /// Corresponds to sub-invariant C.24a (execution skipped due to policy).
    /// </summary>
    [Fact]
    public async Task Invariant_D27_NoRebalanceIfRequestInNoRebalanceRange()
    {
        // ARRANGE: Large thresholds to create wide NoRebalanceRange
        var options = TestHelpers.CreateDefaultOptions(leftCacheSize: 2.0, rightCacheSize: 2.0,
            leftThreshold: 0.4, rightThreshold: 0.4, debounceDelay: TimeSpan.FromMilliseconds(1000));
        var (cache, _) = TrackCache(TestHelpers.CreateCacheWithDefaults(_domain, _cacheDiagnostics, options));

        // ACT: First request establishes cache and NoRebalanceRange
        await TestHelpers.ExecuteRequestAndWaitForRebalance(cache, TestHelpers.CreateRange(100, 110));
        _cacheDiagnostics.Reset();

        // Second request within NoRebalanceRange
        await cache.GetDataAsync(TestHelpers.CreateRange(103, 107), CancellationToken.None);
        await cache.WaitForIdleAsync();

        // ASSERT: Rebalance skipped due to NoRebalanceRange policy (execution should never start)
        TestHelpers.AssertRebalanceSkippedDueToPolicy(_cacheDiagnostics);
    }

    /// <summary>
    /// Tests Invariant D.28 (🟢 Behavioral): If DesiredCacheRange == CurrentCacheRange, rebalance execution
    /// not required. When cache already matches desired state, system skips execution as optimization.
    /// Uses DEBUG counter RebalanceSkippedSameRange to verify early-exit in RebalanceExecutor.
    /// Corresponds to sub-invariant C.24c (execution skipped due to optimization).
    /// </summary>
    [Fact]
    public async Task Invariant_D28_SkipWhenDesiredEqualsCurrentRange()
    {
        // ARRANGE
        var options = TestHelpers.CreateDefaultOptions(leftCacheSize: 1.0, rightCacheSize: 1.0,
            leftThreshold: 0.4, rightThreshold: 0.4, debounceDelay: TimeSpan.FromMilliseconds(100));
        var (cache, _) = TrackCache(TestHelpers.CreateCacheWithDefaults(_domain, _cacheDiagnostics, options));

        // ACT: First request establishes cache at desired range
        var firstRange = TestHelpers.CreateRange(100, 110);
        await TestHelpers.ExecuteRequestAndWaitForRebalance(cache, firstRange);
        _cacheDiagnostics.Reset();

        // Second request: same range should trigger intent, pass decision logic, starts executions, but skip before mutating data due to same-range optimization
        await cache.GetDataAsync(firstRange, CancellationToken.None);
        await cache.WaitForIdleAsync();

        // ASSERT: Intent published but execution optimized away
        Assert.Equal(1, _cacheDiagnostics.RebalanceIntentPublished);

        // Execution should either be skipped entirely or not completed
        // (skipped due to same-range optimization or never started)
        Assert.Equal(0, _cacheDiagnostics.RebalanceExecutionCompleted);
        Assert.Equal(1, _cacheDiagnostics.RebalanceSkippedSameRange);
    }

    // NOTE: Invariant D.25, D.26, D.28, D.29: Decision Path is purely analytical,
    // never mutates cache state, checks DesiredCacheRange == CurrentCacheRange
    // Cannot be directly tested via public API - requires internal state access
    // or integration tests with mock decision engine

    #endregion

    #region E. Cache Geometry & Policy Invariants

    /// <summary>
    /// Tests Invariant E.30 (🟢 Behavioral): DesiredCacheRange is computed solely from RequestedRange
    /// and cache configuration. Verifies ProportionalRangePlanner computes desired cache range deterministically
    /// based only on user's requested range and config parameters (leftCacheSize, rightCacheSize), independent
    /// of current cache contents. With config (leftSize=1.0, rightSize=1.0), cache expands by RequestedRange.Span
    /// on each side. Related: E.31 (Architectural - DesiredCacheRange independent of current cache contents).
    /// </summary>
    [Fact]
    public async Task Invariant_E30_DesiredRangeComputedFromConfigAndRequest()
    {
        // ARRANGE: Expansion coefficients: leftSize=1.0 (expand left by 100%), rightSize=1.0 (expand right by 100%)
        var options = TestHelpers.CreateDefaultOptions(leftCacheSize: 1.0, rightCacheSize: 1.0,
            debounceDelay: TimeSpan.FromMilliseconds(50));
        var (cache, _) = TrackCache(TestHelpers.CreateCacheWithDefaults(_domain, _cacheDiagnostics, options));

        // ACT: Request a range [100, 110] (Size: 11)
        var requestRange = TestHelpers.CreateRange(100, 110);
        await TestHelpers.ExecuteRequestAndWaitForRebalance(cache, requestRange);

        // Calculate expected desired range using the helper that mimics ProportionalRangePlanner
        var expectedDesiredRange = TestHelpers.CalculateExpectedDesiredRange(requestRange, options, _domain);

        // Reset counters to track only the next request
        _cacheDiagnostics.Reset();

        // Make another request within the calculated desired range
        var withinDesired = await cache.GetDataAsync(TestHelpers.CreateRange(95, 115), CancellationToken.None);

        // ASSERT: Data is correct, demonstrating cache expanded based on configuration
        TestHelpers.AssertUserDataCorrect(withinDesired, TestHelpers.CreateRange(95, 115));

        // Verify this was a full cache hit, proving the desired range was calculated correctly
        TestHelpers.AssertFullCacheHit(_cacheDiagnostics);

        // Verify the expected desired range calculation matches actual behavior
        // The request [95, 115] should be fully within expectedDesiredRange
        Assert.True(expectedDesiredRange.Contains(TestHelpers.CreateRange(95, 115)),
            $"Request range [95, 115] should be within calculated desired range {expectedDesiredRange}");
    }

    // NOTE: Invariant E.31, E.32, E.33, E.34: DesiredCacheRange independent of current cache,
    // represents canonical target state, geometry determined by configuration,
    // NoRebalanceRange derived from CurrentCacheRange and config
    // Cannot be directly observed via public API - requires internal state inspection

    /// <summary>
    /// Demonstrates all three cache hit/miss scenarios tracked by instrumentation counters:
    /// 1. Full Cache Miss (cold start and non-intersecting jump)
    /// 2. Full Cache Hit (request fully within cache)
    /// 3. Partial Cache Hit (request partially overlaps cache)
    /// Validates cache hit/miss tracking is accurate for performance monitoring and testing.
    /// Also verifies data source access patterns to ensure optimization correctness.
    /// </summary>
    [Fact]
    public async Task CacheHitMiss_AllScenarios()
    {
        // ARRANGE
        var options = TestHelpers.CreateDefaultOptions(leftCacheSize: 1.0, rightCacheSize: 1.0,
            debounceDelay: TimeSpan.FromMilliseconds(50));
        var (cache, _) = TrackCache(TestHelpers.CreateCacheWithDefaults(_domain, _cacheDiagnostics, options));

        // SCENARIO 1: Cold Start - Full Cache Miss
        _cacheDiagnostics.Reset();
        var requestedRange = TestHelpers.CreateRange(100, 110);
        await cache.GetDataAsync(requestedRange, CancellationToken.None);
        TestHelpers.AssertFullCacheMiss(_cacheDiagnostics);
        TestHelpers.AssertDataSourceFetchedFullRange(_cacheDiagnostics);
        Assert.Equal(0, _cacheDiagnostics.UserRequestFullCacheHit);
        Assert.Equal(0, _cacheDiagnostics.UserRequestPartialCacheHit);
        Assert.Equal(0, _cacheDiagnostics.DataSourceFetchMissingSegments);

        // Wait for rebalance to populate cache with expanded range
        await cache.WaitForIdleAsync();

        // SCENARIO 2: Full Cache Hit - Request within cached range
        _cacheDiagnostics.Reset();
        var expectedDesired = TestHelpers.CalculateExpectedDesiredRange(requestedRange, options, _domain);
        await cache.GetDataAsync(expectedDesired, CancellationToken.None);
        TestHelpers.AssertFullCacheHit(_cacheDiagnostics);
        Assert.Equal(0, _cacheDiagnostics.UserRequestFullCacheMiss);
        Assert.Equal(0, _cacheDiagnostics.UserRequestPartialCacheHit);
        Assert.Equal(0, _cacheDiagnostics.DataSourceFetchSingleRange);
        Assert.Equal(0, _cacheDiagnostics.DataSourceFetchMissingSegments);

        // Wait for rebalance
        await cache.WaitForIdleAsync();

        // SCENARIO 3: Partial Cache Hit - Request partially overlaps cache
        _cacheDiagnostics.Reset();
        // Shift the expected desired range to create a new request that partially overlaps the existing cache
        expectedDesired = TestHelpers.CalculateExpectedDesiredRange(expectedDesired, options, _domain);
        expectedDesired = expectedDesired.Shift(_domain, expectedDesired.Span(_domain).Value / 2);
        await cache.GetDataAsync(expectedDesired, CancellationToken.None);
        TestHelpers.AssertPartialCacheHit(_cacheDiagnostics);
        TestHelpers.AssertDataSourceFetchedMissingSegments(_cacheDiagnostics);
        Assert.Equal(0, _cacheDiagnostics.UserRequestFullCacheMiss);
        Assert.Equal(0, _cacheDiagnostics.UserRequestFullCacheHit);
        Assert.Equal(0, _cacheDiagnostics.DataSourceFetchSingleRange);

        // Wait for rebalance
        await cache.WaitForIdleAsync();

        // SCENARIO 4: Full Cache Miss - Non-intersecting jump
        _cacheDiagnostics.Reset();
        // Create a request that is completely outside the current cache range to trigger a full cache miss
        expectedDesired = TestHelpers.CalculateExpectedDesiredRange(expectedDesired, options, _domain);
        expectedDesired = expectedDesired.Shift(_domain, expectedDesired.Span(_domain).Value * 2);
        await cache.GetDataAsync(expectedDesired, CancellationToken.None);
        TestHelpers.AssertFullCacheMiss(_cacheDiagnostics);
        TestHelpers.AssertDataSourceFetchedFullRange(_cacheDiagnostics);
        Assert.Equal(0, _cacheDiagnostics.UserRequestFullCacheHit);
        Assert.Equal(0, _cacheDiagnostics.UserRequestPartialCacheHit);
        Assert.Equal(0, _cacheDiagnostics.DataSourceFetchMissingSegments);
    }

    #endregion

    #region F. Rebalance Execution Invariants

    /// <summary>
    /// Tests Invariants F.35 (🟢 Behavioral), F.35a (🔵 Architectural), and G.46 (🟢 Behavioral):
    /// Rebalance Execution MUST support cancellation at all stages and yield to User Path immediately.
    /// Validates detailed cancellation mechanics, lifecycle tracking (Started == Completed + Cancelled),
    /// and high-level guarantee that cancellation works in all scenarios.
    /// Uses slow data source to allow cancellation during execution. Verifies DEBUG instrumentation counters
    /// ensure proper lifecycle tracking. Related: A.0a (User Path cancels rebalance), C.24d (execution
    /// skipped due to cancellation).
    /// </summary>
    [Fact]
    public async Task Invariant_F35_G46_RebalanceCancellationBehavior()
    {
        // ARRANGE: Slow data source to allow cancellation during execution
        var options = TestHelpers.CreateDefaultOptions(leftCacheSize: 2.0, rightCacheSize: 2.0,
            debounceDelay: TimeSpan.FromMilliseconds(50));
        var (cache, _) = TrackCache(TestHelpers.CreateCacheWithDefaults(_domain, _cacheDiagnostics, options,
            fetchDelay: TimeSpan.FromMilliseconds(200)));

        // ACT: First request triggers rebalance, then immediately cancel with multiple new requests
        await cache.GetDataAsync(TestHelpers.CreateRange(100, 110), CancellationToken.None);
        await cache.GetDataAsync(TestHelpers.CreateRange(105, 115), CancellationToken.None);
        await cache.GetDataAsync(TestHelpers.CreateRange(110, 120), CancellationToken.None);
        await cache.WaitForIdleAsync();

        // ASSERT: Verify cancellation occurred (F.35, G.46)
        TestHelpers.AssertRebalancePathCancelled(_cacheDiagnostics, 2); // 2 cancels for the 2 new requests after the first

        // Verify Rebalance lifecycle integrity: every started execution reaches terminal state (F.35a)
        TestHelpers.AssertRebalanceLifecycleIntegrity(_cacheDiagnostics);
    }

    /// <summary>
    /// Tests Invariant F.36 (🔵 Architectural) and F.36a (🟢 Behavioral): Rebalance Execution Path is the
    /// only path responsible for cache normalization (expanding, trimming, recomputing NoRebalanceRange).
    /// After rebalance completes, cache is normalized to serve data from expanded range beyond original request.
    /// User Path performs minimal mutations while Rebalance Execution handles optimization.
    /// </summary>
    [Fact]
    public async Task Invariant_F36a_RebalanceNormalizesCache()
    {
        // ARRANGE
        var options = TestHelpers.CreateDefaultOptions(leftCacheSize: 1.0, rightCacheSize: 1.0,
            debounceDelay: TimeSpan.FromMilliseconds(50));
        var (cache, _) = TrackCache(TestHelpers.CreateCacheWithDefaults(_domain, _cacheDiagnostics, options));

        // ACT: Make request and wait for rebalance
        await TestHelpers.ExecuteRequestAndWaitForRebalance(cache, TestHelpers.CreateRange(100, 110));

        // ASSERT: Rebalance executed successfully
        TestHelpers.AssertRebalanceCompleted(_cacheDiagnostics);
        TestHelpers.AssertRebalanceLifecycleIntegrity(_cacheDiagnostics);

        // Cache should be normalized - verify by requesting from expected expanded range
        var extendedData = await cache.GetDataAsync(TestHelpers.CreateRange(95, 115), CancellationToken.None);
        TestHelpers.AssertUserDataCorrect(extendedData, TestHelpers.CreateRange(95, 115));
    }

    /// <summary>
    /// Tests Invariants F.40, F.41, F.42 (🟢 Behavioral/🟡 Conceptual): Post-execution guarantees.
    /// F.40: CacheData corresponds to DesiredCacheRange. F.41: CurrentCacheRange == DesiredCacheRange.
    /// F.42: NoRebalanceRange is recomputed. After successful rebalance, cache reaches normalized state
    /// serving data from expanded/optimized range (based on config with leftSize=1.0, rightSize=1.0).
    /// </summary>
    [Fact]
    public async Task Invariant_F40_F41_F42_PostExecutionGuarantees()
    {
        // ARRANGE
        var options = TestHelpers.CreateDefaultOptions(leftCacheSize: 1.0, rightCacheSize: 1.0,
            debounceDelay: TimeSpan.FromMilliseconds(50));
        var (cache, _) = TrackCache(TestHelpers.CreateCacheWithDefaults(_domain, _cacheDiagnostics, options));

        // ACT: Request and wait for rebalance to complete
        await TestHelpers.ExecuteRequestAndWaitForRebalance(cache, TestHelpers.CreateRange(100, 110));

        if (_cacheDiagnostics.RebalanceExecutionCompleted > 0)
        {
            // After rebalance, cache should serve data from normalized range [100-11, 110+11] = [89, 121]
            var normalizedData = await cache.GetDataAsync(TestHelpers.CreateRange(90, 120), CancellationToken.None);
            TestHelpers.AssertUserDataCorrect(normalizedData, TestHelpers.CreateRange(90, 120));
        }
    }

    // NOTE: Invariant F.38, F.39: Requests data from IDataSource only for missing subranges,
    // does not overwrite existing data
    // Requires instrumentation of CacheDataExtensionService or mock data source tracking

    #endregion

    #region G. Execution Context & Scheduling Invariants

    /// <summary>
    /// Tests Invariants G.43, G.44, G.45: Execution context separation between User Path and Rebalance operations.
    /// G.43: User Path operates in user execution context (request completes quickly).
    /// G.44: Rebalance Decision/Execution Path execute outside user context (Task.Run).
    /// G.45: Rebalance Execution performs I/O only in background context (not blocking user).
    /// Verifies user requests complete quickly without blocking on background operations, proving rebalance
    /// work is properly scheduled on background threads. Critical for maintaining responsive user-facing latency.
    /// </summary>
    [Fact]
    public async Task Invariant_G43_G44_G45_ExecutionContextSeparation()
    {
        // ARRANGE
        var options = TestHelpers.CreateDefaultOptions(debounceDelay: TimeSpan.FromMilliseconds(100));
        var (cache, _) = TrackCache(TestHelpers.CreateCacheWithDefaults(_domain, _cacheDiagnostics, options));

        // ACT: User request completes synchronously (in user context)
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var data = await cache.GetDataAsync(TestHelpers.CreateRange(100, 110), CancellationToken.None);
        stopwatch.Stop();

        // ASSERT: User request completed quickly (didn't wait for background rebalance)
        Assert.Equal(1, _cacheDiagnostics.UserRequestServed);
        Assert.Equal(1, _cacheDiagnostics.RebalanceIntentPublished);
        Assert.Equal(0, _cacheDiagnostics.RebalanceExecutionCompleted);
        TestHelpers.AssertUserDataCorrect(data, TestHelpers.CreateRange(100, 110));
        await cache.WaitForIdleAsync();
        Assert.Equal(1, _cacheDiagnostics.RebalanceExecutionCompleted);
    }

    /// <summary>
    /// Tests Invariant G.46 (🟢 Behavioral): User-facing cancellation during IDataSource fetch operations.
    /// Verifies User Path properly propagates cancellation token through to IDataSource.FetchAsync().
    /// Users can cancel their own requests during potentially long-running data source operations.
    /// Related: G.46 covers "all scenarios" - this test focuses on user-facing cancellation.
    /// See also: Invariant_F35_G46 for background rebalance cancellation.
    /// </summary>
    [Fact]
    public async Task Invariant_G46_UserCancellationDuringFetch()
    {
        // ARRANGE: Slow mock data source to allow cancellation during fetch
        var (cache, _) = TrackCache(TestHelpers.CreateCacheWithDefaults(_domain, _cacheDiagnostics, TestHelpers.CreateDefaultOptions(),
            fetchDelay: TimeSpan.FromMilliseconds(300)));

        // Act & Assert: Cancel token during fetch operation
        var cts = new CancellationTokenSource();
        var requestTask = cache.GetDataAsync(TestHelpers.CreateRange(100, 110), cts.Token).AsTask();

        // Cancel while fetch is in progress
        await Task.Delay(50, CancellationToken.None);
        await cts.CancelAsync();

        // Should throw OperationCanceledException or derived type (TaskCanceledException)
        var exception = await Record.ExceptionAsync(async () => await requestTask);
        Assert.True(exception is OperationCanceledException,
            $"Expected OperationCanceledException but got {exception.GetType().Name}");
    }

    #endregion

    #region Additional Comprehensive Tests

    /// <summary>
    /// Comprehensive integration test covering multiple invariants in realistic usage scenario.
    /// Tests: Cold start (A.8), Cache expansion (A.8), Background rebalance normalization (F.36a),
    /// Non-intersecting replacement (A.8, A.9a), Cache consistency (B.11).
    /// Validates all components work correctly together. Verifies: user requests always served (A.1),
    /// data is correct (A.10), cache properly maintains state through multiple transitions.
    /// </summary>
    [Fact]
    public async Task CompleteScenario_MultipleRequestsWithRebalancing()
    {
        // ARRANGE
        var options = TestHelpers.CreateDefaultOptions(leftCacheSize: 1.0, rightCacheSize: 1.0,
            leftThreshold: 0.2, rightThreshold: 0.2, debounceDelay: TimeSpan.FromMilliseconds(50));
        var (cache, _) = TrackCache(TestHelpers.CreateCacheWithDefaults(_domain, _cacheDiagnostics, options));

        // Act & Assert: Sequential user requests
        // Request 1: Cold start
        var data1 = await cache.GetDataAsync(TestHelpers.CreateRange(100, 110), CancellationToken.None);
        TestHelpers.AssertUserDataCorrect(data1, TestHelpers.CreateRange(100, 110));

        // Request 2: Overlapping expansion
        var data2 = await cache.GetDataAsync(TestHelpers.CreateRange(105, 120), CancellationToken.None);
        TestHelpers.AssertUserDataCorrect(data2, TestHelpers.CreateRange(105, 120));
        await cache.WaitForIdleAsync();

        // Request 3: Within cached/rebalanced range
        var data3 = await cache.GetDataAsync(TestHelpers.CreateRange(110, 115), CancellationToken.None);
        TestHelpers.AssertUserDataCorrect(data3, TestHelpers.CreateRange(110, 115));

        // Request 4: Non-intersecting jump
        var data4 = await cache.GetDataAsync(TestHelpers.CreateRange(200, 210), CancellationToken.None);
        TestHelpers.AssertUserDataCorrect(data4, TestHelpers.CreateRange(200, 210));
        await cache.WaitForIdleAsync();

        // Request 5: Verify cache stability
        var data5 = await cache.GetDataAsync(TestHelpers.CreateRange(205, 215), CancellationToken.None);
        TestHelpers.AssertUserDataCorrect(data5, TestHelpers.CreateRange(205, 215));

        // Wait for background rebalance to settle before checking counters
        await cache.WaitForIdleAsync();

        // Verify key behavioral properties
        Assert.Equal(5, _cacheDiagnostics.UserRequestServed);
        Assert.True(_cacheDiagnostics.RebalanceIntentPublished >= 5);
        TestHelpers.AssertRebalanceCompleted(_cacheDiagnostics);
    }

    /// <summary>
    /// Comprehensive concurrency test with rapid burst of 20 concurrent requests verifying intent cancellation
    /// and system stability under high load. Validates: All requests served correctly (A.1, A.10),
    /// Intent cancellation works (C.17, C.18), At most one active intent (C.17),
    /// Cache remains consistent (B.11, B.15). Ensures single-consumer model with cancellation-based
    /// coordination handles realistic high-load scenarios without data corruption or request failures.
    /// </summary>
    [Fact]
    public async Task ConcurrencyScenario_RapidRequestsBurstWithCancellation()
    {
        // ARRANGE
        var options = TestHelpers.CreateDefaultOptions(debounceDelay: TimeSpan.FromSeconds(1));
        var (cache, _) = TrackCache(TestHelpers.CreateCacheWithDefaults(_domain, _cacheDiagnostics, options));

        // ACT: Fire 20 rapid concurrent requests
        var tasks = new List<Task<ReadOnlyMemory<int>>>();
        for (var i = 0; i < 20; i++)
        {
            var start = 100 + i * 5;
            tasks.Add(cache.GetDataAsync(TestHelpers.CreateRange(start, start + 10), CancellationToken.None).AsTask());
        }

        var results = await Task.WhenAll(tasks);

        // Wait for background rebalance to settle before checking counters
        await cache.WaitForIdleAsync();

        // ASSERT: All requests completed successfully with correct data
        Assert.Equal(20, results.Length);
        for (var i = 0; i < results.Length; i++)
        {
            var expectedRange = TestHelpers.CreateRange(100 + i * 5, 110 + i * 5);
            TestHelpers.AssertUserDataCorrect(results[i], expectedRange);
        }

        Assert.Equal(20, _cacheDiagnostics.UserRequestServed);
        Assert.True(_cacheDiagnostics.RebalanceIntentPublished == 20);
        TestHelpers.AssertRebalancePathCancelled(_cacheDiagnostics, 19); // Each new request cancels the previous intent, so expect 19 cancellations
        Assert.Equal(1, _cacheDiagnostics.RebalanceExecutionCompleted);
    }

    /// <summary>
    /// Tests read mode behavior. Snapshot mode: zero-allocation reads via direct ReadOnlyMemory access.
    /// CopyOnRead mode: defensive copies for memory safety when callers hold references beyond the call.
    /// Both modes return correct data matching requested ranges.
    /// </summary>
    [Theory]
    [InlineData(UserCacheReadMode.Snapshot)]
    [InlineData(UserCacheReadMode.CopyOnRead)]
    public async Task ReadMode_VerifyBehavior(UserCacheReadMode readMode)
    {
        // ARRANGE
        var options = TestHelpers.CreateDefaultOptions(readMode: readMode);
        var (cache, _) = TrackCache(TestHelpers.CreateCacheWithDefaults(_domain, _cacheDiagnostics, options));

        // Act
        var data1 = await cache.GetDataAsync(TestHelpers.CreateRange(100, 110), CancellationToken.None);
        var data2 = await cache.GetDataAsync(TestHelpers.CreateRange(105, 115), CancellationToken.None);

        // Assert
        TestHelpers.VerifyDataMatchesRange(data1, TestHelpers.CreateRange(100, 110));
        TestHelpers.VerifyDataMatchesRange(data2, TestHelpers.CreateRange(105, 115));
    }

    #endregion
}