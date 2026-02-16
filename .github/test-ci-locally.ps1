# Local CI/CD Testing Script
# This script replicates the GitHub Actions workflow locally for testing

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "SlidingWindowCache CI/CD Local Test" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Environment variables (matching GitHub Actions)
$env:SOLUTION_PATH = "SlidingWindowCache.sln"
$env:PROJECT_PATH = "src/SlidingWindowCache/SlidingWindowCache.csproj"
$env:WASM_VALIDATION_PATH = "src/SlidingWindowCache.WasmValidation/SlidingWindowCache.WasmValidation.csproj"
$env:UNIT_TEST_PATH = "tests/SlidingWindowCache.Unit.Tests/SlidingWindowCache.Unit.Tests.csproj"
$env:INTEGRATION_TEST_PATH = "tests/SlidingWindowCache.Integration.Tests/SlidingWindowCache.Integration.Tests.csproj"
$env:INVARIANTS_TEST_PATH = "tests/SlidingWindowCache.Invariants.Tests/SlidingWindowCache.Invariants.Tests.csproj"

# Track failures
$failed = $false

# Step 1: Restore solution dependencies
Write-Host "[Step 1/9] Restoring solution dependencies..." -ForegroundColor Yellow
dotnet restore $env:SOLUTION_PATH
if ($LASTEXITCODE -ne 0) { 
    Write-Host "❌ Restore failed" -ForegroundColor Red
    $failed = $true
}
else {
    Write-Host "✅ Restore successful" -ForegroundColor Green
}
Write-Host ""

# Step 2: Build solution
Write-Host "[Step 2/9] Building solution (Release)..." -ForegroundColor Yellow
dotnet build $env:SOLUTION_PATH --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) { 
    Write-Host "❌ Build failed" -ForegroundColor Red
    $failed = $true
}
else {
    Write-Host "✅ Build successful" -ForegroundColor Green
}
Write-Host ""

# Step 3: Validate WebAssembly compatibility
Write-Host "[Step 3/9] Validating WebAssembly compatibility..." -ForegroundColor Yellow
dotnet build $env:WASM_VALIDATION_PATH --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) { 
    Write-Host "❌ WebAssembly validation failed" -ForegroundColor Red
    $failed = $true
}
else {
    Write-Host "✅ WebAssembly compilation successful - library is compatible with net8.0-browser" -ForegroundColor Green
}
Write-Host ""

# Step 4: Run Unit Tests
Write-Host "[Step 4/9] Running Unit Tests with coverage..." -ForegroundColor Yellow
dotnet test $env:UNIT_TEST_PATH --configuration Release --no-build --verbosity normal --collect:"XPlat Code Coverage" --results-directory ./TestResults/Unit
if ($LASTEXITCODE -ne 0) { 
    Write-Host "❌ Unit tests failed" -ForegroundColor Red
    $failed = $true
}
else {
    Write-Host "✅ Unit tests passed" -ForegroundColor Green
}
Write-Host ""

# Step 5: Run Integration Tests
Write-Host "[Step 5/9] Running Integration Tests with coverage..." -ForegroundColor Yellow
dotnet test $env:INTEGRATION_TEST_PATH --configuration Release --no-build --verbosity normal --collect:"XPlat Code Coverage" --results-directory ./TestResults/Integration
if ($LASTEXITCODE -ne 0) { 
    Write-Host "❌ Integration tests failed" -ForegroundColor Red
    $failed = $true
}
else {
    Write-Host "✅ Integration tests passed" -ForegroundColor Green
}
Write-Host ""

# Step 6: Run Invariants Tests
Write-Host "[Step 6/9] Running Invariants Tests with coverage..." -ForegroundColor Yellow
dotnet test $env:INVARIANTS_TEST_PATH --configuration Release --no-build --verbosity normal --collect:"XPlat Code Coverage" --results-directory ./TestResults/Invariants
if ($LASTEXITCODE -ne 0) { 
    Write-Host "❌ Invariants tests failed" -ForegroundColor Red
    $failed = $true
}
else {
    Write-Host "✅ Invariants tests passed" -ForegroundColor Green
}
Write-Host ""

# Step 7: Check coverage files
Write-Host "[Step 7/9] Checking coverage files..." -ForegroundColor Yellow
$coverageFiles = Get-ChildItem -Path "./TestResults" -Filter "coverage.cobertura.xml" -Recurse
if ($coverageFiles.Count -gt 0) {
    Write-Host "✅ Found $($coverageFiles.Count) coverage file(s)" -ForegroundColor Green
    foreach ($file in $coverageFiles) {
        Write-Host "   - $($file.FullName)" -ForegroundColor Gray
    }
}
else {
    Write-Host "⚠️  No coverage files found" -ForegroundColor Yellow
}
Write-Host ""

# Step 8: Build NuGet package
Write-Host "[Step 8/9] Creating NuGet package..." -ForegroundColor Yellow
if (Test-Path "./artifacts") {
    Remove-Item -Path "./artifacts" -Recurse -Force
}
dotnet pack $env:PROJECT_PATH --configuration Release --no-build --output ./artifacts
if ($LASTEXITCODE -ne 0) { 
    Write-Host "❌ Package creation failed" -ForegroundColor Red
    $failed = $true
}
else {
    $packages = Get-ChildItem -Path "./artifacts" -Filter "*.nupkg"
    Write-Host "✅ Package created successfully" -ForegroundColor Green
    foreach ($pkg in $packages) {
        Write-Host "   - $($pkg.Name)" -ForegroundColor Gray
    }
}
Write-Host ""

# Step 9: Summary
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Test Summary" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
if ($failed) {
    Write-Host "❌ Some steps failed - see output above" -ForegroundColor Red
    exit 1
}
else {
    Write-Host "✅ All steps passed successfully!" -ForegroundColor Green
    Write-Host ""
    Write-Host "Next steps:" -ForegroundColor Cyan
    Write-Host "  - Review coverage reports in ./TestResults/" -ForegroundColor Gray
    Write-Host "  - Inspect NuGet package in ./artifacts/" -ForegroundColor Gray
    Write-Host "  - Push to trigger GitHub Actions workflow" -ForegroundColor Gray
    exit 0
}
