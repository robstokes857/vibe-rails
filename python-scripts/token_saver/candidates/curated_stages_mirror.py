"""Python APPROXIMATION of the curated default stages, for allowlist-expansion sizing.

Question it answers: "if tool X joined the allowlist, what would the pipeline save on
its outputs?" Run it filtered to a non-allowlisted tool, --on raw:

    python experiment.py candidates/curated_stages_mirror.py --name <you> \
        --provider openai --tool exec --on raw

Mirrors (in shipped order): crlf-normalize, trailing-whitespace, blank-edges,
blank-runs, dedupe-lines (>=3 -> ` [xN]`), truncate-long (150/50, middle >=10 lines
and >=4096 chars). Shape stages are skipped (a non-shell command classifies None
anyway). Fail-open mirror is CONSERVATIVE: declines outright on ESC/BEL and on bare
CR (the real minifier handles well-formed ANSI; we undercount, never overcount).

APPROXIMATION, not the pipeline — port + real-pipeline validation before believing
any number to the last digit (runbook §7). C# is truth.
"""

import re

META = {"description": "approximate curated ON-stages (minify+condense, no shape) for allowlist sizing"}

_BARE_CR = re.compile(r"\r(?!\n)")


def transform(text, tool, command, provider):
    if "\x1b" in text or "\x07" in text or _BARE_CR.search(text):
        return text  # conservative fail-open
    s = text.replace("\r\n", "\n")                 # crlf-normalize
    s = re.sub(r"[ \t]+\n", "\n", s)               # trailing-whitespace
    s = re.sub(r"[ \t]+$", "", s)
    lines = s.split("\n")
    while lines and lines[0].strip() == "":        # blank-edges
        lines.pop(0)
    while lines and lines[-1].strip() == "":
        lines.pop()
    out, i = [], 0
    while i < len(lines):                          # blank-runs + dedupe-lines
        line = lines[i]
        j = i + 1
        while j < len(lines) and lines[j] == line:
            j += 1
        run = j - i
        if line.strip() == "":
            out.append("")                         # any blank run -> one blank line
        elif run >= 3:
            out.append("%s [x%d]" % (line, run))
        else:
            out.extend(lines[i:j])
        i = j
    if len(out) > 200:                             # truncate-long: head 150 + tail 50
        middle = out[150:-50]
        middle_chars = sum(len(l) + 1 for l in middle)
        if len(middle) >= 10 and middle_chars >= 4096:
            out = out[:150] + ["[... %d lines elided ...]" % len(middle)] + out[-50:]
    result = "\n".join(out)
    return result if len(result) < len(text) else text  # never-grows
