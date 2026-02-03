#!/usr/bin/env pwsh
# prepare-binaries.ps1 - Copy AOT binaries + wwwroot to extension bin/ folder
# Usage: Run from vscode-viberails directory: npm run prepare-binaries

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$ScriptDir = Split-Path -Parent $PSCommandPath
$ExtensionRoot = Split-Path -Parent $ScriptDir
$RepoRoot = Split-Path -Parent $ExtensionRoot
$ArtifactsDir = Join-Path $RepoRoot "Scripts" "artifacts" "aot"
$WwwrootSource = Join-Path $RepoRoot "VibeRails" "wwwroot"
$BinDir = Join-Path $ExtensionRoot "bin"

Write-Host "VibeRails Extension - Binary Preparation" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Check if AOT binaries exist
$winBinary = Join-Path $ArtifactsDir "win-x64" "vb.exe"
$linuxBinary = Join-Path $ArtifactsDir "linux-x64" "vb"

$missingBinaries = @()
if (-not (Test-Path $winBinary)) {
    $missingBinaries += "win-x64/vb.exe"
}
if (-not (Test-Path $linuxBinary)) {
    $missingBinaries += "linux-x64/vb"
}

if ($missingBinaries.Count -gt 0) {
    Write-Host "Missing AOT binaries:" -ForegroundColor Yellow
    $missingBinaries | ForEach-Object { Write-Host "  - $_" -ForegroundColor Yellow }
    Write-Host ""
    Write-Host "Run Scripts/build.ps1 first to build AOT binaries." -ForegroundColor Yellow
    exit 1
}

# Check if wwwroot exists
if (-not (Test-Path $WwwrootSource)) {
    Write-Host "Error: wwwroot not found at $WwwrootSource" -ForegroundColor Red
    exit 1
}

# Clean and create bin directory structure
Write-Host "Preparing bin directory structure..." -ForegroundColor Cyan
if (Test-Path $BinDir) {
    Write-Host "  Cleaning existing bin/" -ForegroundColor Gray
    Remove-Item -Recurse -Force $BinDir
}

$platforms = @(
    @{ Name = "win32-x64"; SourceDir = "win-x64"; Binary = "vb.exe" },
    @{ Name = "linux-x64"; SourceDir = "linux-x64"; Binary = "vb" }
)

foreach ($platform in $platforms) {
    $platformDir = Join-Path $BinDir $platform.Name
    New-Item -ItemType Directory -Force -Path $platformDir | Out-Null
    Write-Host "  Created bin/$($platform.Name)/" -ForegroundColor Green

    # Copy binary
    $sourceBinary = Join-Path $ArtifactsDir $platform.SourceDir $platform.Binary
    $destBinary = Join-Path $platformDir $platform.Binary
    Copy-Item -Path $sourceBinary -Destination $destBinary -Force
    Write-Host "    Copied $($platform.Binary)" -ForegroundColor Green

    # Copy wwwroot
    $destWwwroot = Join-Path $platformDir "wwwroot"
    Copy-Item -Path $WwwrootSource -Destination $destWwwroot -Recurse -Force
    $fileCount = (Get-ChildItem -Path $destWwwroot -Recurse -File).Count
    Write-Host "    Copied wwwroot/ ($fileCount files)" -ForegroundColor Green

    # Set execute permissions on Linux binary (no-op on Windows)
    if ($platform.Name -eq "linux-x64" -and $IsLinux) {
        chmod +x $destBinary
        Write-Host "    Set execute permissions" -ForegroundColor Green
    }

    # Validate structure
    $indexHtml = Join-Path $destWwwroot "index.html"
    if (-not (Test-Path $indexHtml)) {
        Write-Host "    Warning: index.html not found in wwwroot" -ForegroundColor Yellow
    }
}

# Display summary
Write-Host ""
Write-Host "Binary preparation complete!" -ForegroundColor Green
Write-Host ""
Write-Host "Platform packages ready:" -ForegroundColor Cyan
foreach ($platform in $platforms) {
    $platformDir = Join-Path $BinDir $platform.Name
    $binaryPath = Join-Path $platformDir $platform.Binary
    $binarySize = [math]::Round((Get-Item $binaryPath).Length / 1MB, 2)
    $wwwrootSize = [math]::Round((Get-ChildItem -Path (Join-Path $platformDir "wwwroot") -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 2)
    $totalSize = $binarySize + $wwwrootSize
    Write-Host "  $($platform.Name): " -NoNewline -ForegroundColor Cyan
    Write-Host "$($totalSize) MB " -NoNewline -ForegroundColor Yellow
    Write-Host "($($binarySize) MB binary + $($wwwrootSize) MB wwwroot)" -ForegroundColor Gray
}

Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "  1. npm run compile" -ForegroundColor White
Write-Host "  2. npm run package:win32-x64" -ForegroundColor White
Write-Host "  3. npm run package:linux-x64" -ForegroundColor White
Write-Host ""
