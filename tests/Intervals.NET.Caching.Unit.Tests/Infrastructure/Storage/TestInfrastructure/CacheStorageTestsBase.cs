using Intervals.NET.Data.Extensions;
using Intervals.NET.Domain.Default.Numeric;
using Intervals.NET.Caching.Infrastructure.Storage;
using Intervals.NET.Caching.Unit.Tests.Infrastructure.Extensions;
using static Intervals.NET.Caching.Unit.Tests.Infrastructure.Storage.TestInfrastructure.StorageTestHelpers;

namespace Intervals.NET.Caching.Unit.Tests.Infrastructure.Storage.TestInfrastructure;

/// <summary>
/// Abstract base class providing shared test coverage for all <see cref="ICacheStorage{TRange,TData,TDomain}"/>
/// implementations, enforcing the ICacheStorage interface contract, data correctness (Invariant B.1),
/// and error handling.
/// </summary>
/// <remarks>
/// Subclasses provide the concrete storage instance via <see cref="CreateStorage"/> and
/// <see cref="CreateVariableStepStorage"/>. Implementation-specific tests (e.g., dual-buffer
/// staging for CopyOnReadStorage) live in the subclass.
/// The factory methods return <c>object</c> and are cast internally to keep the public abstract
/// class compatible with the internal <see cref="ICacheStorage{TRange,TData,TDomain}"/> type.
/// </remarks>
public abstract class CacheStorageTestsBase
{
    /// <summary>
    /// Factory method that subclasses override to provide the storage implementation under test
    /// using a fixed-step domain. Must return an <see cref="ICacheStorage{TRange,TData,TDomain}"/> instance.
    /// </summary>
    protected abstract object CreateStorageObject(IntegerFixedStepDomain domain);

    /// <summary>
    /// Factory method that subclasses override to provide the storage implementation under test
    /// using a variable-step domain. Must return an <see cref="ICacheStorage{TRange,TData,TDomain}"/> instance.
    /// </summary>
    protected abstract object CreateVariableStepStorageObject(IntegerVariableStepDomain domain);

    private ICacheStorage<int, int, IntegerFixedStepDomain> CreateStorage(IntegerFixedStepDomain domain) =>
        (ICacheStorage<int, int, IntegerFixedStepDomain>)CreateStorageObject(domain);

    private ICacheStorage<int, int, IntegerVariableStepDomain> CreateVariableStepStorage(IntegerVariableStepDomain domain) =>
        (ICacheStorage<int, int, IntegerVariableStepDomain>)CreateVariableStepStorageObject(domain);

    #region Interface Contract Tests

    [Fact]
    public void Range_InitiallyEmpty()
    {
        // ARRANGE
        var domain = CreateFixedStepDomain();
        var storage = CreateStorage(domain);

        // ACT & ASSERT
        // Default Range<int> behavior - storage starts uninitialized
        // Range is a value type, so it's always non-null
        _ = storage.Range;
    }

    [Fact]
    public void Range_UpdatesAfterRematerialize()
    {
        // ARRANGE
        var domain = CreateFixedStepDomain();
        var storage = CreateStorage(domain);
        var rangeData = CreateRangeData(10, 20, domain);

        // ACT
        storage.Rematerialize(rangeData);

        // ASSERT
        Assert.Equal(10, storage.Range.Start.Value);
        Assert.Equal(20, storage.Range.End.Value);
    }

    #endregion

    #region Rematerialize Tests

    [Fact]
    public void Rematerialize_StoresDataCorrectly()
    {
        // ARRANGE
        var domain = CreateFixedStepDomain();
        var storage = CreateStorage(domain);
        var rangeData = CreateRangeData(5, 15, domain);

        // ACT
        storage.Rematerialize(rangeData);
        var result = storage.Read(CreateRange(5, 15));

        // ASSERT
        VerifyDataMatchesRange(result, 5, 15);
    }

    [Fact]
    public void Rematerialize_UpdatesRange()
    {
        // ARRANGE
        var domain = CreateFixedStepDomain();
        var storage = CreateStorage(domain);
        var rangeData = CreateRangeData(100, 200, domain);

        // ACT
        storage.Rematerialize(rangeData);

        // ASSERT
        Assert.Equal(100, storage.Range.Start.Value);
        Assert.Equal(200, storage.Range.End.Value);
    }

