#!/usr/bin/env pwsh
# debug_ubuntu.ps1
# Builds and runs VibeRails in an Ubuntu container with debugger support
# Allows VS Code to attach for remote debugging

[CmdletBinding()]
param(
    [string] $Project = "VibeRails/VibeRails.csproj",
    [string] $Configuration = "Debug",
    [string] $Framework = "net10.0",
    [string] $Rid = "linux-x64",
    [string] $DockerImage = "mcr.microsoft.com/dotnet/sdk:10.0",
    [int] $DebugPort = 4024
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# Determine repo root
$scriptDir = Split-Path -Parent $PSCommandPath
$repoRoot = Split-Path -Parent $scriptDir

Write-Host "`n==> Building $Project for debugging in Ubuntu container" -ForegroundColor Cyan
Write-Host "    Configuration: $Configuration" -ForegroundColor Gray
Write-Host "    RID: $Rid" -ForegroundColor Gray
Write-Host "    Debug Port: $DebugPort" -ForegroundColor Gray

# Build the project
$buildOutput = Join-Path $repoRoot "Scripts/debug/$Rid"
Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $buildOutput
New-Item -ItemType Directory -Force -Path $buildOutput | Out-Null

$projectPath = Join-Path $repoRoot $Project

Write-Host "`n==> Building project..." -ForegroundColor Cyan
& dotnet publish $projectPath `
    -c $Configuration `
    -f $Framework `
    -r $Rid `
    --self-contained true `
    -o $buildOutput `
    /p:PublishAot=false `
    /p:DebugType=portable

if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE"
}

Write-Host "`n==> Starting Ubuntu container with debugger support..." -ForegroundColor Cyan
Write-Host "    Container will expose port $DebugPort for remote debugging" -ForegroundColor Gray
Write-Host "    Use VS Code 'Attach to Docker' configuration to connect" -ForegroundColor Yellow

# Run container interactively with debugger port exposed
$containerName = "viberails-debug-$(Get-Date -Format 'yyyyMMdd-HHmmss')"

# Mount at /app for simplicity
$containerWorkDir = "/app"
$containerBuildOutput = "$containerWorkDir/Scripts/debug/$Rid"

# Build the bash command as a single line to avoid line ending issues
$bashCommand = "echo '==> Installing vsdbg (VS Code debugger)...' && " +
    "curl -sSL https://aka.ms/getvsdbgsh | bash /dev/stdin -v latest -l /vsdbg && " +
    "echo '' && " +
    "echo '==> Debugger installed. Starting application with debug support...' && " +
    "echo '    Attach VS Code debugger to localhost:$DebugPort' && " +
    "echo '    Container: $containerName' && " +
    "echo '' && " +
    "./vb && " +
    "echo '' && " +
    "echo 'Application exited.'"

$dockerArgs = @(
    "run", "--rm", "-i"
    "--name", $containerName
    "-v", "${repoRoot}:${containerWorkDir}"
    "-w", $containerBuildOutput
    "-p", "${DebugPort}:${DebugPort}"
    "-e", "DOTNET_DebuggerWorkerHostPath=/usr/share/dotnet/dotnet"
    $DockerImage
    "bash", "-c", $bashCommand
)

Write-Host "`nExecuting: docker $($dockerArgs -join ' ')" -ForegroundColor DarkGray
& docker @dockerArgs

Write-Host "`n==> Container exited." -ForegroundColor Green
