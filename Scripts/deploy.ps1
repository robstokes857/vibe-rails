#!/usr/bin/env pwsh
# Wrapper for backward compatibility.
# Primary release script now lives at deploy/deploy.ps1.

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$PrimaryScript = Join-Path $RepoRoot "deploy" "deploy.ps1"

if (-not (Test-Path $PrimaryScript)) {
    throw "Primary script not found: $PrimaryScript"
}

& $PrimaryScript @args
exit $LASTEXITCODE
