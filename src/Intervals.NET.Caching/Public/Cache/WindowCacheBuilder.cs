using Intervals.NET.Domain.Abstractions;
using Intervals.NET.Caching.Public.Configuration;
using Intervals.NET.Caching.Public.Instrumentation;

namespace Intervals.NET.Caching.Public.Cache;

/// <summary>
/// Non-generic entry point for creating cache instances via fluent builders.
/// Enables full generic type inference so callers do not need to specify type parameters explicitly.
/// </summary>
/// <remarks>
/// <para><strong>Entry Points:</strong></para>
/// <list type="bullet">
/// <item>
///   <description>
///     <see cref="For{TRange,TData,TDomain}"/> — returns a
///     <see cref="WindowCacheBuilder{TRange,TData,TDomain}"/> for building a single
///     <see cref="WindowCache{TRange,TData,TDomain}"/>.
///   </description>
/// </item>
/// <item>
///   <description>
///     <see cref="Layered{TRange,TData,TDomain}"/> — returns a
///     <see cref="LayeredWindowCacheBuilder{TRange,TData,TDomain}"/> for building a
///     multi-layer <see cref="LayeredWindowCache{TRange,TData,TDomain}"/>.
///   </description>
/// </item>
/// </list>
/// <para><strong>Single-Cache Example:</strong></para>
/// <code>
/// await using var cache = WindowCacheBuilder.For(dataSource, domain)
///     .WithOptions(o =&gt; o
///         .WithCacheSize(1.0)
///         .WithThresholds(0.2))
///     .Build();
/// </code>
/// <para><strong>Layered-Cache Example:</strong></para>
/// <code>
/// await using var cache = WindowCacheBuilder.Layered(dataSource, domain)
///     .AddLayer(o =&gt; o.WithCacheSize(10.0).WithReadMode(UserCacheReadMode.CopyOnRead))
///     .AddLayer(o =&gt; o.WithCacheSize(0.5))
///     .Build();
/// </code>
/// </remarks>
public static class WindowCacheBuilder
{
    /// <summary>
    /// Creates a <see cref="WindowCacheBuilder{TRange,TData,TDomain}"/> for building a single
    /// <see cref="WindowCache{TRange,TData,TDomain}"/> instance.
    /// </summary>
    /// <typeparam name="TRange">The type representing range boundaries. Must implement <see cref="IComparable{T}"/>.</typeparam>
    /// <typeparam name="TData">The type of data being cached.</typeparam>
    /// <typeparam name="TDomain">The range domain type. Must implement <see cref="IRangeDomain{TRange}"/>.</typeparam>
    /// <param name="dataSource">The data source from which to fetch data.</param>
    /// <param name="domain">The domain defining range characteristics.</param>
    /// <returns>A new <see cref="WindowCacheBuilder{TRange,TData,TDomain}"/> instance.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="dataSource"/> or <paramref name="domain"/> is <c>null</c>.
    /// </exception>
    public static WindowCacheBuilder<TRange, TData, TDomain> For<TRange, TData, TDomain>(
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

        return new WindowCacheBuilder<TRange, TData, TDomain>(dataSource, domain);
    }

    /// <summary>
    /// Creates a <see cref="LayeredWindowCacheBuilder{TRange,TData,TDomain}"/> for building a
    /// multi-layer cache stack.
    /// </summary>
    /// <typeparam name="TRange">The type representing range boundaries. Must implement <see cref="IComparable{T}"/>.</typeparam>
    /// <typeparam name="TData">The type of data being cached.</typeparam>
    /// <typeparam name="TDomain">The range domain type. Must implement <see cref="IRangeDomain{TRange}"/>.</typeparam>
    /// <param name="dataSource">The real (bottom-most) data source from which raw data is fetched.</param>
    /// <param name="domain">The range domain shared by all layers.</param>
    /// <returns>A new <see cref="LayeredWindowCacheBuilder{TRange,TData,TDomain}"/> instance.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="dataSource"/> or <paramref name="domain"/> is <c>null</c>.
    /// </exception>
    public static LayeredWindowCacheBuilder<TRange, TData, TDomain> Layered<TRange, TData, TDomain>(
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

        return new LayeredWindowCacheBuilder<TRange, TData, TDomain>(dataSource, domain);
    }
}

