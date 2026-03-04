using Intervals.NET.Caching.Core.State;

namespace Intervals.NET.Caching.Unit.Tests.Core.State;

/// <summary>
/// Unit tests for <see cref="RuntimeCacheOptionsHolder"/> verifying atomic read/write semantics.
/// </summary>
public class RuntimeCacheOptionsHolderTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_WithInitialOptions_CurrentReturnsInitialSnapshot()
    {
        // ARRANGE
        var initial = new RuntimeCacheOptions(1.0, 2.0, 0.1, 0.2, TimeSpan.FromMilliseconds(50));

        // ACT
        var holder = new RuntimeCacheOptionsHolder(initial);

        // ASSERT
        Assert.Same(initial, holder.Current);
    }

    #endregion

    #region Update Tests

    [Fact]
    public void Update_WithNewOptions_CurrentReturnsNewSnapshot()
    {
        // ARRANGE
        var initial = new RuntimeCacheOptions(1.0, 1.0, null, null, TimeSpan.Zero);
        var updated = new RuntimeCacheOptions(3.0, 4.0, 0.1, 0.2, TimeSpan.FromMilliseconds(100));
        var holder = new RuntimeCacheOptionsHolder(initial);

        // ACT
        holder.Update(updated);

        // ASSERT
        Assert.Same(updated, holder.Current);
    }

    [Fact]
    public void Update_MultipleTimes_CurrentReturnsLatestSnapshot()
    {
        // ARRANGE
        var initial = new RuntimeCacheOptions(1.0, 1.0, null, null, TimeSpan.Zero);
        var second = new RuntimeCacheOptions(2.0, 2.0, null, null, TimeSpan.Zero);
        var third = new RuntimeCacheOptions(3.0, 3.0, null, null, TimeSpan.Zero);
        var holder = new RuntimeCacheOptionsHolder(initial);

        // ACT
        holder.Update(second);
        holder.Update(third);

        // ASSERT
        Assert.Same(third, holder.Current);
        Assert.Equal(3.0, holder.Current.LeftCacheSize);
    }

    [Fact]
    public void Update_DoesNotMutateInitialSnapshot()
    {
        // ARRANGE
        var initial = new RuntimeCacheOptions(1.0, 1.0, null, null, TimeSpan.Zero);
        var holder = new RuntimeCacheOptionsHolder(initial);

        // ACT
        holder.Update(new RuntimeCacheOptions(5.0, 5.0, null, null, TimeSpan.Zero));

        // ASSERT — initial object is unchanged
        Assert.Equal(1.0, initial.LeftCacheSize);
    }

    #endregion

    #region Concurrent Access Tests

    [Fact]
    public async Task Update_ConcurrentWrites_CurrentIsOneOfThePublishedSnapshots()
    {
        // ARRANGE
        var initial = new RuntimeCacheOptions(0.0, 0.0, null, null, TimeSpan.Zero);
        var holder = new RuntimeCacheOptionsHolder(initial);
        var snapshots = new RuntimeCacheOptions[10];
        for (var i = 0; i < snapshots.Length; i++)
        {
            snapshots[i] = new RuntimeCacheOptions(i, i, null, null, TimeSpan.Zero);
        }

        // ACT — ten concurrent writers
        await Task.WhenAll(snapshots.Select(s => Task.Run(() => holder.Update(s))));

        // ASSERT — current must be one of the published snapshots (last-writer-wins)
        var current = holder.Current;
        Assert.Contains(current, snapshots);
    }

    [Fact]
    public async Task Current_ConcurrentReadsWhileWriting_NeverReturnsNull()
    {
        // ARRANGE
        var holder = new RuntimeCacheOptionsHolder(
            new RuntimeCacheOptions(1.0, 1.0, null, null, TimeSpan.Zero));

        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var readResults = new System.Collections.Concurrent.ConcurrentBag<RuntimeCacheOptions>();

        // ACT — concurrent reads and writes
        var readerTask = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                readResults.Add(holder.Current);
                await Task.Yield();
            }
        });

        var writerTask = Task.Run(async () =>
        {
            var i = 0;
            while (!cts.Token.IsCancellationRequested)
            {
                holder.Update(new RuntimeCacheOptions(i % 10, i % 10, null, null, TimeSpan.Zero));
                i++;
                await Task.Yield();
            }
        });

        await Task.WhenAll(readerTask, writerTask);

        // ASSERT — reader never observed null
        Assert.All(readResults, r => Assert.NotNull(r));
    }

    #endregion
}
