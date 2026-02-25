using Intervals.NET;
using Intervals.NET.Domain.Abstractions;
using SlidingWindowCache.Core.Planning;
using SlidingWindowCache.Core.Rebalance.Decision;
using SlidingWindowCache.Core.Rebalance.Execution;
using SlidingWindowCache.Core.Rebalance.Intent;
using SlidingWindowCache.Core.State;
using SlidingWindowCache.Core.UserPath;
using SlidingWindowCache.Infrastructure.Concurrency;
using SlidingWindowCache.Infrastructure.Instrumentation;
using SlidingWindowCache.Infrastructure.Storage;
using SlidingWindowCache.Public.Configuration;
using SlidingWindowCache.Public.Dto;

namespace SlidingWindowCache.Public;

/// <summary>
/// Represents a sliding window cache that retrieves and caches data for specified ranges,
/// with automatic rebalancing based on access patterns.
/// </summary>
/// <typeparam name="TRange">
/// The type representing the range boundaries. Must implement <see cref="IComparable{T}"/>.
/// </typeparam>
/// <typeparam name="TData">
/// The type of data being cached.
/// </typeparam>
/// <typeparam name="TDomain">
/// The type representing the domain of the ranges. Must implement <see cref="IRangeDomain{TRange}"/>.
/// Supports both fixed-step (O(1)) and variable-step (O(N)) domains. While variable-step domains
/// have O(N) complexity for range calculations, this cost is negligible compared to data source I/O.
/// </typeparam>
/// <remarks>
/// <para><strong>Domain Flexibility:</strong></para>
/// <para>
/// This cache works with any <see cref="IRangeDomain{TRange}"/> implementation, whether fixed-step
/// or variable-step. The in-memory cost of O(N) step counting (microseconds) is orders of magnitude
/// smaller than typical data source operations (milliseconds to seconds via network/disk I/O).
/// </para>
/// <para><strong>Examples:</strong></para>
/// <list type="bullet">
/// <item><description>Fixed-step: DateTimeDayFixedStepDomain, IntegerFixedStepDomain (O(1) operations)</description></item>
/// <item><description>Variable-step: Business days, months, custom calendars (O(N) operations, still fast)</description></item>
/// </list>
/// <para><strong>Resource Management:</strong></para>
/// <para>
/// WindowCache manages background processing tasks and resources that require explicit disposal.
/// Always call <see cref="IAsyncDisposable.DisposeAsync"/> when done using the cache instance.
/// </para>
/// <para><strong>Disposal Behavior:</strong></para>
/// <list type="bullet">
/// <item><description>Gracefully stops background rebalance processing loops</description></item>
/// <item><description>Disposes internal synchronization primitives (semaphores, cancellation tokens)</description></item>
/// <item><description>After disposal, all methods throw <see cref="ObjectDisposedException"/></description></item>
/// <item><description>Safe to call multiple times (idempotent)</description></item>
/// <item><description>Does not require timeout - completes when background tasks finish current work</description></item>
/// </list>
/// <para><strong>Usage Pattern:</strong></para>
/// <code>
/// await using var cache = new WindowCache&lt;int, int, IntegerFixedStepDomain&gt;(...);
/// var data = await cache.GetDataAsync(range, cancellationToken);
/// // DisposeAsync automatically called at end of scope
/// </code>
/// </remarks>
public interface IWindowCache<TRange, TData, TDomain> : IAsyncDisposable
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    /// <summary>
    /// Retrieves data for the specified range, utilizing the sliding window cache mechanism.
    /// </summary>
    /// <param name="requestedRange">
    /// The range for which to retrieve data.
    /// </param>
    /// <param name="cancellationToken">
    /// A cancellation token to cancel the operation.
    /// </param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains a 
    /// <see cref="RangeResult{TRange, TData}"/> with the actual available range and data.
    /// </returns>
    /// <remarks>
    /// <para><strong>Bounded Data Sources:</strong></para>
    /// <para>
    /// When working with bounded data sources (e.g., databases with min/max IDs, time-series with
    /// temporal limits), the returned RangeResult.Range indicates what portion of the request was
    /// actually available. The Range may be:
    /// </para>
    /// <list type="bullet">
    /// <item><description>Equal to requestedRange - all data available (typical case)</description></item>
    /// <item><description>Subset of requestedRange - partial data available (truncated at boundaries)</description></item>
    /// <item><description>Null - no data available for the requested range</description></item>
    /// </list>
    /// <para><strong>Example:</strong></para>
    /// <code>
    /// var result = await cache.GetDataAsync(Range.Closed(50, 600), ct);
    /// if (result.Range.HasValue)
    /// {
    ///     Console.WriteLine($"Got data for range: {result.Range.Value}");
    ///     ProcessData(result.Data);
    /// }
    /// else
    /// {
    ///     Console.WriteLine("No data available for requested range");
    /// }
    /// </code>
    /// <para>See boundary handling documentation for details.</para>
    /// </remarks>
    ValueTask<RangeResult<TRange, TData>> GetDataAsync(
        Range<TRange> requestedRange,
        CancellationToken cancellationToken);

    /// <summary>
    /// Waits for the cache to reach an idle state (no pending intent and no executing rebalance).
    /// </summary>
    /// <param name="cancellationToken">
    /// A cancellation token to cancel the wait operation.
    /// </param>
    /// <returns>
    /// A task that completes when the cache reaches idle state.
    /// </returns>
    /// <remarks>
    /// <para><strong>Idle State Definition:</strong></para>
    /// <para>
    /// The cache is considered idle when:
    /// <list type="bullet">
    /// <item><description>No pending intent is awaiting processing</description></item>
    /// <item><description>No rebalance execution is currently running</description></item>
    /// </list>
    /// </para>
    /// <para><strong>Use Cases:</strong></para>
    /// <list type="bullet">
    /// <item><description>Testing: Ensure cache has stabilized before assertions</description></item>
    /// <item><description>Cold start synchronization: Wait for initial rebalance to complete</description></item>
    /// <item><description>Diagnostics: Verify cache has converged to optimal state</description></item>
    /// </list>
    /// </remarks>
    Task WaitForIdleAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IWindowCache{TRange,TData,TDomain}"/>
