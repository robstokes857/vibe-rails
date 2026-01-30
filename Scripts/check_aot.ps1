#!/usr/bin/env pwsh
# check_aot.ps1 (PS 7+)
# Checks for AOT/trimming compatibility issues with NuGet packages and code.
# Runs a publish with AOT analyzers enabled and reports any warnings.

[CmdletBinding()]
param(
    [string] $Project = "",
    [string] $Configuration = "Release",
    [string] $Framework = "net10.0",
    [string] $Rid = "win-x64"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Resolve-ProjectPath([string]$maybe) {
    if ($maybe) { return (Resolve-Path $maybe).Path }

    $scriptDir = Split-Path -Parent $PSCommandPath
    $searchRoot = if (Test-Path (Join-Path $scriptDir ".git")) { $scriptDir } else { Split-Path -Parent $scriptDir }

    $csproj = Get-ChildItem -Path $searchRoot -Recurse -Filter *.csproj -File |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj|Scripts|artifacts|Tests|MCP|ToonConvert)[\\/]' } |
        Sort-Object FullName |
        Select-Object -First 1

    if (-not $csproj) {
        throw "No .csproj found. Pass -Project ./path/to/YourApp.csproj"
    }
    return $csproj.FullName
}

# ---- main ----
$projectPath = Resolve-ProjectPath $Project
$projectName = [IO.Path]::GetFileNameWithoutExtension($projectPath)
$projectDir = Split-Path -Parent $projectPath

Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host " AOT Compatibility Check: $projectName" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

# Create temp output directory
$tempOut = Join-Path ([IO.Path]::GetTempPath()) "aot-check-$([guid]::NewGuid().ToString('N').Substring(0,8))"
New-Item -ItemType Directory -Force -Path $tempOut | Out-Null

try {
    # Run publish with AOT to get all warnings
    $publishArgs = @(
        "publish", $projectPath,
        "-c", $Configuration,
        "-f", $Framework,
        "-r", $Rid,
        "--self-contained", "true",
        "-o", $tempOut,
        "/p:PublishAot=true",
        "/p:TreatWarningsAsErrors=false",
        "-v", "normal"
    )

    Write-Host "Running: dotnet $($publishArgs -join ' ')`n" -ForegroundColor DarkGray

    # Capture output
    $output = & dotnet @publishArgs 2>&1 | Out-String

    # Parse for AOT/trimming warnings
    $warningPatterns = @(
        'IL2\d{3}',   # Trimming warnings (IL2xxx)
        'IL3\d{3}',   # AOT warnings (IL3xxx)
        'RequiresUnreferencedCode',
        'RequiresDynamicCode',
        'RequiresAssemblyFiles',
        'DynamicallyAccessedMembers',
        'trim analysis',
        'AOT analysis'
    )

    $pattern = $warningPatterns -join '|'
    $warnings = $output -split "`n" | Where-Object { $_ -match $pattern }

    Write-Host "`n========================================" -ForegroundColor Cyan
    Write-Host " Results" -ForegroundColor Cyan
    Write-Host "========================================`n" -ForegroundColor Cyan

    if ($warnings.Count -eq 0) {
        Write-Host "[PASS] No AOT/trimming warnings detected!" -ForegroundColor Green
        Write-Host "`nYour packages and code appear to be AOT-compatible.`n" -ForegroundColor Green
    } else {
        Write-Host "[WARN] Found $($warnings.Count) potential AOT/trimming issue(s):`n" -ForegroundColor Yellow

        $warnings | ForEach-Object {
            $line = $_.Trim()
            if ($line) {
                # Color-code by severity
                if ($line -match 'error') {
                    Write-Host "  $line" -ForegroundColor Red
                } elseif ($line -match 'warning') {
                    Write-Host "  $line" -ForegroundColor Yellow
                } else {
                    Write-Host "  $line" -ForegroundColor DarkYellow
                }
            }
        }

        Write-Host "`n----------------------------------------" -ForegroundColor DarkGray
        Write-Host "Common fixes:" -ForegroundColor Cyan
        Write-Host "  - IL2xxx: Add [DynamicallyAccessedMembers] or use source generators" -ForegroundColor Gray
        Write-Host "  - IL3xxx: Avoid reflection/dynamic code or suppress with [RequiresDynamicCode]" -ForegroundColor Gray
        Write-Host "  - For JSON: Use [JsonSerializable] source generation (AppJsonSerializerContext)" -ForegroundColor Gray
        Write-Host "  - Check if package has an AOT-compatible version`n" -ForegroundColor Gray
    }

    # Check if build actually succeeded
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[ERROR] Build failed with exit code $LASTEXITCODE" -ForegroundColor Red
        Write-Host "`nFull output:`n$output" -ForegroundColor DarkGray
        exit $LASTEXITCODE
    }

} finally {
    # Cleanup temp directory
    if (Test-Path $tempOut) {
        Remove-Item -Recurse -Force $tempOut -ErrorAction SilentlyContinue
    }
}

Write-Host "Done.`n" -ForegroundColor Green
