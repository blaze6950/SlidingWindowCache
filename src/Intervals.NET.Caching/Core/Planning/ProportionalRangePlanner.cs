using Intervals.NET;
using Intervals.NET.Domain.Abstractions;
using Intervals.NET.Caching.Core.Rebalance.Decision;
using Intervals.NET.Caching.Core.Rebalance.Intent;
using Intervals.NET.Caching.Core.State;
using Intervals.NET.Caching.Infrastructure.Extensions;

namespace Intervals.NET.Caching.Core.Planning;

/// <summary>
///     Computes the canonical <c>DesiredCacheRange</c> for a given user <c>RequestedRange</c> and cache geometry configuration.
/// </summary>
/// <remarks>
/// <para><strong>Architectural Context:</strong></para>
/// <para>
///   <list type="bullet">
///     <item><description>Invoked synchronously by <c>RebalanceDecisionEngine</c> within the background intent processing loop (<see cref="IntentController{TRange,TData,TDomain}.ProcessIntentsAsync"/>)</description></item>
///     <item><description>Defines the shape of the sliding window cache by expanding the requested range according to configuration</description></item>
///     <item><description><b>Pure function at the call site:</b> Reads a consistent snapshot of <see cref="RuntimeCacheOptionsHolder.Current"/> once at the start of <see cref="Plan"/> and uses it throughout — no side effects, deterministic within a single invocation</description></item>
///     <item><description>Does <b>not</b> read or mutate cache state; independent of current cache contents</description></item>
///     <item><description>Used only as analytical input (never executes I/O or mutates shared state)</description></item>
///   </list>
/// </para>
/// <para><strong>Runtime-Updatable Configuration:</strong></para>
/// <para>
///   The planner holds a reference to a shared <see cref="RuntimeCacheOptionsHolder"/> rather than a frozen
///   copy of options. This allows <c>LeftCacheSize</c> and <c>RightCacheSize</c> to be updated at runtime via
///   <c>IWindowCache.UpdateRuntimeOptions</c> without reconstructing the planner. Changes take effect on the
///   next rebalance decision cycle ("next cycle" semantics).
/// </para>
/// <para><strong>Responsibilities:</strong></para>
/// <para>
///   <list type="bullet">
///     <item><description>Computes <c>DesiredCacheRange</c> for any <c>RequestedRange</c> + current config snapshot</description></item>
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
///   <item><description>E.1: DesiredCacheRange is computed solely from RequestedRange + config</description></item>
///   <item><description>E.2: DesiredCacheRange is independent of current cache contents</description></item>
///   <item><description>E.3: DesiredCacheRange defines canonical state for convergence semantics</description></item>
///   <item><description>E.4: Sliding window geometry is determined solely by configuration</description></item>
///   <item><description>D.1, D.2: Analytical/pure (CPU-only), never mutates cache state</description></item>
/// </list>
/// <para><strong>Related:</strong> <see cref="NoRebalanceSatisfactionPolicy{TRange}"/> (threshold calculation, <b>when</b> to rebalance logic)</para>
/// <para>See: <see href="../docs/components/decision.md" /> for architectural overview.</para>
/// </remarks>
/// <typeparam name="TRange">Type representing the boundaries of a window/range; must be comparable (see <see cref="IComparable{TRange}"/>) so intervals can be ordered and spanned.</typeparam>
/// <typeparam name="TDomain">Provides domain-specific logic to compute spans, boundaries, and interval arithmetic for <c>TRange</c>.</typeparam>
internal sealed class ProportionalRangePlanner<TRange, TDomain>
    where TRange : IComparable<TRange>
    where TDomain : IRangeDomain<TRange>
{
    private readonly RuntimeCacheOptionsHolder _optionsHolder;
    private readonly TDomain _domain;

    /// <summary>
    ///     Initializes a new instance of <see cref="ProportionalRangePlanner{TRange, TDomain}"/> with the specified options holder and domain definition.
    /// </summary>
    /// <param name="optionsHolder">
    ///     Shared holder for the current runtime options snapshot. The planner reads
    ///     <see cref="RuntimeCacheOptionsHolder.Current"/> once per <see cref="Plan"/> invocation so that
    ///     changes published via <c>IWindowCache.UpdateRuntimeOptions</c> take effect on the next cycle.
    /// </param>
    /// <param name="domain">Domain implementation used for range arithmetic and span calculations.</param>
    /// <remarks>
    /// <para>
    ///   This constructor wires the planner to a shared options holder and domain only; it does not perform any computation or validation. The planner is invoked by <c>RebalanceDecisionEngine</c> during Stage 3 (Desired Range Computation) of the decision evaluation pipeline, which executes in the background intent processing loop.
    /// </para>
    /// <para>
    ///   <b>References:</b> Invariants E.1-E.4, D.1-D.2 (see <c>docs/invariants.md</c>).
    /// </para>
    /// </remarks>
    public ProportionalRangePlanner(RuntimeCacheOptionsHolder optionsHolder, TDomain domain)
    {
        _optionsHolder = optionsHolder;
        _domain = domain;
    }

    /// <summary>
    ///     Computes the canonical <c>DesiredCacheRange</c> to target for a given <paramref name="requested"/> window, expanding left/right according to the current runtime configuration.
    /// </summary>
    /// <param name="requested">User-requested range for which cache expansion should be planned.</param>
    /// <returns>
    ///     The canonical <c>DesiredCacheRange</c> — representing the window the cache should hold to optimally satisfy the request with proportional left/right extension.
    /// </returns>
    /// <remarks>
    /// <para>This method:
    ///   <list type="bullet">
    ///     <item><description>Snapshots <see cref="RuntimeCacheOptionsHolder.Current"/> once at entry for consistency within the invocation</description></item>
    ///     <item><description>Defines the <b>shape</b> of the sliding window, not the contents</description></item>
    ///     <item><description>Is pure/side-effect free: No cache state or I/O interaction</description></item>
    ///     <item><description>Applies only the current options snapshot and domain arithmetic (see <c>LeftCacheSize</c>, <c>RightCacheSize</c> on <see cref="RuntimeCacheOptions"/>)</description></item>
    ///     <item><description>Does <b>not</b> trigger or decide rebalance — strictly analytical</description></item>
    ///     <item><description>Enforces Invariants: E.1 (function of <c>RequestedRange + config</c>), E.2 (independent of cache state), E.3 (defines canonical convergent target), D.1-D.2 (analytical/CPU-only)</description></item>
    ///   </list>
    /// </para>
    /// <para>
    ///   Typical usage: Invoked during Stage 3 of the rebalance decision pipeline by <c>RebalanceDecisionEngine.Evaluate()</c>, which runs in the background intent processing loop (<c>IntentController.ProcessIntentsAsync</c>). Executes after stability checks (Stages 1-2) and before equality validation (Stage 4).
    /// </para>
    /// <para>See also:
    ///   <see cref="NoRebalanceSatisfactionPolicy{TRange}"/>
    ///   <see href="../docs/components/decision.md" />
    /// </para>
    /// </remarks>
    public Range<TRange> Plan(Range<TRange> requested)
    {
        // Snapshot current options once for consistency within this invocation
        var options = _optionsHolder.Current;

        var size = requested.Span(_domain);

        var left = size.Value * options.LeftCacheSize;
        var right = size.Value * options.RightCacheSize;

        return requested.Expand(
            domain: _domain,
            left: (long)Math.Round(left),
            right: (long)Math.Round(right)
        );
    }
}
