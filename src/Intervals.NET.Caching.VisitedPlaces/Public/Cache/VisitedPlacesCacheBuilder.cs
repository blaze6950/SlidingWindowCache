using Intervals.NET.Domain.Abstractions;
using Intervals.NET.Caching.Layered;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction;
using Intervals.NET.Caching.VisitedPlaces.Public.Configuration;
using Intervals.NET.Caching.VisitedPlaces.Public.Instrumentation;

namespace Intervals.NET.Caching.VisitedPlaces.Public.Cache;

/// <summary>
/// Non-generic entry point for creating <see cref="VisitedPlacesCache{TRange,TData,TDomain}"/>
/// instances via fluent builders. Enables full generic type inference so callers do not need
/// to specify type parameters explicitly.
/// </summary>
/// <remarks>
/// <para><strong>Entry Points:</strong></para>
/// <list type="bullet">
/// <item>
///   <description>
///     <see cref="For{TRange,TData,TDomain}"/> — returns a
///     <see cref="VisitedPlacesCacheBuilder{TRange,TData,TDomain}"/> for building a single
///     <see cref="VisitedPlacesCache{TRange,TData,TDomain}"/>.
///   </description>
/// </item>
/// <item>
///   <description>
///     <see cref="Layered{TRange,TData,TDomain}"/> — returns a
///     <see cref="LayeredRangeCacheBuilder{TRange,TData,TDomain}"/> for building a
///     multi-layer cache stack (add layers via <c>AddVisitedPlacesLayer</c> extension method).
///   </description>
/// </item>
/// </list>
/// <para><strong>Single-Cache Example:</strong></para>
/// <code>
/// await using var cache = VisitedPlacesCacheBuilder.For(dataSource, domain)
///     .WithOptions(o => o.WithStorageStrategy(StorageStrategy.SnapshotAppendBuffer))
///     .WithEviction(
///         policies: [new MaxSegmentCountPolicy(maxCount: 50)],
///         selector: new LruEvictionSelector&lt;int, MyData&gt;())
///     .Build();
/// </code>
/// <para><strong>Layered-Cache Example:</strong></para>
/// <code>
/// await using var cache = VisitedPlacesCacheBuilder.Layered(dataSource, domain)
///     .AddVisitedPlacesLayer(
///         policies: [new MaxSegmentCountPolicy(maxCount: 100)],
///         selector: new LruEvictionSelector&lt;int, MyData&gt;())
///     .Build();
/// </code>
/// </remarks>
public static class VisitedPlacesCacheBuilder
{
    /// <summary>
    /// Creates a <see cref="VisitedPlacesCacheBuilder{TRange,TData,TDomain}"/> for building a single
    /// <see cref="VisitedPlacesCache{TRange,TData,TDomain}"/> instance.
    /// </summary>
    /// <typeparam name="TRange">The type representing range boundaries. Must implement <see cref="IComparable{T}"/>.</typeparam>
    /// <typeparam name="TData">The type of data being cached.</typeparam>
    /// <typeparam name="TDomain">The range domain type. Must implement <see cref="IRangeDomain{TRange}"/>.</typeparam>
    /// <param name="dataSource">The data source from which to fetch data.</param>
    /// <param name="domain">The domain defining range characteristics.</param>
    /// <returns>A new <see cref="VisitedPlacesCacheBuilder{TRange,TData,TDomain}"/> instance.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="dataSource"/> or <paramref name="domain"/> is <c>null</c>.
    /// </exception>
    public static VisitedPlacesCacheBuilder<TRange, TData, TDomain> For<TRange, TData, TDomain>(
        IDataSource<TRange, TData> dataSource,
        TDomain domain)
        where TRange : IComparable<TRange>
        where TDomain : IRangeDomain<TRange>
    {
        if (dataSource is null)
        {
            throw new ArgumentNullException(nameof(dataSource));
        }

        if (domain is null)
        {
            throw new ArgumentNullException(nameof(domain));
        }

        return new VisitedPlacesCacheBuilder<TRange, TData, TDomain>(dataSource, domain);
    }

