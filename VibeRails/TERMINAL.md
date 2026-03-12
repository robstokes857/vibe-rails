# Terminal Architecture & Known Issues

## Architecture Overview

The web terminal uses a **multi-tab** system built on xterm.js.

### Frontend layers (`wwwroot/js/modules/`)

| Class | File | Responsibility |
|-------|------|---------------|
| `TerminalController` | `terminal-multitab.js` | Singleton held by the SPA app. Creates/destroys `TerminalManager` instances as the user navigates between views. Tracks a `managerGeneration` counter to cancel stale async init races. |
| `TerminalManager` | `terminal-multitab.js` | One per mounted view (dashboard panel or terminal-focus page). Owns the tab strip DOM, all `TerminalTab` instances, resize/lock/focus layout handlers, and the settings panel. Destroyed and recreated on every navigation via `resetLayoutStateForNavigation()` + `destroy()`. |
| `TerminalTab` | `terminal-multitab.js` | One per browser tab. Owns a `VibeTerminal` (xterm.js wrapper) and a `WebSocket`. Created lazily — `ensureTerminal()` creates xterm only when the tab is first activated or connected. |
| `VibeTerminal` | `vibe-terminal.js` | Thin wrapper around `xterm.Terminal`. Handles fit/resize debounce, ResizeObserver, xterm addon loading (fit, search, web-links, ligatures, image, progress), safe byte writes, clipboard paste intercept, and scroll-follow logic. |

### Backend layers (`Services/Terminal/`, `Routes/`)

| Component | Responsibility |
|-----------|---------------|
| `TerminalTabHostService` | Main process. Manages a pool of child `vb` processes (one per browser tab). Proxies browser WebSocket connections to `ws://127.0.0.1:{childPort}/api/v1/terminal/ws`. |
| `TerminalSessionService` | Child process. Owns the PTY (`Terminal.cs`), the circular replay buffer, and the `WebSocket` consumer subscription. Called by `TerminalRoutes.cs`. |
| `Terminal.cs` | PTY wrapper. Spawns a shell (`pwsh.exe` / `bash`), runs a single read loop, dispatches output to `ITerminalConsumer` subscribers, and maintains the `CircularBuffer` replay. |
| `TerminalResizeCoordinator` | Static helper. Applies PTY resize (`cols×rows`) and optionally debounces a `Ctrl+L` redraw. Used by both the resize command path and the pre-replay resize path. |
| `CircularBuffer` | Stores the last ≈10 MB of PTY output. Tracks ANSI "break points" (`\x1b[?1049h`, `\x1b[2J`, etc.) so replay can start from a clean screen state. |

### WebSocket connection flow (per tab reconnect)

```
Browser
  └─ GET /api/v1/terminal/tabs/{tabId}/ws?cols=X&rows=Y   (TerminalTabsRoutes)
       └─ TerminalTabHostService.HandleWebSocketProxyAsync
            └─ ClientWebSocket → ws://127.0.0.1:{port}/api/v1/terminal/ws?cols=X&rows=Y
                 └─ TerminalSessionService.HandleWebSocketAsync
                      1. Pre-resize PTY to cols×rows          ← NEW (2026-03-12)
                      2. Send replay buffer (CircularBuffer)
                      3. Subscribe WebSocket as live consumer
                      4. Start input loop (resize commands, keystrokes)
```

### Navigation lifecycle

Every call to `app.loadView(view)` calls `terminalController.resetLayoutStateForNavigation()` first, which:
1. Increments `managerGeneration` (cancels any in-flight `bindTerminalActions` promise).
2. Calls `manager.resetLayoutStateForNavigation()` (tears down lock/focus layout handlers).
3. Calls `manager.destroy()` — disposes every `TerminalTab` (closes WebSocket, disposes xterm), clears the tab map.

The new view then calls `bindTerminalActions(container, ...)` which creates a fresh `TerminalManager`. On init it calls `restoreTabs()` (fetches `/api/v1/terminal/tabs`) and reconnects to any live sessions.

The PTY process **survives** navigation — only the browser-side socket and xterm instance are torn down.

---

## Replay Buffer

The backend uses a `CircularBuffer` (default ≈10 MB, set via `Terminal.DefaultReplayBufferSize`) to store PTY output. On WebSocket reconnect the buffer is replayed from the **last ANSI break point** (sequences like `\x1b[?1049h` enter-alternate-screen or `\x1b[2J` clear-display), so the viewer sees the current screen without replaying the entire session history.

For CLIs where replay causes rendering issues (Codex), `ShouldUseReplayBuffer()` returns `false` and the code falls back to sending `Ctrl+L` (0x0C) to trigger an in-app redraw instead.

