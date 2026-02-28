using Intervals.NET;
using Intervals.NET.Domain.Abstractions;
using SlidingWindowCache.Core.Rebalance.Decision;
using SlidingWindowCache.Infrastructure.Extensions;
using SlidingWindowCache.Public.Configuration;

namespace SlidingWindowCache.Core.Planning;

/// <summary>
/// Plans the no-rebalance range by shrinking the cache range using threshold ratios.
/// This defines the stability zone within which user requests do not trigger rebalancing.
/// </summary>
/// <typeparam name="TRange">The type representing the range boundaries.</typeparam>
/// <typeparam name="TDomain">The type representing the domain of the ranges.</typeparam>
/// <remarks>
/// <para><strong>Role:</strong> Cache Geometry Planning - Threshold Zone Computation</para>
/// <para><strong>Characteristics:</strong> Pure function, stateless, configuration-driven</para>
/// <para>
/// Works in tandem with <see cref="ProportionalRangePlanner{TRange,TDomain}"/> to define
/// complete cache geometry: desired cache range (expansion) and no-rebalance zone (shrinkage).
/// Invalid threshold configurations (sum exceeding 1.0) are prevented at <see cref="WindowCacheOptions"/> construction time.
/// </para>
/// <para><strong>Execution Context:</strong> Background thread (intent processing loop)</para>
/// <para>
/// Invoked by <see cref="RebalanceDecisionEngine{TRange,TDomain}"/> during Stage 3 of the decision pipeline,
/// which executes in the background intent processing loop (see <c>IntentController.ProcessIntentsAsync</c>).
/// </para>
/// </remarks>
internal readonly struct NoRebalanceRangePlanner<TRange, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private readonly WindowCacheOptions _options;
    private readonly TDomain _domain;

    public NoRebalanceRangePlanner(WindowCacheOptions options, TDomain domain)
    {
        _options = options;
        _domain = domain;
    }

    /// <summary>
    /// Computes the no-rebalance range by shrinking the cache range using threshold ratios.
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
    /// Note: WindowCacheOptions constructor ensures leftThreshold + rightThreshold does not exceed 1.0.
    /// </remarks>
    public Range<TRange>? Plan(Range<TRange> cacheRange)
    {
        var leftThreshold = _options.LeftThreshold ?? 0;
        var rightThreshold = _options.RightThreshold ?? 0;
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
