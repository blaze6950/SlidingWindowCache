using System.Threading.Channels;
using Intervals.NET;
using Intervals.NET.Domain.Abstractions;
using SlidingWindowCache.Core.Rebalance.Intent;
using SlidingWindowCache.Infrastructure.Concurrency;
using SlidingWindowCache.Infrastructure.Instrumentation;

namespace SlidingWindowCache.Core.Rebalance.Execution;

/// <summary>
/// Execution request message sent from IntentController to RebalanceExecutionController.
/// Contains all information needed to execute a rebalance operation.
/// </summary>
/// <typeparam name="TRange">The type representing the range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <typeparam name="TDomain">The type representing the domain of the ranges.</typeparam>
internal record ExecutionRequest<TRange, TData, TDomain>(
    Intent<TRange, TData, TDomain> Intent,
    Range<TRange> DesiredRange,
    Range<TRange>? DesiredNoRebalanceRange,
    CancellationTokenSource CancellationTokenSource
)
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    public void Cancel()
    {
        try
        {
            CancellationTokenSource.Cancel();
        }
        catch
        {
            // Ignore disposal errors
        }
    }

    public void Dispose()
    {
        try
        {
            CancellationTokenSource.Dispose();
        }
        catch
        {
            // Ignore disposal errors
        }
    }
}

/// <summary>
/// Execution actor responsible for sequential, single-threaded execution of rebalance operations.
/// This is the SOLE component in the entire system that mutates CacheState.
/// </summary>
/// <typeparam name="TRange">The type representing the range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <typeparam name="TDomain">The type representing the domain of the ranges.</typeparam>
/// <remarks>
/// <para><strong>Architectural Role - Execution Actor (Single-Threaded):</strong></para>
/// <para>
/// This actor runs on a single background thread and processes execution requests sequentially via Channel.
/// It is the ONLY component in the system that writes to CacheState, ensuring:
/// - No concurrent cache mutations
/// - No race conditions on cache state
/// - Predictable execution ordering
/// - Safe cancellation before mutations occur
/// </para>
/// <para><strong>Single-Threaded Guarantee via Channel:</strong></para>
/// <para>
/// By processing execution requests sequentially from a Channel, this actor guarantees that
/// NO TWO REBALANCE EXECUTIONS ever run in parallel. The Channel acts as a natural queue,
/// ensuring serial execution without explicit locks or semaphores.
/// </para>
/// <para><strong>Cancellation for Short-Circuit Optimization:</strong></para>
/// <para>
/// Each execution request carries a CancellationToken. The actor checks cancellation:
/// <list type="bullet">
/// <item><description>After debounce delay (before I/O) - avoid fetching obsolete data</description></item>
/// <item><description>After data fetch (before mutation) - avoid applying obsolete results</description></item>
/// <item><description>During I/O operations - exit early from long-running fetches</description></item>
/// </list>
/// This allows fast exit from superseded operations, especially effective with debounce delay.
/// </para>
/// </remarks>
internal sealed class RebalanceExecutionController<TRange, TData, TDomain>
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

    /// <summary>
    /// Todo
    /// </summary>
    private ExecutionRequest<TRange, TData, TDomain>? _lastExecutionRequest;

    /// <summary>
    /// Initializes a new instance of the <see cref="RebalanceExecutionController{TRange,TData,TDomain}"/> class.
    /// </summary>
    /// <param name="executor">The executor for performing rebalance operations.</param>
    /// <param name="debounceDelay">The debounce delay before executing rebalance.</param>
    /// <param name="cacheDiagnostics">The diagnostics interface for recording rebalance-related metrics and events.</param>
    /// <param name="activityCounter">Activity counter for tracking active operations.</param>
    /// <remarks>
    /// The execution loop starts immediately upon construction and runs for the lifetime of the cache instance.
    /// This actor guarantees single-threaded execution of all cache mutations.
    /// </remarks>
    public RebalanceExecutionController(
        RebalanceExecutor<TRange, TData, TDomain> executor,
        TimeSpan debounceDelay,
        ICacheDiagnostics cacheDiagnostics,
        AsyncActivityCounter activityCounter
    )
    {
        _executor = executor;
        _debounceDelay = debounceDelay;
        _cacheDiagnostics = cacheDiagnostics;
        _activityCounter = activityCounter;
        // Initialize unbounded channel with single reader/writer semantics
        // Unbounded prevents backpressure on IntentController actor
        // SingleReader: only execution loop reads; SingleWriter: only IntentController writes
        _executionChannel = Channel.CreateUnbounded<ExecutionRequest<TRange, TData, TDomain>>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true, // Only IntentController actor enqueues execution requests
                AllowSynchronousContinuations = false
            });
        // Start execution loop immediately - runs for cache lifetime
        _executionLoopTask = ProcessExecutionRequestsAsync();
    }

    /// <summary>
    /// Todo
    /// </summary>
    internal ExecutionRequest<TRange, TData, TDomain>? LastExecutionRequest => _lastExecutionRequest;

    public void PublishExecutionRequest(Intent<TRange, TData, TDomain> intent, Range<TRange> desiredRange,
        Range<TRange>? desiredNoRebalanceRange)
    {
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
        Interlocked.Exchange(ref _lastExecutionRequest, request);

        // Enqueue execution request to channel - will be processed by execution loop sequentially
        // This is thread-safe and non-blocking due to Channel's single-writer semantics
        _executionChannel.Writer.TryWrite(request);
    }

    /// <summary>
    /// Execution actor loop that processes requests sequentially.
    /// This is the SOLE mutator of CacheState in the entire system.
    /// </summary>
    /// <remarks>
    /// <para><strong>Sequential Execution Guarantee:</strong></para>
    /// <para>
    /// This loop runs on a single background thread and processes requests one at a time via Channel.
    /// NO TWO REBALANCE EXECUTIONS can ever run in parallel. The Channel ensures serial processing.
    /// </para>
    /// <para><strong>Processing Steps for Each Request:</strong></para>
    /// <list type="number">
    /// <item><description>Read ExecutionRequest from channel</description></item>
    /// <item><description>Apply debounce delay (with cancellation check)</description></item>
    /// <item><description>Check cancellation before execution</description></item>
    /// <item><description>Execute rebalance via RebalanceExecutor (CacheState mutation occurs here)</description></item>
    /// <item><description>Signal TaskCompletionSource completion (success/canceled/faulted)</description></item>
    /// <item><description>Handle exceptions and diagnostics</description></item>
    /// </list>
    /// </remarks>
    private async Task ProcessExecutionRequestsAsync()
    {
        await foreach (var request in _executionChannel.Reader.ReadAllAsync())
        {
            _cacheDiagnostics.RebalanceExecutionStarted();

            var (intent, desiredRange, desiredNoRebalanceRange, cancellationTokenSource) = request;
            var cancellationToken = cancellationTokenSource.Token;

            try
            {
                // Step 1: Apply debounce delay - allows superseded operations to be cancelled
                // ConfigureAwait(false) ensures continuation on thread pool
                await Task.Delay(_debounceDelay, cancellationToken)
                    .ConfigureAwait(false);

                // Step 2: Check cancellation after debounce - avoid wasted I/O work
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
}