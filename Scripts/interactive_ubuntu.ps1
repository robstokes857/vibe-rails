#!/usr/bin/env pwsh
# interactive_ubuntu.ps1
# Build Linux binary via Docker, then launch an interactive Ubuntu container with the binary available

[CmdletBinding()]
param(
    # Where your linux publish output lives
    [string] $LinuxOutDir = "Scripts/artifacts/aot/linux-x64",

    # Optional: explicit binary name (if you want to override auto-detect)
    [string] $BinaryName = "",

    # Ubuntu base (noble = 24.04)
    [string] $UbuntuImage = "ubuntu:24.04",

    # Project to build (auto-detected if not specified)
    [string] $Project = ""
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
Require-Cmd dotnet

# Determine paths
$scriptDir = Split-Path -Parent $PSCommandPath
$repoRoot = Split-Path -Parent $scriptDir

if (-not [IO.Path]::IsPathRooted($LinuxOutDir)) {
    $LinuxOutDir = Join-Path $repoRoot $LinuxOutDir
}

# Step 1: Build Linux binary using build_linux_only.ps1 or inline build
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Building Linux Binary via Docker" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

$buildScript = Join-Path $scriptDir "build_linux_only.ps1"
if (Test-Path $buildScript) {
    & $buildScript -Project $Project
} else {
    # Inline Linux-only build if build_linux_only.ps1 doesn't exist
    Write-Host "Building Linux binary inline..." -ForegroundColor Yellow

    # Find project
    if (-not $Project) {
        # Prefer VibeRails.csproj in the VibeRails folder, exclude PtyNet submodule
        $csproj = Get-ChildItem -Path $repoRoot -Recurse -Filter *.csproj -File |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj|Scripts|artifacts|Tests|MCP|ToonConvert|PtyNet)[\\/]' } |
            Where-Object { $_.Name -eq "VibeRails.csproj" } |
            Select-Object -First 1

        # Fallback to any csproj if VibeRails.csproj not found
        if (-not $csproj) {
            $csproj = Get-ChildItem -Path $repoRoot -Recurse -Filter *.csproj -File |
                Where-Object { $_.FullName -notmatch '[\\/](bin|obj|Scripts|artifacts|Tests|MCP|ToonConvert|PtyNet)[\\/]' } |
                Sort-Object FullName |
                Select-Object -First 1
        }
        if (-not $csproj) { throw "No .csproj found. Pass -Project parameter." }
        $Project = $csproj.FullName
    }

    # Clean and create output directory
    if (Test-Path $LinuxOutDir) {
        Remove-Item -Recurse -Force $LinuxOutDir
    }
    New-Item -ItemType Directory -Force -Path $LinuxOutDir | Out-Null

    $projectRel = [IO.Path]::GetRelativePath($repoRoot, $Project).Replace("\","/")
    $dockerImage = "mcr.microsoft.com/dotnet/sdk:10.0-aot"

    $dotnetArgs = @(
        "publish", "/src/$projectRel",
        "-c", "Release",
        "-f", "net10.0",
        "-r", "linux-x64",
        "--self-contained", "true",
        "-o", "/out",
        "/p:PublishAot=true",
        "/p:StripSymbols=true",
        "/p:InvariantGlobalization=true"
    )

    $dockerArgs = @(
        "run","--rm",
        "-v","${repoRoot}:/src",
        "-v","$((Resolve-Path $LinuxOutDir).Path):/out",
        "-w","/src",
        $dockerImage,
        "dotnet"
    ) + $dotnetArgs

    Write-Host "==> docker $($dockerArgs -join ' ')" -ForegroundColor DarkGray
    & docker @dockerArgs
    if ($LASTEXITCODE -ne 0) { throw "Linux build failed" }
}

Write-Host "`n✅ Build complete!" -ForegroundColor Green
Write-Host ""

# Step 2: Find the binary
$binPath = Find-LinuxBinary -dir $LinuxOutDir -binaryName $BinaryName
$binName = [IO.Path]::GetFileName($binPath)
$linuxDirFull = (Resolve-Path $LinuxOutDir).Path

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Interactive Ubuntu Container" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Binary: $binPath" -ForegroundColor Green
Write-Host "Ubuntu: $UbuntuImage" -ForegroundColor Green
Write-Host ""
Write-Host "Your binary is available at: /app/$binName" -ForegroundColor Yellow
Write-Host ""
Write-Host "Quick start commands:" -ForegroundColor Cyan
Write-Host "  cd /app" -ForegroundColor White
Write-Host "  ./$binName         # Run without args (interactive menu)" -ForegroundColor White
Write-Host "  ./$binName --help  # Run with args" -ForegroundColor White
Write-Host "  exit               # Exit the container" -ForegroundColor White
Write-Host ""

# Setup script that runs when container starts (use LF line endings for bash)
# Copy ALL files from /in to /app (includes native libs like libe_sqlite3.so, wwwroot, etc.)
$setupScript = "set -e`nmkdir -p /app`ncp -r /in/* /app/`nchmod +x '/app/$binName'`ncd /app`nexec bash"

$dockerArgs = @(
    "run", "--rm", "-it",
    "-v", "${linuxDirFull}:/in:ro",
    "-w", "/app",
    $UbuntuImage,
    "bash", "-c", $setupScript
)

Write-Host "==> Launching container..." -ForegroundColor DarkGray
& docker @dockerArgs

Write-Host ""
Write-Host "Container exited." -ForegroundColor Green
