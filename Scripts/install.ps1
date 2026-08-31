#!/usr/bin/env pwsh
# install.ps1 - Install VibeRails (vb) on Windows
# Usage: irm https://raw.githubusercontent.com/robstokes857/vibe-rails/main/Scripts/install.ps1 | iex

$ErrorActionPreference = "Stop"

$GithubRepo = "robstokes857/vibe-rails"
$InstallDir = Join-Path $env:USERPROFILE ".vibe_rails"
$AssetName = "vb-win-x64.zip"

function Assert-ReleasePayload {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootDir
    )

    $requiredFiles = @(
        "vb.exe",
        "appsettings.json",
        "wwwroot\index.html",
        "Models\BertV2\model.onnx.zip",
        "Models\BertV2\vocab.txt",
        "scripts\pre-commit-hook.sh",
        "scripts\commit-msg-hook.sh",
        "onnxruntime.dll",
        "e_sqlite3.dll",
        "vec0.dll"
    )

    foreach ($relativePath in $requiredFiles) {
        $path = Join-Path $RootDir $relativePath
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Release package is incomplete: required file '$relativePath' is missing. The existing installation was not changed."
        }
    }

    # Release payloads are overlaid into ~/.vibe_rails. Refuse an archive that
    # accidentally contains known user-owned roots before it can overwrite them.
    $protectedNames = @("config.json", "envs", "history", "logs", "sandboxes")
    foreach ($entry in Get-ChildItem -LiteralPath $RootDir -Force) {
        $isStateDatabase = $entry.Name.StartsWith("state.db", [StringComparison]::OrdinalIgnoreCase)
        $isProtectedName = $protectedNames -icontains $entry.Name
        $isRuntimeModels = $entry.Name.Equals("models", [StringComparison]::OrdinalIgnoreCase) -and
            -not $entry.Name.Equals("Models", [StringComparison]::Ordinal)
        if ($isStateDatabase -or $isProtectedName -or $isRuntimeModels) {
            throw "Release package contains protected user-data path '$($entry.Name)'. The existing installation was not changed."
        }
    }
}

function Assert-StableInstallTarget {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $profileDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    $expectedPath = [System.IO.Path]::GetFullPath((Join-Path $profileDirectory ".vibe_rails"))
    $actualPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $actualPath.Equals($expectedPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Installation target must be the current user's stable directory: $expectedPath"
    }

    if (-not (Test-Path -LiteralPath $actualPath)) {
        return
    }

    $item = Get-Item -LiteralPath $actualPath -Force
    if (-not $item.PSIsContainer) {
        throw "Installation target exists but is not a directory: $actualPath"
    }
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Installation target must not be a symlink or junction: $actualPath"
    }

    $owner = (Get-Acl -LiteralPath $actualPath).GetOwner(
        [System.Security.Principal.SecurityIdentifier])
    $currentUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
    if ($null -eq $currentUser -or $owner.Value -ne $currentUser.Value) {
        throw "Installation target must be owned by the current user: $actualPath"
    }
}

