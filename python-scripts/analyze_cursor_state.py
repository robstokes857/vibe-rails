"""Characterize cursor-visibility behavior in a session's byte stream.

Built for the 2026-06-10 'Codex cursor flicker came back after 1.7.3'
investigation (see runbooks/terminal/TERMINAL.md "## 2026-06-10"). Use it
whenever a CLI's cursor visibly hops/blinks in the live terminal and you need
to know whether the transient cursor states are in the bytes or in our render
path.

Tracks, chunk by chunk (SessionLogs rows = raw PTY reads):
  - DECTCEM state (\\e[?25l hide / \\e[?25h show), including combined ?Pm;Pm
  - DEC 2026 synchronized-output state (BSU/ESU)
  - approximate cursor row (CUP/HVP/VPA/CUU/CUD/CNL/CPL + LF), alt-screen toggles

Reports what state each chunk LEAVES the terminal in -- i.e. what xterm.js
renders during the gap until the next chunk. Boundaries where the cursor is
visible and the row varies are visible cursor hops; boundaries alternating
visible/hidden are blink-style flicker.

Row tracking is approximate (no scroll regions / margins / full emulation).
Use it for the shape of the behavior, not exact positions; confirm suspect
chunks with decode_session.py / show_chunks.py.

Usage:
    python analyze_cursor_state.py <session-id> [...] [--gap-ms 1000] [--timeline N]
    python analyze_cursor_state.py --jsonl <capture.jsonl> [...]   # tools/pty-capture output
"""
import argparse
import base64
import json
import os
import re
import sqlite3
from datetime import datetime

DB = os.path.expanduser("~/.vibe_rails/state.db")


def parse_ts(ts: str) -> float:
    m = re.match(r"(\d{4}-\d\d-\d\d)T(\d\d:\d\d:\d\d)\.(\d+)Z?", ts)
    if not m:
        return 0.0
    frac = (m.group(3) + "000000")[:6]
    dt = datetime.fromisoformat(f"{m.group(1)}T{m.group(2)}.{frac}+00:00")
    return dt.timestamp()


class VtState:
    def __init__(self):
        self.cursor_visible = True
        self.sync_active = False
        self.alt_screen = False
        self.row = 1
        self.rows = 0  # terminal height if learned from \e[8;rows;colst
        self.last_abs_row = None  # last explicit row positioning
        self.saved_row = 1

    def feed(self, data: bytes, counts: dict):
        i, n = 0, len(data)
        while i < n:
            c = data[i]
            if c == 0x1B and i + 1 < n:
                nxt = data[i + 1]
                if nxt == 0x5B:  # CSI
                    j = i + 2
                    while j < n and not (0x40 <= data[j] <= 0x7E):
                        j += 1
                    if j >= n:
                        break
                    body = data[i + 2 : j].decode("latin-1", "replace")
                    final = chr(data[j])
                    self._csi(body, final, counts)
                    i = j + 1
                    continue
                if nxt == 0x5D:  # OSC
                    j = i + 2
                    while j < n and data[j] not in (0x07, 0x9C):
                        if data[j] == 0x1B and j + 1 < n and data[j + 1] == 0x5C:
                            j += 1
                            break
                        j += 1
                    i = min(j + 1, n)
                    continue
                if nxt == 0x37:  # DECSC
                    self.saved_row = self.row
                    i += 2
                    continue
                if nxt == 0x38:  # DECRC
                    self.row = self.saved_row
                    i += 2
                    continue
                i += 2
                continue
            if c == 0x0A:
                self.row += 1
                if self.rows:
                    self.row = min(self.row, self.rows)
            i += 1

    def _csi(self, body: str, final: str, counts: dict):
        private = body.startswith("?")
        params = body[1:] if private else body
        plist = [p for p in params.split(";") if p]

        if private and final in "hl":
            on = final == "h"
            for p in plist:
                if p == "25":
                    counts["?25h" if on else "?25l"] += 1
                    self.cursor_visible = on
                elif p == "2026":
                    counts["?2026h" if on else "?2026l"] += 1
                    self.sync_active = on
                elif p in ("1049", "1047", "47"):
                    counts["altscreen"] += 1
                    self.alt_screen = on
            return
        if private:
            return

        def p1(default=1):
            try:
                return max(1, int(plist[0])) if plist else default
            except ValueError:
                return default

        if final in ("H", "f", "d"):
            self.row = p1()
            self.last_abs_row = self.row
        elif final in ("A", "F"):
            self.row = max(1, self.row - p1())
        elif final in ("B", "E"):
            self.row += p1()
            if self.rows:
                self.row = min(self.row, self.rows)
        elif final == "t":
            if len(plist) >= 3 and plist[0] == "8":
                try:
                    self.rows = int(plist[1])
                except ValueError:
                    pass
        elif final == "s":
            self.saved_row = self.row
        elif final == "u":
            self.row = self.saved_row


def load_jsonl(path: str):
    """Load a tools/pty-capture JSONL file into (label, rows) where rows mimic
    SessionLogs tuples: (id, timestamp-or-ms, content-bytes)."""
    rows = []
    label = path
    with open(path, encoding="utf-8-sig") as f:
        for i, line in enumerate(f):
            line = line.strip()
            if not line:
                continue
            obj = json.loads(line)
            if obj.get("meta"):
                label = f"{path} cmd={obj.get('cmd')} env={obj.get('env')}"
                continue
            rows.append((i, float(obj["t"]), base64.b64decode(obj["b64"])))
    return label, rows


