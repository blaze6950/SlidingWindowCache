using Intervals.NET;
using Intervals.NET.Domain.Abstractions;
using SlidingWindowCache.Public.Dto;

namespace SlidingWindowCache.Public;

/// <summary>
/// Extension methods for <see cref="IWindowCache{TRange, TData, TDomain}"/> providing
/// opt-in consistency modes on top of the default eventual consistency model.
/// </summary>
/// <remarks>
/// <para><strong>Three Consistency Modes:</strong></para>
/// <list type="bullet">
/// <item><description>
/// <strong>Eventual (default)</strong> — <see cref="IWindowCache{TRange,TData,TDomain}.GetDataAsync"/>
/// returns data immediately. The cache converges in the background without blocking the caller.
/// Suitable for sequential access patterns and hot paths.
/// </description></item>
/// <item><description>
/// <strong>Hybrid</strong> — <see cref="GetDataAndWaitOnMissAsync{TRange,TData,TDomain}"/>
/// returns immediately on a full cache hit; waits for rebalance on a partial hit or full miss.
/// Suitable for random access patterns where the requested range may be far from the current
/// cache position, ensuring the cache is warm for subsequent nearby requests.
/// </description></item>
/// <item><description>
/// <strong>Strong</strong> — <see cref="GetDataAndWaitForIdleAsync{TRange,TData,TDomain}"/>
/// always waits for the cache to reach an idle state before returning.
/// Suitable for testing, cold-start synchronization, and diagnostics.
/// </description></item>
/// </list>
/// <para><strong>Cancellation Graceful Degradation:</strong></para>
/// <para>
/// Both <see cref="GetDataAndWaitOnMissAsync{TRange,TData,TDomain}"/> and
/// <see cref="GetDataAndWaitForIdleAsync{TRange,TData,TDomain}"/> degrade gracefully on
/// cancellation during the idle wait: if <c>WaitForIdleAsync</c> throws
/// <see cref="OperationCanceledException"/>, the already-obtained
/// <see cref="Dto.RangeResult{TRange,TData}"/> is returned instead of propagating the exception.
/// The background rebalance continues unaffected. This preserves valid user data even when the
/// caller no longer needs to wait for convergence.
/// Other exceptions from <c>WaitForIdleAsync</c> (e.g., <see cref="ObjectDisposedException"/>)
/// still propagate normally.
/// </para>
/// <para><strong>Serialized Access Requirement for Hybrid and Strong Modes:</strong></para>
/// <para>
/// <see cref="GetDataAndWaitOnMissAsync{TRange,TData,TDomain}"/> and
/// <see cref="GetDataAndWaitForIdleAsync{TRange,TData,TDomain}"/> provide their semantic guarantees
/// — "cache is warm for my next call" — only under <em>serialized</em> (one-at-a-time) access.
/// </para>
/// <para>
/// Under parallel access (multiple threads concurrently calling these methods on the same cache
/// instance), the methods remain fully safe: no crashes, no hangs, no data corruption.
/// However, the consistency guarantee may degrade:
/// <list type="bullet">
/// <item><description>
/// Due to the <c>AsyncActivityCounter</c>'s "was idle at some point" semantics (Invariant H.49),
/// a thread that calls <c>WaitForIdleAsync</c> during the window between
/// <c>Interlocked.Increment</c> (counter 0→1) and the subsequent <c>Volatile.Write</c> of the
/// new <c>TaskCompletionSource</c> will observe the previous (already-completed) TCS and return
/// immediately, even though work is in-flight.
/// </description></item>
/// <item><description>
/// Under "latest intent wins" semantics in the intent pipeline, one thread's rebalance may be
/// superseded by another's, so a thread may wait for a different rebalance than the one triggered
/// by its own request.
/// </description></item>
/// </list>
/// These behaviours are consistent with the WindowCache design model: one logical consumer
/// per cache instance with coherent, non-concurrent access patterns.
/// </para>
/// </remarks>
public static class WindowCacheExtensions
{
    /// <summary>
    /// Retrieves data for the specified range and — if the request resulted in a cache miss or
    /// partial cache hit — waits for the cache to reach an idle state before returning.
    /// This provides <em>hybrid consistency</em> semantics.
    /// </summary>
    /// <typeparam name="TRange">
    /// The type representing the range boundaries. Must implement <see cref="IComparable{T}"/>.
    /// </typeparam>
    /// <typeparam name="TData">
    /// The type of data being cached.
    /// </typeparam>
    /// <typeparam name="TDomain">
    /// The type representing the domain of the ranges. Must implement <see cref="IRangeDomain{TRange}"/>.
    /// </typeparam>
    /// <param name="cache">
    /// The cache instance to retrieve data from.
    /// </param>
    /// <param name="requestedRange">
    /// The range for which to retrieve data.
    /// </param>
    /// <param name="cancellationToken">
    /// A cancellation token to cancel the operation. Passed to both
    /// <see cref="IWindowCache{TRange, TData, TDomain}.GetDataAsync"/> and, when applicable,
    /// <see cref="IWindowCache{TRange, TData, TDomain}.WaitForIdleAsync"/>.
    /// Cancelling the token during the idle wait stops the <em>wait</em> and causes the method
    /// to return the already-obtained <see cref="RangeResult{TRange,TData}"/> gracefully
    /// (eventual consistency degradation). The background rebalance continues to completion.
    /// </param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains a
    /// <see cref="RangeResult{TRange, TData}"/> with the actual available range, data, and
    /// <see cref="RangeResult{TRange,TData}.CacheInteraction"/>, identical to what
    /// <see cref="IWindowCache{TRange, TData, TDomain}.GetDataAsync"/> returns directly.
    /// The task completes immediately on a full cache hit; on a partial hit or full miss the
    /// task completes only after the cache has reached an idle state (or immediately if the
    /// idle wait is cancelled).
    /// </returns>
    /// <remarks>
    /// <para><strong>Motivation — Avoiding Double Miss on Random Access:</strong></para>
    /// <para>
    /// When the default eventual consistency model is used and the requested range is far from
    /// the current cache position (a "jump"), the caller receives correct data but the cache is
    /// still converging in the background. If the caller immediately makes another nearby request,
    /// that second request may encounter another cache miss before rebalance has completed.
    /// </para>
    /// <para>
    /// This method eliminates the "double miss" problem: by waiting for idle on a miss, the
    /// cache is guaranteed to be warm around the new position before the method returns, so
    /// subsequent nearby requests will hit the cache.
    /// </para>
    /// <para><strong>Behavior by Cache Interaction Type:</strong></para>
    /// <list type="bullet">
    /// <item><description>
    /// <see cref="CacheInteraction.FullHit"/> — returns immediately (eventual consistency).
    /// The cache is already correctly positioned; no idle wait is needed.
    /// </description></item>
    /// <item><description>
    /// <see cref="CacheInteraction.PartialHit"/> — awaits
    /// <see cref="IWindowCache{TRange,TData,TDomain}.WaitForIdleAsync"/> before returning.
    /// Missing segments were already fetched from <c>IDataSource</c> on the user path; the wait
    /// ensures the background rebalance fully populates the cache around the new position.
    /// </description></item>
    /// <item><description>
    /// <see cref="CacheInteraction.FullMiss"/> — awaits
    /// <see cref="IWindowCache{TRange,TData,TDomain}.WaitForIdleAsync"/> before returning.
    /// The entire range was fetched from <c>IDataSource</c> (cold start or non-intersecting jump);
    /// the wait ensures the background rebalance builds the cache window around the new position.
    /// </description></item>
    /// </list>
    /// <para><strong>Idle Semantics (Invariant H.49):</strong></para>
    /// <para>
    /// The idle wait uses "was idle at some point" semantics inherited from
    /// <see cref="IWindowCache{TRange,TData,TDomain}.WaitForIdleAsync"/>. This is sufficient for
    /// the hybrid consistency use case: after the await, the cache has converged at least once since
    /// the request. New activity may begin immediately after, but the next nearby request will find
    /// a warm cache.
    /// </para>
    /// <para><strong>Debounce Latency Note:</strong></para>
    /// <para>
    /// When the idle wait is triggered, the caller pays the full rebalance latency including any
    /// configured debounce delay. On a miss path, the caller has already paid an <c>IDataSource</c>
    /// round-trip; the additional wait is proportionally less significant.
    /// </para>
    /// <para><strong>Serialized Access Requirement:</strong></para>
    /// <para>
    /// This method provides its "cache will be warm for the next call" guarantee only under
    /// serialized (one-at-a-time) access. See <see cref="WindowCacheExtensions"/> class remarks
    /// for a detailed explanation of parallel access behaviour.
    /// </para>
    /// <para><strong>When to Use:</strong></para>
    /// <list type="bullet">
    /// <item><description>
    /// Random access patterns where the requested range may be far from the current cache position
    /// and the caller will immediately make subsequent nearby requests.
    /// </description></item>
    /// <item><description>
    /// Paging or viewport scenarios where a "jump" to a new position should result in a warm
    /// cache before continuing to scroll or page.
    /// </description></item>
    /// </list>
    /// <para><strong>When NOT to Use:</strong></para>
    /// <list type="bullet">
    /// <item><description>
    /// Sequential access hot paths: if the access pattern is sequential and the cache is
    /// well-positioned, full hits will dominate and this method behaves identically to
    /// <see cref="IWindowCache{TRange,TData,TDomain}.GetDataAsync"/> with no overhead.
    /// However, on the rare miss case it will add latency that is unnecessary for sequential access.
    /// Use the default eventual consistency model instead.
    /// </description></item>
    /// <item><description>
    /// Tests or diagnostics requiring <em>unconditional</em> idle wait — prefer
    /// <see cref="GetDataAndWaitForIdleAsync{TRange,TData,TDomain}"/> (strong consistency).
    /// </description></item>
    /// </list>
    /// <para><strong>Exception Propagation:</strong></para>
    /// <list type="bullet">
    /// <item><description>
    /// If <c>GetDataAsync</c> throws (e.g., <see cref="ObjectDisposedException"/>,
    /// <see cref="OperationCanceledException"/>), the exception propagates immediately and
    /// <c>WaitForIdleAsync</c> is never called.
    /// </description></item>
    /// <item><description>
    /// If <c>WaitForIdleAsync</c> throws <see cref="OperationCanceledException"/>, the
    /// already-obtained result is returned (graceful degradation to eventual consistency).
    /// The background rebalance continues; only the wait is abandoned.
    /// </description></item>
    /// <item><description>
    /// If <c>WaitForIdleAsync</c> throws any other exception (e.g.,
    /// <see cref="ObjectDisposedException"/>, <see cref="InvalidOperationException"/>),
    /// the exception propagates normally.
    /// </description></item>
    /// </list>
    /// <para><strong>Cancellation Graceful Degradation:</strong></para>
    /// <para>
    /// Cancelling <paramref name="cancellationToken"/> during the idle wait (after
    /// <c>GetDataAsync</c> has already succeeded) does not discard the obtained data.
    /// The method catches <see cref="OperationCanceledException"/> from <c>WaitForIdleAsync</c>
    /// and returns the <see cref="RangeResult{TRange,TData}"/> that was already retrieved,
    /// degrading to eventual consistency semantics for this call.
    /// </para>
    /// <para><strong>Example:</strong></para>
    /// <code>
    /// // Hybrid consistency: only waits on miss/partial hit, returns immediately on full hit
    /// var result = await cache.GetDataAndWaitOnMissAsync(
    ///     Range.Closed(5000, 5100),  // Far from current cache position — full miss
    ///     cancellationToken);
    ///
    /// // Cache is now warm around [5000, 5100].
    /// // The next nearby request will be a full cache hit.
    /// Console.WriteLine($"Interaction: {result.CacheInteraction}"); // FullMiss
    ///
    /// var nextResult = await cache.GetDataAsync(
    ///     Range.Closed(5050, 5150),  // Within rebalanced cache — full hit
    ///     cancellationToken);
    /// </code>
    /// </remarks>
    public static async ValueTask<RangeResult<TRange, TData>> GetDataAndWaitOnMissAsync<TRange, TData, TDomain>(
        this IWindowCache<TRange, TData, TDomain> cache,
        Range<TRange> requestedRange,
        CancellationToken cancellationToken = default)
        where TRange : IComparable<TRange>
        where TDomain : IRangeDomain<TRange>
    {
        var result = await cache.GetDataAsync(requestedRange, cancellationToken);

        // Wait for idle only on cache miss scenarios (full miss or partial hit) to ensure
        // the cache is rebalanced around the new position before returning.
        // Full cache hits return immediately — the cache is already correctly positioned.
        // If the idle wait is cancelled, return the already-obtained result gracefully
        // (degrade to eventual consistency) rather than discarding valid data.
        if (result.CacheInteraction != CacheInteraction.FullHit)
        {
            try
            {
                await cache.WaitForIdleAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Graceful degradation: cancellation during the idle wait does not
                // discard the data already obtained from GetDataAsync. The background
                // rebalance continues; we simply stop waiting for it.
            }
        }

        return result;
    }

