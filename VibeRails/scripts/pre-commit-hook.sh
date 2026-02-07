#!/bin/sh
# Vibe Rails Pre-Commit Hook
# Validates VCA rules before commits
# Installed by Vibe Rails - do not edit manually

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

# Run VCA validation
$VB_CMD --validate-vca --pre-commit
exit_code=$?

if [ $exit_code -ne 0 ]; then
    echo ""
    echo "VCA validation failed. Commit blocked."
    echo "Fix the issues above or use 'git commit --no-verify' to bypass."
    exit 1
fi

exit 0
# End Vibe Rails Hook
