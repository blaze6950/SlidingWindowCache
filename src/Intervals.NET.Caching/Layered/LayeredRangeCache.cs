using Intervals.NET.Caching.Dto;
using Intervals.NET.Domain.Abstractions;

namespace Intervals.NET.Caching.Layered;

/// <summary>
/// A wrapper around a stack of <see cref="IRangeCache{TRange,TData,TDomain}"/> instances
/// that form a multi-layer cache pipeline. Delegates to the outermost (user-facing) layer,
/// and disposes all layers from outermost to innermost.
/// </summary>
/// <typeparam name="TRange">
/// The type representing range boundaries. Must implement <see cref="IComparable{T}"/>.
/// </typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <typeparam name="TDomain">
/// The type representing the domain of the ranges. Must implement <see cref="IRangeDomain{TRange}"/>.
/// </typeparam>
public sealed class LayeredRangeCache<TRange, TData, TDomain>
    : IRangeCache<TRange, TData, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private readonly List<IRangeCache<TRange, TData, TDomain>> _layers;
    private readonly IReadOnlyList<IRangeCache<TRange, TData, TDomain>> _layersReadOnly;
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
        ArgumentNullException.ThrowIfNull(layers);

        if (layers.Count == 0)
        {
            throw new ArgumentException("At least one layer is required.", nameof(layers));
        }

        _layers = [.. layers];
        _layersReadOnly = _layers.AsReadOnly();
        _userFacingLayer = _layers[^1];
    }

    /// <summary>
    /// Gets the total number of layers in the cache stack.
    /// </summary>
    public int LayerCount => _layers.Count;

    /// <summary>
    /// Gets the ordered list of all cache layers, from deepest (index 0) to outermost (last index).
    /// </summary>
    public IReadOnlyList<IRangeCache<TRange, TData, TDomain>> Layers => _layersReadOnly;

    /// <inheritdoc/>
    public ValueTask<RangeResult<TRange, TData>> GetDataAsync(
        Range<TRange> requestedRange,
        CancellationToken cancellationToken)
        => _userFacingLayer.GetDataAsync(requestedRange, cancellationToken);

    /// <inheritdoc/>
    public async Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        for (var i = _layers.Count - 1; i >= 0; i--)
        {
            await _layers[i].WaitForIdleAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Disposes all layers from outermost to innermost, releasing all background resources.
    /// If one layer throws during disposal, remaining layers are still disposed (best-effort).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        List<Exception>? exceptions = null;

        for (var i = _layers.Count - 1; i >= 0; i--)
        {
            try
            {
                await _layers[i].DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                exceptions ??= [];
                exceptions.Add(ex);
            }
        }

        if (exceptions is not null)
        {
            throw new AggregateException("One or more layers failed during disposal.", exceptions);
        }
    }
}
