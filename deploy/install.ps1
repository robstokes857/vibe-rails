#!/usr/bin/env pwsh
# install.ps1 - Install VibeRails (vb) on Windows
# Usage: irm https://raw.githubusercontent.com/robstokes857/vibe-rails/main/Scripts/install.ps1 | iex

$ErrorActionPreference = "Stop"

$GithubRepo = "robstokes857/vibe-rails"
$InstallDir = Join-Path $env:USERPROFILE ".vibe_rails"
$AssetName = "vb-win-x64.zip"

Write-Host @"

  ╦  ╦╦╔╗ ╔═╗  ╦═╗╔═╗╦╦  ╔═╗  ╦╔╗╔╔═╗╔╦╗╔═╗╦  ╦  ╔═╗╦═╗
  ╚╗╔╝║╠╩╗║╣   ╠╦╝╠═╣║║  ╚═╗  ║║║║╚═╗ ║ ╠═╣║  ║  ║╣ ╠╦╝
   ╚╝ ╩╚═╝╚═╝  ╩╚═╩ ╩╩╩═╝╚═╝  ╩╝╚╝╚═╝ ╩ ╩ ╩╩═╝╩═╝╚═╝╩╚═

"@ -ForegroundColor Magenta

# Get latest release info
Write-Host "Fetching latest release..." -ForegroundColor Cyan
$releaseUrl = "https://api.github.com/repos/$GithubRepo/releases/latest"

try {
    $release = Invoke-RestMethod -Uri $releaseUrl -Headers @{ "User-Agent" = "VibeRails-Installer" }
} catch {
    Write-Host "Error: Could not fetch release info. Check your internet connection." -ForegroundColor Red
    Write-Host "Details: $_" -ForegroundColor Red
    exit 1
}

$version = $release.tag_name
Write-Host "Latest version: $version" -ForegroundColor Green

# Find download URLs
$zipAsset = $release.assets | Where-Object { $_.name -eq $AssetName }
$checksumAsset = $release.assets | Where-Object { $_.name -eq "$AssetName.sha256" }

if (-not $zipAsset) {
    Write-Host "Error: Could not find $AssetName in release assets." -ForegroundColor Red
    exit 1
}

$zipUrl = $zipAsset.browser_download_url
$checksumUrl = $checksumAsset.browser_download_url

# Create temp directory
$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "vibe-rails-install-$(Get-Random)"
New-Item -ItemType Directory -Force -Path $tempDir | Out-Null

try {
    # Download files
    $zipPath = Join-Path $tempDir $AssetName
    $checksumPath = Join-Path $tempDir "$AssetName.sha256"

    Write-Host "Downloading $AssetName..." -ForegroundColor Cyan
    Invoke-WebRequest -Uri $zipUrl -OutFile $zipPath -UseBasicParsing

    if ($checksumUrl) {
        Write-Host "Downloading checksum..." -ForegroundColor Cyan
        Invoke-WebRequest -Uri $checksumUrl -OutFile $checksumPath -UseBasicParsing

        # Verify checksum
        Write-Host "Verifying checksum..." -ForegroundColor Cyan
        $expectedHash = (Get-Content $checksumPath -Raw).Split()[0].Trim()
        $actualHash = (Get-FileHash -Algorithm SHA256 -Path $zipPath).Hash.ToLowerInvariant()

        if ($expectedHash -ne $actualHash) {
            Write-Host "Error: Checksum verification failed!" -ForegroundColor Red
            Write-Host "Expected: $expectedHash" -ForegroundColor Red
            Write-Host "Actual:   $actualHash" -ForegroundColor Red
            exit 1
        }
        Write-Host "Checksum verified!" -ForegroundColor Green
    }

    # Create install directory
    if (Test-Path $InstallDir) {
        Write-Host "Removing existing installation..." -ForegroundColor Yellow
        Remove-Item -Recurse -Force $InstallDir
    }
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null

    # Extract
    Write-Host "Extracting to $InstallDir..." -ForegroundColor Cyan
    Expand-Archive -Path $zipPath -DestinationPath $InstallDir -Force

    # Add to PATH
    $currentPath = [Environment]::GetEnvironmentVariable("Path", "User")
    if ($currentPath -notlike "*$InstallDir*") {
        Write-Host "Adding to PATH..." -ForegroundColor Cyan
        $newPath = "$currentPath;$InstallDir"
        [Environment]::SetEnvironmentVariable("Path", $newPath, "User")
        Write-Host "Added $InstallDir to user PATH" -ForegroundColor Green
    } else {
        Write-Host "$InstallDir is already in PATH" -ForegroundColor Green
    }

    # Also update current session
    $env:Path = "$env:Path;$InstallDir"

    Write-Host ""
    Write-Host "Installation complete!" -ForegroundColor Green
    Write-Host ""
    Write-Host "Installed to: $InstallDir" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "To get started, open a NEW terminal and run:" -ForegroundColor Yellow
    Write-Host "  vb --help" -ForegroundColor White
    Write-Host ""

} finally {
    # Cleanup
    if (Test-Path $tempDir) {
        Remove-Item -Recurse -Force $tempDir -ErrorAction SilentlyContinue
    }
}
