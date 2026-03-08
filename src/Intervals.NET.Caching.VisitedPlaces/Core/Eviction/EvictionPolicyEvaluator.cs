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
///   Notifies <see cref="IStatefulEvictionPolicy{TRange,TData}"/> instances of segment
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
/// <see cref="BackgroundEventProcessor{TRange,TData,TDomain}"/> previously held all of this
/// logic inline. Moving it here simplifies the processor and creates a clean boundary for
/// stateful policy support. The processor is unaware of whether any given policy is stateful;
/// it only calls the three evaluator methods at the appropriate points in the four-step sequence.
/// </para>
/// <para><strong>Stateful vs. Stateless policies:</strong></para>
/// <para>
/// Policies that implement <see cref="IStatefulEvictionPolicy{TRange,TData}"/> receive
/// <see cref="OnSegmentAdded"/> and <see cref="OnSegmentRemoved"/> notifications and can
/// therefore run their <see cref="IEvictionPolicy{TRange,TData}.Evaluate"/> in O(1).
/// Policies that only implement the base <see cref="IEvictionPolicy{TRange,TData}"/> interface
/// (e.g., <see cref="Policies.MaxSegmentCountPolicy{TRange,TData}"/>) are stateless: they
/// receive no lifecycle notifications and recompute their metric from <c>allSegments</c> in
/// <c>Evaluate</c> — which is acceptable when the metric is already O(1)
/// (e.g., <c>allSegments.Count</c>).
/// </para>
/// </remarks>
internal sealed class EvictionPolicyEvaluator<TRange, TData>
    where TRange : IComparable<TRange>
{
    private readonly IReadOnlyList<IEvictionPolicy<TRange, TData>> _policies;
    private readonly IStatefulEvictionPolicy<TRange, TData>[] _statefulPolicies;

    /// <summary>
    /// Initializes a new <see cref="EvictionPolicyEvaluator{TRange,TData}"/>.
    /// </summary>
    /// <param name="policies">
    /// The eviction policies to evaluate. Policies that implement
    /// <see cref="IStatefulEvictionPolicy{TRange,TData}"/> will receive lifecycle notifications;
    /// all others are evaluated statelessly via
    /// <see cref="IEvictionPolicy{TRange,TData}.Evaluate"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="policies"/> is <see langword="null"/>.
    /// </exception>
    public EvictionPolicyEvaluator(IReadOnlyList<IEvictionPolicy<TRange, TData>> policies)
    {
        ArgumentNullException.ThrowIfNull(policies);

        _policies = policies;
        _statefulPolicies = policies
            .OfType<IStatefulEvictionPolicy<TRange, TData>>()
            .ToArray();
    }

    /// <summary>
    /// Notifies all <see cref="IStatefulEvictionPolicy{TRange,TData}"/> instances that a
    /// new segment has been added to storage.
    /// </summary>
    /// <param name="segment">The segment that was just added to storage.</param>
    /// <remarks>
    /// Called by <see cref="BackgroundEventProcessor{TRange,TData,TDomain}"/> in Step 2
    /// (store data) immediately after each segment is added to storage and selector metadata
    /// is initialized.
    /// </remarks>
    public void OnSegmentAdded(CachedSegment<TRange, TData> segment)
    {
        foreach (var policy in _statefulPolicies)
        {
            policy.OnSegmentAdded(segment);
        }
    }

    /// <summary>
    /// Notifies all <see cref="IStatefulEvictionPolicy{TRange,TData}"/> instances that a
    /// segment has been removed from storage.
    /// </summary>
    /// <param name="segment">The segment that was just removed from storage.</param>
    /// <remarks>
    /// Called by <see cref="BackgroundEventProcessor{TRange,TData,TDomain}"/> in Step 4
    /// (execute eviction) immediately after each segment is removed from storage.
    /// </remarks>
    public void OnSegmentRemoved(CachedSegment<TRange, TData> segment)
    {
        foreach (var policy in _statefulPolicies)
        {
            policy.OnSegmentRemoved(segment);
        }
    }

    /// <summary>
    /// Evaluates all registered policies against the current segment collection and returns
    /// a combined pressure representing all violated constraints.
    /// </summary>
    /// <param name="allSegments">All currently stored segments.</param>
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
    /// Called by <see cref="BackgroundEventProcessor{TRange,TData,TDomain}"/> in Step 3
    /// (evaluate eviction), only when at least one segment was stored in the current event cycle.
    /// </remarks>
    public IEvictionPressure<TRange, TData> Evaluate(
        IReadOnlyList<CachedSegment<TRange, TData>> allSegments)
    {
        // Collect exceeded pressures without allocating unless at least one policy fires.
        // Common case: no policy fires → return singleton NoPressure without any allocation.
        IEvictionPressure<TRange, TData>? singleExceeded = null;
        List<IEvictionPressure<TRange, TData>>? multipleExceeded = null;

        foreach (var policy in _policies)
        {
            var pressure = policy.Evaluate(allSegments);

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
