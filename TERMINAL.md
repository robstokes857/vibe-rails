# TERMINAL.md

Terminal problem tracker for the Web UI terminal stack.

Date started: 2026-03-07

## Active Issues

### 1. Duplicate output / incorrect render on navigation and reload

#### Problem

When the user navigates away from the terminal view and returns, or reloads the page, the terminal
can show duplicate output or render incorrectly after reconnecting. The duplication is not always
symmetric and can vary between tabs.

#### Current hypothesis

Terminal manager lifecycle problem. On every navigation `resetLayoutStateForNavigation()` destroys
the current manager and closes all sockets. A new manager is created when the view re-renders and
`initialize()` restores tabs. There is still something in the reconnect/redraw path that causes
content to appear twice, but the exact trigger has not been isolated yet.

Relevant code paths:

- `VibeRails/wwwroot/js/modules/terminal-multitab.js`
  - `TerminalController.resetLayoutStateForNavigation()` — destroys the manager and generation guard
  - `TerminalManager.initialize()` — restores tabs with `connectIfNeeded: false`
  - `TerminalTab.connect()` — resets xterm before opening the socket

#### Status

Ongoing. Previous fixes (generation guards, removing implicit reconnect from tab activation,
`connectIfNeeded: false` in restore) reduced but did not eliminate the issue.

---

### 2. Double cursor on page reload

#### Problem

After a hard page reload, reconnecting a terminal that has an active session shows two cursors on
screen simultaneously. This does **not** happen on normal in-app navigation — only on full page
reload.

#### Hypothesis

Root cause is `resetDisplayOnly()` being triggered mid-stream during the initial connect sequence.

Sequence of events:

1. `connect()` calls `vibeTerminal.reset()` before the socket opens — cursor goes to (0,0).
2. `socket.onopen` fires, `fitAndSyncTerminal()` runs, `sendResizeToPty()` fires immediately.
   `shouldResetDisplayBeforeResize()` returns true (socket is open, session is active) so
   `resetDisplayOnly()` is called again. Resize sent to PTY.
3. `scheduleFitPasses()` queues an RAF fit and a 120 ms deferred fit.
4. PTY receives the resize and starts sending the redrawn screen, which includes ANSI cursor
   positioning escape sequences.
5. The RAF fit or the WebFontsAddon `onLoaded` callback fires `scheduleFitPasses()` while PTY
   data is still mid-stream. If the measured size changed (e.g. fonts not yet settled on first
   load), another `sendResizeToPty()` runs, `shouldResetDisplayBeforeResize()` fires, and
   `resetDisplayOnly()` clears xterm to (0,0) while the AI CLI's ANSI stream expects the cursor
   to already be at its current TUI position.
6. The next batch of PTY data writes from where the CLI thinks the cursor is, while xterm's own
   cursor is at (0,0). Both positions are visible — two cursors.

This is unique to page reload because fonts are not cached in JavaScript memory, so the
WebFontsAddon `onLoaded` event fires and changes the measured cell metrics during the initial
connect window.

#### Fix attempted (2026-03-08)

Added `_initialConnectActive` flag to `TerminalTab`:

- Set to `true` at the start of `connect()`, immediately before the socket is created.
- Cleared to `false` in `socket.onmessage` when the first data frame arrives.
- Also cleared in `disconnect()` to avoid stale flag on forced teardown.
- `shouldResetDisplayBeforeResize()` returns `false` while `_initialConnectActive` is true.

This prevents any resize-driven `resetDisplayOnly()` from firing between socket open and first
data arrival, while still allowing mid-stream resets for user-initiated resizes after the session
is live.

Key file: `VibeRails/wwwroot/js/modules/terminal-multitab.js` — `TerminalTab`

---

## Fixed Issues

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
- `resetDisplayOnly()` in the resize path clears stale xterm cells before a real PTY geometry change
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

- Do not reintroduce raw replay as the reconnect baseline for current AI CLIs.
- If replay is ever used again, limit it to plain shell / line-oriented sessions only.
- A future screen-state solution should be treated separately from archived output history.
