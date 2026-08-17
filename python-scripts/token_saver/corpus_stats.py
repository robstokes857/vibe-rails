"""corpus_stats.py — descriptive report over the mining corpus (runbook §4 answers).

Usage:
    python corpus_stats.py --name <agent-name> [--corpus PATH]

Answers the standing questions: passthrough volume, where the unrewritten chars live
(by provider/tool, allowlisted or not), decline reasons by wire weight, command-shape
distribution (including the v1 no-op shapes), ctl-char fail-open impact, and output
size distribution vs the condenser thresholds.

Writes results/<name>/corpus_stats_<utc>.md and prints the headline numbers.

CONCURRENCY (multi-agent safe): read-only on the corpus; writes only under
results/<name>/. --name is REQUIRED so concurrent runs never collide.
CHANGE PROTOCOL: shared infrastructure — changes go in a NEW file, or ask Rob whether
other agents are running before editing in place (runbooks/token_saver/mining_runbook.md §6).
"""

from __future__ import annotations

import argparse
import datetime
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import mininglib as lib


def mb(chars) -> str:
    return "%.1f" % ((chars or 0) / 1e6)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--name", required=True, help="agent/run name; results go to results/<name>/")
    ap.add_argument("--corpus", default=lib.DEFAULT_CORPUS_DB)
    args = ap.parse_args()

    con = lib.open_ro(args.corpus)
    meta = dict(con.execute("SELECT Key, Value FROM Meta"))
    lines = []
    w = lines.append
    ts = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%d_%H%M%S")
    w("# Corpus stats — %s UTC (by `%s`)" % (ts, args.name))
    w("")
    w("Corpus `%s`, src rowid <= %s, updated %s." % (args.corpus, meta.get("last_src_rowid"), meta.get("updated_utc")))
    w("")

    w("## Exchanges")
    w("")
    w("| provider | exchanges | passthrough | degenerate | parse fails | req MB (before) | req MB (after) |")
    w("|---|---|---|---|---|---|---|")
    for r in con.execute(
            "SELECT Provider, COUNT(*), SUM(Passthrough), SUM(Degenerate), SUM(CASE WHEN Degenerate=0"
            " AND Parsed=0 THEN 1 ELSE 0 END), SUM(ReqChars), SUM(ReqCharsAfter)"
            " FROM Exchanges GROUP BY Provider ORDER BY 2 DESC"):
        w("| %s | %d | %d (%.0f%%) | %d | %d | %s | %s |"
          % (r[0], r[1], r[2] or 0, 100.0 * (r[2] or 0) / r[1], r[3] or 0, r[4] or 0, mb(r[5]), mb(r[6])))
    w("")

    w("## Outputs by tool (top 25 by wire-weighted chars)")
    w("")
    w("| provider | tool | allowlisted | unique outputs | unique MB | wire MB | avg resends |")
    w("|---|---|---|---|---|---|---|")
    for r in con.execute(
            "SELECT Provider, Tool, Allowlisted, COUNT(*), SUM(RawChars), SUM(RawChars*Resends),"
            " AVG(Resends) FROM Outputs GROUP BY Provider, Tool ORDER BY 6 DESC LIMIT 25"):
        w("| %s | %s | %s | %d | %s | %s | %.1f |"
          % (r[0], r[1], "yes" if r[2] else "NO", r[3], mb(r[4]), mb(r[5]), r[6]))
    w("")

    w("## Allowlisted shell output: decline reasons for shape stages (wire MB)")
    w("")
    w("| decline reason | unique outputs | unique MB | wire MB |")
    w("|---|---|---|---|")
    for r in con.execute(
            "SELECT COALESCE(DeclineReason, 'recognized:' || CmdShape), COUNT(*), SUM(RawChars),"
            " SUM(RawChars*Resends) FROM Outputs WHERE Allowlisted = 1"
            " GROUP BY 1 ORDER BY 4 DESC"):
        w("| %s | %d | %s | %s |" % (r[0], r[1], mb(r[2]), mb(r[3])))
    w("")

    w("## Command shapes (all outputs, wire MB) — remember GitLog/GitDiff/DirectoryListing are v1 no-ops")
    w("")
    w("| shape | unique outputs | wire MB |")
    w("|---|---|---|")
    for r in con.execute("SELECT CmdShape, COUNT(*), SUM(RawChars*Resends) FROM Outputs"
                         " GROUP BY CmdShape ORDER BY 3 DESC"):
        w("| %s | %d | %s |" % (r[0], r[1], mb(r[2])))
    w("")

    w("## Ctl-char fail-open impact (ESC/BEL/CR present => shape+condense stages decline)")
    w("")
    w("| | unique outputs | unique MB | wire MB |")
    w("|---|---|---|---|")
    for r in con.execute("SELECT CASE HasCtl WHEN 1 THEN 'has ESC/BEL/CR' ELSE 'clean' END,"
                         " COUNT(*), SUM(RawChars), SUM(RawChars*Resends) FROM Outputs"
                         " WHERE Allowlisted = 1 GROUP BY 1 ORDER BY 4 DESC"):
        w("| %s | %d | %s | %s |" % (r[0], r[1], mb(r[2]), mb(r[3])))
    w("")

    w("## Output size distribution (unique outputs)")
    w("")
    w("| size bucket | outputs | unique MB | wire MB |")
    w("|---|---|---|---|")
    for r in con.execute(
            "SELECT CASE WHEN RawChars < 256 THEN 'a: <256' WHEN RawChars < 1024 THEN 'b: 256-1K'"
            " WHEN RawChars < 4096 THEN 'c: 1K-4K' WHEN RawChars < 16384 THEN 'd: 4K-16K'"
            " WHEN RawChars < 65536 THEN 'e: 16K-64K' ELSE 'f: >64K' END, COUNT(*),"
            " SUM(RawChars), SUM(RawChars*Resends) FROM Outputs GROUP BY 1 ORDER BY 1"):
        w("| %s | %d | %s | %s |" % (r[0], r[1], mb(r[2]), mb(r[3])))
    w("")

    w("## What the shipped pipeline already does (paired outputs)")
    w("")
    row = con.execute("SELECT COUNT(*), SUM(RawChars - COALESCE(AfterChars, RawChars)),"
                      " SUM((RawChars - COALESCE(AfterChars, RawChars)) * Resends)"
                      " FROM Outputs WHERE After IS NOT NULL").fetchone()
    total = con.execute("SELECT COUNT(*) FROM Outputs").fetchone()[0]
    w("- outputs the pipeline changed: %d of %d unique (%.1f%%)" % (row[0], total, 100.0 * row[0] / total if total else 0))
    w("- chars it saved: %s MB unique / %s MB wire-weighted" % (mb(row[1]), mb(row[2])))
    con.close()

    outdir = os.path.join(os.path.dirname(os.path.abspath(__file__)), "results", args.name)
    os.makedirs(outdir, exist_ok=True)
    path = os.path.join(outdir, "corpus_stats_%s.md" % ts)
    with open(path, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
    print("\n".join(lines[:40]))
    print("...\n[stats] full report: %s" % path)
    return 0


if __name__ == "__main__":
    sys.exit(main())
