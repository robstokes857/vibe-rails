"""Quick helper to dump specific chunks from a session by id."""
import argparse
import os
import sqlite3
import sys

sys.path.insert(0, os.path.dirname(__file__))
from decode_session import visualize  # reuse

DEFAULT_DB = os.path.expanduser("~/.vibe_rails/state.db")


def main() -> None:
    p = argparse.ArgumentParser()
    p.add_argument("session_id")
    p.add_argument("chunk_ids", nargs="+", type=int)
    p.add_argument("--db", default=DEFAULT_DB)
    args = p.parse_args()

    db = sqlite3.connect(args.db)
    for cid in args.chunk_ids:
        row = db.execute(
            "SELECT Id, Timestamp, Content FROM SessionLogs WHERE SessionId=? AND Id=?",
            (args.session_id, cid),
        ).fetchone()
        if not row:
            print(f"# chunk {cid} not found")
            continue
        _, ts, content = row
        print(f"===== CHUNK {cid} @ {ts} (len={len(content)}) =====")
        print(visualize(content))
        print()


if __name__ == "__main__":
    main()
