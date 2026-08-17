"""experiment.py — test a candidate transform against every captured tool output.

Usage:
    python experiment.py candidates/<file>.py --name <agent-name>
        [--on after|raw] [--provider P] [--tool T] [--allowlisted-only]
        [--min-chars N] [--limit N] [--top N] [--corpus PATH]

The candidate is a plain Python file exporting:
    def transform(text: str, tool: str, command: str|None, provider: str) -> str
    META = {"description": "..."}            # optional
    def applies(tool, command, provider): ... # optional pre-filter, default all

For every corpus output the harness runs the transform and scores it the way
runbook §5 demands:
  * savings   — chars saved, unique AND resend-weighted; wire-level acceptance
                mirrors the C# guard (JSON-escaped length must strictly shrink)
  * loss      — content chars / content lines removed vs whitespace, marker lines
                added; auto-classifies each fired output Lossless (subsequence) /
                Reshaping (same lines, new order) / Lossy
  * invariants— idempotent (f(f(x))==f(x)), never-grows, deterministic (sampled),
                exceptions; violations come back with samples
  * judgment  — top wins and worst content losses as capped before/after excerpts

--on after (default) tests ON TOP of what the shipped pipeline already sends
upstream (incremental savings); --on raw tests against the raw tool output
(for candidates that would replace or precede existing stages).

Results go to results/<name>/<candidate>_<utc>.md (+ .json). Python verdicts are
prototypes — port survivors to C# and gate on the real pipeline (runbook §7).

CONCURRENCY (multi-agent safe): opens the corpus READ-ONLY and writes only under
results/<name>/ — any number of agents can run experiments at once. --name is
REQUIRED so concurrent runs never collide.
CHANGE PROTOCOL: shared infrastructure — changes go in a NEW file, or ask Rob whether
other agents are running before editing in place (runbooks/token_saver/mining_runbook.md §6).
"""

from __future__ import annotations

import argparse
import collections
import datetime
import hashlib
import importlib.util
import json
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import mininglib as lib

EXCERPT_CAP = 500          # runbook §3: excerpts in reports are hard-capped
DETERMINISM_SAMPLE = 500   # double-call determinism check on the first N fired
MAX_VIOLATION_SAMPLES = 3


def nonws_chars(s: str) -> int:
    return sum(map(len, s.split()))


