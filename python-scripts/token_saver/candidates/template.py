"""Candidate template — COPY me to a new file, don't edit me.

A candidate is the "I think if we did X we could save tokens" hypothesis, written
down as one function. The experiment harness runs it against every real tool output
in the corpus and reports savings, loss, and invariant violations
(see runbooks/token_saver/mining_runbook.md §5-6).

Rules of the game:
  * Return the input UNCHANGED to decline (that is your fail-open). Never raise.
  * Deterministic, idempotent (f(f(x)) == f(x)), never longer than the input.
  * Decline anything containing ESC/BEL/CR unless taming those is your whole point —
    the shipped shape/condense stages fail open on them, and so should you.
  * Lossy is allowed, but leave an explicit marker (like `[xN]` or
    `[... N elided ...]`) so the model knows something was removed.
  * One file per agent/idea. Never edit another agent's candidate — copy it.

Run me (I am a no-op, so expect fired=0):
    python experiment.py candidates/template.py --name <your-name>
"""

META = {"description": "no-op template; copy me"}

CTL = ("\x1b", "\x07", "\r")


def applies(tool, command, provider):
    """Optional pre-filter. Delete this function to consider every output."""
    return True


def transform(text, tool, command, provider):
    if any(c in text for c in CTL):
        return text  # fail open, like the shipped stages
    return text
