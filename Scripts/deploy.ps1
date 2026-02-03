#!/usr/bin/env pwsh
# deploy.ps1 - Build and release to GitHub with interactive version bumping
# Requires: PowerShell 7+, gh CLI, Docker (for Linux builds)

[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$ScriptDir = Split-Path -Parent $PSCommandPath
$RepoRoot = Split-Path -Parent $ScriptDir
$ArtifactsDir = Join-Path $ScriptDir "artifacts" "aot"
$AppConfigFile = Join-Path $RepoRoot "VibeRails" "app_config.json"
$PackageJsonFile = Join-Path $RepoRoot "vscode-viberails" "package.json"
$GithubRepo = "robstokes857/vibe-rails"

# Track state for rollback
$script:TagCreated = $false
$script:TagPushed = $false
$script:VersionCommitPushed = $false
$script:ReleaseCreated = $false
$script:OriginalVersionContent = $null
$script:CurrentTag = $null

# --- Helper Functions ---

function Get-LatestReleaseVersion {
    $releases = gh release list --repo $GithubRepo --limit 1 2>$null
    if (-not $releases) {
        return [version]"0.0.0"
    }
    # Parse version from release tag (e.g., "v1.0.0" -> "1.0.0")
    # gh release list format: TITLE\tSTATUS\tTAG\tDATE - we need column [2]
    $tag = ($releases -split "`t")[2]
    $versionStr = $tag -replace "^v", ""
    try {
        return [version]$versionStr
    } catch {
        return [version]"0.0.0"
    }
}

function Get-BumpedVersion {
    param(
        [version]$Current,
        [string]$BumpType
    )
    switch ($BumpType.ToLower()) {
        "major" { return [version]"$($Current.Major + 1).0.0" }
        "minor" { return [version]"$($Current.Major).$($Current.Minor + 1).0" }
        "patch" { return [version]"$($Current.Major).$($Current.Minor).$($Current.Build + 1)" }
        default { throw "Invalid bump type: $BumpType" }
    }
}

function Get-CurrentVersionFromConfig {
    if (-not (Test-Path $AppConfigFile)) {
        throw "app_config.json not found at $AppConfigFile"
    }

    $config = Get-Content $AppConfigFile -Raw | ConvertFrom-Json
    return $config.version
}

function Update-AppConfigVersion {
    param([string]$Version)

    if (-not (Test-Path $AppConfigFile)) {
        throw "app_config.json not found at $AppConfigFile"
    }

    $config = Get-Content $AppConfigFile -Raw | ConvertFrom-Json
    $config.version = $Version
    $config | ConvertTo-Json -Depth 100 | Set-Content $AppConfigFile -Encoding utf8NoBOM
    Write-Host "Updated $AppConfigFile to version $Version" -ForegroundColor Green
}

function Sync-ExtensionVersion {
    param([string]$Version)

    if (-not (Test-Path $PackageJsonFile)) {
        Write-Host "Warning: package.json not found at $PackageJsonFile" -ForegroundColor Yellow
        return
    }

    $packageJson = Get-Content $PackageJsonFile -Raw | ConvertFrom-Json
    $packageJson.version = $Version
    $packageJson | ConvertTo-Json -Depth 100 | Set-Content $PackageJsonFile -Encoding utf8NoBOM
    Write-Host "Synced package.json version to $Version" -ForegroundColor Green
}

function Build-ExtensionPackages {
    param([string]$Version)

    $extensionDir = Join-Path $RepoRoot "vscode-viberails"

    if (-not (Test-Path $extensionDir)) {
        Write-Host "Warning: vscode-viberails directory not found. Skipping extension packaging." -ForegroundColor Yellow
        return @()
    }

    Write-Host "`nPreparing extension binaries..." -ForegroundColor Cyan
    Push-Location $extensionDir

    try {
        # Check if npm is available
        if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
            Write-Host "Warning: npm not found. Skipping extension packaging." -ForegroundColor Yellow
            return @()
        }

        # Prepare binaries
        npm run prepare-binaries
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to prepare extension binaries"
        }

        # Compile TypeScript
        Write-Host "Compiling TypeScript..." -ForegroundColor Cyan
        npm run compile
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to compile extension TypeScript"
        }

        # Check if vsce is available
        if (-not (Get-Command vsce -ErrorAction SilentlyContinue)) {
            Write-Host "Warning: vsce not found. Install with: npm install -g @vscode/vsce" -ForegroundColor Yellow
            Write-Host "Skipping extension packaging." -ForegroundColor Yellow
            return @()
        }

        # Ensure dist directory exists
        $distDir = Join-Path $extensionDir "dist"
        if (-not (Test-Path $distDir)) {
            New-Item -ItemType Directory -Force -Path $distDir | Out-Null
        }

        # Package extensions
        Write-Host "Packaging extensions..." -ForegroundColor Cyan
        npm run package:win32-x64
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to package win32-x64 extension"
        }

        npm run package:linux-x64
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to package linux-x64 extension"
        }

        # Return paths to .vsix files
        $distDir = Join-Path $extensionDir "dist"
        return @(Get-ChildItem -Path $distDir -Filter "*.vsix" -File)
    } finally {
        Pop-Location
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

    # Use tar command (available on Windows 10+)
    Push-Location $linuxSource
    tar -czf $linuxTar -C $linuxSource .
    Pop-Location

    # Generate checksums
    $files = @($winZip, $linuxTar)
    foreach ($file in $files) {
        $hash = (Get-FileHash -Algorithm SHA256 -Path $file).Hash.ToLowerInvariant()
        $checksumFile = "$file.sha256"
        "$hash  $(Split-Path -Leaf $file)" | Set-Content -Path $checksumFile -Encoding ascii
        Write-Host "Created checksum: $checksumFile" -ForegroundColor Green
    }

    return $releaseDir
}