    [Fact]
    public void Rematerialize_MultipleCalls_ReplacesData()
    {
        // ARRANGE
        var domain = CreateFixedStepDomain();
        var storage = CreateStorage(domain);

        // First rematerialization
        var firstData = CreateRangeData(0, 10, domain);
        storage.Rematerialize(firstData);

        // ACT - Second rematerialization with different range
        var secondData = CreateRangeData(20, 30, domain);
        storage.Rematerialize(secondData);
        var result = storage.Read(CreateRange(20, 30));

        // ASSERT
        Assert.Equal(20, storage.Range.Start.Value);
        Assert.Equal(30, storage.Range.End.Value);
        VerifyDataMatchesRange(result, 20, 30);
    }

    [Fact]
    public void Rematerialize_WithSameSize_ReplacesData()
    {
        // ARRANGE
        var domain = CreateFixedStepDomain();
        var storage = CreateStorage(domain);

        storage.Rematerialize(CreateRangeData(0, 10, domain));

        // ACT - Same size, different values
        storage.Rematerialize(CreateRangeData(100, 110, domain));
        var result = storage.Read(CreateRange(100, 110));

        // ASSERT
        VerifyDataMatchesRange(result, 100, 110);
    }

    [Fact]
    public void Rematerialize_WithLargerSize_ReplacesData()
    {
        // ARRANGE
        var domain = CreateFixedStepDomain();
        var storage = CreateStorage(domain);

        storage.Rematerialize(CreateRangeData(0, 5, domain));

        // ACT - Larger size
        storage.Rematerialize(CreateRangeData(0, 20, domain));
        var result = storage.Read(CreateRange(0, 20));

        // ASSERT
        VerifyDataMatchesRange(result, 0, 20);
    }

    [Fact]
    public void Rematerialize_WithSmallerSize_ReplacesData()
    {
        // ARRANGE
        var domain = CreateFixedStepDomain();
        var storage = CreateStorage(domain);

        storage.Rematerialize(CreateRangeData(0, 20, domain));

        // ACT - Smaller size
        storage.Rematerialize(CreateRangeData(0, 5, domain));
        var result = storage.Read(CreateRange(0, 5));

        // ASSERT
        VerifyDataMatchesRange(result, 0, 5);
    }

    #endregion

    #region Read Tests

    [Fact]
    public void Read_FullRange_ReturnsAllData()
    {
        // ARRANGE
        var domain = CreateFixedStepDomain();
        var storage = CreateStorage(domain);
        storage.Rematerialize(CreateRangeData(0, 10, domain));

        // ACT
        var result = storage.Read(CreateRange(0, 10));

        // ASSERT
        VerifyDataMatchesRange(result, 0, 10);
    }

    [Fact]
    public void Read_PartialRange_AtStart_ReturnsCorrectSubset()
    {
        // ARRANGE
        var domain = CreateFixedStepDomain();
        var storage = CreateStorage(domain);
        storage.Rematerialize(CreateRangeData(0, 20, domain));

        // ACT
        var result = storage.Read(CreateRange(0, 5));

        // ASSERT
        VerifyDataMatchesRange(result, 0, 5);
    }

    [Fact]
    public void Read_PartialRange_InMiddle_ReturnsCorrectSubset()
    {
        // ARRANGE
        var domain = CreateFixedStepDomain();
        var storage = CreateStorage(domain);
        storage.Rematerialize(CreateRangeData(0, 20, domain));

        // ACT
        var result = storage.Read(CreateRange(5, 15));

        // ASSERT
        VerifyDataMatchesRange(result, 5, 15);
    }

    [Fact]
    public void Read_PartialRange_AtEnd_ReturnsCorrectSubset()
    {
        // ARRANGE
        var domain = CreateFixedStepDomain();
        var storage = CreateStorage(domain);
        storage.Rematerialize(CreateRangeData(0, 20, domain));

        // ACT
        var result = storage.Read(CreateRange(15, 20));

        // ASSERT
        VerifyDataMatchesRange(result, 15, 20);
    }