    /// <summary>
    /// Retrieves data for the specified range and waits for the cache to reach an idle
    /// state before returning, providing strong consistency semantics.
    /// </summary>
    /// <typeparam name="TRange">
    /// The type representing the range boundaries. Must implement <see cref="IComparable{T}"/>.
    /// </typeparam>
    /// <typeparam name="TData">
    /// The type of data being cached.
    /// </typeparam>
    /// <typeparam name="TDomain">
    /// The type representing the domain of the ranges. Must implement <see cref="IRangeDomain{TRange}"/>.
    /// </typeparam>
    /// <param name="cache">
    /// The cache instance to retrieve data from.
    /// </param>
    /// <param name="requestedRange">
    /// The range for which to retrieve data.
    /// </param>
    /// <param name="cancellationToken">
    /// A cancellation token to cancel the operation. Passed to both
    /// <see cref="IWindowCache{TRange, TData, TDomain}.GetDataAsync"/> and
    /// <see cref="IWindowCache{TRange, TData, TDomain}.WaitForIdleAsync"/>.
    /// Cancelling the token during the idle wait stops the <em>wait</em> and causes the method
    /// to return the already-obtained <see cref="RangeResult{TRange,TData}"/> gracefully
    /// (eventual consistency degradation). The background rebalance continues to completion.
    /// </param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains a
    /// <see cref="RangeResult{TRange, TData}"/> with the actual available range and data,
    /// identical to what <see cref="IWindowCache{TRange, TData, TDomain}.GetDataAsync"/> returns.
    /// The task completes only after the cache has reached an idle state (no pending intent,
    /// no executing rebalance).
    /// </returns>
    /// <remarks>
    /// <para><strong>Default vs. Strong Consistency:</strong></para>
    /// <para>
    /// By default, <see cref="IWindowCache{TRange, TData, TDomain}.GetDataAsync"/> returns data
    /// immediately under an eventual consistency model: the user always receives correct data,
    /// but the cache window may still be converging toward its optimal configuration in the background.
    /// </para>
    /// <para>
    /// This method extends that with an unconditional wait: it calls <c>GetDataAsync</c> first
    /// (user data returned immediately from cache or <c>IDataSource</c>), then always awaits
    /// <see cref="IWindowCache{TRange, TData, TDomain}.WaitForIdleAsync"/> before returning —
    /// regardless of whether the request was a full hit, partial hit, or full miss.
    /// </para>
    /// <para>
    /// For a conditional wait that only blocks on misses, prefer
    /// <see cref="GetDataAndWaitOnMissAsync{TRange,TData,TDomain}"/> (hybrid consistency).
    /// </para>
    /// <para><strong>Composition:</strong></para>
    /// <code>
    /// // Equivalent to:
    /// var result = await cache.GetDataAsync(requestedRange, cancellationToken);
    /// await cache.WaitForIdleAsync(cancellationToken);
    /// return result;
    /// </code>
    /// <para><strong>When to Use:</strong></para>
    /// <list type="bullet">
    /// <item><description>
    /// When the caller needs to assert or inspect the cache geometry after the request
    /// (e.g., verifying that a rebalance occurred or that the window has shifted).
    /// </description></item>
    /// <item><description>
    /// Cold start synchronization: waiting for the initial rebalance to complete before
    /// proceeding with subsequent operations.
    /// </description></item>
    /// <item><description>
    /// Integration tests that need deterministic cache state before making assertions.
    /// </description></item>
    /// </list>
    /// <para><strong>When NOT to Use:</strong></para>
    /// <list type="bullet">
    /// <item><description>
    /// Hot paths: the idle wait adds latency proportional to the rebalance execution time
    /// (debounce delay + data fetching + cache update). For normal usage, prefer the default
    /// eventual consistency model via <see cref="IWindowCache{TRange, TData, TDomain}.GetDataAsync"/>.
    /// </description></item>
    /// <item><description>
    /// Rapid sequential requests: calling this method back-to-back means each call waits
    /// for the prior rebalance to complete, eliminating the debounce and work-avoidance
    /// benefits of the cache.
    /// </description></item>
    /// <item><description>
    /// Random access patterns where waiting only on misses is sufficient — prefer
    /// <see cref="GetDataAndWaitOnMissAsync{TRange,TData,TDomain}"/> (hybrid consistency).
    /// </description></item>
    /// </list>
    /// <para><strong>Idle Semantics (Invariant H.49):</strong></para>
    /// <para>
    /// The idle wait uses "was idle at some point" semantics inherited from
    /// <see cref="IWindowCache{TRange, TData, TDomain}.WaitForIdleAsync"/>. This is sufficient
    /// for the strong consistency use cases above: after the await, the cache has converged at
    /// least once since the request. New activity may begin immediately after, but the
    /// cache state observed at the idle point reflects the completed rebalance.
    /// </para>
    /// <para><strong>Serialized Access Requirement:</strong></para>
    /// <para>
    /// This method provides its consistency guarantee only under serialized (one-at-a-time) access.
    /// See <see cref="WindowCacheExtensions"/> class remarks for a detailed explanation of
    /// parallel access behaviour.
    /// </para>
    /// <para><strong>Exception Propagation:</strong></para>
    /// <list type="bullet">
    /// <item><description>
    /// If <c>GetDataAsync</c> throws (e.g., <see cref="ObjectDisposedException"/>,
    /// <see cref="OperationCanceledException"/>), the exception propagates immediately and
    /// <c>WaitForIdleAsync</c> is never called.
    /// </description></item>
    /// <item><description>
    /// If <c>WaitForIdleAsync</c> throws <see cref="OperationCanceledException"/>, the
    /// already-obtained result is returned (graceful degradation to eventual consistency).
    /// The background rebalance continues; only the wait is abandoned.
    /// </description></item>
    /// <item><description>
    /// If <c>WaitForIdleAsync</c> throws any other exception (e.g.,
    /// <see cref="ObjectDisposedException"/>, <see cref="InvalidOperationException"/>),
    /// the exception propagates normally.
    /// </description></item>
    /// </list>
    /// <para><strong>Cancellation Graceful Degradation:</strong></para>
    /// <para>
    /// Cancelling <paramref name="cancellationToken"/> during the idle wait (after
    /// <c>GetDataAsync</c> has already succeeded) does not discard the obtained data.
    /// The method catches <see cref="OperationCanceledException"/> from <c>WaitForIdleAsync</c>
    /// and returns the <see cref="RangeResult{TRange,TData}"/> that was already retrieved,
    /// degrading to eventual consistency semantics for this call.
    /// </para>
    /// <para><strong>Example:</strong></para>
    /// <code>
    /// // Strong consistency: returns only after cache has converged
    /// var result = await cache.GetDataAndWaitForIdleAsync(
    ///     Range.Closed(100, 200),
    ///     cancellationToken);
    ///
    /// // Cache geometry is now fully converged — safe to inspect or assert
    /// if (result.Range.HasValue)
    ///     ProcessData(result.Data);
    /// </code>
    /// </remarks>
    public static async ValueTask<RangeResult<TRange, TData>> GetDataAndWaitForIdleAsync<TRange, TData, TDomain>(
        this IWindowCache<TRange, TData, TDomain> cache,
        Range<TRange> requestedRange,
        CancellationToken cancellationToken = default)
        where TRange : IComparable<TRange>
        where TDomain : IRangeDomain<TRange>
    {
        var result = await cache.GetDataAsync(requestedRange, cancellationToken);

        try
        {
            await cache.WaitForIdleAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Graceful degradation: cancellation during the idle wait does not
            // discard the data already obtained from GetDataAsync. The background
            // rebalance continues; we simply stop waiting for it.
        }

        return result;
    }
}
