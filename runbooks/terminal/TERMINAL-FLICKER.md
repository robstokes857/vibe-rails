# TERMINAL-FLICKER.md — the cursor flicker saga, end to end

> **Status (2026-06-11):** Root cause found and confirmed. The flicker was never
> one bug — it was **two different bugs wearing the same name**, and the fix for
> one kept unmasking the other. Both are now handled:
>
> | Bug | Cause | Status |
> |---|---|---|
> | **The blink** (~3 Hz, in place, Claude + Codex) | Our own 90 ms cursor-suppression timer | **FIXED** in 1.7.3 (`d1d273d`) — suppression deleted |
> | **The hop** (~10 Hz, composer ↔ status line, Codex only, **Windows only**) | The in-box Windows ConPTY re-exposing Codex's transient cursor states | **MASKED** 2026-06-11 — suppression revived, gated to Codex tabs only. Underlying cause not curable from our side; see §10, §11 |
>
> **This is the only open terminal bug.** Everything else on the historical list
> (stacked repaints, startup triple-print, textarea caret, occlusion typing lag)
> is fixed or mitigated-and-quiet. The hop itself is now invisible in the web/
> VS Code-webview terminal; what stays open is the *source* of it (Codex ×
> in-box ConPTY), which still shows in externally-launched terminals and in
> recorded session bytes. Windows only — Mac is clean, confirmed.

This document is the canonical write-up. `TERMINAL.md` keeps the dated
investigation log; this file tells the whole story in one place.

---

## 1. Executive summary

Since roughly April 2026, the text cursor in VibeRails terminals running
**Codex** would visibly flicker — jumping between the composer (input box) and
the end of the status/spinner line, several times a second, whenever Codex was
thinking. At various points we blamed xterm.js, our WebSocket batching, an
Anthropic env var, CSS styling, and Codex itself. The final answer implicates a
component none of us were looking at:

**ConPTY — the pseudo-console middleman built into Windows — rewrites every
byte a CLI emits, and the OLD in-box version of it re-emits Codex's transient
mid-frame cursor positions as paintable states.** Codex marks its repaints
atomic (DEC 2026 synchronized output); the old ConPTY re-renders *between*
Codex's write() syscalls and emits the in-between state (visible cursor parked
on the spinner line) **outside** any atomic bracket. xterm.js then faithfully
paints it. The modern ConPTY (the `conpty.dll` from the microsoft/terminal
codebase, which VS Code now bundles) brackets its re-emissions properly, so the
same transient states are never displayable.

That single fact explains every observation we ever made about this bug,
including why it "went away" in v1.6.0, "came back" in 1.7.3, never existed on
Mac, and never showed in VS Code's own terminal.

The fix shipped today is deliberately cheap: the old cursor-suppression
mitigation is back, but **gated to Codex tabs only** (§10). The durable fix —
shipping the modern conpty.dll ourselves, exactly like VS Code does — is
designed, was prototyped and validated during the investigation, and is
documented in §11 for the day we want it.

---

## 2. Not to be confused with

The terminal has had several visually-similar historical bugs. This document is
**only** about the cursor flicker. The others, for disambiguation:

- **Stacked repaints during drag-resize** — N persistent copies of the TUI after
  rapid resizes. Different bug, different fix (`RESIZE_SYNC_DEBOUNCE_MS`), see
  TERMINAL.md "## 2026-05-15".
- **Startup prompt triple-print** — web-font addon reflow, fixed 2026-04-15.
- **Hidden helper-textarea browser caret** — a DOM caret bleeding through,
  fixed long ago with `caret-color: transparent`.
- **Cursor blink setting** — `cursorBlink` is `false` in `vibe-terminal.js`;
  the flicker was never xterm's blinking.

---

## 3. The two flickers

The saga was unsolvable for so long because two distinct artifacts shared the
name "the cursor flicker":

