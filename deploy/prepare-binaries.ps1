#!/usr/bin/env pwsh
# prepare-binaries.ps1 - Copy AOT binaries + wwwroot to extension bin/ folder
# Usage:
#   npm run prepare-binaries
#   pwsh ../deploy/prepare-binaries.ps1
#   pwsh ../deploy/prepare-binaries.ps1 -Targets win32-x64

param(
    [string[]]$Targets = @(),
    [string]$ArtifactsRoot = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$ScriptDir = Split-Path -Parent $PSCommandPath
$RepoRoot = Split-Path -Parent $ScriptDir
$ExtensionRoot = Join-Path $RepoRoot "vscode-viberails"
$ArtifactsDir = if ($ArtifactsRoot) { $ArtifactsRoot } else { Join-Path $RepoRoot "Scripts" "artifacts" "aot" }
$WwwrootSource = Join-Path $RepoRoot "VibeRails" "wwwroot"
$BinDir = Join-Path $ExtensionRoot "bin"
$supportedTargets = @("win32-x64", "linux-x64", "darwin-arm64")

Write-Host "VibeRails Extension - Binary Preparation" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Check if AOT binaries exist
$targetConfigs = @{
    "win32-x64" = @{ SourceDir = "win-x64"; Binary = "vb.exe" }
    "linux-x64" = @{ SourceDir = "linux-x64"; Binary = "vb" }
    "darwin-arm64" = @{ SourceDir = "osx-arm64"; Binary = "vb" }
}

if ($Targets.Count -eq 0) {
    $Targets = @(
        $supportedTargets | Where-Object {
            $config = $targetConfigs[$_]
            Test-Path (Join-Path (Join-Path $ArtifactsDir $config.SourceDir) $config.Binary)
        }
    )
}

$invalidTargets = @($Targets | Where-Object { $_ -notin $supportedTargets })
if ($invalidTargets.Count -gt 0) {
    throw "Unsupported target(s): $($invalidTargets -join ', '). Supported targets: $($supportedTargets -join ', ')"
}

if ($Targets.Count -eq 0) {
    throw "No AOT binaries found under ${ArtifactsDir}. Build or download the target backends before packaging the extension."
}

$missingBinaries = @()
foreach ($target in $Targets) {
    $config = $targetConfigs[$target]
    $binaryDir = Join-Path $ArtifactsDir $config.SourceDir
    $binaryPath = Join-Path $binaryDir $config.Binary
    if (-not (Test-Path $binaryPath)) {
        $missingBinaries += "$($config.SourceDir)/$($config.Binary)"
    }
}

if ($missingBinaries.Count -gt 0) {
    $missingList = $missingBinaries -join ", "
    throw "Missing AOT binaries under ${ArtifactsDir}: $missingList. Build the required AOT binaries before packaging the extension."
}

# Check if wwwroot exists
if (-not (Test-Path $WwwrootSource)) {
    throw "wwwroot not found at $WwwrootSource"
}

# Clean and create bin directory structure
Write-Host "Preparing bin directory structure..." -ForegroundColor Cyan
if (Test-Path $BinDir) {
    Write-Host "  Cleaning existing bin/" -ForegroundColor Gray
    Remove-Item -Recurse -Force $BinDir
}

$platforms = foreach ($target in $Targets) {
    $config = $targetConfigs[$target]
    @{
        Name = $target
        SourceDir = $config.SourceDir
        Binary = $config.Binary
    }
}

foreach ($platform in $platforms) {
    $platformDir = Join-Path $BinDir $platform.Name
    New-Item -ItemType Directory -Force -Path $platformDir | Out-Null
    Write-Host "  Created bin/$($platform.Name)/" -ForegroundColor Green

    # Copy binary
    $sourceBinary = Join-Path (Join-Path $ArtifactsDir $platform.SourceDir) $platform.Binary
    $destBinary = Join-Path $platformDir $platform.Binary
    Copy-Item -Path $sourceBinary -Destination $destBinary -Force
    Write-Host "    Copied $($platform.Binary)" -ForegroundColor Green

    # Copy appsettings.json
    $sourceAppSettings = Join-Path (Join-Path $ArtifactsDir $platform.SourceDir) "appsettings.json"
    if (Test-Path $sourceAppSettings) {
        Copy-Item -Path $sourceAppSettings -Destination (Join-Path $platformDir "appsettings.json") -Force
        Write-Host "    Copied appsettings.json" -ForegroundColor Green
    } else {
        throw "appsettings.json not found at $sourceAppSettings"
    }

    # Copy wwwroot
    $destWwwroot = Join-Path $platformDir "wwwroot"
    Copy-Item -Path $WwwrootSource -Destination $destWwwroot -Recurse -Force
    $fileCount = (Get-ChildItem -Path $destWwwroot -Recurse -File).Count
    Write-Host "    Copied wwwroot/ ($fileCount files)" -ForegroundColor Green

    # Copy remaining AOT publish artifacts (native DLLs from NuGet runtime packages:
    # onnxruntime, e_sqlite3, vec0, winpty, plus winpty-agent.exe). NativeAOT emits
    # these next to vb.exe; without them vb.exe falls back to the system DLL search
    # path and can load a mismatched onnxruntime.dll, which crashes at the
    # CompileApi cctor under ORT >= 1.24.
    $sourceDir = Join-Path $ArtifactsDir $platform.SourceDir
    $skipNames = @($platform.Binary, 'appsettings.json')
    # Excluded extensions: NativeAOT debug symbols are 50-115 MB each (.pdb on
    # Windows, .dbg on Linux, .dwarf on macOS); xmldoc files are runtime-irrelevant.
    # Keeps the VSIX lean. Shipping .dbg also tripped vsce's secret scanner, which
    # matched a github_pat_-shaped byte run inside the 114 MB linux symbol blob.
    $skipExtensions = @('.pdb', '.xml', '.dbg', '.dwarf')
    $extraFiles = Get-ChildItem -Path $sourceDir -File | Where-Object {
        $skipNames -notcontains $_.Name -and $skipExtensions -notcontains $_.Extension
    }
    foreach ($f in $extraFiles) {
        Copy-Item -Path $f.FullName -Destination (Join-Path $platformDir $f.Name) -Force
    }
    if ($extraFiles.Count -gt 0) {
        Write-Host "    Copied $($extraFiles.Count) additional publish files (native DLLs, etc.)" -ForegroundColor Green
    }

    # Copy the scripts/ subdirectory (git hook scripts, BERT download scripts).
    # Subdirectories are invisible to the top-level extra-files loop above —
    # without this the packaged app cannot install or repair Git Guard hooks
    # ("Hook script 'pre-commit-hook.sh' not found"). On Linux publishes the hook scripts
    # ('scripts') and download scripts ('Scripts') are two distinct directories.
    $scriptDirs = @(Get-ChildItem -Path $sourceDir -Directory | Where-Object { $_.Name -ieq 'scripts' })
    foreach ($dir in $scriptDirs) {
        $destScripts = Join-Path $platformDir $dir.Name
        New-Item -ItemType Directory -Path $destScripts -Force | Out-Null
        Copy-Item -Path (Join-Path $dir.FullName '*') -Destination $destScripts -Recurse -Force
        Write-Host "    Copied $($dir.Name)/" -ForegroundColor Green
    }

    # Set execute permissions on Linux binary (no-op on Windows)
    if ($platform.Binary -eq "vb" -and -not $IsWindows) {
        chmod +x $destBinary
        Write-Host "    Set execute permissions" -ForegroundColor Green
    }

    # Validate structure
    $indexHtml = Join-Path $destWwwroot "index.html"
    if (-not (Test-Path $indexHtml)) {
        Write-Host "    Warning: index.html not found in wwwroot" -ForegroundColor Yellow
    }

    # Assert native ONNX Runtime library is present. Missing this silently worked
    # under ORT 1.21 but crashes under ORT 1.24+ because the managed cctor binds
    # the CompileApi function-pointer table at static-init time. If the DLL isn't
    # side-by-side, the CLR loads a mismatched one via DLL search and ArgumentNull
    # /0xC0000005 follows. Fail the build instead of shipping a broken package.
    $ortPresent = Get-ChildItem -Path $platformDir -File | Where-Object { $_.Name -match '(?i)onnxruntime' } | Select-Object -First 1
    if (-not $ortPresent) {
        throw "Native ONNX Runtime library missing from bin/$($platform.Name)/. Expected an onnxruntime.{dll,so,dylib} alongside vb. Did the AOT publish emit it into $sourceDir?"
    }

    # Assert the git hook scripts shipped. HookInstallationService loads them from
    # <base>/scripts at runtime; without them Git Guard's Install/Repair fails on the
    # user's machine with "Hook script 'pre-commit-hook.sh' not found".
    foreach ($hookScript in @('pre-commit-hook.sh', 'commit-msg-hook.sh')) {
        $shipped = @(Get-ChildItem -Path $platformDir -Directory | Where-Object { $_.Name -ieq 'scripts' }) |
            Where-Object { Test-Path (Join-Path $_.FullName $hookScript) }
        if (-not $shipped) {
            throw "Hook script $hookScript missing from bin/$($platform.Name)/scripts/. Git Guard hook install/repair would fail at runtime. Did the AOT publish emit VibeRails/scripts/ into $sourceDir?"
        }
    }
}

# Display summary
Write-Host ""
Write-Host "Binary preparation complete!" -ForegroundColor Green
Write-Host ""
Write-Host "Platform packages ready:" -ForegroundColor Cyan
foreach ($platform in $platforms) {
    $platformDir = Join-Path $BinDir $platform.Name
    $binaryPath = Join-Path $platformDir $platform.Binary
    $binarySize = [math]::Round((Get-Item $binaryPath).Length / 1MB, 2)
    $wwwrootSize = [math]::Round((Get-ChildItem -Path (Join-Path $platformDir "wwwroot") -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 2)
    $totalSize = $binarySize + $wwwrootSize
    Write-Host "  $($platform.Name): " -NoNewline -ForegroundColor Cyan
    Write-Host "$($totalSize) MB " -NoNewline -ForegroundColor Yellow
    Write-Host "($($binarySize) MB binary + $($wwwrootSize) MB wwwroot)" -ForegroundColor Gray
}

Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "  1. npm run compile" -ForegroundColor White
foreach ($platform in $platforms) {
    Write-Host "  - npm run package:$($platform.Name)" -ForegroundColor White
}
Write-Host ""
