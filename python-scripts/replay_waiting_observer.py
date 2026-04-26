"""Replay SessionLogs through the current WaitingForUserInputObserver
implementation (chunk-repetition heuristic):

  - Per-session 5s rolling window of raw PTY chunks (NOT plain-text).
  - Codex's idle screen repaints the same large ANSI cursor-positioning
    chunk every frame; codex's "Working" state patches a different cell
    each frame, producing many distinct chunks per window.
  - Verdict per chunk:
      Indeterminate: <40 big-chunk samples, or top repeat <20 — leave gate.
      Working:       >5 distinct big chunks in window — clear the gate.
      Idle:          repetitive enough — fire if gate not yet set.
  - Fire once per idle->working->idle cycle.

Mirror of VibeRails/Services/Terminal/Observers/WaitingForUserInputObserver.cs.
"""
import argparse
import os
import sqlite3
from collections import deque
from datetime import datetime, timedelta

DEFAULT_DB = os.path.expanduser("~/.vibe_rails/state.db")
SAMPLE_WINDOW = timedelta(seconds=5)
IDLE_CHUNK_SIZE_THRESHOLD = 50
IDLE_MAX_UNIQUE_BIG_CHUNKS = 5
IDLE_MIN_TOP_CHUNK_COUNT = 20
IDLE_MIN_BIG_CHUNK_SAMPLES = 40
MAX_QUEUED_CHUNKS = 20_000


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


def classify(chunks):
    """Returns 'idle' | 'working' | 'indeterminate'."""
    big_counts = {}
    big_total = 0
    for _, content in chunks:
        if len(content) < IDLE_CHUNK_SIZE_THRESHOLD:
            continue
        big_counts[content] = big_counts.get(content, 0) + 1
        big_total += 1

    if big_total < IDLE_MIN_BIG_CHUNK_SAMPLES:
        return "indeterminate"
    if len(big_counts) > IDLE_MAX_UNIQUE_BIG_CHUNKS:
        return "working"
    top = max(big_counts.values())
    return "idle" if top >= IDLE_MIN_TOP_CHUNK_COUNT else "indeterminate"


def replay(db_path, session_id, verbose):
    db = sqlite3.connect(db_path)
    cur = db.execute(
        "SELECT Id, Timestamp, Content FROM SessionLogs WHERE SessionId=? ORDER BY Id",
        (session_id,),
    )
    chunks = deque()  # entries: (timestamp, raw_text)
    has_fired = False
    fires = []
    chunk_count = 0
    for cid, ts_str, content in cur:
        chunk_count += 1
        ts = parse_ts(ts_str)
        if isinstance(content, bytes):
            text = content.decode("utf-8", errors="replace")
        else:
            text = content
        if not text:
            continue
        chunks.append((ts, text))

        while len(chunks) > MAX_QUEUED_CHUNKS:
            chunks.popleft()

        cutoff = ts - SAMPLE_WINDOW
        while chunks and chunks[0][0] < cutoff:
            chunks.popleft()

        verdict = classify(chunks)
        if verdict == "working":
            has_fired = False
        elif verdict == "idle":
            if not has_fired:
                has_fired = True
                fires.append((cid, ts_str))
                if verbose:
                    print(f">>> FIRE at chunk {cid} @ {ts_str} (window={len(chunks)} chunks)")
        # 'indeterminate': leave has_fired alone

    print(f"\nProcessed {chunk_count} chunks. Detected {len(fires)} fires.")
    for cid, ts in fires:
        print(f"  fire @ chunk {cid} {ts}")


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("session_id")
    p.add_argument("--db", default=DEFAULT_DB)
    p.add_argument("-v", "--verbose", action="store_true")
    args = p.parse_args()
    replay(args.db, args.session_id, args.verbose)


if __name__ == "__main__":
    main()
