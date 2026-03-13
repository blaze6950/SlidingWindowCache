namespace Intervals.NET.Caching.VisitedPlaces.Public.Configuration;

/// <summary>
/// Fluent builder for constructing <see cref="VisitedPlacesCacheOptions{TRange,TData}"/>.
/// </summary>
/// <typeparam name="TRange">The type representing range boundaries.</typeparam>
/// <typeparam name="TData">The type of data being cached.</typeparam>
/// <remarks>
/// Obtain an instance via
/// <see cref="Cache.VisitedPlacesCacheBuilder{TRange,TData,TDomain}.WithOptions(Action{VisitedPlacesCacheOptionsBuilder{TRange,TData}})"/>.
/// </remarks>
public sealed class VisitedPlacesCacheOptionsBuilder<TRange, TData>
    where TRange : IComparable<TRange>
{
    private StorageStrategyOptions<TRange, TData> _storageStrategy =
        SnapshotAppendBufferStorageOptions<TRange, TData>.Default;
    private int? _eventChannelCapacity;
    private TimeSpan? _segmentTtl;

    /// <summary>
    /// Sets the storage strategy by supplying a typed options object.
    /// Defaults to <see cref="SnapshotAppendBufferStorageOptions{TRange,TData}.Default"/>.
    /// </summary>
    /// <param name="strategy">
    /// A storage strategy options object, such as
    /// <see cref="SnapshotAppendBufferStorageOptions{TRange,TData}"/> or
    /// <see cref="LinkedListStrideIndexStorageOptions{TRange,TData}"/>.
    /// Must be non-null.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="strategy"/> is <see langword="null"/>.
    /// </exception>
    public VisitedPlacesCacheOptionsBuilder<TRange, TData> WithStorageStrategy(
        StorageStrategyOptions<TRange, TData> strategy)
    {
        _storageStrategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        return this;
    }

    /// <summary>
    /// Sets the background event channel capacity.
    /// Defaults to <see langword="null"/> (unbounded task-chaining scheduling).
    /// </summary>
    public VisitedPlacesCacheOptionsBuilder<TRange, TData> WithEventChannelCapacity(int capacity)
    {
        _eventChannelCapacity = capacity;
        return this;
    }

    /// <summary>
    /// Sets the time-to-live for each cached segment.
    /// When set, segments are automatically removed after this duration from the time they are stored.
    /// Defaults to <see langword="null"/> (no TTL — segments are only removed via eviction policies).
    /// </summary>
    /// <param name="ttl">
    /// The TTL duration. Must be &gt; <see cref="TimeSpan.Zero"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="ttl"/> is &lt;= <see cref="TimeSpan.Zero"/>.
    /// </exception>
    public VisitedPlacesCacheOptionsBuilder<TRange, TData> WithSegmentTtl(TimeSpan ttl)
    {
        if (ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ttl),
                "SegmentTtl must be greater than TimeSpan.Zero.");
        }

        _segmentTtl = ttl;
        return this;
    }

    /// <summary>
    /// Builds and returns a <see cref="VisitedPlacesCacheOptions{TRange,TData}"/> with the configured values.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when any value fails validation.
    /// </exception>
    public VisitedPlacesCacheOptions<TRange, TData> Build() => new(_storageStrategy, _eventChannelCapacity, _segmentTtl);
}
