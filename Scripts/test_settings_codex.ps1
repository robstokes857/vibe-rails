#!/usr/bin/env pwsh
# test_settings_codex.ps1 - Integration test for Codex CLI settings
# Creates an environment, modifies settings, launches CLI, then cleans up

$ErrorActionPreference = "Stop"

Write-Host ""
Write-Host "=====================================" -ForegroundColor Cyan
Write-Host " Codex CLI Settings Integration Test" -ForegroundColor Cyan
Write-Host "=====================================" -ForegroundColor Cyan
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
Write-Host "Enter environment name for testing (or press Enter for 'codex-test'):" -ForegroundColor Yellow
$envName = Read-Host
if ([string]::IsNullOrWhiteSpace($envName)) {
    $envName = "codex-test"
}

Write-Host ""
Write-Host "Using environment name: $envName" -ForegroundColor Green
Write-Host ""

# Step 1: Create environment
Write-Host "Step 1: Creating Codex environment..." -ForegroundColor Cyan
& $exe env create $envName --cli codex
if ($LASTEXITCODE -ne 0) {
    Write-Host "Failed to create environment!" -ForegroundColor Red
    exit 1
}
Write-Host "Environment created successfully." -ForegroundColor Green
Write-Host ""

# Step 2: Show default settings
Write-Host "Step 2: Showing default settings..." -ForegroundColor Cyan
& $exe codex settings $envName
Write-Host ""

# Step 3: Modify settings
Write-Host "Step 3: Modifying settings..." -ForegroundColor Cyan
Write-Host "  - Setting model to o3" -ForegroundColor Gray
Write-Host "  - Setting sandbox to workspace-write" -ForegroundColor Gray
Write-Host "  - Setting approval to on-request" -ForegroundColor Gray
Write-Host "  - Enabling search" -ForegroundColor Gray
& $exe codex set $envName --model o3 --codex-sandbox workspace-write --codex-approval on-request --search
if ($LASTEXITCODE -ne 0) {
    Write-Host "Failed to update settings!" -ForegroundColor Red
    & $exe env delete $envName -y
    exit 1
}
Write-Host "Settings updated successfully." -ForegroundColor Green
Write-Host ""

# Step 4: Verify settings
Write-Host "Step 4: Verifying updated settings..." -ForegroundColor Cyan
& $exe codex settings $envName
Write-Host ""

# Step 5: Launch CLI
Write-Host "Step 5: Launching Codex CLI with this environment..." -ForegroundColor Cyan
Write-Host "        The CLI will open in a new terminal window." -ForegroundColor Gray
Write-Host ""
$workDir = Get-Location
& $exe launch codex --env $envName --workdir $workDir

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

Write-Host "=====================================" -ForegroundColor Green
Write-Host " Codex Settings Test Complete!" -ForegroundColor Green
Write-Host "=====================================" -ForegroundColor Green
Write-Host ""
