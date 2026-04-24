#!/usr/bin/env pwsh
# deploy.ps1 - Preflight + version sync + tag orchestration
# .github/workflows/release.yml publishes:
#   - .NET NativeAOT release assets (win/linux/macos)
#   - Platform-specific VS Code extension packages
#   - VS Code Marketplace extension updates

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$ScriptDir = Split-Path -Parent $PSCommandPath
$RepoRoot = Split-Path -Parent $ScriptDir
$AppSettingsFile = Join-Path $RepoRoot "VibeRails" "appsettings.json"
$PackageJsonFile = Join-Path $RepoRoot "vscode-viberails" "package.json"
$PackageLockFile = Join-Path $RepoRoot "vscode-viberails" "package-lock.json"
$GithubRepo = "robstokes857/vibe-rails"
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
        $packageLockJson = Get-Content $PackageLockFile -Raw | ConvertFrom-Json -AsHashtable
        $packageLockJson["version"] = $Version

        if ($packageLockJson.Contains("packages")) {
            $packages = $packageLockJson["packages"]
            if ($packages -is [System.Collections.IDictionary] -and $packages.Contains("")) {
                $packages[""]["version"] = $Version
            }
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

    $runViewJson = gh run view $runId --repo $GithubRepo --json conclusion,jobs,url
    if ($LASTEXITCODE -ne 0) {
        throw "Could not inspect workflow run details for run $runId."
    }

    $runView = $runViewJson | ConvertFrom-Json
    $runConclusion = [string]$runView.conclusion
    if ($runConclusion -ne "success") {
        throw "Release workflow conclusion is '$runConclusion'. Run URL: $($runView.url)"
    }

    $requiredJobs = @(
        "Build win-x64",
        "Build linux-x64",
        "Build osx-x64",
        "Build osx-arm64",
        "Package VSIX win32-x64",
        "Package VSIX linux-x64",
        "Package VSIX darwin-x64",
        "Package VSIX darwin-arm64",
        "Publish VS Code Extension",
        "Upload Assets To GitHub Release"
    )

    $jobs = @($runView.jobs)
    foreach ($jobName in $requiredJobs) {
        $job = $jobs | Where-Object { $_.name -eq $jobName } | Select-Object -First 1
        if (-not $job) {
            throw "Required release job missing: '$jobName'. Run URL: $($runView.url)"
        }

        $jobConclusion = [string]$job.conclusion
        if ($jobConclusion -ne "success") {
            throw "Release job '$jobName' finished with '$jobConclusion'. Run URL: $($runView.url)"
        }
    }

    Write-Host "Release workflow completed successfully with all required jobs." -ForegroundColor Green
}

function Assert-ReleaseAssetsPresent {
    param([Parameter(Mandatory = $true)][string]$Tag)

    $releaseJson = gh release view $Tag --repo $GithubRepo --json assets,url
    if ($LASTEXITCODE -ne 0) {
        throw "Could not inspect release assets for tag $Tag."
    }

    $release = $releaseJson | ConvertFrom-Json
    $assetNames = @($release.assets | ForEach-Object { [string]$_.name })
    $version = $Tag.TrimStart('v')

    $requiredAssets = @(
        "vb-win-x64.zip",
        "vb-win-x64.zip.sha256",
        "vb-linux-x64.tar.gz",
        "vb-linux-x64.tar.gz.sha256",
        "vb-osx-x64.tar.gz",
        "vb-osx-x64.tar.gz.sha256",
        "vb-osx-arm64.tar.gz",
        "vb-osx-arm64.tar.gz.sha256",
        "vscode-viberails-win32-x64-$version.vsix",
        "vscode-viberails-linux-x64-$version.vsix",
        "vscode-viberails-darwin-x64-$version.vsix",
        "vscode-viberails-darwin-arm64-$version.vsix"
    )

    $missing = @()
    foreach ($name in $requiredAssets) {
        if ($assetNames -notcontains $name) {
            $missing += $name
        }
    }

    if ($missing.Count -gt 0) {
        throw "Release '$Tag' is missing required assets: $($missing -join ', '). Release URL: $($release.url)"
    }

    Write-Host "Verified release assets for $Tag." -ForegroundColor Green
}

function Assert-VsixContainsNativeOnnxRuntime {
    param(
        [Parameter(Mandatory = $true)][string]$Tag,
        [Parameter(Mandatory = $true)][string]$Version
    )

    # Download one platform VSIX and confirm the native ONNX Runtime library
    # is packaged inside. The prepare-binaries step also asserts this, so this
    # is defense-in-depth against packaging regressions (like the 1.6.4 ORT
    # 1.21 -> 1.24 bump that exposed a latent "only copy vb.exe" bug and shipped
    # a VSIX with no native DLLs, crashing with ArgumentNull/0xC0000005).
    $vsixName = "vscode-viberails-win32-x64-$Version.vsix"
    $tmpDir = Join-Path ([System.IO.Path]::GetTempPath()) "viberails-vsix-verify-$Version"
    if (Test-Path $tmpDir) { Remove-Item -Recurse -Force $tmpDir }
    New-Item -ItemType Directory -Force -Path $tmpDir | Out-Null

    try {
        Write-Host "`nDownloading $vsixName to verify native DLL packaging..." -ForegroundColor Cyan
        gh release download $Tag --repo $GithubRepo --pattern $vsixName --dir $tmpDir
        if ($LASTEXITCODE -ne 0) {
            throw "Could not download $vsixName from release $Tag."
        }

        $vsixPath = Join-Path $tmpDir $vsixName
        $zipPath = Join-Path $tmpDir "$vsixName.zip"
        Copy-Item -Path $vsixPath -Destination $zipPath -Force

        $extractDir = Join-Path $tmpDir "extracted"
        Expand-Archive -Path $zipPath -DestinationPath $extractDir -Force

        $ortHit = Get-ChildItem -Path $extractDir -Recurse -File |
                  Where-Object { $_.Name -match '(?i)onnxruntime' } |
                  Select-Object -First 1

        if (-not $ortHit) {
            throw "Released $vsixName does not contain any onnxruntime native library. The VSIX will crash at the BERT job with ArgumentNull/0xC0000005. Fix prepare-binaries.ps1 and re-release."
        }

        Write-Host "  ✓ Found native ONNX Runtime in VSIX: $($ortHit.Name)" -ForegroundColor Green
    } finally {
        if (Test-Path $tmpDir) { Remove-Item -Recurse -Force $tmpDir -ErrorAction SilentlyContinue }
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

$confirm = Read-Host "`nProceed with release $($tag)? (Y/n)"
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
Assert-ReleaseAssetsPresent -Tag $tag
Assert-VsixContainsNativeOnnxRuntime -Tag $tag -Version $newVersion

Write-Host "`nPublished release: https://github.com/$GithubRepo/releases/tag/$tag" -ForegroundColor Green
Write-Host "GitHub Actions built the native assets, published the VS Code extension, and uploaded the VSIX packages." -ForegroundColor Green
Write-Host "`nDone!" -ForegroundColor Green