/// <summary>
/// Fluent builder for constructing a single <see cref="WindowCache{TRange,TData,TDomain}"/> instance.
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
/// Obtain an instance via <see cref="WindowCacheBuilder.For{TRange,TData,TDomain}"/>, which enables
/// full generic type inference — no explicit type parameters required at the call site.
/// </para>
/// <para><strong>Options:</strong></para>
/// <para>
/// Call <see cref="WithOptions(WindowCacheOptions)"/> to supply a pre-built
/// <see cref="WindowCacheOptions"/> instance, or <see cref="WithOptions(Action{WindowCacheOptionsBuilder})"/>
/// to configure options inline using a fluent <see cref="WindowCacheOptionsBuilder"/>.
/// Options are required; <see cref="Build"/> throws if they have not been set.
/// </para>
/// <para><strong>Example — Inline Options:</strong></para>
/// <code>
/// await using var cache = WindowCacheBuilder.For(dataSource, domain)
///     .WithOptions(o =&gt; o
///         .WithCacheSize(1.0)
///         .WithReadMode(UserCacheReadMode.Snapshot)
///         .WithThresholds(0.2))
///     .WithDiagnostics(myDiagnostics)
///     .Build();
/// </code>
/// <para><strong>Example — Pre-built Options:</strong></para>
/// <code>
/// var options = new WindowCacheOptions(1.0, 2.0, UserCacheReadMode.Snapshot, 0.2, 0.2);
///
/// await using var cache = WindowCacheBuilder.For(dataSource, domain)
///     .WithOptions(options)
///     .Build();
/// </code>
/// </remarks>
public sealed class WindowCacheBuilder<TRange, TData, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private readonly IDataSource<TRange, TData> _dataSource;
    private readonly TDomain _domain;
    private WindowCacheOptions? _options;
    private Action<WindowCacheOptionsBuilder>? _configurePending;
    private ICacheDiagnostics? _diagnostics;

    internal WindowCacheBuilder(IDataSource<TRange, TData> dataSource, TDomain domain)
    {
        _dataSource = dataSource;
        _domain = domain;
    }

    /// <summary>
    /// Configures the cache with a pre-built <see cref="WindowCacheOptions"/> instance.
    /// </summary>
    /// <param name="options">The options to use.</param>
    /// <returns>This builder instance, for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="options"/> is <c>null</c>.
    /// </exception>
    public WindowCacheBuilder<TRange, TData, TDomain> WithOptions(WindowCacheOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _configurePending = null;
        return this;
    }

    /// <summary>
    /// Configures the cache options inline using a fluent <see cref="WindowCacheOptionsBuilder"/>.
    /// </summary>
    /// <param name="configure">
    /// A delegate that receives a <see cref="WindowCacheOptionsBuilder"/> and applies the desired settings.
    /// </param>
    /// <returns>This builder instance, for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="configure"/> is <c>null</c>.
    /// </exception>
    public WindowCacheBuilder<TRange, TData, TDomain> WithOptions(
        Action<WindowCacheOptionsBuilder> configure)
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
    public WindowCacheBuilder<TRange, TData, TDomain> WithDiagnostics(ICacheDiagnostics diagnostics)
    {
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        return this;
    }

    /// <summary>
    /// Builds and returns a configured <see cref="IWindowCache{TRange,TData,TDomain}"/> instance.
    /// </summary>
    /// <returns>
    /// A fully wired <see cref="WindowCache{TRange,TData,TDomain}"/> ready for use.
    /// Dispose the returned instance (via <c>await using</c>) to release background resources.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="WithOptions(WindowCacheOptions)"/> or
    /// <see cref="WithOptions(Action{WindowCacheOptionsBuilder})"/> has not been called.
    /// </exception>
    public IWindowCache<TRange, TData, TDomain> Build()
    {
        var resolvedOptions = _options;

        if (resolvedOptions is null && _configurePending is not null)
        {
            var optionsBuilder = new WindowCacheOptionsBuilder();
            _configurePending(optionsBuilder);
            resolvedOptions = optionsBuilder.Build();
        }

        if (resolvedOptions is null)
        {
            throw new InvalidOperationException(
                "Options must be configured before calling Build(). " +
                "Use WithOptions() to supply a WindowCacheOptions instance or configure options inline.");
        }

        return new WindowCache<TRange, TData, TDomain>(_dataSource, _domain, resolvedOptions, _diagnostics);
    }
}
