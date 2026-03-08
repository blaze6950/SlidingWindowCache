namespace Intervals.NET.Caching.VisitedPlaces.Public.Configuration;

/// <summary>
/// Immutable configuration options for the sampling-based eviction selector strategy.
/// Controls how many segments are randomly examined per eviction candidate selection.
/// </summary>
/// <remarks>
/// <para><strong>Sampling-Based Eviction:</strong></para>
/// <para>
/// Rather than sorting all segments (O(N log N)), eviction selectors use random sampling:
/// they examine a small fixed number of randomly chosen segments and select the worst
/// candidate among them. This keeps eviction cost at O(<see cref="SampleSize"/>) regardless
/// of total cache size — allowing the cache to scale to hundreds of thousands or millions
/// of segments.
/// </para>
/// <para><strong>Trade-Off:</strong></para>
/// <para>
/// Larger sample sizes improve eviction quality (the selected candidate is closer to the
/// global worst) but increase per-selection cost. The default of 32 is a practical
/// sweet spot used by Redis and similar systems: it provides near-optimal eviction
/// quality while keeping each selection very cheap.
/// </para>
/// <para><strong>Usage:</strong></para>
/// <code>
/// // Use default sample size (32)
/// var selector = new LruEvictionSelector&lt;int, MyData&gt;();
///
/// // Use custom sample size
/// var selector = new LruEvictionSelector&lt;int, MyData&gt;(new EvictionSamplingOptions(sampleSize: 64));
/// </code>
/// <para><strong>When to increase SampleSize:</strong></para>
/// <list type="bullet">
/// <item><description>Workloads with highly skewed access patterns where sampling quality matters</description></item>
/// <item><description>Small caches (the extra cost is negligible when N is small)</description></item>
/// </list>
/// <para><strong>When to decrease SampleSize:</strong></para>
/// <list type="bullet">
/// <item><description>Extremely large caches under very tight CPU budgets</description></item>
/// <item><description>Workloads where eviction order doesn't matter much</description></item>
/// </list>
/// </remarks>
public sealed class EvictionSamplingOptions
{
    /// <summary>
    /// The default sample size used when no custom options are provided.
    /// </summary>
    public const int DefaultSampleSize = 32;

    /// <summary>
    /// The number of segments randomly examined during each eviction candidate selection.
    /// The worst candidate among the sampled segments is returned for eviction.
    /// </summary>
    /// <remarks>
    /// <para>Must be &gt;= 1.</para>
    /// <para>
    /// When the total number of eligible segments is smaller than <see cref="SampleSize"/>,
    /// all eligible segments are considered (the sample is naturally clamped to the pool size).
    /// </para>
    /// </remarks>
    public int SampleSize { get; }

    /// <summary>
    /// The default <see cref="EvictionSamplingOptions"/> instance using
    /// <see cref="DefaultSampleSize"/> (32).
    /// </summary>
    public static EvictionSamplingOptions Default { get; } = new EvictionSamplingOptions();

    /// <summary>
    /// Initializes a new <see cref="EvictionSamplingOptions"/>.
    /// </summary>
    /// <param name="sampleSize">
    /// The number of segments to randomly sample per eviction candidate selection.
    /// Defaults to <see cref="DefaultSampleSize"/> (32). Must be &gt;= 1.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="sampleSize"/> is less than 1.
    /// </exception>
    public EvictionSamplingOptions(int sampleSize = DefaultSampleSize)
    {
        if (sampleSize < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleSize),
                "SampleSize must be greater than or equal to 1.");
        }

        SampleSize = sampleSize;
    }
}
