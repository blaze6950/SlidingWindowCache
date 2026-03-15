using Intervals.NET.Domain.Abstractions;
using Intervals.NET.Caching.Layered;
using Intervals.NET.Caching.SlidingWindow.Public.Configuration;
using Intervals.NET.Caching.SlidingWindow.Public.Instrumentation;

namespace Intervals.NET.Caching.SlidingWindow.Public.Cache;

/// <summary>
/// Non-generic entry point for creating cache instances via fluent builders.
/// Enables full generic type inference so callers do not need to specify type parameters explicitly.
/// </summary>
public static class SlidingWindowCacheBuilder
{
    /// <summary>
    /// Creates a <see cref="SlidingWindowCacheBuilder{TRange,TData,TDomain}"/> for building a single
    /// <see cref="SlidingWindowCache{TRange,TData,TDomain}"/> instance.
    /// </summary>
    /// <typeparam name="TRange">The type representing range boundaries. Must implement <see cref="IComparable{T}"/>.</typeparam>
    /// <typeparam name="TData">The type of data being cached.</typeparam>
    /// <typeparam name="TDomain">The range domain type. Must implement <see cref="IRangeDomain{TRange}"/>.</typeparam>
    /// <param name="dataSource">The data source from which to fetch data.</param>
    /// <param name="domain">The domain defining range characteristics.</param>
    /// <returns>A new <see cref="SlidingWindowCacheBuilder{TRange,TData,TDomain}"/> instance.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="dataSource"/> or <paramref name="domain"/> is <c>null</c>.
    /// </exception>
    public static SlidingWindowCacheBuilder<TRange, TData, TDomain> For<TRange, TData, TDomain>(
        IDataSource<TRange, TData> dataSource,
        TDomain domain)
        where TRange : IComparable<TRange>
        where TDomain : IRangeDomain<TRange>
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(domain);

        return new SlidingWindowCacheBuilder<TRange, TData, TDomain>(dataSource, domain);
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
        ArgumentNullException.ThrowIfNull(dataSource);

        if (domain is null)
        {
            throw new ArgumentNullException(nameof(domain));
        }

        return new LayeredRangeCacheBuilder<TRange, TData, TDomain>(dataSource, domain);
    }
}

/// <summary>
/// Fluent builder for constructing a single <see cref="SlidingWindowCache{TRange,TData,TDomain}"/> instance.
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
public sealed class SlidingWindowCacheBuilder<TRange, TData, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private readonly IDataSource<TRange, TData> _dataSource;
    private readonly TDomain _domain;
    private SlidingWindowCacheOptions? _options;
    private Action<SlidingWindowCacheOptionsBuilder>? _configurePending;
    private ISlidingWindowCacheDiagnostics? _diagnostics;
    private bool _built;

    internal SlidingWindowCacheBuilder(IDataSource<TRange, TData> dataSource, TDomain domain)
    {
        _dataSource = dataSource;
        _domain = domain;
    }

    /// <summary>
    /// Configures the cache with a pre-built <see cref="SlidingWindowCacheOptions"/> instance.
    /// </summary>
    /// <param name="options">The options to use.</param>
    /// <returns>This builder instance, for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="options"/> is <c>null</c>.
    /// </exception>
    public SlidingWindowCacheBuilder<TRange, TData, TDomain> WithOptions(SlidingWindowCacheOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _configurePending = null;
        return this;
    }

    /// <summary>
    /// Configures the cache options inline using a fluent <see cref="SlidingWindowCacheOptionsBuilder"/>.
    /// </summary>
    /// <param name="configure">
    /// A delegate that receives a <see cref="SlidingWindowCacheOptionsBuilder"/> and applies the desired settings.
    /// </param>
    /// <returns>This builder instance, for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="configure"/> is <c>null</c>.
    /// </exception>
    public SlidingWindowCacheBuilder<TRange, TData, TDomain> WithOptions(
        Action<SlidingWindowCacheOptionsBuilder> configure)
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
    public SlidingWindowCacheBuilder<TRange, TData, TDomain> WithDiagnostics(ISlidingWindowCacheDiagnostics diagnostics)
    {
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        return this;
    }

    /// <summary>
    /// Builds and returns a configured <see cref="ISlidingWindowCache{TRange,TData,TDomain}"/> instance.
    /// </summary>
    /// <returns>
    /// A fully wired <see cref="SlidingWindowCache{TRange,TData,TDomain}"/> ready for use.
    /// Dispose the returned instance (via <c>await using</c>) to release background resources.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="WithOptions(SlidingWindowCacheOptions)"/> or
    /// <see cref="WithOptions(Action{SlidingWindowCacheOptionsBuilder})"/> has not been called,
    /// or when <c>Build()</c> has already been called on this builder instance.
    /// </exception>
    public ISlidingWindowCache<TRange, TData, TDomain> Build()
    {
        if (_built)
        {
            throw new InvalidOperationException(
                "Build() has already been called on this builder. " +
                "Each builder instance may only produce one cache.");
        }

        var resolvedOptions = _options;

        if (resolvedOptions is null && _configurePending is not null)
        {
            var optionsBuilder = new SlidingWindowCacheOptionsBuilder();
            _configurePending(optionsBuilder);
            resolvedOptions = optionsBuilder.Build();
        }

        if (resolvedOptions is null)
        {
            throw new InvalidOperationException(
                "Options must be configured before calling Build(). " +
                "Use WithOptions() to supply a SlidingWindowCacheOptions instance or configure options inline.");
        }

        _built = true;

        return new SlidingWindowCache<TRange, TData, TDomain>(_dataSource, _domain, resolvedOptions, _diagnostics);
    }
}
