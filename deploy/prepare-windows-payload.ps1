#!/usr/bin/env pwsh
# App-local VC++ runtime deployment keeps the installer per-user. Do not run the
# redistributable installer (which requires elevation), or copy System32 DLLs.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PayloadDirectory,
    [string]$RuntimeDirectory
)
$ErrorActionPreference = 'Stop'

function Find-VcRuntimeDirectory {
    param([Parameter(Mandatory)][string]$RedistDirectory)

    # Visual Studio includes toolset aliases such as v143/v145 alongside numeric
    # versions. Prefer the newest numeric version; aliases are a fallback when
    # no versioned directory contains an x64 CRT. Never cast an alias to Version.
    $candidates = foreach ($directory in Get-ChildItem -LiteralPath $RedistDirectory -Directory) {
        $version = $null
        if ([version]::TryParse($directory.Name, [ref]$version)) {
            [pscustomobject]@{ Directory = $directory; Version = $version }
        } elseif ($directory.Name -match '^v(\d+)$') {
            [pscustomobject]@{ Directory = $directory; Version = [version]::new(0, [int]$Matches[1]) }
        }
    }
    $candidates | Sort-Object Version -Descending | ForEach-Object {
        Get-ChildItem -Path (Join-Path $_.Directory.FullName 'x64/Microsoft.VC*.CRT') -Directory -ErrorAction SilentlyContinue
    } | Select-Object -First 1 -ExpandProperty FullName
}

if (-not $RuntimeDirectory) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $vsRoot = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($LASTEXITCODE -ne 0 -or -not $vsRoot) { throw 'Visual C++ redistributable files were not found.' }
    $RuntimeDirectory = Find-VcRuntimeDirectory -RedistDirectory (Join-Path $vsRoot 'VC/Redist/MSVC')
}
if (-not $RuntimeDirectory) { throw 'No x64 Microsoft.VC*.CRT redistributable directory was found.' }
$payload = (Resolve-Path -LiteralPath $PayloadDirectory).Path
$names = @('concrt140.dll', 'msvcp140.dll', 'msvcp140_1.dll', 'msvcp140_2.dll',
    'msvcp140_atomic_wait.dll', 'msvcp140_codecvt_ids.dll', 'vcruntime140.dll',
    'vcruntime140_1.dll', 'vcruntime140_threads.dll')
$runtimeFiles = @(Get-ChildItem -LiteralPath $RuntimeDirectory -Filter '*.dll' -File |
    Where-Object { $names -icontains $_.Name })
foreach ($required in @('msvcp140.dll', 'msvcp140_1.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')) {
    if ($runtimeFiles.Name -inotcontains $required) { throw "VC runtime is missing $required" }
}
foreach ($file in $runtimeFiles) {
    Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $payload $file.Name) -Force
}
