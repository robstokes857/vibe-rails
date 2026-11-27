#!/bin/sh
# Vibe Rails Post-Commit Hook
# VibeRails Hook Version: __VIBERAILS_HOOK_VERSION__
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

viberails_commit=$(git rev-parse HEAD 2>/dev/null || true)

# The commit already succeeded. Queueing its after-commit Automations and linking it to a card
# session are best-effort and must not interfere with Git, so vb runs in the background with no
# terminal streams and Git returns without waiting for it (VIBE-374). HEAD is read here, while it
# still names this commit: a rebase runs this hook once per commit. vb finds the repository from
# the working directory, which Git sets to the top of the work tree.
# Do not enqueue an ambiguous run if HEAD could not be resolved; still allow any chained hook below.
if [ -n "$viberails_commit" ]; then
    viberails_run --job-trigger post-commit --commit "$viberails_commit" </dev/null >/dev/null 2>&1 &
fi

if [ -n "$VIBERAILS_CHAINED_HOOK" ] && [ -f "$VIBERAILS_CHAINED_HOOK" ]; then
    "$VIBERAILS_CHAINED_HOOK" "$@"
fi

# End Vibe Rails Hook