| | **The blink** | **The hop** |
|---|---|---|
| What you see | Cursor turns on/off **in place**, ~3×/sec | Cursor **teleports** between the composer and the status/spinner row, ~10×/sec |
| When | While a CLI streams output | While **Codex** is thinking (spinner active) |
| Affected | Claude + Codex | Codex only |
| Platform | Any | **Windows only** |
| In the recorded bytes? | **No** — live render path only; replay never shows it | **Yes** — baked into SessionLogs; a *focused* replay shows it |
| Cause | OUR `suppressCursorDuringOutput()` 90 ms restore timer cycling against output gaps | Old ConPTY re-emitting Codex's transient cursor states outside `?2026` brackets |
| Fixed | 1.7.3 (`d1d273d`) — deleted the suppression call | Masked 2026-06-11 — suppression revived for Codex tabs only |

The cruel interaction: **the mitigation for the hop (v1.6.0 suppression) was
the cause of the blink**, and the fix for the blink (1.7.3) unmasked the hop.
Every "fix" toggled which bug was visible.

---

## 4. Timeline

| Date | Version | Event |
|---|---|---|
| pre-2026-04 | ≤1.5.x | Codex's "status-line cursor flash" reported — cursor jumping to the footer during redraws. The hop, original edition. |
| 2026-04-16 | v1.6.0 (`3ea2f30`) | Mitigation: `suppressCursorDuringOutput(90ms)` hides the cursor during output bursts. **Hop masked.** A "small residual flicker" is noted and shrugged off — that residual was the suppression's own blink. |
| ~2026-05 | 1.6.x | Claude's TUI redraw cadence changes; its output gaps start straddling the 90 ms restore timer. **Claude starts blinking ~3×/sec.** |
| 2026-06-08 | 1.7.3 (`d1d273d`) | Root cause of the blink found (66 restore→hide cycles in a 22 s Claude session `750e672f`). Suppression call deleted. **Blink fixed — hop unmasked.** Justification "CLIs already manage cursor visibility via DECTCEM" was true for Claude, false for Codex. |
| 2026-06-10 | 1.7.3/1.7.4 | Rob reports "the Codex flicker is back". Byte forensics prove the hop is in the bytes, pre- and post-1.7.3 identical (`4026ff95` vs `33ee4a66`), and that Claude parks its cursor only at the prompt while Codex's parks oscillate. Initially misattributed to Codex's bytes alone ("native parity"). |
| 2026-06-11 | — | Rob's VS Code observation breaks the "native parity" framing. Full investigation: env-var matrix (falsified), codex-rs source dive, ConPTY A/B capture (60 → 0 renderable cursor moves). **Real root cause: in-box ConPTY.** Rob confirms three ways (§8.1). Fix shipped: Codex-only suppression revival. |

---

## 5. Root cause — the full causal chain

### 5.1 What Codex actually emits (source-confirmed)

Codex's TUI (codex-rs, verified at v0.139.0 and unchanged on main) draws every
frame through `tui/src/custom_terminal.rs` `try_draw`:

```rust
self.flush()?;                       // diff is queue!()d — not yet flushed
match cursor_position {
    None => self.hide_cursor()?,
    Some(position) => {
        self.set_cursor_style(cursor_style)?;   // queue!() — no flush
        self.show_cursor()?;                    // execute!(Show)   -> FLUSH
        self.set_cursor_position(position)?;    // execute!(MoveTo) -> FLUSH
    }
}
```

Note the order: **show the cursor first, then move it** (inherited from
upstream ratatui). Combined with `Tui::draw` wrapping everything in
`sync_update` (crossterm: `queue(BeginSynchronizedUpdate)` … 
`execute(EndSynchronizedUpdate)`), the bytes leave the process as **separate
write() syscalls per frame**:

- **write A:** `\e[?2026h` + frame diff + cursor style + `\e[?25h` — the diff's
  last cell painted is the bottom-most change, which during a spinner tick is
  the end of the status line. The cursor is made **visible while parked there**.
- **write B:** `\e[<row>;<col>H` — MoveTo the composer, alone in its own syscall.
- **write C:** `\e[?2026l`.

Logically the whole thing is one atomic frame — the transient "visible cursor on
the status line" state lives strictly *inside* the `?2026` bracket. Any terminal
that honors DEC 2026 should never display it. Codex repaints every **32 ms**
while thinking (spinner tick; global cap 120 fps).

### 5.2 The middleman: ConPTY rewrites everything

