# Agent Guidelines for Intervals.NET.Caching

This document provides essential information for AI coding agents working on the Intervals.NET.Caching codebase.

## Project Overview

**Intervals.NET.Caching** is a C# .NET 8.0 library implementing read-only, range-based caches with decision-driven background maintenance. It is organized into multiple packages:

- **`Intervals.NET.Caching`** — shared foundation: interfaces, DTOs, layered cache infrastructure, concurrency primitives
- **`Intervals.NET.Caching.SlidingWindow`** — sliding window cache implementation (sequential-access optimized)
- **`Intervals.NET.Caching.VisitedPlaces`** — visited places cache implementation (random-access optimized, with eviction and TTL)

This is a production-ready concurrent systems project with extensive architectural documentation.

**Key Architecture Principles:**
- Single-Writer Architecture: Only rebalance execution mutates cache state
- Decision-Driven Execution: Multi-stage validation prevents thrashing
- Smart Eventual Consistency: Converges to optimal state while avoiding unnecessary work
- Fully Lock-Free Concurrency: Volatile/Interlocked operations, including fully lock-free AsyncActivityCounter
- User Path Priority: User requests never block on rebalance operations

## Build Commands

### Prerequisites
- .NET SDK 8.0 (specified in `global.json`)

### Common Build Commands
```bash
# Restore dependencies
dotnet restore Intervals.NET.Caching.sln

# Build solution (Debug)
dotnet build Intervals.NET.Caching.sln

# Build solution (Release)
dotnet build Intervals.NET.Caching.sln --configuration Release

# Build specific project
dotnet build src/Intervals.NET.Caching.SlidingWindow/Intervals.NET.Caching.SlidingWindow.csproj --configuration Release

# Pack for NuGet
dotnet pack src/Intervals.NET.Caching.SlidingWindow/Intervals.NET.Caching.SlidingWindow.csproj --configuration Release --output ./artifacts
```

## Test Commands

### Test Framework: xUnit 2.5.3

```bash
# Run all tests
dotnet test Intervals.NET.Caching.sln --configuration Release

# Run specific test project
dotnet test tests/Intervals.NET.Caching.SlidingWindow.Unit.Tests/Intervals.NET.Caching.SlidingWindow.Unit.Tests.csproj
dotnet test tests/Intervals.NET.Caching.SlidingWindow.Integration.Tests/Intervals.NET.Caching.SlidingWindow.Integration.Tests.csproj
dotnet test tests/Intervals.NET.Caching.SlidingWindow.Invariants.Tests/Intervals.NET.Caching.SlidingWindow.Invariants.Tests.csproj

# Run single test by fully qualified name
dotnet test --filter "FullyQualifiedName=Intervals.NET.Caching.SlidingWindow.Unit.Tests.Public.Configuration.SlidingWindowCacheOptionsTests.Constructor_WithValidParameters_InitializesAllProperties"

# Run tests matching pattern
dotnet test --filter "FullyQualifiedName~Constructor"

# Run with code coverage
dotnet test --collect:"XPlat Code Coverage" --results-directory ./TestResults
```

**Test Projects:**
- **Unit Tests**: Individual component testing with Moq 4.20.70
- **Integration Tests**: Component interaction, concurrency, data source interaction
- **Invariants Tests**: Automated tests validating architectural contracts via public API

## Linting & Formatting

**No explicit linting tools configured.** The codebase relies on:
- Visual Studio/Rider defaults
- Nullable reference types enabled (`<Nullable>enable</Nullable>`)
- Implicit usings enabled (`<ImplicitUsings>enable</ImplicitUsings>`)
- C# 12 language features

## Code Style Guidelines

### Braces

**Always use braces** for all control flow statements (`if`, `else`, `for`, `foreach`, `while`, `do`, `using`, etc.), even for single-line bodies:

```csharp
// Correct
if (condition)
{
    DoSomething();
}

// Incorrect
if (condition)
    DoSomething();

// Incorrect
if (condition) DoSomething();
```

### Namespace Organization
```csharp
// Use file-scoped namespace declarations (C# 10+)
namespace Intervals.NET.Caching.SlidingWindow.Public;
namespace Intervals.NET.Caching.SlidingWindow.Core.UserPath;
namespace Intervals.NET.Caching.SlidingWindow.Infrastructure.Storage;
```

