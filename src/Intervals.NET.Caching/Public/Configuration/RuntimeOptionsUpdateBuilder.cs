namespace Intervals.NET.Caching.Public.Configuration;

/// <summary>
/// Fluent builder for specifying runtime option updates on a live <see cref="IWindowCache{TRange,TData,TDomain}"/> instance.
/// </summary>
/// <remarks>
/// <para><strong>Usage:</strong></para>
/// <code>
/// cache.UpdateRuntimeOptions(update =>
///     update.WithLeftCacheSize(2.0)
///           .WithRightCacheSize(3.0)
///           .WithDebounceDelay(TimeSpan.FromMilliseconds(50)));
/// </code>
/// <para><strong>Partial Updates:</strong></para>
/// <para>
/// Only the fields explicitly set on the builder are changed. All other fields retain their current values.
/// For example, calling only <c>WithLeftCacheSize</c> leaves <c>RightCacheSize</c>, thresholds, and
/// <c>DebounceDelay</c> unchanged.
/// </para>
/// <para><strong>Double-Nullable Thresholds:</strong></para>
/// <para>
/// Because <c>LeftThreshold</c> and <c>RightThreshold</c> are <c>double?</c>, three states must be
/// distinguishable for each:
/// <list type="bullet">
/// <item><description><em>Not specified</em> — keep existing value (default)</description></item>
/// <item><description><em>Set to a value</em> — use <see cref="WithLeftThreshold"/> / <see cref="WithRightThreshold"/></description></item>
/// <item><description><em>Set to null (disabled)</em> — use <see cref="ClearLeftThreshold"/> / <see cref="ClearRightThreshold"/></description></item>
/// </list>
/// </para>
/// <para><strong>Validation:</strong></para>
/// <para>
/// Validation of the merged options (current + deltas) is performed inside
/// <c>IWindowCache.UpdateRuntimeOptions</c> before publishing. If validation fails, an exception is thrown
/// and the current options are left unchanged.
/// </para>
/// <para><strong>"Next Cycle" Semantics:</strong></para>
/// <para>
/// Published updates take effect on the next rebalance decision/execution cycle. In-flight operations
/// continue with the options that were active when they started.
/// </para>
/// </remarks>
public sealed class RuntimeOptionsUpdateBuilder
{
    private double? _leftCacheSize;
    private double? _rightCacheSize;

    // For thresholds we need three states: not set, set to a value, cleared to null.
    // We use a bool flag + nullable value pair: flag=false means "not specified", flag=true means "specified".
    private bool _leftThresholdSet;
    private double? _leftThresholdValue;
    private bool _rightThresholdSet;
    private double? _rightThresholdValue;

    private TimeSpan? _debounceDelay;

    internal RuntimeOptionsUpdateBuilder() { }

    /// <summary>
    /// Sets the left cache size coefficient.
    /// </summary>
    /// <param name="value">Must be ≥ 0.</param>
    /// <returns>This builder, for fluent chaining.</returns>
    public RuntimeOptionsUpdateBuilder WithLeftCacheSize(double value)
    {
        _leftCacheSize = value;
        return this;
    }

    /// <summary>
    /// Sets the right cache size coefficient.
    /// </summary>
    /// <param name="value">Must be ≥ 0.</param>
    /// <returns>This builder, for fluent chaining.</returns>
    public RuntimeOptionsUpdateBuilder WithRightCacheSize(double value)
    {
        _rightCacheSize = value;
        return this;
    }

    /// <summary>
    /// Sets the left no-rebalance threshold to the specified value.
    /// </summary>
    /// <param name="value">Must be in [0, 1].</param>
    /// <returns>This builder, for fluent chaining.</returns>
    public RuntimeOptionsUpdateBuilder WithLeftThreshold(double value)
    {
        _leftThresholdSet = true;
        _leftThresholdValue = value;
        return this;
    }

    /// <summary>
    /// Clears (disables) the left no-rebalance threshold by setting it to <c>null</c>.
    /// </summary>
    /// <returns>This builder, for fluent chaining.</returns>
    public RuntimeOptionsUpdateBuilder ClearLeftThreshold()
    {
        _leftThresholdSet = true;
        _leftThresholdValue = null;
        return this;
    }

    /// <summary>
    /// Sets the right no-rebalance threshold to the specified value.
    /// </summary>
    /// <param name="value">Must be in [0, 1].</param>
    /// <returns>This builder, for fluent chaining.</returns>
    public RuntimeOptionsUpdateBuilder WithRightThreshold(double value)
    {
        _rightThresholdSet = true;
        _rightThresholdValue = value;
        return this;
    }

    /// <summary>
    /// Clears (disables) the right no-rebalance threshold by setting it to <c>null</c>.
    /// </summary>
    /// <returns>This builder, for fluent chaining.</returns>
    public RuntimeOptionsUpdateBuilder ClearRightThreshold()
    {
        _rightThresholdSet = true;
        _rightThresholdValue = null;
        return this;
    }

    /// <summary>
    /// Sets the debounce delay applied before executing a rebalance.
    /// </summary>
    /// <param name="value">Any non-negative <see cref="TimeSpan"/>. <see cref="TimeSpan.Zero"/> disables debouncing.</param>
    /// <returns>This builder, for fluent chaining.</returns>
    public RuntimeOptionsUpdateBuilder WithDebounceDelay(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(value),
                "DebounceDelay must be non-negative.");
        }

        _debounceDelay = value;
        return this;
    }

    /// <summary>
    /// Applies the accumulated deltas to <paramref name="current"/> and returns a new
    /// <see cref="Core.State.RuntimeCacheOptions"/> snapshot.
    /// Fields that were not explicitly set on the builder retain their values from <paramref name="current"/>.
    /// </summary>
    /// <param name="current">The snapshot to merge deltas onto.</param>
    /// <returns>A new validated snapshot.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when any merged value fails validation.</exception>
    /// <exception cref="ArgumentException">Thrown when the merged threshold sum exceeds 1.0.</exception>
    internal Core.State.RuntimeCacheOptions ApplyTo(Core.State.RuntimeCacheOptions current)
    {
        var leftCacheSize = _leftCacheSize ?? current.LeftCacheSize;
        var rightCacheSize = _rightCacheSize ?? current.RightCacheSize;
        var leftThreshold = _leftThresholdSet ? _leftThresholdValue : current.LeftThreshold;
        var rightThreshold = _rightThresholdSet ? _rightThresholdValue : current.RightThreshold;
        var debounceDelay = _debounceDelay ?? current.DebounceDelay;

        return new Core.State.RuntimeCacheOptions(
            leftCacheSize,
            rightCacheSize,
            leftThreshold,
            rightThreshold,
            debounceDelay
        );
    }
}