def load_candidate(path: str):
    spec = importlib.util.spec_from_file_location("candidate", path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    if not callable(getattr(mod, "transform", None)):
        raise SystemExit("candidate %s does not define transform(text, tool, command, provider)" % path)
    return mod


def fmt_mb(chars: int) -> str:
    return "%.2f MB" % (chars / 1e6) if chars >= 1e5 else "%d chars" % chars


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("candidate", help="path to candidate .py file")
    ap.add_argument("--name", required=True, help="agent/run name; results go to results/<name>/")
    ap.add_argument("--on", choices=("after", "raw"), default="after")
    ap.add_argument("--provider", default=None)
    ap.add_argument("--tool", default=None)
    ap.add_argument("--allowlisted-only", action="store_true")
    ap.add_argument("--min-chars", type=int, default=1)
    ap.add_argument("--limit", type=int, default=0, help="stop after N corpus outputs (0 = all)")
    ap.add_argument("--top", type=int, default=10, help="samples per wins/losses list")
    ap.add_argument("--corpus", default=lib.DEFAULT_CORPUS_DB)
    args = ap.parse_args()

    mod = load_candidate(args.candidate)
    meta = getattr(mod, "META", {}) or {}
    applies = getattr(mod, "applies", None)
    cand_sha = hashlib.sha256(open(args.candidate, "rb").read()).hexdigest()[:16]
    stem = os.path.splitext(os.path.basename(args.candidate))[0]

    con = lib.open_ro(args.corpus)
    corpus_meta = dict(con.execute("SELECT Key, Value FROM Meta"))
    where, params = [], []
    if args.provider:
        where.append("Provider = ?"); params.append(args.provider)
    if args.tool:
        where.append("Tool = ?"); params.append(args.tool)
    if args.allowlisted_only:
        where.append("Allowlisted = 1")
    if args.min_chars > 1:
        where.append("RawChars >= ?"); params.append(args.min_chars)
    sql = ("SELECT Provider, Tool, Command, Raw, After, Resends FROM Outputs"
           + ((" WHERE " + " AND ".join(where)) if where else ""))
    if args.limit:
        sql += " LIMIT %d" % args.limit

    started = time.time()
    n = fired = exceptions = grew = wire_rejected = nondet = nonidem = 0
    saved_unique = saved_wire = wire_saved_unique = 0
    content_removed = ws_removed = content_lines_removed = marker_lines_added = 0
    classification = collections.Counter()
    by_tool = collections.defaultdict(lambda: [0, 0, 0])  # fired, saved_unique, saved_wire
    wins, losses = [], []          # (metric, provider, tool, command, before, after)
    violations = collections.defaultdict(list)

    for provider, tool, command, raw, after, resends in con.execute(sql, params):
        text = raw if args.on == "raw" else (after if after is not None else raw)
        if not text:
            continue
        n += 1
        if applies and not applies(tool, command, provider):
            continue
        try:
            out = mod.transform(text, tool, command, provider)
        except Exception as ex:  # candidate bug: count it, treat as fail-open
            exceptions += 1
            if len(violations["exception"]) < MAX_VIOLATION_SAMPLES:
                violations["exception"].append((repr(ex), provider, tool, text[:EXCERPT_CAP], ""))
            continue
        if not isinstance(out, str):
            exceptions += 1
            if len(violations["exception"]) < MAX_VIOLATION_SAMPLES:
                violations["exception"].append(("returned %s" % type(out).__name__, provider, tool,
                                                text[:EXCERPT_CAP], ""))
            continue
        if out == text:
            continue
        fired += 1
        # --- invariants ---------------------------------------------------------
        if len(out) > len(text):
            grew += 1
            if len(violations["grew"]) < MAX_VIOLATION_SAMPLES:
                violations["grew"].append(("+%d chars" % (len(out) - len(text)), provider, tool)
                                          + lib.excerpt_diff(text, out, EXCERPT_CAP))
        try:
            twice = mod.transform(out, tool, command, provider)
        except Exception:
            twice = None
        if twice != out:
            nonidem += 1
            if len(violations["non-idempotent"]) < MAX_VIOLATION_SAMPLES:
                violations["non-idempotent"].append(("f(f(x)) != f(x)", provider, tool)
                                                    + lib.excerpt_diff(out, twice or "<exception>", EXCERPT_CAP))
        if fired <= DETERMINISM_SAMPLE and mod.transform(text, tool, command, provider) != out:
            nondet += 1
            if len(violations["non-deterministic"]) < MAX_VIOLATION_SAMPLES:
                violations["non-deterministic"].append(("two calls differ", provider, tool,
                                                        text[:EXCERPT_CAP], ""))
        # --- savings (wire guard mirrors the C# acceptance rule) ----------------
        accepted = lib.wire_len(out) < lib.wire_len(text)
        if not accepted:
            wire_rejected += 1
        else:
            d = len(text) - len(out)
            saved_unique += d
            saved_wire += d * resends
            wire_saved_unique += lib.wire_len(text) - lib.wire_len(out)
            t = by_tool[(provider, tool)]
            t[0] += 1; t[1] += d; t[2] += d * resends
            wins.append((d * resends, provider, tool, command, text, out))
            wins.sort(key=lambda w: -w[0]); del wins[args.top:]
        # --- loss accounting ----------------------------------------------------
        if lib.is_subsequence(out, text):
            classification["lossless"] += 1
        else:
            lines_in, lines_out = collections.Counter(text.splitlines()), collections.Counter(out.splitlines())
            if lines_in == lines_out:
                classification["reshaping"] += 1
            else:
                classification["lossy"] += 1
                removed = lines_in - lines_out
                added = lines_out - lines_in
                c_lines = sum(cnt for line, cnt in removed.items() if line.strip())
                content_lines_removed += c_lines
                marker_lines_added += sum(added.values())
        c_rm = max(0, nonws_chars(text) - nonws_chars(out))
        content_removed += c_rm
        ws_removed += max(0, (len(text) - len(out)) - c_rm)
        if c_rm > 0:
            losses.append((c_rm, provider, tool, command, text, out))
            losses.sort(key=lambda w: -w[0]); del losses[args.top:]
    con.close()

    # --------------------------------------------------------------------- report
    ts = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%d_%H%M%S")
    outdir = os.path.join(os.path.dirname(os.path.abspath(__file__)), "results", args.name)
    os.makedirs(outdir, exist_ok=True)
    base = os.path.join(outdir, "%s_%s" % (stem, ts))
    display_tokens = saved_wire // 4

    summary = {
        "candidate": args.candidate, "candidate_sha16": cand_sha,
        "description": meta.get("description", ""), "run_by": args.name,
        "utc": ts, "on": args.on,
        "filters": {"provider": args.provider, "tool": args.tool,
                    "allowlisted_only": args.allowlisted_only,
                    "min_chars": args.min_chars, "limit": args.limit},
        "corpus": {"path": args.corpus, "last_src_rowid": corpus_meta.get("last_src_rowid"),
                   "updated_utc": corpus_meta.get("updated_utc")},
        "considered": n, "fired": fired, "exceptions": exceptions,
        "saved_chars_unique": saved_unique, "saved_chars_wire_weighted": saved_wire,
        "saved_wire_bytes_unique": wire_saved_unique,
        "display_tokens_wire_weighted": display_tokens,
        "wire_guard_rejected": wire_rejected,
        "violations": {"grew": grew, "non_idempotent": nonidem, "non_deterministic": nondet},
        "classification": dict(classification),
        "loss": {"content_chars_removed": content_removed, "whitespace_chars_removed": ws_removed,
                 "content_lines_removed": content_lines_removed,
                 "marker_lines_added": marker_lines_added},
        "elapsed_s": round(time.time() - started, 1),
    }
    with open(base + ".json", "w", encoding="utf-8") as f:
        json.dump(summary, f, indent=2)

    ok = grew == 0 and nonidem == 0 and nondet == 0 and exceptions == 0
    lines = []
    w = lines.append
    w("# Experiment: %s" % stem)
    w("")
    w("%s" % (meta.get("description") or "(no META description)"))
    w("")
    w("| | |")
    w("|---|---|")
    w("| Run by / when | `%s` / %s UTC |" % (args.name, ts))
    w("| Candidate | `%s` (sha16 `%s`) |" % (args.candidate, cand_sha))
    w("| Base text | `--on %s`%s |" % (args.on, "" if not where else " with filters `%s`" % " AND ".join(where)))
    w("| Corpus | src rowid <= %s, updated %s |" % (corpus_meta.get("last_src_rowid"), corpus_meta.get("updated_utc")))
    w("| Considered / fired | %d / %d (%.1f%%) |" % (n, fired, 100.0 * fired / n if n else 0))
    w("| **Saved (unique)** | **%s** |" % fmt_mb(saved_unique))
    w("| **Saved (wire-weighted)** | **%s** (~%s display tokens) |" % (fmt_mb(saved_wire), format(display_tokens, ",")))
    w("| Wire-guard rejected | %d fired outputs would re-serialize >= original |" % wire_rejected)
    w("| Invariants | %s |" % ("ALL PASS" if ok else
                               "VIOLATIONS: grew=%d non-idempotent=%d non-deterministic=%d exceptions=%d"
                               % (grew, nonidem, nondet, exceptions)))
    w("| Classification (fired) | %s |" % (", ".join("%s=%d" % kv for kv in classification.most_common()) or "-"))
    w("| Loss | content chars %s, ws chars %s, content lines %d, marker lines +%d |"
      % (fmt_mb(content_removed), fmt_mb(ws_removed), content_lines_removed, marker_lines_added))
    w("")
    if by_tool:
        w("## By tool (accepted savings)")
        w("")
        w("| provider | tool | fired | unique | wire-weighted |")
        w("|---|---|---|---|---|")
        for (prov, tool), (f_, su, sw) in sorted(by_tool.items(), key=lambda kv: -kv[1][2])[:15]:
            w("| %s | %s | %d | %s | %s |" % (prov, tool, f_, fmt_mb(su), fmt_mb(sw)))
        w("")
    for title, entries in (("Top wins (by wire-weighted saving)", wins),
                           ("Worst content losses (judge these!)", losses)):
        if not entries:
            continue
        w("## %s" % title)
        w("")
        for metric, prov, tool, command, before, after in entries:
            b, a = lib.excerpt_diff(before, after, EXCERPT_CAP)
            w("**%s** | %s/%s | cmd: `%s`" % (format(metric, ","), prov, tool, (command or "-")[:120]))
            w("")
            w("```"); w("BEFORE| " + b.replace("\n", "\nBEFORE| ")); w("```")
            w("```"); w("AFTER | " + a.replace("\n", "\nAFTER | ")); w("```")
            w("")
    if violations:
        w("## Violation samples")
        w("")
        for kind, samples in violations.items():
            for s in samples:
                w("- **%s** (%s, %s/%s)" % (kind, s[0], s[1], s[2]))
                w("")
                w("```"); w(str(s[3])[:EXCERPT_CAP]); w("```")
                if s[4]:
                    w("```"); w(str(s[4])[:EXCERPT_CAP]); w("```")
                w("")
    with open(base + ".md", "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")

    print("[experiment] %s: considered %d, fired %d, saved %s unique / %s wire (~%s tokens), "
          "invariants %s, lossy on %d. Report: %s"
          % (stem, n, fired, fmt_mb(saved_unique), fmt_mb(saved_wire), format(display_tokens, ","),
             "OK" if ok else "VIOLATED", classification.get("lossy", 0), base + ".md"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
