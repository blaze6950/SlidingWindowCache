using Intervals.NET;
using Intervals.NET.Domain.Abstractions;
using SlidingWindowCache.Core.Rebalance.Intent;
using SlidingWindowCache.Core.State;
using SlidingWindowCache.Infrastructure.Concurrency;
using SlidingWindowCache.Public.Instrumentation;

namespace SlidingWindowCache.Core.Rebalance.Execution;

/// <summary>
/// Abstract base class providing the shared execution pipeline for rebalance execution controllers.
/// </summary>
/// <typeparam name="TRange">The type representing the range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <typeparam name="TDomain">The type representing the domain of the ranges.</typeparam>
/// <remarks>
/// <para><strong>Purpose:</strong></para>
/// <para>
/// Centralizes the logic that is identical across all
/// <see cref="IRebalanceExecutionController{TRange,TData,TDomain}"/> implementations:
/// shared fields, the <see cref="LastExecutionRequest"/> property, the per-request execution
/// pipeline (debounce → cancellation check → executor call → diagnostics → cleanup), and the
/// disposal guard. Each concrete subclass provides only the serialization mechanism
/// (<see cref="PublishExecutionRequest"/>) and the strategy-specific teardown
/// (<see cref="DisposeAsyncCore"/>).
/// </para>
/// <para><strong>Shared Execution Pipeline:</strong></para>
/// <para>
/// <see cref="ExecuteRequestCoreAsync"/> contains the canonical execution body:
/// <list type="number">
/// <item><description>Signal <c>RebalanceExecutionStarted</c> diagnostic</description></item>
/// <item><description>Snapshot <c>DebounceDelay</c> from the options holder ("next cycle" semantics)</description></item>
/// <item><description>Await <c>Task.Delay(debounceDelay, cancellationToken)</c></description></item>
/// <item><description>Check <c>IsCancellationRequested</c> after debounce (Task.Delay race guard)</description></item>
/// <item><description>Call <see cref="RebalanceExecutor{TRange,TData,TDomain}.ExecuteAsync"/></description></item>
/// <item><description>Catch <c>OperationCanceledException</c> → <c>RebalanceExecutionCancelled</c></description></item>
/// <item><description>Catch all other exceptions → <c>RebalanceExecutionFailed</c></description></item>
/// <item><description><c>finally</c>: dispose the request, decrement the activity counter</description></item>
/// </list>
/// </para>
/// <para><strong>Disposal Protocol:</strong></para>
/// <para>
/// <see cref="DisposeAsync"/> handles the idempotent guard (Interlocked) and cancels the last
/// execution request. It then delegates to <see cref="DisposeAsyncCore"/> for strategy-specific
/// teardown (awaiting the task chain vs. completing the channel), and finally disposes the last
/// execution request.
/// </para>
/// </remarks>
internal abstract class RebalanceExecutionControllerBase<TRange, TData, TDomain>
    : IRebalanceExecutionController<TRange, TData, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    /// <summary>The executor that performs the actual cache mutation.</summary>
    private protected readonly RebalanceExecutor<TRange, TData, TDomain> Executor;

    /// <summary>Shared holder for the current runtime options snapshot.</summary>
    private protected readonly RuntimeCacheOptionsHolder OptionsHolder;

    /// <summary>Diagnostics interface for recording rebalance events.</summary>
    private protected readonly ICacheDiagnostics CacheDiagnostics;

    /// <summary>Activity counter for tracking active operations.</summary>
    private protected readonly AsyncActivityCounter ActivityCounter;

    // Disposal state: 0 = not disposed, 1 = disposed (lock-free via Interlocked)
    private int _disposeState;

    /// <summary>Most recent execution request; updated via Volatile.Write.</summary>
    private ExecutionRequest<TRange, TData, TDomain>? _lastExecutionRequest;

    /// <summary>
    /// Initializes the shared fields.
    /// </summary>
    private protected RebalanceExecutionControllerBase(
        RebalanceExecutor<TRange, TData, TDomain> executor,
        RuntimeCacheOptionsHolder optionsHolder,
        ICacheDiagnostics cacheDiagnostics,
        AsyncActivityCounter activityCounter)
    {
        Executor = executor;
        OptionsHolder = optionsHolder;
        CacheDiagnostics = cacheDiagnostics;
        ActivityCounter = activityCounter;
    }

    /// <inheritdoc/>
    public ExecutionRequest<TRange, TData, TDomain>? LastExecutionRequest =>
        Volatile.Read(ref _lastExecutionRequest);

    /// <summary>
    /// Sets the last execution request atomically (release fence).
    /// </summary>
    private protected void StoreLastExecutionRequest(ExecutionRequest<TRange, TData, TDomain> request) =>
        Volatile.Write(ref _lastExecutionRequest, request);

    /// <inheritdoc/>
    public abstract ValueTask PublishExecutionRequest(
        Intent<TRange, TData, TDomain> intent,
        Range<TRange> desiredRange,
        Range<TRange>? desiredNoRebalanceRange,
        CancellationToken loopCancellationToken);

    /// <summary>
    /// Executes a single rebalance request: debounce, cancellation check, executor call, diagnostics, cleanup.
    /// This is the canonical execution pipeline shared by all strategy implementations.
    /// </summary>
    /// <remarks>
    /// <para><strong>Execution Steps:</strong></para>
    /// <list type="number">
    /// <item><description>Signal <c>RebalanceExecutionStarted</c></description></item>
    /// <item><description>Snapshot <c>DebounceDelay</c> from holder at execution time ("next cycle" semantics)</description></item>
    /// <item><description>Await <c>Task.Delay(debounceDelay, cancellationToken)</c></description></item>
    /// <item><description>Explicit <c>IsCancellationRequested</c> check after debounce (Task.Delay race guard)</description></item>
    /// <item><description>Call <c>RebalanceExecutor.ExecuteAsync</c> — the sole point of CacheState mutation</description></item>
    /// <item><description>Catch <c>OperationCanceledException</c> → signal <c>RebalanceExecutionCancelled</c></description></item>
    /// <item><description>Catch other exceptions → signal <c>RebalanceExecutionFailed</c></description></item>
    /// <item><description><c>finally</c>: dispose request, decrement activity counter</description></item>
    /// </list>
    /// </remarks>
    private protected async Task ExecuteRequestCoreAsync(ExecutionRequest<TRange, TData, TDomain> request)
    {
        CacheDiagnostics.RebalanceExecutionStarted();

        var intent = request.Intent;
        var desiredRange = request.DesiredRange;
        var desiredNoRebalanceRange = request.DesiredNoRebalanceRange;
        var cancellationToken = request.CancellationToken;

        // Snapshot DebounceDelay from the options holder at execution time.
        // This picks up any runtime update published via IWindowCache.UpdateRuntimeOptions
        // since this execution request was enqueued ("next cycle" semantics).
        var debounceDelay = OptionsHolder.Current.DebounceDelay;

        try
        {
            // Step 1: Apply debounce delay - allows superseded operations to be cancelled
            // ConfigureAwait(false) ensures continuation on thread pool
            await Task.Delay(debounceDelay, cancellationToken)
                .ConfigureAwait(false);

            // Step 2: Check cancellation after debounce - avoid wasted I/O work
            // NOTE: We check IsCancellationRequested explicitly here rather than relying solely on the
            // OperationCanceledException catch below. Task.Delay can complete normally just as cancellation
            // is signalled (a race), so we may reach here with cancellation requested but no exception thrown.
            // This explicit check provides a clean diagnostic event path (RebalanceExecutionCancelled) for
            // that case, separate from the exception-based cancellation path in the catch block below.
            if (cancellationToken.IsCancellationRequested)
            {
                CacheDiagnostics.RebalanceExecutionCancelled();
                return;
            }

            // Step 3: Execute the rebalance - this is where CacheState mutation occurs
            // This is the ONLY place in the entire system where cache state is written
            // (when this strategy is active)
            await Executor.ExecuteAsync(
                    intent,
                    desiredRange,
                    desiredNoRebalanceRange,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when execution is cancelled or superseded
            CacheDiagnostics.RebalanceExecutionCancelled();
        }
        catch (Exception ex)
        {
            // Execution failed - record diagnostic
            // Applications MUST monitor RebalanceExecutionFailed events and implement
            // appropriate error handling (logging, alerting, monitoring)
            CacheDiagnostics.RebalanceExecutionFailed(ex);
        }
        finally
        {
            // Dispose CancellationTokenSource
            request.Dispose();

            // Decrement activity counter for execution
            // This ALWAYS happens after execution completes/cancels/fails
            ActivityCounter.DecrementActivity();
        }
    }

    /// <summary>
    /// Performs strategy-specific teardown during disposal.
    /// Called by <see cref="DisposeAsync"/> after the disposal guard has fired and the last request has been cancelled.
    /// </summary>
    /// <remarks>
    /// Implementations should stop the serialization mechanism here:
    /// <list type="bullet">
    /// <item><description><strong>Task-based:</strong> await the current task chain</description></item>
    /// <item><description><strong>Channel-based:</strong> complete the channel writer and await the loop task</description></item>
    /// </list>
    /// </remarks>
    private protected abstract ValueTask DisposeAsyncCore();

    /// <summary>
    /// Returns whether the controller has been disposed.
    /// Subclasses use this to guard <see cref="PublishExecutionRequest"/>.
    /// </summary>
    private protected bool IsDisposed => Volatile.Read(ref _disposeState) != 0;

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        // Idempotent guard using lock-free Interlocked.CompareExchange
        if (Interlocked.CompareExchange(ref _disposeState, 1, 0) != 0)
        {
            return; // Already disposed
        }

        // Cancel last execution request (signals early exit from debounce / I/O)
        Volatile.Read(ref _lastExecutionRequest)?.Cancel();

        // Strategy-specific teardown (await task chain / complete channel + await loop)
        try
        {
            await DisposeAsyncCore().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Log via diagnostics but don't throw - best-effort disposal
            // Follows "Background Path Exceptions" pattern from AGENTS.md
            CacheDiagnostics.RebalanceExecutionFailed(ex);
        }

        // Dispose last execution request resources
        Volatile.Read(ref _lastExecutionRequest)?.Dispose();
    }
}
