#!/usr/bin/env pwsh
# deploy.ps1 - Build and release to GitHub
# Requires: PowerShell 7+, gh CLI, Docker (for Linux builds)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$ScriptDir = Split-Path -Parent $PSCommandPath
$RepoRoot = Split-Path -Parent $ScriptDir
$ArtifactsDir = Join-Path $ScriptDir "artifacts" "aot"
$AppConfigFile = Join-Path $RepoRoot "VibeRails" "app_config.json"
$PackageJsonFile = Join-Path $RepoRoot "vscode-viberails" "package.json"
$GithubRepo = "robstokes857/vibe-rails"

# --- Helper Functions ---

function Test-PreFlightChecks {
    Write-Host "`nRunning pre-flight checks..." -ForegroundColor Cyan

    # Check if we're in a git repository
    if (-not (git rev-parse --git-dir 2>$null)) {
        throw "Not in a git repository"
    }

    # Check current branch
    $currentBranch = git branch --show-current
    if ($currentBranch -ne "main" -and $currentBranch -ne "master") {
        throw "Must be on 'main' or 'master' branch. Currently on: $currentBranch"
    }

    # Check for uncommitted changes
    $status = git status --porcelain
    if ($status) {
        Write-Host "`nUncommitted changes detected:" -ForegroundColor Red
        git status --short
        throw "Working directory must be clean. Commit or stash changes before deploying."
    }

    # Check if branch is up to date with remote
    git fetch origin $currentBranch 2>$null
    $localCommit = git rev-parse HEAD
    $remoteCommit = git rev-parse "origin/$currentBranch" 2>$null

    if ($remoteCommit -and $localCommit -ne $remoteCommit) {
        $behind = git rev-list --count "HEAD..origin/$currentBranch" 2>$null
        if ($behind -gt 0) {
            throw "Local branch is behind remote by $behind commit(s). Run 'git pull' first."
        }
    }

    Write-Host "  ✓ On branch: $currentBranch" -ForegroundColor Green
    Write-Host "  ✓ Working directory clean" -ForegroundColor Green
    Write-Host "  ✓ Synced with remote" -ForegroundColor Green
}

function Get-LatestReleaseVersion {
    $releases = gh release list --repo $GithubRepo --limit 1 2>$null
    if (-not $releases) {
        return [version]"0.0.0"
    }
    # gh release list format: TITLE\tSTATUS\tTAG\tDATE - we need column [2]
    $tag = ($releases -split "`t")[2]
    $versionStr = $tag -replace "^v", ""
    try {
        return [version]$versionStr
    } catch {
        return [version]"0.0.0"
    }
}

function Update-AppConfigVersion {
    param([string]$Version)

    $config = Get-Content $AppConfigFile -Raw | ConvertFrom-Json
    $config.version = $Version
    $config | ConvertTo-Json -Depth 100 | Set-Content $AppConfigFile -Encoding utf8NoBOM
    Write-Host "Updated app_config.json to version $Version" -ForegroundColor Green
}

function Sync-ExtensionVersion {
    param([string]$Version)

    if (Test-Path $PackageJsonFile) {
        $packageJson = Get-Content $PackageJsonFile -Raw | ConvertFrom-Json
        $packageJson.version = $Version
        $packageJson | ConvertTo-Json -Depth 100 | Set-Content $PackageJsonFile -Encoding utf8NoBOM
        Write-Host "Synced package.json version to $Version" -ForegroundColor Green
    }
}

function New-ReleaseArchives {
    param([string]$Version)

    $winSource = Join-Path $ArtifactsDir "win-x64"
    $linuxSource = Join-Path $ArtifactsDir "linux-x64"
    $releaseDir = Join-Path $ArtifactsDir "release"

    # Clean and create release directory
    if (Test-Path $releaseDir) {
        Remove-Item -Recurse -Force $releaseDir
    }
    New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null

    # Create Windows zip
    $winZip = Join-Path $releaseDir "vb-win-x64.zip"
    Write-Host "Creating $winZip..." -ForegroundColor Cyan
    Compress-Archive -Path "$winSource\*" -DestinationPath $winZip -Force

    # Create Linux tar.gz
    $linuxTar = Join-Path $releaseDir "vb-linux-x64.tar.gz"
    Write-Host "Creating $linuxTar..." -ForegroundColor Cyan
    Push-Location $linuxSource
    tar -czf $linuxTar -C $linuxSource .
    Pop-Location

    # Generate checksums
    foreach ($file in @($winZip, $linuxTar)) {
        $hash = (Get-FileHash -Algorithm SHA256 -Path $file).Hash.ToLowerInvariant()
        $checksumFile = "$file.sha256"
        "$hash  $(Split-Path -Leaf $file)" | Set-Content -Path $checksumFile -Encoding ascii
        Write-Host "Created checksum: $checksumFile" -ForegroundColor Green
    }

    return $releaseDir
}