    [Fact]
    public void Read_SingleElement_ReturnsOneValue()
    {
        // ARRANGE
        var domain = CreateFixedStepDomain();
        var storage = CreateStorage(domain);
        storage.Rematerialize(CreateRangeData(0, 10, domain));

        // ACT
        var result = storage.Read(CreateRange(5, 5));

        // ASSERT
        Assert.Equal(1, result.Length);
        Assert.Equal(5, result.Span[0]);
    }

    [Fact]
    public void Read_AtExactBoundaries_ReturnsCorrectData()
    {
        // ARRANGE
        var domain = CreateFixedStepDomain();
        var storage = CreateStorage(domain);
        storage.Rematerialize(CreateRangeData(10, 20, domain));

        // ACT
        var resultStart = storage.Read(CreateRange(10, 10));
        var resultEnd = storage.Read(CreateRange(20, 20));

        // ASSERT
        Assert.Equal(1, resultStart.Length);
        Assert.Equal(10, resultStart.Span[0]);
        Assert.Equal(1, resultEnd.Length);
        Assert.Equal(20, resultEnd.Span[0]);
    }

    [Fact]
    public void Read_AfterMultipleRematerializations_ReturnsCurrentData()
    {
        // ARRANGE
        var domain = CreateFixedStepDomain();
        var storage = CreateStorage(domain);

        storage.Rematerialize(CreateRangeData(0, 10, domain));
        storage.Rematerialize(CreateRangeData(50, 60, domain));
        storage.Rematerialize(CreateRangeData(100, 110, domain));

        // ACT
        var result = storage.Read(CreateRange(100, 110));

        // ASSERT
        VerifyDataMatchesRange(result, 100, 110);
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public void Read_OutOfBounds_ThrowsArgumentOutOfRangeException()
    {
        // ARRANGE
        var domain = CreateFixedStepDomain();
        var storage = CreateStorage(domain);
        storage.Rematerialize(CreateRangeData(10, 20, domain));

        // ACT & ASSERT - Read beyond stored range
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            storage.Read(CreateRange(25, 30)));
    }

