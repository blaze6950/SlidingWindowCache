# Agent Guidelines for Intervals.NET.Caching

This document provides essential information for AI coding agents working on the Intervals.NET.Caching codebase.

## Project Overview

**Intervals.NET.Caching** is a C# .NET 8.0 library implementing a read-only, range-based, sequential-optimized cache with decision-driven background rebalancing. This is a production-ready concurrent systems project with extensive architectural documentation.

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
dotnet build src/Intervals.NET.Caching/Intervals.NET.Caching.csproj --configuration Release

# Pack for NuGet
dotnet pack src/Intervals.NET.Caching/Intervals.NET.Caching.csproj --configuration Release --output ./artifacts
```

## Test Commands

### Test Framework: xUnit 2.5.3

```bash
# Run all tests
dotnet test Intervals.NET.Caching.sln --configuration Release

# Run specific test project
dotnet test tests/Intervals.NET.Caching.Unit.Tests/Intervals.NET.Caching.Unit.Tests.csproj
dotnet test tests/Intervals.NET.Caching.Integration.Tests/Intervals.NET.Caching.Integration.Tests.csproj
dotnet test tests/Intervals.NET.Caching.Invariants.Tests/Intervals.NET.Caching.Invariants.Tests.csproj

# Run single test by fully qualified name
dotnet test --filter "FullyQualifiedName=Intervals.NET.Caching.Unit.Tests.Public.Configuration.WindowCacheOptionsTests.Constructor_WithValidParameters_InitializesAllProperties"

# Run tests matching pattern
dotnet test --filter "FullyQualifiedName~Constructor"

# Run with code coverage
dotnet test --collect:"XPlat Code Coverage" --results-directory ./TestResults
```

**Test Projects:**
- **Unit Tests**: Individual component testing with Moq 4.20.70
- **Integration Tests**: Component interaction, concurrency, data source interaction
- **Invariants Tests**: 27 automated tests validating architectural contracts via public API

## Linting & Formatting

**No explicit linting tools configured.** The codebase relies on:
- Visual Studio/Rider defaults
- Nullable reference types enabled (`<Nullable>enable</Nullable>`)
- Implicit usings enabled (`<ImplicitUsings>enable</ImplicitUsings>`)
- C# 12 language features

## Code Style Guidelines

### Namespace Organization
```csharp
// Use file-scoped namespace declarations (C# 10+)
namespace Intervals.NET.Caching.Public;
namespace Intervals.NET.Caching.Core.UserPath;
namespace Intervals.NET.Caching.Infrastructure.Storage;
```

**Namespace Structure:**
- `Intervals.NET.Caching.Public` - Public API surface
- `Intervals.NET.Caching.Core` - Business logic (internal)
- `Intervals.NET.Caching.Infrastructure` - Infrastructure concerns (internal)

### Naming Conventions

**Classes:**
- PascalCase with descriptive role/responsibility suffix
- Internal classes marked `internal sealed`
- Examples: `WindowCache`, `UserRequestHandler`, `RebalanceDecisionEngine`

**Interfaces:**
- IPascalCase prefix
- Examples: `IDataSource`, `ICacheDiagnostics`, `IWindowCache`

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
using Intervals.NET.Caching.Core.Planning;
using Intervals.NET.Caching.Core.State;
using Intervals.NET.Caching.Public.Instrumentation;
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
- References to invariants (see `docs/invariants.md`)
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
- WindowCache **IS thread-safe** for its internal concurrency (user thread + background threads)
- WindowCache is **NOT designed for multiple users sharing one cache** (violates coherent access pattern)
- Multiple threads from the SAME logical consumer CAN call WindowCache safely (read-only User Path)

**Consistency Modes (three options):**
- **Eventual consistency** (default): `GetDataAsync` — returns immediately, cache converges in background
- **Hybrid consistency**: `GetDataAndWaitOnMissAsync` — waits for idle only on `PartialHit` or `FullMiss`; returns immediately on `FullHit`. Use for warm-cache guarantees without always paying the idle-wait cost.
- **Strong consistency**: `GetDataAndWaitForIdleAsync` — always waits for idle regardless of `CacheInteraction`

**Serialized Access Requirement for Hybrid/Strong Modes:**
`GetDataAndWaitOnMissAsync` and `GetDataAndWaitForIdleAsync` provide their warm-cache guarantee only under **serialized (one-at-a-time) access**. Under parallel access, `WaitForIdleAsync`'s "was idle at some point" semantics (Invariant H.3) may return the old completed TCS, missing the rebalance triggered by the concurrent request. These methods remain safe (no crashes/hangs) but the guarantee degrades under parallelism.

**Lock-Free Operations:**
```csharp
// Intent management using Volatile and Interlocked
var previousIntent = Interlocked.Exchange(ref _currentIntent, newIntent);
var currentIntent = Volatile.Read(ref _currentIntent);

