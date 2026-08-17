"""Shared library for the TokenSaver mining scripts.

CONCURRENCY (multi-agent safe):
  - Several agents may run experiment.py / corpus_stats.py at the same time: the corpus
    is opened READ-ONLY here, and every run writes only to its own results/<name>/ dir.
  - build_corpus.py serializes itself with a lockfile (one builder at a time); it is the
    only writer to the corpus DB.
CHANGE PROTOCOL: this file is shared infrastructure. Do not edit it while other agents
  may be running — put changes in a NEW file (mininglib_v2.py, your own helper module),
  or confirm with Rob that no other agents are active before editing in place.
See runbooks/token_saver/mining_runbook.md §6.
"""

from __future__ import annotations

import hashlib
import json
import os
import sqlite3

VIBE_DIR = os.path.expanduser("~/.vibe_rails")
DEFAULT_SRC_DB = os.path.join(VIBE_DIR, "proxy_exchanges.db")
DEFAULT_CORPUS_DB = os.path.join(VIBE_DIR, "mining_corpus.db")
BUILD_LOCK = os.path.join(VIBE_DIR, "mining_corpus.build.lock")

# Python mirror of CompressionCatalog's default scope allowlists (approximate; C# is truth).
ALLOWLISTS = {
    "anthropic": {"Bash", "PowerShell", "BashOutput"},
    "openai": {"shell_command", "exec_command", "exec", "write_stdin", "wait"},
    "zai": {"bash"},
    "xai": {"bash"},
}

CTL_CHARS = ("\x1b", "\x07", "\r")  # ESC, BEL, CR — the whole-string fail-open trio

_METACHARS = set("|<>&;`$()\n\r")

_TEST_FIRST = {"pytest", "jest", "vitest", "mocha"}
_TEST_SECOND = {"dotnet", "go", "cargo", "playwright"}  # + second token "test"
_PKG = {"npm", "pnpm", "yarn", "bun"}


def _unquote(tok: str) -> str:
    for q in ('"', "'"):
        if len(tok) >= 2 and tok.startswith(q) and tok.endswith(q):
            return tok[1:-1]
    return tok


def classify_command(command):
    """Rough Python mirror of CommandShape.Classify.

    Returns (shape, decline_reason). shape in {git-status, git-log, git-diff, dir-list,
    grep, find, test, none}; decline_reason in {no-command, metachar,
    unrecognized-command, None}. The C# classifier is ground truth (it also allowlists
    flags, which this mirror does NOT); treat these labels as bucketing, not verdicts.
    """
    if not command:
        return "none", "no-command"
    if any(c in _METACHARS for c in command):
        return "none", "metachar"
    toks = [_unquote(t) for t in command.split() if t]
    if not toks:
        return "none", "no-command"
    t0 = toks[0]
    if t0 == "git":
        sub = toks[1] if len(toks) > 1 else ""
        if sub == "status":
            return "git-status", None
        if sub == "log":
            return "git-log", None
        if sub in ("diff", "show"):
            return "git-diff", None
        return "none", "unrecognized-command"
    if t0 in ("ls", "dir", "tree"):
        return "dir-list", None
    if t0 in ("grep", "rg", "ripgrep", "ag"):
        return "grep", None
    if t0 == "find":
        return "find", None
    if t0 in _TEST_FIRST:
        return "test", None
    if t0 in _TEST_SECOND and len(toks) > 1 and toks[1] == "test":
        return "test", None
    if t0 in _PKG and len(toks) > 1 and (toks[1] in ("test", "t") or toks[1].startswith("test:")
                                         or (toks[1] in ("run", "run-script") and len(toks) > 2
                                             and (toks[2] in ("test", "t") or toks[2].startswith("test:")))):
        return "test", None
    if t0 in ("npx", "bunx") and len(toks) > 1 and (toks[1] in ("jest", "vitest", "mocha")
                                                    or (toks[1] == "playwright" and len(toks) > 2 and toks[2] == "test")):
        return "test", None
    if t0 in ("python", "python3") and "-m" in toks and "pytest" in toks:
        return "test", None
    if t0 == "uv" and len(toks) > 2 and toks[1] == "run" and toks[2] == "pytest":
        return "test", None
    return "none", "unrecognized-command"


