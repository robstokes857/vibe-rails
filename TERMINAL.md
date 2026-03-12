# TERMINAL.md

Terminal problem tracker for the Web UI terminal stack.

Date started: 2026-03-07

## Active Issues

None.

---

## Fixed Issues

### ✅ 8. Cursor flicker / jumping cursor positions after reconnect and resize

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

### ✅ 1. Double paste when pasting into the terminal

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

### ✅ 2. Clicking a tab auto-reconnected the terminal

Tab button click passed `connectIfNeeded: true`, making tab selection act as an implicit
reconnect. Fixed by changing all tab activation calls in `addLocalTab()` and `restoreTabs()` to
`connectIfNeeded: false`. Reconnect is now explicit only (Reconnect button, or
`reconnectActiveTab()`).

### ✅ 3. Unselected tabs appeared offline during navigation

Navigation destroyed all browser sockets. Only the active tab reconnected, making inactive tabs
look offline. Resolved as a side effect of the `connectIfNeeded: false` change above. Tabs now
correctly show as paused/disconnected rather than silently re-connecting on activation.

### ✅ 4. History lost on reconnect / hard refresh (current screen)

Current screen state is recovered via the redraw-first reconnect path for AI TUIs. Full
scrollback history intentionally not stored (PTY output persistence remains disabled by design).

### ✅ 5. AI TUI double-render / stale cells on reconnect and resize

Mitigated by:
- redraw-first (not replay) for AI CLI reconnect
- `resetDisplayOnly()` in the shrink-only resize path clears stale xterm cells before a real PTY geometry change
- manager generation guards prevent stale async init from completing after navigation

### ✅ 6. Remote viewer connect/disconnect not visible on native CLI

Fixed by writing directly to `Console.Error` (stderr) on remote attach and detach.
Stderr bypasses the PTY so the TUI is never disturbed.

Key files: `VibeRails/Services/Terminal/TerminalRunner.cs` —
`NotifyRemoteTakeoverAsync`, `HandleRemoteBrowserDisconnectedAsync`

### ✅ 7. Native CLI showed only a blinking cursor in remote browser until resize

**Root cause:** premature `fitAndSyncTerminal({ force: true })` call in `socket.onopen` fired
before the terminal panel CSS had settled, sending wrong cols/rows to the PTY. The PTY redrawn
content arrived into an improperly sized viewport and was invisible.

**Fix:** removed the premature call. `scheduleViewportLayoutSync(40ms)` already fires after
layout settles, sends the correct resize, and triggers the visible PTY redraw. Ctrl+L fallback
added in `HandleRemoteReplayRequestAsync` for the truly-empty-buffer edge case.

Key files:
- `VibeRailsFrontEnd/.../Views/Terminals/Index.cshtml` — `socket.onopen`
- `VibeRails/Services/Terminal/TerminalRunner.cs` — `HandleRemoteReplayRequestAsync`

---

## Notes

- Terminal tracking is consolidated in this root file. The duplicate `VibeRails/TERMINAL.md`
  investigation file was removed on 2026-03-12.
- Do not reintroduce raw replay as the reconnect baseline for current AI CLIs.
- If replay is ever used again, limit it to plain shell / line-oriented sessions only.
- A future screen-state solution should be treated separately from archived output history.
