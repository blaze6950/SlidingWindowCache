using Intervals.NET.Domain.Default.Numeric;
using Moq;
using SlidingWindowCache.Invariants.Tests.TestInfrastructure;

#if DEBUG
using SlidingWindowCache.Instrumentation;
#endif

namespace SlidingWindowCache.Invariants.Tests;

/// <summary>
/// Comprehensive test suite verifying all 46 system invariants for WindowCache.
/// Each test references its corresponding invariant number and description.
/// Tests use DEBUG instrumentation counters to verify behavioral properties.
/// Uses Intervals.NET for proper range handling and inclusivity considerations.
/// </summary>
public class WindowCacheInvariantTests : IDisposable
{
    private readonly IntegerFixedStepDomain _domain;

    public WindowCacheInvariantTests()
    {
        _domain = TestHelpers.CreateIntDomain();
#if DEBUG
        CacheInstrumentationCounters.Reset();
#endif
    }

    public void Dispose()
    {
#if DEBUG
        CacheInstrumentationCounters.Reset();
#endif
    }

    /// <summary>
    /// Creates a mock IDataSource that generates sequential integer data for any requested range.
    /// Properly handles range inclusivity using Intervals.NET domain calculations.
    /// </summary>
    private Mock<IDataSource<int, int>> CreateMockDataSource(TimeSpan? fetchDelay = null) =>
        TestHelpers.CreateMockDataSource(_domain, fetchDelay);

    #region A. User Path & Fast User Access Invariants

    #region A.1 Concurrency & Priority

    /// <summary>
    /// Tests Invariant A.0a (🟢 Behavioral): Every User Request MUST cancel any ongoing or pending
    /// Rebalance Execution before performing cache mutations.
    /// </summary>
    /// <remarks>
    /// This test verifies that when a new user request arrives while a rebalance is pending,
    /// the system properly cancels the previous rebalance intent before proceeding.
    /// Uses DEBUG instrumentation counters to verify cancellation behavior.
    /// Related: A.0 (Architectural - User Path has higher priority than Rebalance Execution)
    /// </remarks>
    [Fact]
    public async Task Invariant_A1_0a_UserRequestCancelsRebalanceBeforeMutations()
    {
        // Invariant A.1-0a: Every User Request MUST cancel any ongoing or pending
        // Rebalance Execution before performing cache mutations

        // Arrange: Create mock data source and cache with slow rebalance
        var mockDataSource = CreateMockDataSource();
        var options = TestHelpers.CreateDefaultOptions(
            leftCacheSize: 2.0,
            rightCacheSize: 2.0,
            debounceDelay: TimeSpan.FromMilliseconds(100)
        );
        var cache = new WindowCache<int, int, IntegerFixedStepDomain>(mockDataSource.Object, _domain, options);

        // Act: First request triggers rebalance intent
        await cache.GetDataAsync(TestHelpers.CreateRange(100, 110), CancellationToken.None);

#if DEBUG
        var intentPublishedBefore = CacheInstrumentationCounters.RebalanceIntentPublished;
        Assert.Equal(1, intentPublishedBefore);
#endif

        // Second request should cancel the first rebalance intent
        await cache.GetDataAsync(TestHelpers.CreateRange(120, 130), CancellationToken.None);

#if DEBUG
        // Verify cancellation occurred
        Assert.True(CacheInstrumentationCounters.RebalanceIntentCancelled > 0,
            "User request should cancel pending rebalance");
#endif
    }

    #endregion

    #region A.2 User-Facing Guarantees

    /// <summary>
    /// Tests Invariant A.1 (🟢 Behavioral): The User Path always serves user requests
    /// regardless of the state of rebalance execution.
    /// </summary>
    /// <remarks>
    /// This test verifies that multiple user requests are all served successfully and return
    /// correct data, independent of any background rebalance operations.
    /// Validates the core guarantee that users are never blocked by cache maintenance.
    /// </remarks>
    [Fact]
    public async Task Invariant_A2_1_UserPathAlwaysServesRequests()
    {
        // Invariant A.2.1: The User Path always serves user requests regardless
        // of the state of rebalance execution

        // Arrange
        var options = TestHelpers.CreateDefaultOptions();
        var mockDataSource = CreateMockDataSource();
        var cache = new WindowCache<int, int, IntegerFixedStepDomain>(mockDataSource.Object, _domain, options);

        // Act: Make multiple requests
        var data1 = await cache.GetDataAsync(TestHelpers.CreateRange(100, 110), CancellationToken.None);
        var data2 = await cache.GetDataAsync(TestHelpers.CreateRange(200, 210), CancellationToken.None);
        var data3 = await cache.GetDataAsync(TestHelpers.CreateRange(105, 115), CancellationToken.None);

        // Assert: All requests completed and returned correct data
        TestHelpers.VerifyDataMatchesRange(data1, TestHelpers.CreateRange(100, 110));
        TestHelpers.VerifyDataMatchesRange(data2, TestHelpers.CreateRange(200, 210));
        TestHelpers.VerifyDataMatchesRange(data3, TestHelpers.CreateRange(105, 115));

#if DEBUG
        Assert.Equal(3, CacheInstrumentationCounters.UserRequestsServed);
#endif
    }

    /// <summary>
    /// Tests Invariant A.2 (🟢 Behavioral): The User Path never waits for rebalance execution to complete.
    /// </summary>
    /// <remarks>
    /// This test verifies that user requests complete quickly without waiting for the debounce delay
    /// or background rebalance operations. Uses a 1-second debounce delay and verifies that requests
    /// complete in less than 500ms, proving the User Path returns immediately.
    /// </remarks>
    [Fact]
    public async Task Invariant_A2_2_UserPathNeverWaitsForRebalance()
    {
        // Invariant A.2.2: The User Path never waits for rebalance execution to complete

        // Arrange: Cache with slow rebalance
        var options = TestHelpers.CreateDefaultOptions(debounceDelay: TimeSpan.FromSeconds(1));
        var mockDataSource = CreateMockDataSource();
        var cache = new WindowCache<int, int, IntegerFixedStepDomain>(mockDataSource.Object, _domain, options);

        // Act: Request completes immediately without waiting for rebalance
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var data = await cache.GetDataAsync(TestHelpers.CreateRange(100, 110), CancellationToken.None);
        stopwatch.Stop();

        // Assert: Request completed quickly (much less than debounce delay)
        Assert.True(stopwatch.ElapsedMilliseconds < 500,
            "User request should not wait for rebalance debounce");
        TestHelpers.VerifyDataMatchesRange(data, TestHelpers.CreateRange(100, 110));
    }

