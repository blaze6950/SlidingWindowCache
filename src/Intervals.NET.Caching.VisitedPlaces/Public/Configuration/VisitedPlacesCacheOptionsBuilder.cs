namespace Intervals.NET.Caching.VisitedPlaces.Public.Configuration;

/// <summary>
/// Fluent builder for constructing <see cref="VisitedPlacesCacheOptions"/>.
/// </summary>
/// <remarks>
/// Obtain an instance via
/// <see cref="Cache.VisitedPlacesCacheBuilder{TRange,TData,TDomain}.WithOptions(Action{VisitedPlacesCacheOptionsBuilder})"/>.
/// </remarks>
public sealed class VisitedPlacesCacheOptionsBuilder
{
    private StorageStrategy _storageStrategy = StorageStrategy.SnapshotAppendBuffer;
    private int _eventChannelCapacity = 128;

    /// <summary>
    /// Sets the storage strategy for the internal segment collection.
    /// Defaults to <see cref="StorageStrategy.SnapshotAppendBuffer"/>.
    /// </summary>
    public VisitedPlacesCacheOptionsBuilder WithStorageStrategy(StorageStrategy strategy)
    {
        _storageStrategy = strategy;
        return this;
    }

    /// <summary>
    /// Sets the background event channel capacity.
    /// Defaults to 128.
    /// </summary>
    public VisitedPlacesCacheOptionsBuilder WithEventChannelCapacity(int capacity)
    {
        _eventChannelCapacity = capacity;
        return this;
    }

    /// <summary>
    /// Builds and returns a <see cref="VisitedPlacesCacheOptions"/> with the configured values.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when any value fails validation.
    /// </exception>
    public VisitedPlacesCacheOptions Build() =>
        new VisitedPlacesCacheOptions(_storageStrategy, _eventChannelCapacity);
}
