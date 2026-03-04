using Intervals.NET;
using Intervals.NET.Domain.Abstractions;
using Intervals.NET.Caching.Public.Cache;
using Intervals.NET.Caching.Public.Configuration;
using Intervals.NET.Caching.Public.Dto;

namespace Intervals.NET.Caching.Public;

/// <summary>
/// Represents a sliding window cache that retrieves and caches data for specified ranges,
/// with automatic rebalancing based on access patterns.
/// </summary>
/// <typeparam name="TRange">
/// The type representing the range boundaries. Must implement <see cref="IComparable{T}"/>.
/// </typeparam>
/// <typeparam name="TData">
/// The type of data being cached.
/// </typeparam>
/// <typeparam name="TDomain">
/// The type representing the domain of the ranges. Must implement <see cref="IRangeDomain{TRange}"/>.
/// Supports both fixed-step (O(1)) and variable-step (O(N)) domains. While variable-step domains
/// have O(N) complexity for range calculations, this cost is negligible compared to data source I/O.
/// </typeparam>
/// <remarks>
/// <para><strong>Domain Flexibility:</strong></para>
/// <para>
/// This cache works with any <see cref="IRangeDomain{TRange}"/> implementation, whether fixed-step
/// or variable-step. The in-memory cost of O(N) step counting (microseconds) is orders of magnitude
/// smaller than typical data source operations (milliseconds to seconds via network/disk I/O).
/// </para>
/// <para><strong>Examples:</strong></para>
/// <list type="bullet">
/// <item><description>Fixed-step: DateTimeDayFixedStepDomain, IntegerFixedStepDomain (O(1) operations)</description></item>
/// <item><description>Variable-step: Business days, months, custom calendars (O(N) operations, still fast)</description></item>
/// </list>
/// <para><strong>Resource Management:</strong></para>
/// <para>
/// WindowCache manages background processing tasks and resources that require explicit disposal.
/// Always call <see cref="IAsyncDisposable.DisposeAsync"/> when done using the cache instance.
/// </para>
/// <para><strong>Disposal Behavior:</strong></para>
/// <list type="bullet">
/// <item><description>Gracefully stops background rebalance processing loops</description></item>
/// <item><description>Disposes internal synchronization primitives (semaphores, cancellation tokens)</description></item>
/// <item><description>After disposal, all methods throw <see cref="ObjectDisposedException"/></description></item>
/// <item><description>Safe to call multiple times (idempotent)</description></item>
/// <item><description>Does not require timeout - completes when background tasks finish current work</description></item>
/// </list>
/// <para><strong>Usage Pattern:</strong></para>
/// <code>
/// await using var cache = new WindowCache&lt;int, int, IntegerFixedStepDomain&gt;(...);
/// var data = await cache.GetDataAsync(range, cancellationToken);
/// // DisposeAsync automatically called at end of scope
/// </code>
/// </remarks>
public interface IWindowCache<TRange, TData, TDomain> : IAsyncDisposable
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    /// <summary>
    /// Retrieves data for the specified range, utilizing the sliding window cache mechanism.
    /// </summary>
    /// <param name="requestedRange">
    /// The range for which to retrieve data.
    /// </param>
    /// <param name="cancellationToken">
    /// A cancellation token to cancel the operation.
    /// </param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains a 
    /// <see cref="RangeResult{TRange, TData}"/> with the actual available range and data.
    /// </returns>
    /// <remarks>
    /// <para><strong>Bounded Data Sources:</strong></para>
    /// <para>
    /// When working with bounded data sources (e.g., databases with min/max IDs, time-series with
    /// temporal limits), the returned RangeResult.Range indicates what portion of the request was
    /// actually available. The Range may be:
    /// </para>
    /// <list type="bullet">
    /// <item><description>Equal to requestedRange - all data available (typical case)</description></item>
    /// <item><description>Subset of requestedRange - partial data available (truncated at boundaries)</description></item>
    /// <item><description>Null - no data available for the requested range</description></item>
    /// </list>
    /// <para><strong>Example:</strong></para>
    /// <code>
    /// var result = await cache.GetDataAsync(Range.Closed(50, 600), ct);
    /// if (result.Range.HasValue)
    /// {
    ///     Console.WriteLine($"Got data for range: {result.Range.Value}");
    ///     ProcessData(result.Data);
    /// }
    /// else
    /// {
    ///     Console.WriteLine("No data available for requested range");
    /// }
    /// </code>
    /// <para>See boundary handling documentation for details.</para>
    /// </remarks>
    ValueTask<RangeResult<TRange, TData>> GetDataAsync(
        Range<TRange> requestedRange,
        CancellationToken cancellationToken);

    /// <summary>
    /// Waits for the cache to reach an idle state (no pending intent and no executing rebalance).
    /// </summary>
    /// <param name="cancellationToken">
    /// A cancellation token to cancel the wait operation.
    /// </param>
    /// <returns>
    /// A task that completes when the cache reaches idle state.
    /// </returns>
    /// <remarks>
    /// <para><strong>Idle State Definition:</strong></para>
    /// <para>
    /// The cache is considered idle when:
    /// <list type="bullet">
    /// <item><description>No pending intent is awaiting processing</description></item>
    /// <item><description>No rebalance execution is currently running</description></item>
    /// </list>
    /// </para>
    /// <para><strong>Use Cases:</strong></para>
    /// <list type="bullet">
    /// <item><description>Testing: Ensure cache has stabilized before assertions</description></item>
    /// <item><description>Cold start synchronization: Wait for initial rebalance to complete</description></item>
    /// <item><description>Diagnostics: Verify cache has converged to optimal state</description></item>
    /// </list>
    /// </remarks>
    Task WaitForIdleAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically updates one or more runtime configuration values on the live cache instance.
    /// </summary>
    /// <param name="configure">
    /// A delegate that receives a <see cref="RuntimeOptionsUpdateBuilder"/> and applies the desired changes.
    /// Only the fields explicitly set on the builder are changed; all others retain their current values.
    /// </param>
    /// <remarks>
    /// <para><strong>Partial Updates:</strong></para>
    /// <para>
    /// You only need to specify the fields you want to change:
    /// </para>
    /// <code>
    /// cache.UpdateRuntimeOptions(update =>
    ///     update.WithLeftCacheSize(2.0)
    ///           .WithDebounceDelay(TimeSpan.FromMilliseconds(50)));
    /// </code>
    /// <para><strong>Threshold Handling:</strong></para>
    /// <para>
    /// Because thresholds are <c>double?</c>, use explicit clear methods to set a threshold to <c>null</c>:
    /// </para>
    /// <code>
    /// cache.UpdateRuntimeOptions(update => update.ClearLeftThreshold());
    /// </code>
    /// <para><strong>Validation:</strong></para>
    /// <para>
    /// The merged options are validated before publishing. If validation fails (e.g. negative cache size,
    /// threshold sum &gt; 1.0), an exception is thrown and the current options are left unchanged.
    /// </para>
    /// <para><strong>"Next Cycle" Semantics:</strong></para>
    /// <para>
    /// Updates take effect on the next rebalance decision/execution cycle. In-flight rebalance operations
    /// continue with the options that were active when they started.
    /// </para>
    /// <para><strong>Thread Safety:</strong></para>
    /// <para>
    /// This method is thread-safe. Concurrent calls follow last-writer-wins semantics, which is acceptable
    /// for configuration updates where the latest user intent should prevail.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">Thrown when called on a disposed cache instance.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when any updated value fails validation.</exception>
    /// <exception cref="ArgumentException">Thrown when the merged threshold sum exceeds 1.0.</exception>
    void UpdateRuntimeOptions(Action<RuntimeOptionsUpdateBuilder> configure);

    /// <summary>
    /// Gets a snapshot of the current runtime-updatable option values on this cache instance.
    /// </summary>
    /// <remarks>
    /// <para><strong>Snapshot Semantics:</strong></para>
    /// <para>
    /// The returned <see cref="RuntimeOptionsSnapshot"/> captures the option values at the moment
    /// this property is read. It is not updated if
    /// <see cref="UpdateRuntimeOptions"/> is called afterward — obtain a new snapshot to see
    /// updated values.
    /// </para>
    /// <para><strong>Usage:</strong></para>
    /// <code>
    /// // Inspect current options
    /// var current = cache.CurrentRuntimeOptions;
    /// Console.WriteLine($"LeftCacheSize={current.LeftCacheSize}");
    ///
    /// // Perform a relative update (e.g. double the left cache size)
    /// var snapshot = cache.CurrentRuntimeOptions;
    /// cache.UpdateRuntimeOptions(u => u.WithLeftCacheSize(snapshot.LeftCacheSize * 2));
    /// </code>
    /// <para><strong>Layered Caches:</strong></para>
    /// <para>
    /// On a <see cref="LayeredWindowCache{TRange,TData,TDomain}"/>, this property returns the
    /// options of the outermost (user-facing) layer. To inspect the options of a specific inner
    /// layer, access that layer directly via
    /// <see cref="LayeredWindowCache{TRange,TData,TDomain}.Layers"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">Thrown when called on a disposed cache instance.</exception>
    RuntimeOptionsSnapshot CurrentRuntimeOptions { get; }
}
