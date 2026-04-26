"""Peek at codex's plain-text output between two timestamps in a session."""
import argparse
import os
import re
import sqlite3

ANSI_CSI = re.compile(r"\x1b\[[\x30-\x3f]*[\x20-\x2f]*[\x40-\x7e]")
ANSI_OSC = re.compile(r"\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)")
ANSI_OTHER = re.compile(r"\x1b[\(\)\*\+\-\./].")
ANSI_SHORT = re.compile(r"\x1b[78=>DEMc\\]")
CTRL_NP = re.compile(r"[\x00-\x08\x0b-\x0c\x0e-\x1a\x1c-\x1f\x7f]")


def to_plain(b):
    s = b.decode("utf-8", errors="replace")
    for rx in (ANSI_CSI, ANSI_OSC, ANSI_OTHER, ANSI_SHORT):
        s = rx.sub("", s)
    return CTRL_NP.sub("", s)


def main():
    p = argparse.ArgumentParser()
    p.add_argument("session_id")
    p.add_argument("from_ts")
    p.add_argument("to_ts")
    p.add_argument("--db", default=os.path.expanduser("~/.vibe_rails/state.db"))
    p.add_argument("--max-chars", type=int, default=4000)
    args = p.parse_args()
    db = sqlite3.connect(args.db)
    rows = db.execute(
        "SELECT Content FROM SessionLogs WHERE SessionId=? AND Timestamp BETWEEN ? AND ? ORDER BY Id",
        (args.session_id, args.from_ts, args.to_ts),
    ).fetchall()
    plain = "".join(to_plain(r[0]) for r in rows)
    plain = re.sub(r"\s+", " ", plain).strip()
    print(plain[: args.max_chars])


if __name__ == "__main__":
    main()
