"""Export SessionLogs chunks from a session as a binary fixture file:
    [uint32 chunk_count]
    repeated chunk_count times:
        [uint32 byte_count][uint32 ms_offset_from_start][bytes]
   All ints little-endian. Tests can replay each chunk at its ms offset
   to faithfully simulate the original timing into a TimeProvider mock."""
import argparse
import os
import sqlite3
import struct
from datetime import datetime

DEFAULT_DB = os.path.expanduser("~/.vibe_rails/state.db")


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


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("session_id")
    p.add_argument("--from-id", type=int, required=True)
    p.add_argument("--to-id", type=int, required=True)
    p.add_argument("--out", required=True, help="Output binary fixture path")
    p.add_argument("--db", default=DEFAULT_DB)
    args = p.parse_args()

    db = sqlite3.connect(args.db)
    rows = db.execute(
        "SELECT Timestamp, Content FROM SessionLogs WHERE SessionId=? AND Id BETWEEN ? AND ? ORDER BY Id",
        (args.session_id, args.from_id, args.to_id),
    ).fetchall()
    if not rows:
        raise SystemExit(f"No chunks found for session {args.session_id} in range {args.from_id}..{args.to_id}")

    start_ts = parse_ts(rows[0][0])
    out_dir = os.path.dirname(args.out)
    if out_dir:
        os.makedirs(out_dir, exist_ok=True)
    with open(args.out, "wb") as f:
        f.write(struct.pack("<I", len(rows)))
        for ts_str, content in rows:
            ts = parse_ts(ts_str)
            ms_offset = int((ts - start_ts).total_seconds() * 1000)
            if ms_offset < 0:
                ms_offset = 0
            f.write(struct.pack("<II", len(content), ms_offset))
            f.write(content)

    total = sum(len(c) for _, c in rows)
    span_ms = int((parse_ts(rows[-1][0]) - start_ts).total_seconds() * 1000)
    print(f"Wrote {len(rows)} chunks ({total} bytes) spanning {span_ms} ms to {args.out}")


if __name__ == "__main__":
    main()
