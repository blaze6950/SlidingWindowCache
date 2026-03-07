using Intervals.NET.Caching.Dto;
using Intervals.NET.Domain.Abstractions;

namespace Intervals.NET.Caching.Layered;

/// <summary>
/// A thin wrapper around a stack of <see cref="IRangeCache{TRange,TData,TDomain}"/> instances
/// that form a multi-layer cache pipeline. Implements <see cref="IRangeCache{TRange,TData,TDomain}"/>
/// by delegating to the outermost (user-facing) layer, and disposes all layers from outermost
/// to innermost when itself is disposed.
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
/// Instances are created exclusively by <see cref="LayeredRangeCacheBuilder{TRange,TData,TDomain}"/>.
/// Do not construct directly; use the builder to ensure correct wiring of layers.
/// </para>
/// <para><strong>Layer Order:</strong></para>
/// <para>
/// Layers are ordered from deepest (index 0, closest to the real data source) to outermost
/// (index <see cref="LayerCount"/> - 1, user-facing). All public cache operations
/// delegate to the outermost layer. Inner layers operate independently and are driven
/// by the outer layer's data source requests via <see cref="RangeCacheDataSourceAdapter{TRange,TData,TDomain}"/>.
/// </para>
/// <para><strong>Disposal:</strong></para>
/// <para>
/// Disposing this instance disposes all managed layers from outermost to innermost.
/// The outermost layer is disposed first to stop new user requests from reaching inner layers.
/// </para>
/// <para><strong>WaitForIdleAsync Semantics:</strong></para>
/// <para>
/// <see cref="WaitForIdleAsync"/> awaits all layers sequentially, from outermost to innermost.
/// This guarantees that the entire cache stack has converged: the outermost layer finishes its
/// rebalance first (which drives fetch requests into inner layers), then each inner layer is
/// awaited in turn until the deepest layer is idle.
/// </para>
/// </remarks>
public sealed class LayeredRangeCache<TRange, TData, TDomain>
    : IRangeCache<TRange, TData, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private readonly IReadOnlyList<IRangeCache<TRange, TData, TDomain>> _layers;
    private readonly IRangeCache<TRange, TData, TDomain> _userFacingLayer;

    /// <summary>
    /// Initializes a new instance of <see cref="LayeredRangeCache{TRange,TData,TDomain}"/>.
    /// </summary>
    /// <param name="layers">
    /// The ordered list of cache layers, from deepest (index 0) to outermost (last index).
    /// Must contain at least one layer.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="layers"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="layers"/> is empty.</exception>
    internal LayeredRangeCache(IReadOnlyList<IRangeCache<TRange, TData, TDomain>> layers)
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
    public int LayerCount => _layers.Count;

    /// <summary>
    /// Gets the ordered list of all cache layers, from deepest (index 0) to outermost (last index).
    /// </summary>
    public IReadOnlyList<IRangeCache<TRange, TData, TDomain>> Layers => _layers;

    /// <inheritdoc/>
    public ValueTask<RangeResult<TRange, TData>> GetDataAsync(
        Range<TRange> requestedRange,
        CancellationToken cancellationToken)
        => _userFacingLayer.GetDataAsync(requestedRange, cancellationToken);

    /// <inheritdoc/>
    /// <remarks>
    /// Awaits all layers sequentially from outermost to innermost. The outermost layer is awaited
    /// first because its rebalance drives fetch requests into inner layers; only after it is idle
    /// can inner layers be known to have received all pending work.
    /// </remarks>
    public async Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        for (var i = _layers.Count - 1; i >= 0; i--)
        {
            await _layers[i].WaitForIdleAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Disposes all layers from outermost to innermost, releasing all background resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        for (var i = _layers.Count - 1; i >= 0; i--)
        {
            await _layers[i].DisposeAsync().ConfigureAwait(false);
        }
    }
}
