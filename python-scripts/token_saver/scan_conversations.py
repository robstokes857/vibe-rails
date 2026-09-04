"""scan_conversations.py — the SEQUENCE dataset beside the corpus (2026-09-03).

Usage:
    python scan_conversations.py --name <agent-name> [--provider P] [--limit N]
                                 [--db PATH] [--out PATH] [--rescan]

build_corpus.py dedupes tool outputs by content hash, which is right for sizing a
transform but destroys the two things the corpus cannot answer:

  1. RE-RUNS (Rob's note at the top of TokenSaver/README.md): after the saver elided
     or deduped a tool result, did the model call the tool AGAIN to get the missing
     part? That needs the ordered tool timeline of a conversation, with each result's
     before/after markers and the assistant text that followed it.
  2. COMPOSITION: what fraction of a request body is even tool output? System prompt,
     tool definitions, thinking blocks, user text, tool_use inputs — none of which the
     saver may touch — bound what any stage can ever save. Real token usage (from the
     response's usage block) turns wire chars into what was actually billed, cache
     reads included.

One streaming pass over ProxyExchanges (READ-ONLY, WAL-safe while VibeRails runs;
never SELECTs body columns without the rowid cursor), writing
~/.vibe_rails/mining_timeline.db:

  Requests       one row per rewrite-endpoint request: composition counters (chars),
                 saver markers found in RequestAfter, and usage tokens parsed from
                 ResponseBody (first message_start / last response.completed).
  Conversations  one row per conversation key (hash of the first user message):
                 the ordered tool timeline of the request that carried the MOST tool
                 calls (the CLI resends history every turn, so the longest request is
                 the whole conversation up to compaction). Timeline items carry tool,
                 command/input summary (<=400 chars), raw/after chars, marker counts,
                 the assistant turn number, a <=300-char snippet of the assistant text that
                 preceded the call, and react_to = indices of the elided/deduped results of
                 the previous assistant turn (what rerun_economics.py classifies).

Incremental via Meta.last_rowid (per --provider filter); --rescan starts over.
Stdlib only; never shipped. Data never leaves the machine (runbook §3).

CONCURRENCY: read-only on the source; the only writer to mining_timeline.db, held
behind an OS lock (mining_timeline.build.lock). Safe beside build_corpus.py.
"""

from __future__ import annotations

import argparse
import datetime
import hashlib
import json
import os
import re
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import mininglib as lib

OUT_DB = os.path.join(lib.VIBE_DIR, "mining_timeline.db")
LOCK = os.path.join(lib.VIBE_DIR, "mining_timeline.build.lock")
SUFFIXES = ("/v1/messages", "/responses", "/chat/completions")

# Tool allowlists as the CATALOG has them today (mininglib.ALLOWLISTS predates native Grok).
ALLOW = {
    "anthropic": {"Bash", "PowerShell", "BashOutput"},
    "openai": {"shell_command", "exec_command", "exec", "write_stdin", "wait"},
    "cli-chat": {"shell_command", "exec_command", "exec", "write_stdin", "wait"},
    "zai": {"bash"},
    "xai": {"bash", "run_terminal_command", "get_command_or_subagent_output"},
}

RE_ELIDED = re.compile(r"\[\.\.\. (\d+) lines elided \.\.\.\]")
RE_DEDUPE = re.compile(r" \[x(\d+)\]$", re.M)
RE_PASSED = re.compile(r"\[\.\.\. (\d+) passed \.\.\.\]")
RE_CLI_TRUNC = re.compile(r"\.\.\. \[(\d+) characters truncated\] \.\.\.")

# usage extraction straight off the SSE/JSON response text — no full parse needed.
RE_IN = re.compile(r'"input_tokens":\s*(\d+)')
RE_CC = re.compile(r'"cache_creation_input_tokens":\s*(\d+)')
RE_CR = re.compile(r'"cache_read_input_tokens":\s*(\d+)')
RE_OUT = re.compile(r'"output_tokens":\s*(\d+)')
RE_CACHED = re.compile(r'"cached_tokens":\s*(\d+)')
RE_REASON = re.compile(r'"reasoning_tokens":\s*(\d+)')

