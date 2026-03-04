using Intervals.NET.Data.Extensions;
using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Caching.Infrastructure.Storage;
using Intervals.NET.Caching.Unit.Tests.Infrastructure.Extensions;
using Intervals.NET.Caching.Unit.Tests.Infrastructure.Storage.TestInfrastructure;
using static Intervals.NET.Caching.Unit.Tests.Infrastructure.Storage.TestInfrastructure.StorageTestHelpers;

namespace Intervals.NET.Caching.Unit.Tests.Infrastructure.Storage;

/// <summary>
/// Unit tests for CopyOnReadStorage that verify the ICacheStorage interface contract,
/// data correctness (Invariant B.1), dual-buffer staging pattern, and error handling.
/// Shared tests are inherited from <see cref="CacheStorageTestsBase"/>.
/// </summary>
public class CopyOnReadStorageTests : CacheStorageTestsBase
{
    protected override object CreateStorageObject(IntegerFixedStepDomain domain) =>
        new CopyOnReadStorage<int, int, IntegerFixedStepDomain>(domain);

    protected override object CreateVariableStepStorageObject(IntegerVariableStepDomain domain) =>
        new CopyOnReadStorage<int, int, IntegerVariableStepDomain>(domain);

    #region Rematerialize Tests (CopyOnRead-specific)

    [Fact]
    public void Rematerialize_SequentialCalls_MaintainsCorrectness()
    {
        // ARRANGE - Test dual-buffer staging pattern with sequential rematerializations
        var domain = CreateFixedStepDomain();
        var storage = new CopyOnReadStorage<int, int, IntegerFixedStepDomain>(domain);

        // ACT & ASSERT - Each rematerialization should work correctly
        for (var i = 0; i < 5; i++)
        {
            var start = i * 10;
            var end = start + 10;
            storage.Rematerialize(CreateRangeData(start, end, domain));

            var result = storage.Read(CreateRange(start, end));
            VerifyDataMatchesRange(result, start, end);
        }
    }

    #endregion

    #region Dual-Buffer Staging Pattern Tests

    [Fact]
    public void StagingPattern_RematerializeWithDerivedData_WorksCorrectly()
    {
        // ARRANGE - Test scenario where rangeData.Data might be based on current storage
        var domain = CreateFixedStepDomain();
        var storage = new CopyOnReadStorage<int, int, IntegerFixedStepDomain>(domain);
        storage.Rematerialize(CreateRangeData(0, 10, domain));

        // ACT - Simulate expansion scenario: get current data and extend it
        var currentData = storage.ToRangeData();
        var extendedData = currentData.Data.Concat(Enumerable.Range(11, 10)).ToArray();
        var extendedRange = CreateRange(0, 20);
        var extendedRangeData = extendedData.ToRangeData(extendedRange, domain);

        storage.Rematerialize(extendedRangeData);

        // ASSERT - Data should be correct despite being derived from current storage
        var result = storage.Read(CreateRange(0, 20));
        VerifyDataMatchesRange(result, 0, 20);
    }

    [Fact]
    public void StagingPattern_MultipleQuickRematerializations_MaintainsCorrectness()
    {
        // ARRANGE - Stress test the dual-buffer pattern
        var domain = CreateFixedStepDomain();
        var storage = new CopyOnReadStorage<int, int, IntegerFixedStepDomain>(domain);

        // ACT - Rapid sequential rematerializations (buffer swapping)
        for (var i = 0; i < 10; i++)
        {
            var start = i * 5;
            var end = start + 5;
            storage.Rematerialize(CreateRangeData(start, end, domain));
        }

        // ASSERT - Final state should be correct
        var result = storage.Read(CreateRange(45, 50));
        VerifyDataMatchesRange(result, 45, 50);
    }

    #endregion

    #region Thread Safety Tests

