# test-lmbootstrap-gemini.ps1 - Build and test Gemini LMBootstrap mode
$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir
$vibeRailsDir = Join-Path $repoRoot "VibeRails"

Write-Host "Building VibeRails..." -ForegroundColor Yellow
dotnet build $vibeRailsDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

$exe = Join-Path $vibeRailsDir "bin\Debug\net10.0\vb.exe"

if (-not (Test-Path $exe)) {
    Write-Host "Executable not found at: $exe" -ForegroundColor Red
    exit 1
}

Write-Host "Launching Gemini in LMBootstrap mode..." -ForegroundColor Cyan
& $exe --lmbootstrap gemini
