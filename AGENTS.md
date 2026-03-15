# Agent Guidelines for Intervals.NET.Caching

C# .NET 8.0 library implementing read-only, range-based caches with decision-driven background maintenance. Three packages:

- **`Intervals.NET.Caching`** — shared foundation: interfaces, DTOs, layered cache infrastructure, concurrency primitives (non-packable)
- **`Intervals.NET.Caching.SlidingWindow`** — sliding window cache (sequential-access optimized, single contiguous window, prefetch)
- **`Intervals.NET.Caching.VisitedPlaces`** — visited places cache (random-access optimized, non-contiguous segments, eviction, TTL)

## Build & Test Commands

Prerequisites: .NET SDK 8.0 (see `global.json`).

```bash
dotnet build Intervals.NET.Caching.sln
dotnet build Intervals.NET.Caching.sln --configuration Release

# All tests
dotnet test Intervals.NET.Caching.sln --configuration Release

# SlidingWindow tests
dotnet test tests/Intervals.NET.Caching.SlidingWindow.Unit.Tests/Intervals.NET.Caching.SlidingWindow.Unit.Tests.csproj
dotnet test tests/Intervals.NET.Caching.SlidingWindow.Integration.Tests/Intervals.NET.Caching.SlidingWindow.Integration.Tests.csproj
dotnet test tests/Intervals.NET.Caching.SlidingWindow.Invariants.Tests/Intervals.NET.Caching.SlidingWindow.Invariants.Tests.csproj

# VisitedPlaces tests
dotnet test tests/Intervals.NET.Caching.VisitedPlaces.Unit.Tests/Intervals.NET.Caching.VisitedPlaces.Unit.Tests.csproj
dotnet test tests/Intervals.NET.Caching.VisitedPlaces.Integration.Tests/Intervals.NET.Caching.VisitedPlaces.Integration.Tests.csproj
dotnet test tests/Intervals.NET.Caching.VisitedPlaces.Invariants.Tests/Intervals.NET.Caching.VisitedPlaces.Invariants.Tests.csproj

# Single test
dotnet test --filter "FullyQualifiedName=Full.Test.Name"
dotnet test --filter "FullyQualifiedName~PartialMatch"

# Local CI validation
.github/test-ci-locally.ps1
```

## Commit & Workflow Policy

**Commits are made exclusively by a human.** Agents must NOT create git commits. Present a summary of all changes for human review.

- **Format**: Conventional Commits, passive voice, multi-type allowed (e.g., `feat: X; test: Y; docs: Z`)
- **Documentation follows code**: every implementation MUST be finalized by updating relevant documentation (see Pre-Change Reference Guide below)

## Code Style

Standard C# conventions apply. Below are project-specific rules only:

- **Always use braces** for all control flow (`if`, `else`, `for`, `foreach`, `while`, `do`, `using`), even single-line bodies
- File-scoped namespace declarations. Internal classes: `internal sealed`
- Generic type parameters: `TRange` (boundary), `TData` (cached data), `TDomain` (range domain) — use consistently
- Async methods always end with `Async`. Use `ValueTask<T>` for hot paths if not async possible, `Task` for infrequent operations
- Prefer `record` types and `init` properties for configuration/DTOs. Use `sealed` for non-inheritable classes
- XML documentation required on all public APIs. Internal components should reference invariant IDs (e.g., `SWC.A.1`, `VPC.B.1`)
- **Error handling**: User Path exceptions propagate to caller. Background Path exceptions are swallowed and reported via `ICacheDiagnostics` — background exceptions must NEVER crash the application
- **Tests**: xUnit with `[Fact]`/`[Theory]`. Naming: `MethodName_Scenario_ExpectedBehavior`. Arrange-Act-Assert pattern with `#region` grouping. Use `Record.Exception`/`Record.ExceptionAsync` to separate ACT from ASSERT
- **`WaitForIdleAsync` semantics**: completes when the system **was idle at some point**, not "is idle now". New activity may start immediately after completion. Guarantees degrade under parallel access (see invariant S.H.3)

## Project Structure

All three packages follow the same internal layer convention: `Public/` (API surface) → `Core/` (business logic, internal) → `Infrastructure/` (storage, concurrency, internal).

