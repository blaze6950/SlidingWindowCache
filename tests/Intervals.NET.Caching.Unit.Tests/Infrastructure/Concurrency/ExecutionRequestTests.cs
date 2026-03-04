using Intervals.NET.Data.Extensions;
using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Caching.Core.Rebalance.Execution;
using Intervals.NET.Caching.Core.Rebalance.Intent;
using Intervals.NET.Caching.Tests.Infrastructure.DataSources;

namespace Intervals.NET.Caching.Unit.Tests.Infrastructure.Concurrency;

/// <summary>
/// Unit tests for ExecutionRequest lifecycle behavior.
/// </summary>
public sealed class ExecutionRequestTests
{
    [Fact]
    public void Cancel_CalledAfterDispose_DoesNotThrow()
    {
        // ARRANGE
        var request = CreateRequest();
        request.Dispose();

        // ACT
        var exception = Record.Exception(() => request.Cancel());

        // ASSERT
        Assert.Null(exception);
    }

    [Fact]
    public void Dispose_CalledMultipleTimes_DoesNotThrow()
    {
        // ARRANGE
        var request = CreateRequest();

        // ACT
        request.Dispose();
        var exception = Record.Exception(() => request.Dispose());

        // ASSERT
        Assert.Null(exception);
    }

    private static ExecutionRequest<int, int, IntegerFixedStepDomain> CreateRequest()
    {
        var domain = new IntegerFixedStepDomain();
        var range = Intervals.NET.Factories.Range.Closed<int>(0, 10);
        var data = DataGenerationHelpers.GenerateDataForRange(range);
        var rangeData = data.ToRangeData(range, domain);
        var intent = new Intent<int, int, IntegerFixedStepDomain>(range, rangeData);
        var cts = new CancellationTokenSource();

        return new ExecutionRequest<int, int, IntegerFixedStepDomain>(
            intent,
            range,
            desiredNoRebalanceRange: null,
            cts
        );
    }
}
