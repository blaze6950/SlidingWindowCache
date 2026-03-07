using Intervals.NET.Caching.Dto;
using Intervals.NET.Domain.Abstractions;

namespace Intervals.NET.Caching.Extensions;

/// <summary>
/// Extension methods for <see cref="IRangeCache{TRange,TData,TDomain}"/> providing
/// strong consistency mode on top of the default eventual consistency model.
/// </summary>
/// <remarks>
/// <para><strong>Strong Consistency:</strong></para>
/// <para>
/// <see cref="GetDataAndWaitForIdleAsync{TRange,TData,TDomain}"/> always waits for the cache to
/// reach an idle state before returning. Suitable for testing, cold-start synchronization,
/// and diagnostics. For production hot paths, use the default eventual consistency model
/// (<see cref="IRangeCache{TRange,TData,TDomain}.GetDataAsync"/>).
/// </para>
/// <para><strong>Cancellation Graceful Degradation:</strong></para>
/// <para>
/// <see cref="GetDataAndWaitForIdleAsync{TRange,TData,TDomain}"/> degrades gracefully on
/// cancellation during the idle wait: if <c>WaitForIdleAsync</c> throws
/// <see cref="OperationCanceledException"/>, the already-obtained
/// <see cref="RangeResult{TRange,TData}"/> is returned instead of propagating the exception.
/// The background rebalance continues unaffected.
/// </para>
/// <para><strong>Serialized Access Requirement:</strong></para>
/// <para>
/// <see cref="GetDataAndWaitForIdleAsync{TRange,TData,TDomain}"/> provides its consistency guarantee
/// only under serialized (one-at-a-time) access. Under parallel access the method remains
/// safe (no crashes, no hangs) but the idle guarantee may degrade.
/// </para>
/// </remarks>
public static class RangeCacheConsistencyExtensions
{
    /// <summary>
    /// Retrieves data for the specified range and unconditionally waits for the cache to reach
    /// an idle state before returning, providing strong consistency semantics.
    /// </summary>
    /// <typeparam name="TRange">
    /// The type representing range boundaries. Must implement <see cref="IComparable{T}"/>.
    /// </typeparam>
    /// <typeparam name="TData">The type of data being cached.</typeparam>
    /// <typeparam name="TDomain">
    /// The type representing the domain of the ranges. Must implement <see cref="IRangeDomain{TRange}"/>.
    /// </typeparam>
    /// <param name="cache">The cache instance to retrieve data from.</param>
    /// <param name="requestedRange">The range for which to retrieve data.</param>
    /// <param name="cancellationToken">
    /// A cancellation token passed to both <c>GetDataAsync</c> and <c>WaitForIdleAsync</c>.
    /// Cancelling during the idle wait causes the method to return the already-obtained
    /// <see cref="RangeResult{TRange,TData}"/> gracefully (eventual consistency degradation).
    /// </param>
    /// <returns>
    /// A task that completes only after the cache has reached an idle state. The result is
    /// identical to what <see cref="IRangeCache{TRange,TData,TDomain}.GetDataAsync"/> returns directly.
    /// </returns>
    /// <remarks>
    /// <para><strong>Composition:</strong></para>
    /// <code>
    /// // Equivalent to:
    /// var result = await cache.GetDataAsync(requestedRange, cancellationToken);
    /// await cache.WaitForIdleAsync(cancellationToken);
    /// return result;
    /// </code>
    /// <para><strong>When to Use:</strong></para>
    /// <list type="bullet">
    /// <item><description>Integration tests that need deterministic cache state before making assertions.</description></item>
    /// <item><description>Cold start synchronization: waiting for the initial rebalance to complete.</description></item>
    /// <item><description>Diagnostics requiring unconditional idle wait.</description></item>
    /// </list>
    /// <para><strong>When NOT to Use:</strong></para>
    /// <list type="bullet">
    /// <item><description>
    /// Hot paths: the idle wait adds latency proportional to the rebalance execution time.
    /// Use <see cref="IRangeCache{TRange,TData,TDomain}.GetDataAsync"/> instead.
    /// </description></item>
    /// </list>
    /// </remarks>
    public static async ValueTask<RangeResult<TRange, TData>> GetDataAndWaitForIdleAsync<TRange, TData, TDomain>(
        this IRangeCache<TRange, TData, TDomain> cache,
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
