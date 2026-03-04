using Intervals.NET;
using Intervals.NET.Domain.Abstractions;
using Intervals.NET.Caching.Public.Configuration;
using Intervals.NET.Caching.Public.Dto;

namespace Intervals.NET.Caching.Public.Cache;

/// <summary>
/// A thin wrapper around a stack of <see cref="WindowCache{TRange,TData,TDomain}"/> instances
/// that form a multi-layer cache pipeline. Implements <see cref="IWindowCache{TRange,TData,TDomain}"/>
/// by delegating to the outermost (user-facing) layer, and disposes all layers in the correct
/// order when itself is disposed.
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
/// Instances are created exclusively by <see cref="LayeredWindowCacheBuilder{TRange,TData,TDomain}"/>.
/// Do not construct directly; use the builder to ensure correct wiring of layers.
/// </para>
/// <para><strong>Layer Order:</strong></para>
/// <para>
/// Layers are ordered from deepest (index 0, closest to the real data source) to outermost
/// (index <see cref="LayerCount"/> - 1, user-facing). All public cache operations
/// delegate to the outermost layer. Inner layers operate independently and are driven
/// by the outer layer's data source requests (via <see cref="WindowCacheDataSourceAdapter{TRange,TData,TDomain}"/>).
/// </para>
/// <para><strong>Disposal:</strong></para>
/// <para>
/// Disposing this instance disposes all managed layers in order from outermost to innermost.
/// The outermost layer is disposed first to stop new user requests from reaching inner layers.
/// Each layer's background loops are stopped gracefully before the next layer is disposed.
/// </para>
/// <para><strong>WaitForIdleAsync Semantics:</strong></para>
/// <para>
/// <see cref="WaitForIdleAsync"/> awaits all layers sequentially, from outermost to innermost.
/// This guarantees that the entire cache stack has converged: the outermost layer finishes its
/// rebalance first (which drives fetch requests into inner layers), then each inner layer is
/// awaited in turn until the deepest layer is idle.
/// </para>
/// <para>
/// This full-stack idle guarantee is required for correct behavior of the
/// <c>GetDataAndWaitForIdleAsync</c> strong consistency extension method when used with a
/// <see cref="LayeredWindowCache{TRange,TData,TDomain}"/>: a caller waiting for strong
/// consistency needs all layers to have converged, not just the outermost one.
/// </para>
/// </remarks>
public sealed class LayeredWindowCache<TRange, TData, TDomain>
    : IWindowCache<TRange, TData, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private readonly IReadOnlyList<IWindowCache<TRange, TData, TDomain>> _layers;
    private readonly IWindowCache<TRange, TData, TDomain> _userFacingLayer;

    /// <summary>
    /// Initializes a new instance of <see cref="LayeredWindowCache{TRange,TData,TDomain}"/>.
    /// </summary>
    /// <param name="layers">
    /// The ordered list of cache layers, from deepest (index 0) to outermost (last index).
    /// Must contain at least one layer.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="layers"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="layers"/> is empty.
    /// </exception>
    internal LayeredWindowCache(IReadOnlyList<IWindowCache<TRange, TData, TDomain>> layers)
    {
        if (layers == null)
        {
            throw new ArgumentNullException(nameof(layers));
        }

        if (layers.Count == 0)
        {
            throw new ArgumentException("At least one layer is required.", nameof(layers));
        }

        _layers = layers;
        _userFacingLayer = layers[^1];
    }

    /// <summary>
    /// Gets the total number of layers in the cache stack.
    /// </summary>
    /// <remarks>
    /// Layers are ordered from deepest (index 0, closest to the real data source) to
    /// outermost (last index, closest to the user).
    /// </remarks>
    public int LayerCount => _layers.Count;

    /// <summary>
    /// Gets the ordered list of all cache layers, from deepest (index 0) to outermost (last index).
    /// </summary>
    /// <remarks>
    /// <para><strong>Layer Order:</strong></para>
    /// <para>
    /// Index 0 is the deepest layer (closest to the real data source). The last index
    /// (<c>Layers.Count - 1</c>) is the outermost, user-facing layer — the same layer that
    /// <see cref="IWindowCache{TRange,TData,TDomain}.GetDataAsync"/> delegates to.
    /// </para>
    /// <para><strong>Per-Layer Operations:</strong></para>
    /// <para>
    /// Each layer exposes the full <see cref="IWindowCache{TRange,TData,TDomain}"/> interface.
    /// Use this property to update options or inspect the current runtime options of a specific layer:
    /// </para>
    /// <code>
    /// // Update options on the innermost (background) layer
    /// layeredCache.Layers[0].UpdateRuntimeOptions(u => u.WithLeftCacheSize(8.0));
    ///
    /// // Inspect options of the outermost (user-facing) layer
    /// var outerOptions = layeredCache.Layers[^1].CurrentRuntimeOptions;
    /// </code>
    /// </remarks>
    public IReadOnlyList<IWindowCache<TRange, TData, TDomain>> Layers => _layers;

    /// <inheritdoc/>
    /// <remarks>
    /// Delegates to the outermost (user-facing) layer. Data is served from that layer's
    /// cache window, which is backed by the next inner layer via
    /// <see cref="WindowCacheDataSourceAdapter{TRange,TData,TDomain}"/>.
    /// </remarks>
    public ValueTask<RangeResult<TRange, TData>> GetDataAsync(
        Range<TRange> requestedRange,
        CancellationToken cancellationToken)
        => _userFacingLayer.GetDataAsync(requestedRange, cancellationToken);

    /// <inheritdoc/>
    /// <remarks>
    /// Awaits all layers sequentially from outermost to innermost. The outermost layer is awaited
    /// first because its rebalance drives fetch requests into inner layers; only after it is idle
    /// can inner layers be known to have received all pending work. Each subsequent inner layer is
    /// then awaited in order, ensuring the full cache stack has converged before this task completes.
    /// </remarks>
    public async Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        // Outermost to innermost: outer rebalance drives inner fetches, so outer must finish first.
        for (var i = _layers.Count - 1; i >= 0; i--)
        {
            await _layers[i].WaitForIdleAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Delegates to the outermost (user-facing) layer. To update a specific inner layer,
    /// access it via <see cref="Layers"/> and call <see cref="IWindowCache{TRange,TData,TDomain}.UpdateRuntimeOptions"/>
    /// on that layer directly.
    /// </remarks>
    public void UpdateRuntimeOptions(Action<RuntimeOptionsUpdateBuilder> configure)
        => _userFacingLayer.UpdateRuntimeOptions(configure);

    /// <inheritdoc/>
    /// <remarks>
    /// Returns the runtime options of the outermost (user-facing) layer. To inspect a specific
    /// inner layer's options, access it via <see cref="Layers"/> and read
    /// <see cref="IWindowCache{TRange,TData,TDomain}.CurrentRuntimeOptions"/> on that layer.
    /// </remarks>
    public RuntimeOptionsSnapshot CurrentRuntimeOptions => _userFacingLayer.CurrentRuntimeOptions;

    /// <summary>
    /// Disposes all layers from outermost to innermost, releasing all background resources.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Disposal order is outermost-first: the user-facing layer is stopped before inner layers,
    /// ensuring no new requests flow into inner layers during their disposal.
    /// </para>
    /// <para>
    /// Each layer's <see cref="IAsyncDisposable.DisposeAsync"/> gracefully stops background
    /// rebalance loops and releases all associated resources (channels, cancellation tokens,
    /// semaphores) before proceeding to the next inner layer.
    /// </para>
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        // Dispose outermost to innermost: stop user-facing layer first,
        // then work inward so inner layers are not disposing while outer still runs.
        for (var i = _layers.Count - 1; i >= 0; i--)
        {
            await _layers[i].DisposeAsync().ConfigureAwait(false);
        }
    }
}
