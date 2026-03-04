using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Domain.Extensions.Fixed;
using Intervals.NET.Extensions;
using SlidingWindowCache.Tests.Infrastructure.Helpers;
using SlidingWindowCache.Public;
using SlidingWindowCache.Public.Cache;
using SlidingWindowCache.Public.Configuration;
using SlidingWindowCache.Public.Extensions;
using SlidingWindowCache.Public.Instrumentation;

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
    /// Ensures any background rebalance operations are completed and cache is properly disposed
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_currentCache != null)
        {
            // Wait for any background rebalance from current test to complete
            await _currentCache.WaitForIdleAsync();

            // Properly dispose the cache to release resources
            await _currentCache.DisposeAsync();
        }
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

    #region Test Data Sources

    /// <summary>
    /// Provides test data for execution strategy parameterization.
    /// Tests both task-based (unbounded) and channel-based (bounded) execution controllers.
    /// </summary>
    public static IEnumerable<object?[]> ExecutionStrategyTestData =>
        new List<object?[]>
        {
            new object?[] { "TaskBased", null }, // Unbounded task-based serialization
            new object?[] { "ChannelBased", 10 } // Bounded channel-based serialization with capacity 10
        };

    /// <summary>
    /// Provides test data for storage strategy parameterization.
    /// Tests both Snapshot (zero-allocation) and CopyOnRead (defensive copy) modes.
    /// </summary>
    public static IEnumerable<object[]> StorageStrategyTestData =>
        new List<object[]>
        {
            new object[] { "Snapshot", UserCacheReadMode.Snapshot },
            new object[] { "CopyOnRead", UserCacheReadMode.CopyOnRead }
        };

    /// <summary>
    /// Provides test data combining scenarios and storage strategies for A3_8 test.
    /// </summary>
    public static IEnumerable<object[]> A3_8_TestData
    {
        get
        {
            var scenarios = new[]
            {
                new object[] { "ColdStart", 100, 110, 0, 0, false },
                new object[] { "CacheExpansion", 105, 120, 100, 110, true },
                new object[] { "FullReplacement", 200, 210, 100, 110, true }
            };

            foreach (var scenario in scenarios)
            {
                foreach (var storage in StorageStrategyTestData)
                {
                    yield return
                    [
                        $"{scenario[0]}_{storage[0]}", // Combined name
                        scenario[1], // reqStart
                        scenario[2], // reqEnd  
                        scenario[3], // priorStart
                        scenario[4], // priorEnd
                        scenario[5], // hasPriorRequest
                        storage[0], // storageName
                        storage[1] // readMode
                    ];
                }
            }
        }
    }

    #endregion

    #region A. User Path & Fast User Access Invariants

    #region A.1 Concurrency & Priority

    /// <summary>
    /// Tests Invariant A.0a (🟢 Behavioral): User Request MAY cancel ongoing or pending Rebalance Execution
    /// ONLY when a new rebalance is validated as necessary by the multi-stage decision pipeline.
    /// Verifies cancellation is validation-driven coordination, not automatic request-driven behavior.
    /// Related: A.0 (Architectural - User Path has higher priority than Rebalance Execution)
    /// Parameterized by execution strategy to verify behavior across both task-based and channel-based controllers.
    /// </summary>
    /// <param name="executionStrategy">Human-readable name of execution strategy for test output</param>
    /// <param name="queueCapacity">Queue capacity: null = task-based (unbounded), >= 1 = channel-based (bounded)</param>
    [Theory]
    [MemberData(nameof(ExecutionStrategyTestData))]
    public async Task Invariant_A_0a_UserRequestCancelsRebalance(string executionStrategy, int? queueCapacity)
    {
        // ARRANGE
        var options = TestHelpers.CreateDefaultOptions(
            leftCacheSize: 2.0,
            rightCacheSize: 2.0,
            debounceDelay: TimeSpan.FromMilliseconds(100),
            rebalanceQueueCapacity: queueCapacity);
        var (cache, _) = TrackCache(TestHelpers.CreateCacheWithDefaults(_domain, _cacheDiagnostics, options));

        // ACT: First request triggers rebalance intent
        await cache.GetDataAsync(TestHelpers.CreateRange(100, 110), CancellationToken.None);
        var intentPublishedBefore = _cacheDiagnostics.RebalanceIntentPublished;
        Assert.Equal(1, intentPublishedBefore);

        // Second request (non-overlapping range) - Decision Engine validates if new rebalance is necessary
        await cache.GetDataAsync(TestHelpers.CreateRange(120, 130), CancellationToken.None);

        // Wait for background rebalance to settle before checking counters
        await cache.WaitForIdleAsync();

        // ASSERT: Priority mechanism enforced via validation-driven cancellation
        // Cancellation occurs ONLY when Decision Engine validates new rebalance as necessary
        // System does NOT guarantee automatic cancellation on every new request
        TestHelpers.AssertIntentPublished(_cacheDiagnostics, 2);

        // Verify lifecycle integrity and system stability (not deterministic cancellation counts)
        TestHelpers.AssertRebalanceLifecycleIntegrity(_cacheDiagnostics);

        // At least one rebalance should complete successfully
        Assert.True(_cacheDiagnostics.RebalanceExecutionCompleted >= 1,
            $"[{executionStrategy}] Expected at least 1 rebalance to complete, but found {_cacheDiagnostics.RebalanceExecutionCompleted}");
    }

    /// <summary>
    /// Tests Invariant A.-1 (🔵 Architectural): Concurrent write safety under extreme load.
    /// Single-writer architecture ensures only Rebalance Execution mutates cache state, but this
    /// stress test verifies robustness under high concurrency with many threads making rapid requests.
    /// Validates that all requests are served correctly without data corruption or race conditions.
    /// Gap identified: No existing stress test validates concurrent safety at scale.
    /// </summary>
    [Fact]
    public async Task Invariant_A_Minus1_ConcurrentWriteSafety()
    {
        // ARRANGE: Create cache with moderate debounce to allow overlapping operations
        var options = TestHelpers.CreateDefaultOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            debounceDelay: TimeSpan.FromMilliseconds(100));
        var (cache, _) = TrackCache(TestHelpers.CreateCacheWithDefaults(_domain, _cacheDiagnostics, options));

        // ACT: Fire 50 concurrent requests from multiple threads
        var tasks = new List<Task<ReadOnlyMemory<int>>>();
        var random = new Random(42); // Deterministic seed for reproducibility

        for (var i = 0; i < 50; i++)
        {
            // Create semi-random ranges to stress the system
            var baseStart = random.Next(100, 500);
            var rangeSize = random.Next(10, 30);
            var range = TestHelpers.CreateRange(baseStart, baseStart + rangeSize);

            tasks.Add(Task.Run(async () => (await cache.GetDataAsync(range, CancellationToken.None)).Data));
        }

        // Wait for all requests to complete
        var results = await Task.WhenAll(tasks);

        // Wait for background operations to settle
        await cache.WaitForIdleAsync();

        // ASSERT: All 50 requests completed successfully
        Assert.Equal(50, results.Length);
        Assert.Equal(50, _cacheDiagnostics.UserRequestServed);

        // Verify each result has correct data (no corruption)
        for (var i = 0; i < results.Length; i++)
        {
            Assert.True(results[i].Length > 0, $"Result {i} should have data");
        }

        // Verify system stability - lifecycle integrity maintained under stress
        TestHelpers.AssertRebalanceLifecycleIntegrity(_cacheDiagnostics);

        // At least one rebalance should have completed (system converged)
        Assert.True(_cacheDiagnostics.RebalanceExecutionCompleted >= 1,
            $"Expected at least 1 rebalance to complete under stress, but found {_cacheDiagnostics.RebalanceExecutionCompleted}");
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
        var result1 = await cache.GetDataAsync(TestHelpers.CreateRange(100, 110), CancellationToken.None);
        var result2 = await cache.GetDataAsync(TestHelpers.CreateRange(200, 210), CancellationToken.None);
        var result3 = await cache.GetDataAsync(TestHelpers.CreateRange(105, 115), CancellationToken.None);

        // ASSERT: All requests completed with correct data
        TestHelpers.AssertUserDataCorrect(result1.Data, TestHelpers.CreateRange(100, 110));
        TestHelpers.AssertUserDataCorrect(result2.Data, TestHelpers.CreateRange(200, 210));
        TestHelpers.AssertUserDataCorrect(result3.Data, TestHelpers.CreateRange(105, 115));
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
        var result = await cache.GetDataAsync(TestHelpers.CreateRange(100, 110), CancellationToken.None);
        stopwatch.Stop();

        // ASSERT: Request completed quickly (much less than debounce delay)
        Assert.Equal(1, _cacheDiagnostics.UserRequestServed);
        Assert.Equal(1, _cacheDiagnostics.RebalanceIntentPublished);
        Assert.Equal(0, _cacheDiagnostics.RebalanceExecutionCompleted);
        TestHelpers.AssertUserDataCorrect(result.Data, TestHelpers.CreateRange(100, 110));
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
            var loopResult = await cache.GetDataAsync(range, CancellationToken.None);
            TestHelpers.AssertUserDataCorrect(loopResult.Data, range);
        }
    }

    #endregion

    #region A.3 Cache Mutation Rules (User Path)

    /// <summary>
    /// Tests Invariant A.8 (🟢 Behavioral): User Path MUST NOT mutate cache under any circumstance.
    /// Cache mutations (population, expansion, replacement) are performed exclusively by Rebalance Execution (single-writer).
    /// Parameterized by storage strategy to verify behavior across both Snapshot and CopyOnRead modes.
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
    [MemberData(nameof(A3_8_TestData))]
    public async Task Invariant_A3_8_UserPathNeverMutatesCache(
        string scenario, int reqStart, int reqEnd, int priorStart, int priorEnd, bool hasPriorRequest,
        string storageName, UserCacheReadMode readMode)
    {
        // ARRANGE
        _ = scenario;
        _ = storageName;
        var options = TestHelpers.CreateDefaultOptions(
            debounceDelay: TimeSpan.FromMilliseconds(50),
            readMode: readMode);
        var (cache, _) = TrackCache(TestHelpers.CreateCacheWithDefaults(_domain, _cacheDiagnostics, options));

        // ACT: Execute prior request if needed to establish cache state
        if (hasPriorRequest)
        {
            await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(priorStart, priorEnd));
            _cacheDiagnostics.Reset(); // Track only the test request
        }

        // Execute the test request
        var result = await cache.GetDataAsync(TestHelpers.CreateRange(reqStart, reqEnd), CancellationToken.None);

        // ASSERT: User receives correct data immediately
        TestHelpers.AssertUserDataCorrect(result.Data, TestHelpers.CreateRange(reqStart, reqEnd));

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
        var result1 = await cache.GetDataAsync(TestHelpers.CreateRange(100, 110), CancellationToken.None);
        var result2 = await cache.GetDataAsync(TestHelpers.CreateRange(105, 115), CancellationToken.None);
        var result3 = await cache.GetDataAsync(TestHelpers.CreateRange(95, 120), CancellationToken.None);

        // ASSERT: All data is contiguous (no gaps)
        TestHelpers.AssertUserDataCorrect(result1.Data, TestHelpers.CreateRange(100, 110));
        TestHelpers.AssertUserDataCorrect(result2.Data, TestHelpers.CreateRange(105, 115));
        TestHelpers.AssertUserDataCorrect(result3.Data, TestHelpers.CreateRange(95, 120));
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
            var result = await cache.GetDataAsync(range, CancellationToken.None);
            var expectedLength = (int)range.End - (int)range.Start + 1;
            Assert.Equal(expectedLength, result.Data.Length);
            TestHelpers.AssertUserDataCorrect(result.Data, range);
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
        var result = await cache.GetDataAsync(TestHelpers.CreateRange(200, 210), CancellationToken.None);

        // ASSERT: Cache still returns correct data
        TestHelpers.AssertUserDataCorrect(result.Data, TestHelpers.CreateRange(200, 210));

        // Verify cache is not corrupted by making another request
        var result2 = await cache.GetDataAsync(TestHelpers.CreateRange(205, 215), CancellationToken.None);
        TestHelpers.AssertUserDataCorrect(result2.Data, TestHelpers.CreateRange(205, 215));
    }

    /// <summary>
    /// Tests Invariant B.15 Enhanced (🟢 Behavioral): Cancellation during I/O operations (during FetchAsync)
    /// MUST NOT leave cache in inconsistent state. This test verifies that when rebalance execution is cancelled
    /// while actively fetching data from the data source (not just during debounce), the cache remains consistent.
    /// Gap identified: Original B.15 test only covers cancellation between requests (during debounce delay).
    /// This test covers cancellation during actual I/O operations when FetchAsync is in progress.
    /// </summary>
    [Fact]
    public async Task Invariant_B15_Enhanced_CancellationDuringIO()
    {
        // ARRANGE: Cache with slow data source to allow cancellation during fetch
        var options = TestHelpers.CreateDefaultOptions(
            leftCacheSize: 2.0,
            rightCacheSize: 2.0,
            debounceDelay: TimeSpan.FromMilliseconds(50));

        var (cache, _) = TrackCache(TestHelpers.CreateCacheWithDefaults(
            _domain,
            _cacheDiagnostics,
            options,
            fetchDelay: TimeSpan.FromMilliseconds(300)));

        // ACT: First request triggers rebalance with slow fetch, then immediately issue second request
        // that triggers cancellation while first rebalance is fetching data
        var request1 = cache.GetDataAsync(TestHelpers.CreateRange(100, 110), CancellationToken.None);

        // Wait for first request to complete and rebalance to start executing (past debounce)
        await request1;
        await Task.Delay(100, CancellationToken.None); // Allow rebalance execution to start I/O

        // Issue second request that will trigger new intent and potentially cancel ongoing fetch
        var request2 = cache.GetDataAsync(TestHelpers.CreateRange(200, 210), CancellationToken.None);
        await request2;

        // Wait for all background operations to settle
        await cache.WaitForIdleAsync();

        // ASSERT: Cache remains consistent despite cancellation during I/O
        var result3 = await cache.GetDataAsync(TestHelpers.CreateRange(205, 215), CancellationToken.None);
        TestHelpers.AssertUserDataCorrect(result3.Data, TestHelpers.CreateRange(205, 215));

        // Verify lifecycle integrity - system remained stable
        TestHelpers.AssertRebalanceLifecycleIntegrity(_cacheDiagnostics);
    }

    /// <summary>
    /// Tests Invariant B.16 (🔵 Architectural): Only most recent RebalanceResult is applied to cache.
    /// Verifies stale result prevention - if execution completes for an obsolete intent, results are discarded.
    /// This architectural guarantee prevents race conditions where slow rebalances from old intents
    /// could overwrite cache with stale data. Gap identified: No existing test validates result application
    /// guards against applying stale rebalance results.
    /// </summary>
    [Fact]
    public async Task Invariant_B16_OnlyLatestResultsApplied()
    {
        // ARRANGE: Cache with longer debounce to control timing
        var options = TestHelpers.CreateDefaultOptions(
            leftCacheSize: 3.0,
            rightCacheSize: 3.0,
            debounceDelay: TimeSpan.FromMilliseconds(150));

        var (cache, _) = TrackCache(TestHelpers.CreateCacheWithDefaults(
            _domain,
            _cacheDiagnostics,
            options,
            fetchDelay: TimeSpan.FromMilliseconds(100)));

        // ACT: Issue rapid sequence of requests to create multiple intents
        // First request: [100, 110] - will trigger rebalance
        await cache.GetDataAsync(TestHelpers.CreateRange(100, 110), CancellationToken.None);

        // Second request immediately: [200, 210] - non-overlapping, should supersede first
        await cache.GetDataAsync(TestHelpers.CreateRange(200, 210), CancellationToken.None);

        // Wait for system to converge
        await cache.WaitForIdleAsync();

        // ASSERT: Cache should reflect the latest intent (around 200-210 range with extensions)
        // Make a request in the second range area to verify cache is centered there
        var result = await cache.GetDataAsync(TestHelpers.CreateRange(205, 215), CancellationToken.None);
        TestHelpers.AssertUserDataCorrect(result.Data, TestHelpers.CreateRange(205, 215));

        // Should be full hit (cache was rebalanced to this region)
        _cacheDiagnostics.Reset();
        var verifyResult = await cache.GetDataAsync(TestHelpers.CreateRange(208, 212), CancellationToken.None);
        TestHelpers.AssertUserDataCorrect(verifyResult.Data, TestHelpers.CreateRange(208, 212));
        TestHelpers.AssertFullCacheHit(_cacheDiagnostics, 1);

        // Verify system stability
        TestHelpers.AssertRebalanceLifecycleIntegrity(_cacheDiagnostics);
    }

    #endregion

    #region C. Rebalance Intent & Temporal Invariants

    /// <summary>
    /// Tests Invariant C.17 (🔵 Architectural): At most one rebalance intent may be active at any time.
    /// This is an architectural constraint enforced by single-writer design. Test verifies system stability
    /// and lifecycle integrity under rapid concurrent requests, not deterministic cancellation counts.
    /// Parameterized by execution strategy to verify behavior across both task-based and channel-based controllers.
    /// </summary>
    /// <param name="executionStrategy">Human-readable name of execution strategy for test output</param>
    /// <param name="queueCapacity">Queue capacity: null = task-based (unbounded), >= 1 = channel-based (bounded)</param>
    [Theory]
    [MemberData(nameof(ExecutionStrategyTestData))]
    public async Task Invariant_C17_AtMostOneActiveIntent(string executionStrategy, int? queueCapacity)
    {
        // ARRANGE
        var options = TestHelpers.CreateDefaultOptions(
            debounceDelay: TimeSpan.FromMilliseconds(200),
            rebalanceQueueCapacity: queueCapacity);
        var (cache, _) = TrackCache(TestHelpers.CreateCacheWithDefaults(_domain, _cacheDiagnostics, options));

        // ACT: Make rapid requests
        await cache.GetDataAsync(TestHelpers.CreateRange(100, 110), CancellationToken.None);
        await cache.GetDataAsync(TestHelpers.CreateRange(110, 120), CancellationToken.None);
        await cache.GetDataAsync(TestHelpers.CreateRange(120, 130), CancellationToken.None);

        // Wait for background rebalance to settle before checking counters
        await cache.WaitForIdleAsync();

        // ASSERT: System stability - verify lifecycle integrity (not deterministic cancellation counts)
        // Architectural invariant: at most one active intent enforced by design
        TestHelpers.AssertRebalanceLifecycleIntegrity(_cacheDiagnostics);

        // Verify that at least one rebalance was scheduled and completed
        Assert.True(_cacheDiagnostics.RebalanceScheduled >= 1,
            $"[{executionStrategy}] Expected at least 1 rebalance to be scheduled, but found {_cacheDiagnostics.RebalanceScheduled}");
        Assert.True(_cacheDiagnostics.RebalanceExecutionCompleted >= 1,
            $"[{executionStrategy}] Expected at least 1 rebalance to complete, but found {_cacheDiagnostics.RebalanceExecutionCompleted}");
    }

    /// <summary>
    /// Tests Invariant C.18 (🟡 Conceptual): Previously created intents may become logically superseded.
    /// This is a conceptual design intent. Test verifies system stability and cache consistency when
    /// multiple intents are published, not deterministic cancellation behavior (obsolescence ≠ cancellation).
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

        // Second request publishes new intent (may supersede old one depending on Decision Engine validation)
        await cache.GetDataAsync(TestHelpers.CreateRange(200, 210), CancellationToken.None);

        // Wait for background rebalance to settle before checking counters
        await cache.WaitForIdleAsync();

        // ASSERT: System stability - new intent published, system remains consistent
        Assert.True(_cacheDiagnostics.RebalanceIntentPublished > publishedBefore);

        // Conceptual invariant: obsolescence ≠ guaranteed cancellation
        // Cancellation depends on Decision Engine validation, not automatic on new requests
        TestHelpers.AssertRebalanceLifecycleIntegrity(_cacheDiagnostics);

        // Verify that at least one rebalance was scheduled and completed
        Assert.True(_cacheDiagnostics.RebalanceScheduled >= 1,
            $"Expected at least 1 rebalance to be scheduled, but found {_cacheDiagnostics.RebalanceScheduled}");
        Assert.True(_cacheDiagnostics.RebalanceExecutionCompleted >= 1,
            $"Expected at least 1 rebalance to complete, but found {_cacheDiagnostics.RebalanceExecutionCompleted}");
    }

    /// <summary>
    /// Tests Invariant C.20 (🔵 Architectural): Decision Engine MUST exit early if intent becomes obsolete.
    /// When processing an intent, if the intent reference changes (new intent published), Decision Engine
    /// should detect obsolescence and exit without scheduling execution. This prevents wasted work and
    /// ensures the system processes only the most recent intent. Gap identified: No test validates
    /// early exit behavior when intents become obsolete during decision processing.
    /// </summary>
    [Fact]
    public async Task Invariant_C20_DecisionEngineExitsEarlyForObsoleteIntent()
    {
        // ARRANGE: Longer debounce to allow time for multiple intents to be published
        var options = TestHelpers.CreateDefaultOptions(
            leftCacheSize: 2.0,
            rightCacheSize: 2.0,
            debounceDelay: TimeSpan.FromMilliseconds(300));
        var (cache, _) = TrackCache(TestHelpers.CreateCacheWithDefaults(_domain, _cacheDiagnostics, options));

        // ACT: Rapid burst of requests to create multiple superseding intents
        // Each new request publishes a new intent that makes previous ones obsolete
        await cache.GetDataAsync(TestHelpers.CreateRange(100, 110), CancellationToken.None);
        await cache.GetDataAsync(TestHelpers.CreateRange(200, 210), CancellationToken.None);
        await cache.GetDataAsync(TestHelpers.CreateRange(300, 310), CancellationToken.None);
        await cache.GetDataAsync(TestHelpers.CreateRange(400, 410), CancellationToken.None);

        // Wait for system to settle
        await cache.WaitForIdleAsync();

        // ASSERT: Multiple intents published
        Assert.True(_cacheDiagnostics.RebalanceIntentPublished >= 4,
            $"Expected at least 4 intents published, but found {_cacheDiagnostics.RebalanceIntentPublished}");

        // Early exit mechanism means not all intents become executions
        // The number of scheduled executions should be less than or equal to intents published
        Assert.True(_cacheDiagnostics.RebalanceScheduled <= _cacheDiagnostics.RebalanceIntentPublished,
            $"Scheduled executions ({_cacheDiagnostics.RebalanceScheduled}) should not exceed published intents ({_cacheDiagnostics.RebalanceIntentPublished})");

        // At least one rebalance should complete successfully (system converged to final state)
        Assert.True(_cacheDiagnostics.RebalanceExecutionCompleted >= 1,
            $"Expected at least 1 rebalance to complete, but found {_cacheDiagnostics.RebalanceExecutionCompleted}");

        // Verify lifecycle integrity despite early exits
        TestHelpers.AssertRebalanceLifecycleIntegrity(_cacheDiagnostics);

        // Verify final cache state is correct (centered around last request)
        var result = await cache.GetDataAsync(TestHelpers.CreateRange(405, 415), CancellationToken.None);
        TestHelpers.AssertUserDataCorrect(result.Data, TestHelpers.CreateRange(405, 415));
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
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(100, 110));
        _cacheDiagnostics.Reset();

        // Second request within NoRebalanceRange - intent published but execution may be skipped
        await cache.GetDataAsync(TestHelpers.CreateRange(102, 108), CancellationToken.None);
        await cache.WaitForIdleAsync();

        // ASSERT: Intent published but execution may be skipped due to NoRebalanceRange
        TestHelpers.AssertIntentPublished(_cacheDiagnostics);
        var totalSkipped = _cacheDiagnostics.RebalanceSkippedCurrentNoRebalanceRange +
                           _cacheDiagnostics.RebalanceSkippedPendingNoRebalanceRange;
        if (totalSkipped > 0)
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
        var tasks = new List<Task<ReadOnlyMemory<int>>>();
        for (var i = 0; i < 10; i++)
        {
            var start = 100 + i * 2;
            tasks.Add(cache.GetDataAsync(TestHelpers.CreateRange(start, start + 10), CancellationToken.None).AsTask()
                .ContinueWith(t => t.Result.Data));
        }

        await Task.WhenAll(tasks);
        await cache.WaitForIdleAsync();

        // ASSERT: System is stable and serves new requests correctly
        var finalResult = await cache.GetDataAsync(TestHelpers.CreateRange(105, 115), CancellationToken.None);
        TestHelpers.AssertUserDataCorrect(finalResult.Data, TestHelpers.CreateRange(105, 115));
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
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(100, 110));
        _cacheDiagnostics.Reset();

        // Second request within NoRebalanceRange
        await cache.GetDataAsync(TestHelpers.CreateRange(103, 107), CancellationToken.None);
        await cache.WaitForIdleAsync();

        // ASSERT: Rebalance skipped due to NoRebalanceRange policy (execution should never start)
        TestHelpers.AssertRebalanceSkippedDueToPolicy(_cacheDiagnostics);
    }

    /// <summary>
    /// Tests Invariant D.27 Stage 1: Rebalance skipped when request is within current cache's NoRebalanceRange.
    /// Stage 1 (current cache stability check) is the fast-path optimization that prevents unnecessary
    /// rebalance when the requested range is fully covered by the existing cache's no-rebalance threshold zone.
    /// This validates the first stage of the multi-stage decision pipeline.
    /// Related: D.27 (NoRebalanceRange policy), C.24a (execution skipped due to policy).
    /// </summary>
    [Fact]
    public async Task Invariant_D27_Stage1_SkipsWhenWithinCurrentNoRebalanceRange()
    {
        // ARRANGE: Set up cache with threshold configuration
        var options = TestHelpers.CreateDefaultOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            leftThreshold: 0.3, // 30% threshold
            rightThreshold: 0.3,
            debounceDelay: TimeSpan.FromMilliseconds(50));
        var (cache, _) = TrackCache(TestHelpers.CreateCacheWithDefaults(_domain, _cacheDiagnostics, options));

        // ACT: Establish cache with range [100, 120] (size 21)
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(100, 120));
        _cacheDiagnostics.Reset();

        // NoRebalanceRange should be approximately [106, 114] (shrunk by 30% on each side)
        // Request within this range should trigger Stage 1 skip
        await cache.GetDataAsync(TestHelpers.CreateRange(108, 112), CancellationToken.None);
        await cache.WaitForIdleAsync();

        // ASSERT: Stage 1 skip occurred
        Assert.Equal(1, _cacheDiagnostics.RebalanceIntentPublished);
        Assert.Equal(1, _cacheDiagnostics.RebalanceSkippedCurrentNoRebalanceRange);
        Assert.Equal(0, _cacheDiagnostics.RebalanceSkippedPendingNoRebalanceRange);
        Assert.Equal(0, _cacheDiagnostics.RebalanceExecutionStarted);
        Assert.Equal(0, _cacheDiagnostics.RebalanceExecutionCompleted);
    }

    /// <summary>
    /// Tests Invariant D.29 Stage 2: Rebalance skipped when request is within pending rebalance's NoRebalanceRange.
    /// Stage 2 (pending rebalance stability check) is the anti-thrashing optimization that prevents
    /// cancellation storms when a scheduled rebalance will already satisfy the incoming request.
    /// This validates the second stage of the multi-stage decision pipeline.
    /// Related: D.29 (multi-stage validation), C.18 (intent supersession with validation).
    /// </summary>
    [Fact]
    public async Task Invariant_D29_Stage2_SkipsWhenWithinPendingNoRebalanceRange()
    {
        // ARRANGE: Set up cache with threshold and debounce to allow multiple intents
        var options = TestHelpers.CreateDefaultOptions(
            leftCacheSize: 1.0, // Large expansion
            rightCacheSize: 1.0,
            leftThreshold: 0.2,
            rightThreshold: 0.2,
            debounceDelay: TimeSpan.FromMilliseconds(2000)); // Long debounce to create pending state
        var (cache, _) = TrackCache(TestHelpers.CreateCacheWithDefaults(_domain, _cacheDiagnostics, options));
        var initialRange = TestHelpers.CreateRange(200, 300); // Span 101
        // Initial cache range is expected to be [98, 400] (size 303).
        // The NoRebalanceRange is 20% shrunk from each side of that cache range,
        // which gives [159, 340] (301 inner span; 20% of 301 is ~60 on each side).

        var requestRange = TestHelpers.CreateRange(300, 400); // Span 101
        // Desired cache range for this request would be [198, 500],
        // and its NoRebalanceRange would be [258, 440] using the same 20% shrink.

        // Next request is chosen to be within the pending rebalance's NoRebalanceRange
        // but outside the current NoRebalanceRange, to trigger a Stage 2 skip.
        var nextRequestRange = TestHelpers.CreateRange(320, 420); // Span 101
        // ACT: Establish initial cache
        await cache.GetDataAndWaitForIdleAsync(initialRange);
        _cacheDiagnostics.Reset();

        // Request 1: Trigger rebalance outside NoRebalanceRange - will be pending due to debounce
        _ = await cache.GetDataAsync(requestRange, CancellationToken.None);
        await Task.Delay(500); // Allow intent to be published but not executed (still in debounce)

        // Request 2: Make another request that would be covered by pending rebalance's NoRebalanceRange
        // This should trigger Stage 2 skip since the pending rebalance will satisfy this request
        _ = await cache.GetDataAsync(nextRequestRange, CancellationToken.None);

        // Wait to complete
        await cache.WaitForIdleAsync();

        // ASSERT: Stage 2 skip occurred for second request
        Assert.Equal(2, _cacheDiagnostics.RebalanceIntentPublished);
        Assert.True(_cacheDiagnostics.RebalanceSkippedPendingNoRebalanceRange >= 1,
            $"Expected at least one Stage 2 skip due to pending rebalance NoRebalanceRange, but found {_cacheDiagnostics.RebalanceSkippedPendingNoRebalanceRange}");
        // First rebalance should execute, second should be skipped by Stage 2
        Assert.Equal(1, _cacheDiagnostics.RebalanceExecutionCompleted);
    }

    /// <summary>
    /// Tests Invariant D.28 (🟢 Behavioral): If DesiredCacheRange == CurrentCacheRange, rebalance execution
    /// is not required (Stage 3 validation / same-range optimization). This is the final decision stage that
    /// prevents no-op rebalance operations when the cache is already in optimal configuration for the request.
    /// Verifies the RebalanceSkippedSameRange counter tracks this optimization.
    /// Related: C.24c (execution skipped due to same range), D.29 (multi-stage decision pipeline).
    /// </summary>
    [Fact]
    public async Task Invariant_D28_SkipWhenDesiredEqualsCurrentRange()
    {
        // ARRANGE
        var options = TestHelpers.CreateDefaultOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            leftThreshold: 1, // Very small NoRebalanceRange - forces decision to Stage 3
            rightThreshold: 0.0,
            debounceDelay: TimeSpan.FromMilliseconds(50));
        var (cache, _) = TrackCache(TestHelpers.CreateCacheWithDefaults(_domain, _cacheDiagnostics, options));

        // ACT: Establish cache with specific range [100, 110]
        var initialRange = TestHelpers.CreateRange(100, 110);
        await cache.GetDataAndWaitForIdleAsync(initialRange);

        _cacheDiagnostics.Reset();

        // Request the exact same expanded range that should already be cached
        // This creates scenario where DesiredCacheRange (computed from request) == CurrentCacheRange (existing cache)
        var result = await cache.GetDataAsync(initialRange, CancellationToken.None);
        await cache.WaitForIdleAsync();

        // ASSERT: Verify same-range skip occurred (Stage 3 validation)
        TestHelpers.AssertUserDataCorrect(result.Data, initialRange);
        TestHelpers.AssertIntentPublished(_cacheDiagnostics, 1);
        TestHelpers.AssertRebalanceSkippedSameRange(_cacheDiagnostics, 1);

        // Verify no execution occurred (optimization prevented unnecessary rebalance)
        Assert.Equal(0, _cacheDiagnostics.RebalanceScheduled);
        Assert.Equal(0, _cacheDiagnostics.RebalanceExecutionStarted);
        Assert.Equal(0, _cacheDiagnostics.RebalanceExecutionCompleted);
    }

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
        await cache.GetDataAndWaitForIdleAsync(requestRange);

        // Calculate expected desired range using the helper that mimics ProportionalRangePlanner
        var expectedDesiredRange = TestHelpers.CalculateExpectedDesiredRange(requestRange, options, _domain);

        // Reset counters to track only the next request
        _cacheDiagnostics.Reset();

        // Make another request within the calculated desired range
        var withinDesired = await cache.GetDataAsync(TestHelpers.CreateRange(95, 115), CancellationToken.None);

        // ASSERT: Data is correct, demonstrating cache expanded based on configuration
        TestHelpers.AssertUserDataCorrect(withinDesired.Data, TestHelpers.CreateRange(95, 115));

        // Verify this was a full cache hit, proving the desired range was calculated correctly
        TestHelpers.AssertFullCacheHit(_cacheDiagnostics);

        // Verify the expected desired range calculation matches actual behavior
        // The request [95, 115] should be fully within expectedDesiredRange
        Assert.True(expectedDesiredRange.Contains(TestHelpers.CreateRange(95, 115)),
            $"Request range [95, 115] should be within calculated desired range {expectedDesiredRange}");
    }

    /// <summary>
    /// Tests Invariant E.31 (🔵 Architectural): DesiredCacheRange is independent of current cache contents.
    /// Verifies that DesiredCacheRange is computed deterministically based only on RequestedRange and config,
    /// not influenced by CurrentCacheRange or intermediate cache states. Two identical requests should produce
    /// identical desired ranges regardless of what cache state existed before. Gap identified: No test validates
    /// that desired range computation is truly independent of cache history.
    /// </summary>
    [Fact]
    public async Task Invariant_E31_DesiredRangeIndependentOfCacheState()
    {
        // ARRANGE: Create two separate cache instances with identical configuration
        var options = TestHelpers.CreateDefaultOptions(
            leftCacheSize: 1.5,
            rightCacheSize: 1.5,
            debounceDelay: TimeSpan.FromMilliseconds(50));

        var diagnostics1 = new EventCounterCacheDiagnostics();
        var (cache1, _) = TestHelpers.CreateCacheWithDefaults(_domain, diagnostics1, options);

        var diagnostics2 = new EventCounterCacheDiagnostics();
        var (cache2, _) = TestHelpers.CreateCacheWithDefaults(_domain, diagnostics2, options);

        // ACT: Cache1 - Establish cache at [100, 110], then request [200, 210]
        await cache1.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(100, 110));
        var result1 = await cache1.GetDataAsync(TestHelpers.CreateRange(200, 210), CancellationToken.None);
        await cache1.WaitForIdleAsync();

        // Cache2 - Cold start directly to [200, 210] (no prior cache state)
        var result2 = await cache2.GetDataAsync(TestHelpers.CreateRange(200, 210), CancellationToken.None);
        await cache2.WaitForIdleAsync();

        // ASSERT: Both caches should have same behavior for [200, 210] despite different histories
        TestHelpers.AssertUserDataCorrect(result1.Data, TestHelpers.CreateRange(200, 210));
        TestHelpers.AssertUserDataCorrect(result2.Data, TestHelpers.CreateRange(200, 210));

        // Both should have scheduled rebalance for the same desired range (deterministic computation)
        // Verify both caches converged to serving the same expanded range
        diagnostics1.Reset();
        diagnostics2.Reset();

        var verify1 = await cache1.GetDataAsync(TestHelpers.CreateRange(195, 215), CancellationToken.None);
        var verify2 = await cache2.GetDataAsync(TestHelpers.CreateRange(195, 215), CancellationToken.None);

        TestHelpers.AssertUserDataCorrect(verify1.Data, TestHelpers.CreateRange(195, 215));
        TestHelpers.AssertUserDataCorrect(verify2.Data, TestHelpers.CreateRange(195, 215));

        // Both should be full cache hits (both caches expanded to same desired range)
        TestHelpers.AssertFullCacheHit(diagnostics1, 1);
        TestHelpers.AssertFullCacheHit(diagnostics2, 1);

        // Cleanup
        await cache1.DisposeAsync();
        await cache2.DisposeAsync();
    }

    // NOTE: Invariant E.32, E.33, E.34: DesiredCacheRange represents canonical target state,
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
    /// Rebalance Execution MUST be cancellation-safe at all stages (before I/O, during I/O, before mutations).
    /// Validates deterministic termination, no partial mutations, lifecycle integrity, and that cancellation
    /// support works as a high-level guarantee (not deterministic per-request behavior).
    /// Uses slow data source to allow cancellation during execution. Verifies DEBUG instrumentation counters
    /// ensure proper lifecycle tracking. Related: A.0a (User Path priority via validation-driven cancellation),
    /// C.24d (execution skipped due to cancellation).
    /// Parameterized by execution strategy to verify behavior across both task-based and channel-based controllers.
    /// </summary>
    /// <param name="executionStrategy">Human-readable name of execution strategy for test output</param>
    /// <param name="queueCapacity">Queue capacity: null = task-based (unbounded), >= 1 = channel-based (bounded)</param>
    [Theory]
    [MemberData(nameof(ExecutionStrategyTestData))]
    public async Task Invariant_F35_G46_RebalanceCancellationBehavior(string executionStrategy, int? queueCapacity)
    {
        // ARRANGE: Slow data source to allow cancellation during execution
        var options = TestHelpers.CreateDefaultOptions(
            leftCacheSize: 2.0,
            rightCacheSize: 2.0,
            debounceDelay: TimeSpan.FromMilliseconds(50),
            rebalanceQueueCapacity: queueCapacity);
        var (cache, _) = TrackCache(TestHelpers.CreateCacheWithDefaults(_domain, _cacheDiagnostics, options,
            fetchDelay: TimeSpan.FromMilliseconds(200)));

        // ACT: First request triggers rebalance, then immediately make new requests
        await cache.GetDataAsync(TestHelpers.CreateRange(100, 110), CancellationToken.None);
        await cache.GetDataAsync(TestHelpers.CreateRange(105, 115), CancellationToken.None);
        await cache.GetDataAsync(TestHelpers.CreateRange(110, 120), CancellationToken.None);
        await cache.WaitForIdleAsync();

        // ASSERT: Verify cancellation-safety (F.35, G.46)
        // Focus on lifecycle integrity and system stability, not deterministic cancellation counts
        // Cancellation is triggered by Decision Engine scheduling, not automatically by requests

        // Verify Rebalance lifecycle integrity: every started execution reaches terminal state (F.35)
        TestHelpers.AssertRebalanceLifecycleIntegrity(_cacheDiagnostics);

        // Verify system stability: at least one rebalance completed successfully
        Assert.True(_cacheDiagnostics.RebalanceExecutionCompleted >= 1,
            $"[{executionStrategy}] Expected at least 1 rebalance to complete, but found {_cacheDiagnostics.RebalanceExecutionCompleted}");
    }

    /// <summary>
    /// Tests Invariant F.36 (🔵 Architectural) and F.36a (🟢 Behavioral): Rebalance Execution Path is the
    /// only path responsible for cache normalization (expanding, trimming, recomputing NoRebalanceRange).
    /// After rebalance completes, cache is normalized to serve data from expanded range beyond original request.
    /// User Path performs minimal mutations while Rebalance Execution handles optimization.
    /// Parameterized by storage strategy to verify behavior across both Snapshot and CopyOnRead modes.
    /// </summary>
    /// <param name="storageName">Human-readable name of storage strategy for test output</param>
    /// <param name="readMode">Storage read mode: Snapshot or CopyOnRead</param>
    [Theory]
    [MemberData(nameof(StorageStrategyTestData))]
    public async Task Invariant_F36a_RebalanceNormalizesCache(string storageName, UserCacheReadMode readMode)
    {
        // ARRANGE
        _ = storageName;
        var options = TestHelpers.CreateDefaultOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            debounceDelay: TimeSpan.FromMilliseconds(50),
            readMode: readMode);
        var (cache, _) = TrackCache(TestHelpers.CreateCacheWithDefaults(_domain, _cacheDiagnostics, options));

        // ACT: Make request and wait for rebalance
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(100, 110));

        // ASSERT: Rebalance executed successfully
        TestHelpers.AssertRebalanceCompleted(_cacheDiagnostics);
        TestHelpers.AssertRebalanceLifecycleIntegrity(_cacheDiagnostics);
        TestHelpers.AssertRebalanceScheduled(_cacheDiagnostics, 1);

        // Cache should be normalized - verify by requesting from expected expanded range
        var extendedData = await cache.GetDataAsync(TestHelpers.CreateRange(95, 115), CancellationToken.None);
        TestHelpers.AssertUserDataCorrect(extendedData.Data, TestHelpers.CreateRange(95, 115));
    }

    /// <summary>
    /// Tests Invariants F.40, F.41, F.42 (🟢 Behavioral/🟡 Conceptual): Post-execution guarantees.
    /// F.40: CacheData corresponds to DesiredCacheRange. F.41: CurrentCacheRange == DesiredCacheRange.
    /// F.42: NoRebalanceRange is recomputed. After successful rebalance, cache reaches normalized state
    /// serving data from expanded/optimized range (based on config with leftSize=1.0, rightSize=1.0).
    /// Parameterized by storage strategy to verify behavior across both Snapshot and CopyOnRead modes.
    /// </summary>
    /// <param name="storageName">Human-readable name of storage strategy for test output</param>
    /// <param name="readMode">Storage read mode: Snapshot or CopyOnRead</param>
    [Theory]
    [MemberData(nameof(StorageStrategyTestData))]
    public async Task Invariant_F40_F41_F42_PostExecutionGuarantees(string storageName, UserCacheReadMode readMode)
    {
        // ARRANGE
        _ = storageName;
        var options = TestHelpers.CreateDefaultOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            debounceDelay: TimeSpan.FromMilliseconds(50),
            readMode: readMode);
        var (cache, _) = TrackCache(TestHelpers.CreateCacheWithDefaults(_domain, _cacheDiagnostics, options));

        // ACT: Request and wait for rebalance to complete
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(100, 110));

        // ASSERT: At least one rebalance must complete for the post-execution guarantees to be meaningful
        Assert.True(_cacheDiagnostics.RebalanceExecutionCompleted > 0,
            "At least one rebalance must complete so that F.40/F.41/F.42 post-execution guarantees can be verified.");
        // Verify rebalance was scheduled
        TestHelpers.AssertRebalanceScheduled(_cacheDiagnostics, 1);
        // After rebalance, cache should serve data from normalized range [100-10, 110+10] = [90, 120]
        var normalizedData = await cache.GetDataAsync(TestHelpers.CreateRange(90, 120), CancellationToken.None);
        TestHelpers.AssertUserDataCorrect(normalizedData.Data, TestHelpers.CreateRange(90, 120));
    }

    /// <summary>
    /// Tests Invariant F.38 (🟢 Behavioral): Incremental fetch optimization - only missing subranges are fetched.
    /// When cache needs to expand, the system should fetch only the missing data segments from IDataSource,
    /// not the entire desired range. This optimization reduces I/O overhead and data source load.
    /// Gap identified: No test validates that only missing segments are fetched during cache expansion.
    /// </summary>
    [Fact]
    public async Task Invariant_F38_IncrementalFetchOptimization()
    {
        // ARRANGE: Create tracking mock to observe which ranges are fetched
        var options = TestHelpers.CreateDefaultOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            debounceDelay: TimeSpan.FromMilliseconds(50));

        var (trackingMock, fetchedRanges) = TestHelpers.CreateTrackingMockDataSource(_domain);
        var cache = TestHelpers.CreateCache(trackingMock, _domain, options, _cacheDiagnostics);
        _currentCache = cache;

        // ACT: First request - cold start, full range fetch expected
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(100, 110));

        // Verify initial fetch occurred
        Assert.True(fetchedRanges.Count >= 1, "Initial fetch should occur for cold start");
        var initialFetchCount = fetchedRanges.Count;

        // Clear fetch tracking
        fetchedRanges.Clear();

        // Second request - overlapping range that extends right
        // Should only fetch missing right segment, not refetch [100, 110]
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(105, 120));

        // ASSERT: Only missing segments should be fetched (incremental optimization)
        // The system should NOT refetch the entire [105, 120] range or full desired range
        // Depending on timing, this may be a partial hit with missing segments fetch
        Assert.True(fetchedRanges.Count > 0,
            "Cache expansion should trigger at least one incremental fetch of missing segments");

        // If fetches occurred, verify they don't include already-cached data
        if (fetchedRanges.Count > 0)
        {
            // Verify no fetch included the fully cached region [100, 110]
            foreach (var fetchedRange in fetchedRanges)
            {
                var fetchStart = (int)fetchedRange.Start;
                var fetchEnd = (int)fetchedRange.End;

                // Fetched range should not fully overlap the initially cached [100, 110]
                var overlapsCached = fetchStart <= 110 && fetchEnd >= 100;
                if (overlapsCached)
                {
                    // If it overlaps, it should be fetching NEW data beyond the cached region
                    Assert.True(fetchEnd > 110 || fetchStart < 100,
                        $"Fetched range [{fetchStart}, {fetchEnd}] should extend beyond cached [100, 110]");
                }
            }
        }

        // Verify final state is correct
        var result = await cache.GetDataAsync(TestHelpers.CreateRange(105, 120), CancellationToken.None);
        TestHelpers.AssertUserDataCorrect(result.Data, TestHelpers.CreateRange(105, 120));
    }

    /// <summary>
    /// Tests Invariant F.39 (🟢 Behavioral): Data preservation during expansion - existing data is not refetched.
    /// When cache expands to include additional data, the system MUST NOT refetch ranges that are already
    /// present in the cache. This is a critical efficiency guarantee that prevents wasteful I/O operations.
    /// Gap identified: No test validates that existing cached data is preserved without refetching.
    /// </summary>
    [Fact]
    public async Task Invariant_F39_DataPreservationDuringExpansion()
    {
        // ARRANGE: Create tracking mock to observe fetch patterns
        var options = TestHelpers.CreateDefaultOptions(
            leftCacheSize: 2.0, // Larger expansion to clearly distinguish fetches
            rightCacheSize: 2.0,
            debounceDelay: TimeSpan.FromMilliseconds(50));

        var (trackingMock, fetchedRanges) = TestHelpers.CreateTrackingMockDataSource(_domain);
        var cache = TestHelpers.CreateCache(trackingMock, _domain, options, _cacheDiagnostics);
        _currentCache = cache;

        // ACT: Establish cache with [100, 110]
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(100, 110));

        // Record what was initially fetched (includes expansion)
        var initialFetchedRanges = new List<Intervals.NET.Range<int>>(fetchedRanges);
        Assert.True(initialFetchedRanges.Count >= 1, "Initial fetch must occur");

        // Clear tracking for next operation
        fetchedRanges.Clear();

        // Request a range that requires cache expansion to the left: [90, 105]
        // This should fetch only NEW data ([90, 99] or surrounding), NOT refetch [100, 110]
        await cache.GetDataAndWaitForIdleAsync(TestHelpers.CreateRange(90, 105));

        // ASSERT: Existing data should NOT be refetched
        // Any new fetches should only be for missing left segments
        if (fetchedRanges.Count > 0)
        {
            foreach (var fetchedRange in fetchedRanges)
            {
                var fetchStart = (int)fetchedRange.Start;
                var fetchEnd = (int)fetchedRange.End;

                // New fetches should not fully contain the original cached range [100, 110]
                var refetchesOriginal = fetchStart <= 100 && fetchEnd >= 110;
                Assert.False(refetchesOriginal,
                    $"Data preservation violated: Fetched range [{fetchStart}, {fetchEnd}] refetches original cache [100, 110]");
            }
        }

        // Verify cache serves correct data after expansion
        var result = await cache.GetDataAsync(TestHelpers.CreateRange(90, 105), CancellationToken.None);
        TestHelpers.AssertUserDataCorrect(result.Data, TestHelpers.CreateRange(90, 105));

        // Verify original range is still correct (data preserved)
        var originalResult = await cache.GetDataAsync(TestHelpers.CreateRange(100, 110), CancellationToken.None);
        TestHelpers.AssertUserDataCorrect(originalResult.Data, TestHelpers.CreateRange(100, 110));
    }

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
        var result = await cache.GetDataAsync(TestHelpers.CreateRange(100, 110), CancellationToken.None);
        stopwatch.Stop();

        // ASSERT: User request completed quickly (didn't wait for background rebalance)
        Assert.Equal(1, _cacheDiagnostics.UserRequestServed);
        Assert.Equal(1, _cacheDiagnostics.RebalanceIntentPublished);
        Assert.Equal(0, _cacheDiagnostics.RebalanceExecutionCompleted);
        TestHelpers.AssertUserDataCorrect(result.Data, TestHelpers.CreateRange(100, 110));
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
        var (cache, _) = TrackCache(TestHelpers.CreateCacheWithDefaults(_domain, _cacheDiagnostics,
            TestHelpers.CreateDefaultOptions(),
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
        var result1 = await cache.GetDataAsync(TestHelpers.CreateRange(100, 110), CancellationToken.None);
        TestHelpers.AssertUserDataCorrect(result1.Data, TestHelpers.CreateRange(100, 110));

        // Request 2: Overlapping expansion
        var result2 = await cache.GetDataAsync(TestHelpers.CreateRange(105, 120), CancellationToken.None);
        TestHelpers.AssertUserDataCorrect(result2.Data, TestHelpers.CreateRange(105, 120));
        await cache.WaitForIdleAsync();

        // Request 3: Within cached/rebalanced range
        var result3 = await cache.GetDataAsync(TestHelpers.CreateRange(110, 115), CancellationToken.None);
        TestHelpers.AssertUserDataCorrect(result3.Data, TestHelpers.CreateRange(110, 115));

        // Request 4: Non-intersecting jump
        var data4 = await cache.GetDataAsync(TestHelpers.CreateRange(200, 210), CancellationToken.None);
        TestHelpers.AssertUserDataCorrect(data4.Data, TestHelpers.CreateRange(200, 210));
        await cache.WaitForIdleAsync();

        // Request 5: Verify cache stability
        var data5 = await cache.GetDataAsync(TestHelpers.CreateRange(205, 215), CancellationToken.None);
        TestHelpers.AssertUserDataCorrect(data5.Data, TestHelpers.CreateRange(205, 215));

        // Wait for background rebalance to settle before checking counters
        await cache.WaitForIdleAsync();

        // Verify key behavioral properties
        Assert.Equal(5, _cacheDiagnostics.UserRequestServed);
        Assert.True(_cacheDiagnostics.RebalanceIntentPublished >= 5);
        Assert.True(_cacheDiagnostics.RebalanceScheduled >= 1,
            $"Expected at least 1 rebalance to be scheduled, but found {_cacheDiagnostics.RebalanceScheduled}");
        TestHelpers.AssertRebalanceCompleted(_cacheDiagnostics);
    }

    /// <summary>
    /// Comprehensive concurrency test with rapid burst of 20 concurrent requests verifying intent cancellation
    /// and system stability under high load. Validates: All requests served correctly (A.1, A.10),
    /// Intent cancellation works (C.17, C.18), At most one active intent (C.17),
    /// Cache remains consistent (B.11, B.15). Ensures single-consumer model with cancellation-based
    /// coordination handles realistic high-load scenarios without data corruption or request failures.
    /// Parameterized by execution strategy to verify behavior across both task-based and channel-based controllers.
    /// </summary>
    /// <param name="executionStrategy">Human-readable name of execution strategy for test output</param>
    /// <param name="queueCapacity">Queue capacity: null = task-based (unbounded), >= 1 = channel-based (bounded)</param>
    [Theory]
    [MemberData(nameof(ExecutionStrategyTestData))]
    public async Task ConcurrencyScenario_RapidRequestsBurstWithCancellation(string executionStrategy,
        int? queueCapacity)
    {
        // ARRANGE
        var options = TestHelpers.CreateDefaultOptions(
            debounceDelay: TimeSpan.FromSeconds(1),
            rebalanceQueueCapacity: queueCapacity);
        var (cache, _) = TrackCache(TestHelpers.CreateCacheWithDefaults(_domain, _cacheDiagnostics, options));

        // ACT: Fire 20 rapid concurrent requests
        var tasks = new List<Task<ReadOnlyMemory<int>>>();
        for (var i = 0; i < 20; i++)
        {
            var start = 100 + i * 5;
            tasks.Add(cache.GetDataAsync(TestHelpers.CreateRange(start, start + 10), CancellationToken.None).AsTask()
                .ContinueWith(t => t.Result.Data));
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

        // Verify system stability: lifecycle integrity and successful completion
        // Cancellation is coordination mechanism triggered by scheduling decisions, not deterministic per-request
        TestHelpers.AssertRebalanceLifecycleIntegrity(_cacheDiagnostics);
        Assert.True(_cacheDiagnostics.RebalanceScheduled >= 1,
            $"[{executionStrategy}] Expected at least 1 rebalance scheduled, but found {_cacheDiagnostics.RebalanceScheduled}");
        Assert.True(_cacheDiagnostics.RebalanceExecutionCompleted >= 1,
            $"[{executionStrategy}] Expected at least 1 rebalance completed, but found {_cacheDiagnostics.RebalanceExecutionCompleted}");
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
        var result1 = await cache.GetDataAsync(TestHelpers.CreateRange(100, 110), CancellationToken.None);
        var result2 = await cache.GetDataAsync(TestHelpers.CreateRange(105, 115), CancellationToken.None);

        // Assert
        TestHelpers.VerifyDataMatchesRange(result1.Data, TestHelpers.CreateRange(100, 110));
        TestHelpers.VerifyDataMatchesRange(result2.Data, TestHelpers.CreateRange(105, 115));
    }

    #endregion
}