function New-GitHubRelease {
    param(
        [string]$Version,
        [string]$ReleaseDir,
        [System.IO.FileInfo[]]$ExtensionPackages = @()
    )

    $tag = "v$Version"
    $script:CurrentTag = $tag
    $title = "VibeRails $Version"

    # Get release assets (core packages)
    $assets = Get-ChildItem -Path $ReleaseDir -File | ForEach-Object { $_.FullName }

    # Add extension packages if available
    if ($ExtensionPackages.Count -gt 0) {
        $assets += $ExtensionPackages | ForEach-Object { $_.FullName }
    }

    # Build release notes from checksums
    $checksumContent = Get-ChildItem -Path $ReleaseDir -Filter "*.sha256" | ForEach-Object {
        Get-Content $_.FullName
    }

    $notes = @"
## SHA256 Checksums

``````
$($checksumContent -join "`n")
``````

## Installation

**VS Code Extension (Recommended):**
Download the platform-specific .vsix file and install:
``````bash
code --install-extension vscode-viberails-$Version-win32-x64.vsix
``````

**Standalone CLI:**

**Windows (PowerShell):**
``````powershell
irm https://raw.githubusercontent.com/$GithubRepo/main/Scripts/install.ps1 | iex
``````

**Linux/macOS:**
``````bash
curl -fsSL https://raw.githubusercontent.com/$GithubRepo/main/Scripts/install.sh | bash
``````
"@

    Write-Host "`nCreating GitHub release $tag..." -ForegroundColor Cyan

    # Create local tag
    git tag -a $tag -m "Release $tag"
    $script:TagCreated = $true

    # Push tag to remote
    git push origin $tag
    $script:TagPushed = $true

    # Create release with assets (files are positional args after the tag)
    gh release create $tag @assets --repo $GithubRepo --title $title --notes $notes --generate-notes
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to create GitHub release"
    }
    $script:ReleaseCreated = $true

    Write-Host "`nRelease created: https://github.com/$GithubRepo/releases/tag/$tag" -ForegroundColor Green
}

