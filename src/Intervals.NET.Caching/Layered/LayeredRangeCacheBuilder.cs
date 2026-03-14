using Intervals.NET.Domain.Abstractions;

namespace Intervals.NET.Caching.Layered;

/// <summary>
/// Fluent builder for constructing a multi-layer cache stack,
/// where each layer is any <see cref="IRangeCache{TRange,TData,TDomain}"/> implementation
/// backed by the layer below it via a <see cref="RangeCacheDataSourceAdapter{TRange,TData,TDomain}"/>.
/// </summary>
/// <typeparam name="TRange">
/// The type representing range boundaries. Must implement <see cref="IComparable{T}"/>.
/// </typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <typeparam name="TDomain">
/// The type representing the domain of the ranges. Must implement <see cref="IRangeDomain{TRange}"/>.
/// </typeparam>
public sealed class LayeredRangeCacheBuilder<TRange, TData, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private readonly IDataSource<TRange, TData> _rootDataSource;
    private readonly TDomain _domain;
    private readonly List<Func<IDataSource<TRange, TData>, IRangeCache<TRange, TData, TDomain>>> _factories = new();
    private bool _built;

    /// <summary>
    /// Initializes a new <see cref="LayeredRangeCacheBuilder{TRange,TData,TDomain}"/>.
    /// </summary>
    /// <param name="rootDataSource">
    /// The real (bottom-most) data source from which raw data is fetched by the deepest layer.
    /// </param>
    /// <param name="domain">The range domain shared by all layers.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="rootDataSource"/> or <paramref name="domain"/> is null.
    /// </exception>
    public LayeredRangeCacheBuilder(IDataSource<TRange, TData> rootDataSource, TDomain domain)
    {
        _rootDataSource = rootDataSource ?? throw new ArgumentNullException(nameof(rootDataSource));
        _domain = domain ?? throw new ArgumentNullException(nameof(domain));
    }

    /// <summary>
    /// Gets the domain passed at construction, available to extension methods that need it.
    /// </summary>
    public TDomain Domain => _domain;

    /// <summary>
    /// Adds a cache layer on top of all previously added layers using a factory delegate.
    /// </summary>
    /// <param name="factory">
    /// A factory that receives the <see cref="IDataSource{TRange,TData}"/> for this layer
    /// (either the root data source for the first layer, or a
    /// <see cref="RangeCacheDataSourceAdapter{TRange,TData,TDomain}"/> wrapping the previous layer)
    /// and returns a fully configured <see cref="IRangeCache{TRange,TData,TDomain}"/> instance.
    /// </param>
    /// <returns>This builder instance, for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="factory"/> is null.</exception>
    public LayeredRangeCacheBuilder<TRange, TData, TDomain> AddLayer(
        Func<IDataSource<TRange, TData>, IRangeCache<TRange, TData, TDomain>> factory)
    {
        _factories.Add(factory ?? throw new ArgumentNullException(nameof(factory)));
        return this;
    }

    /// <summary>
    /// Builds the layered cache stack and returns an <see cref="IRangeCache{TRange,TData,TDomain}"/>
    /// that owns all created layers.
    /// If a factory throws during construction, all previously created layers are disposed
    /// before the exception propagates.
    /// </summary>
    /// <returns>
    /// A <see cref="ValueTask{TResult}"/> that completes with a
    /// <see cref="LayeredRangeCache{TRange,TData,TDomain}"/> whose
    /// <see cref="IRangeCache{TRange,TData,TDomain}.GetDataAsync"/> delegates to the outermost layer.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no layers have been added via <see cref="AddLayer"/>,
    /// or when <see cref="BuildAsync"/> has already been called on this builder instance.
    /// </exception>
    public async ValueTask<IRangeCache<TRange, TData, TDomain>> BuildAsync()
    {
        if (_built)
        {
            throw new InvalidOperationException(
                "BuildAsync() has already been called on this builder instance. " +
                "Create a new builder to construct another cache stack.");
        }

        if (_factories.Count == 0)
        {
            throw new InvalidOperationException(
                "At least one layer must be added before calling BuildAsync(). " +
                "Use AddLayer() to configure one or more cache layers.");
        }

        var caches = new List<IRangeCache<TRange, TData, TDomain>>(_factories.Count);
        var currentSource = _rootDataSource;

        try
        {
            foreach (var factory in _factories)
            {
                var cache = factory(currentSource);
                caches.Add(cache);

                // Wrap this cache as the data source for the next (outer) layer
                currentSource = new RangeCacheDataSourceAdapter<TRange, TData, TDomain>(cache);
            }
        }
        catch
        {
            // Dispose all successfully created layers to prevent resource leaks
            // if a factory throws partway through construction.
            foreach (var cache in caches)
            {
                try
                {
                    await cache.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort cleanup: continue disposing remaining layers
                    // even if one layer's disposal fails.
                }
            }

            throw;
        }

        _built = true;
        return new LayeredRangeCache<TRange, TData, TDomain>(caches);
    }
}
