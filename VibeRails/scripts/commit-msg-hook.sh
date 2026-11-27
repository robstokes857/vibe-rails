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
    echo "Open VibeRails and use Repair Git Guard on the Rules page, or reinstall VibeRails." >&2
    return 127
}

viberails_console_arg=
viberails_prompt_arg=

# Git runs this hook from the top of the work tree and vb finds the repository from its working
# directory, so no --workdir is passed (see the pre-commit hook).
#
# Git GUI clients capture hook output into a panel the user may never look at. On Git for
# Windows, let the hook host open a dedicated popup console when the check blocks the commit;
# a passing check opens no window (VIBE-374). A terminal commit keeps its transcript inline; a
# redirected commit keeps writing to its stream. CI must never open or wait on a window.
case "${OSTYPE:-$(uname -s 2>/dev/null)}" in
    msys*|cygwin*|MINGW*|MSYS*|CYGWIN*)
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

# Message cleanup runs before the chained hook and before validation. Cleanup rewrites the message
# file, so a chained hook that validates, signs, or derives metadata from the message has to see the
# text git will actually record: with a chained hook, cleanup is a pass of its own before it. Without
# one, the validation below cleans the message before it reads it, which saves a VibeRails launch on
# every commit (VIBE-327). Cleanup never blocks a commit, so its exit code is ignored; its stderr is
# dropped because the invocation below reports any "vb not found" problem already.
viberails_cleaned_arg=
if [ -n "$VIBERAILS_CHAINED_HOOK" ] && [ -f "$VIBERAILS_CHAINED_HOOK" ]; then
    viberails_run --vca-hook clean-commit-msg --commit-message "$1" 2>/dev/null || true
    viberails_cleaned_arg=--co-authors-cleaned
    "$VIBERAILS_CHAINED_HOOK" "$@"
    viberails_exit_code=$?
    if [ "$viberails_exit_code" -ne 0 ]; then
        exit "$viberails_exit_code"
    fi
fi

if [ -n "$viberails_console_arg" ]; then
    viberails_run --vca-hook commit-msg --enqueue-automations $viberails_cleaned_arg --commit-message "$1" "$viberails_prompt_arg" "$viberails_console_arg"
elif [ -n "$viberails_prompt_arg" ]; then
    viberails_run --vca-hook commit-msg --enqueue-automations $viberails_cleaned_arg --commit-message "$1" "$viberails_prompt_arg"
else
    viberails_run --vca-hook commit-msg --enqueue-automations $viberails_cleaned_arg --commit-message "$1"
fi
viberails_exit_code=$?

if [ "$viberails_exit_code" -ne 0 ]; then
    echo ""
    echo "VibeRails VCA rejected this commit message. Add the requested acknowledgment or fix the violation." >&2
    exit "$viberails_exit_code"
fi

# End Vibe Rails Hook
