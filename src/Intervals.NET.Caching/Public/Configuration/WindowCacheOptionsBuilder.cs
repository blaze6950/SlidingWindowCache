namespace Intervals.NET.Caching.Public.Configuration;

/// <summary>
/// Fluent builder for constructing <see cref="WindowCacheOptions"/> instances with a clean,
/// discoverable API.
/// </summary>
/// <remarks>
/// <para><strong>Purpose:</strong></para>
/// <para>
/// Provides a fluent alternative to the <see cref="WindowCacheOptions"/> constructor, especially
/// useful for inline configuration via <see cref="Cache.WindowCacheBuilder{TRange,TData,TDomain}"/> and
/// <see cref="Cache.LayeredWindowCacheBuilder{TRange,TData,TDomain}"/>.
/// </para>
/// <para><strong>Required Fields:</strong></para>
/// <para>
/// <see cref="WithLeftCacheSize"/> and <see cref="WithRightCacheSize"/> (or a convenience overload
/// such as <see cref="WithCacheSize(double)"/>) must be called before <see cref="Build"/>.
/// All other fields have sensible defaults.
/// </para>
/// <para><strong>Defaults:</strong></para>
/// <list type="bullet">
/// <item><description><strong>ReadMode</strong>: <see cref="UserCacheReadMode.Snapshot"/></description></item>
/// <item><description><strong>LeftThreshold / RightThreshold</strong>: <c>null</c> (disabled)</description></item>
/// <item><description><strong>DebounceDelay</strong>: 100 ms (applied by <see cref="WindowCacheOptions"/>)</description></item>
/// <item><description><strong>RebalanceQueueCapacity</strong>: <c>null</c> (unbounded task-based)</description></item>
/// </list>
/// <para><strong>Standalone Usage:</strong></para>
/// <code>
/// var options = new WindowCacheOptionsBuilder()
///     .WithCacheSize(1.0)
///     .WithReadMode(UserCacheReadMode.Snapshot)
///     .WithThresholds(0.2)
///     .Build();
/// </code>
/// <para><strong>Inline Usage (via cache builder):</strong></para>
/// <code>
/// var cache = WindowCacheBuilder.For(dataSource, domain)
///     .WithOptions(o =&gt; o
///         .WithCacheSize(1.0)
///         .WithThresholds(0.2))
///     .Build();
/// </code>
/// </remarks>
public sealed class WindowCacheOptionsBuilder
{
    private double? _leftCacheSize;
    private double? _rightCacheSize;
    private UserCacheReadMode _readMode = UserCacheReadMode.Snapshot;
    private double? _leftThreshold;
    private double? _rightThreshold;
    private bool _leftThresholdSet;
    private bool _rightThresholdSet;
    private TimeSpan? _debounceDelay;
    private int? _rebalanceQueueCapacity;

    /// <summary>
    /// Initializes a new instance of the <see cref="WindowCacheOptionsBuilder"/> class.
    /// </summary>
    public WindowCacheOptionsBuilder() { }

    /// <summary>
    /// Sets the left cache size coefficient.
    /// </summary>
    /// <param name="value">
    /// Multiplier of the requested range size for the left buffer. Must be &gt;= 0.
    /// A value of 0 disables left-side caching.
    /// </param>
    /// <returns>This builder instance, for fluent chaining.</returns>
    public WindowCacheOptionsBuilder WithLeftCacheSize(double value)
    {
        _leftCacheSize = value;
        return this;
    }

    /// <summary>
    /// Sets the right cache size coefficient.
    /// </summary>
    /// <param name="value">
    /// Multiplier of the requested range size for the right buffer. Must be &gt;= 0.
    /// A value of 0 disables right-side caching.
    /// </param>
    /// <returns>This builder instance, for fluent chaining.</returns>
    public WindowCacheOptionsBuilder WithRightCacheSize(double value)
    {
        _rightCacheSize = value;
        return this;
    }

    /// <summary>
    /// Sets both left and right cache size coefficients to the same value.
    /// </summary>
    /// <param name="value">
    /// Multiplier applied symmetrically to both left and right buffers. Must be &gt;= 0.
    /// </param>
    /// <returns>This builder instance, for fluent chaining.</returns>
    public WindowCacheOptionsBuilder WithCacheSize(double value)
    {
        _leftCacheSize = value;
        _rightCacheSize = value;
        return this;
    }

