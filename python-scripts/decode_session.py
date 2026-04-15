"""
Decode a VibeRails session's raw SessionLogs BLOBs into a human-readable
transcript with ANSI escape sequences spelled out.

Useful for investigating:
  - Double-print / redundant full-screen redraws
  - Resize-triggered reprint bugs
  - DECSET mode handling (alt-screen, cursor visibility, etc.)
  - Any case where you need to see what the backend actually fed xterm.js

Usage:
    python decode_session.py <session-id> [--out path]
    python decode_session.py --list [N]    # list N most recent sessions

Output: each SessionLogs chunk on its own, with id + timestamp + length
headers, and bytes rendered as:
    \\e[...X     CSI sequences (X is final byte)
    \\e]...\\a   OSC sequences
    \\r \\n       CR / LF (LF also emits a real newline for readability)
    \\a          BEL
    \\xNN        other control / non-ASCII bytes
"""
import argparse
import os
import sqlite3
import sys

DEFAULT_DB = os.path.expanduser("~/.vibe_rails/state.db")


def visualize(b: bytes) -> str:
    out = []
    i = 0
    n = len(b)
    while i < n:
        c = b[i]
        if c == 0x1B:
            # CSI: ESC [ ... <final 0x40-0x7E>
            if i + 1 < n and b[i + 1] == 0x5B:
                j = i + 2
                while j < n and not (0x40 <= b[j] <= 0x7E):
                    j += 1
                if j < n:
                    out.append("\\e[" + b[i + 2 : j].decode("latin-1", "replace") + chr(b[j]))
                    i = j + 1
                    continue
            # OSC: ESC ] ... <BEL or ST>
            elif i + 1 < n and b[i + 1] == 0x5D:
                j = i + 2
                while j < n and b[j] not in (0x07, 0x9C):
                    j += 1
                if j < n:
                    out.append("\\e]" + b[i + 2 : j].decode("latin-1", "replace") + "\\a")
                    i = j + 1
                    continue
            out.append("\\e")
            i += 1
        elif c == 0x0D:
            out.append("\\r")
            i += 1
        elif c == 0x0A:
            out.append("\\n\n")
            i += 1
        elif c == 0x07:
            out.append("\\a")
            i += 1
        elif c < 0x20 or c >= 0x7F:
            out.append("\\x%02x" % c)
            i += 1
        else:
            out.append(chr(c))
            i += 1
    return "".join(out)


def list_recent(db_path: str, limit: int) -> None:
    db = sqlite3.connect(db_path)
    rows = db.execute(
        "SELECT Id, Cli, StartedUTC, EndedUTC, WorkingDirectory "
        "FROM Sessions ORDER BY StartedUTC DESC LIMIT ?",
        (limit,),
    )
    for sid, cli, started, ended, wd in rows:
        print(f"{sid}  {cli:<10}  {started}  {ended or '(open)':<30}  {wd}")


def dump_session(db_path: str, session_id: str, out_path: str) -> None:
    db = sqlite3.connect(db_path)
    cur = db.execute(
        "SELECT Id, Timestamp, Content FROM SessionLogs "
        "WHERE SessionId = ? ORDER BY Id",
        (session_id,),
    )
    count = 0
    total = 0
    with open(out_path, "w", encoding="utf-8") as f:
        for cid, ts, content in cur:
            count += 1
            total += len(content)
            f.write(f"\n===== CHUNK {cid} @ {ts} (len={len(content)}) =====\n")
            f.write(visualize(content))
            f.write("\n")
    if count == 0:
        print(f"No SessionLogs rows for session {session_id}", file=sys.stderr)
        sys.exit(1)
    print(f"Wrote {count} chunks ({total} bytes) to {out_path}")


def main() -> None:
    p = argparse.ArgumentParser(description=__doc__.splitlines()[1])
    p.add_argument("session_id", nargs="?", help="Session GUID")
    p.add_argument("--db", default=DEFAULT_DB, help=f"Path to state.db (default: {DEFAULT_DB})")
    p.add_argument("--out", help="Output file (default: <session_id>.decoded.txt in cwd)")
    p.add_argument("--list", nargs="?", const=20, type=int, metavar="N",
                   help="List the N most recent sessions and exit (default N=20)")
    args = p.parse_args()

    if args.list is not None:
        list_recent(args.db, args.list)
        return

    if not args.session_id:
        p.error("session_id is required (or use --list)")

    out = args.out or f"{args.session_id}.decoded.txt"
    dump_session(args.db, args.session_id, out)


if __name__ == "__main__":
    main()
