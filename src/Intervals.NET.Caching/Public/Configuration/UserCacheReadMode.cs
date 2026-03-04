namespace Intervals.NET.Caching.Public.Configuration;

/// <summary>
/// Defines how materialized cache data is exposed to users.
/// </summary>
/// <remarks>
/// The read mode determines the trade-offs between read performance, allocation behavior,
/// rebalance cost, and memory pressure. This mode is configured once at cache creation time
/// and cannot be changed at runtime.
/// </remarks>
public enum UserCacheReadMode
{
    /// <summary>
    /// Stores data in a contiguous array internally.
    /// User reads return <see cref="ReadOnlyMemory{T}"/> pointing directly to the internal array.
    /// </summary>
    /// <remarks>
    /// <para><strong>Advantages:</strong></para>
    /// <list type="bullet">
    /// <item>Zero allocations on read operations</item>
    /// <item>Fastest read performance</item>
    /// <item>Ideal for read-heavy scenarios</item>
    /// </list>
    /// <para><strong>Disadvantages:</strong></para>
    /// <list type="bullet">
    /// <item>Rebalance always requires allocating a new array (even if size is unchanged)</item>
    /// <item>Large arrays may end up on the Large Object Heap (LOH) when size ? 85,000 bytes</item>
    /// <item>Higher memory pressure during rebalancing</item>
    /// </list>
    /// </remarks>
    Snapshot,

    /// <summary>
    /// Stores data in a growable structure (e.g., <see cref="List{T}"/>) internally.
    /// User reads allocate a new array for the requested range and return it as <see cref="ReadOnlyMemory{T}"/>.
    /// </summary>
    /// <remarks>
    /// <para><strong>Advantages:</strong></para>
    /// <list type="bullet">
    /// <item>Rebalance is cheaper and does not necessarily allocate large arrays</item>
    /// <item>Significantly less memory pressure during rebalancing</item>
    /// <item>Avoids LOH allocations in most cases</item>
    /// <item>Ideal for memory-sensitive scenarios</item>
    /// </list>
    /// <para><strong>Disadvantages:</strong></para>
    /// <list type="bullet">
    /// <item>Allocates a new array on every read operation</item>
    /// <item>Slower read performance due to allocation and copying</item>
    /// </list>
    /// </remarks>
    CopyOnRead
}
