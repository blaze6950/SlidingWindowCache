using Intervals.NET.Caching.Extensions;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Pressure;
using Intervals.NET.Domain.Abstractions;

namespace Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Policies;

/// <summary>
/// An <see cref="IEvictionPolicy{TRange,TData}"/> that fires when the sum of all cached
/// segment spans (total domain coverage) exceeds a configured maximum.
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <typeparam name="TDomain">The range domain type used to compute spans.</typeparam>
/// <remarks>
/// <para><strong>Firing Condition:</strong>
/// <c>_totalSpan &gt; MaxTotalSpan</c></para>
/// <para><strong>Pressure Produced:</strong> <see cref="TotalSpanPressure"/>
/// with the current running total span, the configured maximum, and the domain for per-segment
/// span computation during <see cref="IEvictionPressure{TRange,TData}.Reduce"/>.</para>
/// <para>
/// This policy limits the total cached domain coverage regardless of how many segments it is
/// split into. More meaningful than segment count when segments vary significantly in span.
/// </para>
/// <para><strong>O(1) Evaluate via incremental state:</strong></para>
/// <para>
/// Rather than recomputing the total span from scratch on every
/// <see cref="Evaluate"/> call (O(N) iteration), this policy maintains a running
/// <c>_totalSpan</c> counter that is updated incrementally:
/// </para>
/// <list type="bullet">
/// <item><description>
///   <see cref="OnSegmentAdded"/> adds the segment's span to <c>_totalSpan</c>.
/// </description></item>
/// <item><description>
///   <see cref="OnSegmentRemoved"/> subtracts the segment's span from <c>_totalSpan</c>.
/// </description></item>
/// </list>
/// <para>
/// Both lifecycle hooks are called by <see cref="EvictionPolicyEvaluator{TRange,TData}"/>
/// and may also be called by the TTL actor concurrently. <c>_totalSpan</c> is updated via
/// <see cref="Interlocked.Add(ref long, long)"/> so it is always thread-safe.
/// <see cref="Evaluate"/> reads it via <see cref="Volatile.Read"/> for an acquire fence.
/// </para>
/// <para><strong>Key improvement over the old stateless design:</strong></para>
/// <para>
/// The old implementation iterated <c>allSegments</c> in every <c>Evaluate</c> call and called
/// <c>Span(domain)</c> for each segment (O(N)). With incremental state this is reduced to O(1),
/// matching the complexity of <see cref="MaxSegmentCountPolicy{TRange,TData}"/>.
/// </para>
/// <para><strong>Span Computation:</strong> Uses <typeparamref name="TDomain"/> to compute each
/// segment's span in the lifecycle hooks. The domain is captured at construction and also passed
/// to the pressure object for use during <see cref="IEvictionPressure{TRange,TData}.Reduce"/>.</para>
/// </remarks>
public sealed class MaxTotalSpanPolicy<TRange, TData, TDomain> : IEvictionPolicy<TRange, TData>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private readonly TDomain _domain;
    private long _totalSpan;

    /// <summary>
    /// The maximum total span allowed across all cached segments before eviction is triggered.
    /// </summary>
    public int MaxTotalSpan { get; }

    /// <summary>
    /// Initializes a new <see cref="MaxTotalSpanPolicy{TRange,TData,TDomain}"/> with the
    /// specified maximum total span and domain.
    /// </summary>
    /// <param name="maxTotalSpan">
    /// The maximum total span (in domain units). Must be &gt;= 1.
    /// </param>
    /// <param name="domain">The range domain used to compute segment spans.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="maxTotalSpan"/> is less than 1.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="domain"/> is <see langword="null"/>.
    /// </exception>
    public MaxTotalSpanPolicy(int maxTotalSpan, TDomain domain)
    {
        if (maxTotalSpan < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxTotalSpan),
                "MaxTotalSpan must be greater than or equal to 1.");
        }

        if (domain is null)
        {
            throw new ArgumentNullException(nameof(domain));
        }

        MaxTotalSpan = maxTotalSpan;
        _domain = domain;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Adds <c>segment.Range.Span(domain).Value</c> to the running total atomically via
    /// <see cref="Interlocked.Add(ref long, long)"/>. Safe to call concurrently from the
    /// Background Storage Loop and the TTL actor.
    /// </remarks>
    public void OnSegmentAdded(CachedSegment<TRange, TData> segment)
    {
        Interlocked.Add(ref _totalSpan, segment.Range.Span(_domain).Value);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Subtracts <c>segment.Range.Span(domain).Value</c> from the running total atomically via
    /// <see cref="Interlocked.Add(ref long, long)"/> with a negated value. Safe to call
    /// concurrently from the Background Storage Loop and the TTL actor.
    /// </remarks>
    public void OnSegmentRemoved(CachedSegment<TRange, TData> segment)
    {
        Interlocked.Add(ref _totalSpan, -segment.Range.Span(_domain).Value);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// O(1): reads the cached <c>_totalSpan</c> via <see cref="Volatile.Read"/> and compares
    /// it against <c>MaxTotalSpan</c>.
    /// The running total maintained via <see cref="OnSegmentAdded"/> and
    /// <see cref="OnSegmentRemoved"/> is always current.
    /// </remarks>
    public IEvictionPressure<TRange, TData> Evaluate()
    {
        var currentSpan = Volatile.Read(ref _totalSpan);

        if (currentSpan <= MaxTotalSpan)
        {
            return NoPressure<TRange, TData>.Instance;
        }

        return new TotalSpanPressure(currentSpan, MaxTotalSpan, _domain);
    }

    /// <summary>
    /// An <see cref="IEvictionPressure{TRange,TData}"/> that tracks whether the total span
    /// (sum of all segment spans) exceeds a configured maximum. Each <see cref="Reduce"/> call
    /// subtracts the removed segment's span from the tracked total.
    /// </summary>
    /// <remarks>
    /// <para><strong>Constraint:</strong> <c>currentTotalSpan &gt; maxTotalSpan</c></para>
    /// <para><strong>Reduce behavior:</strong> Subtracts the removed segment's span from <c>currentTotalSpan</c>.
    /// This is order-independent: any segment removal correctly reduces the tracked total regardless
    /// of which selector strategy is used.</para>
    /// <para><strong>TDomain capture:</strong> The <typeparamref name="TDomain"/> is captured internally
    /// so that the <see cref="IEvictionPressure{TRange,TData}"/> interface stays generic only on
    /// <c>&lt;TRange, TData&gt;</c>.</para>
    /// <para><strong>Snapshot semantics:</strong> The <c>currentTotalSpan</c> passed to the constructor
    /// is a snapshot of the policy's running total at the moment <see cref="Evaluate"/> was called.
    /// Subsequent <see cref="OnSegmentAdded"/>/<see cref="OnSegmentRemoved"/> calls on the policy
    /// do not affect an already-created pressure object.</para>
    /// </remarks>
    internal sealed class TotalSpanPressure : IEvictionPressure<TRange, TData>
    {
        private long _currentTotalSpan;
        private readonly int _maxTotalSpan;
        private readonly TDomain _domain;

        /// <summary>
        /// Initializes a new <see cref="TotalSpanPressure"/>.
        /// </summary>
        /// <param name="currentTotalSpan">The current total span across all segments (snapshot).</param>
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
}
