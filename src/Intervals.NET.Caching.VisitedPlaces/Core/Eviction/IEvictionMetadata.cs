namespace Intervals.NET.Caching.VisitedPlaces.Core.Eviction;

/// <summary>
/// Marker interface for selector-owned per-segment eviction metadata.
/// </summary>
/// <remarks>
/// <para>
/// Each <see cref="IEvictionSelector{TRange,TData}"/> implementation is responsible for
/// defining, creating, updating, and interpreting its own metadata type that implements
/// this interface. The metadata is stored directly on <see cref="CachedSegment{TRange,TData}"/>
/// via the <c>EvictionMetadata</c> property.
/// </para>
/// <para>
/// <strong>Design contract:</strong>
/// </para>
/// <list type="bullet">
/// <item><description>Selectors own their metadata type (typically as a nested <c>internal sealed class</c>)</description></item>
/// <item><description>Selectors initialize metadata via <c>InitializeMetadata</c> when a segment is stored</description></item>
/// <item><description>Selectors update metadata via <c>UpdateMetadata</c> when segments are used</description></item>
/// <item><description>Selectors read metadata in <c>OrderCandidates</c> using a lazy-initialize pattern:
/// if the segment carries metadata from a different selector, replace it with the current selector's own type</description></item>
/// <item><description>Selectors that need no metadata (e.g., <c>SmallestFirstEvictionSelector</c>) leave the field null</description></item>
/// </list>
/// <para><strong>Thread safety:</strong> Only mutated by the Background Path (single writer). No concurrent access.</para>
/// </remarks>
public interface IEvictionMetadata
{
}