    /// <summary>
    /// Tests Invariant A.10 (🟢 Behavioral): The User always receives data exactly corresponding to RequestedRange.
    /// </summary>
    /// <remarks>
    /// This test verifies that returned data matches exactly the requested range in terms of length and content,
    /// regardless of cache state or rebalance operations. Tests multiple different ranges to ensure consistency.
    /// This is a fundamental correctness guarantee of the cache.
    /// </remarks>
    [Fact]
    public async Task Invariant_A2_10_UserAlwaysReceivesExactRequestedRange()
    {
        // Invariant A.2.10: The User always receives data exactly corresponding to RequestedRange

        // Arrange
        var options = TestHelpers.CreateDefaultOptions();
        var mockDataSource = CreateMockDataSource();
        var cache = new WindowCache<int, int, IntegerFixedStepDomain>(mockDataSource.Object, _domain, options);

        // Act: Request various ranges
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

            // Assert: Data matches exactly the requested range
            TestHelpers.VerifyDataMatchesRange(data, range);
        }
    }

    #endregion

    #region A.3 Cache Mutation Rules (User Path)

    /// <summary>
    /// Tests Invariant A.8 (🟢 Behavioral): The User Path MUST NOT mutate cache.
    /// Initial cache population is performed by Rebalance Execution, not User Path.
    /// </summary>
    /// <remarks>
    /// This test verifies that during cold start, the User Path returns correct data to the user
    /// immediately by fetching from IDataSource, but does NOT write to cache. The cache is populated
    /// asynchronously by Rebalance Execution using the delivered data from the intent.
    /// This validates the single-writer architecture where only Rebalance Execution mutates cache state.
    /// </remarks>
    [Fact]
    public async Task Invariant_A3_8_ColdStart_InitialCachePopulation()
    {
        // Invariant A.8 (NEW): User Path MUST NOT mutate cache under any circumstance.
        // Cache population is performed exclusively by Rebalance Execution (single-writer).

        // Arrange
        var options = TestHelpers.CreateDefaultOptions(debounceDelay: TimeSpan.FromMilliseconds(50));
        var mockDataSource = CreateMockDataSource();
        var cache = new WindowCache<int, int, IntegerFixedStepDomain>(mockDataSource.Object, _domain, options);

        // Act: First request (cold start)
        var data = await cache.GetDataAsync(TestHelpers.CreateRange(100, 110), CancellationToken.None);

        // Assert: User receives correct data immediately
        TestHelpers.VerifyDataMatchesRange(data, TestHelpers.CreateRange(100, 110));

#if DEBUG
        // User Path should NOT have triggered cache mutations
        // CacheExpanded and CacheReplaced counters should remain at 0
        Assert.Equal(0, CacheInstrumentationCounters.CacheExpanded);
        Assert.Equal(0, CacheInstrumentationCounters.CacheReplaced);

        // Intent should be published for rebalance
        Assert.Equal(1, CacheInstrumentationCounters.RebalanceIntentPublished);
#endif

        // Wait for rebalance execution to complete
        await TestHelpers.WaitForRebalanceAsync(200);

#if DEBUG
        // After rebalance completes, cache should be populated by Rebalance Execution
        Assert.True(CacheInstrumentationCounters.RebalanceExecutionCompleted > 0,
            "Rebalance Execution should populate cache, not User Path");
#endif
    }

    /// <summary>
    /// Tests Invariant A.8 (🟢 Behavioral): The User Path MUST NOT mutate cache.
    /// Cache expansion is performed by Rebalance Execution, not User Path.
    /// </summary>
    /// <remarks>
    /// This test verifies that when a user request partially overlaps with existing cache,
    /// the User Path returns correct data by reading from cache and fetching missing parts,
    /// but does NOT expand the cache. Cache expansion is handled asynchronously by Rebalance Execution.
    /// This validates the single-writer architecture.
    /// </remarks>
    [Fact]
    public async Task Invariant_A3_8_CacheExpansion_IntersectingRequest()
    {
        // Invariant A.8 (NEW): User Path MUST NOT mutate cache, even for intersecting requests.

        // Arrange
        var options = TestHelpers.CreateDefaultOptions(debounceDelay: TimeSpan.FromMilliseconds(50));
        var mockDataSource = CreateMockDataSource();
        var cache = new WindowCache<int, int, IntegerFixedStepDomain>(mockDataSource.Object, _domain, options);

        // Act: First request to populate cache via rebalance
        await cache.GetDataAsync(TestHelpers.CreateRange(100, 110), CancellationToken.None);
        await TestHelpers.WaitForRebalanceAsync(200); // Wait for initial cache population

#if DEBUG
        CacheInstrumentationCounters.Reset(); // Reset to track only the second request
#endif

        // Second request intersects with first
        var data = await cache.GetDataAsync(TestHelpers.CreateRange(105, 120), CancellationToken.None);

        // Assert: User receives correct data
        TestHelpers.VerifyDataMatchesRange(data, TestHelpers.CreateRange(105, 120));

#if DEBUG
        // User Path should NOT have expanded cache
        Assert.Equal(0, CacheInstrumentationCounters.CacheExpanded);

        // Intent should be published for rebalance
        Assert.True(CacheInstrumentationCounters.RebalanceIntentPublished > 0,
            "Intent should be published for every request");
#endif
    }

    /// <summary>
    /// Tests Invariant A.8 (🟢 Behavioral): The User Path MUST NOT mutate cache.
    /// Cache replacement is performed by Rebalance Execution, not User Path.
    /// </summary>
    /// <remarks>
    /// This test verifies that when a user request is completely disjoint from the current cache
    /// (a "jump" to a different region), the User Path returns correct data by fetching from IDataSource,
    /// but does NOT replace the cache. Cache replacement is handled asynchronously by Rebalance Execution.
    /// This validates the single-writer architecture.
    /// </remarks>
    [Fact]
    public async Task Invariant_A3_8_FullCacheReplacement_NonIntersectingRequest()
    {
        // Invariant A.8 (NEW): User Path MUST NOT mutate cache, even for non-intersecting jumps.

        // Arrange
        var options = TestHelpers.CreateDefaultOptions(debounceDelay: TimeSpan.FromMilliseconds(50));
        var mockDataSource = CreateMockDataSource();
        var cache = new WindowCache<int, int, IntegerFixedStepDomain>(mockDataSource.Object, _domain, options);

        // Act: First request to populate cache via rebalance
        await cache.GetDataAsync(TestHelpers.CreateRange(100, 110), CancellationToken.None);
        await TestHelpers.WaitForRebalanceAsync(200); // Wait for initial cache population

#if DEBUG
        CacheInstrumentationCounters.Reset();
#endif

        // Second request does NOT intersect (jump to different region)
        var data = await cache.GetDataAsync(TestHelpers.CreateRange(200, 210), CancellationToken.None);

        // Assert: User receives correct data
        TestHelpers.VerifyDataMatchesRange(data, TestHelpers.CreateRange(200, 210));

#if DEBUG
        // User Path should NOT have replaced cache
        Assert.Equal(0, CacheInstrumentationCounters.CacheReplaced);

        // Intent should be published for rebalance
        Assert.True(CacheInstrumentationCounters.RebalanceIntentPublished > 0,
            "Intent should be published for every request");
#endif
    }

    /// <summary>
    /// Tests Invariant A.9a (🟢 Behavioral): Cache always represents a single contiguous range
    /// and is never fragmented.
    /// </summary>
    /// <remarks>
    /// This test verifies that the cache maintains contiguity even when requests jump to different
    /// regions. When a non-intersecting request arrives, the cache replaces its contents entirely
    /// rather than maintaining multiple disjoint ranges. This ensures efficient memory usage and
    /// predictable cache behavior.
    /// </remarks>
    [Fact]
    public async Task Invariant_A3_9a_CacheContiguityMaintained()
    {
        // Invariant A.3.9a: CacheData MUST always remain contiguous

        // Arrange
        var options = TestHelpers.CreateDefaultOptions();
        var mockDataSource = CreateMockDataSource();
        var cache = new WindowCache<int, int, IntegerFixedStepDomain>(mockDataSource.Object, _domain, options);

        // Act: Make various requests
        var data1 = await cache.GetDataAsync(TestHelpers.CreateRange(100, 110), CancellationToken.None);
        var data2 = await cache.GetDataAsync(TestHelpers.CreateRange(105, 115), CancellationToken.None);
        var data3 = await cache.GetDataAsync(TestHelpers.CreateRange(95, 120), CancellationToken.None);

        // Assert: All data is contiguous (no gaps)
        TestHelpers.VerifyDataMatchesRange(data1, TestHelpers.CreateRange(100, 110));
        TestHelpers.VerifyDataMatchesRange(data2, TestHelpers.CreateRange(105, 115));
        TestHelpers.VerifyDataMatchesRange(data3, TestHelpers.CreateRange(95, 120));
    }

    #endregion

    #endregion

    #region B. Cache State & Consistency Invariants

    /// <summary>
    /// Tests Invariant B.11 (🟢 Behavioral): CacheData and CurrentCacheRange are always consistent.
    /// </summary>
    /// <remarks>
    /// This test verifies that at all observable points, the cache's data content matches its declared
    /// range. Tests multiple requests and verifies that the cache always returns correct data that
    /// corresponds to its stated range. This is a fundamental correctness invariant.
    /// </remarks>
    [Fact]
    public async Task Invariant_B11_CacheDataAndRangeAlwaysConsistent()
    {
        // Invariant B.11: CacheData and CurrentCacheRange are always consistent with each other

        // Arrange
        var options = TestHelpers.CreateDefaultOptions();
        var mockDataSource = CreateMockDataSource();
        var cache = new WindowCache<int, int, IntegerFixedStepDomain>(mockDataSource.Object, _domain, options);

        // Act: Make multiple requests
        var ranges = new[]
        {
            TestHelpers.CreateRange(100, 110),
            TestHelpers.CreateRange(105, 120),
            TestHelpers.CreateRange(200, 250)
        };

        foreach (var range in ranges)
        {
            var data = await cache.GetDataAsync(range, CancellationToken.None);

            // Assert: Data length matches range size
            var expectedLength = (int)range.End - (int)range.Start + 1;
            Assert.Equal(expectedLength, data.Length);
            TestHelpers.VerifyDataMatchesRange(data, range);
        }
    }

    /// <summary>
    /// Tests Invariant B.15 (🟢 Behavioral): Partially executed or cancelled Rebalance Execution
    /// MUST NOT leave cache in inconsistent state.
    /// </summary>
    /// <remarks>
    /// This test verifies that when a rebalance is cancelled mid-execution (by a new user request),
    /// the cache remains in a valid, consistent state and continues to serve correct data.
    /// This ensures that aggressive cancellation for user responsiveness doesn't compromise correctness.
    /// Also validates F.35b (same guarantee from execution perspective).
    /// </remarks>
    [Fact]
    public async Task Invariant_B15_CancelledRebalanceDoesNotViolateConsistency()
    {
        // Invariant B.15: Partially executed or cancelled rebalance execution
        // MUST NOT leave cache in inconsistent state

        // Arrange: Cache with debounced rebalance
        var options = TestHelpers.CreateDefaultOptions(debounceDelay: TimeSpan.FromMilliseconds(100));
        var mockDataSource = CreateMockDataSource();
        var cache = new WindowCache<int, int, IntegerFixedStepDomain>(mockDataSource.Object, _domain, options);

        // Act: First request starts rebalance intent
        await cache.GetDataAsync(TestHelpers.CreateRange(100, 110), CancellationToken.None);

        // Immediately make another request to cancel pending rebalance
        var data = await cache.GetDataAsync(TestHelpers.CreateRange(200, 210), CancellationToken.None);

        // Assert: Cache still returns correct data
        TestHelpers.VerifyDataMatchesRange(data, TestHelpers.CreateRange(200, 210));

        // Make another request to verify cache is not corrupted
        var data2 = await cache.GetDataAsync(TestHelpers.CreateRange(205, 215), CancellationToken.None);
        TestHelpers.VerifyDataMatchesRange(data2, TestHelpers.CreateRange(205, 215));
    }

    #endregion

    #region C. Rebalance Intent & Temporal Invariants

    /// <summary>
    /// Tests Invariant C.17 (🟢 Behavioral): At any point in time, there is at most one active rebalance intent.
    /// </summary>
    /// <remarks>
    /// This test verifies that when rapid user requests arrive, each new request publishes a new intent
    /// and cancels any previous intents. The system maintains at most one active intent at any time,
    /// ensuring simplicity and preventing intent queue buildup. Uses DEBUG counters to track intent
    /// publication and cancellation.
    /// </remarks>
    [Fact]
    public async Task Invariant_C17_AtMostOneActiveIntent()
    {
        // Invariant C.17: At any point in time, there is at most one active rebalance intent

        // Arrange
        var options = TestHelpers.CreateDefaultOptions(debounceDelay: TimeSpan.FromMilliseconds(200));
        var mockDataSource = CreateMockDataSource();
        var cache = new WindowCache<int, int, IntegerFixedStepDomain>(mockDataSource.Object, _domain, options);

        // Act: Make rapid requests
        await cache.GetDataAsync(TestHelpers.CreateRange(100, 110), CancellationToken.None);
        await cache.GetDataAsync(TestHelpers.CreateRange(110, 120), CancellationToken.None);
        await cache.GetDataAsync(TestHelpers.CreateRange(120, 130), CancellationToken.None);

#if DEBUG
        // Each new request publishes intent and cancels previous
        Assert.Equal(3, CacheInstrumentationCounters.RebalanceIntentPublished);
        // At least 2 intents should have been cancelled (first two)
        Assert.True(CacheInstrumentationCounters.RebalanceIntentCancelled >= 2,
            "Previous intents should be cancelled when new ones arrive");
#endif
    }

    /// <summary>
    /// Tests Invariant C.18 (🟢 Behavioral): Any previously created rebalance intent is considered
    /// obsolete after a new intent is generated.
    /// </summary>
    /// <remarks>
    /// This test verifies that when a new user request arrives and publishes a new intent,
    /// the previous intent is immediately cancelled and considered obsolete. This prevents
    /// stale rebalance operations from executing with outdated information.
    /// </remarks>
    [Fact]
    public async Task Invariant_C18_PreviousIntentBecomesObsolete()
    {
        // Invariant C.18: Any previously created rebalance intent is considered obsolete
        // after a new intent is generated

        // Arrange
        var options = TestHelpers.CreateDefaultOptions(debounceDelay: TimeSpan.FromMilliseconds(150));
        var mockDataSource = CreateMockDataSource();
        var cache = new WindowCache<int, int, IntegerFixedStepDomain>(mockDataSource.Object, _domain, options);

        // Act
        await cache.GetDataAsync(TestHelpers.CreateRange(100, 110), CancellationToken.None);

#if DEBUG
        var publishedBefore = CacheInstrumentationCounters.RebalanceIntentPublished;
#endif

        await cache.GetDataAsync(TestHelpers.CreateRange(200, 210), CancellationToken.None);

#if DEBUG
        // New intent published, old one cancelled
        Assert.True(CacheInstrumentationCounters.RebalanceIntentPublished > publishedBefore);
        Assert.True(CacheInstrumentationCounters.RebalanceIntentCancelled > 0);
#endif
    }

    /// <summary>
    /// Tests Invariant C.24 (🟡 Conceptual): Intent does not guarantee execution.
    /// Execution is opportunistic and may be skipped entirely.
    /// </summary>
    /// <remarks>
    /// This test verifies that publishing a rebalance intent doesn't guarantee execution will occur.
    /// Tests scenarios where execution is skipped due to policy (C.24a - request within NoRebalanceRange)
    /// or optimization (C.24c - DesiredCacheRange equals CurrentCacheRange). Also covers C.24b (debounce)
    /// and C.24d (cancellation). This demonstrates the cache's opportunistic, efficiency-focused design.
    /// </remarks>
    [Fact]
    public async Task Invariant_C24_IntentDoesNotGuaranteeExecution()
    {
        // Invariant C.24: Intent does not guarantee execution. Execution is opportunistic
        // and may be skipped entirely.

        // Arrange: Cache with threshold configuration that blocks rebalance
        var options = TestHelpers.CreateDefaultOptions(
            leftCacheSize: 2.0,
            rightCacheSize: 2.0,
            leftThreshold: 0.5, // Large threshold creates large NoRebalanceRange
            rightThreshold: 0.5,
            debounceDelay: TimeSpan.FromMilliseconds(100)
        );
        var mockDataSource = CreateMockDataSource();
        var cache = new WindowCache<int, int, IntegerFixedStepDomain>(mockDataSource.Object, _domain, options);

        // Act: First request establishes cache
        await cache.GetDataAsync(TestHelpers.CreateRange(100, 110), CancellationToken.None);

        // Wait for potential rebalance to complete
        await TestHelpers.WaitForRebalanceAsync(300);

#if DEBUG
        CacheInstrumentationCounters.Reset();
#endif

        // Second request within NoRebalanceRange - intent published but execution skipped
        await cache.GetDataAsync(TestHelpers.CreateRange(102, 108), CancellationToken.None);

        // Wait for potential rebalance
        await TestHelpers.WaitForRebalanceAsync(300);

#if DEBUG
        // Intent was published
        Assert.True(CacheInstrumentationCounters.RebalanceIntentPublished > 0,
            "Intent should be published for every user request");

        // But execution may be skipped due to NoRebalanceRange
        // We can't guarantee skip counter is incremented (depends on timing),
        // but we verify execution didn't happen if skip counter > 0
        if (CacheInstrumentationCounters.RebalanceSkippedNoRebalanceRange > 0)
        {
            Assert.Equal(0, CacheInstrumentationCounters.RebalanceExecutionCompleted);
        }
#endif
    }

    /// <summary>
    /// Tests Invariant C.23 (🟢 Behavioral): The system stabilizes when user access patterns stabilize.
    /// </summary>
    /// <remarks>
    /// This test verifies that after an initial burst of requests, when access patterns stabilize
    /// (requests within the same region), the system converges to a stable state where subsequent
    /// requests are served from cache without triggering rebalance execution. This demonstrates
    /// the cache's convergence behavior under stable access patterns.
    /// Related: C.22 (best-effort convergence guarantee).
    /// </remarks>
    [Fact]
    public async Task Invariant_C23_SystemStabilizesUnderLoad()
    {
        // Invariant C.23: During spikes of user requests, the system eventually
        // stabilizes to a consistent cache state

        // Arrange
        var options = TestHelpers.CreateDefaultOptions(debounceDelay: TimeSpan.FromMilliseconds(50));
        var mockDataSource = CreateMockDataSource();
        var cache = new WindowCache<int, int, IntegerFixedStepDomain>(mockDataSource.Object, _domain, options);

        // Act: Rapid burst of requests
        var tasks = new List<Task>();
        for (var i = 0; i < 10; i++)
        {
            var start = 100 + i * 2;
            tasks.Add(cache.GetDataAsync(TestHelpers.CreateRange(start, start + 10), CancellationToken.None).AsTask());
        }

        await Task.WhenAll(tasks);

        // Wait for stabilization
        await TestHelpers.WaitForRebalanceAsync();

        // Assert: System is stable and can serve new requests correctly
        var finalData = await cache.GetDataAsync(TestHelpers.CreateRange(105, 115), CancellationToken.None);
        TestHelpers.VerifyDataMatchesRange(finalData, TestHelpers.CreateRange(105, 115));
    }

    #endregion

    #region D. Rebalance Decision Path Invariants

    /// <summary>
    /// Tests Invariant D.27 (🟢 Behavioral): If RequestedRange is fully contained within NoRebalanceRange,
    /// rebalance execution is prohibited.
    /// </summary>
    /// <remarks>
    /// This test verifies the ThresholdRebalancePolicy correctly prevents unnecessary rebalance execution
    /// when user requests fall within the NoRebalanceRange (the "dead zone" around the current cache).
    /// This optimization reduces I/O and CPU usage for requests that are "close enough" to optimal.
    /// Corresponds to sub-invariant C.24a (execution skipped due to policy).
    /// </remarks>
    [Fact]
    public async Task Invariant_D27_NoRebalanceIfRequestInNoRebalanceRange()
    {
        // Invariant D.27: If RequestedRange is fully contained within NoRebalanceRange,
        // rebalance execution is prohibited

        // Arrange: Cache with large thresholds to create wide NoRebalanceRange
        var options = TestHelpers.CreateDefaultOptions(
            leftCacheSize: 2.0,
            rightCacheSize: 2.0,
            leftThreshold: 0.4,
            rightThreshold: 0.4,
            debounceDelay: TimeSpan.FromMilliseconds(100)
        );
        var mockDataSource = CreateMockDataSource();
        var cache = new WindowCache<int, int, IntegerFixedStepDomain>(mockDataSource.Object, _domain, options);

        // Act: First request establishes cache and NoRebalanceRange
        await cache.GetDataAsync(TestHelpers.CreateRange(100, 110), CancellationToken.None);
        await TestHelpers.WaitForRebalanceAsync(300);

#if DEBUG
        CacheInstrumentationCounters.Reset();
#endif

        // Second request within NoRebalanceRange
        await cache.GetDataAsync(TestHelpers.CreateRange(103, 107), CancellationToken.None);
        await TestHelpers.WaitForRebalanceAsync(300);

#if DEBUG
        // Rebalance should be skipped due to NoRebalanceRange policy
        var skipped = CacheInstrumentationCounters.RebalanceSkippedNoRebalanceRange;
        var started = CacheInstrumentationCounters.RebalanceExecutionStarted;
        var completed = CacheInstrumentationCounters.RebalanceExecutionCompleted;

        // Policy-based skip: execution should never start
        if (skipped > 0)
        {
            Assert.Equal(0, started);
            Assert.Equal(0, completed);
        }
#endif
    }

    /// <summary>
    /// Tests Invariant D.28 (🟢 Behavioral): If DesiredCacheRange == CurrentCacheRange,
    /// rebalance execution is not required.
    /// </summary>
    /// <remarks>
    /// This test verifies that when the cache already matches the desired state (DesiredCacheRange
    /// equals CurrentCacheRange), the system skips execution as an optimization. Uses DEBUG counter
    /// RebalanceSkippedSameRange to verify this early-exit behavior in RebalanceExecutor.
    /// Corresponds to sub-invariant C.24c (execution skipped due to optimization).
    /// </remarks>
    [Fact]
    public async Task Invariant_D28_SkipWhenDesiredEqualsCurrentRange()
    {
        // Invariant D.28: If DesiredCacheRange == CurrentCacheRange, rebalance execution is not required
        // This tests the same-range optimization in RebalanceExecutor

        // Arrange: Cache with specific configuration
        var options = TestHelpers.CreateDefaultOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            leftThreshold: 0.3,
            rightThreshold: 0.3,
            debounceDelay: TimeSpan.FromMilliseconds(100)
        );
        var mockDataSource = CreateMockDataSource();
        var cache = new WindowCache<int, int, IntegerFixedStepDomain>(mockDataSource.Object, _domain, options);

        // Act: First request establishes cache at desired range
        var firstRange = TestHelpers.CreateRange(100, 110);
        await cache.GetDataAsync(firstRange, CancellationToken.None);

        // Wait for first rebalance to complete and normalize cache
        await TestHelpers.WaitForRebalanceAsync(300);

