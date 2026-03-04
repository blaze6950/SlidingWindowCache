using Intervals.NET;
using Intervals.NET.Caching.Public.Dto;

namespace Intervals.NET.Caching.Public;

/// <summary>
/// Defines the contract for data sources used in the sliding window cache.
/// Implementations must provide a method to fetch data for a single range.
/// The batch fetching method has a default implementation that can be overridden for optimization.
/// </summary>
/// <typeparam name="TRange">
/// The type representing range boundaries. Must implement <see cref="IComparable{T}"/>.
/// </typeparam>
/// <typeparam name="TData">
/// The type of data being fetched.
/// </typeparam>
/// <remarks>
/// <para><strong>Quick Setup — FuncDataSource:</strong></para>
/// <para>
/// Use <see cref="FuncDataSource{TRange,TData}"/> to create a data source from a delegate
/// without defining a class:
/// </para>
/// <code>
/// IDataSource&lt;int, MyData&gt; source = new FuncDataSource&lt;int, MyData&gt;(
///     async (range, ct) =>
///     {
///         var data = await Database.QueryAsync(range, ct);
///         return new RangeChunk&lt;int, MyData&gt;(range, data);
///     });
/// </code>
/// <para><strong>Full Class Implementation:</strong></para>
/// <code>
/// public class MyDataSource : IDataSource&lt;int, MyData&gt;
/// {
///     public async Task&lt;RangeChunk&lt;int, MyData&gt;&gt; FetchAsync(
///         Range&lt;int&gt; range, 
///         CancellationToken ct)
///     {
///         // Fetch data for single range
///         var data = await Database.QueryAsync(range, ct);
///         return new RangeChunk&lt;int, MyData&gt;(range, data);
///     }
///     
///     // Batch method uses default parallel implementation automatically
/// }
/// </code>
/// <para><strong>Optimized Batch Implementation:</strong></para>
/// <code>
/// public class OptimizedDataSource : IDataSource&lt;int, MyData&gt;
/// {
///     public async Task&lt;IEnumerable&lt;MyData&gt;&gt; FetchAsync(
///         Range&lt;int&gt; range, 
///         CancellationToken ct)
///     {
///         return await Database.QueryAsync(range, ct);
///     }
///     
///     // Override for true batch optimization (single DB query)
///     public async Task&lt;IEnumerable&lt;RangeChunk&lt;int, MyData&gt;&gt;&gt; FetchAsync(
///         IEnumerable&lt;Range&lt;int&gt;&gt; ranges, 
///         CancellationToken ct)
///     {
///         // Single database query for all ranges - much more efficient!
///         return await Database.QueryMultipleRangesAsync(ranges, ct);
///     }
/// }
/// </code>
/// </remarks>
public interface IDataSource<TRange, TData> where TRange : IComparable<TRange>
{
    /// <summary>
    /// Fetches data for the specified range asynchronously.
    /// </summary>
    /// <param name="range">
    /// The range for which to fetch data.
    /// </param>
    /// <param name="cancellationToken">
    /// A cancellation token to cancel the operation.
    /// </param>
    /// <returns>
    /// A task that represents the asynchronous fetch operation. 
    /// The task result contains an enumerable of data of type <typeparamref name="TData"/> 
    /// for the specified range.
    /// </returns>
    /// <remarks>
    /// <para><strong>Bounded Data Sources:</strong></para>
    /// <para>
    /// For data sources with physical boundaries (e.g., databases with min/max IDs, 
    /// time-series with temporal limits, paginated APIs with maximum pages), implementations MUST:
    /// </para>
    /// <list type="bullet">
    /// <item><description>Return RangeChunk with Range = null when no data is available for the requested range</description></item>
    /// <item><description>Return truncated range when partial data is available (intersection of requested and available)</description></item>
    /// <item><description>NEVER throw exceptions for out-of-bounds requests - use null Range instead</description></item>
    /// <item><description>Ensure Data contains exactly Range.Span elements when Range is non-null</description></item>
    /// </list>
    /// <para><strong>Boundary Handling Examples:</strong></para>
    /// <code>
    /// // Database with records ID 100-500
    /// public async Task&lt;RangeChunk&lt;int, MyData&gt;&gt; FetchAsync(Range&lt;int&gt; requested, CancellationToken ct)
    /// {
    ///     // Compute intersection with available range
    ///     var available = requested.Intersect(Range.Closed(MinId, MaxId));
    ///     
    ///     // No data available - return RangeChunk with null Range
    ///     if (available == null)
    ///         return new RangeChunk&lt;int, MyData&gt;(null, Array.Empty&lt;MyData&gt;());
    ///     
    ///     // Fetch available portion
    ///     var data = await Database.FetchRecordsAsync(available.LeftEndpoint, available.RightEndpoint, ct);
    ///     return new RangeChunk&lt;int, MyData&gt;(available, data);
    /// }
    /// 
    /// // Examples:
    /// // Request [50..150]  > RangeChunk([100..150], 51 records) - truncated at lower bound
    /// // Request [400..600] > RangeChunk([400..500], 101 records) - truncated at upper bound
    /// // Request [600..700] > RangeChunk(null, empty) - completely out of bounds
    /// </code>
    /// <para>See documentation on boundary handling for detailed guidance.</para>
    /// </remarks>
    Task<RangeChunk<TRange, TData>> FetchAsync(
        Range<TRange> range,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Fetches data for multiple specified ranges asynchronously.
    /// This method can be used for batch fetching to optimize data retrieval when multiple ranges are needed.
    /// </summary>
    /// <param name="ranges">
    /// The ranges for which to fetch data.
    /// </param>
    /// <param name="cancellationToken">
    /// A cancellation token to cancel the operation.
    /// </param>
    /// <returns>
    /// A task that represents the asynchronous fetch operation. 
    /// The task result contains an enumerable of <see cref="RangeChunk{TRange,TData}"/> 
    /// for the specified ranges. Each RangeChunk may have a null Range if no data is available.
    /// </returns>
    /// <remarks>
    /// <para><strong>Default Behavior:</strong></para>
    /// <para>
    /// The default implementation fetches each range in parallel by calling 
    /// <see cref="FetchAsync(Range{TRange}, CancellationToken)"/> for each range.
    /// This provides automatic parallelization without additional implementation effort.
    /// </para>
    /// <para><strong>When to Override:</strong></para>
    /// <para>
    /// Override this method if your data source supports true batch optimization, such as:
    /// </para>
    /// <list type="bullet">
    /// <item><description>Single database query that can fetch multiple ranges at once</description></item>
    /// <item><description>Batch API endpoints that accept multiple range parameters</description></item>
    /// <item><description>Custom batching logic with size limits or throttling</description></item>
    /// </list>
    /// <para><strong>Boundary Handling:</strong></para>
    /// <para>
    /// When implementing for bounded data sources, ensure each RangeChunk follows the same
    /// boundary contract as the single-range FetchAsync method (null Range for unavailable data,
    /// truncated ranges for partial availability).
    /// </para>
    /// </remarks>
    async Task<IEnumerable<RangeChunk<TRange, TData>>> FetchAsync(
        IEnumerable<Range<TRange>> ranges,
        CancellationToken cancellationToken
    )
    {
        var tasks = ranges.Select(range => FetchAsync(range, cancellationToken));
        return await Task.WhenAll(tasks);
    }
}