using Intervals.NET;
using Intervals.NET.Domain.Abstractions;
using Intervals.NET.Extensions;
using SlidingWindowCache.Infrastructure.Extensions;
using SlidingWindowCache.Public.Configuration;

namespace SlidingWindowCache.Core.Rebalance.Intent;

internal readonly struct ThresholdRebalancePolicy<TRange, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private readonly WindowCacheOptions _options;
    private readonly TDomain _domain;

    public ThresholdRebalancePolicy(WindowCacheOptions options, TDomain domain)
    {
        _options = options;
        _domain = domain;
    }

    public bool ShouldRebalance(Range<TRange> noRebalanceRange, Range<TRange> requested) =>
        !noRebalanceRange.Contains(requested);

    public Range<TRange>? GetNoRebalanceRange(Range<TRange> cacheRange) => cacheRange.ExpandByRatio(
        domain: _domain,
        leftRatio: -(_options.LeftThreshold ?? 0), // Negate to shrink
        rightRatio: -(_options.RightThreshold ?? 0) // Negate to shrink
    );
}