#!/usr/bin/env pwsh
# prepare-binaries.ps1 - Copy AOT binaries + wwwroot to extension bin/ folder
# Usage:
#   npm run prepare-binaries
#   pwsh ../deploy/prepare-binaries.ps1
#   pwsh ../deploy/prepare-binaries.ps1 -Targets win32-x64

param(
    [string[]]$Targets = @(),
    [string]$ArtifactsRoot = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$ScriptDir = Split-Path -Parent $PSCommandPath
$RepoRoot = Split-Path -Parent $ScriptDir
$ExtensionRoot = Join-Path $RepoRoot "vscode-viberails"
$ArtifactsDir = if ($ArtifactsRoot) { $ArtifactsRoot } else { Join-Path $RepoRoot "Scripts" "artifacts" "aot" }
$WwwrootSource = Join-Path $RepoRoot "VibeRails" "wwwroot"
$BinDir = Join-Path $ExtensionRoot "bin"
$supportedTargets = @("win32-x64", "linux-x64", "darwin-x64", "darwin-arm64")

Write-Host "VibeRails Extension - Binary Preparation" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Check if AOT binaries exist
$targetConfigs = @{
    "win32-x64" = @{ SourceDir = "win-x64"; Binary = "vb.exe" }
    "linux-x64" = @{ SourceDir = "linux-x64"; Binary = "vb" }
    "darwin-x64" = @{ SourceDir = "osx-x64"; Binary = "vb" }
    "darwin-arm64" = @{ SourceDir = "osx-arm64"; Binary = "vb" }
}

if ($Targets.Count -eq 0) {
    $Targets = @(
        $supportedTargets | Where-Object {
            $config = $targetConfigs[$_]
            Test-Path (Join-Path (Join-Path $ArtifactsDir $config.SourceDir) $config.Binary)
        }
    )
}

$invalidTargets = @($Targets | Where-Object { $_ -notin $supportedTargets })
if ($invalidTargets.Count -gt 0) {
    throw "Unsupported target(s): $($invalidTargets -join ', '). Supported targets: $($supportedTargets -join ', ')"
}

if ($Targets.Count -eq 0) {
    throw "No AOT binaries found under ${ArtifactsDir}. Build or download the target backends before packaging the extension."
}

$missingBinaries = @()
foreach ($target in $Targets) {
    $config = $targetConfigs[$target]
    $binaryDir = Join-Path $ArtifactsDir $config.SourceDir
    $binaryPath = Join-Path $binaryDir $config.Binary
    if (-not (Test-Path $binaryPath)) {
        $missingBinaries += "$($config.SourceDir)/$($config.Binary)"
    }
}

if ($missingBinaries.Count -gt 0) {
    $missingList = $missingBinaries -join ", "
    throw "Missing AOT binaries under ${ArtifactsDir}: $missingList. Build the required AOT binaries before packaging the extension."
}

# Check if wwwroot exists
if (-not (Test-Path $WwwrootSource)) {
    throw "wwwroot not found at $WwwrootSource"
}

# Clean and create bin directory structure
Write-Host "Preparing bin directory structure..." -ForegroundColor Cyan
if (Test-Path $BinDir) {
    Write-Host "  Cleaning existing bin/" -ForegroundColor Gray
    Remove-Item -Recurse -Force $BinDir
}

$platforms = foreach ($target in $Targets) {
    $config = $targetConfigs[$target]
    @{
        Name = $target
        SourceDir = $config.SourceDir
        Binary = $config.Binary
    }
}

foreach ($platform in $platforms) {
    $platformDir = Join-Path $BinDir $platform.Name
    New-Item -ItemType Directory -Force -Path $platformDir | Out-Null
    Write-Host "  Created bin/$($platform.Name)/" -ForegroundColor Green

    # Copy binary
    $sourceBinary = Join-Path (Join-Path $ArtifactsDir $platform.SourceDir) $platform.Binary
    $destBinary = Join-Path $platformDir $platform.Binary
    Copy-Item -Path $sourceBinary -Destination $destBinary -Force
    Write-Host "    Copied $($platform.Binary)" -ForegroundColor Green

    # Copy appsettings.json
    $sourceAppSettings = Join-Path (Join-Path $ArtifactsDir $platform.SourceDir) "appsettings.json"
    if (Test-Path $sourceAppSettings) {
        Copy-Item -Path $sourceAppSettings -Destination (Join-Path $platformDir "appsettings.json") -Force
        Write-Host "    Copied appsettings.json" -ForegroundColor Green
    } else {
        throw "appsettings.json not found at $sourceAppSettings"
    }

    # Copy wwwroot
    $destWwwroot = Join-Path $platformDir "wwwroot"
    Copy-Item -Path $WwwrootSource -Destination $destWwwroot -Recurse -Force
    $fileCount = (Get-ChildItem -Path $destWwwroot -Recurse -File).Count
    Write-Host "    Copied wwwroot/ ($fileCount files)" -ForegroundColor Green

    # Set execute permissions on Linux binary (no-op on Windows)
    if ($platform.Binary -eq "vb" -and -not $IsWindows) {
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
foreach ($platform in $platforms) {
    Write-Host "  - npm run package:$($platform.Name)" -ForegroundColor White
}
Write-Host ""
