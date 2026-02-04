#!/usr/bin/env pwsh
# build.ps1 (PS 7+)
# - Windows AOT built locally
# - Linux AOT built via Docker (Linux AOT SDK image)
# - SHA256 checksums.md written to deploy/artifacts/aot

[CmdletBinding()]
param(
    [string] $Project = "",
    [string] $Configuration = "Release",
    [string] $Framework = "net10.0",
    [string] $OutputRoot = "deploy/artifacts/aot",

    # Windows build (local)
    [string] $WinRid = "win-x64",

    # Linux build (docker)
    [string] $LinuxRid = "linux-x64",
    [switch] $BuildLinuxViaDocker = $true,
    [string] $LinuxDockerImage = "mcr.microsoft.com/dotnet/sdk:10.0-aot"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Require-Cmd([string]$name) {
    if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
        throw "Required command not found on PATH: $name"
    }
}

function Resolve-ProjectPath([string]$maybe) {
    if ($maybe) { return (Resolve-Path $maybe).Path }

    # If script is in a subdirectory, search from parent (repo root)
    $scriptDir = Split-Path -Parent $PSCommandPath
    $searchRoot = if (Test-Path (Join-Path $scriptDir ".git")) { $scriptDir } else { Split-Path -Parent $scriptDir }

    $csproj = Get-ChildItem -Path $searchRoot -Recurse -Filter *.csproj -File |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj|Scripts|artifacts|Tests|MCP|ToonConvert)[\\/]' } |
        Sort-Object FullName |
        Select-Object -First 1

    if (-not $csproj) {
        throw "No .csproj found. Pass -Project ./path/to/YourApp.csproj or set -Project to VibeRails/VibeRails.csproj"
    }
    return $csproj.FullName
}

function Publish-AotLocal([string]$projectPath, [string]$rid, [string]$outDir) {
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null

    $args = @(
        "publish", $projectPath,
        "-c", $Configuration,
        "-f", $Framework,
        "-r", $rid,
        "--self-contained", "true",
        "-o", $outDir,
        "/p:PublishAot=true",
        "/p:StripSymbols=true",
        "/p:InvariantGlobalization=true"
    )

    Write-Host "`n==> dotnet $($args -join ' ')" -ForegroundColor Cyan
    & dotnet @args
}

