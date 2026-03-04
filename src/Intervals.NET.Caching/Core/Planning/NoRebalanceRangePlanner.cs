using Intervals.NET;
using Intervals.NET.Domain.Abstractions;
using Intervals.NET.Caching.Core.Rebalance.Decision;
using Intervals.NET.Caching.Core.State;
using Intervals.NET.Caching.Infrastructure.Extensions;

namespace Intervals.NET.Caching.Core.Planning;

/// <summary>
/// Plans the no-rebalance range by shrinking the cache range using threshold ratios.
/// This defines the stability zone within which user requests do not trigger rebalancing.
/// </summary>
/// <typeparam name="TRange">The type representing the range boundaries.</typeparam>
/// <typeparam name="TDomain">The type representing the domain of the ranges.</typeparam>
/// <remarks>
/// <para><strong>Role:</strong> Cache Geometry Planning - Threshold Zone Computation</para>
/// <para><strong>Characteristics:</strong> Pure function at the call site, configuration-driven</para>
/// <para>
/// Works in tandem with <see cref="ProportionalRangePlanner{TRange,TDomain}"/> to define
/// complete cache geometry: desired cache range (expansion) and no-rebalance zone (shrinkage).
/// Invalid threshold configurations (sum exceeding 1.0) are prevented at construction time
/// of <see cref="RuntimeCacheOptions"/> / <see cref="Public.Configuration.WindowCacheOptions"/>.
/// </para>
/// <para><strong>Runtime-Updatable Configuration:</strong></para>
/// <para>
///   The planner holds a reference to a shared <see cref="RuntimeCacheOptionsHolder"/> rather than a frozen
///   copy of options. This allows <c>LeftThreshold</c> and <c>RightThreshold</c> to be updated at runtime via
///   <c>IWindowCache.UpdateRuntimeOptions</c> without reconstructing the planner. Changes take effect on the
///   next rebalance decision cycle ("next cycle" semantics).
/// </para>
/// <para><strong>Execution Context:</strong> Background thread (intent processing loop)</para>
/// <para>
/// Invoked by <see cref="RebalanceDecisionEngine{TRange,TDomain}"/> during Stage 3 of the decision pipeline,
/// which executes in the background intent processing loop (see <c>IntentController.ProcessIntentsAsync</c>).
/// </para>
/// </remarks>
internal sealed class NoRebalanceRangePlanner<TRange, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private readonly RuntimeCacheOptionsHolder _optionsHolder;
    private readonly TDomain _domain;

    /// <summary>
    /// Initializes a new instance of <see cref="NoRebalanceRangePlanner{TRange, TDomain}"/> with the specified options holder and domain.
    /// </summary>
    /// <param name="optionsHolder">
    ///     Shared holder for the current runtime options snapshot. The planner reads
    ///     <see cref="RuntimeCacheOptionsHolder.Current"/> once per <see cref="Plan"/> invocation so that
    ///     changes published via <c>IWindowCache.UpdateRuntimeOptions</c> take effect on the next cycle.
    /// </param>
    /// <param name="domain">Domain implementation used for range arithmetic and span calculations.</param>
    public NoRebalanceRangePlanner(RuntimeCacheOptionsHolder optionsHolder, TDomain domain)
    {
        _optionsHolder = optionsHolder;
        _domain = domain;
    }

    /// <summary>
    /// Computes the no-rebalance range by shrinking the cache range using the current threshold ratios.
    /// </summary>
    /// <param name="cacheRange">The current cache range to compute thresholds from.</param>
    /// <returns>
    /// The no-rebalance range, or null if thresholds would result in an invalid range.
    /// </returns>
    /// <remarks>
    /// The no-rebalance range is computed by contracting the cache range:
    /// - Left threshold shrinks from the left boundary inward
    /// - Right threshold shrinks from the right boundary inward
    /// This creates a "stability zone" where requests don't trigger rebalancing.
    /// Returns null when the sum of left and right thresholds is >= 1.0, which would completely eliminate the no-rebalance range.
    /// Note: <see cref="RuntimeCacheOptions"/> constructor ensures leftThreshold + rightThreshold does not exceed 1.0.
    /// Snapshots <see cref="RuntimeCacheOptionsHolder.Current"/> once at entry for consistency within the invocation.
    /// </remarks>
    public Range<TRange>? Plan(Range<TRange> cacheRange)
    {
        // Snapshot current options once for consistency within this invocation
        var options = _optionsHolder.Current;

        var leftThreshold = options.LeftThreshold ?? 0;
        var rightThreshold = options.RightThreshold ?? 0;
        var sum = leftThreshold + rightThreshold;

        if (sum >= 1)
        {
            // Means that there is no NoRebalanceRange, the shrinkage shrink the whole cache range
            return null;
        }

        return cacheRange.ExpandByRatio(
            domain: _domain,
            leftRatio: -leftThreshold, // Negate to shrink
            rightRatio: -rightThreshold // Negate to shrink
        );
    }
}
