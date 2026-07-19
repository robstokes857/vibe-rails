#!/bin/sh
# Vibe Rails Pre-Commit Hook
# VibeRails Hook Version: 5
# Validates staged changes before Git creates a commit.
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

    echo "VibeRails VCA could not run because the vb executable was not found." >&2
    echo "Repair the hooks from VibeRails, or use git commit --no-verify for an intentional bypass." >&2
    return 127
}

viberails_repo_root=$(git rev-parse --show-toplevel 2>/dev/null || pwd)
viberails_console_arg=

# Git GUI clients (VS Code SCM panel, SourceTree, etc.) capture hook output into a panel
# the user may never look at. On Git for Windows, ask the hook host to re-spawn itself in
# a dedicated popup console (CREATE_NEW_CONSOLE) so the VCA transcript stays visible.
#
# A GUI launch has neither terminal input nor terminal output. A terminal commit keeps
# its transcript inline and needs no popup; a command such as `git commit >hook.log`
# still has terminal input and should keep writing to the redirected stream. CI must
# never open or wait on a window.
case "$(uname -s 2>/dev/null)" in
    MINGW*|MSYS*|CYGWIN*)
        if [ ! -t 0 ] && [ ! -t 1 ] &&
           [ -z "${CI:-}${TF_BUILD:-}${GITHUB_ACTIONS:-}${GITLAB_CI:-}${JENKINS_URL:-}${TEAMCITY_VERSION:-}${BUILDKITE:-}${APPVEYOR:-}" ]; then
            viberails_console_arg=--console-window
        fi
        ;;
esac

if [ -n "$viberails_console_arg" ]; then
    viberails_run --vca-hook pre-commit --workdir "$viberails_repo_root" "$viberails_console_arg"
else
    viberails_run --vca-hook pre-commit --workdir "$viberails_repo_root"
fi
viberails_exit_code=$?

if [ "$viberails_exit_code" -ne 0 ]; then
    echo ""
    echo "VibeRails VCA blocked this commit. Fix the reported issue or use --no-verify to bypass intentionally." >&2
    exit "$viberails_exit_code"
fi

if [ -n "$VIBERAILS_CHAINED_HOOK" ] && [ -f "$VIBERAILS_CHAINED_HOOK" ]; then
    "$VIBERAILS_CHAINED_HOOK" "$@"
    viberails_exit_code=$?
    if [ "$viberails_exit_code" -ne 0 ]; then
        exit "$viberails_exit_code"
    fi
fi

# End Vibe Rails Hook