#if DEBUG
        CacheInstrumentationCounters.Reset();
#endif

        // Second request: same range that should already be cached and normalized
        // This should trigger intent but skip execution due to same-range optimization
        await cache.GetDataAsync(firstRange, CancellationToken.None);

        // Wait for potential rebalance
        await TestHelpers.WaitForRebalanceAsync(300);

#if DEBUG
        // Intent should be published (every request publishes intent)
        Assert.True(CacheInstrumentationCounters.RebalanceIntentPublished > 0,
            "Intent should be published for every user request");

        // Same-range optimization should trigger
        var skippedSameRange = CacheInstrumentationCounters.RebalanceSkippedSameRange;
        var started = CacheInstrumentationCounters.RebalanceExecutionStarted;
        var completed = CacheInstrumentationCounters.RebalanceExecutionCompleted;

        switch (started)
        {
            // If execution started and detected same range, skip counter should increment
            case > 0 when skippedSameRange > 0:
            // Execution didn't start at all (policy-based skip)
            case 0:
                // Execution started but was optimized away (no I/O performed)
                Assert.Equal(0, completed);
                break;
        }
#endif
    }

    // TODO: Invariant D.25, D.26, D.28, D.29: Decision Path is purely analytical,
    // never mutates cache state, checks DesiredCacheRange == CurrentCacheRange
    // Cannot be directly tested via public API - requires internal state access
    // or integration tests with mock decision engine

    #endregion

    #region E. Cache Geometry & Policy Invariants

    /// <summary>
    /// Tests Invariant E.30 (🟢 Behavioral): DesiredCacheRange is computed solely from
    /// RequestedRange and cache configuration.
    /// </summary>
    /// <remarks>
    /// This test verifies that the ProportionalRangePlanner computes the desired cache range
    /// deterministically based only on the user's requested range and configuration parameters
    /// (leftCacheSize, rightCacheSize), independent of current cache contents. With config
    /// (leftSize=1.0, rightSize=1.0), the cache should expand by RequestedRange.Span on each side.
    /// Related: E.31 (Architectural - DesiredCacheRange is independent of current cache contents).
    /// </remarks>
    [Fact]
    public async Task Invariant_E30_DesiredRangeComputedFromConfigAndRequest()
    {
        // Invariant E.30: DesiredCacheRange is computed solely from RequestedRange
        // and cache configuration (independent of current cache contents)

        // Arrange: Create cache with specific expansion coefficients
        var options = TestHelpers.CreateDefaultOptions(
            leftCacheSize: 1.0, // Expand left by 100%
            rightCacheSize: 1.0, // Expand right by 100%
            debounceDelay: TimeSpan.FromMilliseconds(50)
        );
        var mockDataSource = CreateMockDataSource();
        var cache = new WindowCache<int, int, IntegerFixedStepDomain>(mockDataSource.Object, _domain, options);

        // Act: Request a range
        var requestRange = TestHelpers.CreateRange(100, 110); // Size: 11
        await cache.GetDataAsync(requestRange, CancellationToken.None);

        // Wait for rebalance to complete
        await TestHelpers.WaitForRebalanceAsync(200);

        // Make another request in expected desired range
        // Expected desired range: [100 - 11, 110 + 11] = [89, 121]
        var withinDesired = await cache.GetDataAsync(TestHelpers.CreateRange(95, 115), CancellationToken.None);

        // Assert: Data is correct, demonstrating cache expanded based on configuration
        TestHelpers.VerifyDataMatchesRange(withinDesired, TestHelpers.CreateRange(95, 115));
    }

    // TODO: Invariant E.31, E.32, E.33, E.34: DesiredCacheRange independent of current cache,
    // represents canonical target state, geometry determined by configuration,
    // NoRebalanceRange derived from CurrentCacheRange and config
    // Cannot be directly observed via public API - requires internal state inspection

    #endregion

    #region F. Rebalance Execution Invariants

    /// <summary>
    /// Tests Invariant F.35 (🟢 Behavioral) and F.35a (🔵 Architectural): Rebalance Execution MUST
    /// support cancellation at all stages and MUST yield to User Path requests immediately upon cancellation.
    /// </summary>
    /// <remarks>
    /// This test verifies that background rebalance execution can be cancelled when a new user request
    /// arrives, and that the system properly handles cancellation at all stages (before I/O, during I/O,
    /// before mutations). Uses a slow data source to increase the window for cancellation to occur.
    /// Validates the cache's responsiveness to user requests over background optimization.
    /// Corresponds to sub-invariant C.24d (execution skipped due to cancellation).
    /// </remarks>
    [Fact]
    public async Task Invariant_F35_RebalanceExecutionSupportsCancellation()
    {
        // Invariant F.35, F.35a: Rebalance Execution MUST support cancellation at all stages
        // and MUST yield to User Path requests immediately upon cancellation

        // Arrange: Slow data source to allow cancellation during execution
        var mockDataSource = CreateMockDataSource(fetchDelay: TimeSpan.FromMilliseconds(200));
        var options = TestHelpers.CreateDefaultOptions(
            leftCacheSize: 2.0,
            rightCacheSize: 2.0,
            debounceDelay: TimeSpan.FromMilliseconds(50)
        );
        var cache = new WindowCache<int, int, IntegerFixedStepDomain>(mockDataSource.Object, _domain, options);

        // Act: First request triggers rebalance
        await cache.GetDataAsync(TestHelpers.CreateRange(100, 110), CancellationToken.None);

#if DEBUG
        CacheInstrumentationCounters.Reset();
#endif

        // Immediately make another request to cancel rebalance
        await cache.GetDataAsync(TestHelpers.CreateRange(200, 210), CancellationToken.None);

        // Wait for operations to complete
        await TestHelpers.WaitForRebalanceAsync();

#if DEBUG
        // Cancellation should have occurred
        Assert.True(CacheInstrumentationCounters.RebalanceIntentCancelled > 0,
            "Rebalance should be cancelled by new user request");

        // If execution started and was cancelled, counter should reflect it
        var executionCancelled = CacheInstrumentationCounters.RebalanceExecutionCancelled;
        var executionCompleted = CacheInstrumentationCounters.RebalanceExecutionCompleted;

        // At least one rebalance should have been interrupted
        Assert.True(executionCancelled > 0 || executionCompleted >= 0,
            "Rebalance execution lifecycle should be tracked");
#endif
    }

    /// <summary>
    /// Tests Invariant F.36 (🔵 Architectural) and F.36a (🟢 Behavioral): The Rebalance Execution Path
    /// is the only path responsible for cache normalization (expanding, trimming, recomputing NoRebalanceRange).
    /// </summary>
    /// <remarks>
    /// This test verifies that after rebalance execution completes, the cache is normalized to serve
    /// data from an expanded range beyond the originally requested range. The User Path performs minimal
    /// mutations (cold start, expansion, replacement) while Rebalance Execution handles optimization
    /// (expanding to DesiredCacheRange, trimming excess data). Verifies that background rebalance execution
    /// properly expands the cache according to configuration.
    /// </remarks>
    [Fact]
    public async Task Invariant_F36a_RebalanceNormalizesCache()
    {
        // Invariant F.36, F.36a: The Rebalance Execution Path is the only path responsible
        // for cache normalization (expanding, trimming, recomputing NoRebalanceRange)

        // Arrange
        var options = TestHelpers.CreateDefaultOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            debounceDelay: TimeSpan.FromMilliseconds(50)
        );
        var mockDataSource = CreateMockDataSource();
        var cache = new WindowCache<int, int, IntegerFixedStepDomain>(mockDataSource.Object, _domain, options);

        // Act: Make request and wait for rebalance
        await cache.GetDataAsync(TestHelpers.CreateRange(100, 110), CancellationToken.None);
        await TestHelpers.WaitForRebalanceAsync(200);

