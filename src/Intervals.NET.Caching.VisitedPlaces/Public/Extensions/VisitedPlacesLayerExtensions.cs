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
///         options: new VisitedPlacesCacheOptions(),
///         evaluators: [new MaxSegmentCountEvaluator(maxCount: 100)],
///         executor: new LruEvictionExecutor&lt;int, MyData&gt;())
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
    /// a pre-built <see cref="VisitedPlacesCacheOptions"/> instance.
    /// </summary>
    /// <typeparam name="TRange">The type representing range boundaries. Must implement <see cref="IComparable{T}"/>.</typeparam>
    /// <typeparam name="TData">The type of data being cached.</typeparam>
    /// <typeparam name="TDomain">The range domain type. Must implement <see cref="IRangeDomain{TRange}"/>.</typeparam>
    /// <param name="builder">The layered cache builder to add the layer to.</param>
    /// <param name="evaluators">
    /// One or more eviction evaluators. Eviction is triggered when ANY evaluator fires (OR semantics).
    /// Must be non-null and non-empty.
    /// </param>
    /// <param name="executor">
    /// The eviction executor responsible for selecting which segments to evict and maintaining statistics.
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
    /// Thrown when <paramref name="evaluators"/> or <paramref name="executor"/> is <c>null</c>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="evaluators"/> is empty.
    /// </exception>
    public static LayeredRangeCacheBuilder<TRange, TData, TDomain> AddVisitedPlacesLayer<TRange, TData, TDomain>(
        this LayeredRangeCacheBuilder<TRange, TData, TDomain> builder,
        IReadOnlyList<IEvictionEvaluator<TRange, TData>> evaluators,
        IEvictionExecutor<TRange, TData> executor,
        VisitedPlacesCacheOptions? options = null,
        ICacheDiagnostics? diagnostics = null)
        where TRange : IComparable<TRange>
        where TDomain : IRangeDomain<TRange>
    {
        if (evaluators is null)
        {
            throw new ArgumentNullException(nameof(evaluators));
        }

        if (evaluators.Count == 0)
        {
            throw new ArgumentException(
                "At least one eviction evaluator must be provided.",
                nameof(evaluators));
        }

        if (executor is null)
        {
            throw new ArgumentNullException(nameof(executor));
        }

        var domain = builder.Domain;
        var resolvedOptions = options ?? new VisitedPlacesCacheOptions();
        return builder.AddLayer(dataSource =>
            new VisitedPlacesCache<TRange, TData, TDomain>(
                dataSource, domain, resolvedOptions, evaluators, executor, diagnostics));
    }

    /// <summary>
    /// Adds a <see cref="VisitedPlacesCache{TRange,TData,TDomain}"/> layer configured inline
    /// using a fluent <see cref="VisitedPlacesCacheOptionsBuilder"/>.
    /// </summary>
    /// <typeparam name="TRange">The type representing range boundaries. Must implement <see cref="IComparable{T}"/>.</typeparam>
    /// <typeparam name="TData">The type of data being cached.</typeparam>
    /// <typeparam name="TDomain">The range domain type. Must implement <see cref="IRangeDomain{TRange}"/>.</typeparam>
    /// <param name="builder">The layered cache builder to add the layer to.</param>
    /// <param name="evaluators">
    /// One or more eviction evaluators. Must be non-null and non-empty.
    /// </param>
    /// <param name="executor">
    /// The eviction executor. Must be non-null.
    /// </param>
    /// <param name="configure">
    /// A delegate that receives a <see cref="VisitedPlacesCacheOptionsBuilder"/> and applies
    /// the desired settings for this layer. When <c>null</c>, default options are used.
    /// </param>
    /// <param name="diagnostics">
    /// Optional diagnostics implementation. When <c>null</c>, <see cref="NoOpDiagnostics.Instance"/> is used.
    /// </param>
    /// <returns>The same builder instance, for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="evaluators"/> or <paramref name="executor"/> is <c>null</c>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="evaluators"/> is empty.
    /// </exception>
    public static LayeredRangeCacheBuilder<TRange, TData, TDomain> AddVisitedPlacesLayer<TRange, TData, TDomain>(
        this LayeredRangeCacheBuilder<TRange, TData, TDomain> builder,
        IReadOnlyList<IEvictionEvaluator<TRange, TData>> evaluators,
        IEvictionExecutor<TRange, TData> executor,
        Action<VisitedPlacesCacheOptionsBuilder> configure,
        ICacheDiagnostics? diagnostics = null)
        where TRange : IComparable<TRange>
        where TDomain : IRangeDomain<TRange>
    {
        if (evaluators is null)
        {
            throw new ArgumentNullException(nameof(evaluators));
        }

        if (evaluators.Count == 0)
        {
            throw new ArgumentException(
                "At least one eviction evaluator must be provided.",
                nameof(evaluators));
        }

        if (executor is null)
        {
            throw new ArgumentNullException(nameof(executor));
        }

        if (configure is null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        var domain = builder.Domain;
        return builder.AddLayer(dataSource =>
        {
            var optionsBuilder = new VisitedPlacesCacheOptionsBuilder();
            configure(optionsBuilder);
            var options = optionsBuilder.Build();
            return new VisitedPlacesCache<TRange, TData, TDomain>(
                dataSource, domain, options, evaluators, executor, diagnostics);
        });
    }
}
