using Intervals.NET;
using Intervals.NET.Domain.Abstractions;
using SlidingWindowCache.Core.Planning;
using SlidingWindowCache.Core.Rebalance.Decision;
using SlidingWindowCache.Core.Rebalance.Execution;
using SlidingWindowCache.Core.Rebalance.Intent;
using SlidingWindowCache.Core.State;
using SlidingWindowCache.Core.UserPath;
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
    /// A task that represents the asynchronous operation. The task result contains a <see cref="ReadOnlyMemory{T}"/> 
    /// of data for the specified range from the materialized cache.
    /// </returns>
    ValueTask<ReadOnlyMemory<TData>> GetDataAsync(
        Range<TRange> requestedRange,
        CancellationToken cancellationToken);
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
    private readonly IntentController<TRange, TData, TDomain> _intentController;

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

        var decisionEngine = new RebalanceDecisionEngine<TRange, TDomain>(rebalancePolicy, rangePlanner, noRebalancePlanner);
        var executor =
            new RebalanceExecutor<TRange, TData, TDomain>(state, cacheFetcher, cacheDiagnostics);

        // IntentController composes with Execution Scheduler to form the Rebalance Intent Manager actor
        _intentController = new IntentController<TRange, TData, TDomain>(
            state,
            decisionEngine,
            executor,
            options.DebounceDelay,
            cacheDiagnostics
        );

        // Initialize the UserRequestHandler (Fast Path Actor)
        _userRequestHandler = new UserRequestHandler<TRange, TData, TDomain>(
            state,
            cacheFetcher,
            _intentController,
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

    /// <summary>
    /// Waits for any pending background rebalance operations to complete.
    /// This is an infrastructure API, not part of the domain semantics.
    /// </summary>
    /// <param name="timeout">
    /// Maximum time to wait for idle state. Defaults to 30 seconds.
    /// Throws <see cref="TimeoutException"/> if background tasks do not stabilize within this period.
    /// </param>
    /// <returns>
    /// A Task that completes when all scheduled background rebalance operations have finished.
    /// </returns>
    /// <remarks>
    /// <para><strong>Infrastructure API:</strong></para>
    /// <para>
    /// This method provides deterministic synchronization with background rebalance execution
    /// for testing, graceful shutdown, health checks, and integration scenarios. It is NOT part 
    /// of the cache's domain semantics or normal usage patterns.
    /// </para>
    /// <para><strong>Use Cases:</strong></para>
    /// <list type="bullet">
    /// <item><description>Test stabilization: Ensure cache has converged before assertions</description></item>
    /// <item><description>Graceful shutdown: Wait for background work before disposing resources</description></item>
    /// <item><description>Health checks: Verify rebalance operations are completing successfully</description></item>
    /// <item><description>Integration scenarios: Synchronize with background work completion</description></item>
    /// <item><description>Diagnostic scenarios: Verify rebalance execution has finished</description></item>
    /// </list>
    /// <para><strong>Actor Responsibility Boundaries:</strong></para>
    /// <para>
    /// This method does NOT alter actor responsibilities. It is a pure delegation facade:
    /// </para>
    /// <list type="bullet">
    /// <item><description>UserRequestHandler remains the ONLY publisher of rebalance intents</description></item>
    /// <item><description>IntentController remains the lifecycle authority for intent cancellation</description></item>
    /// <item><description>RebalanceScheduler remains the authority for background Task execution</description></item>
    /// <item><description>WindowCache remains a composition root with no business logic</description></item>
    /// </list>
    /// <para>
    /// This method exists solely to expose the idle synchronization mechanism through the public API
    /// for infrastructure purposes, maintaining the existing architectural separation.
    /// </para>
    /// </remarks>
    public Task WaitForIdleAsync(TimeSpan? timeout = null) => _intentController.WaitForIdleAsync(timeout);
}