    /// <summary>
    /// Creates a <see cref="LayeredRangeCacheBuilder{TRange,TData,TDomain}"/> for building a
    /// multi-layer cache stack.
    /// </summary>
    /// <typeparam name="TRange">The type representing range boundaries. Must implement <see cref="IComparable{T}"/>.</typeparam>
    /// <typeparam name="TData">The type of data being cached.</typeparam>
    /// <typeparam name="TDomain">The range domain type. Must implement <see cref="IRangeDomain{TRange}"/>.</typeparam>
    /// <param name="dataSource">The real (bottom-most) data source from which raw data is fetched.</param>
    /// <param name="domain">The range domain shared by all layers.</param>
    /// <returns>A new <see cref="LayeredRangeCacheBuilder{TRange,TData,TDomain}"/> instance.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="dataSource"/> or <paramref name="domain"/> is <c>null</c>.
    /// </exception>
    public static LayeredRangeCacheBuilder<TRange, TData, TDomain> Layered<TRange, TData, TDomain>(
        IDataSource<TRange, TData> dataSource,
        TDomain domain)
        where TRange : IComparable<TRange>
        where TDomain : IRangeDomain<TRange>
    {
        if (dataSource is null)
        {
            throw new ArgumentNullException(nameof(dataSource));
        }

        if (domain is null)
        {
            throw new ArgumentNullException(nameof(domain));
        }

        return new LayeredRangeCacheBuilder<TRange, TData, TDomain>(dataSource, domain);
    }
}

