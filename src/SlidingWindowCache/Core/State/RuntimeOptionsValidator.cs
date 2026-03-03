namespace SlidingWindowCache.Core.State;

/// <summary>
/// Provides shared validation logic for runtime-updatable cache option values.
/// </summary>
/// <remarks>
/// <para><strong>Purpose:</strong></para>
/// <para>
/// Centralizes the validation rules that are common to both
/// <see cref="RuntimeCacheOptions"/> and
/// <see cref="SlidingWindowCache.Public.Configuration.WindowCacheOptions"/>,
/// eliminating duplication and ensuring both classes enforce identical constraints.
/// </para>
/// <para><strong>Validated Rules:</strong></para>
/// <list type="bullet">
/// <item><description><c>leftCacheSize</c> ≥ 0</description></item>
/// <item><description><c>rightCacheSize</c> ≥ 0</description></item>
/// <item><description><c>leftThreshold</c> in [0, 1] when not null</description></item>
/// <item><description><c>rightThreshold</c> in [0, 1] when not null</description></item>
/// <item><description>Sum of both thresholds ≤ 1.0 when both are specified</description></item>
/// </list>
/// <para><strong>Not Validated Here:</strong></para>
/// <para>
/// Creation-time-only options (<c>rebalanceQueueCapacity</c>) are validated directly
/// in <see cref="SlidingWindowCache.Public.Configuration.WindowCacheOptions"/>
/// because they do not exist on <see cref="RuntimeCacheOptions"/>.
/// <c>DebounceDelay</c> has no range constraint and is not validated.
/// </para>
/// </remarks>
internal static class RuntimeOptionsValidator
{
    /// <summary>
    /// Validates cache size and threshold values that are shared between
    /// <see cref="RuntimeCacheOptions"/> and
    /// <see cref="SlidingWindowCache.Public.Configuration.WindowCacheOptions"/>.
    /// </summary>
    /// <param name="leftCacheSize">Must be ≥ 0.</param>
    /// <param name="rightCacheSize">Must be ≥ 0.</param>
    /// <param name="leftThreshold">Must be in [0, 1] when not null.</param>
    /// <param name="rightThreshold">Must be in [0, 1] when not null.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when any size or threshold value is outside its valid range.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when both thresholds are specified and their sum exceeds 1.0.
    /// </exception>
    internal static void ValidateCacheSizesAndThresholds(
        double leftCacheSize,
        double rightCacheSize,
        double? leftThreshold,
        double? rightThreshold)
    {
        if (leftCacheSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(leftCacheSize),
                "LeftCacheSize must be greater than or equal to 0.");
        }

        if (rightCacheSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rightCacheSize),
                "RightCacheSize must be greater than or equal to 0.");
        }

        if (leftThreshold is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(leftThreshold),
                "LeftThreshold must be greater than or equal to 0.");
        }

        if (rightThreshold is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rightThreshold),
                "RightThreshold must be greater than or equal to 0.");
        }

        if (leftThreshold is > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(leftThreshold),
                "LeftThreshold must not exceed 1.0.");
        }

        if (rightThreshold is > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(rightThreshold),
                "RightThreshold must not exceed 1.0.");
        }

        // Validate that thresholds don't overlap (sum must not exceed 1.0)
        if (leftThreshold.HasValue && rightThreshold.HasValue &&
            (leftThreshold.Value + rightThreshold.Value) > 1.0)
        {
            throw new ArgumentException(
                $"The sum of LeftThreshold ({leftThreshold.Value:F6}) and RightThreshold ({rightThreshold.Value:F6}) " +
                $"must not exceed 1.0 (actual sum: {leftThreshold.Value + rightThreshold.Value:F6}). " +
                "Thresholds represent percentages of the total cache window that are shrunk from each side. " +
                "When their sum exceeds 1.0, the shrinkage zones would overlap, creating an invalid configuration.");
        }
    }
}
