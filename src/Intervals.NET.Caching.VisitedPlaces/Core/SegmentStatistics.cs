namespace Intervals.NET.Caching.VisitedPlaces.Core;

/// <summary>
/// Per-segment statistics maintained by the background event processor and used by eviction
/// selectors to determine candidate ordering.
/// </summary>
/// <remarks>
/// <para><strong>Invariant VPC.E.4:</strong> The Background Event Processor owns this schema.</para>
/// <para><strong>Invariant VPC.E.4a:</strong>
/// Initialized at storage: <c>CreatedAt = now</c>, <c>LastAccessedAt = now</c>, <c>HitCount = 0</c>.</para>
/// <para><strong>Invariant VPC.E.4b:</strong>
/// Updated on use: <c>HitCount</c> incremented, <c>LastAccessedAt = now</c>.</para>
/// </remarks>
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