---

## Open / Fixed Issues

### ~~Double Print + Dual Cursor After Navigating To Terminal Screen and Back~~ ✅ Fixed (2026-03-12)

**Symptom**: Open a terminal in the dashboard → navigate to the Terminal screen (terminal-focus view) → navigate back. The dashboard terminal shows all output twice and renders two cursors.

**Root cause**: A size mismatch between the replay buffer and the first client-side resize.

Detailed sequence before the fix:

1. User is on the dashboard. Terminal is running at, say, 90×28 (dashboard panel size).
2. User opens the terminal-focus (fullscreen) view. A new `TerminalManager` connects and the client sends `__resize__:140×42`. PTY is now 140×42. Shell/TUI redraws at that size.
3. User navigates back to the dashboard. `resetLayoutStateForNavigation()` destroys the terminal-focus manager (closes socket, disposes xterm).
4. Dashboard loads. A new `TerminalManager` is created. `activateTab()` calls `connect()`.
5. `connect()` calls `ensureTerminal()` — creates a fresh xterm in a **hidden** `#terminal-container` (display:none). `fit()` cannot measure real dimensions, so xterm stays at its constructor defaults (120×40). xterm is reset (empty).
6. WebSocket connects. Backend sends the replay — captured at **140×42** (the fullscreen size). xterm (120×40) renders this; lines may wrap differently but content appears.
7. Client `onopen` fires. `fitAndSyncTerminal()` calls `fit()` — still inside a hidden container, real dimensions still unknown. Sends `__resize__:120×40` (default xterm size).
8. Backend applies resize. PTY sends SIGWINCH. Shell/TUI **redraws at 120×40** and sends the new output through the WebSocket consumer subscription.
9. `onmessage` fires again with the redrawn content — written on top of the replay. **Content appears twice.**
10. Later, `#terminal-container` becomes visible (via `updateUi()` → `showTerminal()`). ResizeObserver fires (100 ms debounce) → detects real size (e.g., 90×28) → `sendResizeToPty()` → `resetDisplayOnly()` clears xterm → sends `__resize__:90×28` → PTY SIGWINCH → third redraw. This one looks correct, but by now the user already saw the double-draw.

**Why the "2 cursors"**: the replay left a cursor at one xterm cell position; the SIGWINCH redraw placed the cursor at a different position. Both positions were rendered simultaneously before the next clear.

**Fix — 6 files changed**:

*`terminal-multitab.js`*:
- `TerminalManager.activateTab()`: calls `this.showTerminal()` **before** `target.instance.connect()` when `connectIfNeeded && hasActiveSession`. This makes `#terminal-container` visible so `fit()` measures real pixel dimensions during `ensureTerminal()`.
- `TerminalTab.connect()`: after `ensureTerminal()` and `vibeTerminal.reset()`, explicitly calls `vibeTerminal.fit({ force: true })` and captures the resulting `cols`/`rows` as `preConnectCols`/`preConnectRows`.
- `TerminalManager.getWebSocketUrl(tabId, cols, rows)`: now accepts optional dimensions and appends `?cols=X&rows=Y` to the URL when they are positive integers.

*`TerminalTabsRoutes.cs`*:
- Reads `cols` and `rows` from `context.Request.Query` (validated as positive integers).
- Passes them to `tabHost.HandleWebSocketProxyAsync(tabId, webSocket, cols, rows, ct)`.

*`TerminalTabHostService.cs`*:
- `ITerminalTabHostService.HandleWebSocketProxyAsync` updated to accept `int? cols = null, int? rows = null`.
- Implementation builds `?cols={cols}&rows={rows}` query string and appends it to the upstream URI (`ws://127.0.0.1:{port}/api/v1/terminal/ws{query}`), forwarding the dimensions to the child process.

*`TerminalSessionService.cs`*:
- `ITerminalSessionService.HandleWebSocketAsync` updated to accept `int? cols = null, int? rows = null`.
- Implementation calls `TerminalResizeCoordinator.ApplyResize(terminal, _stateService, sessionId, cols.Value, rows.Value, TerminalIoSource.LocalWebUi)` **before** the replay buffer is sent. If resize throws it is caught and logged as a warning (non-fatal).

*`TerminalRoutes.cs`*:
- Same `cols`/`rows` query-string read + pass-through added for the direct (non-proxied) `/api/v1/terminal/ws` endpoint used by older (non-tab) terminal paths.

**Result**: The replay buffer is generated *after* the PTY has been resized to the client's current dimensions. The replay arrives at the correct size. When the client later sends the same resize in `onopen`, the PTY is already at that size — SIGWINCH either doesn't fire or produces identical content. No double draw, no dual cursor.