On Windows, a CLI never talks to the terminal directly. ConPTY — a headless
mini-terminal inside Windows — consumes the app's VT stream into its own screen
buffer and **re-emits its own synthesized VT stream** on its own frame pacing.
What VibeRails records and renders is ConPTY's rendition, not Codex's bytes.

- **The OLD in-box ConPTY** (`kernel32!CreatePseudoConsole` → conhost.exe, what
  our `Pty.Net` uses — `Pty.Net/Windows/PtyProvider.cs:200`): runs a render pass
  **between Codex's write A and write B**, snapshots "frame complete, cursor
  visible at status-line end", and re-emits that as a finished frame with the
  sync bracket already closed. 6–35 ms later it emits the composer re-park as a
  separate unbracketed delta — its classic VtRenderer cursor-move wrapper
  `\e[?25l\e[<r>;<c>H\e[?25h` (those 19-byte chunks in our captures are
  **conhost's bytes, not Codex's**).
- **The MODERN ConPTY** (`conpty.dll` + `OpenConsole.exe` from the
  microsoft/terminal codebase): wraps its own re-emissions in `?2026` brackets.
  The same transient states still exist in the stream but are never *renderable*
  — a DEC-2026-honoring renderer defers everything inside a bracket.

### 5.3 What xterm.js does with each

xterm.js v6 honors DEC 2026 (deferred render until ESU). Fed the old-ConPTY
stream, the status-line cursor state arrives **sync-closed → renderable →
painted**. Fed the modern-ConPTY stream, it arrives **inside a bracket →
deferred → never painted**. xterm is behaving correctly in both cases; it paints
exactly what the stream makes paintable. So do our server layers —
`WebSocketConsumer` already ships whole sync frames (sync-aware batching,
≤100 ms hold), and the 6–35 ms gap between the frame and the re-park is real
producer-side timing that no amount of pipeline batching can soundly merge.

### 5.4 Real bytes (session `33ee4a66`, Codex v0.139.0, 2026-06-10, our terminal)

```
#9306108  \e[?2026h\e[0 q                                      sync open + style pulse
#9306109  \e[?25l\e[12;2H\e[K…status repaint…\e[13;65H\e[?25h  frame; cursor VISIBLE at 13;65
                                                               (right after "esc to interrupt")
#9306110  \e[?2026l                                            sync closed  ← RENDERABLE: cursor on status line
   …6–35 ms (real gap)…
#9306111  \e[?25l\e[16;3H\e[?25h                               conhost re-park at composer ← RENDERABLE: cursor jumps
```

At ~10 Hz, that pair of renderable states *is* the hop.

---

## 6. Why Windows-only (confirmed on Mac)

On macOS/Linux there is **no ConPTY**. The PTY is a kernel byte pipe; Codex's
writes reach xterm.js byte-for-byte, the transient cursor state stays inside the
`?2026` bracket, and xterm defers it. Nothing to paint, nothing to flicker.

**Confirmed by Rob 2026-06-11: Codex on Mac running 1.7.3 (no suppression at
all) has no flicker.** Same VibeRails code, same xterm.js, same Codex — the only
difference is the absence of the Windows middleman. This is the cleanest
single-variable proof that the hop is not in our render path and not in Codex's
logical output, but in the Windows console layer between them.

Platform matrix:

| Path | Console layer | Hop? |
|---|---|---|
| VibeRails web/webview terminal, Windows | in-box ConPTY (kernel32) | **YES** (masked by Codex-scoped suppression as of 2026-06-11) |
| VibeRails "launch in external terminal", Windows | in-box conhost (default-terminal delegation) | **YES** (not fixable from our side — bytes go to a real console window) |
| VS Code integrated terminal, Windows ≥1.121 | bundled modern conpty.dll | no |
| VS Code integrated terminal, `windowsUseConptyDll: false` | in-box ConPTY | **YES** (Rob reproduced this) |
| Windows Terminal tab (spawns the shell itself) | its own modern console host | no |
| VibeRails on macOS | none (real PTY) | no |

---

## 7. How VS Code handles it

VS Code hit this whole class of problem years ago and solved it by **shipping
its own console host** instead of depending on the OS one:

