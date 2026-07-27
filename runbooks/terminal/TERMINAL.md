# TERMINAL.md

> ## 🟡 Open — awaiting Rob's test (2026-07-26)
>
> Two bugs found in session `71dee36a-caf6-42e1-b245-1625b39a9221` (GLM 5.2).
> Both are **root-caused with byte evidence, fixed, and unit-tested — but not
> verified in Rob's env.** Both fixes are server-side (`vb.exe`), so they need a
> rebuilt/reinstalled VS Code extension. **Rob tests, Rob marks them closed.**
>
> | Bug | Cause in one line | Entry |
> |---|---|---|
> | GLM 5.2 wheel acts like a held up-arrow; composer stuck cycling old prompts | Our reconnect snapshot prologue disables mouse tracking and never restores it → xterm.js falls back to alt-scroll and emits cursor-up/down | "## 2026-07-26 GLM 5.2 wheel…" |
> | `__resize__:171,4` typed into the TUI input | A 4-row fit fails the `rows >= 5` bound, and a control frame that fails to parse falls through to PTY stdin as keystrokes | "## 2026-07-26 `__resize__:171,4`…" |
>
> They are unrelated in cause but showed up together because both need a squashed
> panel / a reconnect — i.e. the same afternoon of dragging things around.
> Neither is a regression from recent terminal work: the resize fall-through has
> been there since `6d028c3`, and the mouse-mode hole since the prologue was
> written (its `?2004` twin was fixed 2026-06-15, the mouse half was missed).

