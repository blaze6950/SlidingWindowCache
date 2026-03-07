namespace Intervals.NET.Caching.VisitedPlaces.Core;

/// <summary>
/// Per-segment statistics owned and maintained by the
/// <see cref="Eviction.IEvictionExecutor{TRange,TData}"/>.
/// </summary>
/// <remarks>
/// <para><strong>Invariant VPC.E.4:</strong> The Eviction Executor owns this schema.</para>
/// <para><strong>Invariant VPC.E.4a:</strong>
/// Initialized at storage: <c>CreatedAt = now</c>, <c>LastAccessedAt = now</c>, <c>HitCount = 0</c>.</para>
/// <para><strong>Invariant VPC.E.4b:</strong>
/// Updated on use: <c>HitCount</c> incremented, <c>LastAccessedAt = now</c>.</para>
/// </remarks>
/// TODO: right now this DTO contains all the possible properties needed by all eviction executor strategies, but at a time we can utilize only one eviction executor strategy, means that only a subset of these properties is relevant for the current strategy.
/// TODO: I would like to make the specific eviction executor strategy to set what exactly segment statistics should look like, without defining of not used peoperties.
public sealed class SegmentStatistics
{
    /// <summary>When the segment was first stored in the cache.</summary>
    public DateTime CreatedAt { get; }

    /// <summary>When the segment was last used to serve a user request.</summary>
    public DateTime LastAccessedAt { get; internal set; }

    /// <summary>Number of times this segment contributed to serving a user request.</summary>
    public int HitCount { get; internal set; }

    /// <summary>
    /// Initializes statistics for a newly stored segment.
    /// </summary>
    /// <param name="now">The timestamp to use for both <see cref="CreatedAt"/> and <see cref="LastAccessedAt"/>.</param>
    internal SegmentStatistics(DateTime now)
    {
        CreatedAt = now;
        LastAccessedAt = now;
        HitCount = 0;
    }
}
