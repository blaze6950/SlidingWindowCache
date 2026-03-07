using Intervals.NET.Caching.VisitedPlaces.Core.Eviction;

namespace Intervals.NET.Caching.VisitedPlaces.Core;

/// <summary>
/// Represents a single contiguous cached segment: a range, its data, and optional selector-owned eviction metadata.
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
    /// Optional selector-owned eviction metadata. Set and interpreted exclusively by the
    /// configured <see cref="IEvictionSelector{TRange,TData}"/>. <see langword="null"/> when
    /// the selector requires no metadata (e.g., <c>SmallestFirstEvictionSelector</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The selector initializes this field via <c>InitializeMetadata</c> when the segment
    /// is stored and updates it via <c>UpdateMetadata</c> when the segment is used.
    /// If a selector encounters a metadata object from a different selector type, it replaces
    /// it with its own (lazy initialization pattern).
    /// </para>
    /// <para><strong>Thread safety:</strong> Only mutated by the Background Path (single writer).</para>
    /// </remarks>
    public IEvictionMetadata? EvictionMetadata { get; internal set; }

    /// <summary>
    /// Initializes a new <see cref="CachedSegment{TRange,TData}"/>.
    /// </summary>
    /// <param name="range">The range this segment covers.</param>
    /// <param name="data">The cached data for this range.</param>
    internal CachedSegment(Range<TRange> range, ReadOnlyMemory<TData> data)
    {
        Range = range;
        Data = data;
    }
}
