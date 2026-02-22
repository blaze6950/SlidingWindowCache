# WindowCache Invariant Tests - Implementation Summary

## Overview
Comprehensive unit test suite for the WindowCache library verifying system invariants through the public API using DEBUG-only instrumentation counters.

**Architecture**: Single-Writer Model with Multi-Stage Rebalance Validation
- User Path is read-only (never mutates cache)
- Rebalance Execution is sole writer (single-writer architecture)
- Rebalance necessity determined by multi-stage analytical validation pipeline
- Cancellation is coordination tool (prevents concurrent executions), not decision mechanism

**Test Statistics**:
- **Total Tests**: 26 test methods (29 individual xUnit test cases including Theory rows)
- **Test Execution Time**: ~7 seconds for full suite
- **Architecture**: Single-writer with intent-carried data

## Implementation Details

### 1. Instrumentation Infrastructure
- **Location**: `src/SlidingWindowCache/Infrastructure/Instrumentation/`
- **Files**:
  - `ICacheDiagnostics.cs` - Public interface for cache event tracking
  - `EventCounterCacheDiagnostics.cs` - Thread-safe counter implementation
  - Each counter includes XML documentation linking to specific invariants and usage locations
  
- **Instrumented Components**:
  - `WindowCache.cs` - No direct instrumentation (facade)
  - `UserRequestHandler.cs` - Tracks user requests served (NO cache mutations - read-only)
  - `IntentController.cs` - Tracks intent published/cancelled
  - `IntentController.cs` - Tracks intent published/cancelled, execution started/completed/cancelled, policy-based skips
  - `RebalanceExecutor.cs` - Tracks optimization-based skips (same-range detection)

- **Counter Types** (with Invariant References):
  - `UserRequestServed` - User requests completed
  - `CacheExpanded` - Range analysis determined expansion needed (called by shared CacheDataExtensionService)
  - `CacheReplaced` - Range analysis determined replacement needed (called by shared CacheDataExtensionService)
  - `RebalanceIntentPublished` - Rebalance intent published (every user request with delivered data)
  - `RebalanceIntentCancelled` - Rebalance intent cancelled (new request supersedes old)
  - `RebalanceExecutionStarted` - Rebalance execution began
  - `RebalanceExecutionCompleted` - Rebalance execution finished successfully (sole writer)
  - `RebalanceExecutionCancelled` - Rebalance execution cancelled
  - `RebalanceSkippedCurrentNoRebalanceRange` - **Policy-based skip (Stage 1)** - Request within current NoRebalanceRange threshold
  - `RebalanceSkippedPendingNoRebalanceRange` - **Policy-based skip (Stage 2)** - Request within pending NoRebalanceRange threshold
  - `RebalanceSkippedSameRange` - **Optimization-based skip** (Invariant D.28) - DesiredRange == CurrentRange

**Note**: `CacheExpanded` and `CacheReplaced` are incremented during range analysis by the shared `CacheDataExtensionService` 
(used by both User Path and Rebalance Path) when determining what data needs to be fetched. They track analysis/planning, 
not actual cache mutations. Actual mutations only occur in Rebalance Execution via `Rematerialize()`.

### 2. Deterministic Synchronization Infrastructure
- **Location**: `tests/SlidingWindowCache.Invariants.Tests/TestInfrastructure/`
- **Files Created**:
  - `TestHelpers.cs` - Factory methods, data verification, and deterministic synchronization utilities

- **Synchronization Strategy**: Deterministic Task Lifecycle Tracking
  - **Method**: `cache.WaitForIdleAsync()` - Waits until the system was idle at some point
  - **Mechanism**: Observe-and-stabilize pattern based on Task reference tracking (not counter polling)
  - **Benefits**:
    - ✅ Race-free: No timing dependencies or polling intervals
    - ✅ Deterministic: Guaranteed idle state when method returns
    - ✅ Fast: Completes immediately when background work finishes
    - ✅ Reliable: Works under concurrent intent cancellation and rescheduling

- **Implementation Details**:
  - **`AsyncActivityCounter`** tracks active operations (intents + executions) using lock-free `Interlocked` operations to support `WaitForIdleAsync()`
  - **`WaitForIdleAsync()`** awaits the `TaskCompletionSource` published by `AsyncActivityCounter` when the activity count reaches zero

