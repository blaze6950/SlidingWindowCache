using Intervals.NET.Caching.Infrastructure.Concurrency;

namespace Intervals.NET.Caching.Unit.Tests.Infrastructure.Concurrency;

/// <summary>
/// Unit tests for AsyncActivityCounter.
/// Validates underflow protection and idle detection semantics.
/// </summary>
public sealed class AsyncActivityCounterTests
{
    [Fact]
    public void DecrementActivity_WithoutMatchingIncrement_ThrowsInvalidOperationException()
    {
        // ARRANGE
        var counter = new AsyncActivityCounter();

        // ACT
        var exception = Record.Exception(() => counter.DecrementActivity());

        // ASSERT
        Assert.NotNull(exception);
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("decremented below zero", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DecrementActivity_AfterUnderflow_AllowsSubsequentBalancedActivity()
    {
        // ARRANGE
        var counter = new AsyncActivityCounter();

        // ACT - Force underflow
        _ = Record.Exception(() => counter.DecrementActivity());

        // ASSERT - Subsequent balanced activity works
        var exception = Record.Exception(() =>
        {
            counter.IncrementActivity();
            counter.DecrementActivity();
        });

        Assert.Null(exception);
    }

    [Fact]
    public async Task IncrementAndDecrement_Balanced_CompletesSuccessfully()
    {
        // ARRANGE
        var counter = new AsyncActivityCounter();

        // ACT
        counter.IncrementActivity();
        counter.DecrementActivity();
        var exception = await Record.ExceptionAsync(async () => await counter.WaitForIdleAsync());

        // ASSERT
        Assert.Null(exception);
    }

    [Fact]
    public async Task WaitForIdleAsync_CompletesImmediately_WhenNoActivity()
    {
        // ARRANGE
        var counter = new AsyncActivityCounter();

        // ACT
        var exception = await Record.ExceptionAsync(async () => await counter.WaitForIdleAsync());

        // ASSERT
        Assert.Null(exception);
    }

    [Fact]
    public async Task WaitForIdleAsync_CompletesAfterLastDecrement()
    {
        // ARRANGE
        var counter = new AsyncActivityCounter();

        // ACT
        counter.IncrementActivity();
        counter.IncrementActivity();
        var waitTask = counter.WaitForIdleAsync();

        // ASSERT - Should still be waiting with one active operation
        counter.DecrementActivity();
        Assert.False(waitTask.IsCompleted);

        counter.DecrementActivity();
        await waitTask;
    }
}