function Publish-AotLinuxViaDocker([string]$projectPath, [string]$rid, [string]$outDir, [string]$image, [string]$repoRoot) {
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null

    $projectRel = [IO.Path]::GetRelativePath($repoRoot, $projectPath).Replace("\","/")

    # Docker wants linux paths inside container; mount repo and output
    $containerRepo = "/src"
    $containerOut  = "/out"

    $dotnetArgs = @(
        "publish", "$containerRepo/$projectRel",
        "-c", $Configuration,
        "-f", $Framework,
        "-r", $rid,
        "--self-contained", "true",
        "-o", $containerOut,
        "/p:PublishAot=true",
        "/p:StripSymbols=true",
        "/p:InvariantGlobalization=true"
    )

    $dockerArgs = @(
        "run","--rm",
        "-v","${repoRoot}:${containerRepo}",
        "-v","$((Resolve-Path $outDir).Path):${containerOut}",
        "-w",$containerRepo,
        $image,
        "dotnet"
    ) + $dotnetArgs

    Write-Host "`n==> docker $($dockerArgs -join ' ')" -ForegroundColor Cyan
    & docker @dockerArgs

    # Fix any files with embedded carriage returns in their names (Windows line ending issue)
    $outDirResolved = (Resolve-Path $outDir).Path
    Get-ChildItem -Path $outDirResolved -File | ForEach-Object {
        $currentName = $_.Name
        if ($currentName -match "`r") {
            $cleanName = $currentName -replace "`r", ""
            $oldPath = $_.FullName
            $newPath = Join-Path $outDirResolved $cleanName
            Write-Host "Fixing filename: '$currentName' -> '$cleanName'" -ForegroundColor Yellow
            Move-Item -Path $oldPath -Destination $newPath -Force
        }
    }
}

function Find-MainBinary([string]$publishDir, [string]$rid) {
    $files = Get-ChildItem -Path $publishDir -File
    if ($rid -like "win-*") {
        $exe = $files | Where-Object Extension -ieq ".exe" | Sort-Object Length -Descending | Select-Object -First 1
        if (-not $exe) { throw "No .exe found in $publishDir" }
        return $exe.FullName
    }

    $noExt = $files | Where-Object { $_.Extension -eq "" } | Sort-Object Length -Descending | Select-Object -First 1
    if ($noExt) { return $noExt.FullName }

    $largest = $files | Sort-Object Length -Descending | Select-Object -First 1
    if (-not $largest) { throw "No files found in $publishDir" }
    return $largest.FullName
}

# ---- main ----
Require-Cmd dotnet
$projectPath = Resolve-ProjectPath $Project
$projectName = [IO.Path]::GetFileNameWithoutExtension($projectPath)

# Determine repo root (parent of script dir if script is in subdirectory)
$scriptDir = Split-Path -Parent $PSCommandPath
$repoRoot = if (Test-Path (Join-Path $scriptDir ".git")) { $scriptDir } else { Split-Path -Parent $scriptDir }

$outRoot = Join-Path $repoRoot $OutputRoot
Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $outRoot
New-Item -ItemType Directory -Force -Path $outRoot | Out-Null

$results = @()

# Windows local build
$winOut = Join-Path $outRoot $WinRid
Publish-AotLocal -projectPath $projectPath -rid $WinRid -outDir $winOut
$winBin = Find-MainBinary -publishDir $winOut -rid $WinRid
$results += [pscustomobject]@{
    RID    = $WinRid
    File   = [IO.Path]::GetFileName($winBin)
    Bytes  = (Get-Item $winBin).Length
    Sha256 = (Get-FileHash -Algorithm SHA256 -Path $winBin).Hash.ToLowerInvariant()
    Path   = $winBin
}

# Linux build
$linuxOut = Join-Path $outRoot $LinuxRid
if ($BuildLinuxViaDocker) {
    Require-Cmd docker
    # Ensure output dir exists and we can mount it
    New-Item -ItemType Directory -Force -Path $linuxOut | Out-Null
    Publish-AotLinuxViaDocker -projectPath $projectPath -rid $LinuxRid -outDir $linuxOut -image $LinuxDockerImage -repoRoot $repoRoot
} else {
    Publish-AotLocal -projectPath $projectPath -rid $LinuxRid -outDir $linuxOut
}

$linuxBin = Find-MainBinary -publishDir $linuxOut -rid $LinuxRid
$results += [pscustomobject]@{
    RID    = $LinuxRid
    File   = [IO.Path]::GetFileName($linuxBin)
    Bytes  = (Get-Item $linuxBin).Length
    Sha256 = (Get-FileHash -Algorithm SHA256 -Path $linuxBin).Hash.ToLowerInvariant()
    Path   = $linuxBin
}

# Write checksums.md
$mdPath = Join-Path $outRoot "checksums.md"
$now = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")

$md = @()
$md += "# Checksums"
$md += ""
$md += "- Project: **$projectName**"
$md += "- Built: **$now**"
$md += ""
$md += "| RID | File | Size (bytes) | SHA-256 |"
$md += "|---|---|---:|---|"
foreach ($r in $results) {
    $md += "| $($r.RID) | $($r.File) | $($r.Bytes) | `$($r.Sha256)` |"
}
$md += ""
$md += "## Output paths"
foreach ($r in $results) {
  $md += ("- **{0}**: ``{1}``" -f $r.RID, $r.Path)
}

$md | Set-Content -Path $mdPath -Encoding utf8

Write-Host "Wrote $mdPath" -ForegroundColor Green
Write-Host "Done." -ForegroundColor Green
