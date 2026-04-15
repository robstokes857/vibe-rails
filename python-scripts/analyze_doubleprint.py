"""
Analyze a session's chunk stream for repeat/redundant full-screen redraws.

Heuristics:
- Full-redraw marker: contains ESC[2J (erase screen) OR ESC[H (cursor home) followed by >= 10 ESC[K (erase line) sequences.
- Large chunks (>= LARGE_THRESHOLD bytes) are summarized with a content fingerprint so we can spot near-duplicate reprints.
- Time-adjacent duplicates (same fingerprint within WINDOW seconds) are flagged.
"""
import argparse
import hashlib
import os
import re
import sqlite3
import sys
from datetime import datetime

DEFAULT_DB = os.path.expanduser("~/.vibe_rails/state.db")
LARGE_THRESHOLD = 100       # bytes
WINDOW_SECS = 3.0

ESC = 0x1B


def strip_ansi(b: bytes) -> bytes:
    """Strip CSI / OSC / single-char ESC sequences, leaving only printable bytes + CR/LF."""
    out = bytearray()
    i = 0
    n = len(b)
    while i < n:
        c = b[i]
        if c == ESC:
            if i + 1 < n and b[i + 1] == 0x5B:
                j = i + 2
                while j < n and not (0x40 <= b[j] <= 0x7E):
                    j += 1
                i = j + 1 if j < n else n
                continue
            elif i + 1 < n and b[i + 1] == 0x5D:
                j = i + 2
                while j < n and b[j] not in (0x07, 0x9C):
                    j += 1
                i = j + 1 if j < n else n
                continue
            i += 2
            continue
        out.append(c)
        i += 1
    return bytes(out)


def fingerprint(b: bytes) -> str:
    """Content fingerprint ignoring ANSI sequences and whitespace variation."""
    text = strip_ansi(b)
    # collapse runs of whitespace (incl. CRLF) into single spaces
    text = re.sub(rb"\s+", b" ", text).strip()
    return hashlib.md5(text).hexdigest()[:12] + f"::{len(text)}B"


def classify(b: bytes) -> list:
    tags = []
    if b"\x1b[2J" in b:
        tags.append("ERASE_SCREEN")
    if b"\x1b[H" in b or b"\x1b[;H" in b or re.search(rb"\x1b\[1;1H", b):
        tags.append("HOME")
    eol_count = b.count(b"\x1b[K")
    if eol_count >= 5:
        tags.append(f"EOL_ERASE_x{eol_count}")
    if b"\x1b[?1049h" in b:
        tags.append("ALT_SCREEN_ENTER")
    if b"\x1b[?1049l" in b:
        tags.append("ALT_SCREEN_EXIT")
    if b"\x1b[?1047h" in b:
        tags.append("ALT_1047_ENTER")
    if b"\x1b[?1047l" in b:
        tags.append("ALT_1047_EXIT")
    if re.search(rb"\x1b\[8;\d+;\d+t", b):
        tags.append("RESIZE")
    if b"\x1b[?25l" in b:
        tags.append("CURSOR_HIDE")
    if b"\x1b[?25h" in b:
        tags.append("CURSOR_SHOW")
    if b"\x1b[?2026h" in b:
        tags.append("SYNC_ON")
    return tags


def parse_ts(ts: str) -> float:
    ts = ts.replace("Z", "+00:00")
    return datetime.fromisoformat(ts).timestamp()


def main() -> None:
    p = argparse.ArgumentParser()
    p.add_argument("session_id")
    p.add_argument("--db", default=DEFAULT_DB)
    p.add_argument("--min-size", type=int, default=LARGE_THRESHOLD)
    args = p.parse_args()

    db = sqlite3.connect(args.db)
    rows = list(db.execute(
        "SELECT Id, Timestamp, Content FROM SessionLogs WHERE SessionId=? ORDER BY Id",
        (args.session_id,),
    ))

    if not rows:
        print("No rows", file=sys.stderr)
        sys.exit(1)

    t0 = parse_ts(rows[0][1])
    total = sum(len(r[2]) for r in rows)
    print(f"Session {args.session_id}")
    print(f"  {len(rows)} chunks, {total} bytes, span {parse_ts(rows[-1][1]) - t0:.2f}s")
    print()

    recent = []  # (ts, fp, cid)
    summary = []
    for cid, ts, content in rows:
        t = parse_ts(ts) - t0
        tags = classify(content)
        if len(content) < args.min_size and not tags:
            continue
        fp = fingerprint(content)
        dup_note = ""
        for pt, pfp, pcid in recent:
            if pfp == fp and t - pt < WINDOW_SECS:
                dup_note = f"  <-- DUP of chunk {pcid} ({t - pt:.3f}s ago)"
                break
        recent.append((t, fp, cid))
        if len(recent) > 50:
            recent.pop(0)
        summary.append(f"+{t:6.3f}s  #{cid}  {len(content):5}B  [{','.join(tags) or '-'}]  fp={fp}{dup_note}")

    print("\n".join(summary))


if __name__ == "__main__":
    main()
