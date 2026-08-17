"""build_corpus.py — one streaming pass over ProxyExchanges into the mining corpus.

Usage:
    python build_corpus.py --name <agent-name> [--limit N] [--batch N]
                           [--db PATH] [--corpus PATH] [--steal-lock]

What it does: reads ~/.vibe_rails/proxy_exchanges.db READ-ONLY (WAL; safe while
VibeRails runs), extracts every tool-output string from RequestBefore, pairs it
positionally with RequestAfter, and upserts deduped rows into
~/.vibe_rails/mining_corpus.db (Outputs + Exchanges + Meta). Incremental: checkpoints
Meta.last_src_rowid every batch, so re-runs only process new exchanges. It never
SELECTs ResponseBody or the trailing counter columns, so each source row's overflow
chain is only walked to the end of RequestAfter.

CONCURRENCY (multi-agent safe): only ONE build may run at a time — enforced by an
OS-held lock on ~/.vibe_rails/mining_corpus.build.lock. A second invocation exits and
tells you who holds the lock. The operating system releases the lock if the holder
dies, so stale-lock stealing is neither needed nor safe. Readers (experiment.py /
corpus_stats.py) are unaffected: WAL lets them read a consistent snapshot mid-build.

--name is REQUIRED: it identifies the lock holder and is stamped into Meta.built_by.

CHANGE PROTOCOL: shared infrastructure — changes go in a NEW file, or ask Rob whether
other agents are running before editing in place (runbooks/token_saver/mining_runbook.md §6).
"""

from __future__ import annotations

import argparse
import datetime
import json
import os
import secrets
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import mininglib as lib

SUFFIXES = ("/v1/messages", "/responses", "/chat/completions")


def utcnow() -> str:
    return datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f0Z")