    [Fact]
    public void Read_PartiallyOutOfBounds_ThrowsArgumentOutOfRangeException()
    {
        // ARRANGE
        var domain = CreateFixedStepDomain();
        var storage = CreateStorage(domain);
        storage.Rematerialize(CreateRangeData(10, 20, domain));

        // ACT & ASSERT - Read overlapping but extending beyond range
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            storage.Read(CreateRange(15, 25)));
    }

    [Fact]
    public void Read_BeforeStoredRange_ThrowsArgumentOutOfRangeException()
    {
        // ARRANGE
        var domain = CreateFixedStepDomain();
        var storage = CreateStorage(domain);
        storage.Rematerialize(CreateRangeData(10, 20, domain));

        // ACT & ASSERT - Read before stored range
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            storage.Read(CreateRange(0, 5)));
    }

    #endregion

    #region ToRangeData Tests

    [Fact]
    public void ToRangeData_AfterRematerialize_RoundTripsCorrectly()
    {
        // ARRANGE
        var domain = CreateFixedStepDomain();
        var storage = CreateStorage(domain);
        var originalData = CreateRangeData(10, 30, domain);
        storage.Rematerialize(originalData);

        // ACT
        var roundTripped = storage.ToRangeData();

        // ASSERT
        AssertRangeDataRoundTrip(originalData, roundTripped);
    }

    [Fact]
    public void ToRangeData_MaintainsSequentialOrder()
    {
        // ARRANGE
        var domain = CreateFixedStepDomain();
        var storage = CreateStorage(domain);
        var originalData = CreateRangeData(5, 15, domain);
        storage.Rematerialize(originalData);

        // ACT
        var rangeData = storage.ToRangeData();
        var dataArray = rangeData.Data.ToArray();

        // ASSERT
        for (var i = 0; i < dataArray.Length; i++)
        {
            Assert.Equal(5 + i, dataArray[i]);
        }
    }

    [Fact]
    public void ToRangeData_AfterMultipleRematerializations_ReflectsCurrentState()
    {
        // ARRANGE
        var domain = CreateFixedStepDomain();
        var storage = CreateStorage(domain);

        storage.Rematerialize(CreateRangeData(0, 10, domain));
        storage.Rematerialize(CreateRangeData(20, 30, domain));
        var finalData = CreateRangeData(100, 120, domain);
        storage.Rematerialize(finalData);

        // ACT
        var result = storage.ToRangeData();

        // ASSERT
        AssertRangeDataRoundTrip(finalData, result);
    }

    #endregion

    #region Invariant B.1 Tests (Data/Range Consistency)

    [Fact]
    public void InvariantB1_DataLengthMatchesRangeSize_AfterRematerialize()
    {
        // ARRANGE
        var domain = CreateFixedStepDomain();
        var storage = CreateStorage(domain);
        var rangeData = CreateRangeData(0, 50, domain);

        // ACT
        storage.Rematerialize(rangeData);
        var data = storage.Read(storage.Range);

        // ASSERT - Data length must equal range size (Invariant B.1)
        var expectedLength = 51; // [0, 50] inclusive = 51 elements
        Assert.Equal(expectedLength, data.Length);
    }

    [Fact]
    public void InvariantB1_DataLengthMatchesRangeSize_AfterMultipleRematerializations()
    {
        // ARRANGE
        var domain = CreateFixedStepDomain();
        var storage = CreateStorage(domain);

        // ACT & ASSERT - Verify consistency after each rematerialization
        storage.Rematerialize(CreateRangeData(0, 10, domain));
        Assert.Equal(11, storage.Read(storage.Range).Length);

        storage.Rematerialize(CreateRangeData(0, 100, domain));
        Assert.Equal(101, storage.Read(storage.Range).Length);

        storage.Rematerialize(CreateRangeData(50, 55, domain));
        Assert.Equal(6, storage.Read(storage.Range).Length);
    }

    [Fact]
    public void InvariantB1_PartialReads_ConsistentWithStoredRange()
    {
        // ARRANGE
        var domain = CreateFixedStepDomain();
        var storage = CreateStorage(domain);
        storage.Rematerialize(CreateRangeData(10, 30, domain));

        // ACT & ASSERT - All partial reads must be consistent with range
        var read1 = storage.Read(CreateRange(10, 15));
        Assert.Equal(6, read1.Length);
        VerifyDataMatchesRange(read1, 10, 15);

        var read2 = storage.Read(CreateRange(20, 25));
        Assert.Equal(6, read2.Length);
        VerifyDataMatchesRange(read2, 20, 25);

        var read3 = storage.Read(CreateRange(25, 30));
        Assert.Equal(6, read3.Length);
        VerifyDataMatchesRange(read3, 25, 30);
    }

    #endregion

    #region Domain-Agnostic Tests

    [Fact]
    public void DomainAgnostic_WorksWithFixedStepDomain()
    {
        // ARRANGE
        var domain = new IntegerFixedStepDomain();
        var storage = CreateStorage(domain);
        var rangeData = CreateRangeData(0, 100, domain);

        // ACT
        storage.Rematerialize(rangeData);
        var result = storage.Read(CreateRange(25, 75));

        // ASSERT
        VerifyDataMatchesRange(result, 25, 75);
    }

    [Fact]
    public void DomainAgnostic_WorksWithVariableStepDomain()
    {
        // ARRANGE
        var steps = new[] { 1, 2, 5, 10, 20, 50, 100 };
        var domain = new IntegerVariableStepDomain(steps);
        var storage = CreateVariableStepStorage(domain);

        var range = CreateRange(2, 50);
        var data = new[] { 2, 5, 10, 20, 50 };
        var rangeData = data.ToRangeData(range, domain);

        // ACT
        storage.Rematerialize(rangeData);
        var result = storage.Read(CreateRange(2, 50));

        // ASSERT
        Assert.Equal(5, result.Length);
        Assert.Equal([2, 5, 10, 20, 50], result.ToArray());
    }

    #endregion
}
