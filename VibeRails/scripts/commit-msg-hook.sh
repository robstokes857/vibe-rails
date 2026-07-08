#!/bin/sh
# Vibe Rails Commit-Msg Hook
# Validates VCA commit-message requirements
# Installed by Vibe Rails - do not edit manually

echo "VibeRails VCA commit-message validation is temporarily disabled."
exit 0

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

# Run VCA commit-message validation. GUI Git clients often surface only the
# first captured line on failure, so buffer detailed output outside terminals
# and print a concise summary before it when the hook blocks.
if [ -t 1 ]; then
    $VB_CMD --vca-hook commit-msg --commit-message "$1" --prompt-acknowledgment
    exit_code=$?
else
    vca_output="$($VB_CMD --vca-hook commit-msg --commit-message "$1" --prompt-acknowledgment 2>&1)"
    exit_code=$?

    if [ $exit_code -ne 0 ]; then
        echo "VibeRails VCA blocked this commit. Show Command Output for required acknowledgments."
        echo ""
    fi

    if [ -n "$vca_output" ]; then
        printf '%s\n' "$vca_output"
    fi
fi

if [ $exit_code -ne 0 ]; then
    echo ""
    echo "VibeRails VCA commit validation failed."
    echo "Add required acknowledgments or fix blocking violations."
    exit 1
fi

exit 0
# End Vibe Rails Hook
