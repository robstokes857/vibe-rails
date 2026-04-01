#!/usr/bin/env pwsh

param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [switch]$SkipNpmInstall,
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Command,
        [Parameter(Mandatory = $false)]
        [string[]]$Arguments = @(),
        [Parameter(Mandatory = $false)]
        [string]$WorkingDirectory = ""
    )

    if ($WorkingDirectory) {
        Push-Location $WorkingDirectory
    }

    try {
        & $Command @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "Command failed: $Command $($Arguments -join ' ')"
        }
    } finally {
        if ($WorkingDirectory) {
            Pop-Location
        }
    }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$extensionRoot = Join-Path $repoRoot "vscode-viberails"
$projectPath = Join-Path $repoRoot "VibeRails" "VibeRails.csproj"
$artifactDir = Join-Path $repoRoot "Scripts" "artifacts" "aot" "win-x64"
$prepareScript = Join-Path $repoRoot "deploy" "prepare-binaries.ps1"

Write-Host "VibeRails VS Code extension smoke test" -ForegroundColor Cyan
Write-Host "======================================" -ForegroundColor Cyan
Write-Host ""

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "'dotnet' was not found on PATH."
}

if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
    throw "'npm' was not found on PATH."
}

if (-not (Get-Command code.cmd -ErrorAction SilentlyContinue)) {
    throw "'code.cmd' was not found on PATH. Install Visual Studio Code and add its CLI to PATH before running this smoke test."
}

if (-not (Get-Command codex -ErrorAction SilentlyContinue)) {
    throw "'codex' was not found on PATH. Install the Codex CLI before running this smoke test."
}

if (-not $SkipNpmInstall) {
    Write-Host "Installing extension dependencies..." -ForegroundColor Cyan
    Invoke-CheckedCommand -Command "npm" -Arguments @("install") -WorkingDirectory $extensionRoot
}

if (-not $SkipPublish) {
    Write-Host "Publishing backend for extension bundle..." -ForegroundColor Cyan
    if (-not (Test-Path $artifactDir)) {
        New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null
    }

    Invoke-CheckedCommand -Command "dotnet" -Arguments @(
        "publish",
        $projectPath,
        "-c", $Configuration,
        "-r", "win-x64",
        "--self-contained", "false",
        "-o", $artifactDir
    ) -WorkingDirectory $repoRoot
}

Write-Host "Preparing bundled backend assets..." -ForegroundColor Cyan
Invoke-CheckedCommand -Command "pwsh" -Arguments @(
    "-ExecutionPolicy", "Bypass",
    "-File", $prepareScript,
    "-Targets", "win32-x64"
)

Write-Host "Running VS Code smoke test..." -ForegroundColor Cyan
$env:VIBERAILS_VSCODE_CLI = (Get-Command code.cmd).Source
try {
    Invoke-CheckedCommand -Command "npm" -Arguments @("test") -WorkingDirectory $extensionRoot
} finally {
    $env:VIBERAILS_VSCODE_CLI = $null
}

Write-Host ""
Write-Host "Smoke test passed." -ForegroundColor Green