// AsyncActivityCounter - fully lock-free as of latest refactor
var newCount = Interlocked.Increment(ref _activityCount);  // Atomic counter
Volatile.Write(ref _idleTcs, newTcs);  // Publish TCS with release fence
var tcs = Volatile.Read(ref _idleTcs);  // Observe TCS with acquire fence
```

**Note**: AsyncActivityCounter is now fully lock-free (refactored from previous lock-based implementation).

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
    var options = new WindowCacheOptions(...);
    
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

### Commit Message Guidelines
- **Format**: Conventional Commits with passive voice
- **Tool**: GitHub Copilot generates commit messages
- **Multi-type commits allowed**: Combine feat/test/docs/fix in single commit

**Examples:**
```
feat: extension method for strong consistency mode has been implemented; test: new method has been covered by unit tests; docs: README.md has been updated with usage examples

fix: race condition in intent processing has been resolved

refactor: AsyncActivityCounter lock has been removed and replaced with lock-free mechanism
```

### Documentation Philosophy
- **Code is source of truth** - documentation follows code
- **CRITICAL**: Every implementation MUST be finalized by updating documentation
- Documentation may be outdated; long-term goal is synchronization with code

### Documentation Update Map

| File                          | Update When                        | Focus                                   |
|-------------------------------|------------------------------------|-----------------------------------------|
| `README.md`                   | Public API changes, new features   | User-facing examples, configuration     |
| `docs/invariants.md`          | Architectural invariants changed   | System constraints, concurrency rules   |
| `docs/architecture.md`        | Concurrency mechanisms changed     | Thread safety, coordination model       |
| `docs/components/overview.md` | New components, major refactoring  | Component catalog, dependencies         |
| `docs/actors.md`              | Component responsibilities changed | Actor roles, explicit responsibilities  |
| `docs/state-machine.md`       | State transitions changed          | State machine specification             |
| `docs/storage-strategies.md`  | Storage implementation changed     | Strategy comparison, performance        |
| `docs/scenarios.md`           | Temporal behavior changed          | Scenario walkthroughs, sequences        |
| `docs/diagnostics.md`         | New diagnostics events             | Instrumentation guide                   |
| `docs/glossary.md`            | Terms or semantics change          | Canonical terminology                   |
| `benchmarks/*/README.md`      | Benchmark changes                  | Performance methodology, results        |
| `tests/*/README.md`           | Test architecture changes          | Test suite documentation                |
| XML comments (in code)        | All code changes                   | Component purpose, invariant references |

## Architecture References

**Before making changes, consult these critical documents:**
- `docs/invariants.md` - System invariants - READ THIS FIRST
- `docs/architecture.md` - Architecture and concurrency model
- `docs/actors.md` - Actor responsibilities and boundaries
- `docs/components/overview.md` - Component catalog (split by subsystem)
- `docs/glossary.md` - Canonical terminology
- `README.md` - User guide and examples

**Key Invariants to NEVER violate:**
1. Cache Contiguity: No gaps allowed in cached ranges
2. Single Writer: Only RebalanceExecutor mutates cache state
3. User Path Priority: User requests never block on rebalance
4. Intent Semantics: Intents are signals, not commands
5. Decision Idempotency: Same inputs → same decision

## File Locations

**Public API:**
- `src/Intervals.NET.Caching/Public/WindowCache.cs` - Main cache facade
- `src/Intervals.NET.Caching/Public/IDataSource.cs` - Data source contract
- `src/Intervals.NET.Caching/Public/Configuration/` - Configuration classes
- `src/Intervals.NET.Caching/Public/Instrumentation/` - Diagnostics

**Core Logic:**
- `src/Intervals.NET.Caching/Core/UserPath/` - User request handling (read-only)
- `src/Intervals.NET.Caching/Core/Rebalance/Decision/` - Decision engine
- `src/Intervals.NET.Caching/Core/Rebalance/Execution/` - Cache mutations (single writer)
- `src/Intervals.NET.Caching/Core/State/` - State management

**Infrastructure:**
- `src/Intervals.NET.Caching/Infrastructure/Storage/` - Storage strategies
- `src/Intervals.NET.Caching/Infrastructure/Concurrency/` - Async coordination

## CI/CD

**GitHub Actions:** `.github/workflows/Intervals.NET.Caching.yml`
- Triggers: Push/PR to main/master, manual dispatch
- Runs: Build, WebAssembly validation, all test suites with coverage
- Coverage: Uploaded to Codecov
- Publish: NuGet.org (on main/master push)

**Local CI Testing:**
```powershell
.github/test-ci-locally.ps1
```

## Important Notes

- **WebAssembly Compatible:** Validated with `net8.0-browser` target
- **Zero Dependencies (runtime):** Only `Intervals.NET.*` packages
- **Deterministic Testing:** Use `WaitForIdleAsync()` for predictable test behavior
- **Immutability:** Prefer `record` types and `init` properties for configuration