**Namespace Structure (SlidingWindow):**
- `Intervals.NET.Caching.SlidingWindow.Public` - Public API surface
- `Intervals.NET.Caching.SlidingWindow.Core` - Business logic (internal)
- `Intervals.NET.Caching.SlidingWindow.Infrastructure` - Infrastructure concerns (internal)

**Namespace Structure (Shared Foundation — `Intervals.NET.Caching`):**
- `Intervals.NET.Caching` - Shared interfaces and DTOs (`IRangeCache`, `IDataSource`, `RangeResult`, etc.)

### Naming Conventions

**Classes:**
- PascalCase with descriptive role/responsibility suffix
- Internal classes marked `internal sealed`
- Examples: `SlidingWindowCache`, `UserRequestHandler`, `RebalanceDecisionEngine`

**Interfaces:**
- IPascalCase prefix
- Examples: `IDataSource`, `ICacheDiagnostics`, `ISlidingWindowCache`

**Generic Type Parameters:**
- `TRange` - Range boundary type
- `TData` - Cached data type
- `TDomain` - Range domain type
- Use consistent generic names across entire codebase

**Fields:**
- Private readonly: `_fieldName` (underscore prefix)
- Examples: `_userRequestHandler`, `_cacheExtensionService`, `_state`

**Properties:**
- PascalCase: `LeftCacheSize`, `CurrentCacheRange`, `NoRebalanceRange`
- Use `init`/`set` appropriately for immutability

**Methods:**
- PascalCase with clear verb-noun structure
- Async methods ALWAYS end with `Async`
- Examples: `GetDataAsync`, `HandleRequestAsync`, `PublishIntent`

### Import Patterns

**Implicit Usings Enabled** - No need for `System.*` imports.

**Import Order:**
1. External libraries (e.g., `Intervals.NET`)
2. Project namespaces (e.g., `Intervals.NET.Caching.*`)
3. Alphabetically sorted within each group

**Example:**
```csharp
using Intervals.NET;
using Intervals.NET.Domain.Abstractions;
using Intervals.NET.Caching.SlidingWindow.Core.Planning;
using Intervals.NET.Caching.SlidingWindow.Core.State;
using Intervals.NET.Caching.SlidingWindow.Public.Instrumentation;
```

### XML Documentation

**Required for all public APIs:**
```csharp
/// <summary>
/// Brief description of the component/method.
/// </summary>
/// <typeparam name="TRange">Description of type parameter.</typeparam>
/// <param name="parameter">Description of parameter.</param>
/// <returns>Description of return value.</returns>
/// <remarks>
/// <para><strong>Architectural Context:</strong></para>
/// <para>Detailed remarks with bullet points...</para>
/// <list type="bullet">
/// <item><description>First point</description></item>
/// </list>
/// </remarks>
```

**Internal components should have detailed architectural remarks:**
- References to invariants (see `docs/sliding-window/invariants.md`)
- Cross-references to related components
- Explicit responsibilities and non-responsibilities
- Execution context (User Thread vs Background Thread)

### Type Guidelines

**Use appropriate types:**
- `ReadOnlyMemory<T>` for data buffers
- `ValueTask<T>` for frequently-called async methods
- `Task` for less frequent async operations
- `record` types for immutable configuration/DTOs
- `sealed` for classes that shouldn't be inherited

**Validation:**
```csharp
// Constructor validation with descriptive exceptions
if (leftCacheSize < 0)
{
    throw new ArgumentOutOfRangeException(
        nameof(leftCacheSize),
        "LeftCacheSize must be greater than or equal to 0."
    );
}
```

### Error Handling

**User Path Exceptions:**
- Propagate exceptions to caller
- Use descriptive exception messages
- Validate parameters early

**Background Path Exceptions:**
```csharp
// Fire-and-forget with diagnostics callback
try
{
    // Rebalance execution
}
catch (Exception ex)
{
    _cacheDiagnostics.RebalanceExecutionFailed(ex);
    // Exception swallowed to prevent background task crashes
}
```

**Critical Rule:** Background exceptions must NOT crash the application. Always capture and report via diagnostics interface.

### Concurrency Patterns

**Single-Writer Architecture (CRITICAL):**
- User Path: READ-ONLY (never mutates Cache, IsInitialized, or NoRebalanceRange)
- Rebalance Execution: SINGLE WRITER (sole authority for cache mutations)
- Serialization: Channel-based with single reader/single writer (intent processing loop)

