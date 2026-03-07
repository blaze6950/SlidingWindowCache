namespace Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Pressure;

/// <summary>
/// Aggregates multiple <see cref="IEvictionPressure{TRange,TData}"/> instances into a single
/// composite pressure. The constraint is exceeded when ANY child pressure is exceeded.
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <remarks>
/// <para><strong>OR Semantics (Invariant VPC.E.1a):</strong></para>
/// <para>
/// <see cref="IsExceeded"/> returns <c>true</c> when at least one child pressure is exceeded.
/// The executor continues removing segments until ALL child pressures are satisfied
/// (i.e., <see cref="IsExceeded"/> becomes <c>false</c>).
/// </para>
/// <para><strong>Reduce propagation:</strong> <see cref="Reduce"/> is forwarded to ALL child pressures
/// so each can independently track whether its own constraint has been satisfied.</para>
/// </remarks>
internal sealed class CompositePressure<TRange, TData> : IEvictionPressure<TRange, TData>
    where TRange : IComparable<TRange>
{
    private readonly IEvictionPressure<TRange, TData>[] _pressures;

    /// <summary>
    /// Initializes a new <see cref="CompositePressure{TRange,TData}"/>.
    /// </summary>
    /// <param name="pressures">The child pressures to aggregate. Must not be empty.</param>
    internal CompositePressure(IEvictionPressure<TRange, TData>[] pressures)
    {
        _pressures = pressures;
    }

    /// <inheritdoc/>
    /// <remarks>Returns <c>true</c> when ANY child pressure is exceeded (OR semantics).</remarks>
    public bool IsExceeded
    {
        get
        {
            foreach (var pressure in _pressures)
            {
                if (pressure.IsExceeded)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <inheritdoc/>
    /// <remarks>Forwards the reduction to ALL child pressures.</remarks>
    public void Reduce(CachedSegment<TRange, TData> removedSegment)
    {
        foreach (var pressure in _pressures)
        {
            pressure.Reduce(removedSegment);
        }
    }
}
