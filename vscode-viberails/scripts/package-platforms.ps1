#!/usr/bin/env pwsh
# package-platforms.ps1 - Build VS Code extension package (no backend bundling)
# Usage: Run from vscode-viberails directory: npm run package:all

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$ScriptDir = Split-Path -Parent $PSCommandPath
$ExtensionRoot = Split-Path -Parent $ScriptDir
$DistDir = Join-Path $ExtensionRoot "dist"

Write-Host "VibeRails Extension - Packaging" -ForegroundColor Cyan
Write-Host "===============================" -ForegroundColor Cyan
Write-Host ""

$vsceIsGlobal = $null -ne (Get-Command vsce -ErrorAction SilentlyContinue)
if (-not $vsceIsGlobal -and -not (Get-Command npx -ErrorAction SilentlyContinue)) {
    Write-Host "Error: neither global 'vsce' nor 'npx' is available." -ForegroundColor Red
    Write-Host "Install Node.js/npm and either:" -ForegroundColor Yellow
    Write-Host "  - npm install -g @vscode/vsce" -ForegroundColor Yellow
    Write-Host "  - or use npx with local @vscode/vsce" -ForegroundColor Yellow
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
if ($vsceIsGlobal) {
    Write-Host "Using global vsce binary." -ForegroundColor Gray
} else {
    Write-Host "Using npx vsce fallback." -ForegroundColor Gray
}
Write-Host ""

Push-Location $ExtensionRoot
try {
    if ($vsceIsGlobal) {
        vsce package -o dist/
    } else {
        npx vsce package -o dist/
    }

    if ($LASTEXITCODE -ne 0) {
        throw "vsce package failed"
    }
} finally {
    Pop-Location
}

$vsixFiles = @(Get-ChildItem -Path $DistDir -File -Filter "vscode-viberails-$version.vsix")
if ($vsixFiles.Count -eq 0) {
    throw "Expected VSIX not found: dist/vscode-viberails-$version.vsix"
}

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
Write-Host "  code --install-extension dist/vscode-viberails-$version.vsix" -ForegroundColor White
Write-Host ""
Write-Host "Or upload to GitHub releases" -ForegroundColor Cyan