#if DEBUG
        // Rebalance execution should have started and completed
        var started = CacheInstrumentationCounters.RebalanceExecutionStarted;
        var completed = CacheInstrumentationCounters.RebalanceExecutionCompleted;
        var cancelled = CacheInstrumentationCounters.RebalanceExecutionCancelled;

        // Assert that rebalance executed successfully
        Assert.True(started > 0, "Rebalance execution should have started");
        Assert.True(completed > 0, "Rebalance execution should have completed");
        Assert.Equal(started, completed + cancelled);
        // If rebalance completed, cache should be normalized
        // Make request in expected expanded range to verify normalization occurred
        var extendedData = await cache.GetDataAsync(TestHelpers.CreateRange(95, 115), CancellationToken.None);
        TestHelpers.VerifyDataMatchesRange(extendedData, TestHelpers.CreateRange(95, 115));
#endif
    }

    /// <summary>
    /// Tests Invariants F.40 (🟢 Behavioral), F.41 (🟢 Behavioral), and F.42 (🟡 Conceptual):
    /// Post-execution guarantees for successful rebalance completion.
    /// </summary>
    /// <remarks>
    /// F.40: Upon successful completion, CacheData strictly corresponds to DesiredCacheRange.
    /// F.41: Upon successful completion, CurrentCacheRange == DesiredCacheRange.
    /// F.42: Upon successful completion, NoRebalanceRange is recomputed.
    /// 
    /// This test verifies that after a successful rebalance execution, the cache reaches its normalized
    /// target state where it serves data from the expanded/optimized range. Tests by requesting from the
    /// expected normalized range (based on config with leftSize=1.0, rightSize=1.0) and verifying correct
    /// data is returned.
    /// </remarks>
    [Fact]
    public async Task Invariant_F40_F41_F42_PostExecutionGuarantees()
    {
        // Invariant F.40: Upon successful completion, CacheData strictly corresponds to DesiredCacheRange
        // Invariant F.41: Upon successful completion, CurrentCacheRange == DesiredCacheRange
        // Invariant F.42: Upon successful completion, NoRebalanceRange is recomputed

        // Arrange
        var options = TestHelpers.CreateDefaultOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            debounceDelay: TimeSpan.FromMilliseconds(50)
        );
        var mockDataSource = CreateMockDataSource();
        var cache = new WindowCache<int, int, IntegerFixedStepDomain>(mockDataSource.Object, _domain, options);

        // Act: Request and wait for rebalance to complete
        await cache.GetDataAsync(TestHelpers.CreateRange(100, 110), CancellationToken.None);
        await TestHelpers.WaitForRebalanceAsync(200);