    [Fact]
    public async Task ThreadSafety_ConcurrentReadAndRematerialize_NeverCorruptsData()
    {
        // ARRANGE - Verify that concurrent Read() and Rematerialize() calls never produce
        // corrupted or inconsistent data. This directly tests the invariant enforced by _lock:
        // a Read() must never observe _activeStorage mid-swap (new list reference but stale Range).
        var domain = CreateFixedStepDomain();
        var storage = new CopyOnReadStorage<int, int, IntegerFixedStepDomain>(domain);
        storage.Rematerialize(CreateRangeData(0, 9, domain));

        const int iterations = 2_000;
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var cts = new CancellationTokenSource();

        // Writer task: continuously rematerializes with alternating ranges
        var writer = Task.Run(() =>
        {
            for (var i = 0; i < iterations && !cts.Token.IsCancellationRequested; i++)
            {
                // Alternate between two distinct ranges so a corrupted swap would be detectable
                var start = (i % 2 == 0) ? 0 : 100;
                var end = start + 9;
                storage.Rematerialize(CreateRangeData(start, end, domain));
            }
        });

        // Reader task: continuously reads and verifies data consistency
        var reader = Task.Run(() =>
        {
            for (var i = 0; i < iterations && !cts.Token.IsCancellationRequested; i++)
            {
                try
                {
                    // Read whatever range is currently active — both legal values are [0,9] and [100,109]
                    var currentRange = storage.Range;
                    if (currentRange.Start.Value == 0 || currentRange.Start.Value == 100)
                    {
                        var data = storage.Read(currentRange);
                        // Verify data is internally consistent: each element equals its range position
                        var expectedStart = currentRange.Start.Value;
                        for (var j = 0; j < data.Length; j++)
                        {
                            if (data.Span[j] != expectedStart + j)
                            {
                                throw new InvalidOperationException(
                                    $"Data corruption at index {j}: expected {expectedStart + j}, got {data.Span[j]}. Range={currentRange}");
                            }
                        }
                    }
                }
                catch (ArgumentOutOfRangeException)
                {
                    // Acceptable: Range and _activeStorage are updated under the lock together,
                    // but we read Range before acquiring the lock in the reader loop above.
                    // A stale Range read that no longer matches _activeStorage will throw here.
                    // This is a benign TOCTOU on the Range property itself (which is not locked
                    // outside of Rematerialize), not a data corruption — ignore it.
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                    cts.Cancel();
                }
            }
        });

        await Task.WhenAll(writer, reader);

        // ASSERT - No data corruption detected
        Assert.Empty(exceptions);
    }

    [Fact]
    public async Task ThreadSafety_ConcurrentRematerializeWithDerivedData_NeverCorrupts()
    {
        // ARRANGE - Verify that the staging buffer + lock pattern prevents corruption when
        // rangeData.Data is a LINQ chain over _activeStorage (the expansion scenario).
        // This is the primary correctness scenario for the dual-buffer design.
        var domain = CreateFixedStepDomain();
        var storage = new CopyOnReadStorage<int, int, IntegerFixedStepDomain>(domain);
        storage.Rematerialize(CreateRangeData(0, 9, domain));

        const int iterations = 500;
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        // Writer task: repeatedly expands by deriving new data from current active storage
        var writer = Task.Run(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                var currentData = storage.ToRangeData();
                // Build new data as a LINQ chain over current active storage (tied to _activeStorage)
                var newData = currentData.Data.Concat(Enumerable.Range(0, 5)).ToArray();
                var newRange = CreateRange(0, newData.Length - 1);
                storage.Rematerialize(newData.ToRangeData(newRange, domain));

                // Reset to small range to keep the test bounded
                storage.Rematerialize(CreateRangeData(0, 9, domain));
            }
        });

        // Reader task: reads while writer is expanding
        var reader = Task.Run(() =>
        {
            for (var i = 0; i < iterations * 4; i++)
            {
                try
                {
                    var data = storage.Read(storage.Range);
                    Assert.True(data.Length > 0);
                }
                catch (ArgumentOutOfRangeException)
                {
                    // Benign TOCTOU on Range property read (see above) — not a data corruption
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }
        });

        await Task.WhenAll(writer, reader);

        // ASSERT - No data corruption detected
        Assert.Empty(exceptions);
    }

    #endregion
}