- **Old Approach (Removed)**:
  - Counter-based polling with stability windows
  - Timing-dependent with configurable intervals
  - Complex lifecycle tracking logic
  - Replaced by deterministic Task tracking

- **Domain Strategy**: Uses `Intervals.NET.Domain.Default.Numeric.IntegerFixedStepDomain` for proper range handling with inclusivity support

- **Mock Strategy**: Uses **Moq** framework for `IDataSource<int, int>` mocking
  - Mock configured per-test in Arrange section
  - Generates sequential integer data respecting range inclusivity
  - Supports configurable fetch delays for cancellation testing
  - Properly calculates range spans using Intervals.NET domain

### 3. Test Project Configuration
- **Updated**: `SlidingWindowCache.Invariants.Tests.csproj`
- **Added Dependencies**:
  - `Moq` (Version 4.20.70) - For IDataSource mocking
  - `xUnit` - Test framework
  - `Intervals.NET` packages - Domain and range handling
  - Project reference to `SlidingWindowCache`
- **Framework**: xUnit with standard `Assert` class (not FluentAssertions - decision for consistency)

### 4. Comprehensive Test Suite
- **Location**: `tests/SlidingWindowCache.Invariants.Tests/WindowCacheInvariantTests.cs`
- **Test Count**: 26 test methods (29 individual xUnit test cases)
- **Test Structure**: Each test method references its invariant number and description

#### Test Categories:

**A. User Path & Fast User Access (8 tests)**
- A.1-0a: User request cancels rebalance (to prevent interference, not for mutation safety)
- A.2.1: User path always serves requests
- A.2.2: User path never waits for rebalance
- A.2.10: User always receives exact requested range
- A.3.8: Cold start - User Path does NOT populate cache (read-only)
- A.3.8: Cache expansion - User Path does NOT expand cache (read-only)
- A.3.8: Full cache replacement - User Path does NOT replace cache (read-only)
- A.3.9a: Cache contiguity maintained

**B. Cache State & Consistency (2 tests)**
- B.11: CacheData and CurrentCacheRange always consistent
- B.15: Cancelled rebalance doesn't violate consistency

**C. Rebalance Intent & Temporal (4 tests)**
- C.17: At most one active intent
- C.18: Previous intent becomes logically superseded (execution relevance determined by multi-stage validation)
- C.24: Intent doesn't guarantee execution (opportunistic, validation-driven)
- C.23: System stabilizes under load

**D. Rebalance Decision Path (4 tests)**
- D.27: No rebalance if request in NoRebalanceRange (policy-based skip) - **Enhanced with execution started assertion**
- D.27 Stage 1: Skips when within current NoRebalanceRange
- D.29 Stage 2: Skips when within pending NoRebalanceRange (anti-thrashing)
- D.28: Rebalance skipped when DesiredRange == CurrentRange (optimization-based skip) - **New test**
- TODOs for D.25, D.26 (require internal state access)

**E. Cache Geometry & Policy (2 tests + TODOs)**
- E.30: DesiredRange computed from config and request
- ReadMode behavior verification (Snapshot and CopyOnRead)
- TODOs for E.31-34 (require internal state inspection)

**F. Rebalance Execution (3 tests)**
- F.35, F.35a: Rebalance execution supports cancellation
- F.36a: Rebalance normalizes cache - **Enhanced with lifecycle integrity assertions**
- F.40-42: Post-execution guarantees

**G. Execution Context & Scheduling (2 tests)**
- G.43-45: Execution context separation
- G.46: Cancellation supported for all scenarios

**Meta-Invariant Assertions (used across multiple tests)**
- Execution lifecycle integrity: `AssertRebalanceLifecycleIntegrity` verifies started == (completed + cancelled)

**Additional Comprehensive Tests (3 tests)**
- Complete scenario with multiple requests and rebalancing
- Concurrency scenario with rapid request bursts and cancellation
- Read mode variations (Snapshot and CopyOnRead)

### 5. Key Implementation Changes (Single-Writer Architecture Migration)

