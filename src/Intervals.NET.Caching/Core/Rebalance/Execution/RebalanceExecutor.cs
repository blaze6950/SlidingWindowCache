using Intervals.NET;
using Intervals.NET.Domain.Abstractions;
using Intervals.NET.Caching.Core.Rebalance.Intent;
using Intervals.NET.Caching.Core.State;
using Intervals.NET.Caching.Public.Instrumentation;

namespace Intervals.NET.Caching.Core.Rebalance.Execution;

/// <summary>
/// Executes rebalance operations by fetching missing data, merging with existing cache,
/// and trimming to the desired range. This is the sole component responsible for cache normalization.
/// Called exclusively by RebalanceExecutionController actor which guarantees single-threaded execution.
/// </summary>
/// <typeparam name="TRange">The type representing the range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <typeparam name="TDomain">The type representing the domain of the ranges.</typeparam>
/// <remarks>
/// <para><strong>Execution Context:</strong> Background / ThreadPool (via RebalanceExecutionController actor)</para>
/// <para><strong>Characteristics:</strong> Asynchronous, cancellable, heavyweight</para>
/// <para><strong>Responsibility:</strong> Cache normalization (expand, trim, recompute NoRebalanceRange)</para>
/// <para><strong>Execution Serialization:</strong> Provided by the active <c>IRebalanceExecutionController</c> actor, which ensures
/// only one rebalance execution runs at a time — either via task chaining (<c>TaskBasedRebalanceExecutionController</c>, default)
/// or via bounded channel (<c>ChannelBasedRebalanceExecutionController</c>).
/// CancellationToken provides early exit signaling. WebAssembly-compatible, async, and lightweight.</para>
/// </remarks>
internal sealed class RebalanceExecutor<TRange, TData, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private readonly CacheState<TRange, TData, TDomain> _state;
    private readonly CacheDataExtensionService<TRange, TData, TDomain> _cacheExtensionService;
    private readonly ICacheDiagnostics _cacheDiagnostics;

    public RebalanceExecutor(
        CacheState<TRange, TData, TDomain> state,
        CacheDataExtensionService<TRange, TData, TDomain> cacheExtensionService,
        ICacheDiagnostics cacheDiagnostics
    )
    {
        _state = state;
        _cacheExtensionService = cacheExtensionService;
        _cacheDiagnostics = cacheDiagnostics;
    }

    /// <summary>
    /// Executes rebalance by normalizing the cache to the desired range.
    /// Called exclusively by RebalanceExecutionController actor (single-threaded).
    /// This is the ONLY component that mutates cache state (single-writer architecture).
    /// </summary>
    /// <param name="intent">The intent with data that was actually assembled in UserPath and the requested range.</param>
    /// <param name="desiredRange">The target cache range to normalize to.</param>
    /// <param name="desiredNoRebalanceRange">The no-rebalance range for the target cache state.</param>
    /// <param name="cancellationToken">Cancellation token to support cancellation at all stages.</param>
    /// <returns>A task representing the asynchronous rebalance operation.</returns>
    /// <remarks>
    /// <para>
    /// This executor is the sole writer of all cache state including:
    /// <list type="bullet">
    /// <item><description>Cache.Rematerialize (cache data and range)</description></item>
    /// <item><description>LastRequested field</description></item>
    /// <item><description>NoRebalanceRange field</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// The delivered data from the intent is used as the authoritative base source,
    /// avoiding duplicate fetches and ensuring consistency with what the user received.
    /// </para>
    /// <para>
    /// This executor is intentionally simple - no analytical decisions, no necessity checks.
    /// Decision logic has been validated by DecisionEngine before invocation.
    /// </para>
    /// <para><strong>Serialization:</strong> The active <c>IRebalanceExecutionController</c> actor guarantees single-threaded
    /// execution (via task chaining or channel-based sequential processing depending on configuration).
    /// No semaphore needed — the actor ensures only one execution runs at a time.
    /// Cancellation allows fast exit from superseded operations.</para>
    /// </remarks>
    public async Task ExecuteAsync(
        Intent<TRange, TData, TDomain> intent,
        Range<TRange> desiredRange,
        Range<TRange>? desiredNoRebalanceRange,
        CancellationToken cancellationToken)
    {
        // Use delivered data as the base - this is what the user received
        var baseRangeData = intent.AssembledRangeData;

        // Cancellation check before expensive I/O
        // Satisfies Invariant 34a: "Rebalance Execution MUST yield to User Path requests immediately"
        cancellationToken.ThrowIfCancellationRequested();

        // Phase 1: Extend delivered data to cover desired range (fetch only truly missing data)
        // Use delivered data as base instead of current cache to ensure consistency
        var extended = await _cacheExtensionService.ExtendCacheAsync(baseRangeData, desiredRange, cancellationToken)
            .ConfigureAwait(false);

        // Cancellation check after I/O but before mutation
        // If User Path cancelled us, don't apply the rebalance result
        cancellationToken.ThrowIfCancellationRequested();

        // Phase 2: Trim to desired range (rebalancing-specific: discard data outside desired range)
        var normalizedData = extended[desiredRange];

        // Final cancellation check before applying mutation
        // Ensures we don't apply obsolete rebalance results
        cancellationToken.ThrowIfCancellationRequested();

        // Phase 3: Apply cache state mutations (single writer — all fields updated atomically)
        _state.UpdateCacheState(normalizedData, desiredNoRebalanceRange);

        _cacheDiagnostics.RebalanceExecutionCompleted();
    }
}