# ---------------------------------------------------------------------------- sqlite

def open_ro(path: str) -> sqlite3.Connection:
    """Read-only URI open + busy_timeout. The only way to touch a live DB (runbook §3)."""
    con = sqlite3.connect("file:%s?mode=ro" % path.replace("\\", "/"), uri=True, timeout=10)
    con.execute("PRAGMA busy_timeout = 5000")
    return con


def open_corpus_rw(path: str) -> sqlite3.Connection:
    con = sqlite3.connect(path, timeout=30)
    con.execute("PRAGMA journal_mode = WAL")
    con.execute("PRAGMA busy_timeout = 10000")
    con.execute("PRAGMA synchronous = NORMAL")
    return con


CORPUS_SCHEMA = """
CREATE TABLE IF NOT EXISTS Meta(Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS Outputs(
    Hash TEXT PRIMARY KEY,
    Provider TEXT NOT NULL,
    Tool TEXT NOT NULL,
    Command TEXT,
    Raw TEXT NOT NULL,
    After TEXT,                -- latest paired post-pipeline string; NULL = identical to Raw
    RawChars INTEGER NOT NULL,
    AfterChars INTEGER,
    Resends INTEGER NOT NULL DEFAULT 1,
    FirstSeenUTC TEXT NOT NULL,
    LastSeenUTC TEXT NOT NULL,
    FirstExchangeId TEXT NOT NULL,
    Allowlisted INTEGER NOT NULL,
    CmdShape TEXT NOT NULL,
    DeclineReason TEXT,
    HasCtl INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_Outputs_ProvTool ON Outputs(Provider, Tool);
CREATE TABLE IF NOT EXISTS Exchanges(
    SrcRowid INTEGER PRIMARY KEY,
    Id TEXT NOT NULL,
    CreatedUTC TEXT NOT NULL,
    Provider TEXT NOT NULL,
    Path TEXT NOT NULL,
    StatusCode INTEGER NOT NULL,
    ReqChars INTEGER NOT NULL,
    ReqCharsAfter INTEGER NOT NULL,
    Passthrough INTEGER NOT NULL,
    Degenerate INTEGER NOT NULL,
    Parsed INTEGER NOT NULL,
    NumOutputs INTEGER NOT NULL
);
"""


def output_hash(provider: str, tool: str, command, raw: str) -> str:
    h = hashlib.sha256()
    for part in (provider, tool, command or "", raw):
        b = part.encode("utf-8", "surrogatepass")
        h.update(str(len(b)).encode())
        h.update(b":")
        h.update(b)
    return h.hexdigest()


# ------------------------------------------------------------------- body extractors
# Each returns an ORDERED list of (tool, command, text). The shipped rewriters replace
# string values in place without changing structure, so the k-th extracted string of
# RequestBefore pairs with the k-th of RequestAfter.

def _texts_from_content(content, text_key="text", type_name="text"):
    out = []
    if isinstance(content, str):
        out.append(content)
    elif isinstance(content, list):
        for part in content:
            if isinstance(part, dict) and part.get("type") == type_name:
                t = part.get(text_key)
                if isinstance(t, str):
                    out.append(t)
    return out


def extract_anthropic(body: dict):
    tool_uses, results = {}, []
    for msg in body.get("messages") or []:
        content = msg.get("content") if isinstance(msg, dict) else None
        if not isinstance(content, list):
            continue
        for block in content:
            if not isinstance(block, dict):
                continue
            btype = block.get("type")
            if btype == "tool_use":
                name = block.get("name")
                inp = block.get("input")
                cmd = inp.get("command") if isinstance(inp, dict) and isinstance(inp.get("command"), str) else None
                if isinstance(block.get("id"), str) and isinstance(name, str):
                    tool_uses[block["id"]] = (name, cmd)
            elif btype == "tool_result":
                tid = block.get("tool_use_id")
                for text in _texts_from_content(block.get("content")):
                    results.append((tid, text))
    out = []
    for tid, text in results:
        name, cmd = tool_uses.get(tid, ("(unknown)", None))
        out.append((name, cmd, text))
    return out


