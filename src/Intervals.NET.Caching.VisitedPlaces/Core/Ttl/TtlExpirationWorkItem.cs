using Intervals.NET.Caching.Infrastructure.Scheduling;

namespace Intervals.NET.Caching.VisitedPlaces.Core.Ttl;

/// <summary>
/// A work item carrying the information needed for a single TTL expiration event:
/// a reference to the segment to remove and the absolute time at which it expires.
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <remarks>
/// <para><strong>Lifecycle:</strong></para>
/// <para>
/// One <see cref="TtlExpirationWorkItem{TRange,TData}"/> is created per stored segment when
/// TTL is enabled. It is published to <c>TtlExpirationExecutor</c>'s scheduler immediately
/// after the segment is stored in the Background Storage Loop (Step 2 of
/// <c>CacheNormalizationExecutor</c>).
/// </para>
/// <para><strong>Ownership of <see cref="ExpiresAt"/>:</strong></para>
/// <para>
/// <see cref="ExpiresAt"/> is computed at creation time as
/// <c>DateTimeOffset.UtcNow + SegmentTtl</c>. The executor delays until this absolute
/// timestamp to account for any scheduling latency between creation and execution.
/// </para>
/// <para><strong>Cancellation:</strong></para>
/// <para>
/// The <see cref="CancellationToken"/> is a shared disposal token passed in at construction
/// time — owned by <c>VisitedPlacesCache</c> and cancelled during <c>DisposeAsync</c>.
/// All in-flight TTL work items share the same token, so a single cancellation signal
/// simultaneously aborts every pending <c>Task.Delay</c> across the entire cache instance,
/// with zero per-item allocation overhead.
/// </para>
/// <para>
/// <see cref="Cancel"/> and <see cref="Dispose"/> are intentional no-ops: the token is
/// owned and cancelled by the cache, not by any individual work item or the scheduler's
/// last-item cancellation mechanism.
/// </para>
/// <para>Alignment: Invariant VPC.T.1 (TTL expirations are idempotent), VPC.T.3 (delays cancelled on disposal).</para>
/// </remarks>
internal sealed class TtlExpirationWorkItem<TRange, TData> : ISchedulableWorkItem
    where TRange : IComparable<TRange>
{
    /// <summary>
    /// Initializes a new <see cref="TtlExpirationWorkItem{TRange,TData}"/>.
    /// </summary>
    /// <param name="segment">The segment to expire.</param>
    /// <param name="expiresAt">The absolute UTC time at which the segment expires.</param>
    /// <param name="cancellationToken">
    /// Shared disposal cancellation token owned by <c>VisitedPlacesCache</c>.
    /// Cancelled during <c>DisposeAsync</c> to abort all pending TTL delays simultaneously.
    /// </param>
    public TtlExpirationWorkItem(
        CachedSegment<TRange, TData> segment,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        Segment = segment;
        ExpiresAt = expiresAt;
        CancellationToken = cancellationToken;
    }

    /// <summary>The segment that will be removed when this work item is executed.</summary>
    public CachedSegment<TRange, TData> Segment { get; }

    /// <summary>The absolute UTC time at which this segment's TTL expires.</summary>
    public DateTimeOffset ExpiresAt { get; }

    /// <inheritdoc/>
    public CancellationToken CancellationToken { get; }

    /// <inheritdoc/>
    /// <remarks>
    /// No-op: cancellation is controlled by the shared disposal token owned by
    /// <c>VisitedPlacesCache</c>, not by per-item cancellation.
    /// </remarks>
    public void Cancel() { }

    /// <inheritdoc/>
    /// <remarks>
    /// No-op: no per-item resources to release. The shared cancellation token is
    /// owned and disposed by <c>VisitedPlacesCache</c>.
    /// </remarks>
    public void Dispose() { }
}
