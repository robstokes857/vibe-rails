#!/usr/bin/env pwsh
# Convenience wrapper for the VibeRails code-sign helper (pwsh 7+, all platforms).
# The real helper is built into the vb binary: `vb --sign-script <name>.py`
# It prompts for your signing PIN interactively; the PIN never appears in arguments,
# history, or logs. Usage: ./sign-script.ps1 my_script.py
param([Parameter(Mandatory = $true)][string]$ScriptName)
vb --sign-script $ScriptName
exit $LASTEXITCODE
