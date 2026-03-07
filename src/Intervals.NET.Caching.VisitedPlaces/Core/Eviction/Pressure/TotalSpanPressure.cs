using Intervals.NET.Caching.Extensions;
using Intervals.NET.Domain.Abstractions;

namespace Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Pressure;

/// <summary>
/// An <see cref="IEvictionPressure{TRange,TData}"/> that tracks whether the total span
/// (sum of all segment spans) exceeds a configured maximum. Each <see cref="Reduce"/> call
/// subtracts the removed segment's span from the tracked total.
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <typeparam name="TDomain">The range domain type used to compute segment spans.</typeparam>
/// <remarks>
/// <para><strong>Produced by:</strong> <see cref="Policies.MaxTotalSpanPolicy{TRange,TData,TDomain}"/></para>
/// <para><strong>Constraint:</strong> <c>currentTotalSpan &gt; maxTotalSpan</c></para>
/// <para><strong>Reduce behavior:</strong> Subtracts the removed segment's span from <c>currentTotalSpan</c>.
/// This is the key improvement over the old <c>MaxTotalSpanEvaluator</c> which had to estimate
/// removal counts using a greedy algorithm that could mismatch the actual executor order.</para>
/// <para><strong>TDomain capture:</strong> The <typeparamref name="TDomain"/> is captured internally
/// so that the <see cref="IEvictionPressure{TRange,TData}"/> interface stays generic only on
/// <c>&lt;TRange, TData&gt;</c>.</para>
/// </remarks>
internal sealed class TotalSpanPressure<TRange, TData, TDomain> : IEvictionPressure<TRange, TData>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private long _currentTotalSpan;
    private readonly int _maxTotalSpan;
    private readonly TDomain _domain;

    /// <summary>
    /// Initializes a new <see cref="TotalSpanPressure{TRange,TData,TDomain}"/>.
    /// </summary>
    /// <param name="currentTotalSpan">The current total span across all segments.</param>
    /// <param name="maxTotalSpan">The maximum allowed total span.</param>
    /// <param name="domain">The range domain used to compute individual segment spans during <see cref="Reduce"/>.</param>
    internal TotalSpanPressure(long currentTotalSpan, int maxTotalSpan, TDomain domain)
    {
        _currentTotalSpan = currentTotalSpan;
        _maxTotalSpan = maxTotalSpan;
        _domain = domain;
    }

    /// <inheritdoc/>
    public bool IsExceeded => _currentTotalSpan > _maxTotalSpan;

    /// <inheritdoc/>
    /// <remarks>Subtracts the removed segment's span from the tracked total.</remarks>
    public void Reduce(CachedSegment<TRange, TData> removedSegment)
    {
        _currentTotalSpan -= removedSegment.Range.Span(_domain).Value;
    }
}
