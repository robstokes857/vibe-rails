#!/usr/bin/env pwsh
# test-vscode-marketplace.ps1
# Validate VS Code Marketplace publishing prerequisites without creating tags/releases.
# Default behavior: verify token + package VSIX (no publish).

param(
    [switch]$Publish
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Command,
        [Parameter(Mandatory = $false)]
        [string[]]$Arguments = @()
    )

    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed: $Command $($Arguments -join ' ')"
    }
}

function Invoke-Vsce {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    if (Get-Command vsce -ErrorAction SilentlyContinue) {
        Invoke-CheckedCommand -Command "vsce" -Arguments $Arguments
        return
    }

    if (-not (Get-Command npx -ErrorAction SilentlyContinue)) {
        throw "Neither 'vsce' nor 'npx' are available on PATH. Install Node.js/npm and/or @vscode/vsce."
    }

    Invoke-CheckedCommand -Command "npx" -Arguments @("--yes", "@vscode/vsce") + $Arguments
}

$scriptDir = Split-Path -Parent $PSCommandPath
$extensionRoot = Split-Path -Parent $scriptDir
$packageJsonPath = Join-Path $extensionRoot "package.json"
$distDir = Join-Path $extensionRoot "dist"

if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
    throw "'npm' was not found on PATH. Install Node.js first."
}
if (-not (Test-Path $packageJsonPath)) {
    throw "File not found: $packageJsonPath"
}

$vsPat = [Environment]::GetEnvironmentVariable("VS_PAT")
if ([string]::IsNullOrWhiteSpace($vsPat)) {
    throw "VS_PAT is not set. Set VS_PAT to your Visual Studio Marketplace PAT before running this test."
}

Push-Location $extensionRoot
try {
    $package = Get-Content -Path $packageJsonPath -Raw | ConvertFrom-Json
    $publisher = [string]$package.publisher
    $version = [string]$package.version
    if ([string]::IsNullOrWhiteSpace($publisher)) {
        throw "Could not read 'publisher' from package.json."
    }
    if ([string]::IsNullOrWhiteSpace($version)) {
        throw "Could not read 'version' from package.json."
    }

    $originalVscePat = $env:VSCE_PAT
    $env:VSCE_PAT = $vsPat
    try {
        Write-Host "Testing Marketplace auth for publisher '$publisher'..." -ForegroundColor Cyan
        Invoke-Vsce -Arguments @("verify-pat", $publisher)

        Write-Host "Packaging extension (no GitHub release/tag involved)..." -ForegroundColor Cyan
        Invoke-CheckedCommand -Command "npm" -Arguments @("run", "package")

        $vsix = Join-Path $distDir "vscode-viberails-$version.vsix"
        if (-not (Test-Path $vsix)) {
            throw "Expected VSIX not found: $vsix"
        }

        $sizeMb = [math]::Round((Get-Item $vsix).Length / 1MB, 2)
        Write-Host ""
        Write-Host "Marketplace preflight PASSED." -ForegroundColor Green
        Write-Host "  VSIX: $vsix ($sizeMb MB)" -ForegroundColor White

        if ($Publish) {
            Write-Host ""
            Write-Host "Publishing VSIX to Marketplace..." -ForegroundColor Cyan
            Invoke-Vsce -Arguments @("publish", "--packagePath", $vsix, "--skip-duplicate")
            Write-Host "Marketplace publish completed." -ForegroundColor Green
        } else {
            Write-Host ""
            Write-Host "No publish executed (default)." -ForegroundColor Yellow
            Write-Host "Re-run with -Publish to publish this VSIX only." -ForegroundColor Yellow
        }
    } finally {
        $env:VSCE_PAT = $originalVscePat
    }
} finally {
    Pop-Location
}
