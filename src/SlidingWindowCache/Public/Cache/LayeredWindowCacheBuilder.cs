using Intervals.NET.Domain.Abstractions;
using SlidingWindowCache.Public.Configuration;
using SlidingWindowCache.Public.Instrumentation;

namespace SlidingWindowCache.Public.Cache;

/// <summary>
/// Fluent builder for constructing a multi-layer (L1/L2/L3/...) cache stack, where each
/// layer is a <see cref="WindowCache{TRange,TData,TDomain}"/> backed by the layer below it
/// via a <see cref="WindowCacheDataSourceAdapter{TRange,TData,TDomain}"/>.
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
/// <para><strong>Layer Ordering:</strong></para>
/// <para>
/// Layers are added from deepest (first call to <see cref="AddLayer"/>) to outermost (last call).
/// The first layer reads from the real <see cref="IDataSource{TRange,TData}"/> passed to
/// <see cref="Create"/>. Each subsequent layer reads from the previous layer via an adapter.
/// </para>
/// <para><strong>Recommended Configuration Patterns:</strong></para>
/// <list type="bullet">
/// <item>
///   <description>
///     <strong>Innermost (deepest) layer:</strong> Use <see cref="UserCacheReadMode.CopyOnRead"/>
///     with large <c>leftCacheSize</c>/<c>rightCacheSize</c> multipliers (e.g., 5–10x).
///     This layer absorbs rebalancing cost and provides a wide prefetch window.
///   </description>
/// </item>
/// <item>
///   <description>
///     <strong>Intermediate layers (optional):</strong> Use <see cref="UserCacheReadMode.CopyOnRead"/>
///     with moderate buffer sizes (e.g., 1–3x). These layers narrow the window toward
///     the user's typical working set.
///   </description>
/// </item>
/// <item>
///   <description>
///     <strong>Outermost (user-facing) layer:</strong> Use <see cref="UserCacheReadMode.Snapshot"/>
///     with small buffer sizes (e.g., 0.3–1.0x). This layer provides zero-allocation reads
///     with minimal memory footprint.
///   </description>
/// </item>
/// </list>
/// <para><strong>Example — Two-Layer Cache:</strong></para>
/// <code>
/// await using var cache = LayeredWindowCacheBuilder&lt;int, byte[], IntegerFixedStepDomain&gt;
///     .Create(realDataSource, domain)
///     .AddLayer(new WindowCacheOptions(         // L2: deep background cache
///         leftCacheSize: 10.0,
///         rightCacheSize: 10.0,
///         readMode: UserCacheReadMode.CopyOnRead,
///         leftThreshold: 0.3,
///         rightThreshold: 0.3))
///     .AddLayer(new WindowCacheOptions(         // L1: user-facing cache
///         leftCacheSize: 0.5,
///         rightCacheSize: 0.5,
///         readMode: UserCacheReadMode.Snapshot))
///     .Build();
///
/// var result = await cache.GetDataAsync(range, ct);
/// </code>
/// <para><strong>Example — Three-Layer Cache:</strong></para>
/// <code>
/// await using var cache = LayeredWindowCacheBuilder&lt;int, byte[], IntegerFixedStepDomain&gt;
///     .Create(realDataSource, domain)
///     .AddLayer(backgroundOptions)     // L3: large distant cache (CopyOnRead, 10x)
///     .AddLayer(midOptions)            // L2: medium intermediate cache (CopyOnRead, 2x)
///     .AddLayer(userOptions)           // L1: small user-facing cache (Snapshot, 0.5x)
///     .Build();
/// </code>
/// <para><strong>Disposal:</strong></para>
/// <para>
/// The <see cref="LayeredWindowCache{TRange,TData,TDomain}"/> returned by <see cref="Build"/>
/// owns all created cache layers and disposes them in reverse order (outermost first) when
/// <see cref="IAsyncDisposable.DisposeAsync"/> is called.
/// </para>
/// </remarks>
public sealed class LayeredWindowCacheBuilder<TRange, TData, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private readonly IDataSource<TRange, TData> _rootDataSource;
    private readonly TDomain _domain;
    private readonly List<LayerDefinition> _layers = new();

    /// <summary>
    /// Private constructor — use <see cref="Create"/> to instantiate.
    /// </summary>
    private LayeredWindowCacheBuilder(IDataSource<TRange, TData> rootDataSource, TDomain domain)
    {
        _rootDataSource = rootDataSource;
        _domain = domain;
    }

    /// <summary>
    /// Creates a new <see cref="LayeredWindowCacheBuilder{TRange,TData,TDomain}"/> rooted at
    /// the specified real data source.
    /// </summary>
    /// <param name="dataSource">
    /// The real (bottom-most) data source from which raw data is fetched.
    /// All cache layers sit above this source.
    /// </param>
    /// <param name="domain">
    /// The range domain shared by all layers.
    /// </param>
    /// <returns>A new builder instance.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="dataSource"/> is null.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="domain"/> is null.
    /// </exception>
    public static LayeredWindowCacheBuilder<TRange, TData, TDomain> Create(
        IDataSource<TRange, TData> dataSource,
        TDomain domain)
    {
        if (dataSource == null)
        {
            throw new ArgumentNullException(nameof(dataSource));
        }

        if (domain is null)
        {
            throw new ArgumentNullException(nameof(domain));
        }

        return new LayeredWindowCacheBuilder<TRange, TData, TDomain>(dataSource, domain);
    }

    /// <summary>
    /// Adds a cache layer on top of all previously added layers.
    /// </summary>
    /// <param name="options">
    /// Configuration options for this layer.
    /// The first call adds the deepest layer (closest to the real data source);
    /// each subsequent call adds a layer closer to the user.
    /// </param>
    /// <param name="diagnostics">
    /// Optional per-layer diagnostics. Pass an <see cref="ICacheDiagnostics"/> instance
    /// to observe this layer's rebalance and data-source events independently from other layers.
    /// When <see langword="null"/>, diagnostics are disabled for this layer.
    /// </param>
    /// <returns>This builder instance, for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="options"/> is null.
    /// </exception>
    public LayeredWindowCacheBuilder<TRange, TData, TDomain> AddLayer(
        WindowCacheOptions options,
        ICacheDiagnostics? diagnostics = null)
    {
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        _layers.Add(new LayerDefinition(options, diagnostics));
        return this;
    }

    /// <summary>
    /// Builds the layered cache stack and returns a <see cref="LayeredWindowCache{TRange,TData,TDomain}"/>
    /// that owns all created layers.
    /// </summary>
    /// <returns>
    /// A <see cref="LayeredWindowCache{TRange,TData,TDomain}"/> whose
    /// <see cref="IWindowCache{TRange,TData,TDomain}.GetDataAsync"/> delegates to the outermost layer.
    /// Dispose the returned instance to release all layer resources.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no layers have been added via <see cref="AddLayer"/>.
    /// </exception>
    public LayeredWindowCache<TRange, TData, TDomain> Build()
    {
        if (_layers.Count == 0)
        {
            throw new InvalidOperationException(
                "At least one layer must be added before calling Build(). " +
                "Use AddLayer() to configure one or more cache layers.");
        }

        var caches = new List<IWindowCache<TRange, TData, TDomain>>(_layers.Count);
        IDataSource<TRange, TData> currentSource = _rootDataSource;

        foreach (var layer in _layers)
        {
            var cache = new WindowCache<TRange, TData, TDomain>(
                currentSource,
                _domain,
                layer.Options,
                layer.Diagnostics);

            caches.Add(cache);

            // Wrap this cache as the data source for the next (outer) layer
            currentSource = new WindowCacheDataSourceAdapter<TRange, TData, TDomain>(cache);
        }

        return new LayeredWindowCache<TRange, TData, TDomain>(caches);
    }

    /// <summary>
    /// Captures the configuration for a single cache layer.
    /// </summary>
    private sealed record LayerDefinition(WindowCacheOptions Options, ICacheDiagnostics? Diagnostics);
}