def _try_lock(handle) -> bool:
    """Take a non-blocking process lock that the OS releases on process death."""
    handle.seek(0)
    if os.name == "nt":
        import msvcrt
        try:
            msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
            return True
        except OSError:
            return False

    import fcntl
    try:
        fcntl.flock(handle.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        return True
    except OSError:
        return False


def _unlock(handle) -> None:
    handle.seek(0)
    if os.name == "nt":
        import msvcrt
        msvcrt.locking(handle.fileno(), msvcrt.LK_UNLCK, 1)
        return

    import fcntl
    fcntl.flock(handle.fileno(), fcntl.LOCK_UN)


def acquire_lock(name: str, steal: bool):
    os.makedirs(lib.VIBE_DIR, exist_ok=True)
    handle = open(lib.BUILD_LOCK, "a+b")
    owner_path = lib.BUILD_LOCK + ".owner"

    # msvcrt locks a byte range, so make byte zero exist before attempting the lock. Two
    # first-time openers may both write this harmless sentinel; only one can lock it.
    handle.seek(0, os.SEEK_END)
    if handle.tell() == 0:
        handle.write(b"\0")
        handle.flush()

    if not _try_lock(handle):
        try:
            with open(owner_path, encoding="utf-8") as owner_file:
                holder = owner_file.read() or "unknown holder"
        except OSError:
            holder = "unknown holder"
        print("[build] another build holds the OS lock: %s" % holder)
        if steal:
            print("[build] --steal-lock cannot override a live OS lock; stop that process first.")
        handle.close()
        return None

    payload = json.dumps({
        "name": name,
        "pid": os.getpid(),
        "started": utcnow(),
        "token": secrets.token_hex(16),
    })
    try:
        with open(owner_path, "w", encoding="utf-8") as owner_file:
            owner_file.write(payload)
            owner_file.flush()
            os.fsync(owner_file.fileno())
    except Exception:
        release_lock(handle)
        raise
    return handle


def release_lock(handle) -> None:
    try:
        _unlock(handle)
    finally:
        handle.close()


def refresh_allowlisted_flags(corpus) -> None:
    """Migrate persisted classifications to the current production allowlist mirror."""
    corpus.execute("UPDATE Outputs SET Allowlisted = 0 WHERE Allowlisted <> 0")
    for provider, tools in lib.ALLOWLISTS.items():
        ordered_tools = sorted(tools)
        placeholders = ",".join("?" for _ in ordered_tools)
        corpus.execute(
            "UPDATE Outputs SET Allowlisted = 1 WHERE Provider = ? AND Tool IN (%s)"
            % placeholders,
            (provider, *ordered_tools))
    corpus.commit()


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--name", required=True, help="agent/run name (lock holder identity)")
    ap.add_argument("--db", default=lib.DEFAULT_SRC_DB, help="source proxy_exchanges.db")
    ap.add_argument("--corpus", default=lib.DEFAULT_CORPUS_DB, help="corpus db to build")
    ap.add_argument("--batch", type=int, default=64)
    ap.add_argument("--limit", type=int, default=0, help="stop after N source rows (0 = all)")
    ap.add_argument(
        "--steal-lock",
        action="store_true",
        help="legacy compatibility flag; live OS locks cannot be stolen")
    args = ap.parse_args()

    if not os.path.exists(args.db):
        print("[build] source not found: %s" % args.db)
        return 1
    lock_handle = acquire_lock(args.name, args.steal_lock)
    if lock_handle is None:
        return 2

    started = time.time()
    rows_done = chars_scanned = outputs_seen = parse_fails = 0
    try:
        src = lib.open_ro(args.db)
        corpus = lib.open_corpus_rw(args.corpus)
        corpus.executescript(lib.CORPUS_SCHEMA)
        refresh_allowlisted_flags(corpus)
        row = corpus.execute("SELECT Value FROM Meta WHERE Key='last_src_rowid'").fetchone()
        last_rowid = int(row[0]) if row else 0
        print("[build] starting after source rowid %d (name=%s)" % (last_rowid, args.name), flush=True)

        while True:
            if args.limit and rows_done >= args.limit:
                break
            take = min(args.batch, args.limit - rows_done) if args.limit else args.batch
            batch = src.execute(
                "SELECT rowid, Id, CreatedUTC, Provider, Path, StatusCode, RequestBefore, RequestAfter"
                " FROM ProxyExchanges WHERE rowid > ? ORDER BY rowid LIMIT ?",
                (last_rowid, take)).fetchall()
            if not batch:
                break
            corpus.execute("BEGIN")
            for (rowid, ex_id, created, provider, path, status, before, after) in batch:
                last_rowid = rowid
                rows_done += 1
                chars_scanned += len(before) + len(after)
                degenerate = 1 if (before == "" and after == "") else 0
                # Parsed=1 means "nothing went wrong": non-rewrite endpoints (count_tokens,
                # model polls) have nothing to parse and stay 1; only a JSON body that a
                # rewrite endpoint failed to parse sets 0.
                parsed, records, after_records = 1, [], []
                if not degenerate and path.endswith(SUFFIXES):
                    records, ok_b = lib.extract_outputs(path, before)
                    after_records, ok_a = lib.extract_outputs(path, after)
                    parsed = 1 if (ok_b and ok_a) else 0
                    if not parsed:
                        parse_fails += 1
                        records, after_records = [], []
                paired = len(records) == len(after_records)
                for i, (tool, cmd, text) in enumerate(records):
                    if text == "":
                        continue
                    outputs_seen += 1
                    after_text = after_records[i][2] if paired else None
                    shape, decline = lib.classify_command(cmd)
                    store_after = after_text if (after_text is not None and after_text != text) else None
                    corpus.execute(
                        "INSERT INTO Outputs(Hash, Provider, Tool, Command, Raw, After, RawChars,"
                        " AfterChars, Resends, FirstSeenUTC, LastSeenUTC, FirstExchangeId,"
                        " Allowlisted, CmdShape, DeclineReason, HasCtl)"
                        " VALUES(?,?,?,?,?,?,?,?,1,?,?,?,?,?,?,?)"
                        " ON CONFLICT(Hash) DO UPDATE SET Resends = Resends + 1,"
                        " LastSeenUTC = excluded.LastSeenUTC, After = excluded.After,"
                        " AfterChars = excluded.AfterChars, Allowlisted = excluded.Allowlisted",
                        (lib.output_hash(provider, tool, cmd, text), provider, tool, cmd, text,
                         store_after, len(text),
                         len(after_text) if after_text is not None else None,
                         created, created, ex_id,
                         1 if tool in lib.ALLOWLISTS.get(provider, set()) else 0,
                         shape, decline,
                         1 if any(c in text for c in lib.CTL_CHARS) else 0))
                corpus.execute(
                    "INSERT OR REPLACE INTO Exchanges(SrcRowid, Id, CreatedUTC, Provider, Path,"
                    " StatusCode, ReqChars, ReqCharsAfter, Passthrough, Degenerate, Parsed, NumOutputs)"
                    " VALUES(?,?,?,?,?,?,?,?,?,?,?,?)",
                    (rowid, ex_id, created, provider, path, status, len(before), len(after),
                     1 if before == after else 0, degenerate, parsed, len(records)))
            corpus.execute("INSERT OR REPLACE INTO Meta(Key, Value) VALUES('last_src_rowid', ?)",
                           (str(last_rowid),))
            corpus.execute("INSERT OR REPLACE INTO Meta(Key, Value) VALUES('built_by', ?)", (args.name,))
            corpus.execute("INSERT OR REPLACE INTO Meta(Key, Value) VALUES('updated_utc', ?)", (utcnow(),))
            corpus.commit()
            print("[build] rowid %d | %d rows | %.0f MB scanned | %d outputs | %.0fs"
                  % (last_rowid, rows_done, chars_scanned / 1e6, outputs_seen, time.time() - started),
                  flush=True)

        n_out = corpus.execute("SELECT COUNT(*), COALESCE(SUM(RawChars),0), COALESCE(SUM(RawChars*Resends),0)"
                               " FROM Outputs").fetchone()
        print("[build] DONE: +%d rows this run (%d parse failures). Corpus: %d unique outputs,"
              " %.1f MB unique chars, %.1f MB wire-weighted chars."
              % (rows_done, parse_fails, n_out[0], n_out[1] / 1e6, n_out[2] / 1e6), flush=True)
        corpus.close()
        src.close()
        return 0
    finally:
        release_lock(lock_handle)


if __name__ == "__main__":
    sys.exit(main())
