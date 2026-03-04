using System.Reflection;
using Intervals.NET.Data.Extensions;
using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Caching.Core.Rebalance.Execution;
using Intervals.NET.Caching.Core.Rebalance.Intent;
using Intervals.NET.Caching.Core.State;
using Intervals.NET.Caching.Infrastructure.Concurrency;
using Intervals.NET.Caching.Infrastructure.Storage;
using Intervals.NET.Caching.Public.Instrumentation;
using Intervals.NET.Caching.Tests.Infrastructure.DataSources;

namespace Intervals.NET.Caching.Unit.Tests.Infrastructure.Concurrency;

/// <summary>
/// Unit tests for TaskBasedRebalanceExecutionController.
/// Validates chain resilience when previous task is faulted.
/// </summary>
public sealed class TaskBasedRebalanceExecutionControllerTests
{
    [Fact]
    public async Task PublishExecutionRequest_ContinuesAfterFaultedPreviousTask()
    {
        // ARRANGE
        var domain = new IntegerFixedStepDomain();
        var diagnostics = new EventCounterCacheDiagnostics();
        var storage = new SnapshotReadStorage<int, int, IntegerFixedStepDomain>(domain);
        var state = new CacheState<int, int, IntegerFixedStepDomain>(storage, domain);
        var dataSource = new SimpleTestDataSource<int>(i => i);
        var cacheExtensionService = new CacheDataExtensionService<int, int, IntegerFixedStepDomain>(
            dataSource,
            domain,
            diagnostics
        );
        var executor = new RebalanceExecutor<int, int, IntegerFixedStepDomain>(
            state,
            cacheExtensionService,
            diagnostics
        );
        var activityCounter = new AsyncActivityCounter();

        var controller = new TaskBasedRebalanceExecutionController<int, int, IntegerFixedStepDomain>(
            executor,
            new RuntimeCacheOptionsHolder(new RuntimeCacheOptions(0, 0, null, null, TimeSpan.Zero)),
            diagnostics,
            activityCounter
        );

        var requestedRange = Intervals.NET.Factories.Range.Closed<int>(0, 10);
        var data = DataGenerationHelpers.GenerateDataForRange(requestedRange);
        var rangeData = data.ToRangeData(requestedRange, domain);
        var intent = new Intent<int, int, IntegerFixedStepDomain>(requestedRange, rangeData);

        var currentTaskField = typeof(TaskBasedRebalanceExecutionController<int, int, IntegerFixedStepDomain>)
            .GetField("_currentExecutionTask", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(currentTaskField);

        currentTaskField!.SetValue(controller, Task.FromException(new InvalidOperationException("Previous task failed")));

        // ACT
        await controller.PublishExecutionRequest(intent, requestedRange, null, CancellationToken.None);

        var chainedTask = (Task)currentTaskField.GetValue(controller)!;
        await chainedTask;

        // ASSERT
        Assert.True(diagnostics.RebalanceExecutionFailed >= 1,
            "Expected previous task failure to be recorded and current execution to continue.");
        Assert.True(diagnostics.RebalanceExecutionStarted >= 1);
    }
}
