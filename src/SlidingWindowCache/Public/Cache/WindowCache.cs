using Intervals.NET;
using Intervals.NET.Domain.Abstractions;
using SlidingWindowCache.Core.Planning;
using SlidingWindowCache.Core.Rebalance.Decision;
using SlidingWindowCache.Core.Rebalance.Execution;
using SlidingWindowCache.Core.Rebalance.Intent;
using SlidingWindowCache.Core.State;
using SlidingWindowCache.Core.UserPath;
using SlidingWindowCache.Infrastructure.Concurrency;
using SlidingWindowCache.Infrastructure.Storage;
using SlidingWindowCache.Public.Configuration;
using SlidingWindowCache.Public.Dto;
using SlidingWindowCache.Public.Instrumentation;

namespace SlidingWindowCache.Public.Cache;

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

    // Shared runtime options holder — updated via UpdateRuntimeOptions, read by planners and execution controllers
    private readonly RuntimeCacheOptionsHolder _runtimeOptionsHolder;

    // Activity counter for tracking active intents and executions
    private readonly AsyncActivityCounter _activityCounter = new();

    // Disposal state tracking (lock-free using Interlocked)
    // 0 = not disposed, 1 = disposing, 2 = disposed
    private int _disposeState;

    // TaskCompletionSource for coordinating concurrent DisposeAsync calls
    // Allows loser threads to await disposal completion without CPU burn
    // Published via Volatile.Write when winner thread starts disposal
    private TaskCompletionSource? _disposalCompletionSource;

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
        cacheDiagnostics ??= NoOpDiagnostics.Instance;
        var cacheStorage = CreateCacheStorage(domain, options.ReadMode);
        var state = new CacheState<TRange, TData, TDomain>(cacheStorage, domain);

        // Create the shared runtime options holder from the initial WindowCacheOptions values.
        // Planners and execution controllers hold a reference to this holder and read Current
        // at invocation time, enabling runtime updates via UpdateRuntimeOptions.
        _runtimeOptionsHolder = new RuntimeCacheOptionsHolder(
            new RuntimeCacheOptions(
                options.LeftCacheSize,
                options.RightCacheSize,
                options.LeftThreshold,
                options.RightThreshold,
                options.DebounceDelay
            )
        );

        // Initialize all internal actors following corrected execution context model
        var rebalancePolicy = new NoRebalanceSatisfactionPolicy<TRange>();
        var rangePlanner = new ProportionalRangePlanner<TRange, TDomain>(_runtimeOptionsHolder, domain);
        var noRebalancePlanner = new NoRebalanceRangePlanner<TRange, TDomain>(_runtimeOptionsHolder, domain);
        var cacheFetcher = new CacheDataExtensionService<TRange, TData, TDomain>(dataSource, domain, cacheDiagnostics);

        var decisionEngine =
            new RebalanceDecisionEngine<TRange, TDomain>(rebalancePolicy, rangePlanner, noRebalancePlanner);
        var executor =
            new RebalanceExecutor<TRange, TData, TDomain>(state, cacheFetcher, cacheDiagnostics);

        // Create execution actor (guarantees single-threaded cache mutations)
        // Strategy selected based on RebalanceQueueCapacity configuration
        var executionController = CreateExecutionController(
            executor,
            _runtimeOptionsHolder,
            options.RebalanceQueueCapacity,
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
    }

    /// <summary>
    /// Creates the appropriate execution controller based on the specified rebalance queue capacity.
    /// </summary>
    private static IRebalanceExecutionController<TRange, TData, TDomain> CreateExecutionController(
        RebalanceExecutor<TRange, TData, TDomain> executor,
        RuntimeCacheOptionsHolder optionsHolder,
        int? rebalanceQueueCapacity,
        ICacheDiagnostics cacheDiagnostics,
        AsyncActivityCounter activityCounter
    )
    {
        if (rebalanceQueueCapacity == null)
        {
            // Unbounded strategy: Task-based serialization (default, recommended for most scenarios)
            return new TaskBasedRebalanceExecutionController<TRange, TData, TDomain>(
                executor,
                optionsHolder,
                cacheDiagnostics,
                activityCounter
            );
        }

        // Bounded strategy: Channel-based serialization with backpressure support
        return new ChannelBasedRebalanceExecutionController<TRange, TData, TDomain>(
            executor,
            optionsHolder,
            cacheDiagnostics,
            activityCounter,
            rebalanceQueueCapacity.Value
        );
    }

    /// <summary>
    /// Creates the appropriate cache storage based on the specified read mode in options.
    /// </summary>
    private static ICacheStorage<TRange, TData, TDomain> CreateCacheStorage(
        TDomain domain,
        UserCacheReadMode readMode
    ) => readMode switch
    {
        UserCacheReadMode.Snapshot => new SnapshotReadStorage<TRange, TData, TDomain>(domain),
        UserCacheReadMode.CopyOnRead => new CopyOnReadStorage<TRange, TData, TDomain>(domain),
        _ => throw new ArgumentOutOfRangeException(nameof(readMode),
            readMode, "Unknown read mode.")
    };

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

    /// <inheritdoc cref="IWindowCache{TRange,TData,TDomain}.UpdateRuntimeOptions"/>
    /// <remarks>
    /// <para><strong>Implementation:</strong></para>
    /// <para>
    /// Reads the current snapshot from <see cref="_runtimeOptionsHolder"/>, applies the builder deltas,
    /// validates the merged result (via <see cref="RuntimeCacheOptions"/> constructor), then publishes
    /// the new snapshot via <see cref="RuntimeCacheOptionsHolder.Update"/> using a Volatile.Write
    /// (release fence). Background threads pick up the new snapshot on their next read cycle.
    /// </para>
    /// <para>
    /// If validation throws, the holder is not updated and the current options remain active.
    /// </para>
    /// </remarks>
    public void UpdateRuntimeOptions(Action<RuntimeOptionsUpdateBuilder> configure)
    {
        // Check disposal state using Volatile.Read (lock-free)
        if (Volatile.Read(ref _disposeState) != 0)
        {
            throw new ObjectDisposedException(
                nameof(WindowCache<TRange, TData, TDomain>),
                "Cannot update runtime options on a disposed cache.");
        }

        // ApplyTo reads the current snapshot, merges deltas, and validates —
        // throws if validation fails (holder not updated in that case).
        var builder = new RuntimeOptionsUpdateBuilder();
        configure(builder);
        var newOptions = builder.ApplyTo(_runtimeOptionsHolder.Current);

        // Publish atomically; background threads see the new snapshot on next read.
        _runtimeOptionsHolder.Update(newOptions);
    }

    /// <inheritdoc cref="IWindowCache{TRange,TData,TDomain}.CurrentRuntimeOptions"/>
    public RuntimeOptionsSnapshot CurrentRuntimeOptions
    {
        get
        {
            // Check disposal state using Volatile.Read (lock-free)
            if (Volatile.Read(ref _disposeState) != 0)
            {
                throw new ObjectDisposedException(
                    nameof(WindowCache<TRange, TData, TDomain>),
                    "Cannot access runtime options on a disposed cache.");
            }

            return _runtimeOptionsHolder.Current.ToSnapshot();
        }
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
    /// and <see cref="TaskCompletionSource"/> operations, consistent with the project's 
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
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _disposalCompletionSource, tcs);

            try
            {
                // Dispose the UserRequestHandler which cascades to all internal actors
                // Disposal order: UserRequestHandler -> IntentController -> RebalanceExecutionController
                await _userRequestHandler.DisposeAsync().ConfigureAwait(false);

                // Signal successful completion
                tcs.TrySetResult();
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
            TaskCompletionSource? tcs;
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