#!/bin/sh
# Vibe Rails Pre-Commit Hook
# VibeRails Hook Version: __VIBERAILS_HOOK_VERSION__
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
    echo "Open VibeRails and use Repair Git Guard on the Rules page, or reinstall VibeRails." >&2
    return 127
}

viberails_console_arg=
viberails_prompt_arg=

# Git runs this hook from the top of the work tree, and vb finds the repository from its working
# directory, so no --workdir is passed: resolving it here cost a git launch on every commit, and
# `pwd` would be an MSYS path that a Windows vb cannot use.
#
# Git GUI clients (VS Code SCM panel, SourceTree, etc.) capture hook output into a panel
# the user may never look at. On Git for Windows, let the hook host open a dedicated popup
# console (CREATE_NEW_CONSOLE) when the check blocks the commit, so the person sees why and
# can answer it there. A passing check opens no window (VIBE-374).
#
# A GUI launch has neither terminal input nor terminal output. A terminal commit keeps
# its transcript inline and needs no popup; a command such as `git commit >hook.log`
# still has terminal input and should keep writing to the redirected stream. CI must
# never open or wait on a window. Git for Windows' shell names the platform in OSTYPE,
# which saves starting uname there.
case "${OSTYPE:-$(uname -s 2>/dev/null)}" in
    msys*|cygwin*|MINGW*|MSYS*|CYGWIN*)
        if [ ! -t 0 ] && [ ! -t 1 ] &&
           [ -z "${CI:-}${TF_BUILD:-}${GITHUB_ACTIONS:-}${GITLAB_CI:-}${JENKINS_URL:-}${TEAMCITY_VERSION:-}${BUILDKITE:-}${APPVEYOR:-}" ]; then
            viberails_console_arg=--console-window
        fi
        ;;
esac

# A person who can answer is offered a signed exception when STOP blocks a deliberate change.
# Git gives hooks a null stdin, so in practice that person is at the Git Guard console above.
# Automated and redirected commits receive the normal blocking transcript without waiting.
if { [ -t 0 ] && [ -t 1 ]; } || [ -n "$viberails_console_arg" ]; then
    viberails_prompt_arg=--prompt-acknowledgment
fi

if [ -n "$viberails_console_arg" ]; then
    viberails_run --vca-hook pre-commit --enqueue-automations "$viberails_prompt_arg" "$viberails_console_arg"
elif [ -n "$viberails_prompt_arg" ]; then
    viberails_run --vca-hook pre-commit --enqueue-automations "$viberails_prompt_arg"
else
    viberails_run --vca-hook pre-commit --enqueue-automations
fi
viberails_exit_code=$?

if [ "$viberails_exit_code" -ne 0 ]; then
    echo ""
    echo "VibeRails VCA blocked this commit. Follow the guidance above: fix the issue, change the rule, or sign an exception for a deliberate change." >&2
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
