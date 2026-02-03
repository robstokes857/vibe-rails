#!/usr/bin/env pwsh
# package-platforms.ps1 - Build platform-specific VS Code extension packages
# Usage: Run from vscode-viberails directory: npm run package:all

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$ScriptDir = Split-Path -Parent $PSCommandPath
$ExtensionRoot = Split-Path -Parent $ScriptDir
$DistDir = Join-Path $ExtensionRoot "dist"
$BinDir = Join-Path $ExtensionRoot "bin"

Write-Host "VibeRails Extension - Platform Packaging" -ForegroundColor Cyan
Write-Host "=========================================" -ForegroundColor Cyan
Write-Host ""

# Check if vsce is installed
if (-not (Get-Command vsce -ErrorAction SilentlyContinue)) {
    Write-Host "Error: vsce (VS Code Extension Manager) is not installed." -ForegroundColor Red
    Write-Host "Install it with: npm install -g @vscode/vsce" -ForegroundColor Yellow
    exit 1
}

# Check if binaries are prepared
if (-not (Test-Path $BinDir)) {
    Write-Host "Error: bin/ directory not found." -ForegroundColor Red
    Write-Host "Run 'npm run prepare-binaries' first." -ForegroundColor Yellow
    exit 1
}

$platforms = @("win32-x64", "linux-x64")
$missingPlatforms = @()

foreach ($platform in $platforms) {
    $platformDir = Join-Path $BinDir $platform
    if (-not (Test-Path $platformDir)) {
        $missingPlatforms += $platform
    }
}

if ($missingPlatforms.Count -gt 0) {
    Write-Host "Error: Missing platform binaries:" -ForegroundColor Red
    $missingPlatforms | ForEach-Object { Write-Host "  - $_" -ForegroundColor Yellow }
    Write-Host "Run 'npm run prepare-binaries' first." -ForegroundColor Yellow
    exit 1
}

# Create dist directory
if (-not (Test-Path $DistDir)) {
    New-Item -ItemType Directory -Force -Path $DistDir | Out-Null
    Write-Host "Created dist/ directory" -ForegroundColor Green
}

# Get package version
$packageJsonPath = Join-Path $ExtensionRoot "package.json"
$packageJson = Get-Content $packageJsonPath | ConvertFrom-Json
$version = $packageJson.version

Write-Host "Packaging version: $version" -ForegroundColor Cyan
Write-Host ""

# Package each platform
$vsixFiles = @()
$vscodeignorePath = Join-Path $ExtensionRoot ".vscodeignore"
$vscodeignoreBackup = Join-Path $ExtensionRoot ".vscodeignore.backup"

# Backup original .vscodeignore
Copy-Item $vscodeignorePath $vscodeignoreBackup -Force

foreach ($platform in $platforms) {
    Write-Host "Packaging $platform..." -ForegroundColor Cyan

    Push-Location $ExtensionRoot
    try {
        # Restore original .vscodeignore
        Copy-Item $vscodeignoreBackup $vscodeignorePath -Force

        # Append platform-specific inclusion to .vscodeignore
        Write-Host "  Configuring .vscodeignore for $platform..." -ForegroundColor Gray
        Add-Content -Path $vscodeignorePath -Value "`n# TEMPORARY: Include only $platform binaries"
        Add-Content -Path $vscodeignorePath -Value "!bin/$platform/**"

        # Run vsce package
        vsce package --target $platform -o dist/

        if ($LASTEXITCODE -ne 0) {
            throw "vsce package failed for $platform"
        }

        # Find the created .vsix file
        $vsixPattern = "vscode-viberails-$platform-$version.vsix"
        $vsixPath = Join-Path $DistDir $vsixPattern

        if (Test-Path $vsixPath) {
            $vsixFiles += $vsixPath
            $sizeMB = [math]::Round((Get-Item $vsixPath).Length / 1MB, 2)
            Write-Host "  Created: $vsixPattern ($sizeMB MB)" -ForegroundColor Green
        } else {
            Write-Host "  Warning: Expected file not found: $vsixPattern" -ForegroundColor Yellow
        }
    } finally {
        Pop-Location
    }

    Write-Host ""
}

# Restore original .vscodeignore
Write-Host "Restoring original .vscodeignore..." -ForegroundColor Gray
Copy-Item $vscodeignoreBackup $vscodeignorePath -Force
Remove-Item $vscodeignoreBackup -Force

# Display summary
Write-Host "Packaging complete!" -ForegroundColor Green
Write-Host ""
Write-Host "Generated packages:" -ForegroundColor Cyan
foreach ($vsix in $vsixFiles) {
    $filename = Split-Path -Leaf $vsix
    $sizeMB = [math]::Round((Get-Item $vsix).Length / 1MB, 2)
    Write-Host "  $filename ($sizeMB MB)" -ForegroundColor White
}

Write-Host ""
Write-Host "Installation:" -ForegroundColor Cyan
Write-Host "  code --install-extension dist/vscode-viberails-$version-win32-x64.vsix" -ForegroundColor White
Write-Host ""
Write-Host "Or upload to GitHub releases" -ForegroundColor Cyan