**UserRequestHandler.cs**:
- **REMOVED**: All `_state.Cache.Rematerialize()` calls (User Path is now read-only)
- **REMOVED**: `_state.LastRequested` writes (only Rebalance Execution writes)
- **ADDED**: Cold start detection using cache data enumeration
- **ADDED**: Materialization of assembled data to array (for user + intent)
- **ADDED**: Creation of `RangeData` for intent with delivered data
- **PRESERVED**: Cancellation logic (User Path priority)
- **PRESERVED**: Cache hit detection and read logic
- **PRESERVED**: IDataSource fetching for missing data

**IntentController.cs**:
- **ADDED**: `RangeData<TRange,TData,TDomain> deliveredData` parameter to intent
- **ADDED**: Intent now carries both requested range and actual delivered data
- **PURPOSE**: Enables Rebalance Execution to use delivered data as authoritative source

**RebalanceExecutor.cs**:
- **ADDED**: Accept `requestedRange` and `deliveredData` parameters
- **CHANGED**: Uses delivered data from intent as base (not current cache)
- **ADDED**: Writes to `_state.LastRequested` (sole writer)
- **ADDED**: Writes to `_state.NoRebalanceRange` (already was sole writer)
- **RESPONSIBILITY**: Sole writer of all cache state (Cache, LastRequested, NoRebalanceRange)

**CacheState.cs**:
- **CHANGED**: `LastRequested` and `NoRebalanceRange` setters to `internal`
- **PURPOSE**: Enforce single-writer pattern at compile time

**Storage Classes**:
- **CopyOnReadStorage.cs**: Refactored to use dual-buffer (staging buffer) pattern for safe rematerialization
  - Active buffer remains immutable during reads
  - Staging buffer used for new range data during rematerialization
  - Atomic buffer swap after rematerialization completes
  - Prevents enumeration issues when concatenating existing + new data
- **SnapshotReadStorage.cs**: No changes needed - already uses safe rematerialization pattern

### 6. Test Execution
- **Build Configuration**: DEBUG mode (required for instrumentation and Task tracking)
- **Reset Pattern**: Each test resets counters in constructor/dispose
- **Synchronization**: Uses deterministic `cache.WaitForIdleAsync()` for race-free background work completion
- **Data Verification**: Custom helper verifies returned data matches expected range values

## Invariants Coverage

### Single-Writer Architecture

**Key Architectural Change**:
- **User Path**: Read-only with respect to cache state (never mutates)
- **Rebalance Execution**: Sole writer of all cache state
- **Intent Structure**: Contains both requested range and delivered data (`RangeData`)
- **Concurrency**: Single-writer eliminates race conditions

### Test Coverage Breakdown

**User Path Tests (8 tests - verify read-only behavior)**:
- User Path serves requests without mutating cache
- User Path cancels rebalance to prevent interference (not for mutation safety)
- User Path returns correct data immediately
- User Path publishes intent with delivered data
- Cache mutations occur exclusively via Rebalance Execution

**Rebalance Execution Tests (verify single-writer)**:
- Rebalance Execution is sole writer of cache state
- Rebalance Execution uses delivered data from intent
- Rebalance Execution handles cancellation properly
- Cache state converges asynchronously (eventual consistency)

