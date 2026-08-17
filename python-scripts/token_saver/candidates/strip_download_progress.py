"""Seed candidate: collapse package-manager download/progress spam to its final line.

Hypothesis: pip/npm/git/apt progress output ("Downloading ... 45%", "Receiving
objects: 87% ...") is high-volume, zero-information-after-the-fact text. Keep only
the LAST line of each consecutive progress run (the terminal state) and mark the
elision. Deliberately Lossy: the experiment report's loss accounting + worst-case
samples are the point — judge them before believing the savings number.
"""

import re

META = {"description": "collapse consecutive download/progress lines to the last one + [... N progress lines ...]"}

CTL = ("\x1b", "\x07", "\r")

PROGRESS = re.compile(
    r"^\s*("
    r"(Downloading|Fetching|Pulling|Pushing|Extracting|Unpacking|Installing collected|Collecting)\b.*"
    r"|Receiving objects:\s*\d+%.*"
    r"|Resolving deltas:\s*\d+%.*"
    r"|Compressing objects:\s*\d+%.*"
    r"|Counting objects:\s*\d+%.*"
    r"|(reading|Reading) (database|package lists)\b.*"
    r"|Progress.*\d+\s*%.*"
    r"|\s*[\d.]+\s*[kMG]i?B\s*/\s*[\d.]+\s*[kMG]i?B.*"
    r")$")


def transform(text, tool, command, provider):
    if any(c in text for c in CTL):
        return text
    lines = text.split("\n")
    out, i, changed = [], 0, False
    while i < len(lines):
        if PROGRESS.match(lines[i]):
            j = i
            while j < len(lines) and PROGRESS.match(lines[j]):
                j += 1
            run = j - i
            if run >= 3:  # keep short runs verbatim; only spam pays
                out.append("[... %d progress lines ...]" % (run - 1))
                out.append(lines[j - 1])
                changed = True
            else:
                out.extend(lines[i:j])
            i = j
        else:
            out.append(lines[i])
            i += 1
    if not changed:
        return text
    result = "\n".join(out)
    return result if len(result) < len(text) else text  # never-grows
