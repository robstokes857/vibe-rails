"""What would the LOSSLESS stages alone (crlf-normalize, trailing-whitespace, blank-edges,
blank-runs) save on Read-tool output — no dedupe, no truncation, nothing that could shift a line
the model will quote back in an Edit old_string. Sizing only: scope-read is OFF by owner
decision (runbook §5) and this candidate does not argue for flipping it; it puts a number on the
minify-only variant plan_1A parked without one.

    python experiment.py candidates/read_minify_only.py --name <you> --provider anthropic --tool Read --on raw
"""
import re

META = {"description": "lossless minify only (no condense) on Read output — sizing for the parked scope-read question"}

_BARE_CR = re.compile(r"\r(?!\n)")


def applies(tool, command, provider):
    return tool in ("Read", "read", "read_file")


def transform(text, tool, command, provider):
    if "\x1b" in text or "\x07" in text or _BARE_CR.search(text):
        return text
    s = text.replace("\r\n", "\n")
    s = re.sub(r"[ \t]+\n", "\n", s)
    s = re.sub(r"[ \t]+$", "", s)
    lines = s.split("\n")
    while lines and lines[0].strip() == "":
        lines.pop(0)
    while lines and lines[-1].strip() == "":
        lines.pop()
    out, blank = [], 0
    for line in lines:
        if line.strip() == "":
            blank += 1
            if blank <= 2:
                out.append(line)
        else:
            blank = 0
            out.append(line)
    result = "\n".join(out)
    return result if len(result) < len(text) else text