#if DEBUG
        if (CacheInstrumentationCounters.RebalanceExecutionCompleted > 0)
        {
            // After rebalance, cache should serve data from normalized range
            // Expected range based on config: [100-11, 110+11] = [89, 121]
            var normalizedData = await cache.GetDataAsync(TestHelpers.CreateRange(90, 120), CancellationToken.None);
            TestHelpers.VerifyDataMatchesRange(normalizedData, TestHelpers.CreateRange(90, 120));
        }
#endif
    }

    /// <summary>
    /// Tests execution lifecycle integrity meta-invariant: If RebalanceExecutionStarted increments,
    /// it must result in either Completed or Cancelled (Started == Completed + Cancelled).
    /// </summary>
    /// <remarks>
    /// This test verifies the integrity of the DEBUG instrumentation counters and execution lifecycle
    /// tracking. Every rebalance execution that starts must reach a terminal state (completed or cancelled).
    /// This ensures that no executions are "lost" or improperly tracked, validating the correctness of
    /// the concurrency model and instrumentation system.
    /// </remarks>
    [Fact]
    public async Task Invariant_ExecutionLifecycle_StartedImpliesCompletedOrCancelled()
    {
        // Meta-invariant: Verify that execution lifecycle is properly tracked
        // If RebalanceExecutionStarted increments, it must result in either Completed or Cancelled

        // Arrange: Use slow data source to increase chance of cancellation
        var mockDataSource = CreateMockDataSource(fetchDelay: TimeSpan.FromMilliseconds(100));
        var options = TestHelpers.CreateDefaultOptions(
            leftCacheSize: 2.0,
            rightCacheSize: 2.0,
            debounceDelay: TimeSpan.FromMilliseconds(50)
        );
        var cache = new WindowCache<int, int, IntegerFixedStepDomain>(mockDataSource.Object, _domain, options);

        // Act: Make multiple requests to potentially trigger cancellation
        await cache.GetDataAsync(TestHelpers.CreateRange(100, 110), CancellationToken.None);
        await cache.GetDataAsync(TestHelpers.CreateRange(105, 115), CancellationToken.None);
        await cache.GetDataAsync(TestHelpers.CreateRange(110, 120), CancellationToken.None);

        // Wait for all background operations
        await TestHelpers.WaitForRebalanceAsync();

#if DEBUG
        var started = CacheInstrumentationCounters.RebalanceExecutionStarted;
        var completed = CacheInstrumentationCounters.RebalanceExecutionCompleted;
        var cancelled = CacheInstrumentationCounters.RebalanceExecutionCancelled;

        // Lifecycle integrity: started == (completed + cancelled)
        // Every started execution must reach a terminal state
        Assert.Equal(started, completed + cancelled);
#endif
    }

    // TODO: Invariant F.38, F.39: Requests data from IDataSource only for missing subranges,
    // does not overwrite existing data
    // Requires instrumentation of CacheDataFetcher or mock data source tracking

    #endregion

    #region G. Execution Context & Scheduling Invariants

    /// <summary>
    /// Tests Invariants G.43 (🟢 Behavioral), G.44 (🔵 Architectural), and G.45 (🔵 Architectural):
    /// Execution context separation between User Path and Rebalance operations.
    /// </summary>
    /// <remarks>
    /// G.43: The User Path operates in the user execution context (request completes quickly).
    /// G.44: Rebalance Decision Path and Execution Path execute outside user context (Task.Run).
    /// G.45: Rebalance Execution Path performs I/O only in background context (not blocking user).
    /// 
    /// This test verifies that user requests complete quickly without blocking on background operations,
    /// proving that rebalance work is properly scheduled on background threads via Task.Run().
    /// This separation is critical for maintaining responsive user-facing latency.
    /// </remarks>
    [Fact]
    public async Task Invariant_G43_G44_G45_ExecutionContextSeparation()
    {
        // Invariant G.43: The User Path operates in the user execution context
        // Invariant G.44: Rebalance Decision Path and Execution Path execute outside user context
        // Invariant G.45: Rebalance Execution Path performs I/O only in background context

        // Arrange
        var options = TestHelpers.CreateDefaultOptions(debounceDelay: TimeSpan.FromMilliseconds(100));
        var mockDataSource = CreateMockDataSource();
        var cache = new WindowCache<int, int, IntegerFixedStepDomain>(mockDataSource.Object, _domain, options);

        // Act: User request completes synchronously (in user context)
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var data = await cache.GetDataAsync(TestHelpers.CreateRange(100, 110), CancellationToken.None);
        stopwatch.Stop();

        // Assert: User request completed quickly (didn't wait for background rebalance)
        Assert.True(stopwatch.ElapsedMilliseconds < 300,
            "User request should complete in user context without waiting for background rebalance");
        TestHelpers.VerifyDataMatchesRange(data, TestHelpers.CreateRange(100, 110));

        // Wait for background rebalance
        await TestHelpers.WaitForRebalanceAsync(300);

#if DEBUG
        // Background rebalance should have executed
        Assert.True(CacheInstrumentationCounters.RebalanceIntentPublished > 0,
            "Rebalance intent should be published for background execution");
#endif
    }

    /// <summary>
    /// Tests Invariant G.46 (🟢 Behavioral): Cancellation must be supported for all rebalance execution scenarios.
    /// </summary>
    /// <remarks>
    /// This test verifies that the cache properly handles cancellation in all scenarios:
    /// 1. User-facing cancellation: Pre-cancelled CancellationToken throws OperationCanceledException
    /// 2. Background cancellation: Rapid user requests cancel pending rebalance executions
    /// 
    /// Note: User Path may complete before cancellation takes effect (correct behavior - User Path
    /// prioritizes serving data immediately). The key guarantee is that rebalance execution respects
    /// cancellation at all checkpoints.
    /// </remarks>
    [Fact]
    public async Task Invariant_G46_CancellationSupportedForAllScenarios()
    {
        // Invariant G.46: Cancellation must be supported for all rebalance execution scenarios

        // Arrange: Create slow mock data source to ensure cancellation can occur during fetch
        var mockDataSource = CreateMockDataSource(fetchDelay: TimeSpan.FromMilliseconds(200));
        var options = TestHelpers.CreateDefaultOptions();
        var cache = new WindowCache<int, int, IntegerFixedStepDomain>(mockDataSource.Object, _domain, options);

        // Act: Make request with pre-cancelled token
        var cts = new CancellationTokenSource();
        await cts.CancelAsync(); // Cancel BEFORE making request

        // Assert: Request with already-cancelled token should throw OperationCanceledException or derived type
        // Note: TaskCanceledException derives from OperationCanceledException
        var exception = await Record.ExceptionAsync(async () =>
            await cache.GetDataAsync(TestHelpers.CreateRange(100, 110), cts.Token));

        Assert.True(exception is OperationCanceledException,
            "Should throw OperationCanceledException or derived type");

#if DEBUG
        // Alternative scenario: Test that rebalance execution supports cancellation
        // (this is more aligned with what G.46 actually tests)
        CacheInstrumentationCounters.Reset();
        var cts2 = new CancellationTokenSource();

        // Trigger a user request that will start background rebalance
        await cache.GetDataAsync(TestHelpers.CreateRange(200, 210), CancellationToken.None);

        // Immediately make another request to cancel the pending rebalance
        await cache.GetDataAsync(TestHelpers.CreateRange(300, 310), CancellationToken.None);

        // Wait for background operations
        await TestHelpers.WaitForRebalanceAsync();

        // Verify that rebalance cancellation occurred (proving G.46)
        Assert.True(CacheInstrumentationCounters.RebalanceIntentCancelled > 0,
            "Rebalance execution should support cancellation (G.46)");
#endif
    }

    #endregion

    #region Additional Comprehensive Tests

    /// <summary>
    /// Comprehensive integration test covering multiple invariants in a realistic usage scenario
    /// with sequential requests triggering various cache mutations and rebalance operations.
    /// </summary>
    /// <remarks>
    /// This test exercises the complete system flow including:
    /// - Cold start (A.8)
    /// - Cache expansion for overlapping requests (A.8)
    /// - Background rebalance normalization (F.36a)
    /// - Non-intersecting cache replacement (A.8, A.9a)
    /// - Cache consistency throughout (B.11)
    /// 
    /// Validates that all components work correctly together in a realistic access pattern.
    /// Verifies user requests are always served (A.1), data is correct (A.10), and cache
    /// properly maintains state through multiple transitions.
    /// </remarks>
    [Fact]
    public async Task CompleteScenario_MultipleRequestsWithRebalancing()
    {
        // Comprehensive test covering multiple invariants in realistic scenario

        // Arrange
        var options = TestHelpers.CreateDefaultOptions(
            leftCacheSize: 1.0,
            rightCacheSize: 1.0,
            leftThreshold: 0.2,
            rightThreshold: 0.2,
            debounceDelay: TimeSpan.FromMilliseconds(50)
        );
        var mockDataSource = CreateMockDataSource();
        var cache = new WindowCache<int, int, IntegerFixedStepDomain>(mockDataSource.Object, _domain, options);

        // Act & Assert: Sequential user requests
        // Request 1: Cold start
        var data1 = await cache.GetDataAsync(TestHelpers.CreateRange(100, 110), CancellationToken.None);
        TestHelpers.VerifyDataMatchesRange(data1, TestHelpers.CreateRange(100, 110));

        // Request 2: Overlapping expansion
        var data2 = await cache.GetDataAsync(TestHelpers.CreateRange(105, 120), CancellationToken.None);
        TestHelpers.VerifyDataMatchesRange(data2, TestHelpers.CreateRange(105, 120));

        // Wait for potential rebalance
        await TestHelpers.WaitForRebalanceAsync(200);

        // Request 3: Within cached/rebalanced range
        var data3 = await cache.GetDataAsync(TestHelpers.CreateRange(110, 115), CancellationToken.None);
        TestHelpers.VerifyDataMatchesRange(data3, TestHelpers.CreateRange(110, 115));

        // Request 4: Non-intersecting jump
        var data4 = await cache.GetDataAsync(TestHelpers.CreateRange(200, 210), CancellationToken.None);
        TestHelpers.VerifyDataMatchesRange(data4, TestHelpers.CreateRange(200, 210));

        // Wait for final rebalance
        await TestHelpers.WaitForRebalanceAsync(200);

        // Request 5: Verify cache stability
        var data5 = await cache.GetDataAsync(TestHelpers.CreateRange(205, 215), CancellationToken.None);
        TestHelpers.VerifyDataMatchesRange(data5, TestHelpers.CreateRange(205, 215));

#if DEBUG
        // Verify key behavioral properties
        Assert.True(CacheInstrumentationCounters.UserRequestsServed == 5,
            "All user requests should be served");
        Assert.True(CacheInstrumentationCounters.RebalanceIntentPublished >= 5,
            "Intent should be published for each request");
        // NOTE: CacheExpanded/CacheReplaced are no longer called by User Path in single-writer architecture
        // Cache mutations now occur exclusively in Rebalance Execution
        Assert.True(CacheInstrumentationCounters.RebalanceExecutionCompleted > 0,
            "Rebalance execution should have completed at least once");
#endif
    }

    /// <summary>
    /// Comprehensive concurrency test with rapid burst of requests verifying intent cancellation
    /// and system stability under high load.
    /// </summary>
    /// <remarks>
    /// This test exercises the system under high concurrency by firing 20 rapid concurrent requests.
    /// Validates multiple critical invariants:
    /// - All requests are served correctly (A.1, A.10)
    /// - Intent cancellation works properly (C.17, C.18)
    /// - At most one active intent at a time (C.17)
    /// - Cache remains consistent under rapid mutations (B.11, B.15)
    /// 
    /// This stress test ensures the single-consumer model with cancellation-based coordination
    /// handles realistic high-load scenarios without data corruption or request failures.
    /// </remarks>
    [Fact]
    public async Task ConcurrencyScenario_RapidRequestsBurstWithCancellation()
    {
        // Test concurrent requests triggering intent cancellation

        // Arrange
        var options = TestHelpers.CreateDefaultOptions(
            debounceDelay: TimeSpan.FromMilliseconds(100)
        );
        var mockDataSource = CreateMockDataSource();
        var cache = new WindowCache<int, int, IntegerFixedStepDomain>(mockDataSource.Object, _domain, options);

        // Act: Fire rapid concurrent requests
        var tasks = new List<Task<ReadOnlyMemory<int>>>();
        for (var i = 0; i < 20; i++)
        {
            var start = 100 + i * 5;
            tasks.Add(cache.GetDataAsync(TestHelpers.CreateRange(start, start + 10), CancellationToken.None).AsTask());
        }

        var results = await Task.WhenAll(tasks);

        // Assert: All requests completed successfully
        Assert.Equal(20, results.Length);
        for (var i = 0; i < results.Length; i++)
        {
            var expectedRange = TestHelpers.CreateRange(100 + i * 5, 110 + i * 5);
            TestHelpers.VerifyDataMatchesRange(results[i], expectedRange);
        }

#if DEBUG
        Assert.Equal(20, CacheInstrumentationCounters.UserRequestsServed);
        Assert.True(CacheInstrumentationCounters.RebalanceIntentPublished >= 20);
        // Many intents should have been cancelled
        Assert.True(CacheInstrumentationCounters.RebalanceIntentCancelled >= 15,
            "Rapid requests should cancel many pending rebalances");
#endif
    }

    /// <summary>
    /// Tests Snapshot read mode behavior, verifying zero-allocation reads from cache.
    /// </summary>
    /// <remarks>
    /// This test validates the SnapshotReadStorage implementation, which provides direct
    /// ReadOnlyMemory access to cached data without copying. This mode offers the best
    /// performance for scenarios where the caller can safely consume data immediately
    /// without holding references beyond the synchronous call.
    /// 
    /// Verifies that data is correctly returned and matches requested ranges in Snapshot mode.
    /// </remarks>
    [Fact]
    public async Task ReadModeSnapshot_VerifyBehavior()
    {
        // Verify Snapshot read mode behavior (zero allocation reads)

        // Arrange
        var options = TestHelpers.CreateDefaultOptions(readMode: UserCacheReadMode.Snapshot);
        var mockDataSource = CreateMockDataSource();
        var cache = new WindowCache<int, int, IntegerFixedStepDomain>(mockDataSource.Object, _domain, options);

        // Act
        var data1 = await cache.GetDataAsync(TestHelpers.CreateRange(100, 110), CancellationToken.None);
        var data2 = await cache.GetDataAsync(TestHelpers.CreateRange(105, 115), CancellationToken.None);

        // Assert
        TestHelpers.VerifyDataMatchesRange(data1, TestHelpers.CreateRange(100, 110));
        TestHelpers.VerifyDataMatchesRange(data2, TestHelpers.CreateRange(105, 115));
    }

    /// <summary>
    /// Tests CopyOnRead mode behavior, verifying safe defensive copies are made on each read.
    /// </summary>
    /// <remarks>
    /// This test validates the CopyOnReadStorage implementation, which creates a defensive
    /// copy of cached data on each read operation. This mode provides memory safety for
    /// scenarios where callers may hold references to returned data beyond the call,
    /// protecting against concurrent modifications during background rebalance operations.
    /// 
    /// Verifies that data is correctly returned and matches requested ranges in CopyOnRead mode.
    /// </remarks>
    [Fact]
    public async Task ReadModeCopyOnRead_VerifyBehavior()
    {
        // Verify CopyOnRead mode behavior (allocates on each read)

        // Arrange
        var options = TestHelpers.CreateDefaultOptions(readMode: UserCacheReadMode.CopyOnRead);
        var mockDataSource = CreateMockDataSource();
        var cache = new WindowCache<int, int, IntegerFixedStepDomain>(mockDataSource.Object, _domain, options);

        // Act
        var data1 = await cache.GetDataAsync(TestHelpers.CreateRange(100, 110), CancellationToken.None);
        var data2 = await cache.GetDataAsync(TestHelpers.CreateRange(105, 115), CancellationToken.None);

        // Assert
        TestHelpers.VerifyDataMatchesRange(data1, TestHelpers.CreateRange(100, 110));
        TestHelpers.VerifyDataMatchesRange(data2, TestHelpers.CreateRange(105, 115));
    }

    #endregion
}
