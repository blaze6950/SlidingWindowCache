namespace Intervals.NET.Caching.Public.Configuration;

/// <summary>
/// A read-only snapshot of the current runtime-updatable cache option values.
/// </summary>
/// <remarks>
/// <para><strong>Purpose:</strong></para>
/// <para>
/// Exposes the current values of the five runtime-updatable options on a live cache instance.
/// Obtained via <see cref="IWindowCache{TRange,TData,TDomain}.CurrentRuntimeOptions"/>.
/// </para>
/// <para><strong>Usage:</strong></para>
/// <code>
/// // Inspect current values
/// var current = cache.CurrentRuntimeOptions;
/// Console.WriteLine($"Left: {current.LeftCacheSize}, Right: {current.RightCacheSize}");
///
/// // Perform a relative update (e.g. double the left size)
/// var current = cache.CurrentRuntimeOptions;
/// cache.UpdateRuntimeOptions(u => u.WithLeftCacheSize(current.LeftCacheSize * 2));
/// </code>
/// <para><strong>Snapshot Semantics:</strong></para>
/// <para>
/// This object captures the option values at the moment the property was read.
/// It is not updated if <see cref="IWindowCache{TRange,TData,TDomain}.UpdateRuntimeOptions"/>
/// is called afterward — obtain a new snapshot to see updated values.
/// </para>
/// <para><strong>Relationship to RuntimeCacheOptions:</strong></para>
/// <para>
/// This is a public projection of the internal <c>RuntimeCacheOptions</c> snapshot.
/// It contains the same five values but is exposed as a public, user-facing type.
/// </para>
/// </remarks>
public sealed class RuntimeOptionsSnapshot
{
    internal RuntimeOptionsSnapshot(
        double leftCacheSize,
        double rightCacheSize,
        double? leftThreshold,
        double? rightThreshold,
        TimeSpan debounceDelay)
    {
        LeftCacheSize = leftCacheSize;
        RightCacheSize = rightCacheSize;
        LeftThreshold = leftThreshold;
        RightThreshold = rightThreshold;
        DebounceDelay = debounceDelay;
    }

    /// <summary>
    /// The coefficient for the left cache size relative to the requested range.
    /// </summary>
    public double LeftCacheSize { get; }

    /// <summary>
    /// The coefficient for the right cache size relative to the requested range.
    /// </summary>
    public double RightCacheSize { get; }

    /// <summary>
    /// The left no-rebalance threshold percentage, or <c>null</c> if the left threshold is disabled.
    /// </summary>
    public double? LeftThreshold { get; }

    /// <summary>
    /// The right no-rebalance threshold percentage, or <c>null</c> if the right threshold is disabled.
    /// </summary>
    public double? RightThreshold { get; }

    /// <summary>
    /// The debounce delay applied before executing a rebalance.
    /// </summary>
    public TimeSpan DebounceDelay { get; }
}
