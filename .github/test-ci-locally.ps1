# Local CI/CD Testing Script
# This script replicates the GitHub Actions workflows locally for testing

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Intervals.NET.Caching CI/CD Local Test" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Environment variables (matching GitHub Actions)
$env:SOLUTION_PATH = "Intervals.NET.Caching.sln"

# SlidingWindow
$env:SWC_PROJECT_PATH = "src/Intervals.NET.Caching.SlidingWindow/Intervals.NET.Caching.SlidingWindow.csproj"
$env:SWC_WASM_VALIDATION_PATH = "src/Intervals.NET.Caching.SlidingWindow.WasmValidation/Intervals.NET.Caching.SlidingWindow.WasmValidation.csproj"
$env:SWC_UNIT_TEST_PATH = "tests/Intervals.NET.Caching.SlidingWindow.Unit.Tests/Intervals.NET.Caching.SlidingWindow.Unit.Tests.csproj"
$env:SWC_INTEGRATION_TEST_PATH = "tests/Intervals.NET.Caching.SlidingWindow.Integration.Tests/Intervals.NET.Caching.SlidingWindow.Integration.Tests.csproj"
$env:SWC_INVARIANTS_TEST_PATH = "tests/Intervals.NET.Caching.SlidingWindow.Invariants.Tests/Intervals.NET.Caching.SlidingWindow.Invariants.Tests.csproj"

# VisitedPlaces
$env:VPC_PROJECT_PATH = "src/Intervals.NET.Caching.VisitedPlaces/Intervals.NET.Caching.VisitedPlaces.csproj"
$env:VPC_WASM_VALIDATION_PATH = "src/Intervals.NET.Caching.VisitedPlaces.WasmValidation/Intervals.NET.Caching.VisitedPlaces.WasmValidation.csproj"
$env:VPC_UNIT_TEST_PATH = "tests/Intervals.NET.Caching.VisitedPlaces.Unit.Tests/Intervals.NET.Caching.VisitedPlaces.Unit.Tests.csproj"
$env:VPC_INTEGRATION_TEST_PATH = "tests/Intervals.NET.Caching.VisitedPlaces.Integration.Tests/Intervals.NET.Caching.VisitedPlaces.Integration.Tests.csproj"
$env:VPC_INVARIANTS_TEST_PATH = "tests/Intervals.NET.Caching.VisitedPlaces.Invariants.Tests/Intervals.NET.Caching.VisitedPlaces.Invariants.Tests.csproj"

# Track failures
$failed = $false

# Step 1: Restore solution dependencies
Write-Host "[Step 1/12] Restoring solution dependencies..." -ForegroundColor Yellow
dotnet restore $env:SOLUTION_PATH
if ($LASTEXITCODE -ne 0) {
    Write-Host "? Restore failed" -ForegroundColor Red
    $failed = $true
}
else {
    Write-Host "? Restore successful" -ForegroundColor Green
}
Write-Host ""

# Step 2: Build solution
Write-Host "[Step 2/12] Building solution (Release)..." -ForegroundColor Yellow
dotnet build $env:SOLUTION_PATH --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) {
    Write-Host "? Build failed" -ForegroundColor Red
    $failed = $true
}
else {
    Write-Host "? Build successful" -ForegroundColor Green
}
Write-Host ""

# Step 3: Validate SlidingWindow WebAssembly compatibility
Write-Host "[Step 3/12] Validating SlidingWindow WebAssembly compatibility..." -ForegroundColor Yellow
dotnet build $env:SWC_WASM_VALIDATION_PATH --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) {
    Write-Host "? SlidingWindow WebAssembly validation failed" -ForegroundColor Red
    $failed = $true
}
else {
    Write-Host "? SlidingWindow WebAssembly compilation successful - library is compatible with net8.0-browser" -ForegroundColor Green
}
Write-Host ""

# Step 4: Validate VisitedPlaces WebAssembly compatibility
Write-Host "[Step 4/12] Validating VisitedPlaces WebAssembly compatibility..." -ForegroundColor Yellow
dotnet build $env:VPC_WASM_VALIDATION_PATH --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) {
    Write-Host "? VisitedPlaces WebAssembly validation failed" -ForegroundColor Red
    $failed = $true
}
else {
    Write-Host "? VisitedPlaces WebAssembly compilation successful - library is compatible with net8.0-browser" -ForegroundColor Green
}
Write-Host ""

# Step 5: Run SlidingWindow Unit Tests
Write-Host "[Step 5/12] Running SlidingWindow Unit Tests with coverage..." -ForegroundColor Yellow
dotnet test $env:SWC_UNIT_TEST_PATH --configuration Release --no-build --verbosity normal --collect:"XPlat Code Coverage" --results-directory ./TestResults/SWC/Unit
if ($LASTEXITCODE -ne 0) {
    Write-Host "? SlidingWindow Unit tests failed" -ForegroundColor Red
    $failed = $true
}
else {
    Write-Host "? SlidingWindow Unit tests passed" -ForegroundColor Green
}
Write-Host ""

# Step 6: Run SlidingWindow Integration Tests
Write-Host "[Step 6/12] Running SlidingWindow Integration Tests with coverage..." -ForegroundColor Yellow
dotnet test $env:SWC_INTEGRATION_TEST_PATH --configuration Release --no-build --verbosity normal --collect:"XPlat Code Coverage" --results-directory ./TestResults/SWC/Integration
if ($LASTEXITCODE -ne 0) {
    Write-Host "? SlidingWindow Integration tests failed" -ForegroundColor Red
    $failed = $true
}
else {
    Write-Host "? SlidingWindow Integration tests passed" -ForegroundColor Green
}
Write-Host ""