function Assert-NoDestinationReparsePoints {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PayloadDir,

        [Parameter(Mandatory = $true)]
        [string]$InstallDir
    )

    # The overlay copy writes THROUGH an existing symlink/junction at any payload-shadowed
    # path (e.g. an attacker-planted 'wwwroot' junction would redirect application files into
    # another directory). Refuse to copy while any such link exists.
    $payloadRoot = [System.IO.Path]::GetFullPath($PayloadDir)
    foreach ($item in Get-ChildItem -LiteralPath $payloadRoot -Recurse -Force) {
        $relative = $item.FullName.Substring($payloadRoot.Length).TrimStart('\', '/')
        $destination = Join-Path $InstallDir $relative
        if (-not (Test-Path -LiteralPath $destination)) {
            continue
        }
        $destinationItem = Get-Item -LiteralPath $destination -Force
        if (($destinationItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to overlay through a symlink/junction at '$destination'. Remove the link and retry; no application files were replaced."
        }
    }
}

function Resolve-VbdProbe {
    param(
        [Parameter(Mandatory = $true)]
        [string]$StagedExecutable,

        [Parameter(Mandatory = $true)]
        [string]$InstalledExecutable
    )

    # Prefer the staged (new) executable, but a hardened host can block executing a freshly
    # downloaded binary from TEMP (AV, AppLocker). Fall back to the installed executable; if
    # neither can answer, VBD cannot have been registered by a pre-VBD build, so treat it as
    # not installed instead of failing every install on such machines.
    try {
        return @{ Executable = $StagedExecutable; Status = (Get-VbdStatus -Executable $StagedExecutable) }
    } catch {
        Write-Host "Staged VBD probe failed: $($_.Exception.Message)" -ForegroundColor Yellow
    }

    if (Test-Path -LiteralPath $InstalledExecutable -PathType Leaf) {
        Write-Host "Falling back to the installed executable for the VBD probe..." -ForegroundColor Yellow
        try {
            return @{ Executable = $InstalledExecutable; Status = (Get-VbdStatus -Executable $InstalledExecutable) }
        } catch {
            Write-Host "Installed VBD probe also failed: $($_.Exception.Message)" -ForegroundColor Yellow
        }
        Write-Host "WARNING: VBD state could not be determined (the installed executable may predate VBD). Assuming it is not installed." -ForegroundColor Yellow
    }

    return @{ Executable = $null; Status = $null }
}

function Get-VbdStatus {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Executable
    )

    $json = (& $Executable --job-daemon-service status --json 2>$null | Out-String).Trim()
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0 -or [string]::IsNullOrWhiteSpace($json)) {
        throw "VBD status command failed with exit code $exitCode."
    }

    try {
        $status = $json | ConvertFrom-Json -ErrorAction Stop
    } catch {
        throw "VBD status command returned invalid JSON: $($_.Exception.Message)"
    }

    $propertyNames = @($status.PSObject.Properties.Name)
    if ($propertyNames -notcontains "isInstalled" -or $propertyNames -notcontains "isRunning") {
        throw "VBD status JSON did not contain isInstalled and isRunning."
    }

    return $status
}

function Invoke-VbdAction {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Executable,

        [Parameter(Mandatory = $true)]
        [ValidateSet("stop", "repair", "start")]
        [string]$Action
    )

    try {
        & $Executable --job-daemon-service $Action | Out-Host
        return $LASTEXITCODE -eq 0
    } catch {
        Write-Host "VBD $Action command failed: $($_.Exception.Message)" -ForegroundColor Red
        return $false
    }
}

function Wait-VbdRunningState {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Executable,

        [Parameter(Mandatory = $true)]
        [bool]$ExpectedRunning,

        [int]$TimeoutSeconds = 10
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try {
            $status = Get-VbdStatus -Executable $Executable
            if ([bool]$status.isRunning -eq $ExpectedRunning) {
                return $true
            }
        } catch {
            # Lifecycle registration can take a moment to settle. Retry until
            # the bounded deadline and report failure to the caller.
        }

        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)

    return $false
}

function Wait-ProcessExit {
    param(
        [Parameter(Mandatory = $true)]
        [int]$TargetProcessId,

        [int]$TimeoutSeconds = 10
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if (-not (Get-Process -Id $TargetProcessId -ErrorAction SilentlyContinue)) {
            return $true
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)

    return $false
}

function Wait-ExecutableReadyForReplacement {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Executable,

        [int]$TimeoutSeconds = 10
    )

    if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) {
        return $true
    }

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $stream = $null
        try {
            $stream = [System.IO.File]::Open(
                $Executable,
                [System.IO.FileMode]::Open,
                [System.IO.FileAccess]::ReadWrite,
                [System.IO.FileShare]::None)
            return $true
        } catch {
            # A running vb process (or another writer) still owns the executable.
        } finally {
            if ($null -ne $stream) {
                $stream.Dispose()
            }
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)

    return $false
}

function Show-VbdRecoveryCommands {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Executable,

        [Parameter(Mandatory = $true)]
        [bool]$WasRunning
    )

    $quotedExecutable = $Executable.Replace("'", "''")
    Write-Host ""
    Write-Host "VBD could not be restored automatically." -ForegroundColor Red
    Write-Host "After resolving the installation error, run these current-user commands:" -ForegroundColor Yellow
    Write-Host "  & '$quotedExecutable' --job-daemon-service repair" -ForegroundColor White
    if ($WasRunning) {
        Write-Host "  & '$quotedExecutable' --job-daemon-service start" -ForegroundColor White
    }
    Write-Host ""
}

