#!/usr/bin/env pwsh
# local_deploy.ps1 - Build and deploy VibeRails locally for testing
# Publishes a Release AOT build to deploy/artifacts/aot/win-x64 then copies to ~/.vibe_rails

$ErrorActionPreference = "Stop"

$SkipBuild = $args | Where-Object { $_ -match "skip" -and $_ -match "build" }

$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$Project = Join-Path $RepoRoot "VibeRails" "VibeRails.csproj"
$PublishDir = Join-Path $RepoRoot "deploy" "artifacts" "aot" "win-x64"
$InstallDir = Join-Path $env:USERPROFILE ".vibe_rails"

Write-Host ""
Write-Host "  Local Deploy - VibeRails" -ForegroundColor Magenta
Write-Host "  Project:    $Project" -ForegroundColor Cyan
Write-Host "  Build dir:  $PublishDir" -ForegroundColor Cyan
Write-Host "  Target dir: $InstallDir" -ForegroundColor Cyan
Write-Host ""

# Kill any running vb processes (they hold file locks)
$vbProcs = Get-Process -Name "vb" -ErrorAction SilentlyContinue
if ($vbProcs) {
    Write-Host "Stopping running vb processes..." -ForegroundColor Yellow
    $vbProcs | Stop-Process -Force
    Start-Sleep -Seconds 1
}

# Publish AOT build to the standard artifacts location
if ($SkipBuild) {
    Write-Host "Skipping build." -ForegroundColor Yellow
} else {
    Write-Host "Publishing Release AOT build..." -ForegroundColor Cyan
    dotnet publish $Project -c Release -r win-x64 --self-contained true -o $PublishDir /p:PublishAot=true /p:StripSymbols=true /p:InvariantGlobalization=true
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Build failed!" -ForegroundColor Red
        exit 1
    }
    Write-Host "Build succeeded." -ForegroundColor Green
}

# Ensure install dir exists
if (-not (Test-Path $InstallDir)) {
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
}

# Copy published files to install dir (overwrite app files, preserve user data)
Write-Host "Deploying to $InstallDir..." -ForegroundColor Cyan
Copy-Item -Path (Join-Path $PublishDir "*") -Destination $InstallDir -Recurse -Force

Write-Host ""
Write-Host "Deploy complete!" -ForegroundColor Green
Write-Host "Run 'vb --launch-web' to test." -ForegroundColor Yellow
Write-Host ""
