# TERMINAL.md

Terminal problem tracker for the Web UI terminal stack.

Date started: 2026-03-07

## Current Read

The terminal issues are not one bug. They are a cluster of lifecycle and state-recovery bugs:

1. Browser terminal instances are being torn down and recreated during navigation.
2. Reconnect is currently mixed into tab activation and view restore.
3. AI CLIs are full-screen TUIs, but reconnect/recovery logic still has some line-shell assumptions.
4. Browser scrollback and durable terminal history are separate concerns, and neither is fully restored today.

## Recent Changes

### 2026-03-07

- Removed implicit reconnect on normal tab activation and navigation restore.
- Added manager invalidation guards so stale async terminal initialization should not complete after navigation destroys that manager.
- Left reconnect in explicit flows only, such as the reconnect button and explicit focus/quick-launch reuse paths.

## Active Issues

### 1. Duplicate output after navigation with multiple open terminals

#### Problem

When multiple terminal tabs are open and the user navigates around the app, returning to the terminal view can cause output to appear twice. The duplication is not always symmetric:

- one tab may show only the top portion duplicated
- another tab may show a much larger block duplicated
- a previously open but not currently selected tab can appear to go offline briefly

#### Current hypothesis

This looks like a terminal manager lifecycle problem more than a rendering-only problem.

Relevant code paths:

- `VibeRails/wwwroot/app.js`
  - `loadView()` always calls `terminalController.resetLayoutStateForNavigation()`
- `VibeRails/wwwroot/js/modules/terminal-multitab.js`
  - `TerminalController.resetLayoutStateForNavigation()` destroys the current manager
  - `bindTerminalActions()` creates a new manager and asynchronously calls `initialize()`
  - `TerminalManager.initialize()` restores tabs and auto-activates one with `connectIfNeeded: true`

Likely failure pattern:

1. Navigation destroys the current browser-side terminal manager and closes sockets.
2. A new manager is created when the dashboard or focus view re-renders.
3. The restore/initialize path reconnects automatically.
4. There is no generation token or cancellation guard around in-flight async initialization.
5. If old and new lifecycle work overlap, duplicate browser consumers or duplicate redraw/bootstrap windows can occur.

#### Why the screenshots fit this theory

- only part of one terminal duplicated: likely partial redraw or partial replay window during reconnect
- larger history duplicated in another tab: likely a second attach/bootstrap on a tab with more visible content
- non-selected tab looked offline: navigation destroys all browser sockets, but only the restored/active tab reconnects immediately

#### Fix plan

1. Add a manager-generation or cancellation guard so stale `initialize()` / reconnect work cannot complete after navigation.
2. Make view restore choose tabs without auto-connecting them.
3. Ensure only one browser socket can be active per terminal tab from the local app at a time.
4. Re-test with two or more active tabs while navigating between dashboard, swarm, and focus view.

### 2. Clicking a tab auto-reconnects the terminal

#### Problem

Clicking into a disconnected terminal tab reconnects it automatically. The user does not want selection to imply reconnect.

#### Current hypothesis

This is explicit in the current code.

Relevant code paths:

- `TerminalManager.addLocalTab()`
  - tab button click calls `activateTab(state.id, { connectIfNeeded: true })` for tabs with active sessions
- `TerminalManager.activateTab()`
  - if `connectIfNeeded` is true and the tab has an active session but no open socket, it calls `connect()`

This means simple tab selection acts as an implicit reconnect command.

#### Fix plan

1. Separate `activateTab()` from reconnect behavior.
2. Make tab selection UI-only by default.
3. Reserve reconnect for:
   - explicit reconnect button
   - explicit user command
   - optional future preference if we decide to support auto-reconnect as a setting

### 3. Unselected tabs temporarily appear offline during navigation

#### Problem

When the user navigates away and comes back, a tab that was previously open but not selected can briefly show as offline or disconnected.

#### Current hypothesis

This is consistent with the current manager destroy/restore behavior.

Relevant code paths:

- navigation destroys the browser-side manager and all browser sockets
- restored manager reconnects the chosen active tab during `initialize()`
- inactive tabs remain disconnected until later interaction

This produces the perception that tabs are dropping offline during navigation.

#### Fix plan

1. Document the intended behavior: browser connection state is not the same thing as PTY/session state.
2. After we remove implicit reconnect from tab activation, show disconnected tabs as paused viewers instead of silently reconnecting them.
3. Decide whether navigation should:
   - reconnect no tabs automatically
   - reconnect only the selected tab
   - reconnect all visible tabs

Current recommendation: reconnect none automatically.

### 4. History is lost on reconnect or hard refresh

#### Problem

After reconnect or refresh, the user loses terminal scrollback/history from the browser view.

#### Current hypothesis

There are two different kinds of history:

1. current screen state
2. durable output history / scrollback

Today the app only partially recovers the first, and does not durably restore the second.

Relevant code paths:

- `VibeRails/Services/Terminal/Terminal.cs`
  - keeps only a 16 KB replay buffer
- `VibeRails/Services/Terminal/TerminalSessionService.cs`
  - uses redraw-first reconnect for current AI CLIs instead of replay
- `VibeRails/wwwroot/js/modules/terminal-multitab.js`
  - restores tab chrome/state, not browser xterm scrollback
- `VibeRails/Services/Terminal/TerminalStateService.cs`
  - terminal output persistence is intentionally disabled

So a reconnect can recover the current live screen, but not a full browser history. A hard refresh definitely loses the browser xterm buffer because that state lived in JavaScript memory.

#### Fix plan

1. Re-enable durable output persistence for terminal output.
2. Keep reconnect fidelity work separate from history work.
3. Evaluate a proper screen-state restore mechanism instead of raw byte replay for AI TUIs.

## Known TUI-Specific Rendering Problems

### 5. AI terminal reconnect/resize can double-render or leave stale cells

#### Problem

AI CLIs such as Claude and Codex use full-screen TUI patterns. Replaying partial PTY history or resizing without clearing stale local cells can duplicate welcome cards, prompts, or layout borders.

#### Current status

Partially mitigated already:

- local reconnect uses redraw-first instead of replay for current AI CLIs
- resize path clears the local xterm view before a real PTY geometry change

This reduces duplication, but does not solve the navigation/lifecycle bugs above.

## Working Theory

The main unresolved problem is local browser lifecycle, not PTY process lifetime.

The PTY and tab child processes are usually still running. The unstable part is:

- browser socket ownership
- manager initialization/destruction overlap
- reconnect policy being tied to selection and view restore

## Next Fix Order

1. Remove implicit reconnect from tab activation.
2. Add cancellation/generation guards to terminal manager initialization and restore flows.
3. Make navigation restore non-destructive to tab state, but non-reconnecting by default.
4. Re-enable durable terminal output persistence.
5. After lifecycle is stable, evaluate proper screen-state snapshot/restore for AI TUIs.

## Notes

- Do not reintroduce raw replay as the reconnect baseline for current AI CLIs.
- If replay is ever used again, limit it to plain shell / line-oriented sessions only.
- A future screen-state solution should be treated separately from archived output history.
