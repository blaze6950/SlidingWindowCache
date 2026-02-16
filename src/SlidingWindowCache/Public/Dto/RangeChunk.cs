using Intervals.NET;

namespace SlidingWindowCache.Public.Dto;

/// <summary>
/// Represents a chunk of data associated with a specific range. This is used to encapsulate the data fetched for a particular range in the sliding window cache.
/// </summary>
public record RangeChunk<TRangeType, TDataType>(Range<TRangeType> Range, IEnumerable<TDataType> Data)
    where TRangeType : IComparable<TRangeType>;