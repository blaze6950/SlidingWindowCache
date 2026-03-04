using System.Threading.Channels;
using Intervals.NET;
using Intervals.NET.Domain.Abstractions;
using Intervals.NET.Caching.Core.Rebalance.Intent;
using Intervals.NET.Caching.Core.State;
using Intervals.NET.Caching.Infrastructure.Concurrency;
using Intervals.NET.Caching.Public.Instrumentation;

namespace Intervals.NET.Caching.Core.Rebalance.Execution;

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
///     await ExecuteRequestCoreAsync(request);  // One at a time
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
    : RebalanceExecutionControllerBase<TRange, TData, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private readonly Channel<ExecutionRequest<TRange, TData, TDomain>> _executionChannel;
    private readonly Task _executionLoopTask;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelBasedRebalanceExecutionController{TRange,TData,TDomain}"/> class.
    /// </summary>
    /// <param name="executor">The executor for performing rebalance operations.</param>
    /// <param name="optionsHolder">
    ///     Shared holder for the current runtime options snapshot. The controller reads
    ///     <see cref="RuntimeCacheOptionsHolder.Current"/> at the start of each execution to pick up
    ///     the latest <c>DebounceDelay</c> published via <c>IWindowCache.UpdateRuntimeOptions</c>.
    /// </param>
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
        RuntimeCacheOptionsHolder optionsHolder,
        ICacheDiagnostics cacheDiagnostics,
        AsyncActivityCounter activityCounter,
        int capacity
    ) : base(executor, optionsHolder, cacheDiagnostics, activityCounter)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity),
                "Capacity must be greater than or equal to 1.");
        }

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
    public override async ValueTask PublishExecutionRequest(
        Intent<TRange, TData, TDomain> intent,
        Range<TRange> desiredRange,
        Range<TRange>? desiredNoRebalanceRange,
        CancellationToken loopCancellationToken)
    {
        // Check disposal state
        if (IsDisposed)
        {
            throw new ObjectDisposedException(
                nameof(ChannelBasedRebalanceExecutionController<TRange, TData, TDomain>),
                "Cannot publish execution request to a disposed controller.");
        }

        // Increment activity counter for new execution request
        ActivityCounter.IncrementActivity();

        // Create CancellationTokenSource for this execution request
        var cancellationTokenSource = new CancellationTokenSource();

        // Create execution request message
        var request = new ExecutionRequest<TRange, TData, TDomain>(
            intent,
            desiredRange,
            desiredNoRebalanceRange,
            cancellationTokenSource
        );
        StoreLastExecutionRequest(request);

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
            ActivityCounter.DecrementActivity();
        }
        catch (Exception ex)
        {
            // If write fails (e.g., channel completed during disposal), clean up and report
            request.Dispose();
            ActivityCounter.DecrementActivity();
            CacheDiagnostics.RebalanceExecutionFailed(ex);
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
            await ExecuteRequestCoreAsync(request).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    private protected override async ValueTask DisposeAsyncCore()
    {
        // Complete the channel - signals execution loop to exit after current operation
        _executionChannel.Writer.Complete();

        // Wait for execution loop to complete gracefully
        // No timeout needed per architectural decision: graceful shutdown with cancellation
        await _executionLoopTask.ConfigureAwait(false);
    }
}
