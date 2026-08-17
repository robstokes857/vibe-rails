"""Seed candidate: lower the dedupe-lines threshold from >=3 to >=2.

Hypothesis (runbook §4 Q7, threshold tuning): the shipped `dedupe-lines` stage only
collapses runs of >= 3 identical lines. Runs of exactly 2 are untouched — how much
wire is riding on them? This mirrors the shipped marker format (` [xN]`) so the
model sees a familiar shape.

Run on top of the shipped pipeline output (the default --on after) to measure the
INCREMENTAL win of changing the threshold.
"""

META = {"description": "collapse runs of >=2 identical non-blank lines to `line [xN]` (shipped stage needs >=3)"}

CTL = ("\x1b", "\x07", "\r")


def transform(text, tool, command, provider):
    if any(c in text for c in CTL):
        return text
    lines = text.split("\n")
    out, i, changed = [], 0, False
    while i < len(lines):
        line = lines[i]
        j = i + 1
        while j < len(lines) and lines[j] == line:
            j += 1
        run = j - i
        # skip blank lines (blank-runs owns those) and lines already carrying a marker
        if run >= 2 and line.strip() and not line.rstrip().endswith("]"):
            out.append("%s [x%d]" % (line, run))
            changed = True
        else:
            out.extend(lines[i:j])
        i = j
    if not changed:
        return text
    result = "\n".join(out)
    return result if len(result) < len(text) else text  # never-grows
