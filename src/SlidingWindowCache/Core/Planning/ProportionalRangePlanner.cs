using Intervals.NET;
using Intervals.NET.Domain.Abstractions;
using SlidingWindowCache.Core.Rebalance.Decision;
using SlidingWindowCache.Core.Rebalance.Intent;
using SlidingWindowCache.Infrastructure.Extensions;
using SlidingWindowCache.Public.Configuration;

namespace SlidingWindowCache.Core.Planning;

/// <summary>
///     Computes the canonical <c>DesiredCacheRange</c> for a given user <c>RequestedRange</c> and cache geometry configuration.
/// </summary>
/// <remarks>
/// <para><strong>Architectural Context:</strong></para>
/// <para>
///   <list type="bullet">
///     <item><description>Invoked synchronously by <c>RebalanceDecisionEngine</c> within the background intent processing loop (<see cref="IntentController{TRange,TData,TDomain}.ProcessIntentsAsync"/>)</description></item>
///     <item><description>Defines the shape of the sliding window cache by expanding the requested range according to configuration</description></item>
///     <item><description><b>Pure function:</b> Stateless, value type, no side effects, deterministic: outcome depends only on configuration and request</description></item>
///     <item><description>Does <b>not</b> read or mutate cache state; independent of current cache contents</description></item>
///     <item><description>Used only as analytical input (never executes I/O or mutates shared state)</description></item>
///   </list>
/// </para>
/// <para><strong>Responsibilities:</strong></para>
/// <para>
///   <list type="bullet">
///     <item><description>Computes <c>DesiredCacheRange</c> for any <c>RequestedRange</c> + config (see <see cref="WindowCacheOptions"/>)</description></item>
///     <item><description>Defines canonical geometry for rebalance, ensuring predictability and stability</description></item>
///     <item><description>Answers: <b>"What shape to target?"</b> in the rebalance decision pipeline</description></item>
///   </list>
/// </para>
/// <para><strong>Non-Responsibilities:</strong></para>
/// <para>
///   <list type="bullet">
///     <item><description>Does <b>not</b> decide <b>whether</b> to rebalance; invoked only during necessity evaluation</description></item>
///     <item><description>Does <b>not</b> mutate cache or any shared state; no write access</description></item>
///   </list>
/// </para>
/// <para><strong>Invariant References:</strong></para>
/// <list type="bullet">
///   <item><description>E.30: DesiredCacheRange is computed solely from RequestedRange + config</description></item>
///   <item><description>E.31: DesiredCacheRange is independent of current cache contents</description></item>
///   <item><description>E.32: DesiredCacheRange defines canonical state for convergence semantics</description></item>
///   <item><description>E.33: Sliding window geometry is determined solely by configuration</description></item>
///   <item><description>D.25, D.26: Analytical/pure (CPU-only), never mutates cache state</description></item>
/// </list>
/// <para><strong>Related:</strong> <see cref="NoRebalanceSatisfactionPolicy{TRange}"/> (threshold calculation, <b>when</b> to rebalance logic)</para>
/// <para>See: <see href="../docs/component-map.md#proportionalrangeplanner" /> for architectural overview.</para>
/// </remarks>
/// <typeparam name="TRange">Type representing the boundaries of a window/range; must be comparable (see <see cref="IComparable{TRange}"/>) so intervals can be ordered and spanned.</typeparam>
/// <typeparam name="TDomain">Provides domain-specific logic to compute spans, boundaries, and interval arithmetic for <c>TRange</c>.</typeparam>
internal readonly struct ProportionalRangePlanner<TRange, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private readonly WindowCacheOptions _options;
    private readonly TDomain _domain;

    /// <summary>
    ///     Initializes a new instance of <see cref="ProportionalRangePlanner{TRange, TDomain}"/> with the specified cache configuration and domain definition.
    /// </summary>
    /// <param name="options">Immutable cache geometry configuration (see <see cref="WindowCacheOptions"/>); provides proportional left/right sizing policies.</param>
    /// <param name="domain">Domain implementation used for range arithmetic and span calculations.</param>
    /// <remarks>
    /// <para>
    ///   This constructor wires the planner to a specific cache configuration and domain only; it does not perform any computation or validation. The planner is invoked by <c>RebalanceDecisionEngine</c> during Stage 3 (Desired Range Computation) of the decision evaluation pipeline, which executes in the background intent processing loop.
    /// </para>
    /// <para>
    ///   <b>References:</b> Invariants E.30-E.33, D.25-D.26 (see <c>docs/invariants.md</c>).
    /// </para>
    /// </remarks>
    public ProportionalRangePlanner(WindowCacheOptions options, TDomain domain)
    {
        _options = options;
        _domain = domain;
    }

    /// <summary>
    ///     Computes the canonical <c>DesiredCacheRange</c> to target for a given <paramref name="requested"/> window, expanding left/right according to the cache configuration.
    /// </summary>
    /// <param name="requested">User-requested range for which cache expansion should be planned.</param>
    /// <returns>
    ///     The canonical <c>DesiredCacheRange</c> — representing the window the cache should hold to optimally satisfy the request with proportional left/right extension.
    /// </returns>
    /// <remarks>
    /// <para>This method:
    ///   <list type="bullet">
    ///     <item><description>Defines the <b>shape</b> of the sliding window, not the contents</description></item>
    ///     <item><description>Is pure/side-effect free: No cache state or I/O interaction</description></item>
    ///     <item><description>Applies only configuration and domain arithmetic (see <see cref="WindowCacheOptions.LeftCacheSize"/>, <see cref="WindowCacheOptions.RightCacheSize"/>)</description></item>
    ///     <item><description>Does <b>not</b> trigger or decide rebalance — strictly analytical</description></item>
    ///     <item><description>Enforces Invariants: E.30 (function of <c>RequestedRange + config</c>), E.31 (independent of cache state), E.32 (defines canonical convergent target), D.25-D.26 (analytical/CPU-only)</description></item>
    ///   </list>
    /// </para>
    /// <para>
    ///   Typical usage: Invoked during Stage 3 of the rebalance decision pipeline by <c>RebalanceDecisionEngine.Evaluate()</c>, which runs in the background intent processing loop (<c>IntentController.ProcessIntentsAsync</c>). Executes after stability checks (Stages 1-2) and before equality validation (Stage 4).
    /// </para>
    /// <para>See also:
    ///   <see cref="NoRebalanceSatisfactionPolicy{TRange}"/>
    ///   <see href="../docs/component-map.md#proportionalrangeplanner" />
    /// </para>
    /// </remarks>
    public Range<TRange> Plan(Range<TRange> requested)
    {
        var size = requested.Span(_domain);

        var left = size.Value * _options.LeftCacheSize;
        var right = size.Value * _options.RightCacheSize;

        return requested.Expand(
            domain: _domain,
            left: (long)left,
            right: (long)right
        );
    }
}