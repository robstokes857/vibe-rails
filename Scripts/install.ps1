#!/usr/bin/env pwsh
# install.ps1 - Install VibeRails (vb) on Windows
# Usage: irm https://raw.githubusercontent.com/robstokes857/vibe-rails/main/Scripts/install.ps1 | iex

$ErrorActionPreference = "Stop"

$GithubRepo = "robstokes857/vibe-rails"
$InstallDir = Join-Path $env:USERPROFILE ".vibe_rails"
$AssetName = "vb-win-x64.zip"

function Install-BertV2ModelAssets {
    param(
        [string]$RootDir
    )

    $bundledDir = Join-Path $RootDir "Models\BertV2"
    $bundledModelArchive = Join-Path $bundledDir "model.onnx.zip"
    $bundledVocab = Join-Path $bundledDir "vocab.txt"

    $runtimeDir = Join-Path $RootDir "models\bertv2"
    $runtimeModel = Join-Path $runtimeDir "model.onnx"
    $runtimeVocab = Join-Path $runtimeDir "vocab.txt"

    if ((Test-Path $runtimeModel) -and (Test-Path $runtimeVocab)) {
        Write-Host "BertV2 model assets already installed, skipping." -ForegroundColor Green
        return
    }

    if (-not (Test-Path $bundledModelArchive)) {
        throw "Bundled BertV2 model archive not found at $bundledModelArchive. The release package is incomplete."
    }
    if (-not (Test-Path $bundledVocab)) {
        throw "Bundled BertV2 vocab not found at $bundledVocab. The release package is incomplete."
    }

    New-Item -ItemType Directory -Force -Path $runtimeDir | Out-Null

    if (-not (Test-Path $runtimeVocab)) {
        Write-Host "Installing BertV2 vocab..." -ForegroundColor Cyan
        Copy-Item -Path $bundledVocab -Destination $runtimeVocab -Force
    }

    if (-not (Test-Path $runtimeModel)) {
        $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "vibe_rails_bertv2_$(Get-Random)"
        New-Item -ItemType Directory -Force -Path $tempDir | Out-Null
        try {
            Write-Host "Extracting BertV2 model..." -ForegroundColor Cyan
            Expand-Archive -Path $bundledModelArchive -DestinationPath $tempDir -Force

            $extractedModelPath = Join-Path $tempDir "model.onnx"
            if (-not (Test-Path $extractedModelPath)) {
                throw "BertV2 model archive did not contain model.onnx"
            }

            Move-Item -Path $extractedModelPath -Destination $runtimeModel -Force
        } finally {
            if (Test-Path $tempDir) {
                Remove-Item -Recurse -Force $tempDir -ErrorAction SilentlyContinue
            }
        }
    }

    Write-Host "BertV2 model assets installed to $runtimeDir" -ForegroundColor Green
}

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
$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "vibe_rails_install_$(Get-Random)"
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

    # Create install directory if it doesn't exist
    if (-not (Test-Path $InstallDir)) {
        New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    }

    # Extract (overwrites app files, preserves user data like state.db, envs/, etc.)
    Write-Host "Extracting to $InstallDir..." -ForegroundColor Cyan
    Expand-Archive -Path $zipPath -DestinationPath $InstallDir -Force

    Install-BertV2ModelAssets -RootDir $InstallDir

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
