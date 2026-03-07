namespace Intervals.NET.Caching.VisitedPlaces.Public.Configuration;

/// <summary>
/// Specifies the internal storage strategy used by <see cref="Cache.VisitedPlacesCache{TRange,TData,TDomain}"/>
/// for maintaining the collection of non-contiguous cached segments.
/// </summary>
/// <remarks>
/// <para><strong>Selection Guidance:</strong></para>
/// <list type="bullet">
/// <item><description><see cref="SnapshotAppendBuffer"/> — default; optimal for smaller caches (&lt; ~85 KB total data, &lt; ~50 segments).</description></item>
/// <item><description><see cref="LinkedListStrideIndex"/> — optimal for larger caches (&gt; ~85 KB or &gt; ~50–100 segments) where Large Object Heap pressure is a concern.</description></item>
/// </list>
/// <para>
/// The selected strategy cannot be changed after construction. Both strategies expose the same
/// external behaviour and uphold all VPC invariants. The choice is purely a performance trade-off.
/// See <c>docs/visited-places/storage-strategies.md</c> for a detailed comparison.
/// </para>
/// </remarks>
public enum StorageStrategy
{
    /// <summary>
    /// Sorted snapshot array with a fixed-size append buffer (default strategy).
    /// Optimised for small caches with a high read-to-write ratio.
    /// Reads: O(log n + k + m) with zero allocation via <c>ReadOnlyMemory&lt;T&gt;</c> slice.
    /// Normalization rebuilds the array when the append buffer fills.
    /// </summary>
    SnapshotAppendBuffer = 0,

    /// <summary>
    /// Doubly-linked list with a stride index and stride append buffer.
    /// Optimised for larger caches where allocating a single sorted array would pressure the Large Object Heap.
    /// Reads: O(log(n/N) + k + N + m) where N is the stride.
    /// </summary>
    LinkedListStrideIndex = 1,
}
