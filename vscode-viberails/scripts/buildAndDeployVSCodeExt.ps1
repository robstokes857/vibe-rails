#!/usr/bin/env pwsh
# buildAndDeployVSCodeExt.ps1 - Interactive release script
# Prompts for version + VS PAT, then always packages and publishes.
# Supports non-interactive execution via parameters.

param(
    [string]$Version,
    [string]$Pat,
    [switch]$SkipVersionUpdate
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

    Invoke-CheckedCommand -Command "npx" -Arguments @("vsce") + $Arguments
}

function ConvertTo-PlainText {
    param(
        [Parameter(Mandatory = $true)]
        [SecureString]$SecureValue
    )

    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureValue)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    } finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
}

function Get-PackageVersion {
    param([string]$PackageJsonPath)
    return [string]((Get-Content -Path $PackageJsonPath -Raw | ConvertFrom-Json).version)
}

$ScriptDir = Split-Path -Parent $PSCommandPath
$ExtensionRoot = Split-Path -Parent $ScriptDir
$PackageJsonPath = Join-Path $ExtensionRoot "package.json"
$DistDir = Join-Path $ExtensionRoot "dist"

Write-Host "VibeRails Extension - Release" -ForegroundColor Cyan
Write-Host "=============================" -ForegroundColor Cyan
Write-Host ""

if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
    throw "'npm' was not found on PATH. Install Node.js first."
}

Push-Location $ExtensionRoot
try {
    $currentVersion = Get-PackageVersion -PackageJsonPath $PackageJsonPath
    Write-Host "Current version: $currentVersion" -ForegroundColor Green
    Write-Host ""

    $newVersion = if ($Version) { $Version.Trim() } else { (Read-Host "Enter version to publish (example: 1.4.0)").Trim() }
    if ([string]::IsNullOrWhiteSpace($newVersion)) {
        throw "Version is required."
    }
    if ($newVersion -notmatch '^\d+\.\d+\.\d+([\-+][0-9A-Za-z\.-]+)?$') {
        throw "Version must be semver-like (examples: 1.4.0, 1.4.0-beta.1)."
    }

    if (-not $Pat) {
        $Pat = [Environment]::GetEnvironmentVariable("VS_PAT")
    }
    if (-not $Pat) {
        $Pat = [Environment]::GetEnvironmentVariable("VSCE_PAT")
    }
    if (-not $Pat) {
        $securePat = Read-Host -Prompt "Enter VS_PAT (input hidden)" -AsSecureString
        $Pat = ConvertTo-PlainText -SecureValue $securePat
    }

    $pat = $Pat
    if ([string]::IsNullOrWhiteSpace($pat)) {
        throw "VS_PAT (or VSCE_PAT) is required."
    }

    $env:VSCE_PAT = $pat
    try {
        if ($SkipVersionUpdate) {
            Write-Host "Skipping version update (-SkipVersionUpdate)." -ForegroundColor Yellow
        } elseif ($currentVersion -eq $newVersion) {
            Write-Host "Version already set to $newVersion; skipping npm version." -ForegroundColor Yellow
        } else {
            Write-Host "Updating version to $newVersion..." -ForegroundColor Cyan
            Invoke-CheckedCommand -Command "npm" -Arguments @("version", $newVersion, "--no-git-tag-version")
            $currentVersion = Get-PackageVersion -PackageJsonPath $PackageJsonPath
        }

        $currentVersion = Get-PackageVersion -PackageJsonPath $PackageJsonPath

        Write-Host "Packaging extension..." -ForegroundColor Cyan
        Invoke-CheckedCommand -Command "npm" -Arguments @("run", "package")

        $vsixFiles = @()
        if (Test-Path $DistDir) {
            $vsixFiles = @(Get-ChildItem -Path $DistDir -File -Filter "vscode-viberails-$currentVersion.vsix" | Sort-Object Name)
        }

        if ($vsixFiles.Count -eq 0) {
            throw "No VSIX files found for version $currentVersion in dist/. Run packaging first."
        }

        Write-Host "Publishing $($vsixFiles.Count) package(s)..." -ForegroundColor Cyan
        $publishArgs = @("publish", "--packagePath") + ($vsixFiles | ForEach-Object { $_.FullName }) + @("--skip-duplicate")
        Invoke-Vsce -Arguments $publishArgs

        Write-Host ""
        Write-Host "Release published successfully." -ForegroundColor Green
        Write-Host "Generated VSIX files:" -ForegroundColor Cyan
        foreach ($file in $vsixFiles) {
            $sizeMB = [math]::Round(($file.Length / 1MB), 2)
            Write-Host "  - $($file.Name) ($sizeMB MB)" -ForegroundColor White
        }
    } finally {
        $env:VSCE_PAT = $null
    }
} finally {
    Pop-Location
}