/// <remarks>
/// <para><strong>Architecture:</strong></para>
/// <para>
/// WindowCache acts as a <strong>Public Facade</strong> and <strong>Composition Root</strong>.
/// It wires together all internal actors but does not implement business logic itself.
/// All user requests are delegated to the internal <see cref="UserRequestHandler{TRange,TData,TDomain}"/> actor.
/// </para>
/// <para><strong>Internal Actors:</strong></para>
/// <list type="bullet">
/// <item><description><strong>UserRequestHandler</strong> - Fast Path Actor (User Thread)</description></item>
/// <item><description><strong>IntentController</strong> - Temporal Authority (Background)</description></item>
/// <item><description><strong>RebalanceDecisionEngine</strong> - Pure Decision Logic (Background)</description></item>
/// <item><description><strong>RebalanceExecutor</strong> - Mutating Actor (Background)</description></item>
/// </list>
/// </remarks>
public sealed class WindowCache<TRange, TData, TDomain>
    : IWindowCache<TRange, TData, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    // Internal actors
    private readonly UserRequestHandler<TRange, TData, TDomain> _userRequestHandler;

    // Activity counter for tracking active intents and executions
    private readonly AsyncActivityCounter _activityCounter = new();

    // Disposal state tracking (lock-free using Interlocked)
    // 0 = not disposed, 1 = disposing, 2 = disposed
    private int _disposeState;

    // TaskCompletionSource for coordinating concurrent DisposeAsync calls
    // Allows loser threads to await disposal completion without CPU burn
    // Published via Volatile.Write when winner thread starts disposal
    private TaskCompletionSource<bool>? _disposalCompletionSource;

    /// <summary>
    /// Initializes a new instance of the <see cref="WindowCache{TRange,TData,TDomain}"/> class.
    /// </summary>
    /// <param name="dataSource">
    /// The data source from which to fetch data.
    /// </param>
    /// <param name="domain">
    /// The domain defining the range characteristics.
    /// </param>
    /// <param name="options">
    /// The configuration options for the window cache.
    /// </param>
    /// <param name="cacheDiagnostics">
    /// Optional diagnostics interface for logging and metrics. Can be null if diagnostics are not needed.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when an unknown read mode is specified in the options.
    /// </exception>
    public WindowCache(
        IDataSource<TRange, TData> dataSource,
        TDomain domain,
        WindowCacheOptions options,
        ICacheDiagnostics? cacheDiagnostics = null
    )
    {
        // Initialize diagnostics (use NoOpDiagnostics if null to avoid null checks in actors)
        cacheDiagnostics ??= new NoOpDiagnostics();
        var cacheStorage = CreateCacheStorage(domain, options);
        var state = new CacheState<TRange, TData, TDomain>(cacheStorage, domain);

        // Initialize all internal actors following corrected execution context model
        var rebalancePolicy = new ThresholdRebalancePolicy<TRange, TDomain>();
        var rangePlanner = new ProportionalRangePlanner<TRange, TDomain>(options, domain);
        var noRebalancePlanner = new NoRebalanceRangePlanner<TRange, TDomain>(options, domain);
        var cacheFetcher = new CacheDataExtensionService<TRange, TData, TDomain>(dataSource, domain, cacheDiagnostics);

        var decisionEngine =
            new RebalanceDecisionEngine<TRange, TDomain>(rebalancePolicy, rangePlanner, noRebalancePlanner);
        var executor =
            new RebalanceExecutor<TRange, TData, TDomain>(state, cacheFetcher, cacheDiagnostics);

        // Create execution actor (guarantees single-threaded cache mutations)
        // Strategy selected based on RebalanceQueueCapacity configuration
        var executionController = CreateExecutionController(
            executor,
            options,
            cacheDiagnostics,
            _activityCounter
        );

        // Create intent controller actor (fast CPU-bound decision logic with cancellation support)
        var intentController = new IntentController<TRange, TData, TDomain>(
            state,
            decisionEngine,
            executionController,
            cacheDiagnostics,
            _activityCounter
        );

        // Initialize the UserRequestHandler (Fast Path Actor)
        _userRequestHandler = new UserRequestHandler<TRange, TData, TDomain>(
            state,
            cacheFetcher,
            intentController,
            dataSource,
            cacheDiagnostics
        );

        return;

        // Factory method to create the appropriate execution controller based on the specified rebalance queue capacity
        static IRebalanceExecutionController<TRange, TData, TDomain> CreateExecutionController(
            RebalanceExecutor<TRange, TData, TDomain> executor,
            WindowCacheOptions options,
            ICacheDiagnostics cacheDiagnostics,
            AsyncActivityCounter activityCounter
        )
        {
            if (options.RebalanceQueueCapacity == null)
            {
                // Unbounded strategy: Task-based serialization (default, recommended for most scenarios)
                return new TaskBasedRebalanceExecutionController<TRange, TData, TDomain>(
                    executor,
                    options.DebounceDelay,
                    cacheDiagnostics,
                    activityCounter
                );
            }
            else
            {
                // Bounded strategy: Channel-based serialization with backpressure support
                return new ChannelBasedRebalanceExecutionController<TRange, TData, TDomain>(
                    executor,
                    options.DebounceDelay,
                    cacheDiagnostics,
                    activityCounter,
                    options.RebalanceQueueCapacity.Value
                );
            }
        }

        // Factory method to create the appropriate cache storage based on the specified read mode in options
        static ICacheStorage<TRange, TData, TDomain> CreateCacheStorage(
            TDomain fixedStepDomain,
            WindowCacheOptions windowCacheOptions
        ) => windowCacheOptions.ReadMode switch
        {
            UserCacheReadMode.Snapshot => new SnapshotReadStorage<TRange, TData, TDomain>(fixedStepDomain),
            UserCacheReadMode.CopyOnRead => new CopyOnReadStorage<TRange, TData, TDomain>(fixedStepDomain),
            _ => throw new ArgumentOutOfRangeException(nameof(windowCacheOptions.ReadMode),
                windowCacheOptions.ReadMode, "Unknown read mode.")
        };
    }

    /// <inheritdoc cref="IWindowCache{TRange,TData,TDomain}.GetDataAsync"/>
    /// <remarks>
    /// This method acts as a thin delegation layer to the internal <see cref="UserRequestHandler{TRange,TData,TDomain}"/> actor.
    /// WindowCache itself implements no business logic - it is a pure facade.
    /// </remarks>
    public ValueTask<RangeResult<TRange, TData>> GetDataAsync(
        Range<TRange> requestedRange,
        CancellationToken cancellationToken)
    {
        // Check disposal state using Volatile.Read (lock-free)
        if (Volatile.Read(ref _disposeState) != 0)
        {
            throw new ObjectDisposedException(
                nameof(WindowCache<TRange, TData, TDomain>),
                "Cannot retrieve data from a disposed cache.");
        }

        // Delegate to UserRequestHandler (Fast Path Actor)
        return _userRequestHandler.HandleRequestAsync(requestedRange, cancellationToken);
    }

    /// <inheritdoc cref="IWindowCache{TRange,TData,TDomain}.WaitForIdleAsync"/>
    /// <remarks>
    /// <para><strong>Implementation Strategy:</strong></para>
    /// <para>
    /// Delegates to AsyncActivityCounter which tracks active operations using lock-free atomic operations:
    /// <list type="bullet">
    /// <item><description>Counter increments atomically when intent published or execution enqueued</description></item>
    /// <item><description>Counter decrements atomically when intent processing completes or execution finishes</description></item>
    /// <item><description>TaskCompletionSource signaled when counter reaches 0 (idle state)</description></item>
    /// <item><description>Returns Task that completes when system idle (state-based, supports multiple awaiters)</description></item>
    /// </list>
    /// </para>
    /// <para><strong>Idle State Definition:</strong></para>
    /// <para>
    /// Cache is idle when activity counter is 0, meaning:
    /// <list type="bullet">
    /// <item><description>No intent processing in progress</description></item>
    /// <item><description>No rebalance execution running</description></item>
    /// </list>
    /// </para>
    /// <para><strong>Idle State Semantics - "Was Idle" NOT "Is Idle":</strong></para>
    /// <para>
    /// This method completes when the system <strong>was idle at some point in time</strong>.
    /// It does NOT guarantee the system is still idle after completion (new activity may start immediately).
    /// This is correct behavior for eventual consistency models - callers must re-check state if needed.
    /// </para>
    /// <para><strong>Typical Usage (Testing):</strong></para>
    /// <code>
    /// // Trigger operation that schedules rebalance
    /// await cache.GetDataAsync(newRange);
    /// 
    /// // Wait for system to stabilize
    /// await cache.WaitForIdleAsync();
    /// 
    /// // Cache WAS idle at some point - assert on converged state
    /// Assert.Equal(expectedRange, cache.CurrentCacheRange);
    /// </code>
    /// </remarks>
    public Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        // Check disposal state using Volatile.Read (lock-free)
        if (Volatile.Read(ref _disposeState) != 0)
        {
            throw new ObjectDisposedException(
                nameof(WindowCache<TRange, TData, TDomain>),
                "Cannot access a disposed WindowCache instance.");
        }

        return _activityCounter.WaitForIdleAsync(cancellationToken);
    }

    /// <summary>
    /// Asynchronously disposes the WindowCache and releases all associated resources.
    /// </summary>
    /// <returns>
    /// A task that represents the asynchronous disposal operation.
    /// </returns>
    /// <remarks>
    /// <para><strong>Disposal Sequence:</strong></para>
    /// <list type="number">
    /// <item><description>Atomically transitions disposal state from 0 (active) to 1 (disposing)</description></item>
    /// <item><description>Disposes UserRequestHandler which cascades to IntentController and RebalanceExecutionController</description></item>
    /// <item><description>Waits for all background processing loops to complete gracefully</description></item>
    /// <item><description>Transitions disposal state to 2 (disposed)</description></item>
    /// </list>
    /// <para><strong>Idempotency:</strong></para>
    /// <para>
    /// Safe to call multiple times. Subsequent calls will wait for the first disposal to complete
    /// using a three-state pattern (0=active, 1=disposing, 2=disposed). This ensures exactly-once
    /// disposal execution while allowing concurrent disposal attempts to complete successfully.
    /// </para>
    /// <para><strong>Thread Safety:</strong></para>
    /// <para>
    /// Uses lock-free synchronization via <see cref="Interlocked.CompareExchange"/>, <see cref="Volatile"/>,
    /// and <see cref="TaskCompletionSource{TResult}"/> operations, consistent with the project's 
    /// "Mostly Lock-Free Concurrency" architecture principle.
    /// </para>
    /// <para><strong>Concurrent Disposal Coordination:</strong></para>
    /// <para>
    /// When multiple threads call DisposeAsync concurrently:
    /// <list type="bullet">
    /// <item><description>Winner thread (first to transition 0→1): Creates TCS, performs disposal, signals completion</description></item>
    /// <item><description>Loser threads (see state=1): Await TCS.Task to wait asynchronously without CPU burn</description></item>
    /// <item><description>All threads observe the same disposal outcome (success or exception propagation)</description></item>
    /// </list>
    /// This pattern prevents CPU spinning while the winner thread performs async disposal operations.
    /// Similar to <see cref="AsyncActivityCounter"/> idle coordination pattern.
    /// </para>
    /// <para><strong>Architectural Context:</strong></para>
    /// <para>
    /// WindowCache acts as the Composition Root and owns all internal actors. Disposal follows
    /// the ownership hierarchy: WindowCache → UserRequestHandler → IntentController → RebalanceExecutionController.
    /// Each actor disposes its owned resources in reverse order of initialization.
    /// </para>
    /// <para><strong>Exception Handling:</strong></para>
    /// <para>
    /// Any exceptions during disposal are propagated to ALL callers (both winner and losers). 
    /// This aligns with the "Background Path Exceptions" pattern where cleanup failures should be 
    /// observable but not crash the application. Loser threads will observe and re-throw the same 
    /// exception that occurred during disposal.
    /// </para>
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        // Three-state disposal pattern for idempotency and concurrent disposal support
        // States: 0 = active, 1 = disposing, 2 = disposed

        // Attempt to transition from active (0) to disposing (1)
        var previousState = Interlocked.CompareExchange(ref _disposeState, 1, 0);

        if (previousState == 0)
        {
            // Winner thread - create TCS and perform disposal
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _disposalCompletionSource, tcs);

            try
            {
                // Dispose the UserRequestHandler which cascades to all internal actors
                // Disposal order: UserRequestHandler -> IntentController -> RebalanceExecutionController
                await _userRequestHandler.DisposeAsync().ConfigureAwait(false);
                
                // Signal successful completion
                tcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                // Signal failure - loser threads will observe this exception
                tcs.TrySetException(ex);
                throw;
            }
            finally
            {
                // Mark disposal as complete (transition to state 2)
                Volatile.Write(ref _disposeState, 2);
            }
        }
        else if (previousState == 1)
        {
            // Loser thread - await disposal completion asynchronously
            // Brief spin-wait for TCS publication (should be very fast - CPU-only operation)
            TaskCompletionSource<bool>? tcs;
            var spinWait = new SpinWait();
            
            while ((tcs = Volatile.Read(ref _disposalCompletionSource)) == null)
            {
                spinWait.SpinOnce();
            }
            
            // Await disposal completion without CPU burn
            // If winner threw exception, this will re-throw the same exception
            await tcs.Task.ConfigureAwait(false);
        }
        // If previousState == 2, disposal already completed - return immediately (idempotent)
    }
}