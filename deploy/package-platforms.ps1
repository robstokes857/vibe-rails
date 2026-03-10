#!/usr/bin/env pwsh
# package-platforms.ps1 - Build platform-specific VS Code extension packages with bundled backends.
# Usage:
#   npm run package:all
#   npm run package:win32-x64
#   npm run package:linux-x64
#   npm run package:darwin-x64
#   npm run package:darwin-arm64

param(
    [string[]]$Targets = @()
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$ScriptDir = Split-Path -Parent $PSCommandPath
$RepoRoot = Split-Path -Parent $ScriptDir
$ExtensionRoot = Join-Path $RepoRoot "vscode-viberails"
$DistDir = Join-Path $ExtensionRoot "dist"
$PrepareBinariesScript = Join-Path $ScriptDir "prepare-binaries.ps1"
$VsCodeIgnorePath = Join-Path $ExtensionRoot ".vscodeignore"
$ArtifactsDir = Join-Path $RepoRoot "Scripts" "artifacts" "aot"

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

# Validate input
$supportedTargets = @("win32-x64", "linux-x64", "darwin-x64", "darwin-arm64")
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
    throw "No packaged backend targets were detected under ${ArtifactsDir}. Build or download the target backends first."
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
Write-Host "Targets: $($Targets -join ', ')" -ForegroundColor Gray
Write-Host ""

# Stage bundled binaries into bin/<target>/ before packaging.
if (-not (Test-Path $PrepareBinariesScript)) {
    throw "Binary preparation script not found: $PrepareBinariesScript"
}

& $PrepareBinariesScript -Targets $Targets

if (-not (Test-Path $VsCodeIgnorePath)) {
    throw ".vscodeignore not found: $VsCodeIgnorePath"
}

$originalVsCodeIgnore = Get-Content -Path $VsCodeIgnorePath -Raw
$generatedPackages = @()

Push-Location $ExtensionRoot
try {
    foreach ($target in $Targets) {
        Write-Host "Packaging $target..." -ForegroundColor Cyan

        $targetVsCodeIgnore = $originalVsCodeIgnore.TrimEnd("`r", "`n") + "`r`n`r`n# Included by deploy/package-platforms.ps1`r`n!bin/$target/**`r`n"
        Set-Content -Path $VsCodeIgnorePath -Value $targetVsCodeIgnore -Encoding utf8NoBOM

        if ($vsceIsGlobal) {
            vsce package --target $target -o dist/
        } else {
            npx vsce package --target $target -o dist/
        }

        if ($LASTEXITCODE -ne 0) {
            throw "vsce package failed for $target"
        }

        $generatedPackages += "vscode-viberails-$target-$version.vsix"
    }
} finally {
    Set-Content -Path $VsCodeIgnorePath -Value $originalVsCodeIgnore -Encoding utf8NoBOM
    Pop-Location
}

$vsixFiles = @()
foreach ($packageName in $generatedPackages) {
    $packagePath = Join-Path $DistDir $packageName
    if (-not (Test-Path $packagePath)) {
        throw "Expected VSIX not found: $packagePath"
    }

    $vsixFiles += Get-Item -Path $packagePath
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
foreach ($vsix in $vsixFiles) {
    Write-Host "  code --install-extension dist/$($vsix.Name)" -ForegroundColor White
}
Write-Host ""
Write-Host "Or upload to GitHub releases" -ForegroundColor Cyan
