using Intervals.NET.Domain.Abstractions;
using Intervals.NET.Caching;
using Intervals.NET.Caching.Layered;
using Intervals.NET.Caching.SlidingWindow.Public.Cache;
using Intervals.NET.Caching.SlidingWindow.Public.Configuration;
using Intervals.NET.Caching.SlidingWindow.Public.Instrumentation;

namespace Intervals.NET.Caching.SlidingWindow.Public.Extensions;

/// <summary>
/// Extension methods on <see cref="LayeredRangeCacheBuilder{TRange,TData,TDomain}"/> that add
/// a <see cref="SlidingWindowCache{TRange,TData,TDomain}"/> layer to the cache stack.
/// </summary>
/// <remarks>
/// <para><strong>Usage:</strong></para>
/// <code>
/// await using var cache = SlidingWindowCacheBuilder.Layered(dataSource, domain)
///     .AddSlidingWindowLayer(o =&gt; o.WithCacheSize(10.0).WithReadMode(UserCacheReadMode.CopyOnRead))
///     .AddSlidingWindowLayer(o =&gt; o.WithCacheSize(0.5))
///     .Build();
/// </code>
/// <para>
/// Each call wraps the previous layer (or root data source) in a
/// <see cref="RangeCacheDataSourceAdapter{TRange,TData,TDomain}"/> and passes it to a new
/// <see cref="SlidingWindowCache{TRange,TData,TDomain}"/> instance.
/// </para>
/// </remarks>
public static class SlidingWindowLayerExtensions
{
    /// <summary>
    /// Adds a <see cref="SlidingWindowCache{TRange,TData,TDomain}"/> layer configured with
    /// a pre-built <see cref="SlidingWindowCacheOptions"/> instance.
    /// </summary>
    /// <typeparam name="TRange">The type representing range boundaries. Must implement <see cref="IComparable{T}"/>.</typeparam>
    /// <typeparam name="TData">The type of data being cached.</typeparam>
    /// <typeparam name="TDomain">The range domain type. Must implement <see cref="IRangeDomain{TRange}"/>.</typeparam>
    /// <param name="builder">The layered cache builder to add the layer to.</param>
    /// <param name="options">The configuration options for this layer's SlidingWindowCache.</param>
    /// <param name="diagnostics">
    /// Optional diagnostics implementation. When <c>null</c>, <see cref="NoOpDiagnostics.Instance"/> is used.
    /// </param>
    /// <returns>The same builder instance, for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="options"/> is <c>null</c>.
    /// </exception>
    public static LayeredRangeCacheBuilder<TRange, TData, TDomain> AddSlidingWindowLayer<TRange, TData, TDomain>(
        this LayeredRangeCacheBuilder<TRange, TData, TDomain> builder,
        SlidingWindowCacheOptions options,
        ICacheDiagnostics? diagnostics = null)
        where TRange : IComparable<TRange>
        where TDomain : IRangeDomain<TRange>
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        var domain = builder.Domain;
        return builder.AddLayer(dataSource =>
            new SlidingWindowCache<TRange, TData, TDomain>(dataSource, domain, options, diagnostics));
    }

    /// <summary>
    /// Adds a <see cref="SlidingWindowCache{TRange,TData,TDomain}"/> layer configured inline
    /// using a fluent <see cref="SlidingWindowCacheOptionsBuilder"/>.
    /// </summary>
    /// <typeparam name="TRange">The type representing range boundaries. Must implement <see cref="IComparable{T}"/>.</typeparam>
    /// <typeparam name="TData">The type of data being cached.</typeparam>
    /// <typeparam name="TDomain">The range domain type. Must implement <see cref="IRangeDomain{TRange}"/>.</typeparam>
    /// <param name="builder">The layered cache builder to add the layer to.</param>
    /// <param name="configure">
    /// A delegate that receives a <see cref="SlidingWindowCacheOptionsBuilder"/> and applies
    /// the desired settings for this layer.
    /// </param>
    /// <param name="diagnostics">
    /// Optional diagnostics implementation. When <c>null</c>, <see cref="NoOpDiagnostics.Instance"/> is used.
    /// </param>
    /// <returns>The same builder instance, for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="configure"/> is <c>null</c>.
    /// </exception>
    public static LayeredRangeCacheBuilder<TRange, TData, TDomain> AddSlidingWindowLayer<TRange, TData, TDomain>(
        this LayeredRangeCacheBuilder<TRange, TData, TDomain> builder,
        Action<SlidingWindowCacheOptionsBuilder> configure,
        ICacheDiagnostics? diagnostics = null)
        where TRange : IComparable<TRange>
        where TDomain : IRangeDomain<TRange>
    {
        if (configure is null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        var domain = builder.Domain;
        return builder.AddLayer(dataSource =>
        {
            var optionsBuilder = new SlidingWindowCacheOptionsBuilder();
            configure(optionsBuilder);
            var options = optionsBuilder.Build();
            return new SlidingWindowCache<TRange, TData, TDomain>(dataSource, domain, options, diagnostics);
        });
    }
}
