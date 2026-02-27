using System.Threading.Channels;
using Intervals.NET;
using Intervals.NET.Domain.Abstractions;
using SlidingWindowCache.Core.Rebalance.Intent;
using SlidingWindowCache.Infrastructure.Concurrency;
using SlidingWindowCache.Infrastructure.Instrumentation;

namespace SlidingWindowCache.Core.Rebalance.Execution;

/// <summary>
/// Channel-based execution actor responsible for sequential execution of rebalance operations with bounded capacity and backpressure support.
/// This is the SOLE component in the entire system that mutates CacheState when selected as the execution strategy.
/// </summary>
/// <typeparam name="TRange">The type representing the range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <typeparam name="TDomain">The type representing the domain of the ranges.</typeparam>
/// <remarks>
/// <para><strong>Architectural Role - Bounded Channel Execution Strategy:</strong></para>
/// <para>
/// This implementation uses System.Threading.Channels with bounded capacity to serialize rebalance executions.
/// It provides backpressure by blocking the intent processing loop when the channel is full, creating natural
/// throttling of upstream intent processing. This prevents excessive queuing of execution requests under
/// sustained high-frequency load.
/// </para>
/// <para><strong>Serialization Mechanism - Bounded Channel:</strong></para>
/// <para>
/// Uses Channel.CreateBounded with single-reader/single-writer semantics for optimal performance.
/// The bounded capacity ensures predictable memory usage and prevents runaway queue growth.
/// When capacity is reached, PublishExecutionRequest blocks (await WriteAsync) until space becomes available,
/// creating backpressure that throttles the intent processing loop.
/// </para>
/// <code>
/// // Bounded channel with backpressure:
/// await _executionChannel.Writer.WriteAsync(request);  // Blocks when full
/// 
/// // Sequential processing loop:
/// await foreach (var request in _executionChannel.Reader.ReadAllAsync())
/// {
///     await ExecuteRebalanceAsync(request);  // One at a time
/// }
/// </code>
/// <para><strong>Backpressure Behavior:</strong></para>
/// <para>
/// When the channel reaches its configured capacity, the intent processing loop naturally blocks
/// on WriteAsync. This creates intentional throttling:
/// </para>
/// <list type="bullet">
/// <item><description>Intent processing pauses until execution completes and frees channel space</description></item>
/// <item><description>User requests continue to be served immediately (User Path never blocks)</description></item>
/// <item><description>System self-regulates under sustained high load</description></item>
/// <item><description>Prevents memory exhaustion from unbounded request accumulation</description></item>
/// </list>
/// <para><strong>Single-Writer Architecture Guarantee:</strong></para>
/// <para>
/// The channel's single-reader loop ensures that NO TWO REBALANCE EXECUTIONS ever run concurrently.
/// Only one execution request is processed at a time, guaranteeing serialized cache mutations and
/// eliminating write-write race conditions.
/// </para>
/// <para><strong>Cancellation for Short-Circuit Optimization:</strong></para>
/// <para>
/// Each execution request carries a CancellationToken. Cancellation is checked:
/// </para>
/// <list type="bullet">
/// <item><description>After debounce delay (before I/O) - avoid fetching obsolete data</description></item>
/// <item><description>After data fetch (before mutation) - avoid applying obsolete results</description></item>
/// <item><description>During I/O operations - exit early from long-running fetches</description></item>
/// </list>
/// <para><strong>Trade-offs:</strong></para>
/// <list type="bullet">
/// <item><description>✅ Bounded memory usage (fixed queue size = capacity × request size)</description></item>
/// <item><description>✅ Natural backpressure (throttles upstream when full)</description></item>
/// <item><description>✅ Predictable resource consumption</description></item>
/// <item><description>✅ Self-regulating under sustained high load</description></item>
/// <item><description>⚠️ Intent processing blocks when full (intentional throttling mechanism)</description></item>
/// <item><description>⚠️ Slightly more complex than task-based approach</description></item>
/// </list>
/// <para><strong>When to Use:</strong></para>
/// <para>
/// Use this strategy when:
/// </para>
/// <list type="bullet">
/// <item><description>High-frequency request patterns (>1000 requests/sec)</description></item>
/// <item><description>Resource-constrained environments requiring predictable memory usage</description></item>
/// <item><description>Real-time dashboards with streaming data updates</description></item>
/// <item><description>Scenarios where backpressure throttling is desired</description></item>
/// </list>
/// <para><strong>Configuration:</strong></para>
/// <para>
/// Selected automatically when <see cref="Public.Configuration.WindowCacheOptions.RebalanceQueueCapacity"/> 
/// is set to a value >= 1. Typical capacity values: 5-10 for moderate backpressure, 3-5 for strict control.
/// </para>
/// <para>See also: <see cref="TaskBasedRebalanceExecutionController{TRange,TData,TDomain}"/> for unbounded alternative</para>
/// </remarks>
internal sealed class ChannelBasedRebalanceExecutionController<TRange, TData, TDomain>
    : IRebalanceExecutionController<TRange, TData, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private readonly RebalanceExecutor<TRange, TData, TDomain> _executor;
    private readonly TimeSpan _debounceDelay;
    private readonly ICacheDiagnostics _cacheDiagnostics;
    private readonly Channel<ExecutionRequest<TRange, TData, TDomain>> _executionChannel;
    private readonly Task _executionLoopTask;

    // Activity counter for tracking active operations
    private readonly AsyncActivityCounter _activityCounter;

    // Disposal state tracking (lock-free using Interlocked)
    // 0 = not disposed, 1 = disposed
    private int _disposeState;

    /// <summary>
    /// Stores the most recent execution request submitted to the execution controller.
    /// Used for tracking the current execution state and for testing/diagnostic purposes.
    /// </summary>
    private ExecutionRequest<TRange, TData, TDomain>? _lastExecutionRequest;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelBasedRebalanceExecutionController{TRange,TData,TDomain}"/> class.
    /// </summary>
    /// <param name="executor">The executor for performing rebalance operations.</param>
    /// <param name="debounceDelay">The debounce delay before executing rebalance.</param>
    /// <param name="cacheDiagnostics">The diagnostics interface for recording rebalance-related metrics and events.</param>
    /// <param name="activityCounter">Activity counter for tracking active operations.</param>
    /// <param name="capacity">The bounded channel capacity for backpressure control. Must be >= 1.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when capacity is less than 1.</exception>
    /// <remarks>
    /// <para><strong>Channel Configuration:</strong></para>
    /// <para>
    /// Creates a bounded channel with the specified capacity and single-reader/single-writer semantics.
    /// The bounded capacity enables backpressure: when full, PublishExecutionRequest will block
    /// (await WriteAsync) until space becomes available, throttling the intent processing loop.
    /// </para>
    /// <para><strong>Execution Loop Lifecycle:</strong></para>
    /// <para>
    /// The execution loop starts immediately upon construction and runs for the lifetime of the cache instance.
    /// This actor guarantees single-threaded execution of all cache mutations via sequential channel processing.
    /// </para>
    /// </remarks>
    public ChannelBasedRebalanceExecutionController(
        RebalanceExecutor<TRange, TData, TDomain> executor,
        TimeSpan debounceDelay,
        ICacheDiagnostics cacheDiagnostics,
        AsyncActivityCounter activityCounter,
        int capacity
    )
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity),
                "Capacity must be greater than or equal to 1.");
        }

        _executor = executor;
        _debounceDelay = debounceDelay;
        _cacheDiagnostics = cacheDiagnostics;
        _activityCounter = activityCounter;

        // Initialize bounded channel with single reader/writer semantics
        // Bounded capacity enables backpressure on IntentController actor
        // SingleReader: only execution loop reads; SingleWriter: only IntentController writes
        _executionChannel = Channel.CreateBounded<ExecutionRequest<TRange, TData, TDomain>>(
            new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = true, // Only IntentController actor enqueues execution requests
                AllowSynchronousContinuations = false,
                FullMode = BoundedChannelFullMode.Wait // Block on WriteAsync when full (backpressure)
            });

        // Start execution loop immediately - runs for cache lifetime
        _executionLoopTask = ProcessExecutionRequestsAsync();
    }

    /// <summary>
    /// Gets the most recent execution request submitted to the execution controller.
    /// Returns null if no execution request has been submitted yet.
    /// </summary>
    /// <remarks>
    /// <para><strong>Thread Safety:</strong></para>
    /// <para>
    /// Uses <see cref="Volatile.Read"/> to ensure proper memory visibility across threads.
    /// This property can be safely accessed from multiple threads (intent loop, decision engine).
    /// </para>
    /// </remarks>
    public ExecutionRequest<TRange, TData, TDomain>? LastExecutionRequest
        => Volatile.Read(ref _lastExecutionRequest);

    /// <summary>
    /// Publishes a rebalance execution request to the bounded channel for sequential processing.
    /// </summary>
    /// <param name="intent">The rebalance intent containing delivered data and context.</param>
    /// <param name="desiredRange">The target cache range computed by the decision engine.</param>
    /// <param name="desiredNoRebalanceRange">The desired NoRebalanceRange to be set after execution completes.</param>
    /// <param name="loopCancellationToken">Cancellation token from the intent processing loop. Used to unblock WriteAsync during disposal.</param>
    /// <returns>A ValueTask representing the asynchronous write operation. Completes when the request is enqueued (may block if channel is full).</returns>
    /// <remarks>
    /// <para><strong>Backpressure Behavior:</strong></para>
    /// <para>
    /// This method uses async write semantics with backpressure. When the bounded channel is at capacity,
    /// this method will AWAIT (not return) until space becomes available. This creates intentional
    /// backpressure that throttles the intent processing loop, preventing excessive request accumulation.
    /// </para>
    /// <para><strong>Cancellation Behavior:</strong></para>
    /// <para>
    /// The loopCancellationToken enables graceful shutdown during disposal. If the channel is full and
    /// disposal begins, the token cancellation will unblock the WriteAsync operation, preventing disposal hangs.
    /// On cancellation, the method cleans up resources and returns gracefully without throwing.
    /// </para>
    /// <para><strong>Execution Context:</strong></para>
    /// <para>
    /// Called by IntentController from the background intent processing loop after multi-stage validation
    /// confirms rebalance necessity. The awaiting behavior (when full) naturally throttles upstream intent processing.
    /// </para>
    /// <para><strong>User Path Impact:</strong></para>
    /// <para>
    /// User requests are NEVER blocked. The User Path returns data immediately and publishes intents
    /// in a fire-and-forget manner. Only the background intent processing loop experiences backpressure.
    /// </para>
    /// </remarks>
    public async ValueTask PublishExecutionRequest(
        Intent<TRange, TData, TDomain> intent,
        Range<TRange> desiredRange,
        Range<TRange>? desiredNoRebalanceRange,
        CancellationToken loopCancellationToken)
    {
        // Check disposal state using Volatile.Read (lock-free)
        if (Volatile.Read(ref _disposeState) != 0)
        {
            throw new ObjectDisposedException(
                nameof(ChannelBasedRebalanceExecutionController<TRange, TData, TDomain>),
                "Cannot publish execution request to a disposed controller.");
        }

        // Increment activity counter for new execution request
        _activityCounter.IncrementActivity();

        // Create CancellationTokenSource for this execution request
        var cancellationTokenSource = new CancellationTokenSource();

        // Create execution request message
        var request = new ExecutionRequest<TRange, TData, TDomain>(
            intent,
            desiredRange,
            desiredNoRebalanceRange,
            cancellationTokenSource
        );
        Volatile.Write(ref _lastExecutionRequest, request);

        // Enqueue execution request to bounded channel
        // BACKPRESSURE: This will await if channel is at capacity, creating backpressure on intent processing loop
        // CANCELLATION: loopCancellationToken enables graceful shutdown during disposal
        try
        {
            await _executionChannel.Writer.WriteAsync(request, loopCancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (loopCancellationToken.IsCancellationRequested)
        {
            // Write cancelled during disposal - clean up and exit gracefully
            // Don't throw - disposal is shutting down the loop
            request.Dispose();
            _activityCounter.DecrementActivity();
        }
        catch (Exception ex)
        {
            // If write fails (e.g., channel completed during disposal), clean up and report
            request.Dispose();
            _activityCounter.DecrementActivity();
            _cacheDiagnostics.RebalanceExecutionFailed(ex);
            throw; // Re-throw to signal failure to caller
        }
    }

    /// <summary>
    /// Execution actor loop that processes requests sequentially from the bounded channel.
    /// This is the SOLE mutator of CacheState in the entire system when this strategy is active.
    /// </summary>
    /// <remarks>
    /// <para><strong>Sequential Execution Guarantee:</strong></para>
    /// <para>
    /// This loop runs on a single background thread and processes requests one at a time via Channel.
    /// NO TWO REBALANCE EXECUTIONS can ever run in parallel. The Channel ensures serial processing.
    /// </para>
    /// <para><strong>Processing Steps for Each Request:</strong></para>
    /// <list type="number">
    /// <item><description>Read ExecutionRequest from bounded channel (blocks if empty)</description></item>
    /// <item><description>Apply debounce delay (with cancellation check)</description></item>
    /// <item><description>Check cancellation before execution</description></item>
    /// <item><description>Execute rebalance via RebalanceExecutor (CacheState mutation occurs here)</description></item>
    /// <item><description>Handle exceptions and diagnostics</description></item>
    /// <item><description>Dispose request resources and decrement activity counter</description></item>
    /// </list>
    /// <para><strong>Backpressure Effect:</strong></para>
    /// <para>
    /// When this loop processes a request, it frees space in the bounded channel, allowing
    /// any blocked PublishExecutionRequest calls to proceed. This creates natural flow control.
    /// </para>
    /// </remarks>
    private async Task ProcessExecutionRequestsAsync()
    {
        await foreach (var request in _executionChannel.Reader.ReadAllAsync())
        {
            _cacheDiagnostics.RebalanceExecutionStarted();

            var intent = request.Intent;
            var desiredRange = request.DesiredRange;
            var desiredNoRebalanceRange = request.DesiredNoRebalanceRange;
            var cancellationToken = request.CancellationToken;

            try
            {
                // Step 1: Apply debounce delay - allows superseded operations to be cancelled
                // ConfigureAwait(false) ensures continuation on thread pool
                await Task.Delay(_debounceDelay, cancellationToken)
                    .ConfigureAwait(false);

                // Step 2: Check cancellation after debounce - avoid wasted I/O work
                // NOTE: We check IsCancellationRequested explicitly here rather than relying solely on the
                // OperationCanceledException catch below. Task.Delay can complete normally just as cancellation
                // is signalled (a race), so we may reach here with cancellation requested but no exception thrown.
                // This explicit check provides a clean diagnostic event path (RebalanceExecutionCancelled) for
                // that case, separate from the exception-based cancellation path in the catch block below.
                if (cancellationToken.IsCancellationRequested)
                {
                    _cacheDiagnostics.RebalanceExecutionCancelled();
                    continue;
                }

                // Step 3: Execute the rebalance - this is where CacheState mutation occurs
                // This is the ONLY place in the entire system where cache state is written
                await _executor.ExecuteAsync(
                        intent,
                        desiredRange,
                        desiredNoRebalanceRange,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when execution is cancelled or superseded
                _cacheDiagnostics.RebalanceExecutionCancelled();
            }
            catch (Exception ex)
            {
                // Execution failed - record diagnostic
                // Applications MUST monitor RebalanceExecutionFailed events and implement
                // appropriate error handling (logging, alerting, monitoring)
                _cacheDiagnostics.RebalanceExecutionFailed(ex);
            }
            finally
            {
                // Dispose CancellationTokenSource
                request.Dispose();

                // Decrement activity counter for execution
                // This ALWAYS happens after execution completes/cancels/fails
                _activityCounter.DecrementActivity();
            }
        }
    }

    /// <summary>
    /// Disposes the execution controller and releases all managed resources.
    /// Gracefully shuts down the execution loop and waits for completion.
    /// </summary>
    /// <returns>A ValueTask representing the asynchronous disposal operation.</returns>
    /// <remarks>
    /// <para><strong>Disposal Sequence:</strong></para>
    /// <list type="number">
    /// <item><description>Mark as disposed (prevents new execution requests)</description></item>
    /// <item><description>Cancel last execution request (if present)</description></item>
    /// <item><description>Complete the channel writer (signals loop to exit after current operation)</description></item>
    /// <item><description>Wait for execution loop to complete gracefully</description></item>
    /// <item><description>Dispose last execution request resources</description></item>
    /// </list>
    /// <para><strong>Thread Safety:</strong></para>
    /// <para>
    /// This method is thread-safe and idempotent using lock-free Interlocked operations.
    /// Multiple concurrent calls will execute disposal only once.
    /// </para>
    /// <para><strong>Exception Handling:</strong></para>
    /// <para>
    /// Uses best-effort cleanup. Exceptions during loop completion are logged via diagnostics
    /// but do not prevent subsequent cleanup steps.
    /// </para>
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        // Idempotent check using lock-free Interlocked.CompareExchange
        if (Interlocked.CompareExchange(ref _disposeState, 1, 0) != 0)
        {
            return; // Already disposed
        }

        Volatile.Read(ref _lastExecutionRequest)?.Cancel();

        // Complete the channel - signals execution loop to exit after current operation
        _executionChannel.Writer.Complete();

        // Wait for execution loop to complete gracefully
        // No timeout needed per architectural decision: graceful shutdown with cancellation
        try
        {
            await _executionLoopTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Log via diagnostics but don't throw - best-effort disposal
            // Follows "Background Path Exceptions" pattern from AGENTS.md
            _cacheDiagnostics.RebalanceExecutionFailed(ex);
        }

        // Dispose last execution request if present
        Volatile.Read(ref _lastExecutionRequest)?.Dispose();
    }
}
