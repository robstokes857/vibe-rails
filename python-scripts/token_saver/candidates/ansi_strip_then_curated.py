"""Sizing for the Codex `ansi-strip` question: on ESC-bearing exec output the condenser fails open
whole-string (any ESC), so neither dedupe nor truncation ever runs there. This candidate strips
SGR colour + OSC title sequences (what ansi-strip does; cursor moves left alone => decline), then
applies the curated-stage mirror. Fires ONLY on outputs that contain ESC, so the report's totals
are exactly the incremental value of turning ansi-strip on for that provider.

    python experiment.py candidates/ansi_strip_then_curated.py --name <you> --provider openai --tool exec --on raw

Owner preference keeps ansi-strip OFF (runbook §5); this is evidence, not a default flip.
"""
import re, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import curated_stages_mirror as curated

META = {"description": "ansi-strip (SGR+OSC+BEL only) then curated stages, on ESC-bearing outputs only"}

_SGR = re.compile(r"\x1b\[[0-9;]*m")
_OSC = re.compile(r"\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)")
_BARE_CR = re.compile(r"\r(?!\n)")


def transform(text, tool, command, provider):
    if "\x1b" not in text:
        return text
    s = _OSC.sub("", text)
    s = _SGR.sub("", s)
    s = s.replace("\x07", "")
    if "\x1b" in s or _BARE_CR.search(s):
        return text  # cursor moves / bare CR: the real minifier keeps those verbatim; decline whole
    out = curated.transform(s, tool, command, provider)
    return out if len(out) < len(text) else text