SCHEMA = """
CREATE TABLE IF NOT EXISTS Meta(Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS Requests(
    SrcRowid INTEGER PRIMARY KEY,
    Id TEXT NOT NULL, CreatedUTC TEXT NOT NULL, Provider TEXT NOT NULL, Path TEXT NOT NULL,
    StatusCode INTEGER NOT NULL, ConvKey TEXT NOT NULL, Model TEXT,
    MsgCount INTEGER NOT NULL, NumToolUses INTEGER NOT NULL, NumToolResults INTEGER NOT NULL,
    NumErrResults INTEGER NOT NULL,
    TotalChars INTEGER NOT NULL, AfterTotalChars INTEGER NOT NULL, Passthrough INTEGER NOT NULL,
    Degenerate INTEGER NOT NULL,
    SystemChars INTEGER NOT NULL, ToolsDefChars INTEGER NOT NULL, NumToolsDef INTEGER NOT NULL,
    UserTextChars INTEGER NOT NULL, SysReminderChars INTEGER NOT NULL,
    AsstTextChars INTEGER NOT NULL, ThinkingChars INTEGER NOT NULL,
    ToolUseInputChars INTEGER NOT NULL,
    ToolResultChars INTEGER NOT NULL, ToolResultAllowChars INTEGER NOT NULL,
    ToolResultAfterChars INTEGER NOT NULL, ToolResultAllowAfterChars INTEGER NOT NULL,
    ElisionMarkers INTEGER NOT NULL, ElidedLines INTEGER NOT NULL,
    DedupeMarkers INTEGER NOT NULL, PassedMarkers INTEGER NOT NULL,
    CliTruncMarkers INTEGER NOT NULL, CliTruncChars INTEGER NOT NULL,
    InputTokens INTEGER, CacheCreationTokens INTEGER, CacheReadTokens INTEGER,
    OutputTokens INTEGER, ReasoningTokens INTEGER,
    ResponseChars INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_Requests_Prov ON Requests(Provider, CreatedUTC);
CREATE INDEX IF NOT EXISTS IX_Requests_Conv ON Requests(ConvKey);
CREATE TABLE IF NOT EXISTS Conversations(
    ConvKey TEXT PRIMARY KEY, Provider TEXT NOT NULL, Model TEXT,
    FirstUTC TEXT NOT NULL, LastUTC TEXT NOT NULL, NumRequests INTEGER NOT NULL,
    MaxToolUses INTEGER NOT NULL, MaxMsgCount INTEGER NOT NULL, SrcRowidOfMax INTEGER NOT NULL,
    Timeline TEXT NOT NULL
);
"""


def utcnow() -> str:
    return datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f0Z")


# ------------------------------------------------------------------------------ lock

