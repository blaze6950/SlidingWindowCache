using SlidingWindowCache.Public.Configuration;

namespace SlidingWindowCache.Core.State;

/// <summary>
/// An immutable snapshot of the runtime-updatable cache configuration values.
/// </summary>
/// <remarks>
/// <para><strong>Architectural Context:</strong></para>
/// <para>
/// <see cref="RuntimeCacheOptions"/> holds the five configuration values that may be changed on a live
/// cache instance via <c>IWindowCache.UpdateRuntimeOptions</c>. It is always treated as an immutable
/// snapshot: updates create a new instance which is then atomically published via
/// <see cref="RuntimeCacheOptionsHolder"/>.
/// </para>
/// <para><strong>Snapshot Consistency:</strong></para>
/// <para>
/// Because the holder swaps the entire reference atomically (Volatile.Write), all five values are always
/// observed as a consistent set by background threads reading <see cref="RuntimeCacheOptionsHolder.Current"/>.
/// There is never a window where some values belong to an old update and others to a new one.
/// </para>
/// <para><strong>Validation:</strong></para>
/// <para>
/// Applies the same validation rules as
/// <see cref="SlidingWindowCache.Public.Configuration.WindowCacheOptions"/>:
/// cache sizes ≥ 0, thresholds in [0, 1], threshold sum ≤ 1.0.
/// </para>
/// <para><strong>Threading:</strong></para>
/// <para>
/// Instances are read-only after construction and therefore inherently thread-safe.
/// The holder manages the visibility of the current snapshot across threads.
/// </para>
/// </remarks>
internal sealed class RuntimeCacheOptions
{
    /// <summary>
    /// Initializes a new <see cref="RuntimeCacheOptions"/> snapshot and validates all values.
    /// </summary>
    /// <param name="leftCacheSize">The coefficient for the left cache size. Must be ≥ 0.</param>
    /// <param name="rightCacheSize">The coefficient for the right cache size. Must be ≥ 0.</param>
    /// <param name="leftThreshold">The left no-rebalance threshold percentage. Must be in [0, 1] when not null.</param>
    /// <param name="rightThreshold">The right no-rebalance threshold percentage. Must be in [0, 1] when not null.</param>
    /// <param name="debounceDelay">The debounce delay applied before executing a rebalance. Must be non-negative.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="leftCacheSize"/> or <paramref name="rightCacheSize"/> is less than 0,
    /// when a threshold value is outside [0, 1], or when <paramref name="debounceDelay"/> is negative.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when both thresholds are specified and their sum exceeds 1.0.
    /// </exception>
    public RuntimeCacheOptions(
        double leftCacheSize,
        double rightCacheSize,
        double? leftThreshold,
        double? rightThreshold,
        TimeSpan debounceDelay)
    {
        RuntimeOptionsValidator.ValidateCacheSizesAndThresholds(
            leftCacheSize, rightCacheSize, leftThreshold, rightThreshold);

        if (debounceDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(debounceDelay),
                "DebounceDelay must be non-negative.");
        }

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
    /// The left no-rebalance threshold percentage, or <c>null</c> to disable the left threshold.
    /// </summary>
    public double? LeftThreshold { get; }

    /// <summary>
    /// The right no-rebalance threshold percentage, or <c>null</c> to disable the right threshold.
    /// </summary>
    public double? RightThreshold { get; }

    /// <summary>
    /// The debounce delay applied before executing a rebalance.
    /// </summary>
    public TimeSpan DebounceDelay { get; }

    /// <summary>
    /// Projects this internal snapshot to a public <see cref="RuntimeOptionsSnapshot"/> DTO.
    /// </summary>
    internal RuntimeOptionsSnapshot ToSnapshot() =>
        new(LeftCacheSize, RightCacheSize, LeftThreshold, RightThreshold, DebounceDelay);
}