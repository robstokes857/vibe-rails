#!/usr/bin/env pwsh
# test_settings_claude.ps1 - Integration test for Claude CLI settings
# Creates an environment, modifies settings, launches CLI, then cleans up

$ErrorActionPreference = "Stop"

Write-Host ""
Write-Host "======================================" -ForegroundColor Cyan
Write-Host " Claude CLI Settings Integration Test" -ForegroundColor Cyan
Write-Host "======================================" -ForegroundColor Cyan
Write-Host ""

# Setup paths
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir
$vibeRailsDir = Join-Path $repoRoot "VibeRails"
$exe = Join-Path $vibeRailsDir "bin\Debug\net10.0\vb.exe"

# Build if needed
if (-not (Test-Path $exe)) {
    Write-Host "Building VibeRails..." -ForegroundColor Yellow
    dotnet build $vibeRailsDir

    if ($LASTEXITCODE -ne 0) {
        Write-Host "Build failed!" -ForegroundColor Red
        exit 1
    }
}

# Prompt for environment name
Write-Host "Enter environment name for testing (or press Enter for 'claude-test'):" -ForegroundColor Yellow
$envName = Read-Host
if ([string]::IsNullOrWhiteSpace($envName)) {
    $envName = "claude-test"
}

Write-Host ""
Write-Host "Using environment name: $envName" -ForegroundColor Green
Write-Host ""

# Step 1: Create environment
Write-Host "Step 1: Creating Claude environment..." -ForegroundColor Cyan
& $exe env create $envName --cli claude
if ($LASTEXITCODE -ne 0) {
    Write-Host "Failed to create environment!" -ForegroundColor Red
    exit 1
}
Write-Host "Environment created successfully." -ForegroundColor Green
Write-Host ""

# Step 2: Show default settings
Write-Host "Step 2: Showing default settings..." -ForegroundColor Cyan
& $exe claude settings $envName
Write-Host ""

# Step 3: Modify settings
Write-Host "Step 3: Modifying settings..." -ForegroundColor Cyan
Write-Host "  - Setting model to opus" -ForegroundColor Gray
Write-Host "  - Setting permission mode to plan" -ForegroundColor Gray
Write-Host "  - Setting allowed tools to Read,Glob,Grep" -ForegroundColor Gray
Write-Host "  - Enabling verbose logging" -ForegroundColor Gray
& $exe claude set $envName --model opus --permission-mode plan --allowed-tools "Read,Glob,Grep" --verbose
if ($LASTEXITCODE -ne 0) {
    Write-Host "Failed to update settings!" -ForegroundColor Red
    & $exe env delete $envName -y
    exit 1
}
Write-Host "Settings updated successfully." -ForegroundColor Green
Write-Host ""

# Step 4: Verify settings
Write-Host "Step 4: Verifying updated settings..." -ForegroundColor Cyan
& $exe claude settings $envName
Write-Host ""

# Step 5: Launch CLI
Write-Host "Step 5: Launching Claude CLI with this environment..." -ForegroundColor Cyan
Write-Host "        The CLI will open in a new terminal window." -ForegroundColor Gray
Write-Host ""
$workDir = Get-Location
& $exe launch claude --env $envName --workdir $workDir

Write-Host ""
Write-Host "============================================" -ForegroundColor Yellow
Write-Host " Press Enter when you have closed the CLI" -ForegroundColor Yellow
Write-Host "============================================" -ForegroundColor Yellow
Read-Host

# Step 6: Delete environment
Write-Host ""
Write-Host "Step 6: Cleaning up - deleting test environment..." -ForegroundColor Cyan
& $exe env delete $envName
if ($LASTEXITCODE -ne 0) {
    Write-Host "Failed to delete environment!" -ForegroundColor Red
    exit 1
}
Write-Host "Environment deleted successfully." -ForegroundColor Green
Write-Host ""

Write-Host "======================================" -ForegroundColor Green
Write-Host " Claude Settings Test Complete!" -ForegroundColor Green
Write-Host "======================================" -ForegroundColor Green
Write-Host ""
