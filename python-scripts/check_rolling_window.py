"""Check what working-signals are present in a 15s rolling plain-text window
just before each false-positive moment in session 881cd29d."""
import os
import re
import sqlite3
from datetime import datetime, timedelta

DB = os.path.expanduser("~/.vibe_rails/state.db")
SESSION = "881cd29d-4689-424b-952d-9d7355d5ce30"

ANSI_CSI = re.compile(r"\x1b\[[\x30-\x3f]*[\x20-\x2f]*[\x40-\x7e]")
ANSI_OSC = re.compile(r"\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)")
ANSI_OTHER = re.compile(r"\x1b[\(\)\*\+\-\./].")
ANSI_SHORT = re.compile(r"\x1b[78=>DEMc\\]")
CTRL_NP = re.compile(r"[\x00-\x08\x0b-\x0c\x0e-\x1a\x1c-\x1f\x7f]")

TIMER_RX = re.compile(r"\d+\s*[smh]\b", re.IGNORECASE)
TIMER_MS_RX = re.compile(r"\d+\s*m\s+\d+\s*s", re.IGNORECASE)


def to_plain(b):
    s = b.decode("utf-8", errors="replace")
    for rx in (ANSI_CSI, ANSI_OSC, ANSI_OTHER, ANSI_SHORT):
        s = rx.sub("", s)
    return CTRL_NP.sub("", s)


def parse_ts(s):
    s = s.replace("Z", "+00:00")
    if "." in s:
        head, frac_and_tz = s.split(".", 1)
        idx_p = frac_and_tz.find("+")
        idx_m = frac_and_tz.find("-", 1)
        idx = max(idx_p, idx_m) if idx_p >= 0 or idx_m >= 0 else -1
        if idx > 0:
            frac, tz = frac_and_tz[:idx], frac_and_tz[idx:]
        else:
            frac, tz = frac_and_tz, ""
        s = f"{head}.{frac[:6]}{tz}"
    return datetime.fromisoformat(s)


db = sqlite3.connect(DB)
for target_id in [1578226, 1584551, 1593297, 1661293]:
    target = db.execute("SELECT Timestamp FROM SessionLogs WHERE Id=?", (target_id,)).fetchone()
    target_ts = parse_ts(target[0])
    cutoff = target_ts - timedelta(seconds=15)
    rows = db.execute(
        "SELECT Content FROM SessionLogs WHERE SessionId=? AND Timestamp >= ? AND Timestamp <= ? ORDER BY Id",
        (SESSION, cutoff.isoformat(), target[0]),
    ).fetchall()
    plain = "".join(to_plain(c) for (c,) in rows)
    print(f"Chunk {target_id} @ {target[0]} — rolling 15s window has {len(plain)} chars of plain text, {len(rows)} chunks")
    print(f"  has 'orking': {'orking' in plain.lower()}")
    print(f"  has 'interrupt': {'interrupt' in plain.lower()}")
    print(f"  has 'interupt': {'interupt' in plain.lower()}")
    print(f"  has 'esc to': {'esc to' in plain.lower()}")
    timer = TIMER_RX.search(plain)
    print(f"  has timer match: {timer.group() if timer else None!r}")
    timer_ms = TIMER_MS_RX.search(plain)
    print(f"  has m-s timer:   {timer_ms.group() if timer_ms else None!r}")
    print(f"  has '•': {chr(0x2022) in plain}")
    print(f"  has '◦': {chr(0x25E6) in plain}")
    # Show only printable chars (no whitespace) to give a flavor of what is in the window
    sample = ''.join(c for c in plain if c.isprintable() and not c.isspace())[:300]
    print(f"  printable sample: {sample!r}")
    print()