def try_lock(name: str):
    os.makedirs(lib.VIBE_DIR, exist_ok=True)
    handle = open(LOCK, "a+b")
    handle.seek(0, os.SEEK_END)
    if handle.tell() == 0:
        handle.write(b"\0")
        handle.flush()
    handle.seek(0)
    try:
        if os.name == "nt":
            import msvcrt
            msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
        else:
            import fcntl
            fcntl.flock(handle.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
    except OSError:
        handle.close()
        return None
    with open(LOCK + ".owner", "w", encoding="utf-8") as f:
        f.write(json.dumps({"name": name, "pid": os.getpid(), "started": utcnow()}))
    return handle


# --------------------------------------------------------------------------- helpers

def h16(s: str) -> str:
    return hashlib.sha256(s.encode("utf-8", "surrogatepass")).hexdigest()[:16]


def jlen(obj) -> int:
    try:
        return len(json.dumps(obj, ensure_ascii=False))
    except (TypeError, ValueError):
        return 0


def text_blocks(content, type_name="text", text_key="text"):
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


def markers(after_text: str):
    el = RE_ELIDED.findall(after_text)
    return (len(el), sum(int(x) for x in el),
            len(RE_DEDUPE.findall(after_text)), len(RE_PASSED.findall(after_text)))


def cli_trunc(raw: str):
    m = RE_CLI_TRUNC.findall(raw)
    return len(m), sum(int(x) for x in m)


def usage_from_response(provider: str, resp: str):
    if not resp:
        return (None, None, None, None, None)
    if provider == "anthropic":
        m_in = RE_IN.search(resp)
        m_cc = RE_CC.search(resp)
        m_cr = RE_CR.search(resp)
        outs = RE_OUT.findall(resp)
        return (int(m_in.group(1)) if m_in else None,
                int(m_cc.group(1)) if m_cc else None,
                int(m_cr.group(1)) if m_cr else None,
                int(outs[-1]) if outs else None, None)
    ins = RE_IN.findall(resp)
    cached = RE_CACHED.findall(resp)
    outs = RE_OUT.findall(resp)
    reas = RE_REASON.findall(resp)
    return (int(ins[-1]) if ins else None, None,
            int(cached[-1]) if cached else None,
            int(outs[-1]) if outs else None,
            int(reas[-1]) if reas else None)


def summarize_input(name: str, inp) -> tuple[str, str | None]:
    """(summary <=400 chars, path-ish hint) for a tool_use input."""
    if not isinstance(inp, dict):
        return (str(inp)[:400] if inp is not None else "", None)
    cmd = inp.get("command")
    if isinstance(cmd, str) and name in ("Bash", "PowerShell", "bash", "shell_command",
                                         "run_terminal_command"):
        return (cmd[:400], None)
    if isinstance(cmd, list) and all(isinstance(x, str) for x in cmd):
        return (" ".join(cmd)[:400], None)
    for key in ("cmd", "code"):
        v = inp.get(key)
        if isinstance(v, str):
            return (v[:400], None)
    path = None
    for key in ("file_path", "path", "filePath", "notebook_path"):
        v = inp.get(key)
        if isinstance(v, str):
            path = v
            break
    return (json.dumps(inp, ensure_ascii=False)[:400], path)


class Walk:
    """Accumulates one request's composition + ordered tool timeline."""

    def __init__(self, provider: str):
        self.provider = provider
        self.c = {k: 0 for k in ("system", "tools_def", "n_tools_def", "user_text", "sys_reminder",
                                 "asst_text", "thinking", "tool_use_input")}
        self.items = []          # ordered tool calls
        self.by_id = {}
        self.results = []        # ordered (item or None, text) for pairing with After
        self.msg_count = 0
        self.model = None
        self.err_results = 0
        self.pending_text = ""
        self.turn = 0            # assistant turn counter (a turn = one assistant message)
        self.last_side = None    # "asst" | "user" — turn boundaries for the Responses shape
        self.conv_key_src = None

    def assistant_side(self):
        if self.last_side != "asst":
            self.turn += 1
            self.last_side = "asst"

    def user_side(self):
        self.last_side = "user"

    def tool_use(self, tid, name, inp):
        summ, path = summarize_input(name, inp)
        self.assistant_side()
        item = {"i": len(self.items), "turn": self.turn, "tool": name, "id": (tid or "")[-8:],
                "in": jlen(inp), "cmd": summ, "raw": 0, "aft": 0, "err": 0, "nres": 0,
                "el": 0, "eln": 0, "dd": 0, "pt": 0, "ct": 0, "pre": len(self.pending_text),
                "note": self.pending_text[:300]}
        if path:
            item["path"] = path
        self.c["tool_use_input"] += item["in"]
        self.items.append(item)
        if tid:
            self.by_id[tid] = item
        self.pending_text = ""
        return item

    def tool_result(self, tid, texts, is_error):
        self.user_side()
        item = self.by_id.get(tid)
        if is_error:
            self.err_results += 1
        for t in texts:
            self.results.append((item, t))
            if item is not None:
                item["raw"] += len(t)
                item["nres"] += 1
                item["err"] = max(item["err"], 1 if is_error else 0)
                n, ch = cli_trunc(t)
                item["ct"] += ch
        if item is not None:
            item["h"] = h16("".join(texts))[:12]

    def pair_after(self, after_texts):
        """Positional pairing with RequestAfter's result strings (rewriters keep structure)."""
        if len(after_texts) != len(self.results):
            for item, t in self.results:
                if item is not None:
                    item["aft"] += len(t)
            return False
        for (item, t), a in zip(self.results, after_texts):
            if item is None:
                continue
            item["aft"] += len(a)
            if a != t:
                el, eln, dd, pt = markers(a)
                el0, eln0, dd0, pt0 = markers(t)
                item["el"] += max(0, el - el0)
                item["eln"] += max(0, eln - eln0)
                item["dd"] += max(0, dd - dd0)
                item["pt"] += max(0, pt - pt0)
        return True


def note_reactions(items):
    """Second pass over an ordered timeline: a tool call REACTS to the elided/deduped results of
    the immediately preceding assistant turn that made tool calls (turn numbers are contiguous
    only when no text-only turn intervened, which is exactly the "model saw the marker and acted"
    case we want). react_to = indices of those elided/deduped items."""
    by_turn = {}
    for it in items:
        by_turn.setdefault(it["turn"], []).append(it)
    for it in items:
        prev = by_turn.get(it["turn"] - 1)
        if not prev:
            continue
        hits = [p["i"] for p in prev if p.get("el") or p.get("dd") or p.get("pt")]
        if hits:
            it["react_to"] = hits
    return items


# ---------------------------------------------------------------- provider walkers

def walk_anthropic(body: dict, w: Walk):
    w.model = body.get("model") if isinstance(body.get("model"), str) else None
    sys_ = body.get("system")
    if isinstance(sys_, str):
        w.c["system"] = len(sys_)
    elif isinstance(sys_, list):
        w.c["system"] = sum(len(b.get("text") or "") for b in sys_ if isinstance(b, dict))
    tools = body.get("tools")
    if isinstance(tools, list):
        w.c["tools_def"] = jlen(tools)
        w.c["n_tools_def"] = len(tools)
    msgs = body.get("messages") or []
    if not isinstance(msgs, list):
        return
    w.msg_count = len(msgs)
    if msgs and isinstance(msgs[0], dict):
        w.conv_key_src = json.dumps(msgs[0], sort_keys=True, ensure_ascii=False)
    after_texts_placeholder = None  # pairing happens in the caller
    for m in msgs:
        if not isinstance(m, dict):
            continue
        role = m.get("role")
        content = m.get("content")
        if role == "assistant":
            w.assistant_side()
        else:
            w.user_side()
        if isinstance(content, str):
            if role == "user":
                w.c["user_text"] += len(content)
                if "<system-reminder>" in content:
                    w.c["sys_reminder"] += len(content)
            else:
                w.c["asst_text"] += len(content)
                w.pending_text += content
            continue
        if not isinstance(content, list):
            continue
        for b in content:
            if not isinstance(b, dict):
                continue
            t = b.get("type")
            if t == "text":
                txt = b.get("text") or ""
                if role == "user":
                    w.c["user_text"] += len(txt)
                    if "<system-reminder>" in txt:
                        w.c["sys_reminder"] += len(txt)
                else:
                    w.c["asst_text"] += len(txt)
                    w.pending_text += txt
            elif t == "thinking":
                w.c["thinking"] += len(b.get("thinking") or "") + len(b.get("signature") or "")
            elif t == "redacted_thinking":
                w.c["thinking"] += len(b.get("data") or "")
            elif t == "tool_use":
                w.tool_use(b.get("id"), b.get("name") or "?", b.get("input"))
            elif t == "tool_result":
                w.tool_result(b.get("tool_use_id"), text_blocks(b.get("content")),
                              bool(b.get("is_error")))


def anthropic_after_texts(body: dict):
    out = []
    for m in body.get("messages") or []:
        content = m.get("content") if isinstance(m, dict) else None
        if not isinstance(content, list):
            continue
        for b in content:
            if isinstance(b, dict) and b.get("type") == "tool_result":
                out.extend(text_blocks(b.get("content")))
    return out


def _responses_command(item: dict):
    if item.get("type") == "custom_tool_call":
        inp = item.get("input")
        return {"input": inp} if isinstance(inp, str) else {}
    if item.get("type") in ("local_shell_call", "shell_call"):
        action = item.get("action")
        if isinstance(action, dict):
            cmd = action.get("command") or action.get("commands")
            if isinstance(cmd, list):
                return {"command": " ".join(str(x) for x in cmd)}
            if isinstance(cmd, str):
                return {"command": cmd}
        return {}
    args = item.get("arguments")
    if isinstance(args, str):
        try:
            parsed = json.loads(args)
            return parsed if isinstance(parsed, dict) else {"arguments": args}
        except json.JSONDecodeError:
            return {"arguments": args}
    return {}


def walk_responses(body: dict, w: Walk):
    w.model = body.get("model") if isinstance(body.get("model"), str) else None
    instr = body.get("instructions")
    if isinstance(instr, str):
        w.c["system"] = len(instr)
    tools = body.get("tools")
    if isinstance(tools, list):
        w.c["tools_def"] = jlen(tools)
        w.c["n_tools_def"] = len(tools)
    items = body.get("input") or []
    if not isinstance(items, list):
        return
    w.msg_count = len(items)
    firsts = []
    for it in items:
        if isinstance(it, dict) and it.get("type", "message") == "message" and it.get("role") == "user":
            firsts.append(json.dumps(it, sort_keys=True, ensure_ascii=False))
            if len(firsts) == 2:
                break
    w.conv_key_src = "\n".join(firsts) if firsts else None
    for it in items:
        if not isinstance(it, dict):
            continue
        t = it.get("type", "message")
        if t == "message":
            role = it.get("role")
            content = it.get("content")
            texts = text_blocks(content, "input_text") + text_blocks(content, "output_text") \
                if isinstance(content, list) else ([content] if isinstance(content, str) else [])
            n = sum(len(x) for x in texts)
            if role == "assistant":
                w.assistant_side()
                w.c["asst_text"] += n
                w.pending_text += "".join(texts)
            elif role in ("user", "developer", "system"):
                w.user_side()
                w.c["user_text"] += n
                if any("<system-reminder>" in x or "<environment_context>" in x for x in texts):
                    w.c["sys_reminder"] += n
        elif t == "reasoning":
            w.assistant_side()
            w.c["thinking"] += len(it.get("encrypted_content") or "") \
                + sum(len(s.get("text") or "") for s in (it.get("summary") or []) if isinstance(s, dict))
        elif t in ("function_call", "custom_tool_call", "local_shell_call", "shell_call"):
            name = it.get("name") or ("shell_command" if "shell" in t else t)
            w.tool_use(it.get("call_id"), name, _responses_command(it))
        elif t in ("function_call_output", "custom_tool_call_output"):
            out = it.get("output")
            texts = text_blocks(out, "input_text") if isinstance(out, list) else \
                ([out] if isinstance(out, str) else [])
            w.tool_result(it.get("call_id"), texts, False)
        elif t == "local_shell_call_output":
            out = it.get("output")
            w.tool_result(it.get("call_id"), [out] if isinstance(out, str) else [], False)
        elif t == "shell_call_output":
            out = it.get("output")
            texts = []
            if isinstance(out, list):
                for e in out:
                    if isinstance(e, dict):
                        for k in ("stdout", "stderr"):
                            v = e.get(k)
                            if isinstance(v, str):
                                texts.append(v)
            w.tool_result(it.get("call_id"), texts, False)


def responses_after_texts(body: dict):
    out = []
    for it in body.get("input") or []:
        if not isinstance(it, dict):
            continue
        t = it.get("type")
        if t in ("function_call_output", "custom_tool_call_output"):
            o = it.get("output")
            out.extend(text_blocks(o, "input_text") if isinstance(o, list) else
                       ([o] if isinstance(o, str) else []))
        elif t == "local_shell_call_output":
            o = it.get("output")
            if isinstance(o, str):
                out.append(o)
        elif t == "shell_call_output":
            o = it.get("output")
            if isinstance(o, list):
                for e in o:
                    if isinstance(e, dict):
                        for k in ("stdout", "stderr"):
                            v = e.get(k)
                            if isinstance(v, str):
                                out.append(v)
    return out


def walk_chat(body: dict, w: Walk):
    w.model = body.get("model") if isinstance(body.get("model"), str) else None
    tools = body.get("tools")
    if isinstance(tools, list):
        w.c["tools_def"] = jlen(tools)
        w.c["n_tools_def"] = len(tools)
    msgs = body.get("messages") or []
    if not isinstance(msgs, list):
        return
    w.msg_count = len(msgs)
    first_user = next((m for m in msgs if isinstance(m, dict) and m.get("role") == "user"), None)
    w.conv_key_src = json.dumps(first_user, sort_keys=True, ensure_ascii=False) if first_user else None
    for m in msgs:
        if not isinstance(m, dict):
            continue
        role = m.get("role")
        content = m.get("content")
        texts = text_blocks(content)
        n = sum(len(x) for x in texts)
        if role == "system":
            w.c["system"] += n
        elif role == "user":
            w.c["user_text"] += n
        elif role == "assistant":
            w.assistant_side()
            w.c["asst_text"] += n
            w.pending_text += "".join(texts)
            rc = m.get("reasoning_content")
            if isinstance(rc, str):
                w.c["thinking"] += len(rc)
            for tc in m.get("tool_calls") or []:
                if not isinstance(tc, dict):
                    continue
                fn = tc.get("function") or {}
                args = fn.get("arguments")
                parsed = {}
                if isinstance(args, str):
                    try:
                        parsed = json.loads(args)
                    except json.JSONDecodeError:
                        parsed = {"arguments": args}
                w.tool_use(tc.get("id"), fn.get("name") or "?", parsed if isinstance(parsed, dict) else {})
        elif role == "tool":
            w.tool_result(m.get("tool_call_id"), texts, False)


def chat_after_texts(body: dict):
    out = []
    for m in body.get("messages") or []:
        if isinstance(m, dict) and m.get("role") == "tool":
            out.extend(text_blocks(m.get("content")))
    return out


WALKERS = {
    "/v1/messages": (walk_anthropic, anthropic_after_texts),
    "/responses": (walk_responses, responses_after_texts),
    "/chat/completions": (walk_chat, chat_after_texts),
}


# ------------------------------------------------------------------------------ main

def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--name", required=True)
    ap.add_argument("--provider", default=None, help="only this provider (default: all)")
    ap.add_argument("--limit", type=int, default=0)
    ap.add_argument("--batch", type=int, default=32)
    ap.add_argument("--db", default=lib.DEFAULT_SRC_DB)
    ap.add_argument("--out", default=OUT_DB)
    ap.add_argument("--rescan", action="store_true", help="ignore the checkpoint, start at rowid 0")
    args = ap.parse_args()

    lock = try_lock(args.name)
    if lock is None:
        print("[scan] another scan holds the lock (%s)" % (LOCK + ".owner"))
        return 2
    started = time.time()
    try:
        src = lib.open_ro(args.db)
        out = lib.open_corpus_rw(args.out)
        out.executescript(SCHEMA)
        ck = "last_rowid:%s" % (args.provider or "all")
        row = out.execute("SELECT Value FROM Meta WHERE Key=?", (ck,)).fetchone()
        last = 0 if args.rescan else (int(row[0]) if row else 0)
        print("[scan] starting after rowid %d (%s)" % (last, ck), flush=True)
        conv_cache = {}  # ConvKey -> (max_tool_uses, ...) hot cache to cut UPDATE churn
        done = scanned = 0
        while True:
            if args.limit and done >= args.limit:
                break
            take = min(args.batch, args.limit - done) if args.limit else args.batch
            sql = ("SELECT rowid, Id, CreatedUTC, Provider, Path, StatusCode, RequestBefore,"
                   " RequestAfter, ResponseBody FROM ProxyExchanges WHERE rowid > ?")
            params = [last]
            if args.provider:
                sql += " AND Provider = ?"
                params.append(args.provider)
            sql += " ORDER BY rowid LIMIT ?"
            params.append(take)
            batch = src.execute(sql, params).fetchall()
            if not batch:
                break
            out.execute("BEGIN")
            for (rowid, ex_id, created, provider, path, status, before, after, resp) in batch:
                last = rowid
                done += 1
                if not path.endswith(SUFFIXES):
                    continue
                scanned += len(before) + len(after) + len(resp or "")
                degenerate = 1 if (before == "" and after == "") else 0
                passthrough = 1 if before == after else 0
                suffix = next(s for s in SUFFIXES if path.endswith(s))
                walker, after_fn = WALKERS[suffix]
                w = Walk(provider)
                paired = False
                if not degenerate:
                    try:
                        body = json.loads(before)
                        if isinstance(body, dict):
                            walker(body, w)
                            if passthrough:
                                for item, t in w.results:
                                    if item is not None:
                                        item["aft"] += len(t)
                                paired = True
                            else:
                                ab = json.loads(after)
                                paired = w.pair_after(after_fn(ab)) if isinstance(ab, dict) else False
                    except (json.JSONDecodeError, RecursionError, TypeError, AttributeError):
                        pass
                allow = ALLOW.get(provider, set())
                tr = sum(it["raw"] for it in w.items) + sum(len(t) for it, t in w.results if it is None)
                tra = sum(it["raw"] for it in w.items if it["tool"] in allow)
                tafter = sum(it["aft"] for it in w.items) + sum(len(t) for it, t in w.results if it is None)
                tafter_a = sum(it["aft"] for it in w.items if it["tool"] in allow)
                el = sum(it["el"] for it in w.items)
                eln = sum(it["eln"] for it in w.items)
                dd = sum(it["dd"] for it in w.items)
                pt = sum(it["pt"] for it in w.items)
                ctn = sum(1 for it in w.items if it["ct"])
                ctc = sum(it["ct"] for it in w.items)
                usage = usage_from_response(provider, resp or "")
                conv_key = provider + ":" + (h16(w.conv_key_src) if w.conv_key_src else "nokey")
                out.execute(
                    "INSERT OR REPLACE INTO Requests VALUES (?,?,?,?,?,?,?,?, ?,?,?,?, ?,?,?,?,"
                    " ?,?,?, ?,?, ?,?, ?, ?,?,?,?, ?,?,?,?, ?,?, ?,?,?,?,?, ?)",
                    (rowid, ex_id, created, provider, path, status, conv_key, w.model,
                     w.msg_count, len(w.items), len(w.results), w.err_results,
                     len(before), len(after), passthrough, degenerate,
                     w.c["system"], w.c["tools_def"], w.c["n_tools_def"],
                     w.c["user_text"], w.c["sys_reminder"],
                     w.c["asst_text"], w.c["thinking"],
                     w.c["tool_use_input"],
                     tr, tra, tafter, tafter_a,
                     el, eln, dd, pt,
                     ctn, ctc,
                     usage[0], usage[1], usage[2], usage[3], usage[4],
                     len(resp or "")))
                # conversation upsert: keep the timeline of the request with the most tool calls
                n_items = len(w.items)
                cur = conv_cache.get(conv_key)
                if cur is None:
                    r = out.execute("SELECT MaxToolUses, MaxMsgCount FROM Conversations WHERE ConvKey=?",
                                    (conv_key,)).fetchone()
                    cur = (r[0], r[1]) if r else None
                replace = cur is None or n_items > cur[0] or (n_items == cur[0] and w.msg_count >= cur[1])
                if replace:
                    timeline = json.dumps(note_reactions(w.items), ensure_ascii=False)
                    out.execute(
                        "INSERT INTO Conversations(ConvKey, Provider, Model, FirstUTC, LastUTC, NumRequests,"
                        " MaxToolUses, MaxMsgCount, SrcRowidOfMax, Timeline) VALUES (?,?,?,?,?,1,?,?,?,?)"
                        " ON CONFLICT(ConvKey) DO UPDATE SET Model=excluded.Model, LastUTC=excluded.LastUTC,"
                        " NumRequests=NumRequests+1, MaxToolUses=excluded.MaxToolUses,"
                        " MaxMsgCount=excluded.MaxMsgCount, SrcRowidOfMax=excluded.SrcRowidOfMax,"
                        " Timeline=excluded.Timeline",
                        (conv_key, provider, w.model, created, created, n_items, w.msg_count, rowid, timeline))
                    conv_cache[conv_key] = (n_items, w.msg_count)
                else:
                    out.execute("UPDATE Conversations SET LastUTC=?, NumRequests=NumRequests+1 WHERE ConvKey=?",
                                (created, conv_key))
            out.execute("INSERT OR REPLACE INTO Meta(Key, Value) VALUES(?, ?)", (ck, str(last)))
            out.execute("INSERT OR REPLACE INTO Meta(Key, Value) VALUES('updated_utc', ?)", (utcnow(),))
            out.commit()
            if done % (args.batch * 16) == 0:
                print("[scan] rowid %d | %d rows | %.0f MB | %.0fs" % (last, done, scanned / 1e6, time.time() - started), flush=True)
        n = out.execute("SELECT COUNT(*) FROM Requests").fetchone()[0]
        nc = out.execute("SELECT COUNT(*) FROM Conversations").fetchone()[0]
        print("[scan] DONE: +%d rows, %.0f MB scanned, %.0fs. Requests=%d Conversations=%d"
              % (done, scanned / 1e6, time.time() - started, n, nc), flush=True)
        out.close()
        src.close()
        return 0
    finally:
        try:
            if os.name == "nt":
                import msvcrt
                lock.seek(0)
                msvcrt.locking(lock.fileno(), msvcrt.LK_UNLCK, 1)
        finally:
            lock.close()


if __name__ == "__main__":
    sys.exit(main())