def _codex_command(item: dict):
    if item.get("type") == "custom_tool_call":
        inp = item.get("input")
        return inp if isinstance(inp, str) else None
    args = item.get("arguments")
    if isinstance(args, str):
        try:
            parsed = json.loads(args)
            if isinstance(parsed, dict) and isinstance(parsed.get("command"), str):
                return parsed["command"]
        except json.JSONDecodeError:
            pass
    return None


def extract_codex(body: dict):
    calls, out = {}, []
    items = body.get("input") or []
    if not isinstance(items, list):
        return out
    for item in items:
        if not isinstance(item, dict):
            continue
        itype = item.get("type")
        if itype in ("function_call", "custom_tool_call"):
            cid, name = item.get("call_id"), item.get("name")
            if isinstance(cid, str) and isinstance(name, str):
                calls[cid] = (name, _codex_command(item))
        elif itype in ("function_call_output", "custom_tool_call_output"):
            name, cmd = calls.get(item.get("call_id"), (None, None))
            if name is None:
                continue  # uncorrelated: rewriter neither compresses nor captures these
            for text in _texts_from_content(item.get("output"), type_name="input_text"):
                out.append((name, cmd, text))
        elif itype == "local_shell_call_output":
            o = item.get("output")
            if isinstance(o, str):
                out.append(("shell_command", None, o))
        elif itype == "shell_call_output":
            o = item.get("output")
            if isinstance(o, list):
                for entry in o:
                    if isinstance(entry, dict):
                        for key in ("stdout", "stderr"):
                            v = entry.get(key)
                            if isinstance(v, str):
                                out.append(("shell_command", None, v))
    return out


def extract_chat(body: dict):
    calls, out = {}, []
    for msg in body.get("messages") or []:
        if not isinstance(msg, dict):
            continue
        role = msg.get("role")
        if role == "assistant":
            for tc in msg.get("tool_calls") or []:
                if not isinstance(tc, dict):
                    continue
                fn = tc.get("function")
                if not (isinstance(fn, dict) and isinstance(tc.get("id"), str)):
                    continue
                name, cmd = fn.get("name"), None
                args = fn.get("arguments")
                if isinstance(args, str):
                    try:
                        parsed = json.loads(args)
                        c = parsed.get("command") if isinstance(parsed, dict) else None
                        if isinstance(c, str):
                            cmd = c
                        elif isinstance(c, list) and all(isinstance(x, str) for x in c):
                            cmd = " ".join(c)
                    except json.JSONDecodeError:
                        pass
                if isinstance(name, str):
                    calls[tc["id"]] = (name, cmd)
        elif role == "tool":
            name, cmd = calls.get(msg.get("tool_call_id"), (None, None))
            if name is None:
                continue
            for text in _texts_from_content(msg.get("content")):
                out.append((name, cmd, text))
    return out


def extract_outputs(path: str, body_text: str):
    """(records, parsed_ok). Empty records + True = parsed fine, nothing extractable."""
    if path.endswith("/v1/messages"):
        fn = extract_anthropic
    elif path.endswith("/responses"):
        fn = extract_codex
    elif path.endswith("/chat/completions"):
        fn = extract_chat
    else:
        return [], True
    try:
        body = json.loads(body_text)
        if not isinstance(body, dict):
            return [], False
        return fn(body), True
    except (json.JSONDecodeError, RecursionError):
        return [], False


# ------------------------------------------------------------------------ text utils

def is_subsequence(needle: str, hay: str) -> bool:
    it = iter(hay)
    return all(c in it for c in needle)


def wire_len(s: str) -> int:
    """Approximate JSON wire length of a string token (C# Utf8JsonWriter is truth)."""
    return len(json.dumps(s, ensure_ascii=False).encode("utf-8", "surrogatepass"))


def excerpt_diff(before: str, after: str, cap: int = 500):
    """(before_snip, after_snip) centered on the first difference, each capped."""
    n = min(len(before), len(after))
    i = 0
    while i < n and before[i] == after[i]:
        i += 1
    start = max(0, i - cap // 4)
    b = before[start:start + cap]
    a = after[start:start + cap]
    pre = "..." if start > 0 else ""
    return (pre + b + ("..." if start + cap < len(before) else ""),
            pre + a + ("..." if start + cap < len(after) else ""))