# --- Main ---

Write-Host @"

  ╦  ╦╦╔╗ ╔═╗  ╦═╗╔═╗╦╦  ╔═╗  ╔╦╗╔═╗╔═╗╦  ╔═╗╦ ╦
  ╚╗╔╝║╠╩╗║╣   ╠╦╝╠═╣║║  ╚═╗   ║║║╣ ╠═╝║  ║ ║╚╦╝
   ╚╝ ╩╚═╝╚═╝  ╩╚═╩ ╩╩╩═╝╚═╝  ═╩╝╚═╝╩  ╩═╝╚═╝ ╩

"@ -ForegroundColor Magenta

# Check prerequisites
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "GitHub CLI (gh) is required. Install from https://cli.github.com/"
}

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw "Docker is required for Linux builds."
}

# Run pre-flight checks
Test-PreFlightChecks

# Get current version
$currentVersion = Get-LatestReleaseVersion
Write-Host "Current release: " -NoNewline
Write-Host "v$currentVersion" -ForegroundColor Yellow

# Prompt for new version number
Write-Host "`nEnter new version (e.g., 1.1.0):"
do {
    $newVersionInput = Read-Host "Version"
    $newVersionInput = $newVersionInput.TrimStart('v')
    try {
        $newVersion = [version]$newVersionInput
        $isValid = $true
    } catch {
        Write-Host "Invalid version format. Please use X.Y.Z format (e.g., 1.1.0)" -ForegroundColor Red
        $isValid = $false
    }
} while (-not $isValid)

Write-Host "`nNew version will be: " -NoNewline
Write-Host "v$newVersion" -ForegroundColor Green

# Confirm
$confirm = Read-Host "`nProceed with release v${newVersion}? (Y/n)"
if ($confirm -and $confirm.ToLower() -ne 'y') {
    Write-Host "Aborted." -ForegroundColor Yellow
    exit 0
}

# Update versions
Update-AppConfigVersion -Version $newVersion
Sync-ExtensionVersion -Version $newVersion

# Run build
Write-Host "`nRunning build..." -ForegroundColor Cyan
$buildScript = Join-Path $ScriptDir "build.ps1"
$vibeRailsProject = Join-Path $RepoRoot "VibeRails" "VibeRails.csproj"
& $buildScript -Project $vibeRailsProject
if ($LASTEXITCODE -ne 0) {
    throw "Build failed!"
}

# Verify build outputs
$winDir = Join-Path $ArtifactsDir "win-x64"
$linuxDir = Join-Path $ArtifactsDir "linux-x64"

if (-not (Test-Path $winDir) -or -not (Test-Path $linuxDir)) {
    throw "Build outputs not found at $winDir and $linuxDir"
}

# Verify version in build outputs
$winConfig = Get-Content (Join-Path $winDir "app_config.json") -Raw | ConvertFrom-Json
$linuxConfig = Get-Content (Join-Path $linuxDir "app_config.json") -Raw | ConvertFrom-Json

if ($winConfig.version -ne $newVersion -or $linuxConfig.version -ne $newVersion) {
    throw "Version mismatch in build outputs! Expected $newVersion"
}

Write-Host "Version validation passed: $newVersion" -ForegroundColor Green

# Create release archives
$releaseDir = New-ReleaseArchives -Version $newVersion

# Commit version changes to main (NOT the binaries)
Write-Host "`nCommitting version changes..." -ForegroundColor Cyan
git add $AppConfigFile
if (Test-Path $PackageJsonFile) {
    git add $PackageJsonFile
}
git commit -m "Bump version to $newVersion"

# Push to main
Write-Host "Pushing to main..." -ForegroundColor Cyan
git push origin main

# Create GitHub release with artifacts
Write-Host "`nCreating GitHub release..." -ForegroundColor Cyan
$tag = "v$newVersion"

# Get checksums for release notes
$checksumContent = Get-ChildItem -Path $releaseDir -Filter "*.sha256" | ForEach-Object {
    Get-Content $_.FullName
} | Out-String

$releaseNotes = @"
## SHA256 Checksums

``````
$checksumContent
``````

## Installation

**Windows (PowerShell):**
``````powershell
irm https://raw.githubusercontent.com/$GithubRepo/main/Scripts/install.ps1 | iex
``````

**Linux/macOS:**
``````bash
curl -fsSL https://raw.githubusercontent.com/$GithubRepo/main/Scripts/install.sh | bash
``````
"@

# Get all release asset files
$assets = Get-ChildItem -Path $releaseDir -File | ForEach-Object { $_.FullName }

# Create release
gh release create $tag @assets --repo $GithubRepo --title "VibeRails $newVersion" --notes $releaseNotes --generate-notes

Write-Host "`nRelease created: https://github.com/$GithubRepo/releases/tag/$tag" -ForegroundColor Green
Write-Host "`nDone!" -ForegroundColor Green
