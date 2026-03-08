using Intervals.NET.Domain.Abstractions;
using Intervals.NET.Caching.Layered;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction;
using Intervals.NET.Caching.VisitedPlaces.Public.Cache;
using Intervals.NET.Caching.VisitedPlaces.Public.Configuration;
using Intervals.NET.Caching.VisitedPlaces.Public.Instrumentation;

namespace Intervals.NET.Caching.VisitedPlaces.Public.Extensions;

/// <summary>
/// Extension methods on <see cref="LayeredRangeCacheBuilder{TRange,TData,TDomain}"/> that add
/// a <see cref="VisitedPlacesCache{TRange,TData,TDomain}"/> layer to the cache stack.
/// </summary>
/// <remarks>
/// <para><strong>Usage:</strong></para>
/// <code>
/// await using var cache = VisitedPlacesCacheBuilder.Layered(dataSource, domain)
///     .AddVisitedPlacesLayer(
///         options: new VisitedPlacesCacheOptions&lt;int, MyData&gt;(),
///         policies: [new MaxSegmentCountPolicy(maxCount: 100)],
///         selector: new LruEvictionSelector&lt;int, MyData&gt;())
///     .Build();
/// </code>
/// <para>
/// Each call wraps the previous layer (or root data source) in a
/// <see cref="RangeCacheDataSourceAdapter{TRange,TData,TDomain}"/> and passes it to a new
/// <see cref="VisitedPlacesCache{TRange,TData,TDomain}"/> instance.
/// </para>
/// </remarks>
public static class VisitedPlacesLayerExtensions
{
    /// <summary>
    /// Adds a <see cref="VisitedPlacesCache{TRange,TData,TDomain}"/> layer configured with
    /// a pre-built <see cref="VisitedPlacesCacheOptions{TRange,TData}"/> instance.
    /// </summary>
    /// <typeparam name="TRange">The type representing range boundaries. Must implement <see cref="IComparable{T}"/>.</typeparam>
    /// <typeparam name="TData">The type of data being cached.</typeparam>
    /// <typeparam name="TDomain">The range domain type. Must implement <see cref="IRangeDomain{TRange}"/>.</typeparam>
    /// <param name="builder">The layered cache builder to add the layer to.</param>
    /// <param name="policies">
    /// One or more eviction policies. Eviction is triggered when ANY produces an exceeded pressure (OR semantics).
    /// Must be non-null and non-empty.
    /// </param>
    /// <param name="selector">
    /// The eviction selector responsible for determining candidate ordering for eviction.
    /// Must be non-null.
    /// </param>
    /// <param name="options">
    /// The configuration options for this layer's VisitedPlacesCache.
    /// When <c>null</c>, default options are used.
    /// </param>
    /// <param name="diagnostics">
    /// Optional diagnostics implementation. When <c>null</c>, <see cref="NoOpDiagnostics.Instance"/> is used.
    /// </param>
    /// <returns>The same builder instance, for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="policies"/> or <paramref name="selector"/> is <c>null</c>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="policies"/> is empty.
    /// </exception>
    public static LayeredRangeCacheBuilder<TRange, TData, TDomain> AddVisitedPlacesLayer<TRange, TData, TDomain>(
        this LayeredRangeCacheBuilder<TRange, TData, TDomain> builder,
        IReadOnlyList<IEvictionPolicy<TRange, TData>> policies,
        IEvictionSelector<TRange, TData> selector,
        VisitedPlacesCacheOptions<TRange, TData>? options = null,
        ICacheDiagnostics? diagnostics = null)
        where TRange : IComparable<TRange>
        where TDomain : IRangeDomain<TRange>
    {
        ArgumentNullException.ThrowIfNull(policies);

        if (policies.Count == 0)
        {
            throw new ArgumentException(
                "At least one eviction policy must be provided.",
                nameof(policies));
        }

        ArgumentNullException.ThrowIfNull(selector);

        var domain = builder.Domain;
        var resolvedOptions = options ?? new VisitedPlacesCacheOptions<TRange, TData>();
        return builder.AddLayer(dataSource =>
            new VisitedPlacesCache<TRange, TData, TDomain>(
                dataSource, domain, resolvedOptions, policies, selector, diagnostics));
    }

    /// <summary>
    /// Adds a <see cref="VisitedPlacesCache{TRange,TData,TDomain}"/> layer configured inline
    /// using a fluent <see cref="VisitedPlacesCacheOptionsBuilder{TRange,TData}"/>.
    /// </summary>
    /// <typeparam name="TRange">The type representing range boundaries. Must implement <see cref="IComparable{T}"/>.</typeparam>
    /// <typeparam name="TData">The type of data being cached.</typeparam>
    /// <typeparam name="TDomain">The range domain type. Must implement <see cref="IRangeDomain{TRange}"/>.</typeparam>
    /// <param name="builder">The layered cache builder to add the layer to.</param>
    /// <param name="policies">
    /// One or more eviction policies. Must be non-null and non-empty.
    /// </param>
    /// <param name="selector">
    /// The eviction selector. Must be non-null.
    /// </param>
    /// <param name="configure">
    /// A delegate that receives a <see cref="VisitedPlacesCacheOptionsBuilder{TRange,TData}"/> and applies
    /// the desired settings for this layer. When <c>null</c>, default options are used.
    /// </param>
    /// <param name="diagnostics">
    /// Optional diagnostics implementation. When <c>null</c>, <see cref="NoOpDiagnostics.Instance"/> is used.
    /// </param>
    /// <returns>The same builder instance, for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="policies"/> or <paramref name="selector"/> is <c>null</c>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="policies"/> is empty.
    /// </exception>
    public static LayeredRangeCacheBuilder<TRange, TData, TDomain> AddVisitedPlacesLayer<TRange, TData, TDomain>(
        this LayeredRangeCacheBuilder<TRange, TData, TDomain> builder,
        IReadOnlyList<IEvictionPolicy<TRange, TData>> policies,
        IEvictionSelector<TRange, TData> selector,
        Action<VisitedPlacesCacheOptionsBuilder<TRange, TData>> configure,
        ICacheDiagnostics? diagnostics = null)
        where TRange : IComparable<TRange>
        where TDomain : IRangeDomain<TRange>
    {
        ArgumentNullException.ThrowIfNull(policies);

        if (policies.Count == 0)
        {
            throw new ArgumentException(
                "At least one eviction policy must be provided.",
                nameof(policies));
        }

        ArgumentNullException.ThrowIfNull(selector);

        ArgumentNullException.ThrowIfNull(configure);

        var domain = builder.Domain;
        return builder.AddLayer(dataSource =>
        {
            var optionsBuilder = new VisitedPlacesCacheOptionsBuilder<TRange, TData>();
            configure(optionsBuilder);
            var options = optionsBuilder.Build();
            return new VisitedPlacesCache<TRange, TData, TDomain>(
                dataSource, domain, options, policies, selector, diagnostics);
        });
    }
}
