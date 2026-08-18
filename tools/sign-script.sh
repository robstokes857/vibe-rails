#!/usr/bin/env bash
# Convenience wrapper for the VibeRails code-sign helper (bash/zsh, macOS + Linux).
# The real helper is built into the vb binary: `vb --sign-script <name>.py`
# It prompts for your signing PIN interactively; the PIN never appears in arguments,
# history, or logs. Usage: ./sign-script.sh my_script.py
set -euo pipefail
if [ $# -ne 1 ]; then
  echo "Usage: $(basename "$0") <script-name>.py" >&2
  exit 1
fi
exec vb --sign-script "$1"
