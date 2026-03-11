using Intervals.NET.Caching.Infrastructure.Concurrency;
using Intervals.NET.Caching.Infrastructure.Scheduling;

namespace Intervals.NET.Caching.VisitedPlaces.Unit.Tests.Infrastructure;

/// <summary>
/// Unit tests for <see cref="ConcurrentWorkScheduler{TWorkItem}"/>.
/// Verifies that each published work item executes independently and concurrently,
/// the activity counter lifecycle is correct, and disposal is handled safely.
/// </summary>
public sealed class ConcurrentWorkSchedulerTests
{
    #region PublishWorkItemAsync — Basic Execution

    [Fact]
    public async Task PublishWorkItemAsync_SingleItem_ExecutesItem()
    {
        // ARRANGE
        var executed = new TaskCompletionSource();
        var activityCounter = new AsyncActivityCounter();
        await using var scheduler = new ConcurrentWorkScheduler<TestWorkItem>(
            executor: (item, ct) => { executed.TrySetResult(); return Task.CompletedTask; },
            debounceProvider: static () => TimeSpan.Zero,
            diagnostics: NoOpWorkSchedulerDiagnostics.Instance,
            activityCounter: activityCounter);

        var workItem = new TestWorkItem();

        // ACT
        await scheduler.PublishWorkItemAsync(workItem, CancellationToken.None);

        // ASSERT — item eventually executes
        await executed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task PublishWorkItemAsync_MultipleItems_AllExecuteConcurrently()
    {
        // ARRANGE — items with 100ms delay; if serialized total would be >= 300ms
        const int itemCount = 3;
        var completions = new TaskCompletionSource[itemCount];
        for (var i = 0; i < itemCount; i++)
        {
            completions[i] = new TaskCompletionSource();
        }

        var idx = 0;
        var activityCounter = new AsyncActivityCounter();
        await using var scheduler = new ConcurrentWorkScheduler<TestWorkItem>(
            executor: async (item, ct) =>
            {
                var myIdx = Interlocked.Increment(ref idx) - 1;
                await Task.Delay(100, ct).ConfigureAwait(false);
                completions[myIdx].TrySetResult();
            },
            debounceProvider: static () => TimeSpan.Zero,
            diagnostics: NoOpWorkSchedulerDiagnostics.Instance,
            activityCounter: activityCounter);

        // ACT
        var before = DateTimeOffset.UtcNow;
        for (var i = 0; i < itemCount; i++)
        {
            await scheduler.PublishWorkItemAsync(new TestWorkItem(), CancellationToken.None);
        }

        await Task.WhenAll(completions.Select(c => c.Task))
            .WaitAsync(TimeSpan.FromSeconds(5));

        var elapsed = DateTimeOffset.UtcNow - before;

        // ASSERT — all completed concurrently; should be well under 300ms if parallel
        Assert.True(elapsed < TimeSpan.FromMilliseconds(280),
            $"Items appear to be serialized (elapsed={elapsed.TotalMilliseconds:F0}ms)");
    }

    #endregion

    #region PublishWorkItemAsync — Activity Counter

    [Fact]
    public async Task PublishWorkItemAsync_ActivityCounterIncrementedThenDecremented()
    {
        // ARRANGE
        var releaseGate = new TaskCompletionSource();
        var activityCounter = new AsyncActivityCounter();
        await using var scheduler = new ConcurrentWorkScheduler<TestWorkItem>(
            executor: async (item, ct) => await releaseGate.Task.ConfigureAwait(false),
            debounceProvider: static () => TimeSpan.Zero,
            diagnostics: NoOpWorkSchedulerDiagnostics.Instance,
            activityCounter: activityCounter);

        // ACT — publish item; while item holds gate, idle should not complete
        await scheduler.PublishWorkItemAsync(new TestWorkItem(), CancellationToken.None);

        var idleBeforeRelease = activityCounter.WaitForIdleAsync();
        Assert.False(idleBeforeRelease.IsCompleted, "Should not be idle while item is executing");

        // Release the gate so the item completes
        releaseGate.TrySetResult();

        // Now idle should complete
        await idleBeforeRelease.WaitAsync(TimeSpan.FromSeconds(5));
    }

    #endregion

    #region PublishWorkItemAsync — Disposal Guard

    [Fact]
    public async Task PublishWorkItemAsync_AfterDisposal_ThrowsObjectDisposedException()
    {
        // ARRANGE
        var activityCounter = new AsyncActivityCounter();
        var scheduler = new ConcurrentWorkScheduler<TestWorkItem>(
            executor: (item, ct) => Task.CompletedTask,
            debounceProvider: static () => TimeSpan.Zero,
            diagnostics: NoOpWorkSchedulerDiagnostics.Instance,
            activityCounter: activityCounter);

        await scheduler.DisposeAsync();

        // ACT
        var ex = await Record.ExceptionAsync(() =>
            scheduler.PublishWorkItemAsync(new TestWorkItem(), CancellationToken.None).AsTask());

        // ASSERT
        Assert.NotNull(ex);
        Assert.IsType<ObjectDisposedException>(ex);
    }

    #endregion

    #region Disposal

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        // ARRANGE
        var activityCounter = new AsyncActivityCounter();
        var scheduler = new ConcurrentWorkScheduler<TestWorkItem>(
            executor: (item, ct) => Task.CompletedTask,
            debounceProvider: static () => TimeSpan.Zero,
            diagnostics: NoOpWorkSchedulerDiagnostics.Instance,
            activityCounter: activityCounter);

        // ACT — dispose twice: should not throw
        var ex = await Record.ExceptionAsync(async () =>
        {
            await scheduler.DisposeAsync();
            await scheduler.DisposeAsync();
        });

        // ASSERT
        Assert.Null(ex);
    }

    #endregion

    #region Test Doubles

    private sealed class TestWorkItem : ISchedulableWorkItem
    {
        private readonly CancellationTokenSource _cts = new();

        public CancellationToken CancellationToken => _cts.Token;

        public void Cancel()
        {
            try { _cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        public void Dispose() => _cts.Dispose();
    }

    #endregion
}
