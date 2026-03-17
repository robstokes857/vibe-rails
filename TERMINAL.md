# TERMINAL.md

Terminal problem tracker for the Web UI terminal stack.

Date started: 2026-03-07

## Active Issues

Only this terminal issue is currently considered open.

### 🐛 Remaining roaming cursor still visible in Web UI terminal

The cursor issue is improved but not fully gone. The old extra cursor that ignored terminal
setting changes appears to be fixed, but users still report one cursor artifact that flies
around the viewport, usually less often than before, and can still end up near the bottom-right
corner during wraps or redraws.

**Root cause (confirmed 2026-03-16):** xterm.css hides textarea content via `opacity: 0` but
does NOT set `outline: none` on the helper textarea (only on `.xterm.focus`). On Windows/Chrome
the browser focus-ring is composited at the OS level and can show through `opacity: 0`, so the
focused textarea's outline rectangle appears as a phantom cursor at the cursor position. During
line wrap the textarea briefly stays at the old right-edge position before updating — exactly
the "parks at bottom-right" symptom. `caret-color: transparent` alone was insufficient because
the visible artefact was the outline/focus-ring, not the text caret.

**2026-03-16 fix:** added `outline: none !important` and `opacity: 0 !important` to
`.vb-terminal-element .xterm-helper-textarea` in `style.css`; added matching inline
`ta.style.outline = 'none'` and `ta.style.opacity = '0'` in `patchTextarea()` as runtime
backup.

**Retest needed:** verify no phantom cursor while typing, reconnecting, resizing, hard
refreshing. Confirm the bottom-right artefact on wrap is gone.

Key files:
- `VibeRails/wwwroot/style.css` — `.vb-terminal-element .xterm-helper-textarea`
- `VibeRails/wwwroot/js/modules/vibe-terminal.js` — `patchTextarea()`

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

### ✅ Double print / full-session duplicate replay on reconnect and hard refresh

On reconnect and especially on hard refresh, the browser could repaint the top/full screen
twice or replay the entire visible AI CLI session again. This was most noticeable with
full-screen TUI CLIs like Codex/Claude/Gemini/Copilot.

**Root cause:** the local WebSocket attach path unconditionally sent `terminal.GetGridReplay()`
before subscribing the live WebSocket consumer. For managed AI CLI sessions, this conflicted
with redraw-style attach behavior and caused duplicated TUI content on browser reconnect / hard
refresh. Plain shell / line-oriented sessions could still use replay, but managed AI CLIs
needed redraw-first attach instead.

**Fix:** updated `VibeRails/Services/Terminal/TerminalSessionService.cs` so managed AI CLIs
(`Claude`, `Codex`, `Gemini`, `Copilot`) now skip local replay on attach and instead subscribe
the WebSocket first, then request a redraw with `Ctrl+L`. Plain shell / line-oriented sessions
still use `GetGridReplay()`.

**2026-03-16 retest:** user confirmed this tested good. The duplicate replay / double print
issue no longer reproduces in current testing.

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
- redraw-first (not replay) for AI CLI reconnect
- `resetDisplayOnly()` in the shrink-only resize path clears stale xterm cells before a real PTY geometry change
- manager generation guards prevent stale async init from completing after navigation

### ✅ Cursor stuck at bottom-right and not blinking after replay

After reconnect the cursor appeared frozen at the bottom-right corner of the xterm.js viewport
and cursor blink was disabled.

**Root cause:** `\u001bc` (RIS hard reset) resets xterm.js cursor state to its defaults —
visible but **not blinking**. TUI apps normally re-enable blink via `\x1b[?12h` on startup, but
those sequences are ephemeral and not captured in the emulator cell grid, so they are never
replayed. The cursor position was also left at the end of the last cell written before the CUP
reposition.

**Fix:** append to `TerminalGridSerializer.Serialize()` after the CUP sequence:
- `\u001b[0m` — clear residual SGR from the last cell
- `\u001b[?25h` — cursor visible (DECTCEM)
- `\u001b[?12h` — cursor blink on (ATT160)

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

**`TerminalGridSerializer.Serialize()`:**
- Emits `\x1bc` (hard reset) to clear xterm.js including its own scrollback
- Writes scrollback rows oldest-first, each with `\r\n`, using delta SGR encoding
- Writes current screen rows
- Repositions cursor to the emulator's current cursor position
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
- If replay is ever used again, limit it to plain shell / line-oriented sessions only.
- **Sluggish typing (accepted):** ~20 ms per character echo latency is inherent — xterm.js v6
  `WriteBuffer` batches via `setTimeout(0)` (~4 ms) + rAF render (~16 ms). No delay on our
  `onData` → `socket.send()` path; the bottleneck is xterm.js's async write pipeline. Fixing
  would require local echo or xterm.js internal APIs. Accepted as-is.