- node-pty grew a `useConptyDll` mode that loads a bundled
  `conpty.dll`/`OpenConsole.exe` (built from microsoft/terminal, MIT) and
  resolves `CreatePseudoConsole`/`Resize`/`ClosePseudoConsole` from it instead
  of kernel32.
- VS Code shipped it experimentally in 1.93 (Aug 2024), A/B-rolled it from Dec
  2025 (PR #282835), updated the dll to v1.25.260303002 in Mar 2026
  (PR #301398), and **flipped it default-on in 1.121** (PR #315951, merged
  2026-05-12; stable ~2026-05-19). The dlls live at
  `resources\app\node_modules\node-pty\build\Release\conpty\`.

That date matters: any VS Code updated since ~May 20 renders Codex through the
modern conhost — which is why "it looks fine in VS Code's terminal but not in
ours" became true *recently* and silently. VibeRails' own PTY (`Pty.Net` →
kernel32) was unaffected by VS Code's setting; the two terminals on the same
machine were running **different ConPTY implementations**.

For completeness: VS Code's terminal also sets `TERM_PROGRAM=vscode`,
`TERM_PROGRAM_VERSION`, `COLORTERM=truecolor` (and **not** `TERM` on Windows) —
none of which matter here; see §8.3.

---

## 8. The evidence

### 8.1 Rob's manual confirmations (the decisive ones)

1. **VS Code conpty flip:** setting `terminal.integrated.windowsUseConptyDll`
   to `false` and running Codex in VS Code's own terminal **reproduces the
   identical flicker there**; flipping it back cleans it. Same app, same
   terminal, same machine — only the console host changes. This is the
   experiment that confirms the middleman, with zero VibeRails code involved.
2. **Mac:** Codex under VibeRails 1.7.3 on macOS — no flicker (no ConPTY).
3. **1.6.15 on Windows:** no Codex flicker — the suppression (v1.6.0–1.7.2) was
   masking the hop the whole time, and Codex's 32 ms cadence never tripped the
   timer's blink artifact (unlike Claude's).
4. **Launch-path split:** Codex started from VibeRails in an external native
   terminal flickers; Codex started by hand from a PowerShell tab does not.
   Both windows can even be Windows Terminal — what differs is *which conhost
   feeds them* (delegated in-box conhost vs WT's own host).
5. **Replay:** replaying a recorded Codex session shows no flicker — until you
   click the replay terminal to focus it, and the flicker appears. xterm only
   draws a cursor when focused; the hop is baked into the recorded bytes
   (they're the old ConPTY's output). This also explains why the 2026-06-09
   "replay never flickers" differential — run on *Claude* sessions, whose bytes
   can't hop — exonerated the bytes incorrectly for Codex.

### 8.2 Controlled A/B capture (same codex, same env, only the conpty swapped)

A one-off harness spawned `powershell → codex` under our own `Pty.Net` spawn
path and recorded every raw PTY read with timestamps, once through
`kernel32!CreatePseudoConsole` and once through VS Code's bundled `conpty.dll`
(via a temporary, since-reverted resolver override in Pty.Net). Analyzed with
`python-scripts/analyze_cursor_state.py --jsonl`, which tracks DECTCEM/DEC-2026
state per chunk and — the key metric — **cursor movement at RENDERABLE
boundaries** (chunk ends with sync closed, i.e. states a 2026-honoring renderer
actually paints):

| | in-box ConPTY | modern conpty.dll |
|---|---|---|
| visible-cursor row moves, all chunk boundaries | 60 | 49 |
| …at **renderable** boundaries only | **60 (100%)** | **7** |
| shape of renderable moves | composer row ↔ spinner row, 21× each way (oscillation) | one-time boot layout shifts (1→2→11→12→14), zero oscillation |

Same transient states exist in both streams; the modern conhost just never
makes them paintable.

### 8.3 The env-var matrix (hypothesis falsified)

Because Claude Code's flicker history involved TERM-sniffing
(`CLAUDE_CODE_FORCE_SYNC_OUTPUT`), we tested whether Codex gates rendering on
the environment. Four captures through the in-box ConPTY — bare env (our
production shape), `+WT_SESSION`, `+TERM_PROGRAM=vscode +TERM=xterm-256color
+COLORTERM=truecolor`, `+TERM=xterm-256color` only — produced **the same park
pattern** (~0.7 visible-row moves per frame in all four). Codex's source agrees:
its terminal detection (`codex-rs/terminal-detection`) feeds keyboard-mode,
reflow-cap, color and telemetry decisions — **nothing cursor- or sync-related is
env-gated on native Windows**. Env vars are a dead end for this bug.

### 8.4 Codex source findings (upstream framing)

The precise upstream defect is: **`show_cursor()` before `set_cursor_position()`,
across a syscall boundary, inside the sync bracket** (§5.1). Any terminal that
honors the bracket hides it; the old ConPTY un-brackets it. Reported shapes
upstream: openai/codex#9081 ("cursor blink speeds up and jumps during TUI
redraw", Windows Terminal/cmd/pwsh — closed "not planned"), #11063 (cursor jank
during streaming — open, no PR). If the order were reversed upstream, the hop
would be impossible in every terminal. A user-level lever also exists:
`tui.animations = false` in `~/.codex/config.toml` stops the 32 ms spinner
ticks entirely, collapsing repaint (and hop) frequency to actual content
updates.

### 8.5 Session byte archaeology

- Pre-fix Codex session `4026ff95` (2026-06-08, suppression era): 31k chunks,
  cursor visible at 31,133/31,134 boundaries, **10,833** visible-row moves.
- Post-fix Codex sessions `33ee4a66` / `c3c1a69e` (2026-06-10): same shape.
  **The bytes never changed** — 1.7.3 only removed the client-side mask.
- Claude contrast (`c8bac352`, same day): hide-only chunks bracket whole repaint
  bursts, cursor parks only at the prompt row, 8 row-moves per session. This
  asymmetry is why deleting the suppression was correct for Claude and a
  regression for Codex.

---

## 9. Red herrings and wrong turns (so nobody repeats them)

1. **`CLAUDE_CODE_FORCE_SYNC_OUTPUT`** — blamed for the blink in early June.
   Falsified twice (1.6.15 had no env var and still blinked; replay of identical
   bytes doesn't blink). Stays a red herring.
2. **"CLIs already manage cursor visibility via DECTCEM"** (`d1d273d`'s
   justification) — Claude-only. Codex relies on the sync bracket instead, and
   the old ConPTY breaks the bracket.
3. **"Native parity — Codex flickers in native terminals too"** (2026-06-10
   conclusion) — half-true and overbroad. It flickers in *old-conhost-fed*
   terminals. Rob's VS Code observation falsified the framing within a day.
4. **Cursor styling/CSS** — never a suspect that survived contact: styling
   recolors the cursor; the hop is the cursor's *position* changing in the
   stream we're fed.
5. **The position-settle render gate** (2026-06-10 proposal: hide the rendered
   cursor until its position is stable across rAF ticks) — clever, unnecessary.
   Dead. Do not build it; the actual differentiator was the conhost version.
6. **Server-side batching fixes** — unsound by construction: the frame→re-park
   gap is real producer-side time (6–35 ms); merging it requires cadence-guessing
   timers, the exact "mitigation becomes the next bug" trap from 2026-04-16.

---

## 10. The fix shipped 2026-06-11 — Codex-only suppression

Rob chose the cheapest sound option: bring back the v1.6.0 mitigation, scoped
to the one CLI that needs it.

### What changed

- `terminal-tab.js` flush path: the exact `suppressCursorDuringOutput(OUTPUT_CURSOR_IDLE_MS)`
  call `d1d273d` deleted is back, wrapped in
  `if ((this.state.cli || '').toLowerCase() === 'codex')`. All the machinery
  (90 ms timer, `vb-terminal-cursor-suppressed` CSS class, restore paths in
  `vibe-terminal.js`) was still in the tree. **Per-terminal by construction** —
  the class toggles on that tab's element only.
- CLI plumbing (tabs didn't know their CLI before):
  `TerminalSessionService.ActiveCli` (cleared on teardown; null for
  externally-owned sessions) → optional `Cli` on `TerminalStatusResponse` /
  `TerminalTabStatusResponse` (child status → root `BuildTabStatusAsync` → tabs
  list and start responses) → client `state.cli` set in `startSession`,
  hydrated in `terminal-multitab.js` `addLocalTab` on reload, cleared on stop.
  Suppression therefore survives webview reloads mid-session.

### Why this is safe for Codex but stays banned for Claude

The 90 ms timer blinks when a CLI's output gaps **straddle** 90 ms: each gap
fires the restore (cursor ON), the next chunk re-hides (OFF). Claude's
spinner/status cadence does exactly that (66 cycles in 22 s, session
`750e672f`). Codex ticks every **32 ms** — comfortably under the timer — so
during thinking the cursor stays cleanly hidden, and when output stops it
restores once and stays. That is precisely the 1.6.15 visual Rob confirmed as
flicker-free. **Never enable suppression for Claude (or globally) again** — that
recreates the 3 Hz blink fixed in 1.7.3.

### Accepted limitations

- **External-terminal Codex sessions still hop.** Those bytes render in a real
  console window fed by the in-box conhost; our CSS can't reach them. Only §11
  fixes that path.
- **Focused replays of recorded Codex sessions still hop** — the transient
  states are baked into SessionLogs.
- **Cadence risk:** if a future Codex slows its repaint cadence past ~90 ms, the
  blink artifact returns *for Codex*. If that happens: do **not** tune the
  timer; go to §11.

---

## 11. The durable fix we did not take (kept on the shelf)

Ship the modern console host ourselves — exactly VS Code's move:

- Bundle `conpty.dll` + `OpenConsole.exe` (MIT; VS Code and node-pty
  redistribute these exact binaries; official source is the
  microsoft/terminal-produced package, pin the version VS Code ships —
  v1.25.x).
- In `Pty.Net/Windows/NativeMethods.cs`, resolve
  `CreatePseudoConsole`/`ResizePseudoConsole`/`ClosePseudoConsole` from the
  bundled dll with kernel32 fallback (node-pty's `useConptyDll` pattern). A
  working env-var-gated prototype (`VIBERAILS_CONPTY_DLL`) was built and
  validated during this investigation (it produced the §8.2 numbers), then
  reverted — Pty.Net is currently untouched relative to HEAD.

What it buys over §10: fixes the hop at the source for web tabs **and**
external terminals; future recordings stop containing the transient states;
likely also helps the still-open xterm stacked-repaints race (frames would
arrive genuinely sync-bracketed); removes the cadence risk entirely.

Costs/cautions: a distribution change (new native binaries in the CLI package
and the VS Code extension); `prepare-binaries.ps1` must explicitly copy the
dlls — remember the 1.6.4 ONNX packaging bug, where exactly this step was
missed; needs x64/arm64 coverage decisions; Rob was not comfortable adopting
this without a dedicated review — **propose it, don't presume it.**

Revisit triggers: the suppression blink returns for Codex; another CLI starts
exhibiting conhost-artifact rendering; the stacked-repaints bug warrants a real
fix; or "external terminal flickers" becomes a complaint that matters.

---

## 12. Tooling and reproduction

- **`python-scripts/analyze_cursor_state.py`** (in tree) — the diagnostic that
  cracked the byte-level picture. Per chunk: DECTCEM + DEC-2026 state,
  approximate cursor row, chunk "shape"; per session: visibility flips,
  visible-row moves, and the decisive **renderable-boundary** metrics. Works on
  live sessions (`analyze_cursor_state.py <session-id>`) and on raw capture
  files (`--jsonl <file>`). Rule of thumb: row oscillation at *all* boundaries
  but not at *renderable* boundaries = invisible to users; oscillation at
  renderable boundaries = a visible hop.
- **PTY capture harness** (`tools/pty-capture/` — one-off; source removed after
  the investigation, leftover `bin/`/`captures/` are local-only and
  `captures/` is gitignored). Design, if it ever needs rebuilding: a ~150-line
  console app referencing `Pty.Net`, spawning `powershell.exe -NoLogo` at
  171×27 with a sanitized env (strip `TERM*`, `WT_*`, `VSCODE_*`, `COLORTERM`;
  add `LANG`/`LC_ALL`/`PYTHONIOENCODING` to mirror production), typing
  `codex\r` after 2 s, recording every `ReaderStream.ReadAsync` (4096-byte
  buffer, matching production) as JSONL `{t: ms, b64: bytes}` for ~14 s. The
  conpty.dll A/B variant additionally needs the §11 Pty.Net resolver override.
- **30-second visual repros:** (a) focused 1× replay of Codex session
  `33ee4a66-8841-4e3b-9eb0-500ea4838653`; (b) VS Code with
  `windowsUseConptyDll: false` running codex; (c) any Codex session in a
  VibeRails terminal on a build without the Codex-scoped suppression.

Reference sessions in `state.db`: `4026ff95` (pre-1.7.3 Codex), `33ee4a66`,
`c3c1a69e` (post-1.7.3 Codex), `c8bac352` (Claude contrast), `750e672f` (the
blink quantification).

---

## 13. Verification gate (for the 2026-06-11 fix, and after any future change here)

1. Codex tab, thinking: no cursor hop; cursor hidden while streaming, reappears
   at the composer ≤90 ms after output settles.
2. Claude tab, streaming: no blink; the `vb-terminal-cursor-suppressed` class
   must never appear on a Claude tab.
3. Reload the page/webview mid-Codex-session: suppression still engages after
   reconnect (`state.cli` hydrated from the tabs list).
4. Shell tab: behavior unchanged.
5. Mac: unchanged (gate is harmless there; the hop never existed).

---

## 14. References

- Codex source: `codex-rs/tui/src/custom_terminal.rs` (`try_draw` — the
  show-before-move order), `tui.rs` (`sync_update`), `status_indicator_widget.rs`
  (32 ms tick), `terminal-detection/src/lib.rs` (env sniffing, none of it
  cursor-related), config `tui.animations`.
- Upstream issues: openai/codex#9081 (closed not-planned), openai/codex#11063
  (open).
- VS Code: PR #315951 (conpty.dll default-on, 1.121), PR #282835 (A/B rollout),
  PR #301398 (conpty 1.25), issue #224488; `terminalConfiguration.ts`
  (`windowsUseConptyDll`, default `true`, dll v1.25.260303002).
- Our log: `TERMINAL.md` entries "## 2026-06-09" (the blink), "## 2026-06-10"
  (the bytes), "## 2026-06-11" (root cause + fix); commits `3ea2f30` (v1.6.0
  suppression), `d1d273d` (1.7.3 removal).

---

## 15. Lessons

1. **Name the middleman.** Every hypothesis for months lived at the endpoints —
   our renderer, our pipeline, Codex's bytes. The component that actually
   caused it (ConPTY) rewrites every byte and appeared in none of our diagrams.
   On Windows, "the bytes the app emitted" and "the bytes we received" are
   different streams; forensics must say which one they're looking at.
2. **Two bugs under one name resist diagnosis.** The blink and the hop each had
   clean, simple causes. As "the flicker," they were unsolvable, because every
   experiment produced contradictory evidence (fixing one revealed the other).
   Splitting the symptom table (§3) was the turning point.
3. **A user's 5-minute experiment can outweigh a day of byte forensics.** Rob's
   `windowsUseConptyDll` flip and the Mac test were single-variable experiments
   that confirmed/falsified more than any amount of stream analysis. When a
   differential observation exists ("clean here, broken there"), chase the
   *difference between the two paths* before theorizing within one of them.
4. **"Renderable" is the metric, not "present in the stream."** Both conhosts
   emit the transient cursor states; only one makes them paintable. Counting
   raw escape sequences misled; counting *sync-closed commit states* resolved
   it instantly.
5. **Masking mitigations age into bugs — but scoped masks are legitimate.** The
   v1.6.0 suppression was a global mask with an unexamined side effect and it
   eventually became the bug. The 2026-06-11 revival is the same code, but
   scoped to the one CLI whose cadence provably tolerates it, with the failure
   mode documented and a tripwire (§10) for when to stop masking and fix the
   source.
6. **When a fix's justification is a generalization ("CLIs manage their own
   cursor"), test it per-case.** It held for the CLI we were staring at and
   failed for the other one.
