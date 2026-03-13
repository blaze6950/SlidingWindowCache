using Intervals.NET.Caching.VisitedPlaces.Core.Background;
using Intervals.NET.Caching.VisitedPlaces.Core.Eviction.Pressure;

namespace Intervals.NET.Caching.VisitedPlaces.Core.Eviction;

/// <summary>
/// Encapsulates the full eviction policy pipeline: segment lifecycle notifications,
/// multi-policy evaluation, and composite pressure construction.
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <remarks>
/// <para><strong>Responsibilities:</strong></para>
/// <list type="bullet">
/// <item><description>
///   Notifies all <see cref="IEvictionPolicy{TRange,TData}"/> instances of segment
///   lifecycle events (<see cref="OnSegmentAdded"/>, <see cref="OnSegmentRemoved"/>) so they
///   can maintain incremental state and avoid O(N) recomputation in
///   <see cref="IEvictionPolicy{TRange,TData}.Evaluate"/>.
/// </description></item>
/// <item><description>
///   Evaluates all registered policies and collects exceeded pressures.
/// </description></item>
/// <item><description>
///   Constructs a <see cref="CompositePressure{TRange,TData}"/> when multiple policies fire
///   simultaneously, or returns the single exceeded pressure directly when only one fires.
/// </description></item>
/// <item><description>
///   Returns <see cref="NoPressure{TRange,TData}.Instance"/> when no policy constraint is
///   violated (<see cref="IEvictionPressure{TRange,TData}.IsExceeded"/> is <see langword="false"/>).
/// </description></item>
/// </list>
/// <para><strong>Execution Context:</strong> Background Path (single writer thread)</para>
/// <para><strong>Design:</strong></para>
/// <para>
/// <see cref="CacheNormalizationExecutor{TRange,TData,TDomain}"/> previously held all of this
/// logic inline. Moving it here simplifies the executor and creates a clean boundary for
/// stateful policy support. The executor is unaware of whether any given policy maintains
/// internal state; it only calls the three evaluator methods at the appropriate points in
/// the four-step sequence.
/// </para>
/// <para><strong>All policies are stateful:</strong></para>
/// <para>
/// All <see cref="IEvictionPolicy{TRange,TData}"/> implementations maintain incremental state
/// via <see cref="IEvictionPolicy{TRange,TData}.OnSegmentAdded"/> and
/// <see cref="IEvictionPolicy{TRange,TData}.OnSegmentRemoved"/>. Every registered policy
/// receives lifecycle notifications; <see cref="IEvictionPolicy{TRange,TData}.Evaluate"/>
/// runs in O(1) by reading the cached aggregate.
/// </para>
/// </remarks>
internal sealed class EvictionPolicyEvaluator<TRange, TData>
    where TRange : IComparable<TRange>
{
    private readonly IReadOnlyList<IEvictionPolicy<TRange, TData>> _policies;

    /// <summary>
    /// Initializes a new <see cref="EvictionPolicyEvaluator{TRange,TData}"/>.
    /// </summary>
    /// <param name="policies">
    /// The eviction policies to evaluate. All policies receive lifecycle notifications
    /// (<see cref="OnSegmentAdded"/>, <see cref="OnSegmentRemoved"/>) and are evaluated via
    /// <see cref="IEvictionPolicy{TRange,TData}.Evaluate"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="policies"/> is <see langword="null"/>.
    /// </exception>
    public EvictionPolicyEvaluator(IReadOnlyList<IEvictionPolicy<TRange, TData>> policies)
    {
        ArgumentNullException.ThrowIfNull(policies);

        _policies = policies;
    }

    /// <summary>
    /// Notifies all <see cref="IEvictionPolicy{TRange,TData}"/> instances that a
    /// new segment has been added to storage.
    /// </summary>
    /// <param name="segment">The segment that was just added to storage.</param>
    /// <remarks>
    /// Called by <see cref="CacheNormalizationExecutor{TRange,TData,TDomain}"/> in Step 2
    /// (store data) immediately after each segment is added to storage and selector metadata
    /// is initialized.
    /// </remarks>
    public void OnSegmentAdded(CachedSegment<TRange, TData> segment)
    {
        foreach (var policy in _policies)
        {
            policy.OnSegmentAdded(segment);
        }
    }

    /// <summary>
    /// Notifies all <see cref="IEvictionPolicy{TRange,TData}"/> instances that a
    /// segment has been removed from storage.
    /// </summary>
    /// <param name="segment">The segment that was just removed from storage.</param>
    /// <remarks>
    /// Called by <see cref="CacheNormalizationExecutor{TRange,TData,TDomain}"/> in Step 4
    /// (execute eviction) immediately after each segment is removed from storage.
    /// </remarks>
    public void OnSegmentRemoved(CachedSegment<TRange, TData> segment)
    {
        foreach (var policy in _policies)
        {
            policy.OnSegmentRemoved(segment);
        }
    }

    /// <summary>
    /// Evaluates all registered policies against the current cached aggregates and returns
    /// a combined pressure representing all violated constraints.
    /// </summary>
    /// <returns>
    /// <list type="bullet">
    /// <item><description>
    ///   <see cref="NoPressure{TRange,TData}.Instance"/> — when no policy constraint is violated
    ///   (no eviction needed). <see cref="IEvictionPressure{TRange,TData}.IsExceeded"/> is
    ///   <see langword="false"/>.
    /// </description></item>
    /// <item><description>
    ///   A single <see cref="IEvictionPressure{TRange,TData}"/> — when exactly one policy fires.
    /// </description></item>
    /// <item><description>
    ///   A <see cref="CompositePressure{TRange,TData}"/> — when two or more policies fire
    ///   simultaneously (OR semantics, Invariant VPC.E.1a).
    /// </description></item>
    /// </list>
    /// </returns>
    /// <remarks>
    /// Called by <see cref="CacheNormalizationExecutor{TRange,TData,TDomain}"/> in Step 3
    /// (evaluate eviction), only when at least one segment was stored in the current request cycle.
    /// </remarks>
    public IEvictionPressure<TRange, TData> Evaluate()
    {
        // Collect exceeded pressures without allocating unless at least one policy fires.
        // Common case: no policy fires → return singleton NoPressure without any allocation.
        IEvictionPressure<TRange, TData>? singleExceeded = null;
        List<IEvictionPressure<TRange, TData>>? multipleExceeded = null;

        foreach (var policy in _policies)
        {
            var pressure = policy.Evaluate();

            if (!pressure.IsExceeded)
            {
                continue;
            }

            if (singleExceeded is null)
            {
                singleExceeded = pressure;
            }
            else
            {
                multipleExceeded ??= [singleExceeded];
                multipleExceeded.Add(pressure);
            }
        }

        if (multipleExceeded is not null)
        {
            return new CompositePressure<TRange, TData>([.. multipleExceeded]);
        }

        return singleExceeded ?? NoPressure<TRange, TData>.Instance;
    }
}
