"""timeline_stats.py — descriptive report over the SEQUENCE dataset (mining_timeline.db).

Usage:
    python timeline_stats.py --name <agent-name> [--provider anthropic] [--since 2026-07-29]
                             [--db PATH]

Companion to corpus_stats.py (which reads the deduped corpus). This one reads what
scan_conversations.py produced and answers the questions the corpus cannot:

  A. real usage per day (uncached / cache-write / cache-read / output) and a cost-equivalent
     view (anthropic: in*1 + cacheW*1.25 + cacheR*0.1 + out*5; openai: (in-cached)*1 +
     cached*0.1 + out*8 — OpenAI's input_tokens INCLUDES cached_tokens)
  B. request classes (model x has-tools x system-prompt size) — finds side-request families
     such as the auto-mode security classifier
  C. body composition of main-conversation requests (what fraction is even tool output)
  D. cache health on mature turns (miss ratio buckets) and big-miss idle gaps
  E. duplicate tool results within a conversation
  F. reactions to saver markers (what the model did right after an elided/deduped result)
  G. elisions per day with command heads
  H. passthrough anatomy

Writes results/<name>/timeline_stats_<provider>_<utc>.md and prints it. Read-only; --name is
REQUIRED so concurrent runs never collide (runbooks/token_saver/mining_runbook.md §6).
"""

from __future__ import annotations

import argparse
import collections
import datetime
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import mininglib as lib
from rerun_economics import classify as classify_reaction

DEFAULT_DB = os.path.join(lib.VIBE_DIR, "mining_timeline.db")


def mb(x):
    return "%.1f" % ((x or 0) / 1e6)


def M(x):
    return "%.2fM" % ((x or 0) / 1e6)


def cost_eq(prov, i, cc, cr, o):
    i, cc, cr, o = (x or 0 for x in (i, cc, cr, o))
    if prov == "anthropic":
        return i + 1.25 * cc + 0.1 * cr + 5 * o
    return (i - cr) + 0.1 * cr + 8 * o


