using Intervals.NET;
using Intervals.NET.Domain.Abstractions;
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
    /// </remarks>
    public Range<TRange>? Plan(Range<TRange> cacheRange) => cacheRange.ExpandByRatio(
        domain: _domain,
        leftRatio: -(_options.LeftThreshold ?? 0), // Negate to shrink
        rightRatio: -(_options.RightThreshold ?? 0) // Negate to shrink
    );
}
