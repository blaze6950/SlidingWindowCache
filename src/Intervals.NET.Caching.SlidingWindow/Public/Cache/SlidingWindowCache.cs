using Intervals.NET.Domain.Abstractions;
using Intervals.NET.Extensions;
using Intervals.NET.Caching.Dto;
using Intervals.NET.Caching.Infrastructure.Concurrency;
using Intervals.NET.Caching.Infrastructure.Scheduling;
using Intervals.NET.Caching.Infrastructure.Scheduling.Supersession;
using Intervals.NET.Caching.SlidingWindow.Core.Planning;
using Intervals.NET.Caching.SlidingWindow.Core.Rebalance.Decision;
using Intervals.NET.Caching.SlidingWindow.Core.Rebalance.Execution;
using Intervals.NET.Caching.SlidingWindow.Core.Rebalance.Intent;
using Intervals.NET.Caching.SlidingWindow.Core.State;
using Intervals.NET.Caching.SlidingWindow.Core.UserPath;
using Intervals.NET.Caching.SlidingWindow.Infrastructure.Adapters;
using Intervals.NET.Caching.SlidingWindow.Infrastructure.Storage;
using Intervals.NET.Caching.SlidingWindow.Public.Configuration;
using Intervals.NET.Caching.SlidingWindow.Public.Instrumentation;

namespace Intervals.NET.Caching.SlidingWindow.Public.Cache;