def parse_ts(ts):
    return datetime.datetime.strptime(ts[:26], "%Y-%m-%dT%H:%M:%S.%f")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--name", required=True)
    ap.add_argument("--provider", default="anthropic")
    ap.add_argument("--since", default="2026-07-29")
    ap.add_argument("--db", default=DEFAULT_DB)
    args = ap.parse_args()
    prov, since = args.provider, args.since
    con = lib.open_ro(args.db)
    L = []
    w = L.append
    ts = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%d_%H%M%S")
    w("# Timeline stats — %s, since %s — %s UTC (by `%s`)" % (prov, since, ts, args.name))
    w("")

    # ---- A. usage per day
    w("## A. usage per day (cost-eq: %s)" % ("in*1 + cacheW*1.25 + cacheR*0.1 + out*5" if prov == "anthropic" else "(in-cached)*1 + cached*0.1 + out*8"))
    w("")
    w("| day | requests | uncached | cache-write | cache-read | output | cost-eq | big misses (>50k cacheW) |")
    w("|---|---|---|---|---|---|---|---|")
    tot = collections.Counter()
    for d, n, i, cc, cr, o, miss, misstok in con.execute(
            """SELECT substr(CreatedUTC,1,10), COUNT(*), SUM(InputTokens), SUM(CacheCreationTokens), SUM(CacheReadTokens),
               SUM(OutputTokens), SUM(CASE WHEN CacheCreationTokens>50000 THEN 1 ELSE 0 END),
               SUM(CASE WHEN CacheCreationTokens>50000 THEN CacheCreationTokens ELSE 0 END)
               FROM Requests WHERE Provider=? AND StatusCode=200 AND CreatedUTC>=? GROUP BY 1 ORDER BY 1""", (prov, since)):
        i, cc, cr, o, misstok = (x or 0 for x in (i, cc, cr, o, misstok))
        ce = cost_eq(prov, i, cc, cr, o)
        uncached = i if prov == "anthropic" else i - cr
        for k, v in (("n", n), ("i", uncached), ("cc", cc), ("cr", cr), ("o", o), ("ce", ce), ("miss", miss or 0), ("misstok", misstok)):
            tot[k] += v
        w("| %s | %d | %s | %s | %s | %s | %s | %d (%s) |" % (d, n, M(uncached), M(cc), M(cr), M(o), M(ce), miss or 0, M(misstok)))
    ce = tot["ce"] or 1
    w("| **total** | %d | %s | %s | %s | %s | **%s** | %d (%s) |" % (tot["n"], M(tot["i"]), M(tot["cc"]), M(tot["cr"]), M(tot["o"]), M(ce), tot["miss"], M(tot["misstok"])))
    w("")
    w("cost-eq shares: uncached %.0f%%, cache-write %.0f%%, cache-read %.0f%%, output %.0f%%" % (
        100 * tot["i"] / ce, 100 * 1.25 * tot["cc"] / ce if prov == "anthropic" else 0, 100 * 0.1 * tot["cr"] / ce,
        100 * (5 if prov == "anthropic" else 8) * tot["o"] / ce))
    w("")

    # ---- B. request classes
    w("## B. request classes (model x tools present x system-prompt size)")
    w("")
    w("| model | tools | system | requests | req MB | cost-eq | avg msgs | shell-result MB |")
    w("|---|---|---|---|---|---|---|---|")
    for r in con.execute(
            """SELECT Model, CASE WHEN NumToolsDef>0 THEN 'tools' ELSE 'no-tools' END,
               CASE WHEN SystemChars>100000 THEN '>100K' WHEN SystemChars>20000 THEN '20-100K' ELSE '<20K' END,
               COUNT(*), SUM(TotalChars), SUM(InputTokens), SUM(CacheCreationTokens), SUM(CacheReadTokens), SUM(OutputTokens),
               AVG(MsgCount), SUM(ToolResultAllowChars)
               FROM Requests WHERE Provider=? AND StatusCode=200 AND CreatedUTC>=? GROUP BY 1,2,3 ORDER BY 5 DESC""", (prov, since)):
        w("| %s | %s | %s | %d | %s | %s | %.1f | %s |" % (r[0], r[1], r[2], r[3], mb(r[4]), M(cost_eq(prov, *r[5:9])), r[9] or 0, mb(r[10])))
    w("")

    # ---- C. composition
    w("## C. composition of main-conversation requests (tools present), char-weighted")
    w("")
    r = con.execute(
        """SELECT COUNT(*), SUM(TotalChars), SUM(SystemChars), SUM(ToolsDefChars), SUM(UserTextChars), SUM(SysReminderChars),
           SUM(AsstTextChars), SUM(ThinkingChars), SUM(ToolUseInputChars), SUM(ToolResultChars), SUM(ToolResultAllowChars),
           SUM(ToolResultAfterChars), SUM(ToolResultAllowAfterChars), SUM(AfterTotalChars), AVG(NumToolsDef), AVG(ToolsDefChars)
           FROM Requests WHERE Provider=? AND StatusCode=200 AND NumToolsDef>0 AND CreatedUTC>=?""", (prov, since)).fetchone()
    if r[0]:
        total = r[1] or 1
        w("| slice | MB | share |")
        w("|---|---|---|")
        for name, v in zip(["system", "tools_def", "user text (all)", "· of which system-reminders", "assistant text", "thinking / reasoning",
                            "tool_use inputs", "tool results (all)", "· of which allowlisted"], r[2:11]):
            w("| %s | %s | %.1f%% |" % (name, mb(v), 100 * (v or 0) / total))
        w("| **total** | %s | requests %d, avg tools/req %.0f, avg tools_def chars/req %.0f |" % (mb(total), r[0], r[14] or 0, r[15] or 0))
        w("")
        w("saver removed %s MB of tool_result chars = %.2f%% of total wire" % (mb((r[9] or 0) - (r[11] or 0)), 100 * ((r[1] or 0) - (r[13] or 0)) / total))
    else:
        w("(no requests carry a tools[] array for this provider — Codex declares tools inside input[])")
    w("")

    # ---- D. cache health
    w("## D. cache health on mature turns (>=10 messages): miss ratio = cacheW / (cacheW + cacheR)")
    w("")
    w("| model | bucket | turns | cache-write | cache-read |")
    w("|---|---|---|---|---|")
    for r in con.execute(
            """SELECT Model, CASE WHEN CacheCreationTokens*1.0/(CacheCreationTokens+CacheReadTokens+1) < 0.05 THEN 'a: <5%'
               WHEN CacheCreationTokens*1.0/(CacheCreationTokens+CacheReadTokens+1) < 0.5 THEN 'b: 5-50%' ELSE 'c: >50% (re-written)' END,
               COUNT(*), SUM(CacheCreationTokens), SUM(CacheReadTokens) FROM Requests
               WHERE Provider=? AND StatusCode=200 AND MsgCount>=10 AND CreatedUTC>=? GROUP BY 1,2 ORDER BY 1,2""", (prov, since)):
        w("| %s | %s | %d | %s | %s |" % (r[0], r[1], r[2], M(r[3]), M(r[4])))
    w("")
    prev = {}
    gaps = collections.Counter()
    gap_tok = collections.Counter()
    for ck, tsv, cc in con.execute("""SELECT ConvKey, CreatedUTC, CacheCreationTokens FROM Requests
            WHERE Provider=? AND StatusCode=200 AND CreatedUTC>=? ORDER BY CreatedUTC""", (prov, since)):
        t = parse_ts(tsv)
        p = prev.get(ck)
        prev[ck] = t
        if (cc or 0) <= 50000:
            continue
        if p is None:
            k = "first request of conversation"
        else:
            g = (t - p).total_seconds() / 60
            k = "<5 min" if g < 5 else "5-60 min" if g < 60 else "1-6 h" if g < 360 else ">6 h"
        gaps[k] += 1
        gap_tok[k] += cc
    w("big misses (>50k cache-write) by idle gap since the previous request of the same conversation:")
    w("")
    for k, n in gaps.most_common():
        w("- %s: %d misses, %s tokens" % (k, n, M(gap_tok[k])))
    w("")

    # ---- E/F/G over timelines
    dup = collections.defaultdict(lambda: [0, 0, 0.0])
    kinds = collections.Counter()
    marker_kinds = collections.Counter()
    byday = collections.defaultdict(lambda: [0, 0, collections.Counter()])
    nconv = 0
    for ck, nreq, first, tline in con.execute(
            "SELECT ConvKey, NumRequests, FirstUTC, Timeline FROM Conversations WHERE Provider=? AND FirstUTC>=?", (prov, since)):
        items = json.loads(tline)
        if not items:
            continue
        nconv += 1
        seen = set()
        by_i = {it["i"]: it for it in items}
        for it in items:
            h = it.get("h")
            if h and it.get("raw", 0) >= 200:
                key = (it["tool"], h)
                if key in seen:
                    d = dup[it["tool"]]
                    d[0] += 1
                    d[1] += it["raw"]
                    d[2] += it["raw"] * max(1.0, nreq * (1 - it["i"] / max(1, len(items))))
                seen.add(key)
            if it.get("el") or it.get("dd") or it.get("pt"):
                marker_kinds["el" if it.get("el") else ("dd" if it.get("dd") else "pt")] += 1
            if it.get("el"):
                d = byday[first[:10]]
                d[0] += 1
                d[1] += it["raw"] - it["aft"]
                cmd = it.get("cmd") or ""
                head = re.sub(r"^(cd\s+\S+\s*(&&|;)\s*)+", "", cmd).split()[:1]
                d[2][(head[0][:16] if head else "?")] += 1
            for pi in it.get("react_to") or []:
                p = by_i.get(pi)
                if p is None:
                    continue
                mk = "el" if p.get("el") else ("dd" if p.get("dd") else "pt")
                kinds[(mk, classify_reaction(p, it) or "unrelated")] += 1
    w("## E. duplicate tool results within a conversation (%d conversations)" % nconv)
    w("")
    w("| tool | duplicates | unique MB | est. wire MB |")
    w("|---|---|---|---|")
    for tool, (n, u, wv) in sorted(dup.items(), key=lambda kv: -kv[1][2])[:10]:
        w("| %s | %d | %s | %s |" % (tool, n, mb(u), mb(wv)))
    w("")
    w("## F. reactions to saver markers (results carrying a marker: %s)" % dict(marker_kinds))
    w("")
    w("| marker | reaction of the next assistant turn | tool calls |")
    w("|---|---|---|")
    for (mk, k), n in sorted(kinds.items(), key=lambda kv: (kv[0][0], -kv[1])):
        w("| %s | %s | %d |" % (mk, k, n))
    w("")
    w("## G. elisions by day (command heads)")
    w("")
    for d, (n, ch, heads) in sorted(byday.items()):
        w("- %s: %d elisions, %d chars removed, %s" % (d, n, ch, dict(heads.most_common(5))))
    w("")

    # ---- H. passthrough anatomy
    w("## H. passthrough anatomy (status 200)")
    w("")
    for r in con.execute(
            """SELECT CASE WHEN ToolResultAllowChars=0 THEN 'no allowlisted results' WHEN Passthrough=1 THEN 'allowlisted results, unchanged' ELSE 'rewritten' END,
               COUNT(*), SUM(TotalChars), SUM(ToolResultAllowChars) FROM Requests WHERE Provider=? AND StatusCode=200 AND CreatedUTC>=? GROUP BY 1""", (prov, since)):
        w("- %s: %d requests, %s MB request wire, %s MB allowlisted results" % (r[0], r[1], mb(r[2]), mb(r[3])))
    con.close()

    outdir = os.path.join(os.path.dirname(os.path.abspath(__file__)), "results", args.name)
    os.makedirs(outdir, exist_ok=True)
    path = os.path.join(outdir, "timeline_stats_%s_%s.md" % (prov, ts))
    with open(path, "w", encoding="utf-8") as f:
        f.write("\n".join(L) + "\n")
    print("\n".join(L))
    print("\n[timeline_stats] report: %s" % path)
    return 0


if __name__ == "__main__":
    sys.exit(main())
