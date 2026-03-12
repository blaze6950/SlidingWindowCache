using System.Reflection;
using Intervals.NET.Caching.Infrastructure.Concurrency;
using Intervals.NET.Caching.Infrastructure.Scheduling.Serial;
using Intervals.NET.Data.Extensions;
using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Caching.SlidingWindow.Core.Rebalance.Execution;
using Intervals.NET.Caching.SlidingWindow.Core.Rebalance.Intent;
using Intervals.NET.Caching.SlidingWindow.Core.State;
using Intervals.NET.Caching.SlidingWindow.Infrastructure.Storage;
using Intervals.NET.Caching.SlidingWindow.Infrastructure.Adapters;
using Intervals.NET.Caching.SlidingWindow.Public.Instrumentation;
using Intervals.NET.Caching.SlidingWindow.Tests.Infrastructure.DataSources;

namespace Intervals.NET.Caching.SlidingWindow.Unit.Tests.Infrastructure.Concurrency;

/// <summary>
/// Unit tests for UnboundedSerialWorkScheduler used as a rebalance execution scheduler.
/// Validates chain resilience when previous task is faulted.
/// </summary>
public sealed class TaskBasedRebalanceExecutionControllerTests
{
    [Fact]
    public async Task PublishWorkItemAsync_ContinuesAfterFaultedPreviousTask()
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
        var schedulerDiagnostics = new SlidingWindowWorkSchedulerDiagnostics(diagnostics);

        Func<ExecutionRequest<int, int, IntegerFixedStepDomain>, CancellationToken, Task> executorDelegate =
            (request, ct) => executor.ExecuteAsync(
                request.Intent,
                request.DesiredRange,
                request.DesiredNoRebalanceRange,
                ct);

        var scheduler = new UnboundedSerialWorkScheduler<ExecutionRequest<int, int, IntegerFixedStepDomain>>(
            executorDelegate,
            () => TimeSpan.Zero,
            schedulerDiagnostics,
            activityCounter
        );

        var requestedRange = Factories.Range.Closed<int>(0, 10);
        var data = DataGenerationHelpers.GenerateDataForRange(requestedRange);
        var rangeData = data.ToRangeData(requestedRange, domain);
        var intent = new Intent<int, int, IntegerFixedStepDomain>(requestedRange, rangeData);

        var currentTaskField = typeof(UnboundedSerialWorkScheduler<ExecutionRequest<int, int, IntegerFixedStepDomain>>)
            .GetField("_currentExecutionTask", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(currentTaskField);

        currentTaskField!.SetValue(scheduler, Task.FromException(new InvalidOperationException("Previous task failed")));

        // ACT
        var request = new ExecutionRequest<int, int, IntegerFixedStepDomain>(
            intent,
            requestedRange,
            null,
            new CancellationTokenSource()
        );

        // Increment activity counter as IntentController would before calling PublishWorkItemAsync
        activityCounter.IncrementActivity();

        await scheduler.PublishWorkItemAsync(request, CancellationToken.None);

        var chainedTask = (Task)currentTaskField.GetValue(scheduler)!;
        await chainedTask;

        // ASSERT
        Assert.True(diagnostics.BackgroundOperationFailed >= 1,
            "Expected previous task failure to be recorded and current execution to continue.");
        Assert.True(diagnostics.RebalanceExecutionStarted >= 1);
    }
}
