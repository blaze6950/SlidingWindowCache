namespace SlidingWindowCache.Public.Configuration;

/// <summary>
/// Options for configuring the behavior of the sliding window cache.
/// </summary>
public record WindowCacheOptions
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WindowCacheOptions"/> class.
    /// </summary>
    /// <param name="leftCacheSize">The coefficient for the left cache size.</param>
    /// <param name="rightCacheSize">The coefficient for the right cache size.</param>
    /// <param name="readMode">
    /// The read mode that determines how materialized cache data is exposed to users.
    /// This can affect the performance and memory usage of the cache,
    /// as well as the consistency guarantees provided to users.
    /// </param>
    /// <param name="leftThreshold">The left threshold percentage (optional).</param>
    /// <param name="rightThreshold">The right threshold percentage (optional).</param>
    /// <param name="debounceDelay">The debounce delay for rebalance operations (optional).</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when LeftCacheSize, RightCacheSize, LeftThreshold, or RightThreshold is less than 0.
    /// </exception>
    public WindowCacheOptions(
        double leftCacheSize,
        double rightCacheSize,
        UserCacheReadMode readMode,
        double? leftThreshold = null,
        double? rightThreshold = null,
        TimeSpan? debounceDelay = null
    )
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

        LeftCacheSize = leftCacheSize;
        RightCacheSize = rightCacheSize;
        ReadMode = readMode;
        LeftThreshold = leftThreshold;
        RightThreshold = rightThreshold;
        DebounceDelay = debounceDelay ?? TimeSpan.FromMilliseconds(100);
    }

    /// <summary>
    /// The coefficient to determine the size of the left cache relative to the requested range.
    /// If requested range size is S, left cache size will be S * LeftCacheSize.
    /// Can be set as 0 to disable left caching. Must be greater than or equal to 0
    /// </summary>
    public double LeftCacheSize { get; }

    /// <summary>
    /// The coefficient to determine the size of the right cache relative to the requested range.
    /// If requested range size is S, right cache size will be S * RightCacheSize.
    /// Can be set as 0 to disable right caching. Must be greater than or equal to 0
    /// </summary>
    public double RightCacheSize { get; }

    /// <summary>
    /// The amount of percents of the total cache size that must be exceeded to trigger a rebalance.
    /// The total cache size is defined as the sum of the left, requested range, and right cache sizes.
    /// Can be set as null to disable rebalance based on left threshold. If only one threshold is set,
    /// rebalance will be triggered when that threshold is exceeded or end of the cached range is exceeded.
    /// Must be greater than or equal to 0
    /// Example: 0.2 means 20% of total cache size. Means if the next requested range and the start of the range contains less than 20% of the total cache size, a rebalance will be triggered.
    /// </summary>
    public double? LeftThreshold { get; }

    /// <summary>
    /// The amount of percents of the total cache size that must be exceeded to trigger a rebalance.
    /// The total cache size is defined as the sum of the left, requested range, and right cache sizes.
    /// Can be set as null to disable rebalance based on right threshold. If only one threshold is set,
    /// rebalance will be triggered when that threshold is exceeded or start of the cached range is exceeded.
    /// Must be greater than or equal to 0
    /// Example: 0.2 means 20% of total cache size. Means if the next requested range and the end of the range contains less than 20% of the total cache size, a rebalance will be triggered.
    /// </summary>
    public double? RightThreshold { get; }

    /// <summary>
    /// The debounce delay for rebalance operations.
    /// Default is TimeSpan.FromMilliseconds(100).
    /// </summary>
    public TimeSpan DebounceDelay { get; }

    /// <summary>
    /// The read mode that determines how materialized cache data is exposed to users.
    /// </summary>
    public UserCacheReadMode ReadMode { get; }
}