# Step 7: Run SlidingWindow Invariants Tests
Write-Host "[Step 7/12] Running SlidingWindow Invariants Tests with coverage..." -ForegroundColor Yellow
dotnet test $env:SWC_INVARIANTS_TEST_PATH --configuration Release --no-build --verbosity normal --collect:"XPlat Code Coverage" --results-directory ./TestResults/SWC/Invariants
if ($LASTEXITCODE -ne 0) {
    Write-Host "? SlidingWindow Invariants tests failed" -ForegroundColor Red
    $failed = $true
}
else {
    Write-Host "? SlidingWindow Invariants tests passed" -ForegroundColor Green
}
Write-Host ""

# Step 8: Run VisitedPlaces Unit Tests
Write-Host "[Step 8/12] Running VisitedPlaces Unit Tests with coverage..." -ForegroundColor Yellow
dotnet test $env:VPC_UNIT_TEST_PATH --configuration Release --no-build --verbosity normal --collect:"XPlat Code Coverage" --results-directory ./TestResults/VPC/Unit
if ($LASTEXITCODE -ne 0) {
    Write-Host "? VisitedPlaces Unit tests failed" -ForegroundColor Red
    $failed = $true
}
else {
    Write-Host "? VisitedPlaces Unit tests passed" -ForegroundColor Green
}
Write-Host ""

# Step 9: Run VisitedPlaces Integration Tests
Write-Host "[Step 9/12] Running VisitedPlaces Integration Tests with coverage..." -ForegroundColor Yellow
dotnet test $env:VPC_INTEGRATION_TEST_PATH --configuration Release --no-build --verbosity normal --collect:"XPlat Code Coverage" --results-directory ./TestResults/VPC/Integration
if ($LASTEXITCODE -ne 0) {
    Write-Host "? VisitedPlaces Integration tests failed" -ForegroundColor Red
    $failed = $true
}
else {
    Write-Host "? VisitedPlaces Integration tests passed" -ForegroundColor Green
}
Write-Host ""

# Step 10: Run VisitedPlaces Invariants Tests
Write-Host "[Step 10/12] Running VisitedPlaces Invariants Tests with coverage..." -ForegroundColor Yellow
dotnet test $env:VPC_INVARIANTS_TEST_PATH --configuration Release --no-build --verbosity normal --collect:"XPlat Code Coverage" --results-directory ./TestResults/VPC/Invariants
if ($LASTEXITCODE -ne 0) {
    Write-Host "? VisitedPlaces Invariants tests failed" -ForegroundColor Red
    $failed = $true
}
else {
    Write-Host "? VisitedPlaces Invariants tests passed" -ForegroundColor Green
}
Write-Host ""

# Step 11: Check coverage files
Write-Host "[Step 11/12] Checking coverage files..." -ForegroundColor Yellow
$coverageFiles = Get-ChildItem -Path "./TestResults" -Filter "coverage.cobertura.xml" -Recurse
if ($coverageFiles.Count -gt 0) {
    Write-Host "? Found $($coverageFiles.Count) coverage file(s)" -ForegroundColor Green
    foreach ($file in $coverageFiles) {
        Write-Host "   - $($file.FullName)" -ForegroundColor Gray
    }
}
else {
    Write-Host "??  No coverage files found" -ForegroundColor Yellow
}
Write-Host ""

# Step 12: Build NuGet packages
Write-Host "[Step 12/12] Creating NuGet packages..." -ForegroundColor Yellow
if (Test-Path "./artifacts") {
    Remove-Item -Path "./artifacts" -Recurse -Force
}
dotnet pack $env:SWC_PROJECT_PATH --configuration Release --no-build --output ./artifacts
if ($LASTEXITCODE -ne 0) {
    Write-Host "? Package creation failed (SlidingWindow)" -ForegroundColor Red
    $failed = $true
}
dotnet pack $env:VPC_PROJECT_PATH --configuration Release --no-build --output ./artifacts
if ($LASTEXITCODE -ne 0) {
    Write-Host "? Package creation failed (VisitedPlaces)" -ForegroundColor Red
    $failed = $true
}
if (-not $failed) {
    $packages = Get-ChildItem -Path "./artifacts" -Filter "*.nupkg"
    Write-Host "? Packages created successfully" -ForegroundColor Green
    foreach ($pkg in $packages) {
        Write-Host "   - $($pkg.Name)" -ForegroundColor Gray
    }
}
Write-Host ""

# Summary
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Test Summary" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
if ($failed) {
    Write-Host "? Some steps failed - see output above" -ForegroundColor Red
    exit 1
}
else {
    Write-Host "? All steps passed successfully!" -ForegroundColor Green
    Write-Host ""
    Write-Host "Next steps:" -ForegroundColor Cyan
    Write-Host "  - Review coverage reports in ./TestResults/" -ForegroundColor Gray
    Write-Host "  - Inspect NuGet packages in ./artifacts/" -ForegroundColor Gray
    Write-Host "  - Push to trigger GitHub Actions workflows" -ForegroundColor Gray
    exit 0
}