> **Rob's note (2026-06-09): the long-running cursor flicker is FIXED — and the
> cause was OURS, not the CLI and not the env var.** Shipped in 1.7.3 (`d1d273d`).
> This is the sneaky one that plagued **Codex for months** and recently started
> showing in **Claude** too.
>
> **Symptom:** the text cursor blinks/flickers continuously (~3×/sec) in the live
> terminal while a CLI is producing output. NOT `cursorBlink` (it's `false`), NOT
> the hidden helper-textarea caret (already killed via `caret-color: transparent`),
> NOT the `CLAUDE_CODE_FORCE_SYNC_OUTPUT` env var.
>
> **Cause:** our own `suppressCursorDuringOutput()` in `terminal-tab.js`, called on
> *every* WebSocket output flush. It hid the cursor (transparent theme +
> `.vb-terminal-cursor-suppressed` CSS) and armed a **90 ms** timer to restore it.
> CLIs emit redraws in bursts with gaps that straddle 90 ms (spinner/status at
> ~10–15 Hz with jitter), so the timer fires in each gap → cursor flashes **ON** →
> the next chunk hides it again → **OFF**. That on/off cycle is the flicker.
> Measured **66 cycles in a 22 s Claude session** (`750e672f`) ≈ 3/sec. It shipped
> **2026-04-16** in the "codex fixes" commit (`3ea2f30`, present since **v1.6.0**) —
> it is the *"small residual flicker"* the **"Codex: Status-line cursor flash"**
> entry below shrugged off.
>
> **Fix:** delete the single `suppressCursorDuringOutput()` call. CLIs already
> bracket their own redraws with DECTCEM (`\e[?25l` / `\e[?25h`), which xterm
> renders atomically, so the app-layer suppression was redundant *and* the cause.
>
> **`CLAUDE_CODE_FORCE_SYNC_OUTPUT` was a red herring — do not re-chase it.** Two
> clean falsifiers: (1) **v1.6.15 never set the env var** (added at v1.7.0) **and
> still flickered** (confirmed on a second machine); (2) **replaying the exact same
> bytes** (BSU/ESU pairs and all) through the session viewer **never flickers**.
> The flicker lives in the live render path, not the byte stream. A parallel
> session's env-var-removal "fix" is parked in a local git stash, unmerged — drop it.
>
> **What cracked it:** session **replay never flickers, even with a focused/visible
> cursor**, because `session-viewer.js` never calls `suppressCursorDuringOutput()`.
> Same bytes, no suppression → no flicker. Full writeup + the follow-up cleanup
> (rip out the dead suppression layer and the user-facing cursor-style options) are
> in the **"## 2026-06-09"** entry below.
>
> **⚠️ Update 2026-06-10/11 — half superseded.** The blink fix above stands, but
> removing the suppression **unmasked the ORIGINAL Codex status-line cursor hop**.
> Root cause (confirmed 2026-06-11): the **in-box Windows ConPTY** re-emits
> Codex's transient mid-frame cursor states outside the `?2026` sync brackets;
> the modern conpty.dll (VS Code ≥1.121) does not — that's why VS Code's terminal
> is clean. Fix shipped: the suppression call is BACK but **gated to Codex tabs
> only** (Codex's ~32 ms cadence stays under the 90 ms timer, so the blink that
> killed it for Claude doesn't manifest). The "CLIs already bracket their own
> redraws with DECTCEM" claim is **Claude-only**. The follow-up cleanup of the
> suppression layer is **CANCELLED** — the machinery is in active use again
> (Codex-scoped). Status summary: the **"## 2026-06-11"** entry. **Full saga
> write-up (canonical): [`TERMINAL-FLICKER.md`](TERMINAL-FLICKER.md).**

> **Rob's note (2026-05-21):** Overall happy with where the terminal sits
> right now. The stacked-repaints situation is well-understood, the
> diagnosis below holds up, and day-to-day terminal work is not being
> impacted. Nothing on the open-bug list feels urgent.
>
> **One change is on the table.** The
> `RESIZE_SYNC_DEBOUNCE_MS = 140 → 2000` bump in `terminal-tab.js`,
> plus `CLAUDE_CODE_FORCE_SYNC_OUTPUT=1` for Claude in
> `CommandService.cs`, were originally written on the `cron` branch
> alongside that work. They were cherry-picked into `main` on 2026-05-21
> (commit `f20ba34`) so the code is now sitting in the tree — but **this
> must not ship as default behavior.** The 2 s debounce has a real UX
> cost: Claude Code only learns the new terminal size ~2 s after the
> user stops dragging, so a fast drag-then-type still feels off until
> the size settles. That trade-off needs to be opt-in.
>
> **Update 2026-05-21 (later):** The flag has landed in the per-terminal
> settings panel (Terminal Settings → Rendering → "Resize debounce"),
> backed by the `viberails_terminal_resizeDebounce` localStorage key.
> Default is `"default"` (140 ms, pre-fix behavior); the experimental
> `"extended"` value selects 2 s. `terminal-tab.js` resolves the value
> per resize via `resolveResizeDebounceMs()`, so toggling the dropdown
> takes effect on the next resize — no tab restart needed.
>
> `CLAUDE_CODE_FORCE_SYNC_OUTPUT=1` is intentionally **not** gated.
> Per session `2c93b090` analysis it is currently harmless theatre
> (Claude Code 2.1.142 emits empty BSU/ESU pairs), and it costs
> nothing to leave on so upstream gets a free win the day Claude
> Code's wrapping is fixed.
>
> The diagnostic test (`Session_2c93b090_StackedRepaintsDiagnostic.cs`)
> and the env-var pin test (`CommandServiceTests.cs`) are fine to keep
> as-is regardless of how the flag lands — they pin code-level
> contracts, not user-facing defaults.
>
> See the 2026-05-15 entry below for the full diagnosis. That writeup
> is still the canonical reference for what the bug is and why this
> approach works at the trigger layer.

> **Rob's note (2026-05-15, late):** The "stacked repaints" bug is
> **understood and fixed at the trigger layer.** Real symptom is N
> persistent copies of Claude Code's UI on screen, **does not
> self-correct**, primary trigger is **rapid/sustained resize events**
> from slow panel-drags (each unique cols×rows reaching the PTY fires
> a SIGWINCH → Claude Code repaints → xterm.js renders the paint mid
> non-quiesced reflow → stacked copies persist). Fix: bumped
> `RESIZE_SYNC_DEBOUNCE_MS` in `terminal-tab.js` from **140 ms → 2000 ms**
> so the outgoing `__resize__` only fires once the user has been
> quiet for 2 s. Local xterm fit stays responsive at 100 ms so the
> on-screen terminal still tracks during the drag — only the server
> SIGWINCH is gated. The underlying xterm.js-side reason that the
> same byte stream produces a clean single banner in our C# emulator
> but stacked copies in xterm.js live-render is **still open** — the
> 2 s gate suppresses the trigger condition (N rapid SIGWINCHes) so
> the live-render bug can't manifest, but it doesn't cure the
> live-render bug itself. See "Why 2 s, why not fix xterm.js" below.
>
> **Bug name going forward: "stacked repaints during drag-resize"** (or
> just "stacked repaints" when context is clear). Avoid: "reprint"
> (implies single duplicate; reality is N≥2), "flicker"/"flash"
> (imply transience; symptom is persistent), "resize bug" alone
> (multiple things called that — qualify with "stacked").
>
> The DEC 2026 `CLAUDE_CODE_FORCE_SYNC_OUTPUT=1` env var from earlier
> today **stays in `CommandService.cs`** but is **not the fix.**
> Session `2c93b090` proved Claude Code 2.1.142 emits sync sequences
> as empty `\e[?2026h\e[?2026l` no-op pairs without bracketing the
> actual redraws, so xterm.js's deferred-render path activates around
> nothing. Harmless to leave in (and may help once Claude Code's
> wrapping is fixed upstream), but it's belt-only — not the
> suspenders. Full triage below.

## 2026-07-26 `__resize__:171,4` typed into the OpenCode composer — reserved control frame falls through to the PTY when rows < 5

**Status:** 🟡 **FIX WRITTEN — AWAITING ROB'S TEST. Do not mark closed until Rob
confirms** (needs a rebuilt `vb.exe` / VS Code extension; the server half ships in
`vb.exe`). Test steps at the bottom of this entry. Latent since the file was
created (`6d028c3`) — **not** a recent regression, which is why nothing in the
recent terminal work explains it.

**Symptom (Rob, 2026-07-26, GLM 5.2 tab):** while resizing, the literal text
`__resize__:171,4` appears typed into the TUI's input box.

**Root cause.** *(All line numbers in this section are **pre-fix** coordinates —
they describe the broken state. The fix added ~50 lines, so current lines sit
below these.)* `TerminalControlProtocol.TryParseResizeCommand`
(`TerminalControlProtocol.cs:54`) validates the payload:

```csharp
return cols is >= 10 and <= 1000 && rows is >= 5 and <= 500;
```

`rows = 4` fails `rows >= 5`, so the method returns **false**. The caller
(`TerminalSessionService.cs:456-474`) treats "didn't parse" as "not a control
message" and falls through to `TerminalIoRouter.RouteInputAsync(...)`
(`TerminalSessionService.cs:493`), which does
`terminal.WriteBytesAsync(inputBytes)` (`TerminalIoRouter.cs:129`) — the literal
string is written to **PTY stdin as keystrokes**. OpenCode's composer echoes it.

The client has no guard either: `sendResizeToPty` (`terminal-tab.js:468-490`)
sends whatever `vibeTerminal.cols/rows` says, unclamped.

**Byte evidence — session `71dee36a-caf6-42e1-b245-1625b39a9221`:**

```
CHUNK 26179346 @ 2026-07-26T13:24:37.2988921Z (len=54)
  \e[m\e[38;2;201;209;217m\e[48;2;22;27;34m__resize__:171,4
CHUNK 26181373 @ 2026-07-26T13:25:44.5500371Z (len=51)
  \e[38;2;201;209;217m\e[48;2;22;27;34m__resize__:171,4
```

Corroboration: `TerminalSessionLogs` geometry for that session is
`163x26 → 133x26 → 163x26 → 133x26 …` — **`171x4` never appears**, because
`ApplyResize` was never reached. The PTY stayed at 163x26 while the client's
`lastResizeSignature` was set to `171x4` (it is assigned *before* `socket.send`,
`terminal-tab.js:488`), so the local xterm and the PTY were also genuinely
desynced until the next fit.

**How you get 171x4:** VS Code panel dragged down to a sliver while the sidebar is
collapsed (wide + very short). 4 rows is a real fit result, not a transient
measurement artifact — it was sent twice, a minute apart.

**Same bug on the remote path:** `RemoteTerminalConnection.cs:271-283` uses the
same parser and the same fall-through (`OnInputReceived?.Invoke(inputBytes)`).

### What we're trying (applied 2026-07-26) — both halves; neither alone is enough

1. **Server, the safety net — a reserved prefix never reaches the PTY.**
   New `TerminalControlProtocol.IsReservedControlFrame(input)` → true for
   `__resize__:` and `__cmd__:` (the two prefixes that carry a payload and can
   therefore *fail validation*; `__cmd__:` with an invalid command name falls
   through today too). Checked **after** the parse attempts and **before**
   routing, in both input loops — `TerminalSessionService.WebSocketInputLoopAsync`
   and `RemoteTerminalConnection`'s receive loop — which log a sanitized,
   truncated warning (`SanitizeFrameForLog` strips control chars; a malformed
   frame can carry ANSI escapes, which must not land raw in the log) and
   `continue`. Extended after review to **all** reserved frames, not just the two
   payload-carrying prefixes: a malformed variant of an exact-match frame
   (`__replay__x`, or a `__PIN__:` arriving after the handshake) would otherwise
   still fall through — and that last one would type the PIN into the TUI. No
   current client emits any of those; it's belt-and-suspenders on a failure mode
   not worth leaving open.
2. **Client, so the resize still happens — don't send what will be refused.**
   `sendResizeToPty` (`terminal-tab.js`) now returns early on geometry outside the
   protocol's `cols 10..1000 / rows 5..500`. It returns **before**
   `lastResizeSignature` is assigned, so the signature stays at the last good
   geometry and the real size still sends once the panel is a usable height
   again. Mirrors the existing `preConnectDimsLookSane` guard on the WS-URL hint
   path.

**Deliberately NOT done: widening the bounds to `rows >= 1`.** Refusing a 4-row
PTY is reasonable; the bug is the fall-through, not the validation. Widening would
hide this instance and leave every other malformed-frame path leaking.

**Tests:** new `Tests/Services/Terminal/TerminalControlProtocolTests.cs` (24 cases)
— in-range accept, out-of-range/malformed reject (including the literal
`__resize__:171,4`), `IsReservedControlFrame` claiming every reserved prefix even
with an invalid payload while leaving real input (`ls -la`, `\e[A`,
`echo __resize__:171,4`) alone, and the exact broken pairing: parse fails **and**
frame is reserved. `Tests.Services.Terminal` 94/94 (covers both 2026-07-26 fixes).
The client-side guard has **no unit coverage** (there's no `terminal-tab.js` test
harness — only `terminal-tab-status`); it's verified by Rob's manual test below.

### Rob's test → then mark this entry closed

1. Rebuild + reinstall the extension (server half lives in `vb.exe`).
2. Open an OpenCode or GLM 5.2 tab.
3. Drag the VS Code panel divider **down to a sliver** (a few rows tall), pause,
   drag it back up. Repeat a few times, including with the sidebar collapsed —
   that's the 171-col-wide × 4-row fit that triggered it.
4. **Pass:** nothing appears in the composer, and the TUI ends up correctly sized
   after the panel is restored.
5. **Fail:** `__resize__:<cols>,<rows>` still shows up in the input box → the
   client guard didn't fire; check whether the server logged
   `Dropped malformed control frame` (server half working, client half not).

---

## 2026-07-26 GLM 5.2 wheel acts like a held up-arrow — the snapshot prologue turns mouse tracking OFF and never turns it back on

> **Supersedes the root cause in the 2026-07-21 entry below.** That entry says
> *"mouse tracking stays on the whole session, so this is a routing/state issue,
> not a mode toggle."* That is true for the session it was written from
> (`8d181d25`) and **false in general.** There is a second, VibeRails-caused
> variant where mouse tracking is genuinely off, and the 2026-07-21 fix cannot
> reach it. The shipped translation is still correct — keep it.

**Status:** 🟡 **FIX WRITTEN — AWAITING ROB'S TEST. Do not mark closed until Rob
confirms** (server-side, so it needs a rebuilt `vb.exe` / VS Code extension).
Test steps at the bottom of this entry. Same bug class as the Shift+Enter
bracketed-paste-on-reconnect bug (2026-06-15) — *same prologue line*, different
mode. That fix restored `?2004` and left the mouse modes behind.

**Symptom (Rob, 2026-07-26, GLM 5.2):** "sometimes when I scroll up on the mouse
wheel it acts like an up arrow and scrolls really fast through my old messages…
like the cursor gets stuck in the input box and I can not scroll up in the text
area of the TUI."

**Why the 2026-07-21 fix doesn't cover it.** `_translateOpenCodeMouseWheel`
(`terminal-tab.js:141-165`) rewrites `\e[<64;…M` / `\e[<65;…M` → PageUp/PageDown.
In this variant **xterm.js never emits an SGR mouse event at all**, so the
pre-filter `data.indexOf('\x1b[<6') === -1` returns early and the translation is
a no-op. What reaches the socket is a literal cursor-key sequence.

### The chain

*(Line numbers below are **pre-fix** coordinates describing the broken state;
the fix shifted them.)*

1. **Every** WebSocket attach — page reload, VS Code webview re-init, socket
   reconnect, tab re-attach — calls `terminal.SubscribeWithSnapshot(wsConsumer)`
   (`TerminalSessionService.cs:209`). No CLI gating, no reconnect-only gating.
2. That serializes a snapshot whose prologue
   (`TerminalGridSerializer.AppendSnapshotResetPrologue`, **`TerminalGridSerializer.cs:117`**)
   emits:
   ```
   \e[?1;6;1000;1002;1003;1004;1005;1006;1007;1015;2004;2026l
   ```
   → mouse tracking **OFF** in xterm.js.
3. The serializer then restores exactly two of those modes — `?2004h`
   (`:42-45`, the 2026-06-15 fix) and `?1049h` (`:51`, alt screen). **Mouse
   tracking is never restored.** `AnsiParser.cs:418-419` only tracks `2004` and
   `2026`, so the emulator doesn't even know the mouse modes were on.
4. opentui enables them **once at TUI startup** and does not re-assert on
   SIGWINCH (proof below), so nothing brings them back.
5. xterm.js now has: alt buffer (no scrollback) + no active mouse protocol →
   its **alt-scroll fallback** fires. From the bundled `xterm.min.js`:
   ```js
   addDisposableListener(t,"wheel",t=>{ if(!s.wheel){                      // no mouse protocol
     if(this._customWheelEventHandler&&!1===this._customWheelEventHandler(t))return!1;
     if(!this.buffer.hasScrollback){                                       // alt screen
       if(0===t.deltaY)return!1;
       if(0===e.coreMouseService.consumeWheelEvent(...))return this.cancel(t,!0);
       const i=ESC+(this.coreService.decPrivateModes.applicationCursorKeys?"O":"[")+(t.deltaY<0?"A":"B");
       return this.coreService.triggerDataEvent(i,!0),this.cancel(t,!0)}}},{passive:!1})
   ```
   One wheel notch → `\e[A` / `\eOA`. `consumeWheelEvent` accumulates deltas, so
   a fast spin emits a burst — Rob's *"scrolls really fast."*
6. Those arrows hit OpenCode's focused textarea → `prompt.history.previous` →
   old prompts cycling, composer stuck. **Sticky for the rest of the session.**
   `?1007` (alternate scroll mode) is in the prologue's reset list but
   **xterm.js does not implement 1007 at all** — resetting it is a no-op and does
   not disarm the fallback.

### Byte evidence — session `71dee36a-caf6-42e1-b245-1625b39a9221` (glm-5.2, 17 min)

Whole-session DEC private mode tally: `?1000/1002/1003/1006` **SET = 2,
RESET = 0** (nothing in the PTY stream ever disables them — the disable comes
from *us*, and it is not in `SessionLogs` because the snapshot is generated
server-side, not read from the PTY).

```
CHUNK 26179241 @ 13:16:41.9905910Z   \e[?1000h\e[?1002h\e[?1003h\e[?1006h   ← TUI startup, once
CHUNK 26179330 @ 13:24:12.5652921Z   \e[?1000h\e[?1002h\e[?1003h\e[?1006h\e[?1004h\e[?2004h\e[>4;1m
```

**The decisive fact:** `TerminalSessionLogs` records **10 geometry changes after
13:24:12** (13:24:24, 13:24:27, 13:24:48, 13:24:51, 13:25:58, 13:26:01,
13:33:39 ×2, 13:33:49, 13:33:52) and **zero** further mouse-mode emissions.
**opentui does not re-assert mouse tracking on SIGWINCH.** So resizing does not
heal it, and there is no natural recovery path — exactly matching "I can not
scroll up in the text area."

### What we're trying (applied 2026-07-26) — track and restore, exactly the 2026-06-15 `?2004` recipe

> **⚠️ Model the effective state, not the enables.** The first cut of this fix
> kept a `SortedSet<int>` of every mode ever enabled and replayed it ascending.
> **That is wrong, and the "1000/1002/1003 are cumulative levels" claim that
> justified it is false.** In xterm.js `CoreMouseService.activeProtocol` is a
> *single* value — `case 9/1000/1002/1003` each **overwrite** it, and DECRST of
> **any** of them sets `NONE` (verified in the bundled `xterm.min.js`). Two real
> failures followed: an app that went `?1003h` then `?1002h` (drag) got replayed
> as `1002;1003h` and came back on **any-event**; and `?1000l` removed one member
> of the set while leaving the others, resurrecting tracking the app had turned
> off. Encoding (`?1006`) is a second independent single value; `?1005`/`?1015`
> are **not implemented by xterm.js at all** (it logs and ignores them), so
> tracking them buys nothing. `?1004` (focus) is genuinely independent.

5 files. Our mode state now mirrors xterm.js's model exactly — that is the whole
design rule, since the only consumer of this state is the snapshot we replay
*into* xterm.js:

1. **`TerminalBuffer.cs`** — `_mouseProtocolMode` (0 = off, else 9/1000/1002/1003,
   last-wins), `_mouseEncodingMode` (0 = default, else 1006), `_focusReportingActive`.
   `GetInputReportingModes()` returns the *effective* modes in emit order
   (protocol, encoding, focus) as a fresh array, so it's safe to read outside the
   emulator lock.
2. **`AnsiParser.cs`** — `case 9/1000/1002/1003` → `SetMouseProtocol(enable ? mode : 0)`
   (so a reset of any one clears tracking); `case 1004` → `SetFocusReporting`;
   `case 1006` → `SetMouseEncoding`; `1005`/`1015` stay deliberate no-ops.
3. **`TerminalEmulator/Terminal.cs`** — `GetInputReportingModes()` passthrough, and
   `Reset()` now clears these modes (plus `?2004`/`?2026`).
4. **`Pty/Terminal.cs` `CaptureSnapshotLocked`** — read inside `_emulatorLock`,
   pass to `Serialize`.
5. **`TerminalGridSerializer.Serialize`** — new optional
   `IReadOnlyList<int>? inputReportingModes`; re-emits them as one DECSET
   (for opentui: `\e[?1003;1006h`) right after the `?2004h` restore, so it lands
   **after** the prologue reset.

**Also fixed while in here — RIS/`Reset()` didn't clear any of this.**
`AnsiParser.FullReset` (RIS, `ESC c`) and `Terminal.Reset()` now clear the mouse
modes *and* `?2004`/`?2026`, which they never did. Real terminals drop all of
these on RIS; without it, a `reset` after a TUI crash left the emulator claiming
modes the app no longer had, and the next snapshot re-enabled mouse reporting
into a plain shell — wheel ticks would type SGR reports into its stdin. Not a
regression from this work (the `?2004`/`?2026` half was always missing), but the
same one-line class of bug.

**Tests** (`Tests/Services/Terminal/TerminalGridSerializerTests.cs`, 8 new):
round-trip restore through a second emulator; the one-DECSET byte shape *and* its
ordering vs the prologue; **last-wins protocol** (`?1003h ?1002h` must replay as
`1002`, never `1003`); **any single protocol DECRST kills tracking**; no spurious
restore for a plain shell; set/reset tracking including the prologue's own reset
string; RIS clears; `Reset()` clears.

Suite status: `TerminalEmulator.Tests` 164 passed / 9 skipped,
`Tests.Services.Terminal` 94/94 (covers both 2026-07-26 fixes).

### Rob's test → then mark this entry closed

1. Rebuild + reinstall the extension (the fix lives in `vb.exe`).
2. Open a GLM 5.2 tab, send a few prompts so there's chat history to scroll.
3. **Force a re-snapshot** — this is the step that used to break it. Reload the
   webview, or switch to another tab and back, or let the tab idle past the ~2 min
   WS timeout. (Before the fix, *this* is the moment mouse tracking died.)
4. Wheel-scroll over the chat.
5. **Pass:** the chat scrolls, and the composer does not cycle old prompts.
6. **Fail:** old prompts still cycle → DevTools breakpoint on `onData` in
   `terminal-tab.js` and read what the wheel produces:
   - `\e[<64;…M` → mouse tracking IS restored; you're hitting the *other* variant
     (2026-07-21 upstream hit-test) and the PageUp translation should be handling
     it — check `state.cli` is one of `opencode`/`glm-5.2`/`kimi-k3`.
   - `\e[A` / `\eOA` → mouse tracking still lost; this fix didn't take.
7. Worth also re-checking Shift+Enter → newline in the same reconnected tab, since
   it rides the adjacent `?2004` restore in the same serializer block.

**Do NOT "fix" it by deleting the mouse modes from the prologue's reset list.**
The prologue's job is to make the snapshot self-contained — a viewer holding
stale modes from a previous state must be reset. Track-and-restore is the correct
shape; unconditional-leave-on is not.

**The general rule this leaves behind:** every DEC private mode in the prologue's
reset list must either be tracked+restored or be one a CLI re-asserts on its own.
`?1` (DECCKM) and `?6` (DECOM) are still reset-without-restore and still untracked
— if a "works on a fresh tab, breaks after reconnect, never recovers" bug shows up
around arrow-key encoding or origin mode, that's where to look first.

**Guardrails:** no byte-stream stripping (this is snapshot *generation*, not
filtering a live stream); keep `_translateOpenCodeMouseWheel` (it covers the
upstream hit-test variant, which is a different failure); `?2027`/`?2031`
(opentui grapheme/color-scheme modes) are not in the reset list and are unaffected.

**Discriminating the two variants when Rob reports "wheel scrolls history":**
did it start right after a page reload / webview re-init / reconnect, and does it
never recover? → **this bug** (mouse tracking off, no SGR bytes on the wire).
Did it come and go mid-session with no reconnect? → the 2026-07-21 upstream
hit-test variant. A DevTools breakpoint on `onData` settles it instantly:
`\e[<64;…M` = upstream variant, `\e[A`/`\eOA` = this one.

---

## 2026-07-21 OpenCode mouse wheel cycles input history instead of scrolling chat — FIXED

> **⚠️ Partly superseded 2026-07-26.** The fix below is correct and stays, but the
> root-cause claim *"mouse tracking stays on the whole session"* holds only for
> session `8d181d25`. A second variant — **VibeRails' own snapshot prologue
> disabling mouse tracking on reconnect** — produces the same symptom with no SGR
> mouse bytes at all, so this translation never fires. See the 2026-07-26 entry
> above.

**Status:** FIXED. VibeRails-side translation in `terminal-tab.js`; upstream bug
remains open at [anomalyco/opencode#35295](https://github.com/anomalyco/opencode/issues/35295).

**Symptom (Rob, on 1.8.6, Windows 11 + VS Code extension):** scrolling the mouse
wheel over an OpenCode tab intermittently cycles through input history (showing
previously-sent prompts in the input box) instead of scrolling the chat viewport.
Once it switches to the broken state, it sticks — the user can't scroll the chat
at all until the session is restarted. Sometimes it works fine for a whole session.

**Root cause (upstream):** OpenCode's TUI is built on `opentui`, which enables SGR
mouse tracking (DECSET 1000/1002/1003/1006 — confirmed in session
`8d181d25-a5b8-462e-b7dc-d9ac910d00c1`, zero `\e[?1000l` disables across 190k
chunks) and routes wheel events via hit-testing to whatever renderable is under
the cursor. The `<scrollbox>` (messages) receives them when the cursor is over
the chat; the `<textarea>` (input) receives them when over the input. The textarea
binds `prompt.history.previous`/`next` to up/down arrow keys, and once it grabs
wheel focus it sticks — producing the "can't get it out of the input area" lock-in.

**Why `mouse: false` in `tui.json` is NOT the fix:** the OpenCode docs claim it
"preserves the terminal's native mouse selection/scrolling behavior," but in
alternate-screen mode the terminal's native wheel fallback is to emit up/down
arrow-key sequences — which hit the focused textarea and cycle input history
*every time*, not just intermittently. That's strictly worse. (Confirmed by the
upstream issue and the opentui renderer source.)

**Fix (VibeRails-side, `terminal-tab.js`):** for OpenCode tabs (and the
OpenCode-backed pseudo-CLIs Glm52/KimiK3, whose `LlmParser.ToWireName()` wire
names are `'glm-5.2'` and `'kimi-k3'` — with hyphens, not the enum names),
translate SGR mouse wheel events to PageUp/PageDown in the `onData` input path,
before `socket.send`. OpenCode binds PageUp/PageDown to
`messages_page_up`/`messages_page_down` (and `dialog.select.page_up`/`page_down`
when a dialog is open), so the wheel always scrolls the chat regardless of where
opentui's hit-test routes the mouse event.

```js
_translateOpenCodeMouseWheel(data) {
    const cli = (this.state.cli || '').toLowerCase();
    if (cli !== 'opencode' && cli !== 'glm-5.2' && cli !== 'kimi-k3') {
        return data;
    }
    if (typeof data !== 'string' || data.indexOf('\x1b[<6') === -1) {
        return data;
    }
    return data
        .replace(/\x1b\[<64;\d+;\d+M/g, '\x1b[5~')   // wheel up  → PageUp
        .replace(/\x1b\[<65;\d+;\d+M/g, '\x1b[6~');  // wheel down → PageDown
}
```

**Scope/guardrails:**
- **OpenCode/Glm52/KimiK3 only.** Other CLIs (Claude, Codex, Copilot,
  Antigravity) handle mouse wheel correctly; do not extend this to them. Gated
  on `state.cli` lowercase, same pattern as the Codex cursor-suppression gate
  (`terminal-tab.js:691`).
- **Wire name gotcha (bit us once):** `state.cli` carries the
  `LlmParser.ToWireName()` value, not the enum name. Glm52/KimiK3 serialize as
  `'glm-5.2'`/`'kimi-k3'` (with hyphens), NOT `'glm52'`/`'kimik3'`. The first
  iteration of this fix checked the enum names and silently no-op'd for every
  GLM 5.2 session (e.g. `00e400f8-4f4d-4071-92c3-73b556d22e68`). Always check
  the wire names — see `LlmParser.cs:55-66`.
- **Only SGR wheel events (button 64/65) are translated.** Clicks, drags, and
  other mouse buttons pass through untouched — mouse selection and click-to-focus
  still work normally.
- **No `setTimeout`, no receive-path changes, no byte-stream stripping on
  output.** This is input-path only (xterm.js → VibeRails → PTY), before the
  bytes hit the socket. The output path is untouched.
- **Pre-filter** (`data.indexOf('\x1b[<6') === -1`) avoids running the regex on
  every keystroke; only data containing a wheel-event prefix pays the cost.
- `_trackTypingForNudge` and `statusController.onTerminalData` both already
  ignore escape sequences (control-char filter / single-printable-byte check),
  so they see the original `data` unchanged — no behavior change for typing
  detection or status transitions.

**Trade-off:** mouse-position-based scrolling is lost (wheel always scrolls the
chat, even when hovering over the input textarea). Acceptable because (a) the
native behavior was already broken intermittently, (b) the input textarea rarely
needs wheel scroll (multi-line inputs are the only case, and arrow keys work),
(c) OpenCode's `messages_page_up`/`page_down` keybinds are the documented way to
scroll the chat — we're just routing the wheel to them.

**Verification:** session `8d181d25-a5b8-462e-b7dc-d9ac910d00c1` is a "good"
session (scroll worked) — mouse tracking modes 1000/1002/1003/1006 confirmed
active from chunk 23729882 onward. The fix is input-side only, so it doesn't
change the byte stream captured in SessionLogs; verify by scrolling in a fresh
OpenCode tab after the fix and confirming the chat scrolls (not the input
history).

**Upstream tracking:** [anomalyco/opencode#35295](https://github.com/anomalyco/opencode/issues/35295)
(open as of 2026-07-21). If opentui fixes the hit-test routing, this translation
becomes a no-op (wheel events would still work via PageUp/PageDown) and can stay
in place. Do not remove it until the upstream fix is confirmed across both
Windows (ConPTY) and macOS/Linux.

## 2026-06-13 Small typing-echo lag while the CLI is streaming — OPEN, watching (do NOT fix yet)

> **Status: OPEN / observation phase.** Rob's call (2026-06-13): pin down *when*
> this happens before attempting a fix. No code change yet. This entry is a
> watch-list — log every recurrence against the checklist at the bottom.

**Symptom (Rob, on 1.7.5):** a *small* lag on typed characters that shows up **only
while the child CLI is actively producing output** (e.g. typing a follow-up message
while the agent streams its reply). When the CLI is idle, typing is crisp.

**This is NOT the old lag.** The historical "unusable typing" lag was the cold-start
`setTimeout` occlusion-throttle (Chromium clamps `setTimeout` to 1 s in an occluded
webview), fixed in **1.6.12** by moving the xterm coalesce to `queueMicrotask` (see
the `project_cold_start_settimeout_throttle` memory). That one was 1–3 s per
keystroke and present from boot. This new one is **small (tens of ms),
output-correlated, and only while streaming.** Different bug.

**Rob's anchor:** "1.7.2 felt clean, 1.7.5 has it." Treat as a hint, not a fact —
the archaeology below does *not* (yet) find a VibeRails delta that explains it.

### What actually changed on the input/output hot path, 1.7.2 → 1.7.5

Version → SHA: 1.7.2 `4592026` · 1.7.3 `0ac9253` (`d1d273d` flicker fix) ·
1.7.4 `ff37730` · 1.7.5 `0335cc4` (`d96d646` codex-flicker revival).

The two hot paths (`terminal-tab.js`):
- **INPUT (keystroke):** `onData → statusController.onTerminalData(data);
  _trackTypingForNudge(data); socket.send(data)` (~line 164).
- **OUTPUT:** `socket.onmessage → pendingChunks.push → queueMicrotask(flush) →
  (Codex-only) suppressCursorDuringOutput → vibeTerminal.write(data)` (~line 640).

**Suspects RULED OUT** — none survives the "did it regress *for the same CLI*" test:

| Candidate | Commit | Why it's not it |
|---|---|---|
| Cursor-suppression revival | `d96d646` | Re-added on the output path but **gated to Codex tabs only**. For **Claude**, 1.7.5 does *less* per-chunk work than 1.7.2 (which called it unconditionally). For **Codex**, it's identical to 1.7.2 — `vibe-terminal.js` (the impl) has **zero** committed changes in 1.7.2..1.7.5. No new cost for either CLI. |
| "stop showing output in vs code" | `5a936cd` | One-line removal of a VS Code output-pane auto-reveal (`outputChannel.show`). Nothing to do with the terminal byte path. |
| Tab-status / shell-spinner changes | range | `terminal-tab-status.js` busy/idle transitions are driven by backend session *events*, not per-output-chunk. Not obviously output-correlated. |
| `terminal-multitab.js` 196-line refactor | range | UI/manager refactor; a per-output `updateUi` churn check is still in progress, but no smoking gun yet. |

### Leading hypotheses — ranked after the 2026-06-13 archaeology (nothing scored > 0.5)

Headline from the 13-agent run: **the 1.7.2→1.7.5 VibeRails diff is innocent on the
typing hot path.** `WebSocketConsumer.cs`, `vibe-terminal.js`, and the env-var path
(`LlmCliEnvironmentService.cs`) are **byte-identical** in range; the only hot-path JS
change (`d96d646`'s Codex gate) *removes* work for Claude and is byte-equivalent for
Codex. So a *new* lag for the *same CLI version* almost certainly originates **upstream
in the child CLI's output**, feeding a pre-existing, unchanged server sync-hold.

1. **PRIMARY (~0.45) — upstream Claude Code now emits *real* multi-chunk DEC-2026
   frames → the unchanged 100 ms server sync-hold withholds your typing echo.**
   Keystrokes are **not** locally echoed (`terminal-tab.js:164` only `socket.send`s);
   the "echo" you see is Claude Code re-emitting your keystroke as **output** through
   the PTY. Server-side: `TerminalEmulatorConsumer` parses each chunk and flips
   `_syncOutputActive` on `?2026h`/`?2026l` (`AnsiParser.cs:419`); `WebSocketConsumer`
   reads `IsSyncOutputActive` and, while a frame is open, enters a **hold branch** —
   poll every 4 ms, **don't ship**, until `?2026l` closes the frame or
   **`MaxSyncOutputHoldMs = 100`** elapses (`WebSocketConsumer.cs:14-16, 67-94`; wired
   at `TerminalSessionService.cs:195`). `CLAUDE_CODE_FORCE_SYNC_OUTPUT=1`
   (`LlmCliEnvironmentService.cs:128`, Claude-only) is what makes Claude bracket at
   all. **Gating fact:** with Claude Code 2.1.142 the BSU+ESU arrive **together in one
   16-byte chunk** (see §2026-05-15 forensics, session 2c93b090), so the frame closes
   before tagging and **the hold never engages today.** It can only fire on a frame
   that **straddles chunks** (BSU in one, paint+ESU in a later one) — which is exactly
   what an upstream Claude Code change from "empty no-op pairs" to "proper
   redraw-bracketing" would produce. Echo enqueued inside an open frame then rides the
   hold up to ~100 ms (usually tens); idle → no open frame → 4 ms path → instant.
   Matches the symptom shape exactly. **Weaknesses keeping it at 0.45:** the upstream
   trigger can't be proven from the repo (only old empty-pair captures exist); it is
   **version-gated** (if Rob's `claude` binary was the same at 1.7.2 and 1.7.5, this is
   dead); and it is **Claude-only** (does not cover a Codex repro — see #3).
2. **CONTRIBUTING (~0.10) — the 4 ms `NormalCoalesceDelayMs` floor.** Even with no
   upstream change, a streaming-time echo rides the 4 ms coalesce window instead of
   shipping instantly (`WebSocketConsumer.cs:14, 69-90`). Real but sub-perceptible,
   constant, and unchanged — fails D2. It's just the floor *under* the primary.
3. **CODEX-ONLY (~0.18, only if the repro is on a Codex tab) —
   `suppressCursorDuringOutput` → `_applyTheme()` glyph-atlas clear on WebGL.** On the
   first chunk of each burst the Codex-gated suppression sets `term.options.theme`,
   firing `clearTextureAtlas()` + full-viewport `_fullRefresh()`
   (`vibe-terminal.js:312-319, 688-708`; `OUTPUT_CURSOR_IDLE_MS=90` at
   `terminal-tab.js:35`), serialized ahead of the merged write carrying the echo.
   **Fails D2 vs Rob's stated 1.7.2 baseline** (1.7.2 called suppress unconditionally →
   ran for Codex too, byte-equivalent). It would only qualify if Rob's mental "before"
   is actually **1.7.3/1.7.4**, where suppression was *absent* (removed by `d1d273d`).

Cleared (fail D1/D2): status-observer path, input-send/`_trackTypingForNudge`, and the
new toast/`_notifyTabReady` layer — all byte-identical or not output-correlated.

### Discriminators any real cause must satisfy
- **D1 output-correlated:** lag only while bytes stream, gone when idle. ✔ (the symptom).
- **D2 regressed for the same CLI:** must be newly worse 1.7.2→1.7.5 for the CLI
  Rob is using — OR be an upstream/CLI-version interaction (hypothesis 1). The
  VibeRails-only candidates all **fail D2** so far.
- **D3 magnitude:** tens of ms, not seconds.

### WATCH CHECKLIST — capture this the next time it lags

**Best instrument first — the probe already exists:**
0. **Turn on the `[TypingLag]` Debug probe.** `WebSocketConsumer.cs:119-121` already
   logs each send with `syncOut` and `holdMs`. Run a local Debug build, type during a
   Claude stream, read the log:
   - `syncOut=True` with `holdMs` in the **tens** → **PRIMARY CONFIRMED** (echo held
     inside a straddling sync frame).
   - every send `syncOut=False holdMs~4` → the hold is **not** engaging; primary dead,
     it's the 4 ms floor (negligible) or something else.

**Then the cheap triage:**
1. **`claude --version` now vs the build you ran at 1.7.2.** *(30 s, highest gain — the
   one fact the repo can't supply.)* Bumped past 2.1.142 → smoking gun for the upstream
   theory. Unchanged → primary is dead; pivot to Codex/#3.
2. **Per-CLI triage: Claude vs Codex vs plain shell tab, typing while streaming.**
   *(2 min, decisive.)* Claude-only → primary. Codex-only → #3. Plain-shell too →
   neither; look at VS Code webview scheduling. (A plain shell has no agent output
   stream, so it isolates "the CLI's bytes" from "our input path.")
3. **Env-var A/B:** launch with `CLAUDE_CODE_FORCE_SYNC_OUTPUT=0` (comment
   `LlmCliEnvironmentService.cs:128`), re-test Claude. Lag gone → sync-hold confirmed
   independently of version archaeology.
4. **Idle vs streaming / webview vs browser**, and **capture the session UUID** so we
   can replay and check for real (vs empty) `?2026` frames with
   `python python-scripts/analyze_cursor_state.py <session-id>` — straddling frames
   confirm the upstream trigger; single-chunk 16-byte empty pairs refute it.
5. **Magnitude preview:** lower `MaxSyncOutputHoldMs` 100→16 (`WebSocketConsumer.cs:16`)
   and re-test — a proportional drop in perceived lag confirms the hold is the cause.

### The fix IF/WHEN confirmed (NOT applied — do not pre-empt the watch)
- **Primary:** an **input-echo fast-path** in `WebSocketConsumer`'s send loop — ship
  interleaved echo bytes without honoring the full hold, or lower
  `MaxSyncOutputHoldMs`, or ship small/echo-sized frames immediately. **Frame it
  correctly: this would be a VibeRails *accommodation* for an upstream emission change,
  not a regression we introduced** — our code didn't break; Claude Code's bytes changed.
- **Codex (#3):** stop `suppressCursorDuringOutput` from calling `_applyTheme()` — hide
  the cursor via the cheaper `_syncCursorVisibilityClass()` CSS path instead of swapping
  `options.theme`, so it no longer triggers a WebGL atlas clear + full refresh.

**Guardrails:** no stripping the byte stream (`feedback_no_stripping_terminal_stream`);
the receive path stays on `queueMicrotask`, never `setTimeout` (the 2026-05-05
occlusion landmine). The echo fix belongs at the **server send-loop**, shipping
interleaved echo out of the hold — not by filtering bytes.

> Diagnosis from the 2026-06-13 13-agent archaeology run (691k tokens, 242 tool calls):
> the VibeRails 1.7.2..1.7.5 hot-path diff is **innocent**; primary suspect is upstream
> Claude Code DEC-2026 bracketing × the pre-existing 100 ms server sync-hold. **Awaiting
> Rob's watch-data — the `[TypingLag]` probe + `claude --version` — before any fix.**

---

## 2026-06-12 Tab bar + terminal settings redesign (UI-only; PTY/byte/resize paths untouched)

**What changed (all in tab-strip DOM, settings panel, and CSS):**

1. **Tabs shrink with count.** `.vb-terminal-tab-item` went from a fixed
   `--vb-terminal-tab-width` (250px) to `flex: 1 1 250px` with
   `min-width: var(--vb-terminal-tab-min-width)` (150px; shipped at 120px,
   raised the same day — see the clickability fix in item 2). The strip's
   existing scroll arrows only engage once every tab is at min width.
   **Shrink priority is logo > status text > label** (flex-shrink 4 on
   `.vb-tab-identity` vs 1 on `.vb-tab-status-section`): the label ellipsizes
   away first, then the status text ellipsizes — the status words are never
   hidden (Rob's veto, same day; an initial ~190px container query that hid
   the text was removed, as was the pre-existing ≤992px media rule that hid
   it — VS Code panel viewports routinely sit under 992px). Only the ≤480px
   phone rule still drops the section. `container-type: inline-size` remains
   for the staged action-cluster queries (item 2). Strictly horizontal: the
   strip never wraps and the window-header height is fixed, so the terminal
   viewport size never changes → no fit/SIGWINCH activity from any of this.
2. **Hover swaps status for actions.** The status section fades on hover and a
   rename/minimize/close cluster (`.vb-terminal-tab-actions`) overlays it
   (absolute, `background: inherit` — no reflow). Close is no longer
   permanently visible. `@media (hover: none)` keeps minimize/close inline for
   touch.
   **Same-day clickability fix (Rob's screenshot: center-click on a small
   hovered tab hit the rename pen):** (a) min width 120 → 150px; (b) the
   cluster stages down with tab width via container queries — ≤200px drops
   rename (right-click still renames), ≤149px drops minimize, close always
   survives (icon chips exempt via `:not(.is-minimized)` so their restore
   button stays); (c) the cluster keeps `pointer-events: none` at all times —
   only the *buttons* accept pointer events, and only while revealed — so
   clicks on the padding/gaps between buttons fall through and activate the
   tab instead of dying on the overlay.
3. **Per-tab minimize-to-icon.** New tab-strip button collapses a tab to a
   64px logo+status-icon chip, grouped at the left edge via CSS `order: -1`
   (DOM order and `tabOrder` untouched). State persists in the existing
   sessionStorage tab-meta payload (`minimized`). Clicking a chip activates
   the tab without restoring it; the chip's hover button restores. Built for
   park-a-dev-server tabs. Minimizing does not touch the tab's panel,
   socket, or terminal — chrome only.
4. **THINKING display text: "Working" is SHELL-TAB-ONLY.** Agent tabs say
   "Thinking", exactly as before. First shipped as a global rename to
   "Working"; Rob vetoed it the same day — the status wording ("Thinking" /
   "Ready" / "Waiting for user input") is deliberate product voice and must
   not be renamed or hidden. The shell carve-out lives in
   `SHELL_THINKING_TEXT` / `_statusTextFor()`; the strings are now pinned by
   unit tests. State machine untouched throughout.
5. **Kebab (⋮) menu merged into settings.** Multi Run + Send debug log now sit
   in an always-visible Actions block at the top of the settings panel (same
   `terminal-multirun-btn`/`terminal-senddebug-btn` ids; Playwright specs
   updated). `renderTerminalMenuHtml` deleted; the `TerminalMenu` class stays
   (download menu still uses it).
6. **Focus-view history sidebar: collapsed state rebuilt as a clean rail.**
   The old collapse kept the full 268px sidebar sliding under the terminal
   card behind a fade mask (icon slivers peeking out, plus a clock div at
   `top:12px` overlapping a gradient chevron pill at `top:36px` — Rob's
   "messy" screenshot). Now the sidebar genuinely shrinks to a 44px rail
   (`--ch-sidebar-peek`, width animated in step with the grid column) holding
   ONE real toggle button — history-clock icon when collapsed, chevron when
   open, carries the first-visit pulse; the non-focusable
   `.ch-sidebar-collapsed-icon` div is deleted — plus a vertical "History"
   wordmark. The whole rail click-opens (unchanged handler). The collapsed
   list is opacity-hidden, deliberately NOT `display:none`:
   `_shouldLoadNextPage()` compares scrollHeight/clientHeight, and a
   zero-size body reads as "not full" → would auto-paginate the entire
   history.
7. **User-facing cursor settings removed** — executes the remaining item from
   the 2026-06-09 follow-up list (per Rob: "let the terminal decide"). Gone:
   the settings-panel Cursor section, `viberails_terminal_cursorStyle` /
   `…cursorInactiveStyle` localStorage reads/writes, `setCursorStyle` /
   `setCursorInactiveStyle`, and the `cursorStyle`/`cursorInactiveStyle`
   Terminal options. xterm defaults (block/outline — identical to our old
   defaults) + the CLI's own escape sequences now own the cursor.

**Round 3 (same day, Rob's screenshots: "Cl…" next to a full "Connect…",
hover showing only ×) — supersedes the sizing specifics in items 1–3:**

- **Viewport width caps REMOVED.** The pre-existing `≤992px` (200px) and
  `≤480px` (100px/84px) tab-width media overrides are gone — a VS Code panel
  webview is always "phone-width" by viewport, so the caps gave desktop users
  unreadably tiny tabs. The ≤480 label-truncation (`max-width: 3.5em`) and
  status-section hide went with them. Tabs now shrink ONLY when the strip
  itself runs out of room (flexbox), then the strip scrolls.
- **Preferred width 250px → 300px**, min stays 150px; `@media (hover: none)`
  raises the min to 200px because inline actions take in-flow width there.
- **Name readability floor.** `.vb-tab-identity` got a 100px min-width floor
  (logo + ~70px of label; 110 clipped the spinner at the 150px tab min, so
  100). The **status section is `flex-shrink: 0`** — it never shrinks, so the
  status word ("Thinking"/"Connected"/"Waiting for user input") is always
  fully readable and ONLY the label ellipsizes. Priority: logo > readable
  name > full status text > rest of the label. (With the earlier `shrink: 1`
  a longer tab name squeezed the status text to an ellipsis and it vanished —
  Rob: "add back the text for thinking and stuff.")
- **CONNECTED icon: `fa-link` → `fa-circle`** (a small slate dot / "session
  live" LED, sized 0.42rem so it reads as an indicator not a bullet). The
  chain-link looked like a hyperlink, not a connection. Distinct from READY's
  outlined `fa-circle-check` so the two resting states don't blur together.
- **Action-cluster staging relaxed: ≤200/≤149 → ≤170/≤110.** The ≤149px
  minimize cutoff sat at the 150px tab minimum, so minimize vanished exactly
  when tabs were at min — Rob hovers FOR that button. Minimize + close now
  survive at every real tab width (the ≤110 guard is below min-width by
  design); only the rename pen drops on tight tabs (right-click still
  renames). Center-click stays safe: [min][close] starts ~96px into a 150px
  tab, past the 75px center.
- **Minimized chips grouped at the RIGHT edge** (`order: -1` → `order: 999`),
  parked next to the + button per Rob — not Chrome-style pinned-left.
- **Tabs are content-sized + left-aligned (like VS Code) — NOT full-width.**
  Rob first asked to "use the nav bar width"; the literal read (grow tabs to
  fill the strip) made a single tab span the entire bar with "Thinking" pinned
  at the far edge — "not the entire thing... like a normal ass terminal."
  Final model: `flex: 0 1 auto` (basis auto = content width), clamped between
  `min-width` 150px and `max-width` 300px; tabs never grow to fill. The tab
  list is `flex: 0 1 auto` too (content-width, left-aligned) so the + button
  sits right after the last tab; it still shrinks/scrolls on overflow. List
  background is transparent so empty space (e.g. all tabs minimized to chips)
  doesn't paint a bar — the 1px gaps still read as hairline separators. Chips
  stay fixed at 64px (`flex: 0 0`).
- **History rail icons restored.** Supersedes item 6's "collapsed list is
  opacity-hidden": Rob liked the session icons — the old mess was the
  full-width fade-masked rows, not the icons. The rail now shows an icon-only
  stack (32px brand tiles, centered, below the wordmark; live sessions get a
  green-tinted tile). Items stay pointer-inert (any rail click opens the
  sidebar), the body doesn't scroll, a bottom fade hints at more, and the
  empty/loading prose states are hidden at rail width.

**Deliberately untouched (the guardrail list):**

- `terminal-tab.js` — zero changes. Resize debounce path, `queueMicrotask`
  receive path, and the **Codex-only `suppressCursorDuringOutput` gate** are
  exactly as the 2026-06-11 entry left them. (Cursor-options removal ≠ the
  cancelled suppression-layer removal — that machinery is load-bearing.)
- `cursorBlink: false` stays explicit in `vibe-terminal.js`.
- The **Resize debounce** select (Rendering section) survives the settings
  redesign unchanged — same `viberails_terminal_resizeDebounce` key.
- No byte-stream involvement anywhere (no-stripping rule holds).

---

## 2026-06-11 Codex cursor hop — root cause found (in-box ConPTY) + Codex-only suppression shipped

> **📕 The full story lives in [`TERMINAL-FLICKER.md`](TERMINAL-FLICKER.md)** —
> a complete write-up of the whole flicker saga: the two distinct bugs that
> shared one name, the timeline (hop masked in v1.6.0 → blink fixed / hop
> unmasked in 1.7.3 → root-caused and re-masked today), the full causal chain
> with byte evidence and codex-rs source references, why it is **Windows-only**,
> how VS Code handles it, every confirmation test Rob ran, the red herrings,
> the shipped fix, the durable alternative we shelved, tooling, verification
> gate, and lessons. **Read that document before touching anything
> cursor-related.**

**Status:** Root cause CONFIRMED. **The Codex hop is the only open terminal
bug** — reintroduced (unmasked) by the 1.7.3 blink fix, now re-masked in the
web/webview terminal. It is **Windows-only**: Mac on 1.7.3 is confirmed clean
(no ConPTY middleman).

**One-paragraph version:** Codex makes its cursor visible *before* moving it to
the composer, split across write() syscalls inside a DEC-2026 sync bracket. The
**in-box Windows ConPTY** (what Pty.Net gets from `kernel32!CreatePseudoConsole`)
re-renders between those syscalls and re-emits the transient
cursor-on-the-spinner-row state *outside* the bracket — making it renderable, so
xterm paints the cursor hopping ~10×/sec while Codex thinks. The modern conhost
(`conpty.dll`, bundled + default in VS Code since 1.121, ~2026-05-19) brackets
its re-emissions, so the same transient states are never paintable — which is
why VS Code's terminal is clean, and why **Rob reproduced our exact flicker in
VS Code's own terminal by flipping `terminal.integrated.windowsUseConptyDll` to
`false`**. A/B capture through both conhosts: renderable cursor oscillation
**60 → 0**. Env vars: no effect (tested in a 4-config matrix + confirmed in
codex-rs source).

**Fix shipped today (Rob's call — cheapest sound option):** revived the
pre-1.7.3 `suppressCursorDuringOutput()` call in `terminal-tab.js`, **gated to
Codex tabs only**. Safe because Codex's ~32 ms output cadence stays under the
90 ms restore timer, so the blink that killed global suppression for Claude
can't manifest — matches the clean 1.6.15 experience Rob confirmed. Plumbing:
`TerminalSessionService.ActiveCli` → optional `Cli` on the terminal status DTOs
(child status → root → tabs list) → client `state.cli` (set on start, hydrated
on reload, cleared on stop). **Never enable suppression for Claude or globally**
— that re-creates the 1.7.3 blink. Accepted limitations (external-terminal
launches still hop; focused replays of old recordings still hop; cadence
tripwire), the verification gate, and the shelved conpty.dll-bundling option
are all in `TERMINAL-FLICKER.md` §10–§13.

---

## 2026-06-10 Codex cursor flicker is BACK after 1.7.3 — it's the original status-line hop, unmasked (there were always TWO flickers)

**Status:** Root-caused at the byte level + upstream. ~~Not fixed yet — mitigation
decision pending (Rob).~~ **Superseded 2026-06-11:** real root cause is the
in-box ConPTY re-exposing Codex's transient cursor states (see entry above); the
fix shipped is Codex-only revival of the old suppression, NOT the position-settle
gate recommended below. Do **not** revert `d1d273d` for Claude.

### TL;DR — two distinct cursor flickers were conflated

| | Blink (fixed in 1.7.3) | Hop (back since 1.7.3) |
|---|---|---|
| Symptom | cursor blinks on/off **in place** ~3×/s | cursor **teleports** between composer and status/paint rows ~10×/s while Codex thinks |
| Cause | OUR `suppressCursorDuringOutput()` 90 ms restore timer | CODEX's own emission: every frame parks a *visible* cursor at the end of the status/spinner line, then re-parks at the composer in a separate write 6–35 ms later |
| Affected | Claude + Codex | Codex only (Claude parks only at the prompt) |
| In the bytes? | No — live render path only | **YES** — every Codex session, pre- and post-1.7.3, byte-identical shape |

`d1d273d` fixed the blink and was right to. Its justification — "CLIs already
bracket their own redraws with DECTCEM" — is true for **Claude**, **false for
Codex**. That over-generalization is what the 2026-06-09 entry got wrong, and
why deleting the suppression resurrected the pre-v1.6.0 hop the suppression had
been masking all along.

### Byte evidence (session `33ee4a66-8841-4e3b-9eb0-500ea4838653`, Codex v0.139.0, 2026-06-10, post-1.7.3)

Per render tick during thinking, Codex emits (SessionLogs rows are raw per-PTY-read
payloads — `SessionOutputWriter.HandleDataAsync` writes "always the original
payload"; analyzer: `python-scripts/analyze_cursor_state.py`):

```
#9306108  \e[?2026h\e[0 q                                       BSU + cursor-style pulse
#9306109  \e[?25l\e[12;2H\e[K…status repaint…\e[13;65H\e[?25h   frame; parks cursor VISIBLE at 13;65
                                                                (end of "(0s • esc to interrupt)")
#9306110  \e[?2026l                                             ESU
   …6–35 ms gap — real producer-side timing, not our coalescing…
#9306111  \e[?25l\e[16;3H\e[?25h                                unbracketed re-park at composer 16;3
```

- Frame-final park positions vary frame to frame: composer (`16;3`), status-line
  end (`13;65`), end of painted region (`18;39`). The visible cursor genuinely
  oscillates composer ↔ status-line at the spinner cadence.
- `analyze_cursor_state.py` on `33ee4a66` (post-fix): cursor VISIBLE at **532/533**
  active chunk boundaries; visible-row **moves at 172** of them. On `4026ff95`
  (2026-06-08, **pre**-fix, 31k chunks): visible at 31,133/31,134, **10,833**
  row-moves — same shape. **The bytes did not change; 1.7.3 only unmasked them.**
- Claude contrast (`c8bac352`, same day): hide-only chunks bracket whole repaint
  bursts, parks ONLY at the prompt row — visible-row moves 8/169. The DECTCEM
  discipline asymmetry is the entire story.

### Why the pipeline is not to blame

- `WebSocketConsumer` already does protocol-correct sync-aware batching (holds
  ≤100 ms while a `?2026` frame is open, ships whole frames). The frame and the
  re-park are **separate WS messages** because Codex emits them 6–35 ms apart —
  beyond the 4 ms coalesce, and both commits are sync-CLOSED states, so perfect
  DEC 2026 handling cannot help.
- The 2026-06-09 "replay never flickers" differential was run on **Claude**
  sessions, whose bytes can't hop. `session-viewer.js` preserves inter-chunk
  delays (`delayMs` → `setTimeout`), so a **focused 1× replay of `33ee4a66`
  should visibly reproduce the hop** — that's the 30-second confirmation repro.

### Upstream: known Codex CLI behavior, native terminals affected too

- [openai/codex#9081](https://github.com/openai/codex/issues/9081) — cursor
  "jumps/sweeps" during TUI redraws on Windows Terminal / cmd / pwsh —
  **closed "not planned"**.
- [openai/codex#11063](https://github.com/openai/codex/issues/11063) — cursor
  jank during streaming; proposes hiding the cursor while writing scrollback —
  open, no PR.
- No `[tui]` config knob and no env var controls this (nothing analogous to
  Claude's `CLAUDE_CODE_FORCE_SYNC_OUTPUT`).

So: pre-1.7.3 VibeRails was *better than native terminals* on the hop (masked)
and worse on the blink (caused). Post-1.7.3 we have **native parity** — the same
hop is visible in Windows Terminal running the same Codex version.

### Mitigation options (decision pending)

1. **Do nothing** — accept native parity, point at upstream. Zero risk; flicker stays.
2. **Recommended: position-settle cursor render gate, Codex tabs only, render-clock-driven (no timers).**
   Hide the *rendered* cursor (reuse `vb-terminal-cursor-suppressed` +
   `_syncCursorVisibilityClass`) only while its position is "unsettled":
   - After each `write()` resolves, read `term.buffer.active.cursorX/Y`.
   - Cursor moved **cross-row** → hide; after ~3 consecutive rAF ticks at the
     same position, promote it to "settled" and show.
   - Returns to the already-settled position, or moves **within the same row**
     (typing echo) → show immediately.
   - Status-line park dwells 6–35 ms < settle window → never rendered. Composer
     is the settled position → solid the whole time. Typing unaffected.
   - Cannot recreate the 3 Hz blink: restore is position-stability-driven, not
     output-idle-driven — a transient position never satisfies it.
   - rAF only — no `setTimeout`, no receive-path changes, no occlusion-throttle
     exposure (occluded ⇒ nothing renders anyway). No byte-stream involvement
     (read-only position observation + CSS), so the no-stripping rule holds.
3. **Report upstream with these byte captures** (complements 1 or 2). The clean
   producer-side fix is parking the cursor at the composer *inside* the sync
   frame (or keeping it DECTCEM-hidden while thinking) — aligns with the
   #11063 proposal.

**Rejected:** reverting `d1d273d` (restores the blink); re-adding the 90 ms
suppression for Codex only (same blink, Codex's cadence straddles 90 ms — that
was the original months-long complaint); server-side post-ESU hold to merge the
re-park (cadence-guessing timer, adds latency to every frame — the exact
"mitigation becomes the next bug" trap); waiting-state-driven cursor hide
(seconds of latency + false-positive history per the waiting-observer playbook).

### Guardrails

- The 2026-06-09 follow-up "rip out the dead suppression layer" is **ON HOLD** —
  option 2 reuses that CSS/class machinery. Decide the mitigation first.
- Everything else in the 2026-06-09 entry stands: the env-var red herring
  falsifiers, the blink mechanics, dropping the stashed env-var removal.

---

## 2026-06-09 Cursor flicker (the long-running Codex/Claude one) — root cause and fix

**Status:** FIXED, shipped in **1.7.3** (commit `d1d273d`). Verified visually by Rob
(flicker gone in a fresh browser session). This supersedes the "small residual
flicker" left open by the **2026-04-16 "Codex: Status-line cursor flash"** entry
below — it is the *same* bug, and that entry's mitigation was its cause.

> **⚠️ Correction 2026-06-10:** the claim that the suppression timer explains why
> "Codex flickered the entire time" is only half right, and "CLIs already bracket
> their own redraws with DECTCEM" is **false for Codex**. Codex's bytes park a
> *visible* cursor at the status line every frame and re-park it at the composer
> 6–35 ms later — a real cursor hop that this entry's fix **unmasked**. The blink
> diagnosis and fix below remain correct. See the "## 2026-06-10" entry above.

### Symptom

The text cursor flickers/blinks continuously in the live Web UI / VS Code terminal
while a CLI is producing output — roughly **3×/sec**. Present in **every recent
Claude session** and in **Codex for a long time** (months — the bug we could never
pin down). It is **not**:

- cursor blink — `vibe-terminal.js` sets `cursorBlink: false`;
- the hidden helper-textarea browser caret — already suppressed with
  `caret-color: transparent` (see "Cursor flickering during TUI loading" below);
- the DEC 2026 `CLAUDE_CODE_FORCE_SYNC_OUTPUT` env var — see "The env-var red herring".

### Root cause — our own `suppressCursorDuringOutput()`

`terminal-tab.js`'s WebSocket flush path called, on **every** output chunk:

```js
this.vibeTerminal?.suppressCursorDuringOutput?.(OUTPUT_CURSOR_IDLE_MS); // 90 ms
this.vibeTerminal?.write(data);
```

`suppressCursorDuringOutput(90)` (in `vibe-terminal.js`):

1. **hides** the cursor — swaps the xterm theme `cursor`/`cursorAccent` to transparent
   **and** toggles the `vb-terminal-cursor-suppressed` CSS class
   (`.xterm-cursor { opacity: 0 }`);
2. arms a **90 ms** `setTimeout` → `restoreSuppressedCursor()` (un-hide), re-armed on
   every subsequent chunk.

During a *continuous* burst (gaps < 90 ms) the cursor stays hidden — fine. But CLIs
don't stream continuously: spinner/status redraws arrive in **bursts separated by
gaps that straddle 90 ms** (~10–15 Hz with jitter). Each gap > 90 ms fires the
restore timer → cursor flashes **ON**; the next chunk suppresses it again → **OFF**.
That on/off cycle *is* the flicker.

**Quantified** against real bytes (session `750e672f`, 22 s Claude session): median
inter-chunk gap was **7 ms**, but **66 gaps exceeded 90 ms** → ~66 restore→hide
cycles → **~3 flickers/sec**. (Reproduce with `decode_session.py` + a per-chunk
timestamp pass over `SessionLogs`.)

### Why Codex forever, Claude only recently

`suppressCursorDuringOutput` was added **2026-04-16** (commit `3ea2f30`,
"codex fixes…"; present since **v1.6.0**) to mitigate the *status-line cursor hop* —
see the 2026-04-16 entry below. Codex's output cadence has always straddled the
90 ms threshold, so **Codex flickered the entire time** (the "small residual flicker"
that entry left open). Claude's TUI only recently changed to a comparable
steady-state redraw cadence (frequent spinner/status repaints; on 1.7.x the env var
also injects an ~11.5 Hz stream of empty `?2026h/l` chunks), so Claude crossed the
same threshold and started flickering too.

### The env-var red herring (do not re-chase)

A parallel investigation blamed `CLAUDE_CODE_FORCE_SYNC_OUTPUT=1` (it makes Claude
emit empty `\e[?2026h\e[?2026l` pairs every render tick). That is **wrong**. Two
clean falsifiers:

1. **v1.6.15 never set the env var** (added at v1.7.0) **and still flickered** —
   confirmed on a second Windows machine.
2. **Replaying the exact same bytes** — BSU/ESU pairs included — through the session
   viewer **never flickers**.

The env var only changes the *byte stream*; the flicker is in the *live render path*.
The env-var-removal change is parked in a local git stash, **unmerged** — it does not
fix this and should be dropped (or have its rationale rewritten before it ever lands).

### What cracked it (method worth repeating)

Rob noticed that **session replay never flickers — even when you focus the replay
terminal and a real cursor appears.** The replay path (`session-viewer.js`) feeds the
*same recorded bytes* into xterm but **never calls `suppressCursorDuringOutput()`**
and never wires up our live cursor handling. Same bytes + no suppression = no flicker
→ the cause must be something the *live* path does to the cursor, not the bytes and
not the CLI. That differential pointed straight at our suppression code. (Same shape
as the stacked-repaints lesson: when identical bytes render cleanly on one path but
not another, the bug is on the path, not in the bytes.)

### The fix

Deleted the single `suppressCursorDuringOutput()` call in `terminal-tab.js`'s flush
path (commit `d1d273d`, shipped 1.7.3). The CLIs already manage cursor visibility via
DECTCEM (`\e[?25l` hide / `\e[?25h` show) around their redraws, and xterm renders
those atomically — so the app-layer suppression was both redundant and the actual
cause. One-line behavioral change; the now-unused methods/constant/CSS were left in
place for the hotfix.

### Follow-up (not done yet)

- **Remove the dead suppression layer:** `suppressCursorDuringOutput` /
  `restoreSuppressedCursor` / `_cursorSuppressed` / `_cursorRestoreTimeoutId` /
  the `_getAppliedTheme` hidden-cursor branch / `_syncCursorVisibilityClass`,
  `OUTPUT_CURSOR_IDLE_MS`, and the `.vb-terminal-cursor-suppressed` CSS.
- ~~**Remove the user-facing cursor-style options**~~ **DONE 2026-06-12** (see
  that entry): cursor-style / inactive-style selects, their localStorage keys,
  `setCursorStyle` / `setCursorInactiveStyle`, and the `cursorStyle` /
  `cursorInactiveStyle` Terminal options are gone. `cursorBlink: false` was
  deliberately KEPT explicit (this file's diagnoses reference it; xterm's
  default happens to match).
- **Drop the env-var change** parked in the git stash.

### Lesson

A "mitigation" that hides a symptom on a **timer** can quietly *become* the next bug.
The 2026-04-16 entry literally documented a *"small residual flicker"* right after
adding output-driven cursor suppression, and waved it off as "Codex still legitimately
repainting." That residual was the suppression's own restore→re-suppress cycle. When a
fix leaves behind a residual artifact of the **same kind** it was meant to fix, suspect
the fix.

---

## 2026-05-15 Stacked repaints during drag-resize — diagnosis, fix, and lessons

**Status:** Code change landed. **Awaiting Rob's manual re-test using
the same slow-sidebar-drag repro that produced `fd7ac97f` —** do not
mark this closed until a fresh slow-drag session shows ≤1 redraw per
drag in `analyze_doubleprint.py` and no visible stacking on screen.

This entry is the **canonical write-up** for the stacked-repaints bug.
It supersedes (does not delete) the earlier 2026-05-13 entry, which is
preserved below as historical context.

### TL;DR

| | |
|---|---|
| **Trigger** | Rapid/sustained `__resize__` messages to the server during slow panel-drag → one SIGWINCH per intermediate size → one Claude Code full-screen repaint per SIGWINCH → xterm.js renders the bursts mid-reflow → stacked copies of the UI persist |
| **Root cause (downstream)** | Unknown xterm.js or pipeline reflow-during-incoming-bytes behavior — bytes are clean per C# emulator, but xterm.js live-render produces stacking. **Not investigated to root**; suppressed at trigger. |
| **Fix** | `RESIZE_SYNC_DEBOUNCE_MS = 140 → 2000` in `terminal-tab.js`. Trailing-edge debounce: the outgoing `__resize__` only fires after 2 s of quiet on resize events. Each new `onFitChange` callback restarts the timer (`scheduleResizeToPty` already does `clearPendingResizeToPty` before scheduling, so this works for free). |
| **Side effect** | After the user stops dragging, Claude Code learns the new size ~2 s later. xterm.js itself reflows on the user's screen continuously (100 ms fit debounce in `vibe-terminal.js`, unchanged). |
| **Sessions** | `f3e25a1e` (2026-05-13, original misread, single-resize), `dd5cc208` (2026-05-07, same misread), `2c93b090` (2026-05-15, no-resize repro from boot animation), `fd7ac97f` (2026-05-15, slow-drag repro that finally exposed the rapid-resize trigger) |
| **What stays in** | DEC 2026 env var in `CommandService.cs` + its test (`CommandServiceTests.cs`); diagnostic test `Session_2c93b090_StackedRepaintsDiagnostic.cs` (one-shot forensic, kept for archaeology) |
| **What was rejected** | Codex 2026-05-14 receive-path `setTimeout` hold (reverted, see historical entry); `Ctrl+L`-after-resize-settle (still banned per 2026-04-09 guardrail); server-side `NormalCoalesceDelayMs` bump (not needed once trigger is gated) |

### Symptom (correct version — the 2026-05-13 entry below got this wrong)

- **Persistent stacked repaints**: N copies of Claude Code's
  full-screen UI (banner, divider, prompt, mode indicator) accumulate
  on screen at different vertical positions. They stay there. The
  screen does **not** self-correct on subsequent redraws.
- **Trigger is rapid resize events** — primary trigger. Most cleanly
  reproduced by slowly dragging the panel sidebar in VS Code so the
  terminal container resizes by 1–2 cells at a time, every ~100–500 ms,
  for a few seconds. Each unique cols×rows reaching the PTY fires
  another SIGWINCH → another Claude Code full-screen repaint. The
  faster the drag, the worse the stacking.
- **Also reproduces from periodic non-resize repaints in some cases.**
  Session `2c93b090` had zero resize events but Claude Code's
  boot-animation full-screen repaints still produced stacking. So the
  bug isn't purely a resize-vs-bytes race; it's a "fast successive
  repaints arrive at xterm.js while it's still reflowing the previous
  one" race. Resize storms are just the easiest way to produce that
  condition.
- **Not always rooted at the top of the viewport.** Stacked copies
  appear at whatever cursor baseline each repaint started from. Some
  repaints emit `\e[H` (home) and paint from row 1; others start from
  wherever the cursor happens to be. Rows that one repaint didn't
  touch retain content from a previous repaint that did.

### What we got wrong before, and why

The 2026-05-13 forensic of `f3e25a1e` framed this as **"a brief ~7 ms
flash on resize that self-corrects."** Three layers of misreading:

1. **"Brief flash" → wrong.** The forensic captured the byte stream
   correctly (two Claude Code repaints 7 ms apart after one SIGWINCH)
   and inferred from the byte timing that the visible artifact must
   also be 7 ms. But the artifact is on the *render* side, not the
   *bytes* side — xterm.js commits each repaint as a separate DOM
   update, and once both are committed, both stay visible until
   something explicitly overwrites the rows the second repaint didn't
   touch. The artifact is **not** transient.
2. **"Self-corrects" → wrong.** The 2026-05-13 entry said "the screen
   then settles into the correct final layout." Rob's visual debug on
   2026-05-15 disproved this directly. The screen stays stacked.
3. **"Resize-only / single-resize" → mostly wrong.** The
   `f3e25a1e`/`dd5cc208` sessions happened to capture moments with
   only one SIGWINCH visible in the bytes. We took that as the bug's
   defining shape. The real shape (slow drag → many SIGWINCHes) is
   much more common in normal use, and there's a separate
   non-resize-driven variant.

The reason we missed it: **the original forensic process was
byte-stream-driven, not visual-driven.** A visible artifact on screen
that the bytes-alone analysis can't reproduce (because the bytes are
*correct* — see "Bytes are clean" below) needs a screenshot or a
recording, not just a `decode_session.py` dump. The forensic shop in
2026-05-13 didn't have either. Lesson noted at the bottom of this
entry.

### Investigation log (following `SESSION_DEBUG_PLAYBOOK.md`)

Did this end-to-end after Rob pointed at the playbook. Steps below
match the playbook's numbered workflow.

**Step 1 — Resolve UUIDs.** Rob handed over two sessions:
- `2c93b090-75bf-4976-af6a-56373576c0ee` — visual debug, shows three
  stacked banners on screen during boot animation, no drag involved.
- `fd7ac97f-0b7b-4c0b-9c32-4daf9030392d` — slow sidebar drag,
  reproduces stacking with rapid resize events.

**Step 2 — Decode the raw stream.** `python decode_session.py …`
produced `<uuid>.decoded.txt` for both. Inspected for known suspects
(`\e[2J` storms, CUP fusion, bracketed paste, prompt glyphs,
dropdown lines).

**Step 4 — Classify.** Per the playbook table: symptom is "xterm.js
replay double-prints / loses redraws / glitches on resize" → subsystem
is `TerminalEmulator` → required step before testing is **run
`analyze_doubleprint.py` first**.

**`analyze_doubleprint.py` findings — `2c93b090` (boot animation, no drag):**

```
+ 2.142s  #6289863     16B  [SYNC_ON]                          # empty BSU+ESU
+ 2.143s  #6289864   1636B  [-]                                 # 1st banner paint
+ 6.223s  #6289867   1936B  [HOME,EOL_ERASE_x24]               # 2nd paint, ~4s later
+ 6.229s  #6289868     16B  [SYNC_ON]                          # empty BSU+ESU
+ 6.238s  #6289869   1744B  [HOME,EOL_ERASE_x22]               # 3rd paint
+11.341s  #6289870   1726B  [HOME,EOL_ERASE_x22] fp=baaaf645…  # DUPLICATE fp of #6289869
+11.346s  #6289871     16B  [SYNC_ON]
+11.347s  #6289872   1815B  [HOME,EOL_ERASE_x21]               # yet another full paint
+42.840s  #6289899   1158B  [-]                                # …and so on
```
- 50+ standalone 16-byte `SYNC_ON` chunks across the session, each
  containing exactly `\e[?2026h\e[?2026l` — Claude Code emitting
  empty BSU/ESU pairs that do not bracket the actual paints.
- All `HOME,EOL_ERASE_xN` redraw chunks erase only 19–24 lines, but
  the terminal is 27 rows tall. Rows 23–27 are never touched by any
  redraw, so any prior content there survives.
- Chunks `#6289869` and `#6289870` have **identical fingerprints**
  5 s apart — Claude Code emits the exact same full redraw twice.

**`analyze_doubleprint.py` findings — `fd7ac97f` (slow sidebar drag):**

```
+ 4.627s  #6313694   1791B  [HOME,EOL_ERASE_x20]
+ 4.642s  #6313695   1827B  [HOME,EOL_ERASE_x19]               # 15 ms after previous
+ 4.877s  #6313696   1821B  [HOME,EOL_ERASE_x19] DUP fp        # 235 ms after #6313695
+ 4.892s  #6313697   1819B  [HOME,EOL_ERASE_x19]               # 15 ms
+ 5.710s  #6313698   1813B  [HOME,EOL_ERASE_x19] DUP fp
+ 5.716s  #6313699   1811B  [HOME,EOL_ERASE_x19]               # 6 ms
+ 6.061s  #6313700   1805B  [HOME,EOL_ERASE_x19] DUP fp
+ 6.077s  #6313701   1803B  [HOME,EOL_ERASE_x19]               # 16 ms
+ 6.276s  #6313702   1797B  [HOME,EOL_ERASE_x19] DUP fp
+ 6.283s  #6313703   1795B  [HOME,EOL_ERASE_x19]               # 7 ms
+ 7.077s  #6313704   1789B  [HOME,EOL_ERASE_x19] DUP fp
+ 7.083s  #6313705   1787B  [HOME,EOL_ERASE_x19]               # 6 ms
+ 7.575s  #6313706   1660B  [HOME,EOL_ERASE_x19]
+ 7.582s  #6313707   1779B  [HOME,EOL_ERASE_x19]               # 7 ms
```
- **11 full-screen redraws in ~3 seconds** during the slow drag
  (between +4.6 s and +7.6 s).
- Many pairs fire 6–16 ms apart — these are the rapid post-SIGWINCH
  repaints Claude Code emits in response to each resize event.
- Several duplicate-fingerprint redraws — Claude Code occasionally
  emits the same paint twice, presumably one for the SIGWINCH and one
  for some other refresh trigger.

**Step 5+6 (modified for this bug shape).** The playbook's
`Tests/Services/CleanedInput/` test pattern is for prompt-extraction
bugs in `CleanedUserInputService`. This bug is **not** in that
subsystem (the playbook classification table actually directs xterm-replay
bugs to `TerminalEmulator.Tests/FixtureReplayTests.cs` pattern), so
the fixture/test step we did was:

1. Wrote `TerminalEmulator.Tests/Session_2c93b090_StackedRepaintsDiagnostic.cs`
   (one-shot forensic, not a regression). Replayed `2c93b090`'s
   16,730 bytes through `Terminal` at 171×27 (the dimensions in
   `\e[8;27;171t`). Dumped converged live grid + scrollback.
2. Created companion fixture `TerminalEmulator.Tests/fixtures/session_2c93b090_full.bin`.

**Result — the C# emulator converges to ONE banner.** Full grid dump:

```
[L00] | ▐▛███▜▌   Claude Code v2.1.142
[L01] |▝▜█████▛▘  Opus 4.7 (1M context) with high effort · Claude Max
[L02] |  ▘▘ ▝▝    C:\source\vibe-rails
[L03] |
[L04] |> hi
[L05] |
[L06] |● Hi! What would you like to work on?
[L07] |
[L08] |✻ Sautéed for 1s
[L09] |
[L10] |─────────────…────────  (full-width divider, 171 cols)
[L11] |>
[L12] |─────────────…────────
[L13] |  ⏵⏵ auto mode on (shift+tab to cycle)
[L14] | (empty)
[L15..L26] | (all empty)
```

- Exactly **one** "Claude Code" string in the live grid.
- Zero scrollback rows.
- Cursor at row 13, col 170 (final stable position).
- Not in alt screen.

**So the bytes are clean.** Our C# emulator (which is the same code
path that produces `TerminalGridSerializer.Serialize` snapshots for
reconnect) does not produce stacking when fed the exact byte stream
that produced stacking in xterm.js live-render.

**Step 8 — Fix the smallest piece.** The downstream cause (xterm.js
mishandling something during reflow-with-incoming-bytes) is real but
out of scope for this fix. The trigger (rapid SIGWINCHes from
non-debounced sustained resize events) is in scope, well-understood,
and one line:

```js
// VibeRails/wwwroot/js/modules/terminal-tab.js
const RESIZE_SYNC_DEBOUNCE_MS = 2000;   // was 140
```

Why this works: `onFitChange` is wired to `scheduleResizeToPty()`
which does `clearPendingResizeToPty()` before setting a new timeout.
Each new fit during the drag restarts the 2 s timer. Only when the
user has been quiet on resize for 2 s does the outgoing `__resize__`
actually fire, the server resizes the PTY, Claude Code gets exactly
one SIGWINCH, and emits exactly one final repaint.

The bug needs N≥2 rapid SIGWINCHes to manifest visibly. With N=1 per
drag, the live-render bug has no opportunity to fire.

### Why 2 s, why not fix xterm.js

**The xterm.js live-render bug is still open** — when xterm.js
receives a burst of full-screen redraws while it's still committing
the previous one (or while a reflow is in flight), the result
visually stacks. The same bytes converge cleanly in our C# emulator
and would presumably converge cleanly in xterm.js too if the bytes
arrived in one task. Suspects include:

- xterm.js's parser-vs-renderer task split (rows committed before all
  bytes for the frame are parsed).
- Empty `\e[?2026h\e[?2026l` pairs firing `_onRequestRefreshRows.fire(void 0)`
  at unexpected times (xterm v6 sync output handler).
- `WebSocketConsumer.NormalCoalesceDelayMs = 4 ms` chunking redraws
  into separate WS frames that xterm.js commits as separate paints.
- Some interaction between fit-reflow and incoming-bytes when both
  happen in the same render frame.

We chose **trigger-side suppression** (debounce the outgoing resize)
over **root-cause fix** (find and fix the xterm.js issue) because:

1. **Cost.** The xterm.js fix would require headless xterm replay
   harness + bisection on which sequence triggers it + likely an
   upstream patch. Multi-day. The trigger-side fix is one line.
2. **Justification.** Users resize panels rarely and deliberately.
   A 2 s settle latency on Claude Code learning the new size is
   barely perceptible — the local xterm fit (at 100 ms) makes the
   on-screen terminal track during the drag, so the user sees the
   resize happening live. They only experience the 2 s wait if they
   look closely at "when does Claude Code's prompt area redraw to
   the new dimensions."
3. **Reversibility.** One-line revert if it doesn't work or causes
   secondary issues. The constant is in one place.

**Numerical justification for 2 s vs 200–500 ms** (community
recommendations are around 150–200 ms for fit-debounce in xterm.js +
fit-addon stacks):

- Rob's repro is a **slow** drag with 100–500 ms gaps between mouse
  movements. A 200 ms debounce would still let multiple SIGWINCHes
  through during the drag. A 500 ms might be enough but is close to
  the user's drag rhythm.
- VS Code's own integrated terminal **does not debounce** the
  panel-resize → SIGWINCH path (treated as "as-designed" per
  `microsoft/vscode#71728`), so we have no reference number from
  them. We're going stricter on purpose.
- 2 s is safely outside the human drag-pause window for "I'm still
  resizing." It guarantees the user really stopped before the gate
  opens.

If 2 s feels too laggy in practice, **tune it down** by halving
(2000 → 1000 → 500) and re-running the slow-drag repro until
stacking returns. The minimum that still suppresses the bug is the
right value. **Do not** reduce below 500 ms without re-doing the
manual repro — the rapid-pair patterns in `fd7ac97f` were as quick
as 6 ms apart.

### Verification gate (must pass before this section gets a "Verified" status)

1. **Slow-drag repro (same gesture that produced `fd7ac97f`).**
   - Open Claude Code in the VibeRails web terminal (VS Code extension
     or browser, either is fine — both go through the same
     `terminal-tab.js` debounce path).
   - Slowly drag the VS Code sidebar or panel divider to change the
     terminal's column count, with deliberate 100–500 ms pauses
     between movements, for at least 3–5 seconds.
   - Let go.
   - Wait 2 s.
   - **Expected:** terminal reflows once, Claude Code's banner /
     divider / prompt redraws once at the final size. No stacked
     copies. No visible artifact.
2. **Capture the session ID** that produced the test drag. Run:
   ```
   python python-scripts/analyze_doubleprint.py <new-session-id>
   ```
   **Expected:** ≤1 redraw chunk per drag gesture (not 11 like
   `fd7ac97f`). Empty `SYNC_ON` chunks may still appear since Claude
   Code's empty-BSU/ESU bug is unrelated to this fix; ignore them.
3. **Quick-resize sanity check.** Drag the panel rapidly back and
   forth (Rob's original 2026-05-15 morning repro shape). The bug
   was easier to trigger this way too. **Expected:** same outcome —
   one final repaint after letting go, no stacking.
4. **No-resize sanity check.** Launch Claude Code, do nothing for
   ~30 s, observe the boot animation and any spinner ticks.
   **Expected:** still might show stacking on the boot animation if
   Claude Code's repaint cadence happens to be fast enough. This is
   the open xterm.js issue. The 2 s debounce does **not** fix the
   non-resize variant. If this variant is observable in practice,
   we'll have to revisit the xterm.js root cause.
5. **If all four checks pass:** update this entry's status to
   "Verified <date>, session `<id>`" and the Rob's-note at top of
   file accordingly.

### What stays in, what's new, what's old

| File | What | Status |
|---|---|---|
| `VibeRails/wwwroot/js/modules/terminal-tab.js` | `RESIZE_SYNC_DEBOUNCE_MS = 2000` (was 140) | **NEW** — the actual fix |
| `VibeRails/Services/Terminal/Commands/CommandService.cs` | `CLAUDE_CODE_FORCE_SYNC_OUTPUT=1` for `LLM.Claude` | KEPT — harmless, may matter once Claude Code upstream fixes empty-BSU/ESU |
| `Tests/Services/Terminal/CommandServiceTests.cs` | Env-var contract pin (Claude positive + 3 negatives) | KEPT — pins the contract |
| `TerminalEmulator.Tests/Session_2c93b090_StackedRepaintsDiagnostic.cs` | One-shot forensic: replays `2c93b090` bytes → dumps converged grid | KEPT — useful archaeology if the bug returns |
| `TerminalEmulator.Tests/fixtures/session_2c93b090_full.bin` | Fixture for the above | KEPT |

Did **not** change:

- `VibeRails/Services/Terminal/Consumers/WebSocketConsumer.cs` — server-side coalesce stays at `NormalCoalesceDelayMs = 4 ms`.
- `VibeRails/wwwroot/js/modules/vibe-terminal.js` — local fit debounce stays at 100 ms (immediate user feedback during drag).
- `VibeRails/wwwroot/js/modules/terminal-tab.js`'s `socket.onmessage` — still uses `queueMicrotask`, no `setTimeout` on receive path.
- `TerminalResizeCoordinator.EnableDebouncedRedrawOnResize` — still `false` (would conflict with the 2026-04-09 "no Ctrl+L pokes" guardrail).

### Guardrails (do not unwind)

- **Do not reduce `RESIZE_SYNC_DEBOUNCE_MS` below 500 ms** without
  re-running the slow-drag repro. The fastest rapid-pair in `fd7ac97f`
  was 6 ms apart; 500 ms is a reasonable empirical lower bound.
- **Do not reintroduce `Ctrl+L`-after-resize pokes.** 2026-04-09
  guardrail (the prior "stacked banners" bug was caused by exactly
  this — Ctrl+L on attach scrolling banner rows into scrollback over
  reconnects). Sending fewer SIGWINCHes (this fix) is the right way
  to reduce repaints; sending fake redraw hints is not.
- **Do not add `setTimeout`-based output coalesce on `socket.onmessage`.**
  2026-05-05 occlusion-throttle landmine still applies (1–3 s per
  keystroke for the lifetime of a fresh VS Code process). The
  receive path must stay on `queueMicrotask`.
- **Do not gate the local `vibe-terminal.js` fit at >100 ms.** User
  wants the on-screen terminal to track the drag — gating that too
  makes the resize feel laggy. Only the *outgoing* `__resize__` is
  debounced at 2 s.
- **Do not delete `CLAUDE_CODE_FORCE_SYNC_OUTPUT=1` from
  `CommandService`** without checking whether Claude Code has shipped
  a fix for the empty-BSU/ESU emission bug. If it has, the env var
  starts doing real work (proper bracketing → xterm.js deferred
  render → atomic frame commits) and is suddenly load-bearing.

### Open downstream work (not blocking)

The xterm.js live-render bug that turns clean bytes into stacked
copies is **not investigated to root**. Suggested approach if/when
we revisit:

1. Write a Node test in `UITests/` using `@xterm/headless` that feeds
   `fd7ac97f`'s byte stream into a fresh `Terminal` and dumps the
   final cell grid. Compare against
   `Session_2c93b090_StackedRepaintsDiagnostic`'s C# converged grid.
2. If the headless xterm grid is **also clean** (one banner), then
   the bug is in the browser xterm.js *render* path specifically —
   look at DOM/canvas commits, reflow timing, the WebGL/Canvas
   renderer addon (if we ship one).
3. If the headless xterm grid is **stacked**, then xterm.js's
   *parser* produces different cell state than our C# emulator for
   the same bytes — bisect on which escape sequence triggers it.
   Likely candidates: empty `\e[?2026h\e[?2026l` interacting with
   reflow, or `\e[K` + absolute-CUP-after-`\e[H` ordering.
4. Report findings upstream at `xtermjs/xterm.js` if the cause is in
   xterm.js itself. Our `xterm.min.js` v6.0.0 was published Nov 2025;
   they may have already fixed this on main.

This work is **optional**. The 2 s debounce closes the user-visible
symptom for the common case. The non-resize variant (boot animation
stacking, like `2c93b090`) is rarer and the user can refresh the
terminal to recover. Investigate when convenient, not urgently.

### Lessons (for future Claude / Rob / anyone reading)

1. **A user screenshot is worth a thousand decoded chunks.** The
   2026-05-13 forensic interpreted byte timing as visual timing and
   called this a "transient flash" for two days. The first time Rob
   actually showed a screenshot, the misreading was obvious in 30
   seconds. **If a bug is described in visual terms, get a visual
   capture before doing byte forensics.** Bytes are necessary but
   not sufficient.
2. **Follow `SESSION_DEBUG_PLAYBOOK.md` early, not late.** The
   playbook explicitly says "xterm.js replay double-prints / loses
   redraws / glitches on resize → run `analyze_doubleprint.py` first."
   Skipping this step on 2026-05-15 morning cost a couple of hours
   of speculation about server-side coalesce vs DEC 2026 before the
   tool surfaced the rapid-resize trigger pattern immediately.
3. **Trust the user's bug name.** Rob called this "the reprint /
   double-print bug" from the start. The 2026-05-13 entry tried to
   reframe it as a "resize flicker" and built the wrong mental model
   from there. The user's working name was closer to truth — the
   bug literally is the same content printed multiple times.
4. **Rapid-resize debounce is older than you think.** The
   `RESIZE_SYNC_DEBOUNCE_MS` constant already existed at 140 ms in
   `terminal-tab.js` — that's been catching *some* class of resize
   spam since whenever it was introduced. The bug fixed here is
   strictly "the debounce window is shorter than the user's slow-drag
   rhythm." VS Code itself **had** a debounced resize in 1.25, lost
   it by 1.28 (per Tyriar in `microsoft/vscode#58975`), and didn't
   reinstate. So we're going stricter than VS Code on purpose — and
   for a defensible reason (the underlying xterm.js race we haven't
   fixed yet).
5. **Atomic-protocol fixes (DEC 2026) require the producer's
   cooperation.** Claude Code 2.1.142 advertises sync output support
   when forced via env var, but emits empty BSU/ESU pairs that don't
   wrap actual redraws. Real fix needs upstream patch at
   `anthropics/claude-code`. Until then, the env var is theatre —
   it makes the byte stream *look* like it's bracketed but produces
   no actual atomicity. Don't trust a protocol fix until you've
   verified the producer is using the protocol correctly, not just
   emitting its sequences.

### Reference: session forensic data

All four canonical repros live in `~/.vibe_rails/state.db`
(SessionLogs table) and can be re-replayed with `decode_session.py`
and `analyze_doubleprint.py`:

- **`f3e25a1e-c0eb-4834-a3d2-0eace2bb0e1f`** (2026-05-13) — single
  SIGWINCH 150×10 → 150×28, two repaints 7 ms apart. The original
  "transient flash" misread came from this. Saved binary fixture
  exists at `runbooks/terminal/repro-fixtures/session_f3e25a1e_resize_reprint.bin`
  (the two suspect chunks, 1512 B + 1974 B = 3486 B).
- **`dd5cc208-8b09-4473-8268-0a565a5bd55e`** (2026-05-07) — same shape
  as `f3e25a1e`, longer session. Same misread.
- **`2c93b090-75bf-4976-af6a-56373576c0ee`** (2026-05-15 AM) — boot
  animation alone produced stacking, no resize. Claude Code 2.1.142.
  Visual screenshot at `OneDrive/Desktop/1.png` (local to Rob, not in
  repo). 42 empty `?2026` toggles. Bytes converge cleanly through C#
  emulator. Fixture at
  `TerminalEmulator.Tests/fixtures/session_2c93b090_full.bin`.
- **`fd7ac97f-0b7b-4c0b-9c32-4daf9030392d`** (2026-05-15 PM) — slow
  sidebar drag, 11 redraws in 3 s, rapid-pair pattern (6–235 ms
  between redraws). The session that finally exposed the trigger.

---

## 2026-05-15 (earlier today) DEC 2026 candidate fix — disproven, code stays in

**What changed:**

```csharp
// VibeRails/Services/Terminal/Commands/CommandService.cs
if (llm == LLM.Claude)
{
    environment["CLAUDE_CODE_FORCE_SYNC_OUTPUT"] = "1";
}
```

**Why this works:**

DEC private mode 2026 ("Synchronized Output") lets the application bracket
a burst of redraw bytes with `CSI ?2026 h` (BSU = Begin Synchronized
Update) and `CSI ?2026 l` (ESU = End Synchronized Update). xterm.js v6.0.0
buffers parser-state updates between BSU and ESU and only commits to the
DOM atomically on ESU (or after a 1-second safety timeout). Two server
frames 7 ms apart bracketed inside one BSU/ESU pair → one DOM commit,
zero flicker, regardless of WS task boundaries or `setTimeout` throttling.

This is the upstream-blessed contract that VS Code, ghostty, kitty,
contour, and tmux all implement. Anthropic ships Claude Code with sync
output support but **2.1.110+ regressed** from runtime `DECRQM` capability
detection to a hardcoded TERM allowlist (`xterm-ghostty`, `xterm-kitty`
work; `xterm-256color` and the default ConPTY value don't). Our ConPTY
child lands off the allowlist, so sync output stayed dark and the two
post-resize redraw frames committed as separate paints — the visible
reprint flash. Tracking: `anthropics/claude-code#49584` (2.1.110
regression report), `anthropics/claude-code#55613` (still broken 2.1.126).

Anthropic's documented workaround for terminals their TERM-sniffing
misses is the **`CLAUDE_CODE_FORCE_SYNC_OUTPUT=1`** env var, added
May 2026. That's what we now set per-PTY when the LLM is Claude.

**Receiver-side verification:**

Confirmed `VibeRails/wwwroot/assets/xterm/xterm.min.js` (v6.0.0 per the
bundle header comment) has the full deferred-render path:

- `case 2026: this._coreService.decPrivateModes.synchronizedOutput=!0`
  (BSU enable handler)
- `case 2026: this._coreService.decPrivateModes.synchronizedOutput=!1,
  this._onRequestRefreshRows.fire(void 0)` (ESU disable + atomic flush)
- `if(this._coreService.decPrivateModes.synchronizedOutput) return void
  this._syncOutputHandler.bufferRows(e,t)` (renderer routes deferred
  while sync is active)
- 1000 ms safety timeout literally visible as `1e3` in the source
- DECRQM query response so capability detection works for any future
  consumer that probes us

**Why this beats the alternatives that were on the table:**

| Approach | Trade-off |
|---|---|
| Client-side `setTimeout(40ms)` hold (Codex, 2026-05-14) | Re-opens the `setTimeout` occlusion-throttle landmine (1–3 s/keystroke under fresh-Code.exe) — **rejected** |
| Server-side `NormalCoalesceDelayMs` bump to ~20 ms for ~200 ms post-`__resize__` | Heuristic timing; still races; only catches the specific 7 ms-apart shape — would have been my next try |
| **DEC 2026 env var (this fix)** | Atomic by protocol, no timers, no race window, no client/server code changes, works for any number of frames in any window |

**Caveats / known limits:**

- Only fixes **Claude Code**. If Codex/Copilot ever show the same shape
  of reprint, they'd need either an analogous env var (Codex has its own
  sync-output gating story) or a separate mitigation. Forensic profile
  for the open repro sessions was Claude Code only, so this is enough.
- Depends on Claude Code keeping `CLAUDE_CODE_FORCE_SYNC_OUTPUT` as a
  supported env var. If they retire it, the bug returns; the regression
  test in `CommandServiceTests` won't catch that (it only pins **we**
  set the var, not that Claude Code **honors** it). A future repro of
  the artifact in a fresh session should re-check the byte stream for
  `?2026h`/`?2026l` presence — see "How to verify" below.
- Snapshot serializer (`TerminalGridSerializer`) does not track or
  re-emit sync output state on reconnect. That is fine: reconnect
  snapshots are a single atomic frame already.

**How to verify the fix worked:**

After updating, in a real Claude Code session, dump a fresh session's
bytes and grep for `?2026h` / `?2026l`:

```
python python-scripts/decode_session.py <session-id>
python python-scripts/show_chunks.py <session-id> <suspect-chunks>
```

The repro sessions `f3e25a1e-c0eb-4834-a3d2-0eace2bb0e1f` and
`dd5cc208-8b09-4473-8268-0a565a5bd55e` showed **zero** `?2026` toggles.
A post-fix session should show them bracketing the post-resize redraws.

**Guardrails for future changes:**

- Do not strip or rewrite `?2026h`/`?2026l` anywhere in the consumer
  pipeline. They must reach xterm.js untouched.
- If you migrate the env-merging code path, keep the
  `llm == LLM.Claude → CLAUDE_CODE_FORCE_SYNC_OUTPUT=1` branch — the
  `CommandServiceTests` pins this contract.
- If Anthropic switches Claude Code back to runtime capability detection
  (the right fix), the env var becomes a no-op and can stay or be
  removed without functional change. No urgency.
- Do **not** introduce client-side `setTimeout`-based receive-path
  coalescing as a "belt and suspenders" backup. The 2026-05-05 occlusion
  throttling failure mode still applies; the protocol-level fix is the
  only one that does not interact with it.

---

## 2026-05-13 Resize reprint / overprint — original analysis (historical, symptom characterization corrected 2026-05-15)

> **Correction (2026-05-15):** The "Symptom" and "Root cause shape"
> sections below describe a **brief ~7 ms transient flash that
> self-corrects**. That is **wrong**. Visual debug of session
> `2c93b090` on 2026-05-15 showed the real symptom is **persistent
> stacked repaints that do not self-correct**, and the trigger is not
> limited to resize (boot-time periodic redraws reproduce it too). See
> the 2026-05-15 entry above for the corrected symptom and the new
> investigation plan. The rest of the analysis below — the rejected
> client-side `setTimeout` hold, the explanation of why two frames
> arrive 7 ms apart, the explicit "do not reintroduce `setTimeout` on
> the receive path" guardrail — remains load-bearing and is preserved
> intact. The forensic data on `f3e25a1e` / `dd5cc208` itself is also
> still accurate; the misreading was in interpreting that data as
> describing a transient artifact when those captured sessions likely
> just happened to settle quickly due to follow-on redraws painting
> over the stacked state. New investigations should treat the bug as
> "stacked repaints that accumulate during the session" — see
> proposed canonical name at top of file.

**Status:** Superseded 2026-05-15 by the DEC 2026 env-var fix (see entry
above). Kept here because the forensic profile, the rejected client-side
hold attempt, and the explicit "do not reintroduce `setTimeout` on the
receive path" reasoning are still load-bearing context — if the artifact
ever returns, this section is the starting point for re-investigation
before the fix entry above.

**Original status (2026-05-14):** Open, accepted as live-with after a
candidate fix was investigated, prototyped, and rejected on the analysis
below. The observed impact is small (a brief ~7 ms visual flash at one
resize moment, then the screen self-corrects); the available client-side
fixes re-open a much worse failure mode (see "Persistent process-wide
typing lag" further down in this file). The bug stays for now.

**Symptom:** An existing TUI block (Claude Code task list) overprints itself
at slightly different positions after a viewer transition or geometry change,
leaving characters dropped mid-word at fixed column positions for a short
moment. The screen then settles into the correct final layout. The C#
emulator's final state is **correct** — replay shows the right thing.
The artifact lives purely on the live render path while the burst of
post-resize bytes is in flight to xterm.js.

**Repro sessions:**

- `f3e25a1e-c0eb-4834-a3d2-0eace2bb0e1f` — 2026-05-13, short, best bisect
  candidate. Forensic profile below is from this one. Saved binary at
  `runbooks/terminal/repro-fixtures/session_f3e25a1e_resize_reprint.bin`.
- `dd5cc208-8b09-4473-8268-0a565a5bd55e` — 2026-05-07, long, original
  observation. Same byte-stream signature.

**Forensic profile (from f3e25a1e):**

- Exactly **one** geometry change at the bug moment: 150×10 → 150×28
  (rows only, cols unchanged). No `\e[8;…t` clustering, no `\e[?1049`
  toggles, no `\e[2J` outside the initial PowerShell prompt, no
  `\e[?2004l`, no alt-screen. Clean profile — same as `dd5cc208`.
- The original "fast successive fit/resize" hypothesis (occluded → visible
  transition spamming resize events) was **not** supported by the bytes
  in either session. There is one resize, then the artifact.
- At the resize moment (t≈+33 s), Claude Code emits **two** full-screen
  repaints 7 ms apart, both starting with `\e[H` (cursor home, no
  `\e[2J`):
  - Chunk `5783642` (1512 B, 05:29:25.181Z) — paints the bottom-of-screen
    TUI starting at row 1: status line, ls cmd, "Wibbling…" spinner,
    divider, prompt, mode indicator. **No banner, no user-input row.**
  - Chunk `5783643` (1974 B, 05:29:25.189Z, 7 ms later) — paints the
    full UI starting at row 1: Claude banner rows 1–3, user input
    row 5, then the same bottom TUI elements shifted down to rows 7–15.
- Each line in both chunks ends with `\e[K`, so cell-for-cell the second
  chunk fully overwrites the first. The visible bug is the brief moment
  xterm.js commits the first chunk's layout (bottom UI at rows 1–9)
  before the second chunk lands and replaces it (banner at rows 1–3,
  bottom UI shifted to 7–15).

**Root cause shape:**

Claude Code emits two full-screen frames in response to a single SIGWINCH
when the terminal grows. The first reflects "what was on screen at the
old size, repainted from row 1"; the second reflects "the full UI at the
new size, with the previously-off-screen banner restored above." If both
reach xterm in the same render task, no artifact. If they cross a task
boundary (which they did here, 7 ms apart), xterm commits the
intermediate state for one frame.

This is upstream behavior. Suppressing the first frame is the trap an
earlier fix attempt fell into ("Claude emitting dup sequences" — the two
frames are content-different, not duplicates; any dedup heuristic eats
legitimate first-paints across the rest of the terminal subsystem,
spinner ticks, scrollback recovery, and most non-resize redraws). Do
not go there.

**Candidate fix that was tried and rejected (Codex, 2026-05-14):**

Codex prototyped a client-side post-resize output hold in
`VibeRails/wwwroot/js/modules/terminal-tab.js`. Shape:

- New constants `RESIZE_OUTPUT_QUIET_MS = 40` and
  `RESIZE_OUTPUT_MAX_HOLD_MS = 250`.
- `sendResizeToPty()` armed a deadline
  `_pendingResizeOutputHoldDeadline = performance.now() + 250` right
  before `socket.send('__resize__:…')` (only when the cols×rows
  signature actually changed).
- `socket.onmessage` was rerouted through a new `queuePendingChunkFlush()`:
  - If `deadline > performance.now()` → schedule
    `setTimeout(flushPendingChunks, 40)`. Each new incoming chunk
    `clearTimeout`-cancels and reschedules, so the timer fires after
    40 ms of quiet.
  - Else → existing `queueMicrotask(flushPendingChunks)` path
    (cancelling any pending hold-timer first).
- Disconnect / `socket.onclose` paths cleared the new state.

In local source inspection the two f3e25a1e chunks (7 ms apart) would
land inside one timer-deferred `xterm.write()` and the visible artifact
would go away. The fix was reverted before any real webview validation
run. Two concrete reasons:

1. **Re-opens the `setTimeout` occlusion-throttle door.** See the
   "Persistent process-wide typing lag" Closed/Informational entry
   further down. Severity of that bug was process-lifetime: **every
   keystroke 1–3 s for the entire VS Code session**, only a full VS
   Code restart cleared it, and only fresh `Code.exe` launches (no
   warm parent process) could land in the throttled state. Codex's
   fix narrows the receive-path `setTimeout` to "the next ≤250 ms
   after a real geometry change," not every keystroke — so steady-state
   typing is unaffected. But during a throttled VS Code process,
   **every resize event** in the session (font bumps, panel drags,
   window fits, container resizes) would buffer the post-resize
   redraw burst for ~1 s. That is a real regression on a path that
   today is immediate.
2. **Late-firing callback corrupts subsequent-resize state.** The
   hold-timer callback unconditionally writes
   `this._pendingResizeOutputHoldDeadline = 0;` before flushing. Under
   throttle the callback fires ~1 s after it was scheduled. If a
   *second* resize landed in that ~1 s window and opened its own
   hold (extended deadline to a future value), the late callback from
   the first resize **wipes** that new deadline when it finally fires.
   The second resize's redraw burst then leaks through with no
   coalescing — the original reprint bug re-appears on the second
   resize. The callback has no "am I still the authoritative timer?"
   check (no compare against the current deadline, no signature
   check, no `performance.now()` re-evaluation). The minimal patch
   would be a guard like
   `if (this._pendingResizeOutputHoldDeadline <= performance.now()) { … }`
   in the callback, but that's a patch on a patch about late-firing
   timers under occlusion clamping — exactly the class of reasoning
   the 2026-05-05 fix migrated us away from.

The cost/benefit didn't pencil out: the bug being fixed is a one-time
~7 ms artifact with correct final state; the regressions risked are
1 s post-resize stalls during a session-wide throttle. Reverted.

**Why we are not fixing it yet:**

Every client-side fix considered so far either (a) re-introduces a
`setTimeout` on the `socket.onmessage` receive path — see the explicit
guardrail in the "Persistent process-wide typing lag" entry below — or
(b) requires reasoning about late-firing timers under VS Code webview
occlusion clamping. The cost of getting either wrong is much higher
than the cost of leaving the artifact in place.

The cleaner direction, **when this is revisited**, is server-side:
bump `WebSocketConsumer.NormalCoalesceDelayMs` (currently 4 ms) to ~20
for ~200 ms after a `__resize__` arrives on the server. That keeps the
client purely on `queueMicrotask`, never touches the throttle-prone
path, and the worst case is "post-resize echo is +16 ms slower" —
invisible. This has not been attempted; it is the recommended next try.

**How to verify the bug still exists (manual repro):**

1. Open a Claude Code (or Codex / Copilot) TUI session in the VibeRails
   web terminal or VS Code extension.
2. Let the TUI fill with content (task list, tool output, etc.) so the
   conversation has rows that would scroll above the viewport at a
   smaller height.
3. Resize the terminal pane sharply to **grow** the visible area — drag
   a panel divider so row count changes significantly (e.g. ~10 → ~28
   rows), toggle the side panel, or change font size step. The
   triggering geometry change is a single resize-up.
4. Watch the first paint after the resize. If reproducing: the bottom
   TUI briefly appears at the top of the viewport, then jumps down as
   the banner / earlier rows materialize above it. Sometimes presents
   as "characters dropped mid-word at fixed column positions" during
   the transient. The flash is brief (~7 ms — about one frame).
5. The artifact is not reliably reproducible. Repeat the resize a few
   times; some viewer states (occluded → visible transitions on
   webview return, very rapid successive fits) make it more likely.

**Repro forensics (use these when re-investigating):**

- Suspect chunks in the f3e25a1e SessionLogs: `5783642` (1512 B,
  partial paint) and `5783643` (1974 B, full paint), 7 ms apart at
  `05:29:25.181Z` and `05:29:25.189Z`.
- Dump them with:
  ```
  python python-scripts/decode_session.py f3e25a1e-c0eb-4834-a3d2-0eace2bb0e1f
  python python-scripts/show_chunks.py f3e25a1e-c0eb-4834-a3d2-0eace2bb0e1f 5783642 5783643
  ```
- Replay the bytes directly into the C# emulator or a headless xterm
  for an automated test:
  `runbooks/terminal/repro-fixtures/session_f3e25a1e_resize_reprint.bin`
  is the two chunks concatenated in arrival order (1512 B + 1974 B =
  3486 B). See that directory's README for the layout.
- **Signature check:** in both repro sessions there is exactly one
  `\e[2J` (the initial PowerShell prompt), one `\e[8;…t` (the boot
  geometry report), no `\e[?1049` toggles, no `\e[?2004l`, no
  alt-screen. If a future repro shows a *different* signature
  (extra `\e[2J`s, `?1049` toggles, multiple `\e[8;…t` reports), it
  is not this bug.

**When you pick this back up:**

1. Re-read this entry AND the "Persistent process-wide typing lag"
   entry below before doing anything. Do **not** re-propose a
   `setTimeout`-based receive-path coalesce without explicitly
   addressing both risks above and writing the "am I still
   authoritative?" guard into any timer callback.
2. Try the server-side coalesce bump first
   (`WebSocketConsumer.NormalCoalesceDelayMs` raised to ~20 ms for
   ~200 ms after a `__resize__` is received). Add a unit test under
   `Tests/Services/Terminal/` that feeds the two chunks from
   `session_f3e25a1e_resize_reprint.bin` through the consumer in
   post-resize state and asserts they emerge as one frame.
3. Only fall back to a client-side hold if the server path proves
   insufficient. Even then, prefer `queueMicrotask` chains over
   `setTimeout`, and never use a timer-throttled API
   (`setTimeout` / `requestAnimationFrame` / `setInterval`) on the
   receive path without the explicit "this throttle is
   process-lifetime under fresh-launch VS Code, not a brief
   cold-start hiccup" framing in mind.
4. The reprint is small. Don't trade it for the 2026-05-05 bug.

## 2026-04-27 Snapshot replay state reset contract

Reconnect must preserve the live xterm.js DOM node for stable fit/cell metrics,
but must not preserve xterm protocol state. A stale browser viewer may have
disconnected while it was in alternate screen, bracketed paste, mouse tracking,
synchronized output, application cursor, or a custom scroll region. The next
snapshot has to render from a known baseline.

What this pass does:

1. **Reconnect path** (`TerminalTab.connect()`) — preserves the existing xterm
   DOM (no more `disposeTerminalInstance()` on every reconnect) and instead
   calls `VibeTerminal.resetForSnapshotReplay()`. That uses xterm's full reset
   to clear parser/mode state without remounting DOM children, then reapplies
   VibeRails cursor/theme bookkeeping. This restores the old dispose/new-xterm
   factory-baseline behavior without the reconnect measurement bug caused by
   remounting xterm DOM (synchronous `fit()` could measure cell metrics before
   the browser painted them).
2. **Backend snapshot replay** (`TerminalGridSerializer.Serialize`) — every
   snapshot now starts with an explicit reset prologue that exits alternate
   screen (`?1049/1047/47 l`), disables known transient private modes
   (`?2004`, mouse `?1000-1007/1015`, focus `?1004`, sync `?2026`, app cursor
   `?1`, origin `?6`), enables autowrap (`?7h`), restores normal keypad
   (`ESC >`), resets G0/G1 charsets (`ESC (B`, `ESC )B`), restores the
   full-screen scroll region (`CSI r`), resets attributes (`SGR 0`), hides the
   cursor (`?25l`), clears visible screen and scrollback (`2J`/`3J`), and
   homes the cursor (`CUP 1,1`) before painting rows.
3. If the server emulator is currently in alternate screen, the serializer
   re-enters `?1049h` after the baseline reset and paints the alternate-screen
   grid there. Otherwise the snapshot stays on main screen and scrollback rows
   are replayed normally.

What this pass does **not** do, intentionally:

- **Active-socket refresh** (`TerminalTab.requestViewerSnapshotReplay()` —
  the manual refresh button while the WebSocket stays open) does **not** call
  `resetForSnapshotReplay()` locally. The socket is open and live PTY bytes
  may already be in flight in the WS pipe between the `__cmd__:replay` send
  and the snapshot reply. A local reset would briefly paint those in-flight
  bytes onto a blank terminal until the snapshot prologue lands. The server
  prologue alone is sufficient on this path because xterm.js and the C#
  emulator have been seeing the same byte stream — there is no disconnected
  drift to recover from. This matches the design that shipped in the
  2026-04-26 reconnect double-print fix and is covered by
  `Session_9e670449_ReconnectRegressionTests`.
- The snapshot prologue does not yet serialize every active TUI mode. If
  reconnect paste/mouse semantics need full fidelity later, the C# emulator
  must track those modes and the serializer must reapply them after the
  baseline reset.

Regression coverage (`Tests/Services/Terminal/TerminalGridSerializerTests.cs`):

- stale viewer alt-screen + server main-screen snapshot exits alt-screen
  before painting
- server alt-screen snapshot still re-enters alt-screen before painting
- snapshot prologue includes resets for the known transient modes that caused
  stale browser state leaks

Guardrails for future changes:

- Do not reintroduce a local `resetForSnapshotReplay()` on the active-socket
  refresh path. The flicker window it opens is real (live in-flight bytes
  paint on a blank terminal); the server prologue is the single source of
  truth there.
- Do not go back to disposing and recreating the xterm instance on reconnect.
  Synchronous `fit()` against a freshly-remounted DOM measures stale cell
  metrics. Reuse the DOM and reset protocol state instead.
- If a previously-fixed bug starts showing up after a reconnect or refresh,
  first check whether a new transient private mode escaped the prologue's
  disable list before reaching for any kind of redraw poke.

---

## 2026-04-09 Attach path unified: "be a correct terminal, no per-CLI hacks"

This repo no longer contains any per-CLI attach-path branching. The design
principle going forward:

> **The terminal backend must behave as a correct PTY/ANSI/VT100 implementation.
> If a CLI misbehaves, either our emulator is wrong (and the fix benefits
> everyone) or the CLI is wrong (and it's upstream's job to fix). No
> `if cliName == X then do Y` branches.**

### What changed

1. **Deleted `TerminalReplayPolicy.cs`** — the `codex → redraw-attach` carveout
   is gone. Local and remote attach use one path for every CLI.
2. **New atomic primitive `Terminal.SubscribeWithSnapshot(ITerminalConsumer)`**
   captures the emulator state, delivers it to the consumer as its first
   output, and subscribes it to live PTY bytes — all under
   `_subscriberLock` + `_emulatorLock`. Guarantee: the consumer sees
   `(snapshot at time T)` followed by `(every PTY byte dispatched after T)`
   in order, with no gap and no duplication.
3. **`Terminal.PushSnapshotTo(ITerminalConsumer)`** is the companion for
   already-subscribed consumers (remote PIN verify, remote replay request).
   The snapshot begins with `ED2` + `ED3` + cursor home so it is
   self-healing — whatever the viewer had on screen is wiped and rebuilt.
4. **`ReadLoopAsync` and `PublishOutput` now hold `_subscriberLock` across the
   full dispatch.** Previously they snapshotted the consumer list under lock
   then iterated outside. Holding the lock for the whole dispatch is what
   makes `SubscribeWithSnapshot` atomic: a new subscriber can only join
   *between* dispatches, never mid-dispatch.
5. **Removed every `Ctrl+L`-on-attach poke.** The old "send Ctrl+L to cover
   the gap between snapshot capture and subscription" hack is gone from both
   `TerminalSessionService.HandleWebSocketAsync` and
   `TerminalRunner.HandleRemoteReplayRequestAsync` /
   `HandleRemoteInputAsync` (pin verify). There is no gap anymore, so there
   is nothing to cover.
6. **Removed `replayInProgress` pause flag** from `TerminalRunner` remote
   path. `RemoteOutputConsumer.canForward` now only checks viewer
   authorization, not replay state.
7. **Removed unused `s_activeCli` state** from `TerminalSessionService` —
   attach was its only reader.

### Why this fixes the stacked-banner bug

Claude Code (and Codex/Copilot) react to `Ctrl+L` by emitting a full TUI
repaint that includes the banner. Because those CLIs run on the main screen
(no `DECSET 1049`), each repaint's banner rows eventually scroll into the
emulator's scrollback via normal scroll-up. Over multiple reconnects the
scrollback accumulates one stacked banner per reconnect.

With the Ctrl+L poke removed and the subscribe/snapshot race fixed properly,
there is no more spurious repaint on attach — the viewer sees exactly what
was on screen, nothing more.

### Invariant that must be preserved

All `ITerminalConsumer.OnOutput` implementations must be non-blocking. The
contract is documented on the interface. If a future consumer wants to do
blocking I/O in `OnOutput`, it must queue to a background worker (the way
`WebSocketConsumer` and `RemoteTerminalConnection` already do via their
internal channels). Otherwise `ReadLoopAsync` will stall and PTY output will
back up.

### What we are explicitly not doing

- No workaround for Codex flicker. If flicker remains after this pass, it's
  either a genuine emulator correctness bug (which we'll chase on its own
  merits) or Codex's problem. Do not re-add a per-CLI branch to paper it over.
- No poking TUIs with Ctrl+L, SIGWINCH, or any other redraw hint as a
  synchronization mechanism. Real terminals don't do this.

Validation after this pass:

- `dotnet test .\TerminalEmulator.Tests\TerminalEmulator.Tests.csproj --nologo`
  -> 764 passed
- `dotnet test .\Tests\Tests.csproj -o /tmp/vb-tests --nologo`
  -> 168 passed

---

## 2026-03-18 follow-up audit after backend/emulator hardening

This repo has now landed the terminal backend/emulator hardening pass plus expanded regression
coverage.

Status against the 2026-03-17 review:

- Fixed before this pass: finding 9 (`ESC \` termination inside DCS/APC/PM/SOS passthrough).
- Fixed in this pass: findings 1, 2, 3, 4, 5, 6, 7, 8, 11, 12, 13, 14, 15, 16, and 17.
- Resolved as documentation/architecture drift: finding 10. The current attach policy is the
  atomic `SubscribeWithSnapshot` primitive (see 2026-04-09 section) — one code path for every
  CLI, with no post-attach redraw poke. The old managed-AI "skip replay" note and the
  "codex → redraw-attach" carveout later in this file are both historical and superseded.
- Reduced but not fully eliminated: finding 18. Stale cleanup is now activity-aware
  (`SessionLogs` / `UserInputs`) instead of age-only, but it still is not a true OS-process
  liveness check.

New regression coverage added in this pass:

- `CSI 3 J` clears scrollback without erasing the visible screen
- multi-parameter private CSI mode handling applies every mode in the sequence
- resize marks newly-added rows dirty
- supplementary-plane glyphs round-trip through snapshot/screen text
- snapshot normalization now collapses surrogate pairs consistently for golden fixtures

Validation after the hardening pass:

- `dotnet test .\TerminalEmulator.Tests\TerminalEmulator.Tests.csproj --no-restore --nologo`
  -> 756 passed
- `dotnet test .\Tests\Tests.csproj --no-restore --nologo`
  -> 139 passed

---

## 2026-03-17 Deep backend C# code review (historical snapshot)
I found several remaining C#-side hazards that can still produce:

- duplicate or orphaned terminal sessions
- output loss or reordered DB logs under heavy throughput
- unbounded memory growth when a browser/relay cannot keep up
- replay fidelity drift after scrollback clears / remote reattach
- parser desynchronization on less-common escape sequences
- CLI session hosts that can linger after the PTY has already exited
- cleanup paths that are not fully transactional


### Backend architecture summary (current)

The current C# flow is:

1. Session startup enters through `TerminalRoutes`, `TerminalTabsRoutes`, or the CLI bootstrap path
   in `CliLoop` / `TerminalRunner`.
2. `TerminalRunner.CreateSessionAsync()` creates the DB session, builds launch command/env, creates
   the PTY-backed `Terminal`, wires emulator/logging consumers, optionally wires the remote relay,
   then sends the launch command.
3. `Terminal.ReadLoopAsync()` is the single PTY output source. It fans each chunk out synchronously
   to all current `ITerminalConsumer`s.
4. `TerminalEmulatorConsumer` builds the in-memory screen state; `DbLoggingConsumer` writes PTY
   output into the session DB; `WebSocketConsumer` and `RemoteOutputConsumer` forward bytes to local
   and remote viewers.
5. Reconnect / local attach goes through
   `TerminalSessionService.HandleWebSocketAsync()`, which pre-resizes the PTY and then calls
   `Terminal.SubscribeWithSnapshot(consumer)` — one atomic operation that captures emulator state,
   delivers it as the consumer's first output, and subscribes the consumer to live PTY bytes, all
   under `_subscriberLock`. No post-attach Ctrl+L poke. (See the 2026-04-09 section above.)
6. Stop / exit / teardown is split across `TerminalSessionService.StopSessionAsync()`,
   `CleanupAsync()`, the PTY `Exited` event, `Terminal.DisposeAsync()`, and
   `TerminalStateService.CompleteSessionAsync()`.

That architecture is reasonable. The remaining problems are mostly around **serialization of
ownership transitions**, **backpressure**, and **state-model fidelity**.

### High-severity findings

#### 1. `StartSessionAsync()` has a real single-session race

`TerminalSessionService.StartSessionAsync()` checks `s_terminal` under `s_lock`, but it does **not**
reserve the slot before awaiting `_runner.CreateSessionAsync()`. That means two concurrent callers
can both observe `s_terminal == null`, both leave the lock, and both create PTYs/sessions. The
route layer does a separate `HasActiveSession` pre-check, but that does not close the race because
it is outside the same critical section.

Files:
- `VibeRails/Services/Terminal/TerminalSessionService.cs:74-120`
- `VibeRails/Routes/TerminalRoutes.cs:20-87`

Why this matters:
- two PTYs can be created even though the design assumes only one
- one session can overwrite static ownership state created by the other
- leaked/orphaned sessions become much more likely when startup fails mid-flight

This is one of the highest-priority hardening items.

#### 2. PTY output logging is fire-and-forget, unordered, and unbounded

The PTY read loop is synchronous, but DB logging is not. `DbLoggingConsumer.OnOutput()` calls
`TerminalStateService.LogOutput()`, and that method immediately launches an unawaited async insert:

`_ = _dbService.LogSessionOutputAsync(sessionId, data.ToArray(), false);`

Files:
- `VibeRails/Services/Terminal/Consumers/DbLoggingConsumer.cs:17-20`
- `VibeRails/Services/Terminal/TerminalStateService.cs:81-97`
- `VibeRails/Services/DbService.cs:169-186`

Why this matters:
- every PTY chunk creates a new asynchronous SQLite write task
- heavy TUI output can create a very large number of concurrent pending writes
- SQLite will serialize the actual writes, but task scheduling can still drift from PTY emission
  order, so `SessionLogs.Id` is not guaranteed to remain a perfect "PTY order" proxy
- shutdown can complete the session before all pending output writes have drained

This is a major stability risk because it affects **memory**, **ordering**, and **durability** all
at once. The backend needs a **bounded single-writer output pipeline** here, not raw fire-and-forget
tasks.

#### 3. Local and remote output queues are both unbounded

`WebSocketConsumer` copies every PTY chunk into an unbounded `Channel<byte[]>`. The remote relay
path does the same thing in `RemoteTerminalConnection` with an unbounded outbound channel.

Files:
- `VibeRails/Services/Terminal/Consumers/WebSocketConsumer.cs:12-31`
- `VibeRails/Services/Terminal/Consumers/WebSocketConsumer.cs:39-57`
- `VibeRails/Services/Terminal/RemoteTerminalConnection.cs:15-18`
- `VibeRails/Services/Terminal/RemoteTerminalConnection.cs:68-72`
- `VibeRails/Services/Terminal/RemoteTerminalConnection.cs:180-209`

Why this matters:
- a slow browser, hidden tab, broken socket, or slow relay can fall behind indefinitely
- the producer side never slows down and never drops
- each frame is copied first, so backlog growth is pure memory growth

Under a noisy TUI, this can turn into a process-wide memory problem surprisingly fast. A bounded
queue with an explicit policy is needed: backpressure, disconnect-on-overflow, or drop-old frames.
Right now the policy is effectively "allocate until the process hurts."

#### 4. Startup failure is not transactional and can leak PTYs / sessions

`TerminalRunner.CreateSessionAsync()` creates the DB session first, then creates the PTY, then wires
consumers/remote state, then sends the launch command. If anything throws **after** the PTY exists
but **before** the method returns, the caller has no reference to the created terminal yet.

`TerminalSessionService.StartSessionAsync()` catches and calls `CleanupAsync()`, but `s_terminal`
has not been assigned at that point, so there is nothing for `CleanupAsync()` to dispose.

Files:
- `VibeRails/Services/Terminal/TerminalRunner.cs:31-59`
- `VibeRails/Services/Terminal/TerminalRunner.cs:324-327`
- `VibeRails/Services/Terminal/TerminalSessionService.cs:81-127`

Why this matters:
- PTY process can leak on launch failure
- DB session row can remain open even though startup failed
- remote registration may be partially completed

This should be converted into a local transactional pattern inside `CreateSessionAsync()`: create,
wire, and on any failure dispose terminal + complete session before rethrowing.

#### 5. PTY exit handling is wired through an `async void` event handler

`terminal.Exited += async (sender, exitCode) => { ... }` is attached inside
`TerminalSessionService.StartSessionAsync()`. Because `Exited` is an `EventHandler<int>`, this
lambda is effectively `async void`.

Files:
- `VibeRails/Services/Terminal/TerminalSessionService.cs:100-106`
- `VibeRails/Services/Terminal/Terminal.cs:35-37`
- `VibeRails/Services/Terminal/Terminal.cs:268-278`

Why this matters:
- exceptions after the first `await` do not flow in a controllable way
- completion/cleanup ordering becomes much harder to reason about
- it is easy to accidentally create double-complete / double-cleanup paths that "usually work"
  until one throws at the wrong time

Even if most of the current paths are idempotent enough to survive double calls, the pattern itself
is fragile. Exit completion should be funneled through an explicit task-returning method with a
single idempotent gate.

#### 6. `ED3` (`CSI 3 J`) is modeled incorrectly in the emulator

This is one of the most important replay-fidelity bugs still in the backend.

`TerminalBuffer.EraseInDisplay(3)` explicitly treats mode 3 the same as mode 2:

> `case 3: // whole screen + scrollback (treat same as 2 for now)`

That is wrong in two ways:

1. it does **not** clear the emulator's scrollback ring
2. it **does** clear the visible screen by falling through to whole-screen erase semantics

The second point matters because the serializer itself uses `2J` **and then** `3J`, which strongly
implies the intended model is:

- `2J` clears the visible screen
- `3J` clears scrollback

Files:
- `TerminalEmulator/TerminalBuffer.cs:164-183`
- `TerminalEmulator/TerminalBuffer.cs:342-350`
- `VibeRails/Services/Terminal/TerminalGridSerializer.cs:34-37`

Why this matters:
- shells / TUIs can intentionally clear saved history with `CSI 3 J`
- the live terminal drops that scrollback
- the emulator keeps it
- reconnect replay can therefore resurrect history the app intentionally cleared
- if `3J` is sent without `2J`, the emulator also clears the visible screen when it should not

This is not cosmetic. It is exactly the kind of "I know I cleared that, why did it come back after
reconnect?" problem that makes terminal state feel haunted.

Hardening direction:
- add an explicit **clear scrollback** operation to `TerminalBuffer`
- implement true `ED3` semantics instead of aliasing to `ED2`
- add replay tests proving that after `CSI 3 J`, old scrollback does not come back on reconnect

#### 7. Remote replay currently depends on hidden-cursor replay without a guaranteed redraw, and resize is not serialized with replay

`TerminalGridSerializer.Serialize()` always starts replay with `?25l` and intentionally does **not**
restore cursor visibility. The local WebSocket attach path compensates by sending `Ctrl+L` after the
snapshot, which lets the app redraw and re-establish cursor state. The remote replay paths do **not**
do that.

There is a second problem here: remote resize is not funneled through the same replay gate.
`OnResizeRequested` applies PTY resize immediately, while replay capture/sending runs behind
`takeoverGate`. That means remote replay and remote resize are not fully serialized as one state
transition.

Files:
- `VibeRails/Services/Terminal/TerminalGridSerializer.cs:31-69`
- `VibeRails/Services/Terminal/TerminalSessionService.cs:213-221`
- `VibeRails/Services/Terminal/TerminalRunner.cs:149-155`
- `VibeRails/Services/Terminal/TerminalRunner.cs:220-233`
- `VibeRails/Services/Terminal/TerminalRunner.cs:277-295`

Why this matters:
- local replay has an explicit redraw follow-up
- remote replay currently does not
- remote correctness is therefore dependent on later terminal output or later user input
- a resize can race with remote replay and produce a snapshot at the wrong geometry boundary

That is not a reliable invariant. If replay requires redraw for correctness, **every** replay path
must enforce it, or the serializer needs to become self-sufficient again in a safer way.

#### 8. Native CLI session lifetime can hang because the console input loop is not tied to PTY exit and `Console.ReadKey()` is not cancellable

The CLI paths (`RunCliAsync()` and `RunCliWithWebAsync()`) both block on `ConsoleInputLoopAsync()`.
That loop only checks `ct.IsCancellationRequested`, but the actual wait is `Console.ReadKey()`,
which is synchronous and not cancellable. It is also not connected to terminal exit.

Files:
- `VibeRails/Services/Terminal/TerminalRunner.cs:388-418`
- `VibeRails/Services/Terminal/TerminalRunner.cs:425-489`
- `VibeRails/Services/Terminal/TerminalRunner.cs:495-511`

Why this matters:
- if the PTY process exits naturally, the CLI host can keep waiting for keyboard input
- if cancellation is requested, `Console.ReadKey()` may still sit there until another keypress
- external-terminal unregister/cleanup in `RunCliWithWebAsync()` is delayed until that loop finally
  unwinds

This can create very confusing behavior where the terminal process looks finished, but the host
process, web exposure, or cleanup path lingers.

#### 9. The parser can desynchronize on DCS/APC/PM/SOS strings terminated with `ESC \`

The parser enters `DcsPassthrough` for `ESC P` (DCS) and also for SOS / PM / APC. In that state it
only exits on BEL or `0x9C`. It does **not** handle the common `ESC \` string terminator while in
`DcsPassthrough`.

Files:
- `TerminalEmulator/AnsiParser.cs:113-116`
- `TerminalEmulator/AnsiParser.cs:139-145`
- `TerminalEmulator/AnsiParser.cs:168-171`

Why this matters:
- if a DCS-like string is terminated with `ESC \`, the parser can stay in passthrough mode
- once that happens, subsequent PTY output can be swallowed or ignored until another recognized
  terminator appears
- this is a classic state-machine desync bug: rare, catastrophic, and hard to reproduce

This is the kind of parser bug that can make a session suddenly look blank, frozen, or partially
missing after one unexpected sequence.

#### 10. The code no longer enforces the documented "managed AI CLI skip replay" rule

This file currently says managed AI CLIs (`Claude`, `Codex`, `Antigravity`, `Copilot`) should skip local
replay and use redraw-first attach. The current C# implementation does **not** do that. It reads
`s_activeCli`, but does not branch on it. It always sends `GetGridReplay()`, then subscribes the
consumer, then sends `Ctrl+L`.

Files:
- `VibeRails/Services/Terminal/TerminalSessionService.cs:130-221`
- `TERMINAL.md:84-107` (existing tracker note describing the old guardrail)

Why this matters:
- a documented regression guardrail is no longer encoded in the backend
- if unified replay is now considered safe, the tracker/doc needs to say so explicitly
- if it is **not** safe, the code is currently missing an important safety branch

For a bug class that has already regressed before, documentation/code drift here is dangerous.

### Medium-severity findings

#### 11. Stop/cleanup does not explicitly close the active local WebSocket

`StopSessionAsync()` completes the session and then calls `CleanupAsync()`. `CleanupAsync()` clears
terminal/session state and disposes the terminal, but it does **not** close `s_activeWebSocket`.
That socket is only cleared in the WebSocket handler finally block or via explicit disconnect/takeover.

Files:
- `VibeRails/Services/Terminal/TerminalSessionService.cs:299-315`
- `VibeRails/Services/Terminal/TerminalSessionService.cs:317-339`
- `VibeRails/Services/Terminal/TerminalSessionService.cs:581-610`
- `VibeRails/Services/Terminal/TerminalSessionService.cs:387-473`

Why this matters:
- browser can remain connected while the terminal has already been torn down
- input loop can continue running against a disposed terminal instance
- close semantics become timing-dependent instead of explicit

The stop path should explicitly disconnect the active local viewer before or during cleanup.

#### 12. `Terminal.DisposeAsync()` waits for read-loop exit before killing the PTY

Current ordering is:

1. cancel `_cts`
2. await `_readLoop`
3. `_pty.Kill()`

Files:
- `VibeRails/Services/Terminal/Terminal.cs:211-227`

Why this matters:
- if the PTY read does not unblock promptly on cancellation, dispose can hang
- shutdown behavior is then dependent on stream cancellation semantics of the PTY layer

The safer pattern is usually to close/kill the producer first (or race wait-vs-kill with a timeout),
then await the read loop.

#### 13. Initial terminal title injection still writes OSC to PTY stdin instead of the output path

`Terminal.CreateAsync()` still writes the title-setting OSC sequence directly to `pty.WriterStream`.
That is PTY **stdin**, not terminal output. Elsewhere in the backend, the code already recognizes
that title OSC must go through the output path via `Terminal.PublishOutput()` rather than stdin.

Files:
- `VibeRails/Services/Terminal/Terminal.cs:74-80`
- `VibeRails/Services/Terminal/Terminal.cs:149-155`
- `VibeRails/Services/Terminal/TerminalRunner.cs:84-89`

Why this matters:
- on ConPTY / terminal-emulator stacks, OSC on stdin is not a reliable title mechanism
- best case, the title does not change
- worse case, the shell or TUI receives raw control bytes as input

This is not necessarily the highest-frequency bug, but it is an unnecessary source of startup
weirdness and it directly contradicts the newer output-path guidance already present elsewhere in
the code.

#### 14. Non-BMP / supplementary wide-character handling is lossy

The emulator stores only one `char` per cell. For code points above `U+FFFF`, `AnsiParser` writes
only the high surrogate and marks the cell wide; it does **not** store the actual rune as a complete
logical character.

Files:
- `TerminalEmulator/AnsiParser.cs:547-566`
- `TerminalEmulator/TerminalBuffer.cs:70-105`
- `VibeRails/Services/Terminal/TerminalGridSerializer.cs:79-97`
- `VibeRails/Services/Terminal/TerminalGridSerializer.cs:109-127`

Why this matters:
- emoji / supplementary-plane glyphs cannot round-trip correctly
- serializer can emit invalid or replacement output
- cell-width and glyph-content accuracy diverge

This will not break every CLI, but it is absolutely the kind of thing that creates "random visual
weirdness" once a tool prints emoji, fancy status icons, or non-BMP glyphs.

#### 15. `TerminalBuffer.Resize()` does not resize `_dirtyRows`

The emulator reallocates `_normal` and `_alternate` on resize, but `_dirtyRows` is `readonly` and
never resized.

Files:
- `TerminalEmulator/TerminalBuffer.cs:40-41`
- `TerminalEmulator/TerminalBuffer.cs:303-331`
- `TerminalEmulator/TerminalBuffer.cs:362-374`

Why this matters:
- rows added during growth can never be marked dirty
- `OnRender` becomes incorrect after certain resizes

This is not on the hottest VibeRails runtime path today because replay uses full snapshots, not
dirty-row incremental rendering. Still, it is a correctness bug inside the emulator core.

#### 16. Private CSI mode handling only applies the first mode parameter

`DispatchCsiPrivate()` is called with only `p0`, even if the escape sequence contained multiple
private mode params.

Files:
- `TerminalEmulator/AnsiParser.cs:275-335`
- `TerminalEmulator/AnsiParser.cs:337-364`

Why this matters:
- multi-mode private sequences are only partially modeled
- most mouse/focus modes are intentionally ignored, but the limitation is still real
- future terminal behavior can regress silently if an important mode is not first

#### 17. Remote registration/deregistration is awaited on the critical path despite the comment

`RemoteStateService` claims these are "fire-and-forget operations that don't block terminal
startup/shutdown." They are not. `RegisterTerminalAsync()` and `DeregisterTerminalAsync()` both
await `HttpClient.SendAsync()`, and `TerminalStateService` awaits those methods during create and
complete.

Files:
- `VibeRails/Services/Terminal/RemoteStateService.cs:11-18`
- `VibeRails/Services/Terminal/RemoteStateService.cs:50-79`
- `VibeRails/Services/Terminal/RemoteStateService.cs:86-108`
- `VibeRails/Services/Terminal/TerminalStateService.cs:67-70`
- `VibeRails/Services/Terminal/TerminalStateService.cs:215-219`

Why this matters:
- a slow or unhealthy remote service can delay local startup/teardown
- the comment currently overstates the safety of this path

#### 18. `StaleSessionCleanupJob` can mark live sessions as stale based on age alone

The job looks for any DB session with `EndedUTC IS NULL` and `StartedUTC < now - 5 minutes` and
marks it complete with exit code `-1`. It does not check whether the session is still running or
still receiving activity.

Files:
- `VibeRails/Jobs/StaleSessionCleanupJob.cs:27-39`
- `VibeRails/Services/DbService.cs:188-203`

Why this matters:
- long-running live sessions can be marked "ended" in history while they are still active
- cleanup semantics are based on age, not liveness

This may not kill the PTY, but it absolutely weakens the reliability of session-state diagnostics.

### Lower-severity / diagnostic findings

- `Terminal.PublishOutput()` swallows all consumer exceptions silently, which makes output-path
  failures harder to debug. `VibeRails/Services/Terminal/Terminal.cs:155-165`
- `InputAccumulator` also uses an unbounded channel. It is lower risk than PTY output because it
  operates on completed lines, but it still has no hard cap. `VibeRails/Utils/InputAccumulator.cs:20-22`
- `TerminalIoObserverService` fan-out is fire-and-forget and unbounded. Fine for lightweight
  observers, but risky if observers become expensive. `VibeRails/Services/Terminal/TerminalIoObserverService.cs:48-100`
- `CommandService` ignores `initialPrompt` entirely right now, which is not a stability bug but is
  surprising API behavior. `VibeRails/Services/Terminal/CommandService.cs:34-87`

### What is already solid

These parts are materially better than the old design and should be preserved:

- **One PTY read loop per terminal** with synchronous fan-out from a single source:
  `VibeRails/Services/Terminal/Terminal.cs:229-279`
- **Snapshot-before-subscribe ordering** in local attach, which avoids replay/live interleave:
  `VibeRails/Services/Terminal/TerminalSessionService.cs:186-209`
- **Pre-resize before replay**, which is the right fix for stale-geometry double-draw:
  `VibeRails/Services/Terminal/TerminalSessionService.cs:164-183`
- **Emulator-backed replay** instead of breakpoint-based raw byte replay:
  `VibeRails/Services/Terminal/Terminal.cs:173-188`
- **Session lifecycle owner tracking** for watchdog survival:
  `VibeRails/Services/LocalClientLifecycle.cs:94-139`,
  `VibeRails/Services/Terminal/TerminalSessionService.cs:118,211,240,258,281,599`
- **Parent PID watchdog** in tab children to prevent orphan child servers:
  `VibeRails/Program.cs:282-356`

### Hardening order I would recommend

If the goal is "make this backend as bulletproof as possible," I would harden in this order:

1. **Serialize session startup/shutdown with a real process-global gate**
   - Fix the `StartSessionAsync()` race first.

2. **Replace fire-and-forget DB output logging with a bounded single-writer pipeline**
   - This is the biggest operational stability gap in the current code.

3. **Bound local/remote output queues**
   - Pick an explicit overflow policy instead of allowing unbounded growth.

4. **Make startup and shutdown transactional**
   - No leaked PTYs, no partially-open DB sessions, no open sockets after stop.

5. **Fix emulator fidelity around `ED3`, cursor state, remote replay, and parser desync**
   - This is the path most likely to create "hard to reproduce" visual regressions.

6. **Fix native CLI lifetime handling**
   - `Console.ReadKey()` must not be the thing keeping dead PTY sessions alive.

7. **Remove `async void` session-exit completion**
   - Move to an idempotent, task-based completion path.

8. **Decide the attach policy for managed AI CLIs and encode it in code**
   - Either replay-first is now safe and should be documented/tested, or the old skip-replay
     policy should be restored.

9. **Improve wide/non-BMP handling if fidelity matters**
   - Especially if modern CLIs with emoji/status glyphs are expected.

### Testing note

The old POC still has useful emulator tests in:

- `C:\Users\robst\Desktop\headless\TerminalEmulator.Tests\AnsiParserTests.cs`
- `C:\Users\robst\Desktop\headless\TerminalEmulator.Tests\FixtureReplayTests.cs`

Those are worth porting into this repo, but the highest-value missing tests for the current backend
are actually **serializer/replay pipeline tests**:

- `TerminalGridSerializer` replay should preserve cleared scrollback semantics
- local replay and remote replay should have explicit cursor-state expectations
- chunked PTY output vs full replay should produce identical snapshots
- stop/exit/start races should be covered with session-lifecycle tests

Terminal problem tracker for the Web UI terminal stack.

Date started: 2026-03-07

## Active Issues

**Audit (2026-05-01):** these four are all backend hardening polish — accepted
to live with for now, not user-visible bugs. None are "ConPTY-style upstream"
in the strict sense, but each is a "won't bite us in normal operation" item:

- stale-session cleanup is activity-aware now, but still not true process-liveness detection (accepted: false-positives only on idle but truly-running sessions)
- `InputAccumulator` still uses an unbounded channel (accepted: bounded by user typing speed)
- `TerminalIoObserverService` still fans out on fire-and-forget tasks with no hard cap (accepted: only used by lightweight observers today)
- end-to-end lifecycle/replay race coverage is still thinner than the emulator/parser regression suite (accepted: tracked, not blocking)

---

## Deferred / Parked

### Native CLI remote alerting deferred — remote disabled for native sessions

The title-bar notification approach for alerting the local user when a remote viewer connects
proved unreliable (OSC title gets overwritten by the TUI/shell immediately). A proper alerting
layer is planned (interactive system sitting in front of all sessions). Until then, remote
access is disabled for native CLI sessions via `_nativeRemoteEnabled = false` in
`TerminalRunner.ShouldEnableRemote`. Web terminal remote access is unaffected.

**To re-enable:** flip `_nativeRemoteEnabled = true` in `TerminalRunner.cs`.

Key file: `VibeRails/Services/Terminal/TerminalRunner.cs` — `_nativeRemoteEnabled`, `ShouldEnableRemote`

---

## Fixed Issues

### ✅ Output-flush mechanism A/B: `queueMicrotask` vs `scheduler.postTask` (2026-05-07, settled 2026-05-13)

**Settled — `queueMicrotask` is the keeper.** This entry only closes the flush-
mechanism question. The broader double-print/overprint bug it was meant to
diagnose is **still open** and lives in the open-bug section at the top of this
file — the flush path was a red herring; the resize path is the current
hypothesis.

**Background:** while chasing the post-occlusion overprint bug, a runtime A/B
was rolled out so Rob could compare `queueMicrotask` against
`scheduler.postTask` 10 ms batching as the `socket.onmessage` flush mechanism
in `terminal-tab.js`. The toggle lived under Terminal Settings → "Output
Coalescing" and was backed by `setOutputCoalesceMode` / `_outputCoalesceMode` /
the `viberails_terminal_outputCoalesce` localStorage key.

**Outcome (2026-05-07):** `scheduler.postTask` mode felt **slow in practice** —
possibly delay clamping under the VS Code webview, possibly scheduler queuing
behavior, root cause not isolated. `queueMicrotask` mode was the better daily
driver and did not, on its own, cause the overprint. The `postTask` mode was
therefore removed: the Terminal Settings toggle, the `setOutputCoalesceMode` /
`_outputCoalesceMode` fields, and the `viberails_terminal_outputCoalesce`
localStorage key are gone, and `socket.onmessage` in `terminal-tab.js` always
uses `queueMicrotask(flushPendingChunks)`. The historical "we tried postTask"
context is preserved in the comment above the flush call so we don't
re-introduce it unthinkingly.

**Guardrail:** do not re-add a `postTask` / `setTimeout` coalesce variant
without a fresh reason. The typing-lag fix below depends on `queueMicrotask`
not being throttled the way `setTimeout` is — and when that bug bit, the
1–3 s per-keystroke lag persisted for the **entire lifetime of the VS Code
process** (not just until first paint), with only a full VS Code restart
clearing it. Any new `setTimeout`-bearing path in `socket.onmessage` is
re-opening that door.

### ✅ Persistent process-wide typing lag (~800–3000 ms echo) caused by Chromium throttling our setTimeout coalesce (2026-05-05, verified 2026-05-06)

**Verified fixed in v1.6.12** (commit `989afd1`). End-to-end checks passed:
echo is tight from the first keystroke after a full `Code.exe` + `vb.exe`
teardown, no Codex/Copilot tear regression observed, and `scheduler.postTask`
mode also passed the test at the time. The `scheduler.postTask` runtime A/B
was later removed (2026-05-07) — see the Open Bugs entry at the top of this
file for why.

**Symptom:** When the bug was active, **every keystroke** in the VibeRails
dashboard echoed at 1–3 s (sometimes longer), and the lag **persisted for the
entire lifetime of the VS Code process**, not just the first few seconds.
Closing and reopening the extension within the same VS Code window did **not**
fix it. The only known recovery was fully quitting VS Code and starting it
again — sometimes requiring more than one restart attempt before the next
launch came up clean. Reproducibility was tied to whether VS Code launched
from a truly **fresh process state** (no surviving `Code.exe` in the
background): launches from a fresh state could land in the throttled mode and
stay there; launches that reused an already-warm parent process did not
trigger it. Hence "cold start" undersells the symptom — once a VS Code process
got into the clamped state, it stayed there until that process exited.

**How it presented in measurements:** server round-trip (`Send → Recv`) was
healthy at ~46 ms. Renderer end-to-end (`Recv → Commit`) was healthy at ~16 ms.
But Chrome Performance trace `TimerInstall` / `TimerFire` events showed
short-target timers (requested ~10 ms) firing at:

```
p25:    0.6 ms        ← when not throttled
p50:  801.3 ms        ← median: 80× over budget
p75:  980.5 ms
p95: 1353.0 ms
p99: 2992.6 ms        ← worst: 300× over budget
```

12 of 19 short timers in a 96 s trace fired in 760–2993 ms instead of 10 ms.

**Root cause:** Chromium clamps `setTimeout` to a 1-second minimum when the
renderer reports the frame as "occluded" (`visible_content_area: 0`). When a
fresh-start VS Code webview entered that state, it could **stay** in the
clamped condition for the lifetime of the VS Code process — not just until
the first workbench paint. Our `socket.onmessage` in
`VibeRails/wwwroot/js/modules/terminal-multitab.js` used
`setTimeout(flushPendingChunks, OUTPUT_WRITE_COALESCE_MS)` to coalesce
adjacent WS frames into one `xterm.write()` (anti-tearing for Codex/Copilot
multi-frame redraws — added in commit `3ea2f30`). Under the throttled state,
**every keystroke's coalesce timer** was held 800+ ms before firing, stalling
the entire echo path while the bytes sat in `pendingChunks` — and because the
clamping persisted, the lag did not self-heal: the user saw 1–3 s per
character for as long as that VS Code process kept running.

Restarting just the VibeRails extension within the same VS Code window did
not clear the clamp; only fully quitting and relaunching VS Code did. Some
relaunches still came up throttled and needed a second attempt before a clean
state appeared. The underlying Chromium signal that decides whether the
webview reports as occluded was not pinned down — empirically, fresh-process
launches were the only path that could land in the bad state, and they did
so non-deterministically.

**Why the gate exists** (do not reintroduce setTimeout):
> TUI apps (Codex, Copilot) split their screen updates across multiple
> small PTY writes that arrive 1–10 ms apart — e.g. an erase sequence,
> then `?2026h` (sync-on), then the redrawn content, then `?2026l`
> (sync-off/render). If each arrives as a separate WebSocket frame,
> xterm.js may render intermediate torn states (blank cells) between
> the erase and the sync-on.

That tearing concern is real, but it is **already** covered by:
1. The server-side coalesce in `WebSocketConsumer.cs`
   (`NormalCoalesceDelayMs = 4`) — primary tear protection.
2. xterm.js's own per-frame rAF render — multiple `write()` calls within
   one paint interval produce a single composited paint.

The client-side gate in `terminal-multitab.js` is the third layer, and
its job is only to fold chunks that arrive in the same task into one
`xterm.write()` call.

**Fix:** Replaced `setTimeout` with `queueMicrotask` in `socket.onmessage`.
Microtasks are not subject to background-frame throttling — they always
drain at the end of the current task regardless of visibility state.
Briefly tried `requestAnimationFrame` first; switched on the realization
that rAF couples byte *processing* to visibility — bytes would accumulate
in `pendingChunks` during occlusion and hand a large backlog to
`xterm.write` in one synchronous parse when visibility returned.
Microtask decouples processing from rendering: xterm's parser updates
state as bytes arrive, xterm's own internal rAF renderer paints when
the page is paintable.

A runtime-selectable alternative was briefly shipped in Terminal Settings →
"Output Coalescing" — **`scheduler.postTask` 10ms batching** as the
non-default option, with `queueMicrotask` as the default. The intent was to
recover the cross-task batching the original `setTimeout` provided (10 ms
sliding window across adjacent `onmessage` tasks) without the occlusion
clamp, since `scheduler.postTask` is not subject to the visibility/occlusion
throttling that breaks `setTimeout` and `requestAnimationFrame`. **Removed
2026-05-07** — `postTask` mode felt slow in practice (suspected delay
clamping or scheduler queuing under the VS Code webview, not pinned down).
The setting, the `setOutputCoalesceMode` / `_outputCoalesceMode` plumbing on
`TerminalManager`, and the `viberails_terminal_outputCoalesce` localStorage
key are all gone — `socket.onmessage` now always uses `queueMicrotask`.

Removed the now-unused `OUTPUT_WRITE_COALESCE_MS` constant. Renamed
`flushTimeoutId → flushQueued` (boolean). Disconnect path no longer
needs `clearTimeout` — it just clears `pendingChunks` and the flag,
since any in-flight microtask is no-op'd by the existing
`this.socket !== socket` guard inside `flushPendingChunks`.

**Scope (deliberate):** this is a *client-only* change. An earlier draft
also tightened the server-side constants in `WebSocketConsumer.cs`
(`NormalCoalesceDelayMs` 4→2, `MaxSyncOutputHoldMs` 100→16) to chase
steady-state typing latency. That was rolled back — switching to
`queueMicrotask` already eliminates the same-task client batching window
the original `setTimeout` provided, so the server-side coalesce is now
the *only* multi-frame tear protection left (besides xterm's per-paint
rAF). Halving it in the same change would have stacked two protection
losses on top of each other, exactly when verification hasn't run yet.
If steady-state typing latency is the next target, do it as a separate,
testable change with the Codex/Copilot tear regression check (#2 above)
re-run after.

**Guardrails:**
- Never use `setTimeout` for short-delay batching of incoming WS data
  (or any input-event-driven work) in the webview. Treat `setTimeout`
  as background-throttled by default. Use `queueMicrotask` for
  immediate drain or `requestAnimationFrame` for paint-aligned work.
- For deliberate cross-task batching (multi-frame TUI tear protection),
  `scheduler.postTask({ delay, priority })` is the textbook answer over
  `setTimeout` because `setTimeout` and `requestAnimationFrame` are
  throttled or suspended when the renderer is occluded while
  `scheduler.postTask` is not. We tried `scheduler.postTask` 10 ms here
  as a runtime A/B and pulled it 2026-05-07 because it felt slow in the
  VS Code webview — keep that empirical result in mind before reaching
  for it again.
- If a "every keystroke is slow and stays slow for the whole VS Code
  session, only a full VS Code quit-and-relaunch clears it, and it only
  reproduces from a fresh `Code.exe` process" symptom appears again,
  suspect renderer-occlusion timer throttling first. The symptom does
  **not** self-heal on workbench paint or on extension close/reopen —
  in the original incident it persisted for the lifetime of the
  affected VS Code process. Capture a Performance trace and inspect
  `TimerInstall` / `TimerFire` actual-vs-requested delays before going
  deeper into server-side or render-side suspects.

**Diagnostic signature:** in a Performance trace from cold start, look
for short-target timers (`TimerInstall` requested timeout ≤ 50 ms)
whose corresponding `TimerFire` lands 700–3000 ms later. Trace
`Trace-20260505T155736.json.gz` is the canonical example.

Key files:
- `VibeRails/wwwroot/js/modules/terminal-multitab.js` — `socket.onmessage`,
  `flushPendingChunks`
- `VibeRails/Services/Terminal/Consumers/WebSocketConsumer.cs` —
  `NormalCoalesceDelayMs = 4` (server-side coalesce, unchanged; still the
  primary tear protection)

---

### ✅ Scrollback wiped on shrink-resize during long live xterm.js sessions (2026-05-01)

**Symptom (Rob, session `8dd5fe21-2eaf-4622-a7ba-a070416ffa7d`):** after a long
Claude Code live session in the VS Code extension's web terminal, scrolling up
in xterm.js showed nothing — not even early conversation. C# emulator scrollback
for the same session was healthy (~4,600 rows from the original Claude banner
through the most recent message), so the loss was strictly client-side. Note
this is the **live xterm.js terminal** in the dashboard / VS Code extension —
not the Sessions replay viewer modal.

**Root cause:** `VibeTerminal.clearDisplay()` in
`VibeRails/wwwroot/js/modules/vibe-terminal.js` called xterm.js
`Terminal.clear()`. Per xterm.js v6 contract, `Terminal.clear()` collapses the
buffer to a single row — wiping both the visible viewport AND the scrollback
ring buffer. `clearDisplay()` runs on every shrink-resize via
`resetDisplayOnly()` from `sendResizeToPty()` in `terminal-multitab.js`. So a
single font-size bump (or container shrink) mid-session was enough to drop
scrollback to zero. Claude Code's TUI repaints in place using absolute CUP
positioning and never re-scrolls history back into the buffer, so once the
scrollback was wiped it stayed wiped for the rest of the session — exactly
matching the "scrolling up at the very end shows nothing" symptom.

**Fix:** `clearDisplay()` now writes `\x1b[2J\x1b[H` (ED2 + CUP home) through
`Terminal.write()` instead of calling the API-level `Terminal.clear()`. ED2
erases the visible viewport without touching scrollback — same stale-cell
cleanup the original code wanted, scrollback intact.

**Regression coverage:**
- `UITests/tests/xterm-scrollback.spec.js` — Node test (xterm/headless) that
  replays the captured 30-hour session, asserts `clearDisplay`-equivalent
  cleanup preserves scrollback baseY, and source-greps `vibe-terminal.js` to
  block re-introducing `this._terminal.clear()`. Run with
  `cd UITests && node --test tests/xterm-scrollback.spec.js`.
- `TerminalEmulator.Tests/Session_8dd5fe21_ScrollbackDiagnostic.cs` — pins the
  server-side guarantee that the C# emulator accumulates scrollback through a
  long main-screen TUI session (so reconnect snapshots stay non-empty too).
- Fixture: `TerminalEmulator.Tests/fixtures/session_8dd5fe21_full.bin`
  (~2.8 MB raw concatenated `SessionLogs.Content` for the session).

**Guardrail:** `Terminal.clear()` is the wrong primitive for any "freshen the
viewport" job in this codebase. Use `term.write('\x1b[2J\x1b[H')` for visible
cleanup; only use the API-level reset on a cold reconnect path where
scrollback is about to be repopulated by a server snapshot
(`resetForSnapshotReplay()`).

Key files:
- `VibeRails/wwwroot/js/modules/vibe-terminal.js` — `clearDisplay()`
- `UITests/tests/xterm-scrollback.spec.js`
- `TerminalEmulator.Tests/Session_8dd5fe21_ScrollbackDiagnostic.cs`

---

### ✅ Backend lifecycle / transport hardening pass (2026-03-18)

This pass closed the core backend hazards from the 2026-03-17 review:

- `TerminalSessionService` startup/stop/detach now runs behind a single lifecycle gate, closing the
  single-session race and making teardown ownership transitions explicit
- PTY output persistence now uses a bounded per-session single-writer pipeline instead of spawning
  unbounded fire-and-forget SQLite writes
- local WebSocket and remote relay output paths are now bounded; overflow explicitly disconnects the
  lagging consumer instead of growing memory without limit
- `TerminalRunner.CreateSessionAsync()` is now transactional: startup rollback disposes the PTY,
  tears down remote connection state, and completes the DB session on failure
- PTY exit cleanup no longer runs as `async void`; exit is funneled through scheduled task-based
  teardown
- stop/exit cleanup now explicitly closes the active local WebSocket, disposes the PTY before
  waiting for the read loop, and flushes queued DB output before completing the session
- title OSC is published on the output path instead of being injected into PTY stdin
- remote register/deregister no longer block the main session critical path
- stale-session cleanup no longer closes sessions on age alone; it now checks recent log/input
  activity first

Key files:
- `VibeRails/Services/Terminal/TerminalSessionService.cs`
- `VibeRails/Services/Terminal/TerminalRunner.cs`
- `VibeRails/Services/Terminal/TerminalStateService.cs`
- `VibeRails/Services/Terminal/Terminal.cs`
- `VibeRails/Services/Terminal/Consumers/WebSocketConsumer.cs`
- `VibeRails/Services/Terminal/RemoteTerminalConnection.cs`
- `VibeRails/Services/DbService.cs`

---

### ✅ Emulator/parser fidelity hardening pass (2026-03-18)

This pass also closed the major emulator-side fidelity gaps from the review:

- `ED3` / `CSI 3 J` now clears scrollback only and no longer erases the visible screen
- remote replay now gets the same redraw follow-up as local replay, and remote resize is serialized
  through the same replay/takeover gate
- native CLI console input loop now exits when the PTY exits instead of lingering on
  `Console.ReadKey()` semantics
- supplementary-plane glyphs now preserve the full Unicode scalar for replay/serialization
- resize now resizes `_dirtyRows` and marks new rows dirty
- private CSI mode handling now applies every mode parameter, not just the first
- snapshot normalization was updated so the expanded non-BMP behavior compares cleanly against
  historical golden files

Finding 9 (`ESC \` terminator handling inside DCS/APC/PM/SOS passthrough) was already fixed before
this pass and remained green.

Key files:
- `TerminalEmulator/AnsiParser.cs`
- `TerminalEmulator/TerminalBuffer.cs`
- `TerminalEmulator/TerminalCell.cs`
- `TerminalEmulator/Terminal.cs`
- `VibeRails/Services/Terminal/TerminalGridSerializer.cs`
- `TerminalEmulator.Tests/AnsiParserTests.cs`
- `TerminalEmulator.Tests/EscapeSequenceTests.cs`
- `TerminalEmulator.Tests/SnapshotTests.cs`

---

### ✅ Attach policy reconciled — unified replay is now the intended baseline (2026-03-18)

This file previously contained two conflicting stories:

- one section said managed AI CLIs should skip replay and use redraw-first attach
- later sections described emulator replay as the reconnect baseline

Current code has standardized on a single attach policy for local and remote viewers:

- resize first (if dimensions are known)
- capture emulator replay
- send replay before subscribing live output
- subscribe live output
- send `Ctrl+L` redraw immediately after replay

That means the old managed-AI skip-replay guidance is no longer the current contract. Keep the older
incident notes below as history, not as current architecture guidance.

---

### ✅ JS cursor settings removed — prevents future cursor state fights (2026-03-17)

**Background:** The ghost cursor fix (below) was a C# change — removing `?25h` from
`TerminalGridSerializer.Serialize()`. That fixed the symptom. This is follow-up cleanup to
ensure no JS can re-introduce the same fight.

**What was removed:**
- `cursorBlink`, `cursorStyle`, `cursorInactiveStyle` options from the xterm.js `Terminal()`
  constructor in `vibe-terminal.js` and `term/term.js`
- `setInteractive()` method in `vibe-terminal.js` — was dead code that toggled `cursorBlink`
  on focus change
- `_loadCursorBlink()`, `_loadCursorStyle()` localStorage loaders in `terminal-multitab.js`
- Cursor lines (`cursorStyle`, `cursorBlink`) from `_applySavedTerminalSettings()`
- `applyCursorStyle()` and `applyCursorBlink()` methods
- Event listeners for cursor style/blink selects
- Entire **Cursor** section (Style + Blink dropdowns) from the Terminal Settings panel HTML

**Why:** TUI apps manage cursor visibility and appearance entirely via escape sequences
(`?25l`/`?25h`, `?12h`, SGR). Any JS that sets `terminal.options.cursorBlink/cursorStyle`
overrides what the TUI intended and can reintroduce ghost cursors.

Key files:
- `VibeRails/wwwroot/js/modules/vibe-terminal.js` — constructor, removed `setInteractive()`
- `VibeRails/wwwroot/js/modules/terminal-multitab.js` — loader/apply methods, settings HTML
- `VibeRails/wwwroot/term/term.js` — standalone remote viewer init

---

### ✅ Ghost / roaming cursor after reconnect — TUI fake cursor vs real xterm.js cursor (2026-03-17)

After reconnect, a second cursor-like block appeared alongside or flew around the viewport
independently of the real cursor. It was most visible during TUI loading and immediately after
replay.

**Root cause (primary — cursor visibility fight):**
TUI apps (Claude Code, etc.) intentionally hide the real xterm.js cursor (`?25l`) and draw their
own block/beam glyph at the prompt. `TerminalGridSerializer.Serialize()` was appending `?25h`
(cursor visible) at the end of every replay. This un-hid the real cursor, giving xterm.js two
"cursors": the real hardware cursor (restored by `?25h`) and the TUI's own drawn block — both
visible simultaneously. Removing `?25h` from the end of replay fixes this entirely. The cursor
stays hidden after replay; the subsequent Ctrl+L redraw causes the TUI to re-establish its own
cursor state naturally.

**Root cause (secondary — cursor flying during repaint):**
The replay sequence began with `ESC c` (RIS hard reset) and painted all screen rows using
sequential CRLF flow (`\r\n` between rows). During repaint, xterm.js rendered the cursor
wherever it currently thought it was, then moved it again at the final CUP — visually the
cursor appeared to fly around. Fix: hide cursor at the very start of replay with `?25l`, and
paint each screen row using absolute CUP addressing (`\x1b[{r+1};1H`) instead of CRLF flow.
This makes it impossible for cumulative drift (wide chars, full-width columns, wrap semantics)
to misplace the cursor during repaint.

**Root cause (tertiary — hard reset side-effects):**
`\u001bc` (RIS) resets many terminal modes beyond screen content. In xterm.js this can cause
cursor state changes, mode resets, and visual artifacts when content is immediately repainted.
Replaced with a targeted soft clear: `?25l` + `ED2` + `ED3` + `CUP(1,1)` — clears screen and
scrollback only, leaves all other terminal modes intact.

**Fix summary (`TerminalGridSerializer.Serialize()`):**
1. Start with `?25l` (hide cursor) + `\x1b[2J\x1b[3J\x1b[H` instead of `ESC c`
2. Scrollback rows unchanged (CRLF flow into xterm scrollback is correct)
3. Each screen row prefixed with `\x1b[{r+1};1H` (absolute CUP, no CRLF)
4. End with `\x1b[0m` + CUP to real cursor position — no `?25h`, no `?12h`

**Fix summary (`TerminalSessionService.HandleWebSocketAsync()`):**
Added a comment documenting the critical ordering invariant: snapshot must be sent before
subscribing the live WebSocket consumer. If the consumer is subscribed first, live PTY output
can arrive at the browser while the snapshot is in-flight, producing a concurrent-write race
that creates ghost cursors and corrupted screen state.

**Also closed:** the "Remaining roaming cursor" active bug (2026-03-16 CSS fix + retest pending)
is confirmed resolved by this change. The CSS `outline: none`/`opacity: 0` fix on
`.vb-terminal-element .xterm-helper-textarea` remains in place as defense-in-depth against the
focus-ring artefact.

Key files:
- `VibeRails/Services/Terminal/TerminalGridSerializer.cs` — `Serialize()`
- `VibeRails/Services/Terminal/TerminalSessionService.cs` — `HandleWebSocketAsync()` comment

---

### ✅ Double print / full-session duplicate replay on reconnect and hard refresh

On reconnect and especially on hard refresh, the browser could repaint the top/full screen
twice or replay the entire visible AI CLI session again. This was most noticeable with
full-screen TUI CLIs like Codex/Claude/Antigravity/Copilot.

**Root cause:** the local WebSocket attach path unconditionally sent `terminal.GetGridReplay()`
before subscribing the live WebSocket consumer. For managed AI CLI sessions, this conflicted
with redraw-style attach behavior and caused duplicated TUI content on browser reconnect / hard
refresh. Plain shell / line-oriented sessions could still use replay, but managed AI CLIs
needed redraw-first attach instead.

**Fix:** updated `VibeRails/Services/Terminal/TerminalSessionService.cs` so managed AI CLIs
(`Claude`, `Codex`, `Antigravity`, `Copilot`) now skip local replay on attach and instead subscribe
the WebSocket first, then request a redraw with `Ctrl+L`. Plain shell / line-oriented sessions
still use `GetGridReplay()`.

**2026-03-16 retest:** user confirmed this tested good. The duplicate replay / double print
issue no longer reproduces in current testing.

**Superseded 2026-03-18:** local attach no longer uses a managed-AI special case. The backend now
standardizes on emulator replay for all local sessions, with pre-resize, replay-before-subscribe
ordering, and an immediate post-replay `Ctrl+L` redraw. Treat the old skip-replay branch described
above as historical incident context only.

Key files:
- `VibeRails/Services/Terminal/TerminalSessionService.cs` — `HandleWebSocketAsync`, `s_activeCli`
- `VibeRails/wwwroot/js/modules/terminal-multitab.js` — `connect()`, restore/attach flow

---

### ✅ Double/phantom cursor — ghost cursor alongside real cursor

While typing, a second blinking cursor appeared alongside the real cursor. When typing reached
end of a row, the phantom moved to the bottom-right corner of the viewport. Observed in both
browser and VS Code extension.

**Root cause:** xterm.js v6 positions the `xterm-helper-textarea` ON-SCREEN at the cursor
location (for IME composition support), unlike older xterm.js which parked it at `left: -9999em`.
The browser renders the textarea's native caret at that pixel position, producing a second
blinking cursor on top of xterm.js's own canvas-rendered cursor. At end-of-row / pending-wrap,
xterm.js moves the textarea to the wrap position a frame late, leaving the native caret briefly
at the old column — visually "stuck at bottom-right".

**Fix:** `caret-color: transparent !important` on `.terminal-element .xterm-helper-textarea`
in `style.css`. This hides the browser caret while xterm.js's own cursor remains fully visible.

Key file: `VibeRails/wwwroot/style.css` — `.terminal-element .xterm-helper-textarea`

---

### ✅ Cursor flickering during TUI loading

The cursor visibly flickered or jumped around while a TUI app (e.g. Claude Code) was loading.
Observed in both browser and VS Code extension.

**Root cause (primary):** Same as the double/phantom cursor above — xterm.js v6 moves the
textarea to the cursor position on every cursor-movement sequence. TUI apps emit rapid cursor
moves during startup (`\u001b[R;CH`, `\u001b[?25l/h`, etc.), causing the browser native caret
to flicker across the screen as the textarea tracks each move. Fixed by `caret-color: transparent`.

**Root cause (secondary):** `socket.onopen` called `fitAndSyncTerminal()` which force-sent a
`__resize__` control frame even when dimensions were identical to the pre-connect fit already
forwarded in the WebSocket URL. The server received the same-size resize, sent SIGWINCH to the
PTY, and the TUI performed a full redraw right on top of the just-loaded replay — causing an
additional wave of cursor movement and redraw flicker immediately after reconnect.

**Fix:** Prime `this.lastResizeSignature` in `connect()` with the pre-connect dimensions after
the pre-connect fit. Replace `fitAndSyncTerminal()` in `socket.onopen` with a non-forced
`sendResizeToPty()` that skips if the signature is unchanged. The server already has the correct
PTY dimensions from the URL; the post-connect sync only sends `__resize__` if the container
genuinely changed between pre-connect and `onopen`.

Key files:
- `VibeRails/wwwroot/style.css` — `caret-color: transparent`
- `VibeRails/wwwroot/js/modules/terminal-multitab.js` — `connect()` signature priming, `socket.onopen` resize path

---


### ✅ Cursor flicker / jumping cursor positions after reconnect and resize

After the double-print fix, the Web UI terminal could still show the cursor jumping between
old and new positions during reconnect or layout settle. The screenshot looked like "ghost"
cursors being painted in multiple places even though the text itself was no longer duplicated.

**Root cause:** the client-side resize path called `resetDisplayOnly()` on any new geometry,
including the first post-connect sync. That local xterm reset was useful for stale right-edge
cells on real shrink events, but it was too aggressive for reconnect. It briefly cleared and
repainted the local viewport before the PTY had anything new to say, which made the cursor look
like it was flickering or teleporting.

**Fix:** only clear the local xterm viewport when the terminal actually shrinks
(`newCols < oldCols || newRows < oldRows`). The first post-connect sync and grow-only layout
passes now skip the local reset and just send the resize to the PTY.

Key file: `VibeRails/wwwroot/js/modules/terminal-multitab.js` — `sendResizeToPty`,
`shouldResetDisplayBeforeResize`

### ✅ Double paste when pasting into the terminal

Pasting into the web terminal (Ctrl+V or right-click paste) sent the clipboard text twice.

`attachClipboardPaste` in `vibe-terminal.js` had two simultaneous paste paths: (1) a Ctrl+V
keydown handler calling `navigator.clipboard.readText()`, and (2) xterm's own bubble-phase
`paste` listener, which also fired `onData` because the capture listener only called
`e.preventDefault()` — not `e.stopImmediatePropagation()`. xterm does not check
`e.defaultPrevented`, so it always ran regardless.

Fixed by consolidating to a single paste path: the capture-phase `paste` listener now calls
`e.stopImmediatePropagation()` to block xterm's listener. All paste text is read from
`e.clipboardData`. The keydown handler now only returns `false` to prevent xterm from processing
Ctrl+V as a raw key sequence.

Key file: `VibeRails/wwwroot/js/modules/vibe-terminal.js` — `attachClipboardPaste`

### ✅ Clicking a tab auto-reconnected the terminal

Tab button click passed `connectIfNeeded: true`, making tab selection act as an implicit
reconnect. Fixed by changing all tab activation calls in `addLocalTab()` and `restoreTabs()` to
`connectIfNeeded: false`. Reconnect is now explicit only (Reconnect button, or
`reconnectActiveTab()`).

### ✅ Unselected tabs appeared offline during navigation

Navigation destroyed all browser sockets. Only the active tab reconnected, making inactive tabs
look offline. Resolved as a side effect of the `connectIfNeeded: false` change above. Tabs now
correctly show as paused/disconnected rather than silently re-connecting on activation.

### ✅ History lost on reconnect / hard refresh

Previously used `CircularBuffer` with ANSI break-point heuristics (`\x1b[?1049h`, `\x1b[2J`,
`\x1bc`) to find a "clean" restart point in raw PTY bytes. This was fragile — short sessions
or plain-shell sessions often had no break point, giving clients a blank or partial screen.

**Fix:** replaced `CircularBuffer` entirely with the `TerminalEmulator` library. Every PTY
output chunk is fed to an in-memory VT100 state machine. On reconnect, `GetGridReplay()`
serializes the full scrollback history + current screen as ANSI and sends it as a single
binary WebSocket frame. xterm.js renders it instantly — no animation, no DB, no break-point
guessing. The client always gets the complete scroll history, exactly as VS Code does.

See **TerminalEmulator Integration** section below for architecture details.

### ✅ AI TUI double-render / stale cells on reconnect and resize

Mitigated by:
- pre-resize + replay-before-subscribe + immediate post-replay redraw on reconnect
- `resetDisplayOnly()` in the shrink-only resize path clears stale xterm cells before a real PTY geometry change
- manager generation guards prevent stale async init from completing after navigation

### ✅ Cursor stuck at bottom-right and not blinking after replay

After reconnect the cursor appeared frozen at the bottom-right corner of the xterm.js viewport
and cursor blink was disabled.

**Root cause:** `\u001bc` (RIS hard reset) resets xterm.js cursor state to its defaults —
visible but **not blinking**. TUI apps normally re-enable blink via `\x1b[?12h` on startup, but
those sequences are ephemeral and not captured in the emulator cell grid, so they are never
replayed.

**Original fix (2026-03-16):** appended `?25h` + `?12h` after CUP. **Superseded 2026-03-17**
by the ghost-cursor fix — `?25h` was causing a second ghost cursor when TUIs drew their own
block and the real cursor was unexpectedly restored. Both `?25h` and `?12h` removed. The Ctrl+L
redraw now handles all cursor state restoration correctly.

Key file: `VibeRails/Services/Terminal/TerminalGridSerializer.cs` — end of `Serialize()`

### ✅ Double print on remote viewer connect + local title OSC wrong path

**a) Double print:** `RemoteOutputConsumer` streamed live PTY bytes concurrently while
`GetGridReplay()` snapshot was being sent, so the browser got live output interleaved with
the full replay. Fixed by adding a `replayInProgress` volatile int — `canForward()` returns
false while replay is in flight. Applied to both the replay path and PIN-verified path.

**b) OSC title via wrong path:** `NotifyRemoteTakeoverAsync` wrote the title OSC to PTY stdin
via `WriteBytesAsync`. ConPTY does not interpret OSC from stdin — it passes them to the shell
as raw input. Fixed by adding `Terminal.PublishOutput()` which dispatches bytes to all
`ITerminalConsumer`s via the output path (same as PTY-produced bytes). Title sequences now
use that instead. Also corrected `\x1b` → `\u001b` escapes throughout.

Key files:
- `VibeRails/Services/Terminal/Terminal.cs` — new `PublishOutput` method
- `VibeRails/Services/Terminal/TerminalRunner.cs` — `replayInProgress` gate,
  `PublishOutput` in `NotifyRemoteTakeoverAsync` / `HandleRemoteBrowserDisconnectedAsync`

### ✅ Remote viewer connect/disconnect not visible on native CLI

Superseded by the native CLI remote alerting deferred issue above. The OSC title approach was
implemented (via `PublishOutput`) but the title gets overwritten immediately by the TUI/shell.
Remote access for native CLI sessions has been disabled pending a proper alerting layer.

Key files: `VibeRails/Services/Terminal/TerminalRunner.cs` —
`_nativeRemoteEnabled`, `ShouldEnableRemote`, `isNativeCli` parameter on `CreateSessionAsync`

### ✅ Garbled character (Ƽ) at start of remote viewer replay

The first character rendered in the remote viewer on reconnect was `Ƽ` (U+01BC) instead of a
clean screen reset.

**Root cause:** `TerminalGridSerializer.cs` emitted `"\x1bc"` as the hard-reset sequence. In
C#, `\x` greedily consumes hex digits, so `\x1bc` is codepoint `0x1BC` (Ƽ), not ESC + `c`.

**Fix:** `"\x1bc"` → `"\u001bc"` in `TerminalGridSerializer.cs:32`.

Key file: `VibeRails/Services/Terminal/TerminalGridSerializer.cs` — `Serialize()`

### ✅ Native CLI showed only a blinking cursor in remote browser until resize

**Root cause:** premature `fitAndSyncTerminal({ force: true })` call in `socket.onopen` fired
before the terminal panel CSS had settled, sending wrong cols/rows to the PTY.

**Fix:** removed the premature call. `scheduleViewportLayoutSync(40ms)` fires after layout
settles and sends the correct resize. The Ctrl+L fallback in `HandleRemoteReplayRequestAsync`
has been removed — `GetGridReplay()` always returns a valid full-screen state, so no fallback
is needed. The PIN-verified path also now uses `GetGridReplay()` instead of Ctrl+L.

Key files:
- `VibeRailsFrontEnd/.../Views/Terminals/Index.cshtml` — `socket.onopen`
- `VibeRails/Services/Terminal/TerminalRunner.cs` — `HandleRemoteReplayRequestAsync`

---

## TerminalEmulator Integration

`TerminalEmulator` (`C:\source\VibeControl2\TerminalEmulator\`) is an AOT-safe, net10.0 VT100
state machine that replaced `CircularBuffer` as the terminal state proxy.

**What it does:**
- Parses all ANSI/VT100 sequences (CSI, OSC, DCS, SGR, alternate screen, 256-color, true color)
- Maintains a 2D cell grid (`TerminalCell[rows, cols]`) for the current visible screen
- Keeps a scrollback ring buffer (1000 rows default) of rows that have scrolled off
- Tracks cursor position, SGR attributes, and alternate screen state

**How it's wired:**
- `TerminalEmulatorConsumer` (`ITerminalConsumer`) subscribes to every PTY output chunk and
  feeds it to the emulator under `_emulatorLock`
- `Terminal.Resize()` also resizes the emulator to keep dimensions in sync
- `Terminal.GetGridReplay()` snapshots scrollback + screen under lock, then calls
  `TerminalGridSerializer.Serialize()` outside the lock
- reconnect/reattach now uses emulator replay for both plain shells and managed AI CLIs; attach
  correctness comes from pre-resize + snapshot-before-subscribe + post-replay redraw, not from a
  CLI-type-specific replay bypass

**`TerminalGridSerializer.Serialize()`:**
- Hides cursor (`?25l`), then soft-clears screen (`ED2`) + scrollback (`ED3`) + homes cursor (`CUP 1,1`)
  — does NOT use `ESC c` (RIS) which resets terminal modes and fights TUI cursor state
- Writes scrollback rows oldest-first with `\r\n`, using delta SGR encoding (pushes into xterm scrollback)
- Writes each screen row prefixed with `\x1b[{r+1};1H` (absolute CUP per row, prevents drift from
  wide chars / full-width columns / wrap semantics)
- Resets SGR and repositions cursor via CUP to the emulator's real cursor position
- Does NOT restore cursor visibility — leaves cursor hidden so Ctrl+L redraw lets the TUI
  re-establish its own cursor state (avoids ghost cursor from real + TUI fake cursors both visible)
- Returns UTF-8 bytes ready for a binary WebSocket frame

**Thread safety:**
- `_emulatorLock` (C# 13 `Lock`) serializes `Write()` and `Resize()` from concurrent threads
- `GetSnapshot()` and `GetScrollback()` return copies — serialization is lock-free

**Key files:**
- `TerminalEmulator/Terminal.cs` — public API (`Write`, `Resize`, `GetSnapshot`, `GetScrollback`)
- `TerminalEmulator/TerminalBuffer.cs` — grid + scrollback state
- `TerminalEmulator/AnsiParser.cs` — VT100 state machine
- `VibeRails/Services/Terminal/Terminal.cs` — `GetGridReplay()`
- `VibeRails/Services/Terminal/TerminalGridSerializer.cs` — ANSI serializer
- `VibeRails/Services/Terminal/TerminalEmulatorConsumer.cs` — feeds PTY bytes to emulator

---

## Notes

- Terminal tracking is consolidated in this root file. The duplicate `VibeRails/TERMINAL.md`
  investigation file was removed on 2026-03-12.
- Do not reintroduce `CircularBuffer` or raw replay as the reconnect baseline — fully replaced
  by the TerminalEmulator grid approach.
- Replay is now the standard reconnect baseline for all session types. Do not reintroduce
  CLI-specific skip-replay branches unless a concrete reproducer and regression tests require it.
- **Steady-state typing latency (accepted):** ~20 ms per character echo is
  inherent — xterm.js v6 `WriteBuffer` batches via `setTimeout(0)` (~4 ms) +
  rAF render (~16 ms). No delay on our `onData` → `socket.send()` path; the
  bottleneck is xterm.js's async write pipeline. Fixing would require local
  echo or xterm.js internal APIs. Accepted as-is. The `socket.onmessage`
  client-side flush always uses `queueMicrotask` — the runtime A/B with
  `scheduler.postTask` 10 ms batching was removed 2026-05-07 because
  `postTask` felt slow in the VS Code webview (suspected clamping/queuing,
  not pinned down).
- **Cold-start typing latency (fixed 2026-05-05, verified 2026-05-06, v1.6.12):**
  the previously catastrophic ~800–3000 ms first-cold-start echo lag was a
  separate problem from the steady-state ~20 ms above. Root cause was Chromium
  clamping our `setTimeout`-based output coalesce in `terminal-multitab.js` to a
  1-second minimum while the VS Code webview was occluded during workbench cold
  paint. Fixed by switching to `queueMicrotask`. See the Fixed Issues entry for
  the full diagnostic signature and guardrails — in particular, do not
  reintroduce `setTimeout` for short-delay batching in the webview.




##I MOVED THIS OVER FROM THE OTHER FILE. I don't know who added it. 

 Terminal Known Issues & Notes

## Open Bugs

The sole open known terminal display bug is the resize reprint / overprint
issue tracked at the top of this file. **Status: open / accepted-live-with**
as of 2026-05-14 — a 2026-05-14 candidate fix was rejected on review (the
proposed client-side `setTimeout` hold re-opened the
process-lifetime occlusion-throttle failure mode for a small visual
artifact). The recommended next attempt is the server-side coalesce bump
in `WebSocketConsumer`; until someone has time to do that, the artifact
stays.

The other items historically tracked under "Open Bugs" have been reclassified
as accepted-live-with or deferred and moved to **Closed / Informational**
below. They are not actively open.

---

## Closed / Informational

## Font size change causes incomplete TUI rendering / double print

**Status:** Accepted / live-with — 2026-04-01 (reclassified 2026-05-07)

**Why this is not on the Open list:** root cause is upstream — ConPTY's redraw
after `ResizePseudoConsole` is not always complete at certain col/row
dimensions. There is no clean fix in our layer; mitigations exist (see "Possible
fix directions" below) but the underlying behavior is a Windows ConPTY quirk.
Tracked here so future investigators don't waste time rediscovering the
symptoms.

**Symptom:** Changing xterm.js font size via the +/- buttons can cause two related rendering failures:
1. **Double print:** At the original font size, ConPTY emits a redundant full redraw, causing text to appear twice.
2. **Partial TUI rendering:** After a font size change clears and redraws, some font sizes produce a complete TUI (all conversation content visible), while others show only the header chrome (logo, version, model, workdir) and prompt — the conversation body is blank.

Observed with Claude Code's Ink/React TUI. Font size 10 renders fully; font size 11 renders only header + prompt. Increasing font size from the initial (broken) state cleared the double print but exposed the partial rendering issue.

**Reproduction:**
1. Launch Claude Code in VibeRails web terminal
2. Have an active conversation with tool calls visible
3. Click font size +/- buttons to change size
4. Observe: some sizes render the full TUI, others show only the top chrome and prompt area with blank space where conversation content should be

**Root cause (suspected):** The font-size-change flow in `applyFontSize()` (terminal-multitab.js:382) calls `resetDisplayOnly()` which calls `terminal.clear()` — wiping the xterm.js buffer — then sends `__resize__:cols,rows` to the backend. `TerminalResizeCoordinator.ApplyResize()` resizes the ConPTY, which triggers SIGWINCH to the TUI app. The TUI re-renders at new dimensions and that output flows back through PTY → WebSocket → xterm.js.

The problem is **ConPTY's redraw after `ResizePseudoConsole` is not always complete**. At certain col/row dimensions, the TUI content doesn't fully repaint. At others it works fine. The codebase already acknowledges ConPTY redraw unreliability: `TerminalResizeCoordinator` has "calling ResizePseudoConsole with the same size triggers a full ConPTY redraw, which produces duplicate output."

`EnableDebouncedRedrawOnResize` is `false`, so there is no Ctrl+L safety net to force a full TUI repaint after resize settles.

**Key files:**
- `VibeRails/wwwroot/js/modules/terminal-multitab.js` — `applyFontSize()`, `resetDisplayOnly()`, `sendResizeToPty()`
- `VibeRails/wwwroot/js/modules/vibe-terminal.js` — `clearDisplay()`, `setFontSize()`, `fit()`
- `VibeRails/Services/Terminal/TerminalResizeCoordinator.cs` — `ApplyResize()`, `EnableDebouncedRedrawOnResize`
- `VibeRails/Services/Terminal/Terminal.cs` — `Resize()` → `_pty.Resize()` + `_emulator.Resize()`

**Current test:** Enabled `EnableDebouncedRedrawOnResize = true` in `TerminalResizeCoordinator.cs` (2026-04-01) to see if the debounced Ctrl+L forces a complete TUI repaint after font size changes.

**Possible fix directions:**
- Enable `EnableDebouncedRedrawOnResize = true` so a debounced Ctrl+L forces a full TUI repaint after resize settles. This already exists in `TerminalResizeCoordinator` — **now enabled for testing**.
- Remove or defer the `terminal.clear()` in the font-size path — let the ConPTY redraw overwrite stale content rather than clearing first and risking an incomplete repaint.
- Combine both: skip the preemptive clear and send a debounced Ctrl+L after resize, so the TUI app gets a chance to fully redraw without the user seeing a blank flash.
- Investigate whether the issue is ConPTY-specific (likely) or Ink/React layout-dependent at certain column widths.

---

## Codex: Status-line cursor flash / cursor hop during TUI redraw

**Status:** Mitigated 2026-04-16 — **but superseded 2026-06-09: the cursor
suppression this entry added was itself the cause of the long-running flicker.
Removed in 1.7.3.**

> **⚠️ Root-caused & superseded 2026-06-09 — see the top-of-file note and the
> "## 2026-06-09" entry.** The "output-driven cursor suppression window" this entry
> introduced (hide the xterm cursor during inbound redraw bursts, restore after a
> brief idle period — `suppressCursorDuringOutput` / `restoreSuppressedCursor`) is
> what caused the continuous ~3 Hz cursor flicker that dogged Codex for months and
> later Claude — i.e. the *"small residual flicker"* called out in **Result** below
> was never Codex "legitimately repainting"; it was this mitigation's own
> restore→re-suppress cycle. The suppression call was removed in 1.7.3 (`d1d273d`).
> The original footer/status **cursor hop** this entry describes was real and that
> diagnosis stands — but **do not reintroduce output-driven cursor suppression** to
> chase it; it trades a rare hop for a continuous blink. The CLI's own
> `\e[?25l`/`\e[?25h` already hide the cursor during its redraws.

**Symptom:** In the Web UI terminal, Codex's visible cursor could appear to jump off the input line and flash on the bottom status/footer line while pressing arrow keys, space, or during active thinking/redraw. The flashed cursor sometimes took on the footer line's gray styling, which made it look like a browser caret or renderer bug.

**Correct term:** This is best described as a **VT cursor flash** or **cursor hop during TUI redraw**. More specifically, it was a **status-line cursor flash**: Codex's TUI briefly moved the real terminal cursor to its footer/status row during intermediate redraw steps, and xterm.js rendered that transient position.

**Root cause:** This turned out to be a combination of:
1. Codex splitting one visual redraw across multiple WebSocket messages, so xterm rendered intermediate states.
2. The server flushing some redraw fragments before synchronized output had fully settled.
3. xterm faithfully drawing the real VT cursor even when Codex briefly parked it on the footer/status line during redraw.

This was **not** the hidden helper textarea caret. That browser-caret issue had already been separately suppressed via CSS/runtime textarea patching. The remaining flash was the actual terminal cursor being shown at a transient position inside Codex's TUI.

**What changed:**
- `VibeRails/wwwroot/js/modules/terminal-multitab.js`
  - Replaced microtask-only output batching with a short timer-based coalesce window so adjacent WebSocket frames render together instead of as torn redraw fragments.
  - Reduced extra connect-time focus churn and follow-up fit churn that could amplify visible redraw noise.
  - Added a short output-driven cursor suppression window so the xterm cursor is hidden during inbound redraw bursts and restored after a brief idle period.
- `VibeRails/wwwroot/js/modules/vibe-terminal.js`
  - Added cursor suppression / restore helpers that temporarily hide the rendered xterm cursor by theme + CSS while output is actively streaming.
  - Increased browser-side xterm scrollback from `5000` to `20000`.
- `VibeRails/wwwroot/style.css`
  - Added a terminal state class that hides the xterm cursor layer during transient redraw bursts as a CSS-side fallback.
- `VibeRails/Services/Terminal/Consumers/WebSocketConsumer.cs`
  - Added sync-output-aware batching for terminal output (`CSI ?2026 h/l`) with a fallback timeout so Codex redraw frames are less likely to flush mid-frame.
- `VibeRails/Services/Terminal/TerminalResizeCoordinator.cs`
  - Defers resize application while sync-output is active to avoid extra redraw churn from resize signals landing mid-frame.
- `VibeRails/Services/Terminal/Terminal.cs`
  - Increased C# emulator scrollback from `5000` to `20000` so reconnect snapshots and live browser history stay aligned.
- `VibeRails/Services/Terminal/SessionOutputWriter.cs`
  - Added sync-output-aware alternate-screen frame boundaries to improve replay/history capture for Codex sessions.

**Result:** The major visible footer/status-line cursor flash is now substantially reduced. A small residual flicker may still be visible when Codex updates its own placeholder/footer/status text during active thinking, because the TUI is still legitimately repainting that area. The mitigation here is to hide transient cursor positions, not to stop Codex from redrawing.

**Renderer note:** WebGL remains the global preferred renderer in the Web UI terminal. In testing, Codex looked better in WebGL than canvas, but no Codex-specific renderer override was added.

**Scrollback note:** The general Web UI terminal scrollback cap is now `20000` lines on both the browser xterm side and the C# emulator/reconnect side. However, Codex still uses the alternate screen for parts of its TUI, and alternate-screen scrollback remains inherently limited by terminal behavior.

**Key files touched:**
- `VibeRails/wwwroot/js/modules/terminal-multitab.js`
- `VibeRails/wwwroot/js/modules/vibe-terminal.js`
- `VibeRails/wwwroot/style.css`
- `VibeRails/Services/Terminal/Consumers/WebSocketConsumer.cs`
- `VibeRails/Services/Terminal/TerminalResizeCoordinator.cs`
- `VibeRails/Services/Terminal/Terminal.cs`
- `VibeRails/Services/Terminal/SessionOutputWriter.cs`

---

## Startup prompt triple-print — WebFontsAddon / LigaturesAddon post-connect reflow

**Status:** Fixed — 2026-04-15

**Symptom:** On opening a fresh Web UI terminal, the initial shell prompt flashed 2–3 times within ~1.8s before the CLI took over. Reproduced in session `4a3386aa-a1e7-4337-8a34-f253f2ed75ac` where `SessionLogs` chunks 891 / 892 / 894 were byte-identical full-screen redraws of the PowerShell prompt at +0.09s / +1.31s / +1.81s. Only chunk 891 carried the ConPTY resize report `\e[8;31;122t`; the other two were pure redraws with no resize cause in the byte stream.

**Root cause:** `VibeTerminal` in `wwwroot/js/modules/vibe-terminal.js` loaded two xterm addons that each fired `scheduleFitPasses()` *after* their async load completed — after the PTY was already connected and the TUI running:
- `WebFontsAddon({ onLoaded: () => this.scheduleFitPasses() })` — fires when web fonts finish downloading
- `_loadLigaturesAddon()` dynamic import — fires when the ligatures module resolves

Each callback shifted xterm cell metrics (a real font is narrower/taller than the fallback), `fit()` recomputed cols/rows, a new `__resize__` went to the backend, ConPTY SIGWINCHed the shell, and PSReadLine redrew its entire prompt. Two addons → two extra full redraws.

Per the xterm.js docs: because xterm renders to `<canvas>` / WebGL, the browser does not download web fonts automatically, so custom fonts require the `addon-web-fonts` machinery specifically. Custom fonts and this bug are two faces of the same thing.

**Fix:** Switched the terminal to a cross-platform system monospace stack and removed the whole web-font pipeline.
- Default `fontFamily` now `Menlo, Monaco, Consolas, "Cascadia Mono", "Liberation Mono", "Courier New", monospace` (`vibe-terminal.js`)
- `fontLigatures: true` → `false` on the xterm `Terminal` constructor
- Removed `WebFontsAddon` loader + `<script src="addon-web-fonts.js">` from `index.html`
- Removed `_loadLigaturesAddon()` and the dynamic import
- Removed the font-family picker from Terminal Settings UI + its `localStorage` persistence (`terminal-multitab.js`)
- Removed `window.CXL_FONTS` from `terminal-themes.js`
- `session-viewer.js` (replay modal) updated to the same stack for consistency

Left on disk deliberately (may be used elsewhere in the app): all `.woff`/`.woff2`/`.ttf`/`.otf` asset files, all `@font-face` CSS rules, Monaco editor fonts. Backend has no font preference (confirmed).

**Key files touched:**
- `VibeRails/wwwroot/js/modules/vibe-terminal.js`
- `VibeRails/wwwroot/js/modules/terminal-multitab.js`
- `VibeRails/wwwroot/js/modules/session-viewer.js`
- `VibeRails/wwwroot/assets/xterm/terminal-themes.js`
- `VibeRails/wwwroot/index.html`

**Cross-reference:** The "Font size change causes incomplete TUI rendering / double print" note (above) is separate — that one is about the +/- font-size buttons triggering ConPTY resize partial redraws. It is accepted / live-with, not an actively open bug, but this fix removes one compounding factor (no more font/ligature-load reflow firing on top of a user-initiated size change).

**Debug tool used:** `python-scripts/decode_session.py` + `python-scripts/analyze_doubleprint.py` — dump a session's raw `SessionLogs` BLOBs with ANSI escapes spelled out, then fingerprint chunks to detect identical full-screen redraws within a time window.

---

## Codex: Cannot scroll back during live session

**Symptom:** When a Codex terminal is running, scrolling up in xterm.js does nothing — prior output is inaccessible.

**Root cause:** Codex CLI uses the alternate screen buffer (`\x1b[?1049h`) for its TUI interface. Per the VT/xterm spec, alternate screen mode has no scrollback buffer. xterm.js enforces this: once `?1049h` is received, scrollback is disabled for the duration of the alternate screen session.

This is not a bug in VibeControl — it is standard terminal behavior. `vim`, `nano`, `htop`, and any other TUI application have the same behavior.

**What changed:** Codex CLI updated its UI to use an alternate-screen TUI (progress panels, status bars). Previously it used plain line output and scrollback worked fine.

**C# side detail:** `TerminalBuffer` only pushes rows into `_scrollback` when `!_usingAlternate` (line 344 in `TerminalBuffer.cs`). This is correct — alternate screen output should not pollute the normal scrollback.

**On reconnect:** The `TerminalGridSerializer` correctly replays the C# scrollback (rows accumulated before Codex entered alternate screen) followed by the current screen state. So reconnecting *does* restore pre-session history in xterm.js scrollback.

**Possible fix directions:**
- Before Codex enters alternate screen, snapshot the current xterm.js scrollback and offer a "view history" side panel or modal.
- Not fixable transparently — alternate screen without scrollback is spec behavior that xterm.js cannot override.

---

## Codex: Scrollback lost when it "stops thinking" (exits alternate screen)

**Symptom:** After Codex finishes a thinking phase and returns to normal output, the scrollback that existed before is gone.

**Expected behavior:** When an app exits alternate screen (`\x1b[?1049l`), xterm.js restores the normal screen AND its prior scrollback. Scrollback should survive the transition.

**Likely cause:** Codex (or its underlying framework) explicitly sends `\x1b[3J]` (erase scrollback) when transitioning between modes. This is a separate sequence from `?1049l` and can be sent at any time to wipe xterm's scrollback buffer. We use `\x1b[3J]` ourselves in `TerminalGridSerializer.cs:38`, but only during a reconnect replay — not during live streaming.

**To verify:** Inspect the raw PTY byte stream in `TerminalRunner` around the time Codex switches modes. Look for `\x1b[3J]` in Codex's output. If present, the wipe is coming from Codex itself.

**C# vs xterm.js split:** Our C# `TerminalBuffer` and xterm.js track state independently. During a live session, xterm.js is the truth — the C# emulator only matters on reconnect for replay. If Codex sends `\x1b[3J]` live, xterm.js clears its buffer immediately with no opportunity to intercept it cleanly.

**Possible fix directions:**
- Intercept `\x1b[3J]` in the PTY stream before forwarding to the browser WebSocket and suppress it (fragile — could affect other apps that legitimately want to clear scrollback).
- Not otherwise fixable without becoming a filtering proxy in the PTY pipeline.

### Typing lag / stutter from WebSocket coalesce hold

**Status:** Deferred — 2026-04-20 — investigation complete, fix deferred (reclassified 2026-05-07; not on the Open Bugs list)

**Note (2026-05-05, updated 2026-05-06):** This entry is about the
**server-side** 4 ms `NormalCoalesceDelayMs` hold and is distinct from the
cold-start typing lag fix shipped 2026-05-05 and verified 2026-05-06 in
v1.6.12 (which addressed a **client-side** `setTimeout` being clamped to 1 s
by Chromium during webview occlusion). The remaining 5–17 ms per-frame
stutter described below is the steady-state server-side concern.

**Symptom:** Typing and held-down backspace feel laggy/stuttery in the xterm.js terminal. Stutter is perceptible rather than uniform — some frames pop instantly, others visibly lag.

**Instrumentation added** (currently at `Log.Debug`, toggle to `Information` to re-capture):
- `TerminalStateService.RecordInput` — `[TypingLag] RecordInput chars=N elapsedMs=...`
- `TerminalStateService.LogOutput` — `[TypingLag] LogOutput bytes=N elapsedMs=...`
- `WebSocketConsumer.SendLoopAsync` — `[TypingLag] WS send frames=N bytes=N coalesceRounds=N holdMs=... sendMs=... syncOut=bool`

**Measurements** (2026-04-20 session, non-syncOutput path):
| Stage | Typical elapsed |
|---|---|
| `RecordInput` | 0.01–0.03 ms |
| `LogOutput` | 0.04–0.35 ms |
| WS `sendMs` | 0.01–0.2 ms |
| **WS `holdMs`** | **1–20 ms, clustered ~5–8 ms** |

**Root cause:** `WebSocketConsumer.SendLoopAsync` has a 4 ms `Task.Delay` coalesce window (`NormalCoalesceDelayMs`) gating every non-syncOutput send. On Windows, timer resolution (~15.6 ms) makes that sleep land at 5–16 ms. Logs show `coalesceRounds=1` on nearly every frame — the delay almost never actually merges anything, we just pay the tax. That hold is added to every keystroke echo. A burst of keystrokes produces multiple PTY echo chunks each paying their own hold, which reads as stutter.

**Why the delay exists** (do not remove blindly — commit `3ea2f30`):
> TUI apps (Codex, Copilot) split their screen updates across multiple small PTY writes that arrive 1-10ms apart — e.g. an erase sequence, then `?2026h` (sync-on), then the redrawn content, then `?2026l` (sync-off/render). If each arrives as a separate WebSocket frame, xterm.js may render intermediate torn states (blank cells) between the erase and the sync-on. Waiting 4 ms gives the PTY reader time to enqueue related writes so they coalesce into one frame.

The sync-output path (`?2026h`/`?2026l` bracketed) catches the *synced* portion. The 4 ms delay catches the *unsynced* preamble (e.g. an erase that lands just before sync-on).

**Paste test** (2338 chars input, ~6133 bytes echoed back): The big echo burst coalesced cleanly (2 frames → 1 WS send, 5.7 ms hold). Long trickle of 20–55 byte chunks afterward each pays its own ~8 ms hold. For paste, coalescing works; per-chunk holds on the tail are minor.

**Fast-typing test** (11 keys in 27 ms autorepeat burst): PTY serializes input→echo — first echo arrives ~7 ms after first keystroke with output for 1–2 keys, second echo ~40 ms later covering the rest. Each send adds a 5–17 ms hold. Cumulative hold on bursts matches the felt stutter.

**Fix options considered** (none applied):
1. **Remove the hold entirely.** Would re-introduce the Codex/Copilot tear-flicker the original commit fixed. Rejected.
2. **Shorten to true 1–2 ms.** Use `PeriodicTimer`/`SpinWait` to bypass Windows timer granularity. Keeps tear protection, cuts typing hold from ~8 ms to ~2 ms. Low risk.
3. **Adaptive: only coalesce while a previous `SendAsync` is in flight.** Self-tuning — idle keystrokes send instantly, burst output naturally batches during send roundtrip. Preserves tear protection under load. Most principled.
4. **Trust `?2026h`/`?2026l` only.** Drop unsynced delay. Requires confirming Codex/Copilot always bracket writes properly.

**Also missing before committing a fix:** client-side end-to-end timing (WS `onmessage` → post-`term.write` timestamps in `terminal-multitab.js`). Without that, fixing the server hold is blind to any client-side render/paint bottleneck.

**Files:**
- `VibeRails/Services/Terminal/Consumers/WebSocketConsumer.cs` — `SendLoopAsync`, `NormalCoalesceDelayMs = 4`
- `VibeRails/Services/Terminal/Core/TerminalStateService.cs` — `RecordInput`, `LogOutput` instrumentation

---

## Web UI reconnect double-print after sleep / page navigation

**Status:** Fixed — 2026-04-26

**Symptom:** After the browser disconnected during page navigation / sleep and later reconnected,
the active Web UI terminal could show Claude Code's startup card twice. Session
`9e670449-54f7-461d-a3de-ae4a9db889ef` reproduced this with real `state.db`
bytes: the reconnect snapshot restored the screen, then Claude emitted additional full-screen
repaint bytes and the visible card appeared duplicated.

**Root cause:** This was another instance of the older "replay plus forced TUI redraw" failure
mode, but reintroduced through the frontend font-bump workaround. The earlier reconnect fixes
prevented redundant same-size resize frames by priming `lastResizeSignature`, but
`triggerRedrawBump()` deliberately changed xterm's font size and restored it later. That reset
the resize signature and created two real resize opportunities. On Windows/ConPTY each resize
can deliver SIGWINCH to Claude/Codex, and those TUIs respond with a full redraw. Replaying the
captured Claude repaint bytes twice after the snapshot reproduced the duplicated startup card.

This was **not** the UTF-8 / OSC `0x9C` bug. The UTF-8 bug leaked OSC title text into the grid
before the banner. This bug produced a second real TUI repaint after reconnect.

**Fix:** Browser refresh/reconnect no longer pokes the PTY with font-size changes, Ctrl+L, or
synthetic SIGWINCH-style redraws. The refresh button now asks the server for a fresh emulator
snapshot by sending the structured control frame `__cmd__:replay` over the existing WebSocket
(the backend still accepts legacy `__replay__` for older remote/control paths). The local
WebSocket input loop intercepts that command and calls `Terminal.PushSnapshotTo(wsConsumer)`
instead of routing the text to the PTY.

**Why this is safe:** `PushSnapshotTo` is already the intended self-healing replay primitive for
an attached consumer. It captures the server-side emulator state under the terminal locks and
delivers `ED2` + `ED3` + cursor-home replay bytes to the same WebSocket consumer. It does not
write to the PTY, does not resize ConPTY, does not send Ctrl+L, and does not ask the running TUI
to repaint. That makes it safer than any redraw poke for reconnect/refresh.

**Tradeoff:** Manual refresh now repairs browser-side desync from the C# emulator snapshot. If
the emulator itself is wrong, refresh will faithfully replay the wrong state; that should be
fixed as an emulator/parser bug with captured bytes, not by poking Claude/Codex into repainting.
User-driven font-size changes remain a separate resize/ConPTY redraw concern.

**Regression coverage:**
- Added real-byte fixtures from session `9e670449-54f7-461d-a3de-ae4a9db889ef`.
- `SnapshotReconnect_FollowedByFrontendRefreshPolicy_DoesNotDuplicateStartupCard` fails if
  refresh/reconnect reintroduces a synthetic PTY repaint path such as `triggerRedrawBump()`.
- `SnapshotReconnect_FollowedByOneCapturedPtyRedraw_DoesNotDuplicateStartupCard` pins the
  observed boundary: one captured repaint after snapshot is tolerable, repeated forced repaints
  are what duplicate the startup card.

**Validation:**
- `dotnet test Tests\Tests.csproj --artifacts-path .codex-test-artifacts` -> 288 passed
- `dotnet test TerminalEmulator.Tests\TerminalEmulator.Tests.csproj --artifacts-path .codex-test-artifacts` -> 169 passed, 2 skipped

**Key files touched:**
- `VibeRails/wwwroot/js/modules/terminal-multitab.js` — `requestViewerSnapshotReplay()`,
  `refreshActiveTab()`, removal of reconnect `triggerRedrawBump()`
- `VibeRails/Services/Terminal/Core/TerminalSessionService.cs` — local `__replay__` command
  handling via `Terminal.PushSnapshotTo(...)`
- `Tests/Services/Terminal/Session_9e670449_ReconnectRegressionTests.cs`
- `Tests/Services/Terminal/Fixtures/session_9e670449_before_repaint.bin`
- `Tests/Services/Terminal/Fixtures/session_9e670449_repaint_after_snapshot.bin`

**Guardrail:** Do not reintroduce font-size bumping, Ctrl+L, same-size resize, or any other PTY
redraw poke as a reconnect/refresh mechanism. Reconnect/refresh should replay emulator state;
resize should only happen when the client geometry actually changes.
