namespace Intervals.NET.Caching.VisitedPlaces.Core;

/// <summary>
/// Represents a single contiguous cached segment: a range, its data, and per-segment statistics.
/// </summary>
/// <typeparam name="TRange">The range boundary type. Must implement <see cref="IComparable{T}"/>.</typeparam>
/// <typeparam name="TData">The type of cached data.</typeparam>
/// <remarks>
/// <para><strong>Invariant VPC.C.3:</strong> Overlapping segments are not permitted.
/// Each point in the domain is cached in at most one segment.</para>
/// <para><strong>Invariant VPC.C.2:</strong> Segments are never merged, even if adjacent.</para>
/// </remarks>
public sealed class CachedSegment<TRange, TData>
    where TRange : IComparable<TRange>
{
    /// <summary>The range covered by this segment.</summary>
    public Range<TRange> Range { get; }

    /// <summary>The data stored for this segment.</summary>
    public ReadOnlyMemory<TData> Data { get; }

    /// <summary>
    /// The per-segment statistics owned and maintained by the <see cref="Eviction.IEvictionExecutor{TRange,TData}"/>.
    /// </summary>
    public SegmentStatistics Statistics { get; internal set; }

    /// <summary>
    /// Initializes a new <see cref="CachedSegment{TRange,TData}"/>.
    /// </summary>
    /// <param name="range">The range this segment covers.</param>
    /// <param name="data">The cached data for this range.</param>
    /// <param name="statistics">Initial statistics for this segment.</param>
    internal CachedSegment(Range<TRange> range, ReadOnlyMemory<TData> data, SegmentStatistics statistics)
    {
        Range = range;
        Data = data;
        Statistics = statistics;
    }
}
