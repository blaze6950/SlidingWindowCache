using Intervals.NET;
using Intervals.NET.Domain.Abstractions;
using SlidingWindowCache.Core.Rebalance.Intent;
using SlidingWindowCache.Public.Cache;

namespace SlidingWindowCache.Core.Rebalance.Execution;

/// <summary>
/// Abstraction for rebalance execution serialization strategies.
/// Enables pluggable mechanisms for handling execution request queuing and serialization.
/// </summary>
/// <typeparam name="TRange">The type representing the range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <typeparam name="TDomain">The type representing the domain of the ranges.</typeparam>
/// <remarks>
/// <para><strong>Architectural Role - Execution Serialization Strategy:</strong></para>
/// <para>
/// This interface abstracts the mechanism for serializing rebalance execution requests.
/// The concrete implementation determines how execution requests are queued, scheduled,
/// and serialized to ensure single-writer architecture guarantees.
/// </para>
/// <para><strong>Implementations:</strong></para>
/// <list type="bullet">
/// <item><description>
/// <see cref="TaskBasedRebalanceExecutionController{TRange,TData,TDomain}"/> - 
/// Unbounded task chaining for lightweight serialization (default, recommended for most scenarios)
/// </description></item>
/// <item><description>
/// <see cref="ChannelBasedRebalanceExecutionController{TRange,TData,TDomain}"/> - 
/// Bounded channel-based serialization with backpressure support (for high-frequency or resource-constrained scenarios)
/// </description></item>
/// </list>
/// <para><strong>Strategy Selection:</strong></para>
/// <para>
/// The concrete implementation is selected by <see cref="WindowCache{TRange,TData,TDomain}"/>
/// based on <see cref="Public.Configuration.WindowCacheOptions.RebalanceQueueCapacity"/>:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <strong>null</strong> → <see cref="TaskBasedRebalanceExecutionController{TRange,TData,TDomain}"/> 
/// (recommended for most scenarios: standard web APIs, IoT processing, background jobs)
/// </description></item>
/// <item><description>
/// <strong>>= 1</strong> → <see cref="ChannelBasedRebalanceExecutionController{TRange,TData,TDomain}"/> 
/// with specified capacity (for high-frequency updates, streaming data, resource-constrained devices)
/// </description></item>
/// </list>
/// <para><strong>Single-Writer Architecture Guarantee:</strong></para>
/// <para>
/// ALL implementations MUST guarantee that rebalance executions are serialized (no concurrent executions).
/// This ensures the single-writer architecture invariant: only one rebalance execution can mutate
/// CacheState at any given time, eliminating race conditions and ensuring data consistency.
/// </para>
/// <para><strong>Key Responsibilities (All Implementations):</strong></para>
/// <list type="bullet">
/// <item><description>Accept execution requests via <see cref="PublishExecutionRequest"/></description></item>
/// <item><description>Serialize execution (ensure at most one active execution at a time)</description></item>
/// <item><description>Apply debounce delay before execution</description></item>
/// <item><description>Support cancellation of superseded requests</description></item>
/// <item><description>Invoke <see cref="RebalanceExecutor{TRange,TData,TDomain}"/> for cache mutations</description></item>
/// <item><description>Handle disposal gracefully (complete pending work, cleanup resources)</description></item>
/// </list>
/// <para><strong>Execution Context:</strong></para>
/// <para>
/// All implementations run on background threads (ThreadPool). User Path never directly interacts
/// with execution controllers - requests flow through IntentController after validation.
/// </para>
/// </remarks>
internal interface IRebalanceExecutionController<TRange, TData, TDomain> : IAsyncDisposable
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    /// <summary>
    /// Publishes a rebalance execution request to be processed according to the strategy's serialization mechanism.
    /// </summary>
    /// <param name="intent">The rebalance intent containing delivered data and context.</param>
    /// <param name="desiredRange">The target cache range computed by the decision engine.</param>
    /// <param name="desiredNoRebalanceRange">The desired NoRebalanceRange to be set after execution completes.</param>
    /// <param name="loopCancellationToken">Cancellation token from the intent processing loop. Used to unblock asynchronous operations during disposal.</param>
    /// <returns>A ValueTask representing the asynchronous operation. May complete synchronously (task-based strategy) or asynchronously (channel-based strategy with backpressure).</returns>
    /// <remarks>
    /// <para><strong>Execution Context:</strong></para>
    /// <para>
    /// This method is called by IntentController from the background intent processing loop
    /// after multi-stage validation confirms rebalance necessity.
    /// </para>
    /// <para><strong>Strategy-Specific Behavior:</strong></para>
    /// <list type="bullet">
    /// <item><description>
    /// <strong>Task-Based:</strong> Chains execution to previous task, never blocks.
    /// Returns ValueTask.CompletedTask immediately (synchronous completion). Fire-and-forget scheduling.
    /// loopCancellationToken parameter included for API consistency but not used.
    /// </description></item>
    /// <item><description>
    /// <strong>Channel-Based:</strong> Enqueues to bounded channel. Asynchronously awaits WriteAsync if channel is full
    /// (backpressure mechanism - intentional throttling of intent processing loop).
    /// loopCancellationToken enables cancellation of blocking WriteAsync during disposal.
    /// </description></item>
    /// </list>
    /// <para><strong>Cancellation Behavior:</strong></para>
    /// <para>
    /// When loopCancellationToken is cancelled (during disposal), channel-based strategy can exit gracefully
    /// from blocked WriteAsync operations, preventing disposal hangs.
    /// </para>
    /// <para><strong>Thread Safety:</strong></para>
    /// <para>
    /// This method is called from a single-threaded context (IntentController's processing loop),
    /// but implementations must handle disposal races and be safe for concurrent disposal.
    /// </para>
    /// </remarks>
    ValueTask PublishExecutionRequest(
        Intent<TRange, TData, TDomain> intent,
        Range<TRange> desiredRange,
        Range<TRange>? desiredNoRebalanceRange,
        CancellationToken loopCancellationToken);

    /// <summary>
    /// Gets the most recent execution request submitted to the execution controller.
    /// Returns null if no execution request has been submitted yet.
    /// </summary>
    /// <remarks>
    /// <para><strong>Purpose:</strong></para>
    /// <para>
    /// Used for cancellation coordination (cancel previous before enqueuing new),
    /// testing/diagnostics, and tracking current execution state.
    /// </para>
    /// <para><strong>Thread Safety:</strong></para>
    /// <para>
    /// Implementations use volatile reads or Interlocked operations to ensure visibility across threads.
    /// </para>
    /// </remarks>
    ExecutionRequest<TRange, TData, TDomain>? LastExecutionRequest { get; }
}
