using Intervals.NET;
using Intervals.NET.Domain.Abstractions;
using SlidingWindowCache.Core.Rebalance.Intent;
using SlidingWindowCache.Infrastructure.Concurrency;
using SlidingWindowCache.Infrastructure.Instrumentation;

namespace SlidingWindowCache.Core.Rebalance.Execution;

/// <summary>
/// Task-based execution actor responsible for sequential execution of rebalance operations using task chaining for unbounded serialization.
/// This is the SOLE component in the entire system that mutates CacheState when selected as the execution strategy.
/// </summary>
/// <typeparam name="TRange">The type representing the range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <typeparam name="TDomain">The type representing the domain of the ranges.</typeparam>
/// <remarks>
/// <para><strong>Architectural Role - Task-Based Execution Strategy:</strong></para>
/// <para>
/// This implementation uses task continuation chaining to serialize rebalance executions without explicit queue limits.
/// Each new execution request is chained to await the previous execution's completion, ensuring sequential processing
/// with minimal memory overhead. This is the recommended default strategy for most scenarios.
/// </para>
/// <para><strong>Serialization Mechanism - Lock-Free Task Chaining:</strong></para>
/// <para>
/// Uses async method chaining with volatile write semantics to chain execution tasks. Each new request creates an
/// async method that awaits the previous task's completion before starting its own execution:
/// </para>
/// <code>
/// // Conceptual model (simplified):
/// var previousTask = _currentExecutionTask;
/// var newTask = ChainExecutionAsync(previousTask, newRequest);
/// Volatile.Write(ref _currentExecutionTask, newTask);
/// </code>
/// <para>
/// The task chain reference uses volatile write for visibility (single-writer context - only intent processing loop writes).
/// No locks are needed because this is a single-threaded writer scenario. Actual execution happens asynchronously
/// on the ThreadPool, ensuring no blocking of the intent processing loop.
/// </para>
/// <para><strong>Single-Writer Architecture Guarantee:</strong></para>
/// <para>
/// The task chaining mechanism ensures that NO TWO REBALANCE EXECUTIONS ever run concurrently.
/// Each task awaits the previous task's completion before starting, guaranteeing serialized cache mutations
/// and eliminating write-write race conditions.
/// </para>
/// <para><strong>Cancellation for Short-Circuit Optimization:</strong></para>
/// <para>
/// Each execution request carries a CancellationToken. When a new request is published, the previous
/// request's CancellationToken is cancelled. Cancellation is checked:
/// </para>
/// <list type="bullet">
/// <item><description>After debounce delay (before I/O) - avoid fetching obsolete data</description></item>
/// <item><description>After data fetch (before mutation) - avoid applying obsolete results</description></item>
/// <item><description>During I/O operations - exit early from long-running fetches</description></item>
/// </list>
/// <para><strong>Fire-and-Forget Execution Model:</strong></para>
/// <para>
/// PublishExecutionRequest returns immediately (ValueTask.CompletedTask) after chaining the task. The execution happens
/// asynchronously on the ThreadPool. Exceptions are captured and reported via diagnostics (following the "Background Path
/// Exceptions" pattern from AGENTS.md).
/// </para>
/// <para><strong>Trade-offs:</strong></para>
/// <list type="bullet">
/// <item><description>✅ Lightweight (minimal memory overhead - single Task reference, no lock object)</description></item>
/// <item><description>✅ Simple implementation (fewer moving parts than channel-based)</description></item>
/// <item><description>✅ No backpressure overhead (intent processing never blocks)</description></item>
/// <item><description>✅ Lock-free (volatile write for single-writer pattern)</description></item>
/// <item><description>✅ Sufficient for typical workloads</description></item>
/// <item><description>⚠️ Unbounded (can accumulate task chain under extreme sustained load)</description></item>
/// </list>
/// <para><strong>When to Use:</strong></para>
/// <para>
/// Use this strategy (default, recommended) when:
/// </para>
/// <list type="bullet">
/// <item><description>Standard web APIs with typical request patterns</description></item>
/// <item><description>IoT sensor processing with sequential access</description></item>
/// <item><description>Background batch processing</description></item>
/// <item><description>Any scenario where request bursts are temporary</description></item>
/// <item><description>Memory is not severely constrained</description></item>
/// </list>
/// <para><strong>Configuration:</strong></para>
/// <para>
/// Selected automatically when <see cref="Public.Configuration.WindowCacheOptions.RebalanceQueueCapacity"/> 
/// is null (default). This is the recommended default for most scenarios.
/// </para>
/// <para>See also: <see cref="ChannelBasedRebalanceExecutionController{TRange,TData,TDomain}"/> for bounded alternative with backpressure</para>
/// </remarks>
internal sealed class TaskBasedRebalanceExecutionController<TRange, TData, TDomain> 
    : IRebalanceExecutionController<TRange, TData, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private readonly RebalanceExecutor<TRange, TData, TDomain> _executor;
    private readonly TimeSpan _debounceDelay;
    private readonly ICacheDiagnostics _cacheDiagnostics;

    // Activity counter for tracking active operations
    private readonly AsyncActivityCounter _activityCounter;

    // Task chaining state (volatile write for single-writer pattern)
    private Task _currentExecutionTask = Task.CompletedTask;

    // Disposal state tracking (lock-free using Interlocked)
    // 0 = not disposed, 1 = disposed
    private int _disposeState;

    /// <summary>
    /// Stores the most recent execution request submitted to the execution controller.
    /// Used for tracking the current execution state, cancellation coordination, and testing/diagnostic purposes.
    /// </summary>
    private ExecutionRequest<TRange, TData, TDomain>? _lastExecutionRequest;

    /// <summary>
    /// Initializes a new instance of the <see cref="TaskBasedRebalanceExecutionController{TRange,TData,TDomain}"/> class.
    /// </summary>
    /// <param name="executor">The executor for performing rebalance operations.</param>
    /// <param name="debounceDelay">The debounce delay before executing rebalance.</param>
    /// <param name="cacheDiagnostics">The diagnostics interface for recording rebalance-related metrics and events.</param>
    /// <param name="activityCounter">Activity counter for tracking active operations.</param>
    /// <remarks>
    /// <para><strong>Initialization:</strong></para>
    /// <para>
    /// Initializes the task chain with a completed task. The first execution request will chain to this
    /// completed task, starting the execution chain. All subsequent requests chain to the previous execution.
    /// </para>
    /// <para><strong>Execution Model:</strong></para>
    /// <para>
    /// Unlike channel-based approach, there is no background loop started at construction. Executions are
    /// scheduled on-demand via task chaining when PublishExecutionRequest is called.
    /// </para>
    /// </remarks>
    public TaskBasedRebalanceExecutionController(
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
    }

    /// <summary>
    /// Gets the most recent execution request submitted to the execution controller.
    /// Returns null if no execution request has been submitted yet.
    /// </summary>
    public ExecutionRequest<TRange, TData, TDomain>? LastExecutionRequest => 
        Volatile.Read(ref _lastExecutionRequest);

    /// <summary>
    /// Publishes a rebalance execution request by chaining it to the previous execution task.
    /// </summary>
    /// <param name="intent">The rebalance intent containing delivered data and context.</param>
    /// <param name="desiredRange">The target cache range computed by the decision engine.</param>
    /// <param name="desiredNoRebalanceRange">The desired NoRebalanceRange to be set after execution completes.</param>
    /// <returns>A ValueTask that completes synchronously (fire-and-forget execution model).</returns>
    /// <remarks>
    /// <para><strong>Task Chaining Behavior:</strong></para>
    /// <para>
    /// This method chains the new execution request to the current execution task using volatile write for visibility.
    /// The chaining operation is lock-free (single-writer context - only intent processing loop calls this method).
    /// Returns immediately after chaining - actual execution happens asynchronously on the ThreadPool.
    /// </para>
    /// <para><strong>Cancellation Coordination:</strong></para>
    /// <para>
    /// Before chaining, this method cancels the previous execution request's CancellationToken (if present).
    /// This allows the previous execution to exit early if it's still in the debounce delay or I/O phase.
    /// </para>
    /// <para><strong>Fire-and-Forget Execution:</strong></para>
    /// <para>
    /// Returns ValueTask.CompletedTask immediately (synchronous completion). The execution happens asynchronously
    /// on the ThreadPool. Exceptions during execution are captured and reported via diagnostics.
    /// </para>
    /// <para><strong>Execution Context:</strong></para>
    /// <para>
    /// Called by IntentController from the background intent processing loop (single-threaded context)
    /// after multi-stage validation confirms rebalance necessity. Never blocks - returns immediately.
    /// </para>
    /// </remarks>
    public ValueTask PublishExecutionRequest(
        Intent<TRange, TData, TDomain> intent, 
        Range<TRange> desiredRange,
        Range<TRange>? desiredNoRebalanceRange)
    {
        // Check disposal state using Volatile.Read (lock-free)
        if (Volatile.Read(ref _disposeState) != 0)
        {
            throw new ObjectDisposedException(
                nameof(TaskBasedRebalanceExecutionController<TRange, TData, TDomain>),
                "Cannot publish execution request to a disposed controller.");
        }

        // Increment activity counter for new execution request
        _activityCounter.IncrementActivity();

        // Cancel previous execution request (if exists)
        var previousRequest = Volatile.Read(ref _lastExecutionRequest);
        previousRequest?.Cancel();

        // Create CancellationTokenSource for this execution request
        var cancellationTokenSource = new CancellationTokenSource();

        // Create execution request message
        var request = new ExecutionRequest<TRange, TData, TDomain>(
            intent,
            desiredRange,
            desiredNoRebalanceRange,
            cancellationTokenSource
        );
        
        // Store as last request (for cancellation coordination and diagnostics)
        Volatile.Write(ref _lastExecutionRequest, request);

        // Chain execution to previous task (lock-free using volatile write - single-writer context)
        // Read current task, create new chained task, and update atomically
        var previousTask = _currentExecutionTask;
        var newTask = ChainExecutionAsync(previousTask, request);
        Volatile.Write(ref _currentExecutionTask, newTask);

        // Return immediately - fire-and-forget execution model
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Chains a new execution request to await the previous task's completion before executing.
    /// This ensures sequential execution (single-writer architecture guarantee).
    /// </summary>
    /// <param name="previousTask">The previous execution task to await before starting this execution.</param>
    /// <param name="request">The execution request to process after the previous task completes.</param>
    /// <returns>A Task representing the chained execution operation.</returns>
    /// <remarks>
    /// <para><strong>Sequential Execution:</strong></para>
    /// <para>
    /// This method creates the task chain that ensures NO TWO REBALANCE EXECUTIONS run concurrently.
    /// Each execution awaits the previous execution's completion before starting, guaranteeing serialized
    /// cache mutations and eliminating write-write race conditions.
    /// </para>
    /// <para><strong>Exception Handling:</strong></para>
    /// <para>
    /// All exceptions from both the previous task and the current execution are captured and reported
    /// via diagnostics. This prevents unobserved task exceptions and follows the "Background Path Exceptions"
    /// pattern from AGENTS.md.
    /// </para>
    /// </remarks>
    private async Task ChainExecutionAsync(Task previousTask, ExecutionRequest<TRange, TData, TDomain> request)
    {
        try
        {
            // Await previous task completion (enforces sequential execution)
            await previousTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Previous task failed - log but continue with current execution
            // (Decision: each execution is independent; previous failure shouldn't block current)
            _cacheDiagnostics.RebalanceExecutionFailed(ex);
        }

        try
        {
            // Execute current request
            await ExecuteRequestAsync(request).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Current execution failed - log it
            // ExecuteRequestAsync already handles exceptions internally, but catch here for safety
            _cacheDiagnostics.RebalanceExecutionFailed(ex);
        }
    }

    /// <summary>
    /// Executes a rebalance request with debounce delay and cancellation support.
    /// This is where the actual cache mutation occurs (via RebalanceExecutor).
    /// </summary>
    /// <param name="request">The execution request containing intent, desired range, and cancellation token.</param>
    /// <remarks>
    /// <para><strong>Execution Steps:</strong></para>
    /// <list type="number">
    /// <item><description>Apply debounce delay (with cancellation check)</description></item>
    /// <item><description>Check cancellation after debounce (before I/O)</description></item>
    /// <item><description>Execute rebalance via RebalanceExecutor (CacheState mutation occurs here)</description></item>
    /// <item><description>Handle exceptions and diagnostics</description></item>
    /// <item><description>Cleanup: dispose request and decrement activity counter</description></item>
    /// </list>
    /// <para><strong>Thread Safety:</strong></para>
    /// <para>
    /// This method runs sequentially due to task chaining (one execution at a time).
    /// The single-writer architecture guarantee is maintained through serialization via the task chain.
    /// </para>
    /// <para><strong>Exception Handling:</strong></para>
    /// <para>
    /// All exceptions are captured and reported via diagnostics. This follows the "Background Path Exceptions"
    /// pattern from AGENTS.md: background exceptions must not crash the application.
    /// </para>
    /// </remarks>
    private async Task ExecuteRequestAsync(ExecutionRequest<TRange, TData, TDomain> request)
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
                return;
            }

            // Step 3: Execute the rebalance - this is where CacheState mutation occurs
            // This is the ONLY place in the entire system where cache state is written
            // (when this strategy is active)
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

    /// <summary>
    /// Disposes the execution controller and releases all managed resources.
    /// Waits for the current execution task chain to complete gracefully.
    /// </summary>
    /// <returns>A ValueTask representing the asynchronous disposal operation.</returns>
    /// <remarks>
    /// <para><strong>Disposal Sequence:</strong></para>
    /// <list type="number">
    /// <item><description>Mark as disposed (prevents new execution requests)</description></item>
    /// <item><description>Cancel last execution request (if present)</description></item>
    /// <item><description>Capture current task chain reference (volatile read)</description></item>
    /// <item><description>Wait for task chain to complete gracefully</description></item>
    /// <item><description>Dispose last execution request resources</description></item>
    /// </list>
    /// <para><strong>Thread Safety:</strong></para>
    /// <para>
    /// This method is thread-safe and idempotent using lock-free Interlocked operations.
    /// Multiple concurrent calls will execute disposal only once.
    /// </para>
    /// <para><strong>Graceful Shutdown:</strong></para>
    /// <para>
    /// No timeout is enforced per architectural decision. Disposal waits for the current execution
    /// to complete naturally (typically milliseconds). Cancellation signals early exit.
    /// </para>
    /// <para><strong>Exception Handling:</strong></para>
    /// <para>
    /// Uses best-effort cleanup. Exceptions during task completion are logged via diagnostics
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

        // Cancel last execution request (signals early exit)
        _lastExecutionRequest?.Cancel();

        // Capture current task chain reference (volatile read - no lock needed)
        var currentTask = Volatile.Read(ref _currentExecutionTask);

        // Wait for current task chain to complete gracefully
        // No timeout needed per architectural decision: graceful shutdown with cancellation
        try
        {
            await currentTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Log via diagnostics but don't throw - best-effort disposal
            // Follows "Background Path Exceptions" pattern from AGENTS.md
            _cacheDiagnostics.RebalanceExecutionFailed(ex);
        }

        // Dispose last execution request if present
        _lastExecutionRequest?.Dispose();
    }
}
