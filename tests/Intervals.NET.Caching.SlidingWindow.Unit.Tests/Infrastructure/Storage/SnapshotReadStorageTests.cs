using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Caching.SlidingWindow.Infrastructure.Storage;
using Intervals.NET.Caching.SlidingWindow.Unit.Tests.Infrastructure.Extensions;
using Intervals.NET.Caching.SlidingWindow.Unit.Tests.Infrastructure.Storage.TestInfrastructure;

namespace Intervals.NET.Caching.SlidingWindow.Unit.Tests.Infrastructure.Storage;

/// <summary>
/// Unit tests for SnapshotReadStorage that verify the ICacheStorage interface contract,
/// data correctness (Invariant SWC.B.1), and error handling.
/// Shared tests are inherited from <see cref="CacheStorageTestsBase"/>.
/// </summary>
public class SnapshotReadStorageTests : CacheStorageTestsBase
{
    protected override object CreateStorageObject(IntegerFixedStepDomain domain) =>
        new SnapshotReadStorage<int, int, IntegerFixedStepDomain>(domain);

    protected override object CreateVariableStepStorageObject(IntegerVariableStepDomain domain) =>
        new SnapshotReadStorage<int, int, IntegerVariableStepDomain>(domain);
}
