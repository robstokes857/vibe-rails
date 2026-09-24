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

# vsce emits the "you should bundle your extension" warning when a VSIX holds more
# than this many .js files (@vscode/vsce/out/package.js: `jsFiles.length > 100`).
$VsceJsFileLimit = 100

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

    # Prune web assets the app never requests, so the VSIX stays under vsce's
    # JavaScript-file warning ("This extension consists of N files, out of which M
    # are JavaScript files" - it fires above 100 .js files). Pruning has to happen
    # here rather than in .vscodeignore: package-platforms.ps1 appends a
    # `!bin/<target>/**` negate pattern, and vsce's filter keeps a file when it is
    # negated by ANY pattern regardless of order (@vscode/vsce/out/package.js),
    # so no ignore rule can carve anything back out of the staged backend.
    #
    # These stay in VibeRails/wwwroot for source runs and Monaco upgrades; only the
    # packaged copy is slimmed.
    $prunePaths = @(
        # Localisation bundles. editor.main.js fetches vs/nls.messages.<locale> only
        # when require.config sets vs/nls.availableLanguages to a non-English locale,
        # which monaco-loader.js never does.
        "assets/monaco/vs/nls.messages.*.js"

        # Bootstrap variants index.html does not reference; it loads only
        # bootstrap.bundle.min.js (plus bootstrap.min.css, which is kept).
        "assets/bootstrap.bundle.js"
        "assets/bootstrap.bundle.js.map"
        "assets/bootstrap.esm.js"
        "assets/bootstrap.esm.js.map"
        "assets/bootstrap.esm.min.js"
        "assets/bootstrap.esm.min.js.map"
        "assets/bootstrap.js"
        "assets/bootstrap.js.map"
        "assets/bootstrap.min.js"
        "assets/bootstrap.min.js.map"

        # xterm addons index.html never loads. addon-clipboard is deliberately left
        # out (index.html: "PTY-controlled OSC 52 must not access the browser
        # clipboard"), the rest were never wired up.
        "assets/xterm/addon-clipboard.*"
        "assets/xterm/addon-image.*"
        "assets/xterm/addon-ligatures.*"
        "assets/xterm/addon-web-fonts.js"
    )

    foreach ($prunePath in $prunePaths) {
        $target = Join-Path $destWwwroot ($prunePath -replace '/', [System.IO.Path]::DirectorySeparatorChar)
        $matched = @(Get-Item -Path $target -ErrorAction SilentlyContinue)
        if ($matched.Count -eq 0) {
            throw "Nothing matched prune path '$prunePath' under $destWwwroot. Did the asset move or get removed? Update the prune list in $($MyInvocation.MyCommand.Name)."
        }
        foreach ($item in $matched) {
            Remove-Item -Path $item.FullName -Recurse -Force
        }
    }

    # Replace Monaco's 81 per-language AMD modules with the single concatenated
    # bundle. They are all *named* defines, so the bundle registers exactly the same
    # module ids; factories still run lazily on first use. monaco-loader.js requires
    # 'vs/basic-languages/all' before editor.main, so the bundle must exist.
    $languageDir = Join-Path $destWwwroot "assets/monaco/vs/basic-languages"
    $languageBundle = Join-Path $languageDir "all.js"
    if (-not (Test-Path $languageBundle)) {
        throw "Monaco language bundle not found at $languageBundle. Run: pwsh deploy/build-monaco-language-bundle.ps1"
    }
    $languageDirs = @(Get-ChildItem -Path $languageDir -Directory)
    foreach ($dir in $languageDirs) {
        Remove-Item -Path $dir.FullName -Recurse -Force
    }

    $prunedCount = $fileCount - (Get-ChildItem -Path $destWwwroot -Recurse -File).Count
    Write-Host "    Pruned $prunedCount unused wwwroot files ($($languageDirs.Count) Monaco languages folded into all.js)" -ForegroundColor Green

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

    # Report how close the VSIX is to vsce's JavaScript-file limit, which is what
    # produced the "This extension consists of N files, out of which M are JavaScript
    # files" warning on every publish. vsce only warns above the limit, so this does
    # too: a release must never die over a performance advisory (v1.10.21 did).
    #
    # The VSIX's .js files are the staged backend plus the extension's own compiled
    # output. out/ is compiled by vsce's `vscode:prepublish` *after* this script
    # runs, so count the sources it will emit instead: src/*.ts maps 1:1 onto
    # out/*.js, and .vscodeignore drops out/test/**.
    $stagedJsCount = @(Get-ChildItem -Path $platformDir -Recurse -File -Filter *.js).Count
    $srcDir = Join-Path $ExtensionRoot "src"
    $testSrcDir = Join-Path $srcDir "test"
    $extensionJsCount = @(
        Get-ChildItem -Path $srcDir -Recurse -File -Filter *.ts |
            Where-Object { $_.Name -notlike '*.d.ts' -and -not $_.FullName.StartsWith($testSrcDir, [System.StringComparison]::OrdinalIgnoreCase) }
    ).Count
    $totalJsCount = $stagedJsCount + $extensionJsCount

    if ($totalJsCount -gt $VsceJsFileLimit) {
        Write-Warning "bin/$($platform.Name)/ will ship $totalJsCount .js files ($stagedJsCount staged backend + $extensionJsCount compiled from src/), over vsce's limit of $VsceJsFileLimit, so vsce will print its bundling warning. Packaging continues. To clear it, add unused assets to the prune list above or bundle them."
    } else {
        Write-Host "    $totalJsCount .js files will ship ($stagedJsCount backend + $extensionJsCount extension; vsce warns above $VsceJsFileLimit)" -ForegroundColor Gray
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