def analyze(session_id: str, gap_ms: int, timeline_n: int, jsonl: bool = False):
    if jsonl:
        label, rows = load_jsonl(session_id)
        print(f"\n=== {label}  chunks={len(rows)} "
              f"bytes={sum(len(r[2]) for r in rows)} ===")
        ts_of = lambda v: v / 1000.0  # capture timestamps are ms floats
    else:
        db = sqlite3.connect(DB)
        cli, started = db.execute(
            "SELECT Cli, StartedUTC FROM Sessions WHERE Id = ?", (session_id,)
        ).fetchone()
        rows = db.execute(
            "SELECT Id, Timestamp, Content FROM SessionLogs WHERE SessionId = ? ORDER BY Id",
            (session_id,),
        ).fetchall()
        print(f"\n=== {session_id}  cli={cli}  started={started}  chunks={len(rows)} "
              f"bytes={sum(len(r[2]) for r in rows)} ===")
        ts_of = parse_ts

    st = VtState()
    counts = {k: 0 for k in ("?25l", "?25h", "?2026h", "?2026l", "altscreen")}
    shape = {"hide+show": 0, "hide-only": 0, "show-only": 0, "none": 0}

    bounds = []          # (t, visible, sync_open, row, last_abs_row, cid, len)
    t0 = ts_of(rows[0][1]) if rows else 0

    for cid, ts, content in rows:
        before = dict(counts)
        st.feed(content, counts)
        dh = counts["?25l"] - before["?25l"]
        ds = counts["?25h"] - before["?25h"]
        if dh and ds:
            shape["hide+show"] += 1
        elif dh:
            shape["hide-only"] += 1
        elif ds:
            shape["show-only"] += 1
        else:
            shape["none"] += 1
        bounds.append((ts_of(ts), st.cursor_visible, st.sync_active,
                       st.row, st.last_abs_row, cid, len(content)))

    print(f"counts: {counts}   chunk shapes: {shape}   term rows={st.rows or '?'}")

    # boundary analysis during active streaming
    active = []
    for k in range(len(bounds) - 1):
        gap = (bounds[k + 1][0] - bounds[k][0]) * 1000.0
        if 0 <= gap <= gap_ms:
            active.append((bounds[k], gap))
    vis = [b for b, g in active if b[1]]
    sync_open = [b for b, g in active if b[2]]
    print(f"active-stream boundaries (gap<= {gap_ms}ms): {len(active)}; "
          f"cursor VISIBLE at {len(vis)} ({100.0*len(vis)/max(1,len(active)):.0f}%); "
          f"sync OPEN at {len(sync_open)}")

    flips = 0
    moves = 0
    prev = None
    for b, g in active:
        if prev is not None:
            if b[1] != prev[1]:
                flips += 1
            if b[1] and prev[1] and b[3] != prev[3]:
                moves += 1
        prev = b
    print(f"visibility flips across consecutive active boundaries: {flips}; "
          f"visible-row moves: {moves}")

    # Renderable-only view: a DEC-2026-honoring renderer (xterm.js v6) only
    # commits chunk states where sync is CLOSED; mid-sync states are deferred
    # and never painted. Cursor oscillation that exists at all boundaries but
    # vanishes here is invisible to the user (this is what separates the
    # modern conpty.dll from the in-box conhost on the Codex hop).
    rb = [b for b, g in active if not b[2]]
    rmoves = 0
    for k in range(1, len(rb)):
        if rb[k][1] and rb[k - 1][1] and rb[k][3] != rb[k - 1][3]:
            rmoves += 1
    print(f"RENDERABLE boundaries (sync closed): {len(rb)}; "
          f"visible-row moves renderable-only: {rmoves}")

    if vis:
        rowhist = {}
        for b in vis:
            rowhist[b[3]] = rowhist.get(b[3], 0) + 1
        top = sorted(rowhist.items(), key=lambda kv: -kv[1])[:8]
        print(f"rows where cursor sits VISIBLE at boundary (row: count): {top}")

    # timeline sample from the busiest 30s window
    if timeline_n and bounds:
        best_i, best_cnt = 0, 0
        for k in range(len(bounds)):
            cnt = 0
            for m in range(k, len(bounds)):
                if bounds[m][0] - bounds[k][0] > 30:
                    break
                cnt += 1
            if cnt > best_cnt:
                best_i, best_cnt = k, cnt
        print(f"\ntimeline sample (busiest 30s window, first {timeline_n} chunks):")
        print(" t(s)    cid      len  vis sync row absrow")
        for b in bounds[best_i : best_i + timeline_n]:
            print(f"{b[0]-t0:8.3f} {b[5]:>8} {b[6]:>5}  {'V' if b[1] else '.'}  "
                  f"{'S' if b[2] else '.'} {b[3]:>4} {b[4] if b[4] is not None else '-':>5}")


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("session_ids", nargs="+",
                    help="session GUIDs, or capture .jsonl paths with --jsonl")
    ap.add_argument("--gap-ms", type=int, default=1000)
    ap.add_argument("--timeline", type=int, default=40)
    ap.add_argument("--jsonl", action="store_true",
                    help="inputs are tools/pty-capture JSONL files, not session ids")
    a = ap.parse_args()
    for sid in a.session_ids:
        analyze(sid, a.gap_ms, a.timeline, jsonl=a.jsonl)