/// <inheritdoc cref="ISlidingWindowCache{TRange,TData,TDomain}"/>
public sealed class SlidingWindowCache<TRange, TData, TDomain>
    : ISlidingWindowCache<TRange, TData, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    // Internal actors
    private readonly UserRequestHandler<TRange, TData, TDomain> _userRequestHandler;

    // Shared runtime options holder � updated via UpdateRuntimeOptions, read by planners and execution controllers
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
    /// Initializes a new instance of the <see cref="SlidingWindowCache{TRange,TData,TDomain}"/> class.
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
    public SlidingWindowCache(
        IDataSource<TRange, TData> dataSource,
        TDomain domain,
        SlidingWindowCacheOptions options,
        ISlidingWindowCacheDiagnostics? cacheDiagnostics = null
    )
    {
        // Initialize diagnostics (use NoOpDiagnostics if null to avoid null checks in actors)
        cacheDiagnostics ??= NoOpDiagnostics.Instance;
        var cacheStorage = CreateCacheStorage(domain, options.ReadMode);
        var state = new CacheState<TRange, TData, TDomain>(cacheStorage, domain);

        // Create the shared runtime options holder from the initial SlidingWindowCacheOptions values.
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
    /// Creates the appropriate execution scheduler based on the specified rebalance queue capacity.
    /// </summary>
    private static ISupersessionWorkScheduler<ExecutionRequest<TRange, TData, TDomain>> CreateExecutionController(
        RebalanceExecutor<TRange, TData, TDomain> executor,
        RuntimeCacheOptionsHolder optionsHolder,
        int? rebalanceQueueCapacity,
        ISlidingWindowCacheDiagnostics cacheDiagnostics,
        AsyncActivityCounter activityCounter
    )
    {
        var schedulerDiagnostics = new SlidingWindowWorkSchedulerDiagnostics(cacheDiagnostics);

        // Executor delegate: extracts fields from ExecutionRequest and calls RebalanceExecutor.
        Func<ExecutionRequest<TRange, TData, TDomain>, CancellationToken, Task> executorDelegate =
            (request, ct) => executor.ExecuteAsync(
                request.Intent,
                request.DesiredRange,
                request.DesiredNoRebalanceRange,
                ct);

        // Debounce provider: reads the current DebounceDelay from the options holder at execution time.
        Func<TimeSpan> debounceProvider = () => optionsHolder.Current.DebounceDelay;

        if (rebalanceQueueCapacity == null)
        {
            // Unbounded supersession strategy: task-chaining with cancel-previous (default)
            return new UnboundedSupersessionWorkScheduler<ExecutionRequest<TRange, TData, TDomain>>(
                executorDelegate,
                debounceProvider,
                schedulerDiagnostics,
                activityCounter
            );
        }

        // Bounded supersession strategy: channel-based with backpressure and cancel-previous
        return new BoundedSupersessionWorkScheduler<ExecutionRequest<TRange, TData, TDomain>>(
            executorDelegate,
            debounceProvider,
            schedulerDiagnostics,
            activityCounter,
            rebalanceQueueCapacity.Value,
            singleWriter: true // SWC: IntentController loop is the sole publisher
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

    /// <inheritdoc cref="ISlidingWindowCache{TRange,TData,TDomain}.GetDataAsync"/>
    public ValueTask<RangeResult<TRange, TData>> GetDataAsync(
        Range<TRange> requestedRange,
        CancellationToken cancellationToken)
    {
        // Check disposal state using Volatile.Read (lock-free)
        if (Volatile.Read(ref _disposeState) != 0)
        {
            throw new ObjectDisposedException(
                nameof(SlidingWindowCache<TRange, TData, TDomain>),
                "Cannot retrieve data from a disposed cache.");
        }

        // Invariant S.R.1: requestedRange must be bounded (finite on both ends).
        if (!requestedRange.IsBounded())
        {
            throw new ArgumentException(
                "The requested range must be bounded (finite on both ends). Unbounded ranges cannot be fetched or cached.",
                nameof(requestedRange));
        }

        // Delegate to UserRequestHandler (Fast Path Actor)
        return _userRequestHandler.HandleRequestAsync(requestedRange, cancellationToken);
    }

    /// <inheritdoc cref="ISlidingWindowCache{TRange,TData,TDomain}.WaitForIdleAsync"/>
    public Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        // Check disposal state using Volatile.Read (lock-free)
        if (Volatile.Read(ref _disposeState) != 0)
        {
            throw new ObjectDisposedException(
                nameof(SlidingWindowCache<TRange, TData, TDomain>),
                "Cannot access a disposed SlidingWindowCache instance.");
        }

        return _activityCounter.WaitForIdleAsync(cancellationToken);
    }

    /// <inheritdoc cref="ISlidingWindowCache{TRange,TData,TDomain}.UpdateRuntimeOptions"/>
    public void UpdateRuntimeOptions(Action<RuntimeOptionsUpdateBuilder> configure)
    {
        // Check disposal state using Volatile.Read (lock-free)
        if (Volatile.Read(ref _disposeState) != 0)
        {
            throw new ObjectDisposedException(
                nameof(SlidingWindowCache<TRange, TData, TDomain>),
                "Cannot update runtime options on a disposed cache.");
        }

        // ApplyTo reads the current snapshot, merges deltas, and validates �
        // throws if validation fails (holder not updated in that case).
        var builder = new RuntimeOptionsUpdateBuilder();
        configure(builder);
        var newOptions = builder.ApplyTo(_runtimeOptionsHolder.Current);

        // Publish atomically; background threads see the new snapshot on next read.
        _runtimeOptionsHolder.Update(newOptions);
    }

    /// <inheritdoc cref="ISlidingWindowCache{TRange,TData,TDomain}.CurrentRuntimeOptions"/>
    public RuntimeOptionsSnapshot CurrentRuntimeOptions
    {
        get
        {
            // Check disposal state using Volatile.Read (lock-free)
            if (Volatile.Read(ref _disposeState) != 0)
            {
                throw new ObjectDisposedException(
                    nameof(SlidingWindowCache<TRange, TData, TDomain>),
                    "Cannot access runtime options on a disposed cache.");
            }

            return _runtimeOptionsHolder.Current.ToSnapshot();
        }
    }

    /// <summary>
    /// Asynchronously disposes the SlidingWindowCache and releases all associated resources.
    /// </summary>
    /// <returns>
    /// A task that represents the asynchronous disposal operation.
    /// </returns>
    /// <remarks>
    /// Safe to call multiple times (idempotent). Concurrent callers wait for the first disposal to complete.
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