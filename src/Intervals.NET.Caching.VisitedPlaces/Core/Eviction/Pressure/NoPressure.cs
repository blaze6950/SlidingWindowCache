namespace Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Pressure;

/// <summary>
/// A singleton <see cref="IEvictionPressure{TRange,TData}"/> that represents no constraint violation.
/// Returned by policies when the constraint is not exceeded, avoiding allocation on the non-violation path.
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <remarks>
/// <para><strong>Invariants:</strong></para>
/// <list type="bullet">
/// <item><description><see cref="IsExceeded"/> is always <c>false</c></description></item>
/// <item><description><see cref="Reduce"/> is a no-op (no state to update)</description></item>
/// </list>
/// <para>
/// Similar to <see cref="Instrumentation.NoOpDiagnostics"/>, this avoids null checks throughout
/// the eviction pipeline.
/// </para>
/// </remarks>
public sealed class NoPressure<TRange, TData> : IEvictionPressure<TRange, TData>
    where TRange : IComparable<TRange>
{
    /// <summary>
    /// The shared singleton instance. Use this instead of creating new instances.
    /// </summary>
    public static readonly NoPressure<TRange, TData> Instance = new();

    private NoPressure() { }

    /// <inheritdoc/>
    /// <remarks>Always returns <c>false</c> — no constraint is violated.</remarks>
    public bool IsExceeded => false;

    /// <inheritdoc/>
    /// <remarks>No-op — there is no state to update.</remarks>
    public void Reduce(CachedSegment<TRange, TData> removedSegment) { }
}