**Core package** (`Intervals.NET.Caching`) is non-packable (`IsPackable=false`). Its types compile into SWC/VPC assemblies via `ProjectReference` with `PrivateAssets="all"`. Internal types shared via `InternalsVisibleTo`.

**Namespace pattern**: `Intervals.NET.Caching.{Package}.{Layer}.{Subsystem}` — e.g., `Intervals.NET.Caching.SlidingWindow.Core.Rebalance.Decision`, `Intervals.NET.Caching.VisitedPlaces.Core.Eviction`.

**Test projects** (Unit, Integration, Invariants for each package) plus shared test infrastructure: `tests/*.Tests.Infrastructure/`. Reuse existing test helpers and builders rather than reinventing.

**CI**: Two GitHub Actions workflows, one per publishable package (`.github/workflows/intervals-net-caching-swc.yml`, `.github/workflows/intervals-net-caching-vpc.yml`). Both validate WebAssembly compilation (`net8.0-browser` target).

## Architectural Invariants

Read `docs/shared/invariants.md`, `docs/sliding-window/invariants.md`, and `docs/visited-places/invariants.md` for full specifications. Below are the invariants most likely to be violated by code changes.

**SlidingWindow (SWC):**
1. **Single-writer** (SWC.A.1): only `RebalanceExecutor` mutates cache state; User Path is strictly read-only
2. **Cache contiguity** (SWC.A.12b): `CacheData` must always be a single contiguous range — no gaps, no partial materialization
3. **Atomic state updates** (SWC.B.2): `CacheData` and `CurrentCacheRange` must change atomically — no intermediate inconsistent states
4. **Intent = signal, not command** (SWC.C.8): publishing an intent does NOT guarantee rebalance; the Decision Engine may skip it at any of 5 stages
5. **Multi-stage decision validation** (SWC.D.5): rebalance executes only if ALL stages confirm necessity. Stage 2 MUST evaluate against the pending execution's `DesiredNoRebalanceRange`, not the current cache's

**VisitedPlaces (VPC):**
1. **Single-writer** (VPC.A.1): only the Background Storage Loop mutates segment collection; User Path is strictly read-only
2. **Strict FIFO event ordering** (VPC.B.1): every `CacheNormalizationRequest` processed in order — no supersession, no discards. Violating corrupts eviction metadata (e.g., LRU timestamps)
3. **Segment non-overlap** (VPC.C.3): no two segments share any discrete domain point — `End[i] < Start[i+1]` strictly
4. **Segments never merge** (VPC.C.2): even adjacent segments remain separate forever
5. **Just-stored segment immunity** (VPC.E.3): segment stored in the current background step is excluded from eviction candidates. Without this, infinite fetch-store-evict loops occur under LRU
6. **Idempotent removal** (VPC.T.1): `ISegmentStorage.TryRemove()` checks `segment.IsRemoved` before calling `segment.MarkAsRemoved()` (`Volatile.Write`) — only the first caller (TTL normalization or eviction) performs storage removal and decrements the count

**Shared:**
1. **Activity counter ordering** (S.H.1/S.H.2): increment BEFORE work is made visible; decrement in `finally` blocks ALWAYS. Violating causes `WaitForIdleAsync` to hang or return prematurely
2. **Disposal** (S.J): post-disposal guard on public methods, idempotent disposal, cooperative cancellation of background ops
3. **Bounded range requests** (S.R): requested ranges must be finite on both ends; unbounded ranges throw `ArgumentException`

## SWC vs VPC: Key Architectural Differences

These packages share interfaces but have fundamentally different internals. Do NOT apply patterns from one to the other.

| Aspect | SlidingWindow | VisitedPlaces |
|--------|--------------|---------------|
| Event processing | Latest-intent-wins (supersession via `Interlocked.Exchange`) | Strict FIFO (every event processed in order) |
| Cache structure | Single contiguous window; contiguity mandatory | Non-contiguous segment collection; gaps valid |
| Background I/O | `RebalanceExecutor` calls `IDataSource.FetchAsync` | Background Path does NO I/O; data delivered via User Path events |
| Prefetch | Geometry-based expansion (`LeftCacheSize`/`RightCacheSize`) | Strictly demand-driven; never prefetches |
| Cancellation | Rebalance execution is cancellable via CTS | Background events are NOT cancellable |
| Consistency modes | Eventual, Hybrid, Strong | Eventual, Strong (no Hybrid) |
| Execution contexts | User Thread + Intent Loop + Execution Loop | User Thread + Background Storage Loop |

