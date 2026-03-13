using Intervals.NET.Caching.VisitedPlaces.Core.Eviction;

namespace Intervals.NET.Caching.VisitedPlaces.Public.Configuration;

/// <summary>
/// Fluent builder for assembling an eviction configuration (policies + selector) for a
/// <see cref="Cache.VisitedPlacesCache{TRange,TData,TDomain}"/>.
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <remarks>
/// <para><strong>Usage:</strong></para>
/// <code>
/// .WithEviction(e => e
///     .AddPolicy(MaxSegmentCountPolicy.Create&lt;int, MyData&gt;(50))
///     .WithSelector(LruEvictionSelector.Create&lt;int, MyData&gt;()))
/// </code>
/// <para><strong>OR semantics:</strong> Eviction fires when ANY added policy produces an exceeded
/// pressure. At least one policy and exactly one selector must be configured before
/// <see cref="Build"/> is called (enforced by the consuming builder).</para>
/// </remarks>
public sealed class EvictionConfigBuilder<TRange, TData>
    where TRange : IComparable<TRange>
{
    private readonly List<IEvictionPolicy<TRange, TData>> _policies = [];
    private IEvictionSelector<TRange, TData>? _selector;

    /// <summary>
    /// Adds an eviction policy to the configuration.
    /// Eviction fires when ANY added policy produces an exceeded pressure (OR semantics).
    /// </summary>
    /// <param name="policy">The eviction policy to add. Must be non-null.</param>
    /// <returns>This builder instance, for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="policy"/> is <see langword="null"/>.
    /// </exception>
    public EvictionConfigBuilder<TRange, TData> AddPolicy(IEvictionPolicy<TRange, TData> policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _policies.Add(policy);
        return this;
    }

    /// <summary>
    /// Sets the eviction selector that determines candidate ordering when eviction is triggered.
    /// Replaces any previously set selector.
    /// </summary>
    /// <param name="selector">The eviction selector to use. Must be non-null.</param>
    /// <returns>This builder instance, for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="selector"/> is <see langword="null"/>.
    /// </exception>
    public EvictionConfigBuilder<TRange, TData> WithSelector(IEvictionSelector<TRange, TData> selector)
    {
        _selector = selector ?? throw new ArgumentNullException(nameof(selector));
        return this;
    }

    /// <summary>
    /// Builds and returns the resolved eviction configuration.
    /// Called internally by the cache/layer builders after invoking the user's delegate.
    /// </summary>
    /// <returns>
    /// A tuple of the configured policies list and selector.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no policies have been added or no selector has been set.
    /// </exception>
    internal (IReadOnlyList<IEvictionPolicy<TRange, TData>> Policies, IEvictionSelector<TRange, TData> Selector) Build()
    {
        if (_policies.Count == 0)
        {
            throw new InvalidOperationException(
                "At least one eviction policy must be added. " +
                "Use AddPolicy() to add a policy before building.");
        }

        if (_selector is null)
        {
            throw new InvalidOperationException(
                "An eviction selector must be set. " +
                "Use WithSelector() to set a selector before building.");
        }

        return (_policies, _selector);
    }
}
