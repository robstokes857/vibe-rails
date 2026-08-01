#!/bin/sh
# Vibe Rails Commit-Msg Hook
# VibeRails Hook Version: __VIBERAILS_HOOK_VERSION__
# Applies commit-message cleanup policies, then enforces VCA acknowledgment requirements.
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
viberails_prompt_arg=

# Git GUI clients capture hook output into a panel the user may never look at. On Git for
# Windows, ask the hook host to re-spawn itself in a dedicated popup console so the VCA
# transcript stays visible. A terminal commit keeps its transcript inline; a redirected
# commit keeps writing to its stream. CI must never open or wait on a window.
case "$(uname -s 2>/dev/null)" in
    MINGW*|MSYS*|CYGWIN*)
        if [ ! -t 0 ] && [ ! -t 1 ] &&
           [ -z "${CI:-}${TF_BUILD:-}${GITHUB_ACTIONS:-}${GITLAB_CI:-}${JENKINS_URL:-}${TEAMCITY_VERSION:-}${BUILDKITE:-}${APPVEYOR:-}" ]; then
            viberails_console_arg=--console-window
        fi
        ;;
esac

# Prompt only when a person has somewhere to see and answer it. Automated and
# redirected commits receive the normal blocking transcript without waiting.
if { [ -t 0 ] && [ -t 1 ]; } || [ -n "$viberails_console_arg" ]; then
    viberails_prompt_arg=--prompt-acknowledgment
fi

# Message cleanup runs first, before the chained hook and before validation. Cleanup rewrites the
# message file, so a chained hook that validates, signs, or derives metadata from the message has to
# see the text git will actually record. Cleanup never blocks a commit, so its exit code is ignored;
# its stderr is dropped because the invocation below reports any "vb not found" problem already.
viberails_run --vca-hook clean-commit-msg --commit-message "$1" --workdir "$viberails_repo_root" 2>/dev/null || true

if [ -n "$VIBERAILS_CHAINED_HOOK" ] && [ -f "$VIBERAILS_CHAINED_HOOK" ]; then
    "$VIBERAILS_CHAINED_HOOK" "$@"
    viberails_exit_code=$?
    if [ "$viberails_exit_code" -ne 0 ]; then
        exit "$viberails_exit_code"
    fi
fi

if [ -n "$viberails_console_arg" ]; then
    viberails_run --vca-hook commit-msg --co-authors-cleaned --commit-message "$1" --workdir "$viberails_repo_root" "$viberails_prompt_arg" "$viberails_console_arg"
elif [ -n "$viberails_prompt_arg" ]; then
    viberails_run --vca-hook commit-msg --co-authors-cleaned --commit-message "$1" --workdir "$viberails_repo_root" "$viberails_prompt_arg"
else
    viberails_run --vca-hook commit-msg --co-authors-cleaned --commit-message "$1" --workdir "$viberails_repo_root"
fi
viberails_exit_code=$?

if [ "$viberails_exit_code" -ne 0 ]; then
    echo ""
    echo "VibeRails VCA rejected this commit message. Add the requested acknowledgment or fix the violation." >&2
    exit "$viberails_exit_code"
fi

# End Vibe Rails Hook