## Dangerous Modifications

These changes appear reasonable but silently violate invariants. Functional tests typically still pass.

- **Adding writes in User Path** (either package): introduces write-write races with Background Path. User Path must be strictly read-only
- **Changing VPC event processing to supersession**: corrupts eviction metadata (LRU timestamps for skipped events are lost)
- **Merging VPC segments**: resets eviction metadata, breaks `FindIntersecting` binary search ordering
- **Moving activity counter increment after publish**: `WaitForIdleAsync` returns prematurely (nanosecond race window, nearly impossible to reproduce)
- **Removing `finally` from `DecrementActivity` call sites**: any exception leaves counter permanently incremented; `WaitForIdleAsync` hangs forever
- **Making SWC `Rematerialize()` non-atomic** (split data + range update): User Path reads see inconsistent data/range — silent data corruption
- **Removing just-stored segment immunity**: causes infinite fetch-store-evict loops under LRU (just-stored segment has earliest `LastAccessedAt`)
- **Adding `IDataSource` calls to VPC Background Path**: blocks FIFO event processing, delays metadata updates, no cancellation infrastructure for I/O
- **Publishing intents from SWC Rebalance Execution**: creates positive feedback loop — system never reaches idle, disposal hangs
- **Removing the `IsRemoved` check from `SegmentStorageBase.TryRemove()`**: both TTL normalization and eviction proceed to call `MarkAsRemoved()` and decrement the policy aggregate count, corrupting eviction pressure calculations
- **Swallowing exceptions in User Path**: user receives empty/partial data with no failure signal; `CacheInteraction` classification becomes misleading
- **Adding locks around SWC `CacheState` reads**: creates lock contention between User Path and Rebalance — violates "user requests never block on rebalance"

## Pre-Change Reference Guide

Before modifying a subsystem, read the relevant docs. After completing changes, update the same docs plus any listed under "Also Update."

| Modification Area | Read Before Changing | Also Update After |
|---|---|---|
| SWC rebalance / decision logic | `docs/sliding-window/invariants.md`, `docs/sliding-window/architecture.md` | `docs/sliding-window/state-machine.md`, `docs/sliding-window/scenarios.md` |
| SWC storage strategies | `docs/sliding-window/storage-strategies.md` | same |
| SWC components | `docs/sliding-window/components/overview.md`, relevant component doc | `docs/sliding-window/actors.md` |
| VPC eviction (policy/selector) | `docs/visited-places/eviction.md`, `docs/visited-places/invariants.md` (VPC.E group) | same |
| VPC TTL | `docs/visited-places/invariants.md` (VPC.T group), `docs/visited-places/architecture.md` | same |
| VPC background processing | `docs/visited-places/architecture.md`, `docs/visited-places/invariants.md` (VPC.B group) | `docs/visited-places/scenarios.md` |
| VPC storage strategies | `docs/visited-places/storage-strategies.md` | same |
| VPC components | `docs/visited-places/components/overview.md` | `docs/visited-places/actors.md` |
| `IDataSource` contract | `docs/shared/boundary-handling.md` | same |
| `AsyncActivityCounter` | `docs/shared/invariants.md` (S.H group), `docs/shared/architecture.md` | same |
| Layered cache | `docs/shared/glossary.md`, `README.md` | same |
| Public API changes | `README.md` | `README.md` |
| Diagnostics events | `docs/shared/diagnostics.md` or package-specific diagnostics doc | same |
| New terms or semantic changes | `docs/shared/glossary.md` or package-specific glossary | same |

**Canonical terminology**: see `docs/shared/glossary.md`, `docs/sliding-window/glossary.md`, `docs/visited-places/glossary.md`. Each includes a "Common Misconceptions" section.
