#!/usr/bin/env pwsh
# test-linux.ps1
# Smoke-test the Linux AOT binary by running it inside an Ubuntu Docker container.

[CmdletBinding()]
param(
    # Where your linux publish output lives
    [string] $LinuxOutDir = "Scripts/artifacts/aot/linux-x64",

    # Optional: explicit binary name (if you want to override auto-detect)
    [string] $BinaryName = "",

    # Ubuntu base to test against (noble = 24.04)
    [string] $UbuntuImage = "ubuntu:24.04",

    # Optional args to pass to your CLI (example: "--help")
    [string[]] $Args = @("--help")
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Require-Cmd([string]$name) {
    if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
        throw "Required command not found on PATH: $name"
    }
}

function Find-LinuxBinary([string]$dir, [string]$binaryName) {
    $fullDir = (Resolve-Path $dir).Path

    if ($binaryName) {
        $p = Join-Path $fullDir $binaryName
        if (-not (Test-Path -LiteralPath $p)) { throw "Binary not found: $p" }
        return $p
    }

    $files = Get-ChildItem -Path $fullDir -File

    # Prefer "no extension" file (common for linux publish)
    $noExt = $files | Where-Object { $_.Extension -eq "" } | Sort-Object Length -Descending | Select-Object -First 1
    if ($noExt) { return $noExt.FullName }

    # Fallback: largest file
    $largest = $files | Sort-Object Length -Descending | Select-Object -First 1
    if (-not $largest) { throw "No files found in: $fullDir" }
    return $largest.FullName
}

Require-Cmd docker

# If LinuxOutDir is relative, resolve from script's parent directory (repo root)
$scriptDir = Split-Path -Parent $PSCommandPath
$repoRoot = Split-Path -Parent $scriptDir

if (-not [IO.Path]::IsPathRooted($LinuxOutDir)) {
    $LinuxOutDir = Join-Path $repoRoot $LinuxOutDir
}

$binPath = Find-LinuxBinary -dir $LinuxOutDir -binaryName $BinaryName
$binName = [IO.Path]::GetFileName($binPath)

Write-Host "Linux binary: $binPath" -ForegroundColor Cyan
Write-Host "Ubuntu image: $UbuntuImage" -ForegroundColor Cyan
Write-Host "Args: $($Args -join ' ')" -ForegroundColor Cyan

# Run inside Ubuntu: copy the binary into /app, ensure executable, run it
# We mount only the linux output dir read-only.
$linuxDirFull = (Resolve-Path $LinuxOutDir).Path

# Build bash command as a single line with && separators
$bashScript = "set -euo pipefail && mkdir -p /app && cp '/in/$binName' '/app/$binName' && chmod +x '/app/$binName' && '/app/$binName' $($Args -join ' ')"

$runCmd = @(
    "run", "--rm",
    "-v", "${linuxDirFull}:/in:ro",
    $UbuntuImage,
    "bash", "-c", $bashScript
)

Write-Host "`n==> docker run --rm -v ${linuxDirFull}:/in:ro $UbuntuImage bash -c `"$bashScript`"" -ForegroundColor DarkGray
& docker @runCmd

Write-Host "`n✅ Ubuntu smoke test passed." -ForegroundColor Green
