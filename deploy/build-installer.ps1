#!/usr/bin/env pwsh
<#
.SYNOPSIS
Build a per-user Windows installer from clean win-x64 NativeAOT publish output.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PayloadDirectory,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')][string]$Version,
    [string]$OutputDirectory = 'artifacts/installer',
    [string]$Compiler = 'C:/Program Files (x86)/Inno Setup 6/ISCC.exe',
    [switch]$ReleaseSigning,
    # Disposable automated-install fixture. Never used in a release build.
    [string]$TestRoot
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-InnoSignToolCommand {
    param(
        [Parameter(Mandatory)][string]$PowerShellPath,
        [Parameter(Mandatory)][string]$SigningScript
    )
    # ISCC's /S parser needs its own $q placeholders for embedded quotes.
    # Literal quotes are split into extra script filenames by the native CLI.
    # Escape literal dollars in paths before inserting Inno's placeholders.
    $quotedPowerShell = '$q' + $PowerShellPath.Replace('$', '$$') + '$q'
    $quotedScript = '$q' + $SigningScript.Replace('$', '$$') + '$q'
    return $quotedPowerShell + ' -NoProfile -NonInteractive -File ' + $quotedScript + ' -Path $f'
}

$payload = (Resolve-Path -LiteralPath $PayloadDirectory).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $output | Out-Null
$work = Join-Path $output ('build-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null

try {
    # A closed set of roots prevents an accidental publish from taking ownership
    # of databases, user scripts, credentials, logs or another runtime directory.
    $rootFiles = @('vb.exe', 'appsettings.json', 'appsettings.Development.json', 'vb.staticwebassets.endpoints.json', 'onnxruntime.dll',
        'onnxruntime_providers_shared.dll', 'e_sqlite3.dll', 'vec0.dll')
    $crtFiles = @('concrt140.dll', 'msvcp140.dll', 'msvcp140_1.dll', 'msvcp140_2.dll',
        'msvcp140_atomic_wait.dll', 'msvcp140_codecvt_ids.dll', 'vcruntime140.dll',
        'vcruntime140_1.dll', 'vcruntime140_threads.dll')
    $scripts = @('pre-commit-hook.sh', 'commit-msg-hook.sh', 'post-commit-hook.sh', 'download-bert-model.ps1', 'download-bert-model.sh')
    $required = @('vb.exe', 'appsettings.json', 'wwwroot/index.html', 'onnxruntime.dll',
        'e_sqlite3.dll', 'vec0.dll', 'scripts/pre-commit-hook.sh', 'scripts/commit-msg-hook.sh',
        'Models/BertV2/model.onnx.zip', 'Models/BertV2/vocab.txt',
        'msvcp140.dll', 'msvcp140_1.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')
    foreach ($relative in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $payload $relative) -PathType Leaf)) {
            throw "Incomplete Windows payload: $relative. Run prepare-windows-payload.ps1 after publishing."
        }
    }

    $files = @(Get-ChildItem -LiteralPath $payload -File -Recurse -Force | Sort-Object FullName)
    $entries = [Collections.Generic.List[string]]::new()
    $paths = [Collections.Generic.List[string]]::new()
    $replacePaths = [Collections.Generic.List[string]]::new()
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($payload, $file.FullName).Replace('/', '\')
        # Check the entire source chain, including the root, before accepting it.
        $ancestor = $file.FullName
        while ($ancestor.Length -ge $payload.Length) {
            if (((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Payload contains a symlink or junction: $ancestor"
            }
            $ancestor = Split-Path -Parent $ancestor
        }
        if ($relative -match '["\r\n{}]' -or $file.FullName.Contains('"')) {
            throw "Unsupported installer payload path: $relative"
        }
        $preserve = $false
        if ($relative -imatch '^wwwroot\\') { }
        elseif ($relative -imatch '^scripts\\([^\\]+)$' -and $scripts -icontains $Matches[1]) { }
        elseif ($relative -imatch '^Models\\BertV2\\(model\.onnx\.(zip|gz)|vocab\.txt)$') {
            # Setup ships the materialized model below. Including both compressed
            # archives as well would triple model storage and installer work.
            if ($relative -imatch '\.(zip|gz)$') { continue }
            # Model assets share their case-insensitive directory with runtime
            # models. Install missing assets, never overwrite or uninstall them.
            $preserve = $true
        }
        elseif (($rootFiles + $crtFiles) -icontains $relative) {
            $preserve = $relative -ilike 'appsettings*.json'
        }
        elseif ($relative -imatch '^[^\\]+\.(pdb|dbg|lib|xml)$') { continue }
        else { throw "Unrecognized release file (not safe to overlay user data): $relative" }

        $flags = if ($preserve) { 'onlyifdoesntexist uninsneveruninstall' } else { 'ignoreversion' }
        $destination = Split-Path -Parent $relative
        $entries.Add(('Source: "{0}"; DestDir: "{{app}}\{1}"; Flags: {2}' -f $file.FullName, $destination, $flags))
        $paths.Add($relative)
        if (-not $preserve) { $replacePaths.Add($relative) }
    }

    # Materialize BERT during packaging, so setup needs neither a download nor
    # PowerShell on the target machine. Existing runtime assets remain untouched.
    $modelDir = Join-Path $work 'model'
    Expand-Archive -LiteralPath (Join-Path $payload 'Models/BertV2/model.onnx.zip') -DestinationPath $modelDir
    $model = Join-Path $modelDir 'model.onnx'
    if (-not (Test-Path -LiteralPath $model -PathType Leaf)) { throw 'BERT archive is missing model.onnx' }
    $entries.Add(('Source: "{0}"; DestDir: "{{app}}\models\bertv2"; Flags: onlyifdoesntexist uninsneveruninstall' -f $model))
    $paths.Add('models\bertv2\model.onnx')
    $icon = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../docs/brand/viberails-mark.ico')).Path
    $entries.Add(('Source: "{0}"; DestDir: "{{app}}"; DestName: "viberails.ico"; Flags: ignoreversion' -f $icon))
    $paths.Add('viberails.ico')
    $replacePaths.Add('viberails.ico')

    $fileEntries = Join-Path $work 'files.iss'
    $pathEntries = Join-Path $work 'paths.iss'
    $entries | Set-Content -LiteralPath $fileEntries -Encoding utf8BOM
    $pascal = @('  SetArrayLength(PayloadPaths, ' + $paths.Count + ');')
    for ($i = 0; $i -lt $paths.Count; $i++) {
        $pascal += "  PayloadPaths[$i] := '" + $paths[$i].Replace("'", "''") + "';"
    }
    $pascal += '  SetArrayLength(ReplacePaths, ' + $replacePaths.Count + ');'
    for ($i = 0; $i -lt $replacePaths.Count; $i++) {
        $pascal += "  ReplacePaths[$i] := '" + $replacePaths[$i].Replace("'", "''") + "';"
    }
    $pascal | Set-Content -LiteralPath $pathEntries -Encoding utf8BOM

    $compilerArgs = @('/Qp', "/DAppVersion=$Version", "/DOutputDirectory=$output",
        "/DFileEntries=$fileEntries", "/DPathEntries=$pathEntries")
    if ($TestRoot) {
        if ($ReleaseSigning) { throw 'A fixture installer cannot be release signed.' }
        if ($TestRoot -match '["\r\n{}]') { throw 'Unsupported fixture path' }
        $compilerArgs += '/DTestRoot=' + [IO.Path]::GetFullPath($TestRoot)
        $compilerArgs += '/DTestRootPascal=' + [IO.Path]::GetFullPath($TestRoot).Replace("'", "''")
    }
    if ($ReleaseSigning) {
        # The existing release action installs this exact module. Credentials
        # live only in the signing step's environment, never in command lines.
        $signScript = Join-Path $PSScriptRoot 'installer/sign.ps1'
        $pwshPath = (Get-Command pwsh -ErrorAction Stop).Source
        $signCommand = Get-InnoSignToolCommand -PowerShellPath $pwshPath -SigningScript $signScript
        $compilerArgs += '/DReleaseSigning'
        $compilerArgs += '/Strusted=' + $signCommand
    }
    & $Compiler @compilerArgs (Join-Path $PSScriptRoot 'installer/VibeRails.iss')
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE" }
    $setupName = if ($TestRoot) { "VibeRails-Fixture-$Version-win-x64.exe" } else { "VibeRails-Setup-$Version-win-x64.exe" }
    $setup = Join-Path $output $setupName
    if ($ReleaseSigning -and (Get-AuthenticodeSignature -LiteralPath $setup).Status -ne 'Valid') {
        throw 'Setup signature is invalid; refusing to publish a checksum.'
    }
    $hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($setup))" | Set-Content -LiteralPath "$setup.sha256" -Encoding ascii
    Write-Output $setup
} finally {
    $resolvedWork = [IO.Path]::GetFullPath($work)
    if (-not $resolvedWork.StartsWith($output.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing cleanup outside installer output: $resolvedWork"
    }
    Remove-Item -LiteralPath $resolvedWork -Recurse -Force
}
