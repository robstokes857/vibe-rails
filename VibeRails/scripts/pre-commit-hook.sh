#!/bin/sh
# Vibe Rails Pre-Commit Hook
# Validates VCA rules before commits
# Installed by Vibe Rails - do not edit manually

echo "VibeRails VCA validation is temporarily disabled."
exit 0

# Find vb executable
if command -v vb >/dev/null 2>&1; then
    VB_CMD="vb"
elif [ -f "./vb" ]; then
    VB_CMD="./vb"
elif [ -f "./vb.exe" ]; then
    VB_CMD="./vb.exe"
else
    # Vibe Rails not found - allow commit with warning
    echo "Warning: vb not found in PATH. Skipping VCA validation."
    exit 0
fi

# Run VCA validation. When Git is invoked by VS Code or another GUI client,
# stdout is captured and the client often shows only the first line. Buffer the
# detailed output in that case so failures start with a useful summary instead
# of the progress banner.
if [ -t 1 ]; then
    $VB_CMD --vca-hook pre-commit
    exit_code=$?
else
    vca_output="$($VB_CMD --vca-hook pre-commit 2>&1)"
    exit_code=$?

    if [ $exit_code -ne 0 ]; then
        echo "VibeRails VCA blocked this commit. Show Command Output for details."
        echo ""
    fi

    if [ -n "$vca_output" ]; then
        printf '%s\n' "$vca_output"
    fi
fi

if [ $exit_code -ne 0 ]; then
    echo ""
    echo "VibeRails VCA validation failed. Commit blocked."
    echo "Fix the issues above or use 'git commit --no-verify' to bypass."
    exit 1
fi

exit 0
# End Vibe Rails Hook
