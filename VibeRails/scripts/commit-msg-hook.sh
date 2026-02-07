#!/bin/sh
# Vibe Rails Commit-Msg Hook
# Validates COMMIT-level acknowledgments in commit message
# Installed by Vibe Rails - do not edit manually

# Find vb executable
if command -v vb >/dev/null 2>&1; then
    VB_CMD="vb"
elif [ -f "./vb" ]; then
    VB_CMD="./vb"
elif [ -f "./vb.exe" ]; then
    VB_CMD="./vb.exe"
else
    # Vibe Rails not found - allow commit
    exit 0
fi

# Run commit-msg validation
$VB_CMD --commit-msg "$1"
exit_code=$?

if [ $exit_code -ne 0 ]; then
    echo ""
    echo "Commit message missing required VCA acknowledgments."
    echo "Add acknowledgments for COMMIT-level violations."
    exit 1
fi

exit 0
# End Vibe Rails Hook
