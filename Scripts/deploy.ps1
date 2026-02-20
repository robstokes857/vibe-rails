#!/usr/bin/env pwsh
# deploy.ps1 - Preflight + version sync + tag orchestration
# .github/workflows/release.yml publishes:
#   - .NET NativeAOT release assets (win/linux/macos)
# Then this script publishes the VS Code extension locally.

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$ScriptDir = Split-Path -Parent $PSCommandPath
$RepoRoot = Split-Path -Parent $ScriptDir
$AppSettingsFile = Join-Path $RepoRoot "VibeRails" "appsettings.json"
$PackageJsonFile = Join-Path $RepoRoot "vscode-viberails" "package.json"
$PackageLockFile = Join-Path $RepoRoot "vscode-viberails" "package-lock.json"
$GithubRepo = "robstokes857/vibe-rails"
$VsPatEnvName = "VS_PAT"

# --- Helper Functions ---

function Test-PreFlightChecks {
    Write-Host "`nRunning pre-flight checks..." -ForegroundColor Cyan

    if (-not (git rev-parse --git-dir 2>$null)) {
        throw "Not in a git repository."
    }

    $currentBranch = git branch --show-current
    if ($currentBranch -ne "main" -and $currentBranch -ne "master") {
        throw "Must be on 'main' or 'master' branch. Currently on: $currentBranch"
    }

    $status = git status --porcelain
    if ($status) {
        Write-Host "`nUncommitted changes detected:" -ForegroundColor Red
        git status --short
        throw "Working directory must be clean. Commit or stash changes before deploying."
    }

    git fetch origin $currentBranch 2>$null
    $localCommit = git rev-parse HEAD
    $remoteCommit = git rev-parse "origin/$currentBranch" 2>$null
    if ($remoteCommit -and $localCommit -ne $remoteCommit) {
        $behind = git rev-list --count "HEAD..origin/$currentBranch" 2>$null
        if ($behind -gt 0) {
            throw "Local branch is behind remote by $behind commit(s). Run 'git pull' first."
        }
    }

    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw "GitHub CLI (gh) is required. Install from https://cli.github.com/"
    }

    if (-not (Test-Path $AppSettingsFile)) {
        throw "File not found: $AppSettingsFile"
    }
    if (-not (Test-Path $PackageJsonFile)) {
        throw "File not found: $PackageJsonFile"
    }

    Write-Host "  ✓ On branch: $currentBranch" -ForegroundColor Green
    Write-Host "  ✓ Working directory clean" -ForegroundColor Green
    Write-Host "  ✓ Synced with remote" -ForegroundColor Green
    Write-Host "  ✓ Required files found" -ForegroundColor Green
}

function Get-LatestReleaseVersion {
    $releases = gh release list --repo $GithubRepo --limit 1 2>$null
    if (-not $releases) {
        return [version]"0.0.0"
    }

    $tag = ($releases -split "`t")[2]
    $versionStr = $tag -replace "^v", ""
    try {
        return [version]$versionStr
    } catch {
        return [version]"0.0.0"
    }
}

function Update-AppSettingsVersion {
    param([Parameter(Mandatory = $true)][string]$Version)

    $config = Get-Content $AppSettingsFile -Raw | ConvertFrom-Json
    $config.VibeRails.Version = $Version
    $config | ConvertTo-Json -Depth 100 | Set-Content $AppSettingsFile -Encoding utf8NoBOM
    Write-Host "Updated appsettings.json to version $Version" -ForegroundColor Green
}