function Remove-InstallerStagingDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    $separator = [string][System.IO.Path]::DirectorySeparatorChar
    if (-not $tempRoot.EndsWith($separator, [StringComparison]::Ordinal)) {
        $tempRoot += $separator
    }
    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $resolvedPath.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
        Write-Warning "Refusing to remove staging directory outside the system temp directory: $resolvedPath"
        return
    }

    Remove-Item -LiteralPath $resolvedPath -Recurse -Force -ErrorAction SilentlyContinue
}

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
        $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "vibe_rails_bertv2_$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $tempDir | Out-Null
        try {
            Write-Host "Extracting BertV2 model..." -ForegroundColor Cyan
            Expand-Archive -Path $bundledModelArchive -DestinationPath $tempDir -Force

            $extractedModelPath = Join-Path $tempDir "model.onnx"
            if (-not (Test-Path $extractedModelPath)) {
                throw "BertV2 model archive did not contain model.onnx"
            }

            Move-Item -Path $extractedModelPath -Destination $runtimeModel -Force
        } finally {
            Remove-InstallerStagingDirectory -Path $tempDir
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

# Fail closed: without the published checksum the installer would extract and execute an
# unverified download. A release missing its .sha256 asset is a broken release.
if (-not $checksumAsset) {
    Write-Host "Error: Could not find $AssetName.sha256 in release assets. Refusing to install an unverified download." -ForegroundColor Red
    exit 1
}

$zipUrl = $zipAsset.browser_download_url
$checksumUrl = $checksumAsset.browser_download_url

# Create a private, random staging directory. The release is fully extracted and
# validated here before the live installation or VBD process is touched.
$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "vibe_rails_install_$([Guid]::NewGuid().ToString('N'))"
$payloadDir = Join-Path $tempDir "payload"
New-Item -ItemType Directory -Path $tempDir | Out-Null
New-Item -ItemType Directory -Path $payloadDir | Out-Null

$daemonWasInstalled = $false
$daemonWasRunning = $false
$recoveryNeeded = $false

try {
    # Download files
    $zipPath = Join-Path $tempDir $AssetName
    $checksumPath = Join-Path $tempDir "$AssetName.sha256"

    Write-Host "Downloading $AssetName..." -ForegroundColor Cyan
    Invoke-WebRequest -Uri $zipUrl -OutFile $zipPath -UseBasicParsing

    Write-Host "Downloading checksum..." -ForegroundColor Cyan
    Invoke-WebRequest -Uri $checksumUrl -OutFile $checksumPath -UseBasicParsing

    # Verify checksum (mandatory: the asset's presence was asserted before downloading)
    Write-Host "Verifying checksum..." -ForegroundColor Cyan
    $expectedHash = (Get-Content $checksumPath -Raw).Split()[0].Trim()
    $actualHash = (Get-FileHash -Algorithm SHA256 -Path $zipPath).Hash.ToLowerInvariant()

    if ($expectedHash -ne $actualHash) {
        throw "Checksum verification failed. Expected: $expectedHash. Actual: $actualHash."
    }
    Write-Host "Checksum verified!" -ForegroundColor Green

    Write-Host "Extracting release into private staging..." -ForegroundColor Cyan
    Expand-Archive -Path $zipPath -DestinationPath $payloadDir -Force
    Assert-ReleasePayload -RootDir $payloadDir
    Assert-StableInstallTarget -Path $InstallDir
    Write-Host "Release payload validated." -ForegroundColor Green

    # Use the new staged executable to inspect and control the stable current-user
    # registration, falling back to the installed executable on hosts that refuse
    # to execute a freshly downloaded binary from TEMP.
    $stagedExecutable = Join-Path $payloadDir "vb.exe"
    $probe = Resolve-VbdProbe `
        -StagedExecutable $stagedExecutable `
        -InstalledExecutable (Join-Path $InstallDir "vb.exe")
    $daemonStatus = $probe.Status
    $vbdExecutable = if ($null -ne $probe.Executable) { $probe.Executable } else { $stagedExecutable }

    $daemonWasInstalled = $false
    $daemonWasRunning = $false
    $daemonProcessId = $null
    if ($null -ne $daemonStatus) {
        $daemonState = [string]$daemonStatus.state
        $daemonWasInstalled = [bool]$daemonStatus.isInstalled
        # isReachable guards against a status whose isRunning was computed while the
        # process was still starting; either signal means a live daemon must stop first.
        $daemonWasRunning = ([bool]$daemonStatus.isRunning) -or ([bool]$daemonStatus.isReachable)
        $daemonProcessId = if ($null -ne $daemonStatus.pid) { [int]$daemonStatus.pid } else { $null }

        # An Error state means VBD's own view of the registration is broken; an Unavailable
        # state alongside an active daemon/registration means lifecycle control (including
        # stop) cannot work. Proceeding would replace files under a running daemon.
        if ($daemonState -eq "Error") {
            throw "VBD reported lifecycle state 'Error' ($($daemonStatus.lastError)). Resolve it (vb --job-daemon-service status) and retry. The existing installation was not changed."
        }
        if ($daemonState -eq "Unavailable" -and ($daemonWasInstalled -or $daemonWasRunning)) {
            throw "VBD lifecycle support is unavailable while a VBD process or registration appears active ($($daemonStatus.lastError)). Resolve it and retry. The existing installation was not changed."
        }
    }

    if ($daemonWasInstalled) {
        $daemonStateLabel = if ($daemonWasRunning) { "running" } else { "stopped" }
        Write-Host "Detected installed VBD ($daemonStateLabel)." -ForegroundColor Cyan
    } else {
        Write-Host "VBD is not installed for the current user." -ForegroundColor Cyan
    }

    if ($daemonWasInstalled -or $daemonWasRunning) {
        Write-Host "Ensuring VBD is stopped before replacing files..." -ForegroundColor Cyan
        $recoveryNeeded = $true
        if (-not (Invoke-VbdAction -Executable $vbdExecutable -Action "stop")) {
            throw "Could not stop VBD. The existing installation was not changed."
        }
        if (-not (Wait-VbdRunningState -Executable $vbdExecutable -ExpectedRunning $false)) {
            throw "VBD did not stop within 10 seconds. The existing installation was not changed."
        }
        if ($null -ne $daemonProcessId -and -not (Wait-ProcessExit -TargetProcessId $daemonProcessId)) {
            throw "The previous VBD process (PID $daemonProcessId) did not exit within 10 seconds. The existing installation was not changed."
        }
        Write-Host "VBD stopped." -ForegroundColor Green
    }

    $existingExecutable = Join-Path $InstallDir "vb.exe"
    Assert-StableInstallTarget -Path $InstallDir
    if (-not (Wait-ExecutableReadyForReplacement -Executable $existingExecutable)) {
        throw "The installed vb.exe is still in use. Close running VibeRails windows and retry; no application files were replaced."
    }

    # Overlay only release files. Never recursively remove ~/.vibe_rails: it also
    # contains state.db, environments, logs, models, sandboxes, and user scripts.
    if (-not (Test-Path -LiteralPath $InstallDir)) {
        New-Item -ItemType Directory -Path $InstallDir | Out-Null
    }
    if ($daemonWasInstalled) {
        $recoveryNeeded = $true
    }

    Assert-NoDestinationReparsePoints -PayloadDir $payloadDir -InstallDir $InstallDir

    Write-Host "Installing application files to $InstallDir..." -ForegroundColor Cyan
    foreach ($item in Get-ChildItem -LiteralPath $payloadDir -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $InstallDir -Recurse -Force
    }

    Install-BertV2ModelAssets -RootDir $InstallDir

    $installedExecutable = Join-Path $InstallDir "vb.exe"
    if ($daemonWasInstalled) {
        Write-Host "Repairing current-user VBD registration..." -ForegroundColor Cyan
        if (-not (Invoke-VbdAction -Executable $installedExecutable -Action "repair")) {
            throw "VBD registration repair failed."
        }
    }

    if ($daemonWasRunning) {
        Write-Host "Restarting VBD because it was running before the update..." -ForegroundColor Cyan
        if (-not (Invoke-VbdAction -Executable $installedExecutable -Action "start")) {
            throw "VBD restart failed."
        }
        if (-not (Wait-VbdRunningState -Executable $installedExecutable -ExpectedRunning $true)) {
            throw "VBD did not report running within 10 seconds after restart."
        }
        Write-Host "VBD restarted." -ForegroundColor Green
    } elseif ($daemonWasInstalled) {
        Write-Host "VBD registration repaired; it remains stopped." -ForegroundColor Green
    }

    if ($daemonWasInstalled -or $daemonWasRunning) {
        $recoveryNeeded = $false
    }

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

} catch {
    if ($recoveryNeeded) {
        Show-VbdRecoveryCommands `
            -Executable (Join-Path $InstallDir "vb.exe") `
            -WasRunning $daemonWasRunning
    }
    throw
} finally {
    Remove-InstallerStagingDirectory -Path $tempDir
}