    /// <summary>
    /// Sets left and right cache size coefficients to different values.
    /// </summary>
    /// <param name="left">Multiplier for the left buffer. Must be &gt;= 0.</param>
    /// <param name="right">Multiplier for the right buffer. Must be &gt;= 0.</param>
    /// <returns>This builder instance, for fluent chaining.</returns>
    public WindowCacheOptionsBuilder WithCacheSize(double left, double right)
    {
        _leftCacheSize = left;
        _rightCacheSize = right;
        return this;
    }

    /// <summary>
    /// Sets the read mode that determines how materialized cache data is exposed to users.
    /// Default is <see cref="UserCacheReadMode.Snapshot"/>.
    /// </summary>
    /// <param name="value">The read mode to use.</param>
    /// <returns>This builder instance, for fluent chaining.</returns>
    public WindowCacheOptionsBuilder WithReadMode(UserCacheReadMode value)
    {
        _readMode = value;
        return this;
    }

    /// <summary>
    /// Sets the left no-rebalance threshold percentage.
    /// </summary>
    /// <param name="value">
    /// Percentage of total cache window size. Must be &gt;= 0.
    /// The sum of left and right thresholds must not exceed 1.0.
    /// </param>
    /// <returns>This builder instance, for fluent chaining.</returns>
    public WindowCacheOptionsBuilder WithLeftThreshold(double value)
    {
        _leftThresholdSet = true;
        _leftThreshold = value;
        return this;
    }

    /// <summary>
    /// Sets the right no-rebalance threshold percentage.
    /// </summary>
    /// <param name="value">
    /// Percentage of total cache window size. Must be &gt;= 0.
    /// The sum of left and right thresholds must not exceed 1.0.
    /// </param>
    /// <returns>This builder instance, for fluent chaining.</returns>
    public WindowCacheOptionsBuilder WithRightThreshold(double value)
    {
        _rightThresholdSet = true;
        _rightThreshold = value;
        return this;
    }

    /// <summary>
    /// Sets both left and right no-rebalance threshold percentages to the same value.
    /// </summary>
    /// <param name="value">
    /// Percentage applied symmetrically. Must be &gt;= 0.
    /// The combined sum (i.e. 2 × <paramref name="value"/>) must not exceed 1.0.
    /// </param>
    /// <returns>This builder instance, for fluent chaining.</returns>
    public WindowCacheOptionsBuilder WithThresholds(double value)
    {
        _leftThresholdSet = true;
        _leftThreshold = value;
        _rightThresholdSet = true;
        _rightThreshold = value;
        return this;
    }

    /// <summary>
    /// Sets the debounce delay applied before executing a rebalance.
    /// Default is 100 ms.
    /// </summary>
    /// <param name="value">
    /// Any non-negative <see cref="TimeSpan"/>. <see cref="TimeSpan.Zero"/> disables debouncing.
    /// </param>
    /// <returns>This builder instance, for fluent chaining.</returns>
    public WindowCacheOptionsBuilder WithDebounceDelay(TimeSpan value)
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
    /// Sets the rebalance execution queue capacity, selecting the bounded channel-based strategy.
    /// Default is <c>null</c> (unbounded task-based serialization).
    /// </summary>
    /// <param name="value">The bounded channel capacity. Must be &gt;= 1.</param>
    /// <returns>This builder instance, for fluent chaining.</returns>
    public WindowCacheOptionsBuilder WithRebalanceQueueCapacity(int value)
    {
        _rebalanceQueueCapacity = value;
        return this;
    }

    /// <summary>
    /// Builds a <see cref="WindowCacheOptions"/> instance from the configured values.
    /// </summary>
    /// <returns>A validated <see cref="WindowCacheOptions"/> instance.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when neither <see cref="WithLeftCacheSize"/>/<see cref="WithRightCacheSize"/> nor
    /// a <see cref="WithCacheSize(double)"/> overload has been called.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when any value fails validation (negative sizes, thresholds, or queue capacity &lt;= 0).
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the sum of left and right thresholds exceeds 1.0.
    /// </exception>
    public WindowCacheOptions Build()
    {
        if (_leftCacheSize is null || _rightCacheSize is null)
        {
            throw new InvalidOperationException(
                "LeftCacheSize and RightCacheSize must be configured. " +
                "Use WithLeftCacheSize()/WithRightCacheSize() or WithCacheSize() to set them.");
        }

        return new WindowCacheOptions(
            _leftCacheSize.Value,
            _rightCacheSize.Value,
            _readMode,
            _leftThresholdSet ? _leftThreshold : null,
            _rightThresholdSet ? _rightThreshold : null,
            _debounceDelay,
            _rebalanceQueueCapacity
        );
    }
}
