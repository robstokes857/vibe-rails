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
$ProjectFile = Join-Path $RepoRoot "VibeRails" "VibeRails.csproj"
$PackageJsonFile = Join-Path $RepoRoot "vscode-viberails" "package.json"
$PackageLockFile = Join-Path $RepoRoot "vscode-viberails" "package-lock.json"
$GithubRepo = "robstokes857/vibe-rails"
# --- Helper Functions ---

function Assert-NativeCommandSucceeded {
    param(
        [Parameter(Mandatory = $true)][int]$ExitCode,
        [Parameter(Mandatory = $true)][string]$Operation
    )

    if ($ExitCode -ne 0) {
        throw "$Operation failed with exit code $ExitCode."
    }
}

function Get-GitIndexLockPath {
    $gitDir = (git rev-parse --absolute-git-dir).Trim()
    Assert-NativeCommandSucceeded -ExitCode $LASTEXITCODE -Operation "Locating the git directory"
    return Join-Path $gitDir "index.lock"
}

function Test-GitIndexLockAbandoned {
    param(
        [Parameter(Mandatory = $true)][string]$LockPath,
        [int]$MinimumAgeSeconds = 30
    )

    $lock = Get-Item -LiteralPath $LockPath -ErrorAction SilentlyContinue
    if (-not $lock -or ((Get-Date) - $lock.LastWriteTime).TotalSeconds -lt $MinimumAgeSeconds) {
        return $false
    }

    # Only Windows can name the running git commands; elsewhere never treat a lock as abandoned.
    if (-not $IsWindows) {
        return $false
    }

    # The fsmonitor daemon is long-lived and never holds the index lock.
    $activeGit = @(Get-CimInstance Win32_Process -Filter "Name='git.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -notmatch 'fsmonitor--daemon' })
    if ($activeGit.Count -gt 0) {
        return $false
    }

    # A lock some other tool still has open (for example a libgit2 client) cannot be reopened exclusively.
    try {
        $stream = [System.IO.File]::Open($LockPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
        $stream.Dispose()
        return $true
    } catch {
        return $false
    }
}

function Wait-GitIndexLockRelease {
    param([int]$TimeoutSeconds = 120)

    $lockPath = Get-GitIndexLockPath
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $announced = $false

    while (Test-Path -LiteralPath $lockPath) {
        if (Test-GitIndexLockAbandoned -LockPath $lockPath) {
            Write-Host "  Removing stale $lockPath (no git process is using it)" -ForegroundColor Yellow
            Remove-Item -LiteralPath $lockPath -Force -ErrorAction SilentlyContinue
            return
        }

        if ((Get-Date) -ge $deadline) {
            throw "The git index is still locked after $TimeoutSeconds seconds: $lockPath. Another tool (an editor, VibeRails, a stuck git command) is holding it; close it, or delete the file if no git process is running."
        }

        if (-not $announced) {
            Write-Host "  Waiting for another process to release .git/index.lock..." -ForegroundColor Yellow
            $announced = $true
        }
        Start-Sleep -Milliseconds 500
    }
}

# Editors, VibeRails and other watchers refresh the index the moment the version files change, so
# a git command that writes the index can lose the race for index.lock. Wait, then retry.
function Invoke-GitIndexWrite {
    param(
        [Parameter(Mandatory = $true)][string]$Operation,
        [Parameter(Mandatory = $true)][string[]]$GitArguments,
        [int]$MaxAttempts = 5
    )

    $lockPath = Get-GitIndexLockPath
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        Wait-GitIndexLockRelease
        & git @GitArguments
        $exitCode = $LASTEXITCODE

        # Exit 128 with the lock present again means another process took it between our check and
        # git's own attempt, so nothing was written and the command is safe to repeat.
        if ($exitCode -ne 128 -or -not (Test-Path -LiteralPath $lockPath) -or $attempt -eq $MaxAttempts) {
            break
        }
        Write-Host "  git lost a race for .git/index.lock; retrying ($attempt/$MaxAttempts)..." -ForegroundColor Yellow
    }

    Assert-NativeCommandSucceeded -ExitCode $exitCode -Operation $Operation
}

function Restore-VersionFiles {
    param([Parameter(Mandatory = $true)][hashtable]$OriginalContent)

    foreach ($entry in $OriginalContent.GetEnumerator()) {
        [System.IO.File]::WriteAllBytes($entry.Key, $entry.Value)
    }

    # Best effort: unstage whatever the failed run staged so HEAD, index and files agree again.
    git reset --quiet -- @($OriginalContent.Keys) 2>$null
    Write-Host "Restored the version files to their pre-release contents." -ForegroundColor Yellow
}

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
    if (-not (Test-Path $ProjectFile)) {
        throw "File not found: $ProjectFile"
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

    $config = Get-Content $AppSettingsFile -Raw | ConvertFrom-Json -AsHashtable
    if (-not $config.Contains("VibeRails") -or $config["VibeRails"] -isnot [System.Collections.IDictionary]) {
        throw "appsettings.json does not contain a VibeRails object: $AppSettingsFile"
    }

    # Assignment through the dictionary also restores Version if an older settings cleanup
    # removed it, instead of failing halfway through release orchestration.
    $config["VibeRails"]["Version"] = $Version
    $config | ConvertTo-Json -Depth 100 | Set-Content $AppSettingsFile -Encoding utf8NoBOM
    Write-Host "Updated appsettings.json to version $Version" -ForegroundColor Green
}

function Set-ProjectProperty {
    param(
        [Parameter(Mandatory = $true)][System.Xml.XmlDocument]$Document,
        [Parameter(Mandatory = $true)][System.Xml.XmlElement]$PropertyGroup,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Value,
        [string]$InsertBeforeName = ""
    )

    $node = $PropertyGroup.SelectSingleNode($Name)
    if (-not $node) {
        $node = $Document.CreateElement($Name)
        $insertBefore = $null
        if ($InsertBeforeName) {
            $insertBefore = $PropertyGroup.SelectSingleNode($InsertBeforeName)
        }

        if ($insertBefore) {
            [void]$PropertyGroup.InsertBefore($node, $insertBefore)
        } else {
            [void]$PropertyGroup.AppendChild($node)
        }
    }

    $node.InnerText = $Value
}

function Update-DotNetProjectVersion {
    param([Parameter(Mandatory = $true)][string]$Version)

    $fileVersion = "$Version.0"
    $projectXml = [System.Xml.XmlDocument]::new()
    $projectXml.PreserveWhitespace = $true
    $projectXml.Load($ProjectFile)

    $propertyGroup = $projectXml.SelectSingleNode("/Project/PropertyGroup[TargetFramework]")
    if (-not $propertyGroup) {
        $propertyGroup = $projectXml.SelectSingleNode("/Project/PropertyGroup")
    }
    if (-not $propertyGroup -or $propertyGroup -isnot [System.Xml.XmlElement]) {
        throw "Could not find a PropertyGroup in $ProjectFile"
    }

    Set-ProjectProperty -Document $projectXml -PropertyGroup $propertyGroup -Name "Version" -Value $Version -InsertBeforeName "NoWarn"
    Set-ProjectProperty -Document $projectXml -PropertyGroup $propertyGroup -Name "FileVersion" -Value $fileVersion -InsertBeforeName "NoWarn"
    Set-ProjectProperty -Document $projectXml -PropertyGroup $propertyGroup -Name "InformationalVersion" -Value $Version -InsertBeforeName "NoWarn"

    $projectXml.Save($ProjectFile)
    Write-Host "Updated VibeRails.csproj to version $Version (FileVersion $fileVersion)" -ForegroundColor Green
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

function Assert-VersionSynchronization {
    param([Parameter(Mandatory = $true)][string]$Version)

    $expectedVersion = [string]$Version
    $expectedFileVersion = "$expectedVersion.0"
    $mismatches = [System.Collections.Generic.List[string]]::new()

    $appSettings = Get-Content $AppSettingsFile -Raw | ConvertFrom-Json -AsHashtable
    $appSettingsVersion = if ($appSettings.Contains("VibeRails") -and
        $appSettings["VibeRails"] -is [System.Collections.IDictionary] -and
        $appSettings["VibeRails"].Contains("Version")) {
        [string]$appSettings["VibeRails"]["Version"]
    } else {
        "<missing>"
    }
    if ($appSettingsVersion -ne $expectedVersion) {
        $mismatches.Add("appsettings.json=$appSettingsVersion")
    }

    $projectXml = [System.Xml.XmlDocument]::new()
    $projectXml.Load($ProjectFile)
    $projectVersions = @{
        Version = $expectedVersion
        FileVersion = $expectedFileVersion
        InformationalVersion = $expectedVersion
    }
    foreach ($property in $projectVersions.GetEnumerator()) {
        $node = $projectXml.SelectSingleNode("/Project/PropertyGroup/$($property.Key)")
        $actual = if ($node) { [string]$node.InnerText } else { "<missing>" }
        if ($actual -ne $property.Value) {
            $mismatches.Add("VibeRails.csproj:$($property.Key)=$actual")
        }
    }

    $packageJson = Get-Content $PackageJsonFile -Raw | ConvertFrom-Json -AsHashtable
    $packageVersion = if ($packageJson.Contains("version")) { [string]$packageJson["version"] } else { "<missing>" }
    if ($packageVersion -ne $expectedVersion) {
        $mismatches.Add("package.json=$packageVersion")
    }

    if (Test-Path $PackageLockFile) {
        $packageLock = Get-Content $PackageLockFile -Raw | ConvertFrom-Json -AsHashtable
        $lockVersion = if ($packageLock.Contains("version")) { [string]$packageLock["version"] } else { "<missing>" }
        if ($lockVersion -ne $expectedVersion) {
            $mismatches.Add("package-lock.json=$lockVersion")
        }

        $rootPackageVersion = if ($packageLock.Contains("packages") -and
            $packageLock["packages"] -is [System.Collections.IDictionary] -and
            $packageLock["packages"].Contains("") -and
            $packageLock["packages"][""] -is [System.Collections.IDictionary] -and
            $packageLock["packages"][""].Contains("version")) {
            [string]$packageLock["packages"][""]["version"]
        } else {
            "<missing>"
        }
        if ($rootPackageVersion -ne $expectedVersion) {
            $mismatches.Add("package-lock.json:packages['']=$rootPackageVersion")
        }
    }

    if ($mismatches.Count -gt 0) {
        throw "Version synchronization failed; no release tag was created. Expected $expectedVersion. Mismatches: $($mismatches -join ', ')"
    }

    Write-Host "Verified all release metadata is version $expectedVersion" -ForegroundColor Green
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
        "Build osx-arm64",
        "Package VSIX win32-x64",
        "Package VSIX linux-x64",
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
        "vb-osx-arm64.tar.gz",
        "vb-osx-arm64.tar.gz.sha256",
        "vscode-viberails-win32-x64-$version.vsix",
        "vscode-viberails-linux-x64-$version.vsix",
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
Assert-NativeCommandSucceeded -ExitCode $LASTEXITCODE -Operation "Fetching release tags"
$tagExistsRemote = git ls-remote --tags origin "refs/tags/$tag"
Assert-NativeCommandSucceeded -ExitCode $LASTEXITCODE -Operation "Checking the remote release tag"
if ($tagExistsRemote) {
    throw "Tag already exists on origin: $tag"
}
$tagExistsLocal = git tag --list $tag
Assert-NativeCommandSucceeded -ExitCode $LASTEXITCODE -Operation "Checking the local release tag"
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

$versionFiles = @($AppSettingsFile, $ProjectFile, $PackageJsonFile)
if (Test-Path $PackageLockFile) {
    $versionFiles += $PackageLockFile
}

# The pre-flight checks proved the tree clean, so these bytes are what HEAD holds. Keep them so a
# failure before the version commit exists leaves the tree as found instead of dirty, which the
# next run's pre-flight would reject.
$originalVersionFiles = @{}
foreach ($versionFile in $versionFiles) {
    $originalVersionFiles[$versionFile] = [System.IO.File]::ReadAllBytes($versionFile)
}

try {
    Update-AppSettingsVersion -Version $newVersion
    Update-DotNetProjectVersion -Version $newVersion
    Sync-ExtensionVersion -Version $newVersion
    Assert-VersionSynchronization -Version $newVersion

    Write-Host "`nCommitting version changes..." -ForegroundColor Cyan
    Invoke-GitIndexWrite -Operation "Staging version files" -GitArguments (@("add", "--") + $versionFiles)

    git diff --cached --quiet --exit-code
    $stagedVersionExitCode = $LASTEXITCODE
    if ($stagedVersionExitCode -eq 1) {
        Invoke-GitIndexWrite -Operation "Committing version $newVersion (release stopped before push/tag)" -GitArguments @("commit", "-m", "Bump version to $newVersion")
    } elseif ($stagedVersionExitCode -eq 0) {
        Write-Host "Version metadata was already committed; using the current HEAD." -ForegroundColor Yellow
    } else {
        throw "Could not inspect staged version changes (git diff exit code $stagedVersionExitCode)."
    }
} catch {
    $releaseError = $_
    try {
        Restore-VersionFiles -OriginalContent $originalVersionFiles
    } catch {
        Write-Host "Could not restore the version files: $_" -ForegroundColor Red
    }
    throw $releaseError
}

$remainingChanges = git status --porcelain
Assert-NativeCommandSucceeded -ExitCode $LASTEXITCODE -Operation "Checking the post-commit working tree"
if ($remainingChanges) {
    throw "Version commit did not leave a clean working tree; release stopped before push/tag."
}

$currentBranch = git branch --show-current
Assert-NativeCommandSucceeded -ExitCode $LASTEXITCODE -Operation "Resolving the release branch"
Write-Host "Pushing $currentBranch..." -ForegroundColor Cyan
git push origin $currentBranch
Assert-NativeCommandSucceeded -ExitCode $LASTEXITCODE -Operation "Pushing release commit"

Write-Host "Tagging $tag..." -ForegroundColor Cyan
git tag -a $tag -m "Release $tag"
Assert-NativeCommandSucceeded -ExitCode $LASTEXITCODE -Operation "Creating release tag $tag"
git push origin $tag
Assert-NativeCommandSucceeded -ExitCode $LASTEXITCODE -Operation "Pushing release tag $tag"

$headSha = (git rev-parse HEAD).Trim()
Assert-NativeCommandSucceeded -ExitCode $LASTEXITCODE -Operation "Resolving the release commit"
Wait-ForReleaseWorkflow -HeadSha $headSha -Tag $tag
Assert-ReleaseAssetsPresent -Tag $tag
Assert-VsixContainsNativeOnnxRuntime -Tag $tag -Version $newVersion

Write-Host "`nPublished release: https://github.com/$GithubRepo/releases/tag/$tag" -ForegroundColor Green
Write-Host "GitHub Actions built the native assets, published the VS Code extension, and uploaded the VSIX packages." -ForegroundColor Green
Write-Host "`nDone!" -ForegroundColor Green
