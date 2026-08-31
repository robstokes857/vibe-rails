#!/usr/bin/env pwsh
# local_deploy.ps1 - Build and deploy VibeRails locally for testing
# Publishes a Release AOT build to deploy/artifacts/aot/win-x64 then copies to ~/.vibe_rails

$ErrorActionPreference = "Stop"

$SkipBuild = $args | Where-Object { $_ -match "skip" -and $_ -match "build" }

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $RepoRoot "VibeRails" "VibeRails.csproj"
$PublishDir = Join-Path $RepoRoot "deploy" "artifacts" "aot" "win-x64"
$InstallDir = Join-Path $env:USERPROFILE ".vibe_rails"

function Assert-DeployPayload {
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
        if (-not (Test-Path -LiteralPath (Join-Path $RootDir $relativePath) -PathType Leaf)) {
            throw "Publish output is incomplete: required file '$relativePath' is missing. The installed application was not changed."
        }
    }

    $protectedNames = @("config.json", "envs", "history", "logs", "sandboxes")
    foreach ($entry in Get-ChildItem -LiteralPath $RootDir -Force) {
        $isStateDatabase = $entry.Name.StartsWith("state.db", [StringComparison]::OrdinalIgnoreCase)
        $isProtectedName = $protectedNames -icontains $entry.Name
        $isRuntimeModels = $entry.Name.Equals("models", [StringComparison]::OrdinalIgnoreCase) -and
            -not $entry.Name.Equals("Models", [StringComparison]::Ordinal)
        if ($isStateDatabase -or $isProtectedName -or $isRuntimeModels) {
            throw "Publish output contains protected user-data path '$($entry.Name)'. The installed application was not changed."
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
        throw "Deployment target must be the current user's stable directory: $expectedPath"
    }

    if (-not (Test-Path -LiteralPath $actualPath)) {
        return
    }

    $item = Get-Item -LiteralPath $actualPath -Force
    if (-not $item.PSIsContainer) {
        throw "Deployment target exists but is not a directory: $actualPath"
    }
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Deployment target must not be a symlink or junction: $actualPath"
    }

    $owner = (Get-Acl -LiteralPath $actualPath).GetOwner(
        [System.Security.Principal.SecurityIdentifier])
    $currentUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
    if ($null -eq $currentUser -or $owner.Value -ne $currentUser.Value) {
        throw "Deployment target must be owned by the current user: $actualPath"
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
    # path (e.g. a planted 'wwwroot' junction would redirect application files into another
    # directory). Refuse to copy while any such link exists.
    $payloadRoot = [System.IO.Path]::GetFullPath($PayloadDir)
    foreach ($item in Get-ChildItem -LiteralPath $payloadRoot -Recurse -Force) {
        $relative = $item.FullName.Substring($payloadRoot.Length).TrimStart('', '/')
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
            # Retry while Task Scheduler settles the current-user task state.
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
    Write-Host "After resolving the deploy error, run these current-user commands:" -ForegroundColor Yellow
    Write-Host "  & '$quotedExecutable' --job-daemon-service repair" -ForegroundColor White
    if ($WasRunning) {
        Write-Host "  & '$quotedExecutable' --job-daemon-service start" -ForegroundColor White
    }
    Write-Host ""
}

function Remove-DeployStagingDirectory {
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

Write-Host ""
Write-Host "  Local Deploy - VibeRails" -ForegroundColor Magenta
Write-Host "  Project:    $Project" -ForegroundColor Cyan
Write-Host "  Build dir:  $PublishDir" -ForegroundColor Cyan
Write-Host "  Target dir: $InstallDir" -ForegroundColor Cyan
Write-Host ""

# Publish AOT build to the standard artifacts location
if ($SkipBuild) {
    Write-Host "Skipping build." -ForegroundColor Yellow
} else {
    Write-Host "Publishing Release AOT build..." -ForegroundColor Cyan
    dotnet publish $Project -c Release -r win-x64 --self-contained true -o $PublishDir /p:PublishAot=true /p:StripSymbols=true /p:InvariantGlobalization=true
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Build failed!" -ForegroundColor Red
        exit 1
    }
    Write-Host "Build succeeded." -ForegroundColor Green
}

$stageDir = Join-Path ([System.IO.Path]::GetTempPath()) "vibe_rails_local_deploy_$([Guid]::NewGuid().ToString('N'))"
$payloadDir = Join-Path $stageDir "payload"
New-Item -ItemType Directory -Path $stageDir | Out-Null
New-Item -ItemType Directory -Path $payloadDir | Out-Null

$daemonWasInstalled = $false
$daemonWasRunning = $false
$recoveryNeeded = $false

try {
    # Copy into a private stage so a partially written publish directory can never
    # be overlaid into the current installation while it is still being produced.
    foreach ($item in Get-ChildItem -LiteralPath $PublishDir -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $payloadDir -Recurse -Force
    }
    Assert-DeployPayload -RootDir $payloadDir
    Assert-StableInstallTarget -Path $InstallDir
    Write-Host "Publish payload validated." -ForegroundColor Green

    $stagedExecutable = Join-Path $payloadDir "vb.exe"
    try {
        $daemonStatus = Get-VbdStatus -Executable $stagedExecutable
    } catch {
        throw "Could not determine VBD state from the staged build. $($_.Exception.Message) The installed application was not changed."
    }

    $daemonState = [string]$daemonStatus.state
    $daemonWasInstalled = [bool]$daemonStatus.isInstalled
    # isReachable guards against a status whose isRunning was computed while the process
    # was still starting; either signal means a live daemon must stop before file swaps.
    $daemonWasRunning = ([bool]$daemonStatus.isRunning) -or ([bool]$daemonStatus.isReachable)
    $daemonProcessId = if ($null -ne $daemonStatus.pid) { [int]$daemonStatus.pid } else { $null }

    # An Error state means VBD's own view of the registration is broken; an Unavailable
    # state alongside an active daemon/registration means lifecycle control (including
    # stop) cannot work. Proceeding would replace files under a running daemon.
    if ($daemonState -eq "Error") {
        throw "VBD reported lifecycle state 'Error' ($($daemonStatus.lastError)). Resolve it (vb --job-daemon-service status) and retry. The installed application was not changed."
    }
    if ($daemonState -eq "Unavailable" -and ($daemonWasInstalled -or $daemonWasRunning)) {
        throw "VBD lifecycle support is unavailable while a VBD process or registration appears active ($($daemonStatus.lastError)). Resolve it and retry. The installed application was not changed."
    }
    if ($daemonWasInstalled) {
        $daemonState = if ($daemonWasRunning) { "running" } else { "stopped" }
        Write-Host "Detected installed VBD ($daemonState)." -ForegroundColor Cyan
    } else {
        Write-Host "VBD is not installed for the current user." -ForegroundColor Cyan
    }

    if ($daemonWasInstalled -or $daemonWasRunning) {
        Write-Host "Ensuring VBD is stopped before replacing files..." -ForegroundColor Cyan
        $recoveryNeeded = $true
        if (-not (Invoke-VbdAction -Executable $stagedExecutable -Action "stop")) {
            throw "Could not stop VBD. The installed application was not changed."
        }
        if (-not (Wait-VbdRunningState -Executable $stagedExecutable -ExpectedRunning $false)) {
            throw "VBD did not stop within 10 seconds. The installed application was not changed."
        }
        if ($null -ne $daemonProcessId -and -not (Wait-ProcessExit -TargetProcessId $daemonProcessId)) {
            throw "The previous VBD process (PID $daemonProcessId) did not exit within 10 seconds. The installed application was not changed."
        }
        Write-Host "VBD stopped." -ForegroundColor Green
    }

    # Other dashboard/terminal vb processes also hold vb.exe open on Windows.
    # Preserve local_deploy's existing behavior after giving VBD a graceful stop.
    $vbProcs = Get-Process -Name "vb" -ErrorAction SilentlyContinue
    if ($vbProcs) {
        Write-Host "Stopping remaining running vb processes for local deploy..." -ForegroundColor Yellow
        $vbProcs | Stop-Process -Force
        Start-Sleep -Seconds 1
    }

    $existingExecutable = Join-Path $InstallDir "vb.exe"
    Assert-StableInstallTarget -Path $InstallDir
    if (-not (Wait-ExecutableReadyForReplacement -Executable $existingExecutable)) {
        throw "The installed vb.exe is still in use after stopping local processes. No application files were replaced."
    }

    if (-not (Test-Path -LiteralPath $InstallDir)) {
        New-Item -ItemType Directory -Path $InstallDir | Out-Null
    }
    if ($daemonWasInstalled) {
        $recoveryNeeded = $true
    }

    Assert-NoDestinationReparsePoints -PayloadDir $payloadDir -InstallDir $InstallDir

    # Overlay application files; never delete ~/.vibe_rails because it also owns
    # the database, environments, logs, models, sandboxes, and user scripts.
    Write-Host "Deploying to $InstallDir..." -ForegroundColor Cyan
    foreach ($item in Get-ChildItem -LiteralPath $payloadDir -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $InstallDir -Recurse -Force
    }

    $installedExecutable = Join-Path $InstallDir "vb.exe"
    if ($daemonWasInstalled) {
        Write-Host "Repairing current-user VBD registration..." -ForegroundColor Cyan
        if (-not (Invoke-VbdAction -Executable $installedExecutable -Action "repair")) {
            throw "VBD registration repair failed."
        }
    }

    if ($daemonWasRunning) {
        Write-Host "Restarting VBD because it was running before the deploy..." -ForegroundColor Cyan
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

    Write-Host ""
    Write-Host "Deploy complete!" -ForegroundColor Green
    Write-Host "Run 'vb --launch-web' to test." -ForegroundColor Yellow
    Write-Host ""
} catch {
    if ($recoveryNeeded) {
        Show-VbdRecoveryCommands `
            -Executable (Join-Path $InstallDir "vb.exe") `
            -WasRunning $daemonWasRunning
    }
    throw
} finally {
    Remove-DeployStagingDirectory -Path $stageDir
}
