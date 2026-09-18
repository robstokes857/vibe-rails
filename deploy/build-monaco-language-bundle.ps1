#!/usr/bin/env pwsh
# build-monaco-language-bundle.ps1 - Concatenate Monaco's per-language AMD modules
# into a single basic-languages/all.js.
#
# Why this exists:
#   vsce warns on every package when a VSIX holds more than 100 JavaScript files
#   (@vscode/vsce/out/package.js: `files.length > 5000 || jsFiles.length > 100`).
#   The bundled backend's wwwroot ships Monaco, and basic-languages/ alone is 81
#   separate .js files - the single biggest block of the count.
#
#   Every one of those files is a *named* AMD module, e.g.
#       define("vs/basic-languages/csharp/csharp", ["require","require"], ...)
#   so concatenating them is safe: loading the combined file registers all 81
#   definitions in the AMD loader's registry, and Monaco's lazy per-language
#   require then resolves from memory rather than fetching a file the VSIX no
#   longer carries.
#
#   Load order is not optional. Monaco's AMD loader runs a module factory as soon
#   as its declared dependencies resolve, and 11 of the language modules (html,
#   javascript, python, razor, typescript, xml, yaml, ...) synchronously require
#   "vs/editor/editor.api" - which only exists once editor.main has run. So
#   js/modules/monaco-loader.js requires editor.main first and this bundle second;
#   requiring both at once fails with "Synchronous require cannot resolve module
#   'vs/editor/editor.api'".
#
# When to run it:
#   After refreshing the vendored Monaco copy under VibeRails/wwwroot/assets/monaco/.
#   deploy/prepare-binaries.ps1 stages all.js in place of the 81 per-language files
#   when it builds bin/<target>/wwwroot, and js/modules/monaco-loader.js requires
#   'vs/basic-languages/all' before editor.main, so the file must be committed for
#   source runs and packaged runs alike.
#
# Usage:
#   pwsh deploy/build-monaco-language-bundle.ps1          # regenerate all.js
#   pwsh deploy/build-monaco-language-bundle.ps1 -Check    # fail if it is stale

param(
    [switch]$Check
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$ScriptDir = Split-Path -Parent $PSCommandPath
$RepoRoot = Split-Path -Parent $ScriptDir
$LanguageDir = Join-Path $RepoRoot "VibeRails" "wwwroot" "assets" "monaco" "vs" "basic-languages"
$BundleName = "all.js"
$BundlePath = Join-Path $LanguageDir $BundleName

if (-not (Test-Path $LanguageDir)) {
    throw "Monaco basic-languages directory not found: $LanguageDir"
}

# Sort on the POSIX-style relative path so the bundle is byte-identical no matter
# which platform generated it - otherwise -Check reports false drift in CI.
$sources = Get-ChildItem -Path $LanguageDir -Recurse -File -Filter *.js |
    Where-Object { $_.Name -ne $BundleName } |
    ForEach-Object {
        [pscustomobject]@{
            File = $_
            RelativePath = $_.FullName.Substring($LanguageDir.Length).TrimStart('\', '/').Replace('\', '/')
        }
    } |
    Sort-Object -Property RelativePath -CaseSensitive

if ($sources.Count -eq 0) {
    throw "No per-language Monaco modules found under $LanguageDir"
}

$builder = [System.Text.StringBuilder]::new()
[void]$builder.AppendLine("/* GENERATED FILE - do not edit by hand.")
[void]$builder.AppendLine(" *")
[void]$builder.AppendLine(" * Monaco's $($sources.Count) basic-languages AMD modules concatenated by")
[void]$builder.AppendLine(" * deploy/build-monaco-language-bundle.ps1. Re-run that script after")
[void]$builder.AppendLine(" * refreshing the vendored Monaco copy.")
[void]$builder.AppendLine(" */")

foreach ($source in $sources) {
    $text = [System.IO.File]::ReadAllText($source.File.FullName)
    [void]$builder.AppendLine()
    [void]$builder.AppendLine("/* --- $($source.RelativePath) --- */")
    [void]$builder.AppendLine($text.TrimEnd("`r", "`n"))
}

# The AMD loader still needs a definition for the module the caller asked for.
# Without this, require(['vs/basic-languages/all']) fetches and runs the file but
# then fails with "Can not resolve module" because nothing defined that id.
[void]$builder.AppendLine()
[void]$builder.AppendLine('define("vs/basic-languages/all", [], function () { return {}; });')

# Normalise to LF: the sources check out CRLF under .gitattributes' `* text=auto`,
# so reading them verbatim would otherwise make the bundle's endings depend on the
# checkout that produced it.
$content = $builder.ToString().Replace("`r`n", "`n")

if ($Check) {
    if (-not (Test-Path $BundlePath)) {
        Write-Host "Monaco language bundle is missing: $BundlePath" -ForegroundColor Red
        Write-Host "Run: pwsh deploy/build-monaco-language-bundle.ps1" -ForegroundColor Yellow
        exit 1
    }

    $existing = [System.IO.File]::ReadAllText($BundlePath).Replace("`r`n", "`n")
    if ($existing -ne $content) {
        Write-Host "Monaco language bundle is stale: $BundlePath" -ForegroundColor Red
        Write-Host "Run: pwsh deploy/build-monaco-language-bundle.ps1" -ForegroundColor Yellow
        exit 1
    }

    Write-Host "Monaco language bundle is up to date ($($sources.Count) modules)." -ForegroundColor Green
    exit 0
}

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText($BundlePath, $content, $utf8NoBom)

$sizeKB = [math]::Round((Get-Item $BundlePath).Length / 1KB, 1)
Write-Host "Wrote $BundleName from $($sources.Count) modules ($sizeKB KB)" -ForegroundColor Green
