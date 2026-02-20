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
/// </remarks>
public interface IWindowCache<TRange, TData, TDomain>
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
    /// A task that represents the asynchronous operation. The task result contains the data 
    /// for the specified range as a <see cref="ReadOnlyMemory{T}"/>.
    /// </returns>
    ValueTask<ReadOnlyMemory<TData>> GetDataAsync(
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
/// <item><description><strong>RebalanceIntentManager</strong> - Temporal Authority (Background)</description></item>
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
        var executionController = new RebalanceExecutionController<TRange, TData, TDomain>(
            executor,
            options.DebounceDelay,
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
    public ValueTask<ReadOnlyMemory<TData>> GetDataAsync(
        Range<TRange> requestedRange,
        CancellationToken cancellationToken)
    {
        // Pure facade: delegate to UserRequestHandler actor
        return _userRequestHandler.HandleRequestAsync(requestedRange, cancellationToken);
    }

    /// <inheritdoc cref="IWindowCache{TRange,TData,TDomain}.WaitForIdleAsync"/>
    /// <remarks>
    /// <para><strong>Implementation Strategy:</strong></para>
    /// <para>
    /// Delegates to AsyncActivityCounter which tracks active operations atomically:
    /// <list type="bullet">
    /// <item><description>Counter increments when intent published or execution enqueued</description></item>
    /// <item><description>Counter decrements when intent processing completes or execution finishes</description></item>
    /// <item><description>Returns completed Task when counter reaches 0 (idle state)</description></item>
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
    /// </remarks>
    public Task WaitForIdleAsync(CancellationToken cancellationToken = default) =>
        _activityCounter.WaitForIdleAsync(cancellationToken);
}