**Threading Model - Single Logical Consumer with Internal Concurrency:**
- **User-facing model**: One logical consumer per cache (one user, one viewport, coherent access pattern)
- **Internal implementation**: Multiple threads operate concurrently (User thread + Intent loop + Execution loop)
- SlidingWindowCache **IS thread-safe** for its internal concurrency (user thread + background threads)
- SlidingWindowCache is **NOT designed for multiple users sharing one cache** (violates coherent access pattern)
- Multiple threads from the SAME logical consumer CAN call SlidingWindowCache safely (read-only User Path)

**Consistency Modes (three options):**
- **Eventual consistency** (default): `GetDataAsync` — returns immediately, cache converges in background
- **Hybrid consistency**: `GetDataAndWaitOnMissAsync` — waits for idle only on `PartialHit` or `FullMiss`; returns immediately on `FullHit`. Use for warm-cache guarantees without always paying the idle-wait cost.
- **Strong consistency**: `GetDataAndWaitForIdleAsync` — always waits for idle regardless of `CacheInteraction`

**Serialized Access Requirement for Hybrid/Strong Modes:**
`GetDataAndWaitOnMissAsync` and `GetDataAndWaitForIdleAsync` provide their warm-cache guarantee only under **serialized (one-at-a-time) access**. Under parallel access, `WaitForIdleAsync`'s "was idle at some point" semantics (Invariant S.H.3) may return the old completed TCS, missing the rebalance triggered by the concurrent request. These methods remain safe (no crashes/hangs) but the guarantee degrades under parallelism.

**Lock-Free Operations:**
```csharp
// Intent management using Volatile and Interlocked
var previousIntent = Interlocked.Exchange(ref _currentIntent, newIntent);
var currentIntent = Volatile.Read(ref _currentIntent);

// AsyncActivityCounter - fully lock-free
var newCount = Interlocked.Increment(ref _activityCount);  // Atomic counter
Volatile.Write(ref _idleTcs, newTcs);  // Publish TCS with release fence
var tcs = Volatile.Read(ref _idleTcs);  // Observe TCS with acquire fence
```

**Note**: AsyncActivityCounter is fully lock-free.

### Testing Guidelines

**Test Structure:**
- Use xUnit `[Fact]` and `[Theory]` attributes
- Follow Arrange-Act-Assert pattern
- Use region comments: `#region Constructor - Valid Parameters Tests`

**Test Naming:**
```csharp
[Fact]
public void MethodName_Scenario_ExpectedBehavior()
{
    // ARRANGE
    var options = new SlidingWindowCacheOptions(...);
    
    // ACT
    var result = options.DoSomething();
    
    // ASSERT
    Assert.Equal(expectedValue, result);
}
```

**Exception Testing:**
```csharp
// Use Record.Exception/ExceptionAsync to separate ACT from ASSERT
var exception = Record.Exception(() => operation());
var exceptionAsync = await Record.ExceptionAsync(async () => await operationAsync());

Assert.NotNull(exception);  // Verify exception thrown
Assert.IsType<ArgumentException>(exception);  // Verify type
Assert.Null(exception);  // Verify no exception
```

**WaitForIdleAsync Usage:**
```csharp
// Use for testing to wait until system was idle at some point
await cache.WaitForIdleAsync(); 

// Cache WAS idle (converged state) - assert on that state
Assert.Equal(expectedRange, actualRange);
```

**WaitForIdleAsync Semantics:**
- Completes when system **was idle at some point** (not "is idle now")
- Uses eventual consistency semantics (correct for testing convergence)
- New activity may start immediately after completion
- Re-check state if stronger guarantees needed

**When WaitForIdleAsync is NOT needed**: After normal `GetDataAsync` calls (cache is eventually consistent by design).

## Commit & Documentation Workflow

### Commit Policy

**Commits are made exclusively by a human**, after all changes have been manually reviewed. Agents must NOT create git commits. When work is complete, present a summary of all changes for human review.

### Commit Message Guidelines
- **Format**: Conventional Commits with passive voice
- **Multi-type commits allowed**: Combine feat/test/docs/fix in single commit

**Examples:**
```
feat: extension method for strong consistency mode has been implemented; test: new method has been covered by unit tests; docs: README.md has been updated with usage examples

fix: race condition in intent processing has been resolved
```

