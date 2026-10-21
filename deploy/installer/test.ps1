#!/usr/bin/env pwsh
# End-to-end installer checks use a separately identified, disposable fixture.
# They never launch the app against the developer's normal state/board databases.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PayloadDirectory,
    [Parameter(Mandatory)][string]$Compiler,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../../.test-artifacts/installer-test')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$output = [IO.Path]::GetFullPath($OutputDirectory)
$profileName = 'profile ' + [char]0x00e9 + " O'Brien" + [char]0x2019
$root = Join-Path $output "$profileName/.vibe_rails"
$ownerKey = 'HKCU:/Software/VibeRails/InstallerFixture'
$uninstallKey = 'HKCU:/Software/Microsoft/Windows/CurrentVersion/Uninstall/{AF20777F-58B9-45F7-8E55-200AC00ED0E7}_is1'
if ((Test-Path -LiteralPath $root) -or (Test-Path -LiteralPath $uninstallKey)) {
    throw 'An earlier installer fixture is present. Inspect and uninstall it before rerunning.'
}
$environmentKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment')
try {
    $pathExisted = $null -ne $environmentKey -and $environmentKey.GetValueNames() -icontains 'Path'
    $oldPath = if ($pathExisted) {
        $environmentKey.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    } else { '' }
    $pathKind = if ($pathExisted) { $environmentKey.GetValueKind('Path') } else { [Microsoft.Win32.RegistryValueKind]::ExpandString }
} finally {
    if ($null -ne $environmentKey) { $environmentKey.Dispose() }
}
if (Test-Path -LiteralPath $ownerKey) { throw 'An earlier fixture PATH ownership record is present.' }
$desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) 'VibeRails Installer Test.lnk'
$startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'VibeRails Installer Test.lnk'
foreach ($shortcut in @($desktop, $startMenu)) {
    if (Test-Path -LiteralPath $shortcut) { throw "Fixture shortcut already exists: $shortcut" }
}

function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Get-RawUserPath {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment')
    if ($null -eq $key) { return '' }
    try { $key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) }
    finally { $key.Dispose() }
}
function Invoke-Installer([string]$Executable, [string]$Label, [bool]$Succeed = $true, [string[]]$Extra = @()) {
    $log = Join-Path $output "$Label.log"
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/LOG="' + $log + '"')) + $Extra
    # -Wait includes descendants. Inno's uninstaller runs a temporary self-copy;
    # waiting only on the original process would race the actual uninstall.
    $process = Start-Process -FilePath $Executable -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
    if ($Succeed) { Assert ($process.ExitCode -eq 0) "$Label failed ($($process.ExitCode)); see $log" }
    else { Assert ($process.ExitCode -ne 0) "$Label unexpectedly succeeded; see $log" }
}

