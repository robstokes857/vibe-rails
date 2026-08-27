"""One-off analysis: load a binary fixture (the format export_chunks_fixture.py
produces) and replay it through the WaitingForUserInputObserver statistical
classifier, dumping the per-chunk verdict + buffer stats at the moment of any candidate fire
and at a few sample points. Lets us see what numerical signal could
distinguish a false-positive window (codex Working sub-phase emitting bit-
identical cursor-park frames) from a true-positive window (codex sitting at
the composer / approval menu). The production observer additionally vetoes a
candidate while its terminal model still visibly renders the Codex Working row."""
import argparse
import struct
from collections import deque, Counter

SAMPLE_WINDOW_MS = 5000
IDLE_CHUNK_SIZE_THRESHOLD = 50
IDLE_MAX_UNIQUE_BIG_CHUNKS = 5
IDLE_MIN_TOP_CHUNK_COUNT = 2
IDLE_MIN_BIG_CHUNK_SAMPLES = 4
QUIET_BUFFER_THRESHOLD = 5
IDLE_MIN_OBSERVATION_MS = 2000
SMALL_CHUNK_CONCENTRATION_MIN_SAMPLES = 6


def classify(chunks):
    big_counts = Counter()
    big_total = 0
    small_counts = Counter()
    small_total = 0
    for _, content in chunks:
        if len(content) >= IDLE_CHUNK_SIZE_THRESHOLD:
            big_counts[content] += 1
            big_total += 1
        else:
            small_counts[content] += 1
            small_total += 1
    total = len(chunks)
    stats = dict(
        total=total,
        big_total=big_total,
        big_unique=len(big_counts),
        big_top=max(big_counts.values()) if big_counts else 0,
        small_total=small_total,
        small_unique=len(small_counts),
        small_top=max(small_counts.values()) if small_counts else 0,
    )
    if total < QUIET_BUFFER_THRESHOLD:
        return "indeterminate", stats
    if big_total < IDLE_MIN_BIG_CHUNK_SAMPLES:
        return "working", stats
    if len(big_counts) > IDLE_MAX_UNIQUE_BIG_CHUNKS:
        return "working", stats
    if stats["big_top"] < IDLE_MIN_TOP_CHUNK_COUNT:
        return "working", stats
    if small_total >= SMALL_CHUNK_CONCENTRATION_MIN_SAMPLES and stats["small_top"] < len(small_counts):
        return "working", stats
    return "idle", stats


def load_fixture(path):
    with open(path, "rb") as f:
        data = f.read()
    pos = 0
    (count,) = struct.unpack_from("<I", data, pos)
    pos += 4
    out = []
    for _ in range(count):
        byte_count, ms_offset = struct.unpack_from("<II", data, pos)
        pos += 8
        content = data[pos : pos + byte_count]
        pos += byte_count
        out.append((ms_offset, content))
    return out


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("fixture")
    p.add_argument("--every", type=int, default=20, help="dump every Nth chunk's stats")
    args = p.parse_args()

    chunks_in = load_fixture(args.fixture)
    print(f"Loaded {len(chunks_in)} chunks from {args.fixture}")

    buffer = deque()
    has_fired = False
    fired_chunks = []

    for i, (ms, content) in enumerate(chunks_in):
        buffer.append((ms, content))
        cutoff = ms - SAMPLE_WINDOW_MS
        while buffer and buffer[0][0] < cutoff:
            buffer.popleft()

        oldest_ms = buffer[0][0] if buffer else ms
        if ms - oldest_ms < IDLE_MIN_OBSERVATION_MS:
            continue

        verdict, stats = classify(buffer)
        if verdict == "working":
            has_fired = False
        elif verdict == "idle" and not has_fired:
            has_fired = True
            fired_chunks.append((i, ms, stats))
            print(f"FIRE  @ T+{ms/1000:6.2f}s (chunk #{i:4d})  "
                  f"total={stats['total']:3d}  "
                  f"big={stats['big_total']:3d}/uniq={stats['big_unique']}/top={stats['big_top']}  "
                  f"small={stats['small_total']:3d}/uniq={stats['small_unique']}/top={stats['small_top']}")

        if i % args.every == 0:
            print(f"      @ T+{ms/1000:6.2f}s (chunk #{i:4d})  {verdict:13s}  "
                  f"total={stats['total']:3d}  "
                  f"big={stats['big_total']:3d}/uniq={stats['big_unique']}/top={stats['big_top']}  "
                  f"small={stats['small_total']:3d}/uniq={stats['small_unique']}/top={stats['small_top']}")

    print(f"\nTotal fires: {len(fired_chunks)}")


if __name__ == "__main__":
    main()