function Sync-ExtensionVersion {
    param([Parameter(Mandatory = $true)][string]$Version)

    $packageJson = Get-Content $PackageJsonFile -Raw | ConvertFrom-Json
    $packageJson.version = $Version
    $packageJson | ConvertTo-Json -Depth 100 | Set-Content $PackageJsonFile -Encoding utf8NoBOM
    Write-Host "Synced package.json version to $Version" -ForegroundColor Green

    if (Test-Path $PackageLockFile) {
        $packageLockJson = Get-Content $PackageLockFile -Raw | ConvertFrom-Json
        $packageLockJson.version = $Version

        $rootPackageProp = $packageLockJson.packages.PSObject.Properties | Where-Object { $_.Name -eq "" } | Select-Object -First 1
        if ($rootPackageProp) {
            $rootPackageProp.Value.version = $Version
        }

        $packageLockJson | ConvertTo-Json -Depth 100 | Set-Content $PackageLockFile -Encoding utf8NoBOM
        Write-Host "Synced package-lock.json version to $Version" -ForegroundColor Green
    }
}

function Wait-ForReleaseWorkflow {
    param(
        [Parameter(Mandatory = $true)][string]$HeadSha,
        [Parameter(Mandatory = $true)][string]$Tag,
        [int]$TimeoutMinutes = 90
    )

    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    $runId = $null

    Write-Host "`nWaiting for GitHub Actions release workflow for $Tag..." -ForegroundColor Cyan
    while ((Get-Date) -lt $deadline) {
        $runsJson = gh run list --repo $GithubRepo --workflow release.yml --json databaseId,headSha,status,conclusion,url --limit 30
        if ($LASTEXITCODE -ne 0) {
            Start-Sleep -Seconds 5
            continue
        }

        $runs = $runsJson | ConvertFrom-Json
        $matchingRun = $runs | Where-Object { $_.headSha -eq $HeadSha } | Select-Object -First 1

        if ($matchingRun) {
            $runId = $matchingRun.databaseId
            break
        }

        Start-Sleep -Seconds 5
    }

    if (-not $runId) {
        throw "Timed out waiting for workflow run for tag $Tag."
    }

    Write-Host "Watching run $runId..." -ForegroundColor Cyan
    gh run watch $runId --repo $GithubRepo --exit-status
    if ($LASTEXITCODE -ne 0) {
        throw "Release workflow failed. Check run: https://github.com/$GithubRepo/actions/runs/$runId"
    }

    Write-Host "Release workflow completed successfully." -ForegroundColor Green
}

function Get-RequiredVsPat {
    $vsPat = [Environment]::GetEnvironmentVariable($VsPatEnvName)
    if ([string]::IsNullOrWhiteSpace($vsPat)) {
        throw "$VsPatEnvName is not set. Set $VsPatEnvName to your Visual Studio Marketplace PAT before running deploy."
    }
    return $vsPat
}

function Test-VsPatForPublisher {
    param([Parameter(Mandatory = $true)][string]$VsPat)

    $package = Get-Content $PackageJsonFile -Raw | ConvertFrom-Json
    $publisher = [string]$package.publisher
    if ([string]::IsNullOrWhiteSpace($publisher)) {
        throw "Could not read 'publisher' from $PackageJsonFile."
    }

    $originalVscePat = $env:VSCE_PAT
    $env:VSCE_PAT = $VsPat
    try {
        Write-Host "Validating $VsPatEnvName for VS Code publisher '$publisher'..." -ForegroundColor Cyan
        if (Get-Command vsce -ErrorAction SilentlyContinue) {
            vsce verify-pat $publisher
        } elseif (Get-Command npx -ErrorAction SilentlyContinue) {
            npx --yes @vscode/vsce verify-pat $publisher
        } else {
            throw "Neither 'vsce' nor 'npx' found. Install Node.js/npm or @vscode/vsce."
        }

        if ($LASTEXITCODE -ne 0) {
            throw "$VsPatEnvName failed validation for publisher '$publisher'."
        }
        Write-Host "  ✓ $VsPatEnvName is valid for publisher '$publisher'" -ForegroundColor Green
    } finally {
        $env:VSCE_PAT = $originalVscePat
    }
}

# --- Main ---