### Documentation Philosophy
- **Code is source of truth** - documentation follows code
- **CRITICAL**: Every implementation MUST be finalized by updating documentation

### Documentation Update Map

| File                                          | Update When                        | Focus                                   |
|-----------------------------------------------|------------------------------------|-----------------------------------------|
| `README.md`                                   | Public API changes, new features   | User-facing examples, configuration     |
| `docs/sliding-window/invariants.md`           | Architectural invariants changed   | System constraints, concurrency rules   |
| `docs/sliding-window/architecture.md`         | Concurrency mechanisms changed     | Thread safety, coordination model       |
| `docs/sliding-window/components/overview.md`  | New components, major refactoring  | Component catalog, dependencies         |
| `docs/sliding-window/actors.md`               | Component responsibilities changed | Actor roles, explicit responsibilities  |
| `docs/sliding-window/state-machine.md`        | State transitions changed          | State machine specification             |
| `docs/sliding-window/storage-strategies.md`   | Storage implementation changed     | Strategy comparison, performance        |
| `docs/sliding-window/scenarios.md`            | Temporal behavior changed          | Scenario walkthroughs, sequences        |
| `docs/shared/diagnostics.md`                  | New diagnostics events             | Instrumentation guide                   |
| `docs/shared/glossary.md`                     | Terms or semantics change          | Canonical terminology                   |
| `benchmarks/*/README.md`                      | Benchmark changes                  | Performance methodology, results        |
| `tests/*/README.md`                           | Test architecture changes          | Test suite documentation                |
| XML comments (in code)                        | All code changes                   | Component purpose, invariant references |

## Architecture References

**Before making changes, consult these critical documents:**
- `docs/sliding-window/invariants.md` - System invariants - READ THIS FIRST
- `docs/sliding-window/architecture.md` - Architecture and concurrency model
- `docs/sliding-window/actors.md` - Actor responsibilities and boundaries
- `docs/sliding-window/components/overview.md` - Component catalog (split by subsystem)
- `docs/shared/glossary.md` - Canonical terminology
- `README.md` - User guide and examples

**Key Invariants to NEVER violate:**
1. Cache Contiguity: No gaps allowed in cached ranges
2. Single Writer: Only RebalanceExecutor mutates cache state
3. User Path Priority: User requests never block on rebalance
4. Intent Semantics: Intents are signals, not commands
5. Decision Idempotency: Same inputs → same decision

## File Locations

**Public API (Shared Foundation — `Intervals.NET.Caching`):**
- `src/Intervals.NET.Caching/IRangeCache.cs` - Shared cache interface
- `src/Intervals.NET.Caching/IDataSource.cs` - Data source contract
- `src/Intervals.NET.Caching/Dto/` - Shared DTOs (`RangeResult`, `RangeChunk`, `CacheInteraction`)
- `src/Intervals.NET.Caching/Layered/` - `LayeredRangeCache`, `LayeredRangeCacheBuilder`, `RangeCacheDataSourceAdapter`
- `src/Intervals.NET.Caching/Extensions/` - `RangeCacheConsistencyExtensions` (strong consistency)

**Public API (SlidingWindow):**
- `src/Intervals.NET.Caching.SlidingWindow/Public/ISlidingWindowCache.cs` - SlidingWindow-specific interface
- `src/Intervals.NET.Caching.SlidingWindow/Public/Cache/SlidingWindowCache.cs` - Main cache facade
- `src/Intervals.NET.Caching.SlidingWindow/Public/Cache/SlidingWindowCacheBuilder.cs` - Builder (includes `Layered()`)
- `src/Intervals.NET.Caching.SlidingWindow/Public/Configuration/` - Configuration classes
- `src/Intervals.NET.Caching.SlidingWindow/Public/Instrumentation/` - Diagnostics
- `src/Intervals.NET.Caching.SlidingWindow/Public/Extensions/` - `SlidingWindowCacheConsistencyExtensions`, `SlidingWindowLayerExtensions`

**Core Logic:**
- `src/Intervals.NET.Caching.SlidingWindow/Core/UserPath/` - User request handling (read-only)
- `src/Intervals.NET.Caching.SlidingWindow/Core/Rebalance/Decision/` - Decision engine
- `src/Intervals.NET.Caching.SlidingWindow/Core/Rebalance/Execution/` - Cache mutations (single writer)
- `src/Intervals.NET.Caching.SlidingWindow/Core/State/` - State management

