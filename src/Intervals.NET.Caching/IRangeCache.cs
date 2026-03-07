using Intervals.NET.Caching.Dto;
using Intervals.NET.Domain.Abstractions;

namespace Intervals.NET.Caching;

/// <summary>
/// Defines the common contract for all range-based cache implementations.
/// </summary>
/// <typeparam name="TRange">
/// The type representing range boundaries. Must implement <see cref="IComparable{T}"/>.
/// </typeparam>
/// <typeparam name="TData">
/// The type of data being cached.
/// </typeparam>
/// <typeparam name="TDomain">
/// The type representing the domain of the ranges. Must implement <see cref="IRangeDomain{TRange}"/>.
/// </typeparam>
/// <remarks>
/// <para><strong>Consistency Modes:</strong></para>
/// <para>
/// Implementations provide at minimum eventual consistency via <see cref="GetDataAsync"/>.
/// Opt-in stronger consistency modes are available as extension methods:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <strong>Strong consistency</strong> — <c>GetDataAndWaitForIdleAsync</c> (defined in
/// <c>RangeCacheConsistencyExtensions</c>): always waits for the cache to reach an idle state before returning.
/// </description></item>
/// </list>
/// <para><strong>Resource Management:</strong></para>
/// <para>
/// Implementations manage background resources that require explicit disposal. Always dispose
/// via <c>await using</c> or an explicit <see cref="IAsyncDisposable.DisposeAsync"/> call.
/// </para>
/// </remarks>
public interface IRangeCache<TRange, TData, TDomain> : IAsyncDisposable
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    /// <summary>
    /// Retrieves data for the specified range.
    /// </summary>
    /// <param name="requestedRange">The range for which to retrieve data.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
    /// <returns>
    /// A value task containing a <see cref="RangeResult{TRange,TData}"/> with the actual available
    /// range, the data, and a <see cref="CacheInteraction"/> value indicating how the request was served.
    /// </returns>
    ValueTask<RangeResult<TRange, TData>> GetDataAsync(
        Range<TRange> requestedRange,
        CancellationToken cancellationToken);

    /// <summary>
    /// Waits for the cache to reach an idle state (no pending work, no executing rebalance).
    /// </summary>
    /// <param name="cancellationToken">A cancellation token to cancel the wait.</param>
    /// <returns>A task that completes when the cache was idle at some point.</returns>
    /// <remarks>
    /// <para>
    /// Uses "was idle at some point" semantics: the task completes when the cache has been observed
    /// idle. New activity may begin immediately after. This is correct for convergence testing and
    /// for the strong-consistency extension method <c>GetDataAndWaitForIdleAsync</c>.
    /// </para>
    /// </remarks>
    Task WaitForIdleAsync(CancellationToken cancellationToken = default);
}