$banner = @"

  ╦  ╦╦╔╗ ╔═╗  ╦═╗╔═╗╦╦  ╔═╗  ╔╦╗╔═╗╔═╗╦  ╔═╗╦ ╦
  ╚╗╔╝║╠╩╗║╣   ╠╦╝╠═╣║║  ╚═╗   ║║║╣ ╠═╝║  ║ ║╚╦╝
   ╚╝ ╩╚═╝╚═╝  ╩╚═╩ ╩╩╩═╝╚═╝  ═╩╝╚═╝╩  ╩═╝╚═╝ ╩

"@
Write-Host $banner -ForegroundColor Magenta

Test-PreFlightChecks
$vsPat = Get-RequiredVsPat
Test-VsPatForPublisher -VsPat $vsPat

$currentVersion = Get-LatestReleaseVersion
Write-Host "Current release: " -NoNewline
Write-Host "v$currentVersion" -ForegroundColor Yellow

Write-Host "`nEnter new version (e.g., 1.1.0):"
do {
    $newVersionInput = Read-Host "Version"
    $newVersionInput = $newVersionInput.TrimStart('v')
    if ($newVersionInput -notmatch '^\d+\.\d+\.\d+$') {
        Write-Host "Invalid version format. Please use X.Y.Z format (e.g., 1.1.0)" -ForegroundColor Red
        $isValid = $false
        continue
    }

    try {
        $newVersion = [version]$newVersionInput
        $isValid = $true
    } catch {
        Write-Host "Invalid version format. Please use X.Y.Z format (e.g., 1.1.0)" -ForegroundColor Red
        $isValid = $false
    }
} while (-not $isValid)

$tag = "v$newVersion"

# Prevent accidental tag reuse
git fetch --tags origin 2>$null
$tagExistsRemote = git ls-remote --tags origin "refs/tags/$tag"
if ($tagExistsRemote) {
    throw "Tag already exists on origin: $tag"
}
$tagExistsLocal = git tag --list $tag
if ($tagExistsLocal) {
    throw "Tag already exists locally: $tag"
}

Write-Host "`nNew version will be: " -NoNewline
Write-Host $tag -ForegroundColor Green

$confirm = Read-Host "`nProceed with release $tag? (Y/n)"
if ($confirm -and $confirm.ToLower() -ne "y") {
    Write-Host "Aborted." -ForegroundColor Yellow
    exit 0
}

Update-AppSettingsVersion -Version $newVersion
Sync-ExtensionVersion -Version $newVersion

Write-Host "`nCommitting version changes..." -ForegroundColor Cyan
git add $AppSettingsFile
git add $PackageJsonFile
if (Test-Path $PackageLockFile) {
    git add $PackageLockFile
}
git commit -m "Bump version to $newVersion"

$currentBranch = git branch --show-current
Write-Host "Pushing $currentBranch..." -ForegroundColor Cyan
git push origin $currentBranch

Write-Host "Tagging $tag..." -ForegroundColor Cyan
git tag -a $tag -m "Release $tag"
git push origin $tag

$headSha = (git rev-parse HEAD).Trim()
Wait-ForReleaseWorkflow -HeadSha $headSha -Tag $tag

Write-Host "`nPublishing VS Code extension..." -ForegroundColor Cyan
$vsCodeReleaseScript = Join-Path $RepoRoot "vscode-viberails" "scripts" "buildAndDeployVSCodeExt.ps1"
if (-not (Test-Path $vsCodeReleaseScript)) {
    throw "VS Code release script not found: $vsCodeReleaseScript"
}
& $vsCodeReleaseScript -Version $newVersion.ToString() -Pat $vsPat -SkipVersionUpdate
if ($LASTEXITCODE -ne 0) {
    throw "VS Code extension publish script failed."
}

Write-Host "`nPublished release: https://github.com/$GithubRepo/releases/tag/$tag" -ForegroundColor Green
Write-Host "Installer commands now resolve to this published version." -ForegroundColor Green
Write-Host "`nDone!" -ForegroundColor Green
