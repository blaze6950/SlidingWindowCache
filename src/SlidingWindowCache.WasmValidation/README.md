# SlidingWindowCache.WasmValidation

## Purpose

This project is a **WebAssembly compilation validation target** for the SlidingWindowCache library. It is **NOT** a demo application, test project, or runtime sample.

## Goal

The sole purpose of this project is to ensure that the SlidingWindowCache library successfully compiles for the `net8.0-browser` target framework, validating WebAssembly compatibility.

## What This Is NOT

- ❌ **Not a demo** - Does not demonstrate usage patterns or best practices
- ❌ **Not a test project** - Contains no assertions, test framework, or test execution logic
- ❌ **Not a runtime validation** - Code is not intended to be executed in CI/CD or production
- ❌ **Not a sample** - Does not showcase real-world scenarios or advanced features

## What This IS

- ✅ **Compile-only validation** - Successful build proves WebAssembly compatibility
- ✅ **CI/CD compatibility check** - Ensures library can target browser environments
- ✅ **Minimal API usage** - Instantiates core types to validate no platform-incompatible APIs are used

## Implementation

The project contains minimal code that:

1. Implements a simple `IDataSource<int, int>`
2. Instantiates `WindowCache<int, int, IntegerFixedStepDomain>`
3. Calls `GetDataAsync` with a `Range<int>`
4. Uses `ReadOnlyMemory<int>` return type
5. Calls `WaitForIdleAsync` for completeness

All code uses deterministic, synchronous-friendly patterns suitable for compile-time validation.

## Build Validation

To validate WebAssembly compatibility:

```bash
dotnet build src/SlidingWindowCache.WasmValidation/SlidingWindowCache.WasmValidation.csproj
```

A successful build confirms that:
- All SlidingWindowCache public APIs compile for `net8.0-browser`
- No platform-specific APIs incompatible with WebAssembly are used
- Intervals.NET dependencies are WebAssembly-compatible

## Target Framework

- **Framework**: `net8.0-browser`
- **SDK**: Microsoft.NET.Sdk
- **Output**: Class library (no entry point)

## Dependencies

Matches the main library dependencies:
- Intervals.NET.Data (0.0.1)
- Intervals.NET.Domain.Default (0.0.2)
- Intervals.NET.Domain.Extensions (0.0.3)
- SlidingWindowCache (project reference)

## Integration with CI/CD

This project should be included in CI build matrices to automatically validate WebAssembly compatibility on every build. Any compilation failure indicates a breaking change for browser-targeted applications.