/// <summary>
/// Fluent builder for constructing a single <see cref="VisitedPlacesCache{TRange,TData,TDomain}"/> instance.
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
/// <para><strong>Construction:</strong></para>
/// <para>
/// Obtain an instance via <see cref="VisitedPlacesCacheBuilder.For{TRange,TData,TDomain}"/>, which enables
/// full generic type inference — no explicit type parameters required at the call site.
/// </para>
/// <para><strong>Required configuration:</strong></para>
/// <list type="bullet">
/// <item><description><see cref="WithOptions(VisitedPlacesCacheOptions)"/> or <see cref="WithOptions(Action{VisitedPlacesCacheOptionsBuilder})"/> — required</description></item>
/// <item><description><see cref="WithEviction"/> — required</description></item>
/// </list>
/// <para><strong>Example:</strong></para>
/// <code>
/// await using var cache = VisitedPlacesCacheBuilder.For(dataSource, domain)
///     .WithOptions(o => o.WithStorageStrategy(StorageStrategy.SnapshotAppendBuffer))
///     .WithEviction(
///         policies: [new MaxSegmentCountPolicy(maxCount: 50)],
///         selector: new LruEvictionSelector&lt;int, MyData&gt;())
///     .WithDiagnostics(myDiagnostics)
///     .Build();
/// </code>
/// </remarks>
public sealed class VisitedPlacesCacheBuilder<TRange, TData, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private readonly IDataSource<TRange, TData> _dataSource;
    private readonly TDomain _domain;
    private VisitedPlacesCacheOptions? _options;
    private Action<VisitedPlacesCacheOptionsBuilder>? _configurePending;
    private ICacheDiagnostics? _diagnostics;
    private IReadOnlyList<IEvictionPolicy<TRange, TData>>? _policies;
    private IEvictionSelector<TRange, TData>? _selector;

    internal VisitedPlacesCacheBuilder(IDataSource<TRange, TData> dataSource, TDomain domain)
    {
        _dataSource = dataSource;
        _domain = domain;
    }

    /// <summary>
    /// Configures the cache with a pre-built <see cref="VisitedPlacesCacheOptions"/> instance.
    /// </summary>
    /// <param name="options">The options to use.</param>
    /// <returns>This builder instance, for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="options"/> is <c>null</c>.
    /// </exception>
    public VisitedPlacesCacheBuilder<TRange, TData, TDomain> WithOptions(VisitedPlacesCacheOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _configurePending = null;
        return this;
    }

    /// <summary>
    /// Configures the cache options inline using a fluent <see cref="VisitedPlacesCacheOptionsBuilder"/>.
    /// </summary>
    /// <param name="configure">
    /// A delegate that receives a <see cref="VisitedPlacesCacheOptionsBuilder"/> and applies the desired settings.
    /// </param>
    /// <returns>This builder instance, for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="configure"/> is <c>null</c>.
    /// </exception>
    public VisitedPlacesCacheBuilder<TRange, TData, TDomain> WithOptions(
        Action<VisitedPlacesCacheOptionsBuilder> configure)
    {
        _options = null;
        _configurePending = configure ?? throw new ArgumentNullException(nameof(configure));
        return this;
    }

    /// <summary>
    /// Attaches a diagnostics implementation to observe cache events.
    /// When not called, <see cref="NoOpDiagnostics.Instance"/> is used.
    /// </summary>
    /// <param name="diagnostics">The diagnostics implementation to use.</param>
    /// <returns>This builder instance, for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="diagnostics"/> is <c>null</c>.
    /// </exception>
    public VisitedPlacesCacheBuilder<TRange, TData, TDomain> WithDiagnostics(ICacheDiagnostics diagnostics)
    {
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        return this;
    }

    /// <summary>
    /// Configures the eviction system with a list of policies and a selector.
    /// Both are required; <see cref="Build"/> throws if this method has not been called.
    /// </summary>
    /// <param name="policies">
    /// One or more eviction policies. Eviction is triggered when ANY policy produces an exceeded pressure (OR semantics).
    /// Must be non-null and non-empty.
    /// </param>
    /// <param name="selector">
    /// The eviction selector responsible for determining the order in which candidates are considered for eviction.
    /// Must be non-null.
    /// </param>
    /// <returns>This builder instance, for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="policies"/> or <paramref name="selector"/> is <c>null</c>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="policies"/> is empty.
    /// </exception>
    public VisitedPlacesCacheBuilder<TRange, TData, TDomain> WithEviction(
        IReadOnlyList<IEvictionPolicy<TRange, TData>> policies,
        IEvictionSelector<TRange, TData> selector)
    {
        if (policies is null)
        {
            throw new ArgumentNullException(nameof(policies));
        }

        if (policies.Count == 0)
        {
            throw new ArgumentException(
                "At least one eviction policy must be provided.",
                nameof(policies));
        }

        _policies = policies;
        _selector = selector ?? throw new ArgumentNullException(nameof(selector));
        return this;
    }

    /// <summary>
    /// Builds and returns a configured <see cref="IVisitedPlacesCache{TRange,TData,TDomain}"/> instance.
    /// </summary>
    /// <returns>
    /// A fully wired <see cref="VisitedPlacesCache{TRange,TData,TDomain}"/> ready for use.
    /// Dispose the returned instance (via <c>await using</c>) to release background resources.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="WithOptions(VisitedPlacesCacheOptions)"/> or
    /// <see cref="WithOptions(Action{VisitedPlacesCacheOptionsBuilder})"/> has not been called,
    /// or when <see cref="WithEviction"/> has not been called.
    /// </exception>
    public IVisitedPlacesCache<TRange, TData, TDomain> Build()
    {
        var resolvedOptions = _options;

        if (resolvedOptions is null && _configurePending is not null)
        {
            var optionsBuilder = new VisitedPlacesCacheOptionsBuilder();
            _configurePending(optionsBuilder);
            resolvedOptions = optionsBuilder.Build();
        }

        if (resolvedOptions is null)
        {
            throw new InvalidOperationException(
                "Options must be configured before calling Build(). " +
                "Use WithOptions() to supply a VisitedPlacesCacheOptions instance or configure options inline.");
        }

        if (_policies is null || _selector is null)
        {
            throw new InvalidOperationException(
                "Eviction must be configured before calling Build(). " +
                "Use WithEviction() to supply policies and a selector.");
        }

        return new VisitedPlacesCache<TRange, TData, TDomain>(
            _dataSource,
            _domain,
            resolvedOptions,
            _policies,
            _selector,
            _diagnostics);
    }
}
