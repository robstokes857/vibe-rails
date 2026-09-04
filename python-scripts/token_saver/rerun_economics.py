"""rerun_economics.py — did truncation pay for itself? (Rob's README note, 2026-09-03)

Usage:
    python rerun_economics.py --name <agent-name> [--provider anthropic] [--db PATH]
                              [--chars-per-token 3.6]

For every tool result that carried an elision marker (from scan_conversations.py's per-
conversation timelines): what the saver saved vs what the model's re-fetch cost.

  saved   = elided chars / cpt * (1.25 write + 0.1 per remaining resend)
  re-run  = one extra assistant turn at the conversation's average turn cost (from real usage)
            + the re-fetched output at the same write/resend rates

A re-run is any tool call in the NEXT assistant turn that is: the same command again; the same
shell tool with a shared path-like token and a re-fetch head (sed/head/tail/cat/grep/rg/git/…)
or the same head; a Read of a file named in the elided command; or pause_token_saver. Codex
code-mode exec is unwrapped to the shell command inside the JS before comparing. Heuristic —
report both the count and the break-even rate, and read the examples.

Cost-eq tokens: anthropic in*1 + cacheW*1.25 + cacheR*0.1 + out*5; openai (in-cached)*1 +
cached*0.1 + out*8. Writes results/<name>/rerun_economics_<provider>_<utc>.md. Read-only.
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

DEFAULT_DB = os.path.join(lib.VIBE_DIR, "mining_timeline.db")

SHELL_TOOLS = {"Bash", "PowerShell", "bash", "shell_command", "exec_command", "exec", "run_terminal_command"}
READ_TOOLS = {"Read", "read", "read_file"}
REFETCH_HEADS = {"sed", "head", "tail", "cat", "grep", "rg", "awk", "git", "get-content", "gc", "type",
                 "select-string", "sls", "python", "wc", "nl", "bat", "diff"}
NOISE = {"/dev/null", "2>&1", "2>/dev/null", "|", "||", "&&"}
RE_INNER = re.compile(r"(?:command|cmd)\s*:\s*[\"'`]([^\"'`]{3,})")


def inner_command(tool, cmd):
    """Codex code-mode exec wraps a shell command in JS inside JSON; pull the literal out."""
    if not cmd:
        return ""
    if tool == "exec" or cmd.startswith("{\"input\""):
        flat = cmd.replace("\\\\", "\\").replace("\\\"", "\"").replace("\\n", " ")
        m = RE_INNER.search(flat)
        return m.group(1) if m else flat
    return cmd


def path_tokens(s):
    out = set()
    for t in re.split(r"[\s\"'`|;&<>(),]+", s or ""):
        t = t.strip("\\")
        if not t or t in NOISE or len(t) < 3:
            continue
        if "/" in t or "\\" in t or re.search(r"\w\.[A-Za-z0-9]{1,5}$", t):
            base = re.split(r"[/\\]", t)[-1]
            if base and base not in ("null", "dev"):
                out.add(base.lower())
    return out


def head_word(cmd):
    c = re.sub(r"^(cd\s+\S+\s*(&&|;)\s*)+", "", (cmd or "").strip())
    c = re.sub(r"^(\$?\w+=\S+\s+)+", "", c)
    words = c.split()
    return words[0].rsplit("/", 1)[-1].rsplit("\\", 1)[-1].lower() if words else ""


def classify(p, r):
    """p = the elided item, r = a tool call in the next assistant turn. Kind or None."""
    if "pause_token_saver" in r["tool"]:
        return "pause"
    p_cmd = inner_command(p["tool"], p.get("cmd"))
    r_cmd = inner_command(r["tool"], r.get("cmd"))
    if r["tool"] == p["tool"] and (r.get("cmd") or "") == (p.get("cmd") or ""):
        return "exact-rerun"
    p_paths = path_tokens(p_cmd)
    if r["tool"] in READ_TOOLS and r.get("path"):
        return "read-of-same-file" if os.path.basename(r["path"]).lower() in p_paths else None
    if r["tool"] in SHELL_TOOLS and p["tool"] in SHELL_TOOLS:
        shared = p_paths & path_tokens(r_cmd)
        if shared and (head_word(r_cmd) in REFETCH_HEADS or head_word(r_cmd) == head_word(p_cmd)):
            return "narrowed-rerun"
    return None


def family(tool, cmd):
    c = re.sub(r"^(cd\s+\S+\s*(&&|;)\s*)+", "", inner_command(tool, cmd) or "")
    if re.match(r"(git\s+(-C\s+\S+\s+)?(--no-pager\s+)?(diff|show|log)\b)", c):
        return "git diff/show/log"
    if re.search(r"\b(cat|sed|head|tail|Get-Content|gc|type|nl|bat)\b", c, re.I) or re.search(r"\b(rg|grep)\b.*['\"]\^['\"]", c):
        return "file dump"
    if re.match(r"(rg|grep|egrep|ag|Select-String)\b", c, re.I):
        return "grep"
    if re.match(r"(dotnet|npm|npx|pytest|cargo|go|node|python)\b", c):
        return "build/test/run: " + c.split()[0]
    return "other: " + (c.split()[0][:14] if c.split() else "?")


def turn_cost(prov, i, cc, cr, o):
    i, cc, cr, o = (x or 0 for x in (i, cc, cr, o))
    if prov == "anthropic":
        return i + 1.25 * cc + 0.1 * cr + 5 * o
    return (i - cr) + 0.1 * cr + 8 * o


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--name", required=True)
    ap.add_argument("--provider", default="anthropic")
    ap.add_argument("--db", default=DEFAULT_DB)
    ap.add_argument("--chars-per-token", type=float, default=3.6)
    args = ap.parse_args()
    prov, cpt = args.provider, args.chars_per_token
    con = lib.open_ro(args.db)

    conv_tc = {}
    for ck, n, i, cc, cr, o in con.execute(
            """SELECT ConvKey, COUNT(*), AVG(InputTokens), AVG(CacheCreationTokens), AVG(CacheReadTokens), AVG(OutputTokens)
               FROM Requests WHERE Provider=? AND StatusCode=200 GROUP BY ConvKey""", (prov,)):
        conv_tc[ck] = turn_cost(prov, i, cc, cr, o)

    fam = collections.defaultdict(collections.Counter)
    kinds = collections.Counter()
    examples = []
    for ck, nreq, tline in con.execute("SELECT ConvKey, NumRequests, Timeline FROM Conversations WHERE Provider=?", (prov,)):
        items = json.loads(tline)
        tc = conv_tc.get(ck, 0)
        reactions = collections.defaultdict(list)
        for it in items:
            for pi in it.get("react_to") or []:
                reactions[pi].append(it)
        for it in items:
            if not it.get("el"):
                continue
            f = fam[family(it["tool"], it.get("cmd"))]
            saved = it["raw"] - it["aft"]
            remaining = max(1.0, nreq * (1 - it["i"] / max(1, len(items))))
            f["elisions"] += 1
            f["saved_unique"] += saved
            f["saved_wire"] += saved * remaining
            f["saved_costeq"] += (saved / cpt) * (1.25 + 0.1 * (remaining - 1))
            rerun, kind = None, None
            for r in reactions.get(it["i"], []):
                kind = classify(it, r)
                if kind:
                    rerun = r
                    break
            kinds[kind or "none"] += 1
            if rerun is not None:
                refetch = rerun.get("raw", 0)
                f["reruns"] += 1
                f["refetch"] += refetch
                f["rerun_costeq"] += tc + (refetch / cpt) * (1.25 + 0.1 * (remaining - 1))
                if len(examples) < 8:
                    examples.append((inner_command(it["tool"], it.get("cmd"))[:90], it["raw"], it["aft"], it.get("eln"), kind,
                                     inner_command(rerun["tool"], rerun.get("cmd"))[:90], rerun.get("raw"),
                                     (rerun.get("note") or "")[:140].replace("\n", " ")))
    con.close()

    L = []
    w = L.append
    ts = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%d_%H%M%S")
    w("# Re-run economics — %s — %s UTC (by `%s`), %.1f chars/token" % (prov, ts, args.name, cpt))
    w("")
    w("reaction kinds: %s" % dict(kinds))
    w("")
    w("| family | elisions | re-runs | saved unique K | saved wire MB | saved cost-eq K | re-run cost-eq K | net K |")
    w("|---|---|---|---|---|---|---|---|")
    tot = collections.Counter()
    for k, f in sorted(fam.items(), key=lambda kv: -kv[1]["saved_wire"]):
        for kk, v in f.items():
            tot[kk] += v
        w("| %s | %d | %d | %.0f | %.1f | %.0f | %.0f | **%.0f** |" % (
            k, f["elisions"], f["reruns"], f["saved_unique"] / 1e3, f["saved_wire"] / 1e6, f["saved_costeq"] / 1e3,
            f["rerun_costeq"] / 1e3, (f["saved_costeq"] - f["rerun_costeq"]) / 1e3))
    f = tot
    w("| **TOTAL** | %d | %d | %.0f | %.1f | %.0f | %.0f | **%.0f** |" % (
        f["elisions"], f["reruns"], f["saved_unique"] / 1e3, f["saved_wire"] / 1e6, f["saved_costeq"] / 1e3,
        f["rerun_costeq"] / 1e3, (f["saved_costeq"] - f["rerun_costeq"]) / 1e3))
    if f["elisions"] and f["reruns"]:
        per_rerun = f["rerun_costeq"] / f["reruns"]
        w("")
        w("break-even re-run rate: %.0f%% (observed %.0f%%)" % (100 * f["saved_costeq"] / per_rerun / f["elisions"], 100 * f["reruns"] / f["elisions"]))
    w("")
    w("## examples (elided -> reaction; excerpts capped)")
    w("")
    for e in examples:
        w("- `%s` raw=%d after=%d elided=%s -> **%s** `%s` raw=%s | note: %s" % e)
    outdir = os.path.join(os.path.dirname(os.path.abspath(__file__)), "results", args.name)
    os.makedirs(outdir, exist_ok=True)
    path = os.path.join(outdir, "rerun_economics_%s_%s.md" % (prov, ts))
    with open(path, "w", encoding="utf-8") as fh:
        fh.write("\n".join(L) + "\n")
    print("\n".join(L))
    print("\n[rerun_economics] report: %s" % path)
    return 0


if __name__ == "__main__":
    sys.exit(main())
