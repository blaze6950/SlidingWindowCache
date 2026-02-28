using Intervals.NET;
using Intervals.NET.Data.Extensions;
using Intervals.NET.Domain.Default.Numeric;
using Moq;
using SlidingWindowCache.Core.Rebalance.Execution;
using SlidingWindowCache.Public;
using SlidingWindowCache.Public.Instrumentation;
using SlidingWindowCache.Public.Dto;
using SlidingWindowCache.Tests.Infrastructure.DataSources;

namespace SlidingWindowCache.Unit.Tests.Infrastructure.Concurrency;

/// <summary>
/// Unit tests for CacheDataExtensionService.
/// Validates cache replacement diagnostics on non-overlapping requests.
/// </summary>
public sealed class CacheDataExtensionServiceTests
{
    [Fact]
    public async Task ExtendCacheAsync_NoOverlap_RecordsCacheReplaced()
    {
        // ARRANGE
        var domain = new IntegerFixedStepDomain();
        var diagnostics = new EventCounterCacheDiagnostics();

        var dataSource = new Mock<IDataSource<int, int>>();
        dataSource
            .Setup(ds => ds.FetchAsync(It.IsAny<IEnumerable<Range<int>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<Range<int>> ranges, CancellationToken _) =>
            {
                var chunks = new List<RangeChunk<int, int>>();
                foreach (var range in ranges)
                {
                    var data = DataGenerationHelpers.GenerateDataForRange(range);
                    chunks.Add(new RangeChunk<int, int>(range, data));
                }

                return chunks;
            });

        var service = new CacheDataExtensionService<int, int, IntegerFixedStepDomain>(
            dataSource.Object,
            domain,
            diagnostics
        );

        var currentRange = Intervals.NET.Factories.Range.Closed<int>(0, 10);
        var currentData = Enumerable.Range(0, 11).ToArray().ToRangeData(currentRange, domain);
        var requestedRange = Intervals.NET.Factories.Range.Closed<int>(1000, 1010);
        // ACT
        _ = await service.ExtendCacheAsync(currentData, requestedRange, CancellationToken.None);

        // ASSERT
        Assert.Equal(1, diagnostics.CacheReplaced);
        Assert.Equal(0, diagnostics.CacheExpanded);
    }
}