function Invoke-Rollback {
    param([string]$ErrorMessage)

    Write-Host "`n$ErrorMessage" -ForegroundColor Red
    Write-Host "Rolling back changes..." -ForegroundColor Yellow

    # Delete GitHub release if created
    if ($script:ReleaseCreated -and $script:CurrentTag) {
        Write-Host "  Deleting GitHub release..." -ForegroundColor Yellow
        gh release delete $script:CurrentTag -y --repo $GithubRepo 2>$null
    }

    # Delete remote tag if pushed
    if ($script:TagPushed -and $script:CurrentTag) {
        Write-Host "  Deleting remote tag..." -ForegroundColor Yellow
        git push --delete origin $script:CurrentTag 2>$null
    }

    # Delete local tag if created
    if ($script:TagCreated -and $script:CurrentTag) {
        Write-Host "  Deleting local tag..." -ForegroundColor Yellow
        git tag -d $script:CurrentTag 2>$null
    }

    # Revert version commit if pushed
    if ($script:VersionCommitPushed) {
        Write-Host "  Reverting version commit..." -ForegroundColor Yellow
        git revert --no-commit HEAD 2>$null
        git checkout HEAD -- $VersionFile 2>$null
    }

    # Restore original app_config.json content
    if ($script:OriginalVersionContent) {
        Write-Host "  Restoring original app_config.json..." -ForegroundColor Yellow
        Set-Content -Path $AppConfigFile -Value $script:OriginalVersionContent -Encoding utf8NoBOM
    }

    # Restore package.json if it was modified
    if (Test-Path $PackageJsonFile) {
        Write-Host "  Restoring package.json..." -ForegroundColor Yellow
        git checkout HEAD -- $PackageJsonFile 2>$null
    }

    Write-Host "`nRollback complete. Please check the state manually." -ForegroundColor Yellow
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

# Get current version
$currentVersion = Get-LatestReleaseVersion
Write-Host "Current release: " -NoNewline
Write-Host "v$currentVersion" -ForegroundColor Yellow

# Prompt for new version number
Write-Host "`nSuggested versions:"
Write-Host "  Major (breaking changes): v$((Get-BumpedVersion -Current $currentVersion -BumpType 'major'))"
Write-Host "  Minor (new features):     v$((Get-BumpedVersion -Current $currentVersion -BumpType 'minor'))"
Write-Host "  Patch (bug fixes):        v$((Get-BumpedVersion -Current $currentVersion -BumpType 'patch'))"
Write-Host ""

do {
    $newVersionInput = Read-Host "Enter new version (e.g., 1.1.0)"
    $newVersionInput = $newVersionInput.TrimStart('v')
    try {
        $newVersion = [version]$newVersionInput
        $isValid = $true
    } catch {
        Write-Host "Invalid version format. Please use X.Y.Z format (e.g., 1.1.0)" -ForegroundColor Red
        $isValid = $false
    }
} while (-not $isValid)
$versionTag = "v$newVersion"
$script:CurrentTag = $versionTag
Write-Host "`nNew version will be: " -NoNewline
Write-Host $versionTag -ForegroundColor Green

# Confirm
$confirm = Read-Host "`nProceed with release $($versionTag)? (Y/n)"
if ($confirm -and $confirm.ToLower() -ne 'y') {
    Write-Host "Aborted." -ForegroundColor Yellow
    exit 0
}

if ($DryRun) {
    Write-Host "`n[DRY RUN] Would release $versionTag" -ForegroundColor Yellow
    exit 0
}

try {
    # Save original app_config.json for rollback
    if (Test-Path $AppConfigFile) {
        $script:OriginalVersionContent = Get-Content -Path $AppConfigFile -Raw
    }

    # Update app_config.json
    Update-AppConfigVersion -Version $newVersion

    # Sync extension version
    Sync-ExtensionVersion -Version $newVersion

    # Run build
    if (-not $SkipBuild) {
        Write-Host "`nRunning build..." -ForegroundColor Cyan
        $buildScript = Join-Path $ScriptDir "build.ps1"
        $vibeRailsProject = Join-Path $RepoRoot "VibeRails" "VibeRails.csproj"
        & $buildScript -Project $vibeRailsProject
        if ($LASTEXITCODE -ne 0) {
            throw "Build failed!"
        }
    }

    # Verify build outputs exist
    $winDir = Join-Path $ArtifactsDir "win-x64"
    $linuxDir = Join-Path $ArtifactsDir "linux-x64"

    if (-not (Test-Path $winDir) -or -not (Test-Path $linuxDir)) {
        throw "Build outputs not found. Expected directories at:`n  $winDir`n  $linuxDir"
    }

    # Verify app_config.json exists in build outputs and version matches
    $winConfigPath = Join-Path $winDir "app_config.json"
    $linuxConfigPath = Join-Path $linuxDir "app_config.json"

    if (-not (Test-Path $winConfigPath)) {
        throw "app_config.json not found in Windows build output at $winConfigPath"
    }

    if (-not (Test-Path $linuxConfigPath)) {
        throw "app_config.json not found in Linux build output at $linuxConfigPath"
    }

    # Verify version in bundled config matches
    $winConfig = Get-Content $winConfigPath -Raw | ConvertFrom-Json
    if ($winConfig.version -ne $newVersion) {
        throw "Version mismatch in Windows build! app_config.json has $($winConfig.version) but expected $newVersion"
    }

    $linuxConfig = Get-Content $linuxConfigPath -Raw | ConvertFrom-Json
    if ($linuxConfig.version -ne $newVersion) {
        throw "Version mismatch in Linux build! app_config.json has $($linuxConfig.version) but expected $newVersion"
    }

    Write-Host "Version validation passed: $newVersion" -ForegroundColor Green

    # Create release archives
    $releaseDir = New-ReleaseArchives -Version $newVersion

    # Build extension packages
    $extensionPackages = Build-ExtensionPackages -Version $newVersion

    # Commit version bump (include package.json if it was modified)
    Write-Host "`nCommitting version bump..." -ForegroundColor Cyan
    git add $AppConfigFile
    if (Test-Path $PackageJsonFile) {
        git add $PackageJsonFile
    }
    git commit -m "Bump version to $newVersion"
    git push
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to push version commit"
    }
    $script:VersionCommitPushed = $true

    # Create GitHub release
    New-GitHubRelease -Version $newVersion -ReleaseDir $releaseDir -ExtensionPackages $extensionPackages

    Write-Host "`nDone!" -ForegroundColor Green

} catch {
    Invoke-Rollback -ErrorMessage $_.Exception.Message
    exit 1
}
