#!/bin/sh
# Vibe Rails Post-Commit Hook
# VibeRails Hook Version: 5
# Queues successful-commit Jobs after Git has created the commit.
# Installed by VibeRails - use the dashboard to repair or remove this section.

VIBERAILS_EXECUTABLE='__VIBERAILS_EXECUTABLE__'
VIBERAILS_EXECUTABLE_ARGUMENT='__VIBERAILS_EXECUTABLE_ARGUMENT__'
VIBERAILS_CHAINED_HOOK='__VIBERAILS_CHAINED_HOOK__'

viberails_run() {
    if [ -n "$VIBERAILS_EXECUTABLE" ] && [ -f "$VIBERAILS_EXECUTABLE" ] &&
       { [ -z "$VIBERAILS_EXECUTABLE_ARGUMENT" ] || [ -f "$VIBERAILS_EXECUTABLE_ARGUMENT" ]; }; then
        if [ -n "$VIBERAILS_EXECUTABLE_ARGUMENT" ]; then
            "$VIBERAILS_EXECUTABLE" "$VIBERAILS_EXECUTABLE_ARGUMENT" "$@"
        else
            "$VIBERAILS_EXECUTABLE" "$@"
        fi
        return $?
    fi

    if command -v vb >/dev/null 2>&1; then
        vb "$@"
        return $?
    fi
    return 127
}

viberails_repo_root=$(git rev-parse --show-toplevel 2>/dev/null || pwd)
viberails_commit=$(git rev-parse HEAD 2>/dev/null || true)

# The commit already succeeded. Queueing is best-effort and must not interfere with Git.
viberails_run --job-trigger post-commit --workdir "$viberails_repo_root" --commit "$viberails_commit" >/dev/null 2>&1 || true

if [ -n "$VIBERAILS_CHAINED_HOOK" ] && [ -f "$VIBERAILS_CHAINED_HOOK" ]; then
    "$VIBERAILS_CHAINED_HOOK" "$@"
fi

# End Vibe Rails Hook