**Architectural Invariants (enforced by code structure)**:
- A.-1: User Path and Rebalance Execution never write concurrently (User Path doesn't write)
- A.8: User Path MUST NOT mutate cache (enforced by removing Rematerialize calls)
- F.36: Rebalance Execution is ONLY writer (enforced by internal setters)
- C.24e/f: Intent contains delivered data (enforced by PublishIntent signature)

## Usage

```bash
# Run all invariant tests
dotnet test tests/SlidingWindowCache.Invariants.Tests/SlidingWindowCache.Invariants.Tests.csproj --configuration Debug

# Run specific test
dotnet test --filter "FullyQualifiedName~Invariant_D28_SkipWhenDesiredEqualsCurrentRange"

# Run tests by category (example: all Decision Path tests)
dotnet test --filter "FullyQualifiedName~Invariant_D"
```

## Key Implementation Details

### Skip Condition Distinction
The system has **two distinct skip scenarios**, tracked by separate counters:

1. **Policy-Based Skip** (Invariants D.27 / D.29)
   - Counters: `RebalanceSkippedCurrentNoRebalanceRange` (Stage 1) and `RebalanceSkippedPendingNoRebalanceRange` (Stage 2)
   - Location: `IntentController.ProcessIntentsAsync` (after `DecisionEngine` returns `ShouldSchedule=false`)
   - Reason: Request within NoRebalanceRange threshold zone (current or pending)
   - Characteristic: Execution **never starts** (decision-level optimization)

2. **Optimization-Based Skip** (Invariant D.28)
   - Counter: `RebalanceSkippedSameRange`
   - Location: `RebalanceExecutor.ExecuteAsync` (before I/O operations)
   - Reason: `CurrentCacheRange == DesiredCacheRange` (already at target)
   - Characteristic: Execution **starts but exits early** (executor-level optimization)

### CopyOnRead Storage - Staging Buffer Pattern
The `CopyOnReadStorage` implementation uses a dual-buffer approach for safe rematerialization:
- **Active buffer**: Immutable during reads, serves user requests
- **Staging buffer**: Write-only during rematerialization, reused across operations
- **Atomic swap**: After successful rematerialization, buffers are swapped
- **Rationale**: Prevents enumeration issues when concatenating existing + new data ranges

This pattern ensures:
- Active storage remains immutable during reads (no lock needed for single-consumer model)
- Predictable memory allocation behavior
- No temporary allocations beyond the staging buffer

See `docs/storage-strategies.md` for detailed documentation.

## Notes
- **Architecture**: Single-writer model (User Path read-only, Rebalance Execution sole writer)
- **Intent Structure**: Intent carries delivered `RangeData` (requested range + actual data)
- **Eventual Consistency**: Cache state converges asynchronously via background rebalance
- Instrumentation is available in all builds via `ICacheDiagnostics` / `EventCounterCacheDiagnostics`
- Tests use `cache.WaitForIdleAsync()` for deterministic async verification
- Counter reset in constructor/dispose ensures test isolation
- Uses `Intervals.NET.Domain.Default.Numeric.IntegerFixedStepDomain` for proper range inclusivity handling
- `CacheExpanded` and `CacheReplaced` counters are deprecated (User Path no longer mutates)

## Related Documentation
- `docs/invariants.md` - Complete invariant documentation (updated for single-writer architecture)
- `docs/cache-state-machine.md` - State transitions (updated to show only Rebalance Execution mutates)
- `docs/actors-and-responsibilities.md` - Component responsibilities (updated for read-only User Path)
- `docs/concurrency-model.md` - Single-writer architecture and eventual consistency model
- `MIGRATION_SUMMARY.md` - Implementation details of single-writer migration
- `DOCUMENTATION_UPDATES.md` - Documentation changes made for new architecture

## Test Infrastructure

All tests use:
1. **`WaitForIdleAsync()`** - Deterministic synchronization with background rebalance (available in all builds)
2. **`EventCounterCacheDiagnostics`** - Observable event counters for validation
3. **`TestHelpers`** - Test data builders and common assertion patterns

## Diagnostic Usage in Tests

All tests leverage `EventCounterCacheDiagnostics` for observable validation of cache behavior:

```csharp
private readonly EventCounterCacheDiagnostics _cacheDiagnostics;

public WindowCacheInvariantTests()
{
    _cacheDiagnostics = new EventCounterCacheDiagnostics();
}
```

### Purpose of Diagnostics in Tests

1. **Observable State**: Tracks internal behavioral events without invasive test hooks
2. **Invariant Validation**: Verifies system invariants through event patterns
3. **Scenario Verification**: Confirms expected cache scenarios (hit/miss patterns, rebalance lifecycle)
4. **Test Isolation**: `Reset()` method ensures clean state between test phases

### Common Assertion Patterns

**User Path Scenario Validation:**
```csharp
// Verify full cache hit
TestHelpers.AssertFullCacheHit(_cacheDiagnostics, expectedCount: 1);

// Verify partial cache hit with extension
TestHelpers.AssertPartialCacheHit(_cacheDiagnostics, expectedCount: 1);

// Verify full cache miss (cold start or jump)
TestHelpers.AssertFullCacheMiss(_cacheDiagnostics, expectedCount: 1);
```

**Rebalance Lifecycle Validation:**
```csharp
// Verify rebalance lifecycle integrity (started == completed + cancelled)
TestHelpers.AssertRebalanceLifecycleIntegrity(_cacheDiagnostics);

// Verify rebalance completed successfully
TestHelpers.AssertRebalanceCompleted(_cacheDiagnostics, minExpected: 1);

// Verify rebalance was cancelled by new user request
TestHelpers.AssertRebalancePathCancelled(_cacheDiagnostics, minExpected: 1);
```

**Data Source Interaction Validation:**
```csharp
// Verify single-range fetch (cold start or jump)
TestHelpers.AssertDataSourceFetchedFullRange(_cacheDiagnostics, expectedCount: 1);

// Verify missing-segments fetch (partial hit optimization)
TestHelpers.AssertDataSourceFetchedMissingSegments(_cacheDiagnostics, expectedCount: 1);
```

**Test Isolation with Reset():**
```csharp
// Setup phase
await cache.GetDataAsync(Range.Closed(100, 200), ct);
await cache.WaitForIdleAsync();

// Reset counters to isolate test scenario
_cacheDiagnostics.Reset();

// Test phase - only this scenario's events are tracked
await cache.GetDataAsync(Range.Closed(120, 180), ct);

// Assert only test scenario events
Assert.Equal(1, _cacheDiagnostics.UserRequestFullCacheHit);
Assert.Equal(0, _cacheDiagnostics.UserRequestPartialCacheHit);
```

### Integration with WaitForIdleAsync()

Diagnostics and `WaitForIdleAsync()` work together for complete test determinism:

```csharp
// 1. Perform action
await cache.GetDataAsync(Range.Closed(100, 200), ct);

// 2. Wait for rebalance to complete (deterministic synchronization)
await cache.WaitForIdleAsync();

// 3. Assert using diagnostics (observable validation)
Assert.Equal(1, _cacheDiagnostics.RebalanceExecutionCompleted);
TestHelpers.AssertRebalanceLifecycleIntegrity(_cacheDiagnostics);
```

**Key Distinction:**
- **`WaitForIdleAsync()`**: Synchronization mechanism (when to assert)
- **Diagnostics**: Observable state (what to assert)

### Available Diagnostic Counters

**User Path Events:**
- `UserRequestServed` - Total requests completed
- `CacheExpanded` - Cache expansion operations
- `CacheReplaced` - Cache replacement operations
- `UserRequestFullCacheHit` - Full cache hits
- `UserRequestPartialCacheHit` - Partial cache hits
- `UserRequestFullCacheMiss` - Full cache misses

**Data Source Events:**
- `DataSourceFetchSingleRange` - Single-range fetches
- `DataSourceFetchMissingSegments` - Multi-segment fetches

**Rebalance Lifecycle:**
- `RebalanceIntentPublished` - Intents published
- `RebalanceIntentCancelled` - Intents cancelled
- `RebalanceScheduled` - Rebalances scheduled for execution
- `RebalanceExecutionStarted` - Executions started
- `RebalanceExecutionCompleted` - Executions completed
- `RebalanceExecutionCancelled` - Executions cancelled
- `RebalanceExecutionFailed` - Executions failed with exception
- `RebalanceSkippedCurrentNoRebalanceRange` - Skipped: request within current NoRebalanceRange (Stage 1)
- `RebalanceSkippedPendingNoRebalanceRange` - Skipped: request within pending NoRebalanceRange (Stage 2)
- `RebalanceSkippedSameRange` - Skipped due to DesiredRange == CurrentRange (Stage 4)

### Helper Assertion Library

See `TestHelpers.cs` for complete assertion library including:
- `AssertNoUserPathMutations()` - Verify User Path is read-only
- `AssertIntentPublished()` - Verify intent publication
- `AssertRebalanceLifecycleIntegrity()` - Verify lifecycle invariants
- `AssertRebalanceSkippedDueToPolicy()` - Verify skip optimization
- `AssertFullCacheHit/PartialCacheHit/FullCacheMiss()` - Verify user scenarios
- `AssertDataSourceFetchedFullRange/MissingSegments()` - Verify data source interaction

**See**: [Diagnostics Guide](../../docs/diagnostics.md) for comprehensive diagnostic API reference