**Infrastructure:**
- `src/Intervals.NET.Caching.SlidingWindow/Infrastructure/Storage/` - Storage strategies
- `src/Intervals.NET.Caching.SlidingWindow/Infrastructure/Concurrency/` - Async coordination
- `src/Intervals.NET.Caching/Infrastructure/Concurrency/AsyncActivityCounter.cs` - Shared lock-free activity counter (internal, visible to SWC via InternalsVisibleTo)

**Public API (VisitedPlaces):**
- `src/Intervals.NET.Caching.VisitedPlaces/Public/IVisitedPlacesCache.cs` - VisitedPlaces-specific interface
- `src/Intervals.NET.Caching.VisitedPlaces/Public/Cache/VisitedPlacesCache.cs` - Main cache facade
- `src/Intervals.NET.Caching.VisitedPlaces/Public/Cache/VisitedPlacesCacheBuilder.cs` - Builder (includes `Layered()`)
- `src/Intervals.NET.Caching.VisitedPlaces/Public/Configuration/` - Configuration classes (`VisitedPlacesCacheOptions`, storage strategies, eviction sampling)
- `src/Intervals.NET.Caching.VisitedPlaces/Public/Instrumentation/` - Diagnostics
- `src/Intervals.NET.Caching.VisitedPlaces/Public/Extensions/` - `VisitedPlacesLayerExtensions`
- `src/Intervals.NET.Caching.VisitedPlaces/Core/Eviction/IEvictionPolicy.cs` - Public eviction policy interface
- `src/Intervals.NET.Caching.VisitedPlaces/Core/Eviction/IEvictionSelector.cs` - Public eviction selector interface (also exposes `SamplingEvictionSelector<TRange,TData>` abstract base)
- `src/Intervals.NET.Caching.VisitedPlaces/Core/Eviction/Policies/` - Public concrete policies: `MaxSegmentCountPolicy`, `MaxTotalSpanPolicy`
- `src/Intervals.NET.Caching.VisitedPlaces/Core/Eviction/Selectors/` - Public concrete selectors: `LruEvictionSelector`, `FifoEvictionSelector`, `SmallestFirstEvictionSelector`

**WebAssembly Validation:**
- `src/Intervals.NET.Caching.SlidingWindow.WasmValidation/` - Validates Core + SlidingWindow compile for `net8.0-browser`
- `src/Intervals.NET.Caching.VisitedPlaces.WasmValidation/` - Validates Core + VisitedPlaces compile for `net8.0-browser`

## CI/CD

**GitHub Actions — two package-specific workflows:**

- **`.github/workflows/intervals-net-caching-swc.yml`** — SlidingWindow workflow
  - Triggers: Push/PR to main/master (paths: Core, SlidingWindow, SWC WasmValidation, SWC tests), manual dispatch
  - Runs: Build solution, SWC WebAssembly validation, SWC test suites (Unit/Integration/Invariants) with coverage
  - Coverage: Uploaded to Codecov
  - Publish: `Intervals.NET.Caching` + `Intervals.NET.Caching.SlidingWindow` to NuGet.org (on main/master push)

- **`.github/workflows/intervals-net-caching-vpc.yml`** — VisitedPlaces workflow
  - Triggers: Push/PR to main/master (paths: Core, VisitedPlaces, VPC WasmValidation, VPC tests), manual dispatch
  - Runs: Build solution, VPC WebAssembly validation, VPC test suites (Unit/Integration/Invariants) with coverage
  - Coverage: Uploaded to Codecov
  - Publish: `Intervals.NET.Caching` + `Intervals.NET.Caching.VisitedPlaces` to NuGet.org (on main/master push)

**Note:** Both workflows publish `Intervals.NET.Caching` (core). The `--skip-duplicate` flag on `dotnet nuget push` ensures no conflict if both run concurrently against the same core version.

**Local CI Testing:**
```powershell
.github/test-ci-locally.ps1
```

## Important Notes

- **WebAssembly Compatible:** Validated with `net8.0-browser` target
- **Zero Dependencies (runtime):** Only `Intervals.NET.*` packages
- **Deterministic Testing:** Use `WaitForIdleAsync()` for predictable test behavior
- **Immutability:** Prefer `record` types and `init` properties for configuration
