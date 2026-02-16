using Intervals.NET;
using SlidingWindowCache.Public.Dto;

namespace SlidingWindowCache.Public;

/// <summary>
/// Defines the contract for data sources used in the sliding window cache.
/// Implementations must provide a method to fetch data for a single range.
/// The batch fetching method has a default implementation that can be overridden for optimization.
/// </summary>
/// <typeparam name="TRangeType">
/// The type representing range boundaries. Must implement <see cref="IComparable{T}"/>.
/// </typeparam>
/// <typeparam name="TDataType">
/// The type of data being fetched.
/// </typeparam>
/// <remarks>
/// <para><strong>Basic Implementation:</strong></para>
/// <code>
/// public class MyDataSource : IDataSource&lt;int, MyData&gt;
/// {
///     public async Task&lt;IEnumerable&lt;MyData&gt;&gt; FetchAsync(
///         Range&lt;int&gt; range, 
///         CancellationToken ct)
///     {
///         // Fetch data for single range
///         return await Database.QueryAsync(range, ct);
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
public interface IDataSource<TRangeType, TDataType> where TRangeType : IComparable<TRangeType>
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
    /// The task result contains an enumerable of data of type <typeparamref name="TDataType"/> 
    /// for the specified range.
    /// </returns>
    Task<IEnumerable<TDataType>> FetchAsync(
        Range<TRangeType> range,
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
    /// The task result contains an enumerable of <see cref="RangeChunk{TRangeType,TDataType}"/> 
    /// for the specified ranges.
    /// </returns>
    /// <remarks>
    /// <para><strong>Default Behavior:</strong></para>
    /// <para>
    /// The default implementation fetches each range in parallel by calling 
    /// <see cref="FetchAsync(Range{TRangeType}, CancellationToken)"/> for each range.
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
    /// </remarks>
    async Task<IEnumerable<RangeChunk<TRangeType, TDataType>>> FetchAsync(
        IEnumerable<Range<TRangeType>> ranges,
        CancellationToken cancellationToken
    )
    {
        var tasks = ranges.Select(async range =>
            new RangeChunk<TRangeType, TDataType>(
                range,
                await FetchAsync(range, cancellationToken)
            )
        );

        return await Task.WhenAll(tasks);
    }
}