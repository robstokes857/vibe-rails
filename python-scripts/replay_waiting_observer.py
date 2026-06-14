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
IDLE_MIN_TOP_CHUNK_COUNT = 2
IDLE_MIN_BIG_CHUNK_SAMPLES = 4
QUIET_BUFFER_THRESHOLD = 5
SMALL_CHUNK_CONCENTRATION_MIN_SAMPLES = 6
IDLE_MIN_OBSERVATION_SPAN = timedelta(seconds=2)
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
    """Returns ('idle'|'working'|'indeterminate', snapshot dict).

    Faithful mirror of WaitingForUserInputObserver.Classify (C#).
    """
    big_counts = {}
    small_counts = {}
    big_total = 0
    small_total = 0
    total_chunks = 0
    for _, content in chunks:
        total_chunks += 1
        if len(content) >= IDLE_CHUNK_SIZE_THRESHOLD:
            big_counts[content] = big_counts.get(content, 0) + 1
            big_total += 1
        else:
            small_counts[content] = small_counts.get(content, 0) + 1
            small_total += 1

    big_uniq = len(big_counts)
    small_uniq = len(small_counts)
    big_top = max(big_counts.values()) if big_counts else 0
    small_top = max(small_counts.values()) if small_counts else 0
    snap = dict(total=total_chunks, big_total=big_total, big_uniq=big_uniq,
                big_top=big_top, small_total=small_total, small_uniq=small_uniq,
                small_top=small_top)

    # Buffer effectively silent — don't disturb the gate.
    if total_chunks < QUIET_BUFFER_THRESHOLD:
        return "indeterminate", snap

    # Plenty of chunks but none look idle — treat as Working (releases gate).
    if not big_counts or big_total < IDLE_MIN_BIG_CHUNK_SAMPLES:
        return "working", snap

    if big_uniq > IDLE_MAX_UNIQUE_BIG_CHUNKS:
        return "working", snap

    if big_top < IDLE_MIN_TOP_CHUNK_COUNT:
        return "working", snap

    # Small-chunk concentration gate.
    if small_counts and small_total >= SMALL_CHUNK_CONCENTRATION_MIN_SAMPLES:
        if small_top < small_uniq:
            return "working", snap

    return "idle", snap


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

        # Observation-span gate: don't classify until the buffer has been
        # collecting for nearly a full SampleWindow (see IdleMinObservationSpan).
        oldest = chunks[0][0] if chunks else ts
        if ts - oldest < IDLE_MIN_OBSERVATION_SPAN:
            continue

        verdict, snap = classify(chunks)
        if verdict == "working":
            has_fired = False
        elif verdict == "idle":
            if not has_fired:
                has_fired = True
                fires.append((cid, ts_str, snap))
                if verbose:
                    print(f">>> FIRE at chunk {cid} @ {ts_str} {snap}")
        # 'indeterminate': leave has_fired alone

    print(f"\nProcessed {chunk_count} chunks. Detected {len(fires)} fires.")
    for entry in fires:
        cid, ts = entry[0], entry[1]
        snap = entry[2] if len(entry) > 2 else ""
        print(f"  fire @ chunk {cid} {ts} {snap}")


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("session_id")
    p.add_argument("--db", default=DEFAULT_DB)
    p.add_argument("-v", "--verbose", action="store_true")
    args = p.parse_args()
    replay(args.db, args.session_id, args.verbose)


if __name__ == "__main__":
    main()