$uninstaller = Join-Path $root 'installer/unins000.exe'
try {
    New-Item -ItemType Directory -Force -Path $output | Out-Null
    # Reject a contaminated release before any compiler invocation or model
    # extraction. These are inert fixture files, not application databases.
    $invalidPayload = Join-Path $output 'invalid-payload'
    $requiredFiles = @('vb.exe', 'appsettings.json', 'wwwroot/index.html', 'onnxruntime.dll',
        'e_sqlite3.dll', 'vec0.dll', 'scripts/pre-commit-hook.sh', 'scripts/commit-msg-hook.sh',
        'Models/BertV2/model.onnx.zip', 'Models/BertV2/vocab.txt', 'msvcp140.dll',
        'msvcp140_1.dll', 'vcruntime140.dll', 'vcruntime140_1.dll', 'board.db')
    foreach ($relative in $requiredFiles) {
        $file = Join-Path $invalidPayload $relative
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $file) | Out-Null
        [IO.File]::WriteAllText($file, 'fixture')
    }
    $rejected = $false
    try {
        & (Join-Path $PSScriptRoot '../build-installer.ps1') -PayloadDirectory $invalidPayload `
            -Version 0.0.1 -OutputDirectory (Join-Path $output 'rejected') -Compiler $Compiler | Write-Host
    } catch {
        if ($_.Exception.Message -notlike '*Unrecognized release file*board.db*') { throw }
        $rejected = $true
    }
    Assert $rejected 'Installer accepted a release payload containing user data'
    & (Join-Path $PSScriptRoot '../build-installer.ps1') -PayloadDirectory $PayloadDirectory `
        -Version 0.0.1 -OutputDirectory $output -Compiler $Compiler -TestRoot $root | Write-Host
    $setup = Join-Path $output 'VibeRails-Fixture-0.0.1-win-x64.exe'
    # A substring match would mistake this unrelated path for the install entry.
    [Environment]::SetEnvironmentVariable('Path', "$oldPath;$root-backup", 'User')
    Invoke-Installer $setup 'fresh' -Extra @('/TASKS=desktopicon')
    Assert (Test-Path -LiteralPath $uninstaller) 'Uninstaller was not installed'
    Assert ((Get-Item -LiteralPath (Join-Path $root 'models/bertv2/model.onnx')).Length -gt 1000000) 'Runtime model was not extracted'
    Assert (Test-Path -LiteralPath $desktop) 'Desktop shortcut is missing'
    Assert (Test-Path -LiteralPath $startMenu) 'Start menu shortcut is missing'
    $shell = New-Object -ComObject WScript.Shell
    foreach ($shortcut in @($desktop, $startMenu)) {
        $link = $shell.CreateShortcut($shortcut)
        Assert ($link.TargetPath -eq (Join-Path $root 'vb.exe')) 'Shortcut target is incorrect'
        Assert ($link.Arguments -eq '--web') 'Shortcut launch arguments are incorrect'
        Assert ($link.IconLocation.StartsWith((Join-Path $root 'viberails.ico'))) 'Shortcut icon is incorrect'
    }
    $savedPath = Get-RawUserPath
    Assert (($savedPath -split ';') -contains $root) 'Exact install PATH entry was not added'
    Assert (Test-Path -LiteralPath $ownerKey) 'PATH ownership was not recorded'

    $sentinels = @('state.db', 'state.db-wal', 'state.db-shm', 'board.db', 'board.db-wal',
        'board.db-shm', 'search.db', 'search.db-wal', 'config.json', 'appsettings.json',
        'logs/keep.txt', 'envs/keep.txt', 'sandboxes/keep.txt', 'scripts/custom.ps1',
        'models/custom/model.bin', 'models/bertv2/vocab.txt')
    foreach ($relative in $sentinels) {
        $file = Join-Path $root $relative
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $file) | Out-Null
        [IO.File]::WriteAllText($file, "keep:$relative")
    }
    Invoke-Installer $setup 'upgrade'
    Assert ((Get-RawUserPath) -eq $savedPath) 'Repeat installation duplicated or changed PATH'

    $vb = Join-Path $root 'vb.exe'
    $hash = (Get-FileHash -LiteralPath $vb).Hash
    $lock = [IO.File]::Open($vb, 'Open', 'Read', 'None')
    try {
        Invoke-Installer $setup 'locked-upgrade' $false
        Invoke-Installer $uninstaller 'locked-uninstall' $false
    } finally { $lock.Dispose() }
    Assert ((Get-FileHash -LiteralPath $vb).Hash -eq $hash) 'A locked installation was modified'

    $mutexHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::Unicode.GetBytes([Environment]::GetFolderPath('UserProfile')))).ToLowerInvariant()
    $mutex = [Threading.Mutex]::new($false, "Global\VibeRailsInstaller-$mutexHash")
    try { Invoke-Installer $setup 'concurrent-setup' $false }
    finally { $mutex.Dispose() }

    $wwwroot = Join-Path $root 'wwwroot'
    $parked = Join-Path $root 'wwwroot-original'
    $outside = Join-Path $output 'outside'
    New-Item -ItemType Directory -Path $outside | Out-Null
    $outsideFile = Join-Path $outside 'index.html'
    [IO.File]::WriteAllText($outsideFile, 'outside sentinel')
    # Both resolved paths are inside the disposable fixture before moving.
    Assert ($wwwroot.StartsWith($output + '\')) 'Unsafe fixture source'
    Assert ($parked.StartsWith($output + '\')) 'Unsafe fixture destination'
    Move-Item -LiteralPath $wwwroot -Destination $parked
    New-Item -ItemType Junction -Path $wwwroot -Target $outside | Out-Null
    try {
        Invoke-Installer $setup 'junction-upgrade' $false
        Invoke-Installer $uninstaller 'junction-uninstall' $false
        Assert ([IO.File]::ReadAllText($outsideFile) -eq 'outside sentinel') 'A destination junction was followed'
    } finally {
        Remove-Item -LiteralPath $wwwroot -Force
        Move-Item -LiteralPath $parked -Destination $wwwroot
    }

    Invoke-Installer $uninstaller 'uninstall'
    Assert (-not (Test-Path -LiteralPath $vb)) 'Application executable remained after uninstall'
    Assert (-not (Test-Path -LiteralPath $desktop)) 'Desktop shortcut remained after uninstall'
    Assert (-not (Test-Path -LiteralPath $startMenu)) 'Start menu shortcut remained after uninstall'
    Assert ((Get-RawUserPath) -eq "$oldPath;$root-backup") 'Uninstall changed unrelated PATH entries'
    foreach ($relative in $sentinels) {
        Assert ([IO.File]::ReadAllText((Join-Path $root $relative)) -eq "keep:$relative") "User data changed: $relative"
    }
    Assert (Test-Path -LiteralPath (Join-Path $root 'models/bertv2/model.onnx')) 'Uninstall removed the runtime model'

    # An existing script-installed PATH entry belongs to the user. Preserve it,
    # including quotes, casing, trailing slash, and every unrelated segment.
    $preexistingPath = "$oldPath;`"$($root.ToUpperInvariant())\`";$root-backup"
    [Environment]::SetEnvironmentVariable('Path', $preexistingPath, 'User')
    Invoke-Installer $setup 'reinstall-existing-path'
    Assert (-not (Test-Path -LiteralPath $ownerKey)) 'Installer claimed a pre-existing PATH entry'
    Invoke-Installer $uninstaller 'uninstall-existing-path'
    Assert ((Get-RawUserPath) -eq $preexistingPath) 'A pre-existing PATH entry was removed'
    Write-Host 'Installer lifecycle passed: fresh install, shortcuts, models, upgrade, locks, serialization, junctions, retained data, PATH, uninstall and reinstall.'
} finally {
    # This is a dedicated fixture with its own AppId, shortcuts and ownership key.
    # Keep logs and retained sentinel files for inspection, and restore user PATH.
    try {
        if (Test-Path -LiteralPath $uninstaller) {
            Invoke-Installer $uninstaller 'cleanup'
        }
    } finally {
        $environmentKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Environment')
        try {
            if ($pathExisted) { $environmentKey.SetValue('Path', $oldPath, $pathKind) }
            else { $environmentKey.DeleteValue('Path', $false) }
        }
        finally { $environmentKey.Dispose() }
    }
}
