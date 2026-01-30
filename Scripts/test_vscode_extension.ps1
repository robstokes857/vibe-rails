# Test script for VS Code VibeRails extension
# This builds and installs the extension, then opens VS Code

$repoRoot = Split-Path -Parent $PSScriptRoot
$extensionDir = Join-Path $repoRoot "vscode-viberails"

Write-Host "=== Building VibeRails .NET backend ===" -ForegroundColor Cyan
Push-Location (Join-Path $repoRoot "VibeRails")

# Try to kill any processes locking the build files
$vbProcesses = Get-Process -Name "vb" -ErrorAction SilentlyContinue
if ($vbProcesses) {
    Write-Host "Killing vb processes that may be locking files..." -ForegroundColor Yellow
    $vbProcesses | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
}

$buildResult = dotnet build 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "WARNING: .NET build failed. Extension will use 'dotnet run' fallback." -ForegroundColor Yellow
    Write-Host $buildResult -ForegroundColor DarkGray
} else {
    Write-Host "Build successful!" -ForegroundColor Green
}
Pop-Location

$ErrorActionPreference = "Stop"

Write-Host "`n=== Compiling VS Code extension ===" -ForegroundColor Cyan
Push-Location $extensionDir
npm run compile

Write-Host "`n=== Packaging extension ===" -ForegroundColor Cyan
npx vsce package --allow-missing-repository

Write-Host "`n=== Installing extension ===" -ForegroundColor Cyan
$vsix = Get-ChildItem -Path $extensionDir -Filter "*.vsix" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
code --install-extension $vsix.FullName --force

Pop-Location

Write-Host "`n=== Done! ===" -ForegroundColor Green
Write-Host "`nNow reload VS Code to use the updated extension:" -ForegroundColor Yellow
Write-Host "  Press Ctrl+Shift+P -> 'Developer: Reload Window' -> Enter" -ForegroundColor Cyan
Write-Host "`nThen click the 'VibeRails' button to open the dashboard" -ForegroundColor Cyan
