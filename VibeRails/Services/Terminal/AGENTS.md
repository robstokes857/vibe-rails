# Terminal Service

Current implementation reference for `VibeRails/Services/Terminal`.
Verified against source on 2026-08-01.

## Scope
This folder owns PTY lifecycle, session tracking hooks, local WebSocket viewer handling, and remote relay integration.

Primary files (reorg'd into subdirectories):
- `Services/Terminal/Pty/Terminal.cs`, `KeyTranslator.cs`, `ShellCommandBuilder.cs`
- `Services/Terminal/Core/TerminalRunner.cs`, `TerminalSessionService.cs`, `TerminalStateService.cs`, `TerminalTabHostService.cs`, `ChildParentWatchdogService.cs`, `NativeConsoleGeometry.cs`
- `Services/Terminal/Protocol/TerminalIoRouter.cs`, `TerminalControlProtocol.cs`, `TerminalGridSerializer.cs`, `TerminalTextSanitizer.cs`, `TerminalTextWithControlPart.cs`
- `Services/Terminal/Remote/RemoteTerminalConnection.cs`
- `Services/Terminal/Consumers/*.cs`
- `Services/Terminal/Observers/*.cs`
- `Services/Terminal/Sessions/*.cs` (session activity state, output writer, resize coordinator)
- `Services/Terminal/Commands/CommandService.cs`
- `Services/Terminal/Interfaces/*.cs` (interface contracts, namespace stays flat)

Related routes/UI:
- `Routes/TerminalRoutes.cs`
- `wwwroot/js/modules/terminal-multitab.js`

Remote relay server (other repo):
- `C:\source\VibeRailsFrontEnd\VibeRails-Front\VibeRails-Front`

## Core Architecture

```
Terminal (PTY owner, single read loop)
  - TerminalEmulator (20k-line scrollback, drives grid replay/snapshot attach)
  - Subscribe(ITerminalConsumer) / SubscribeWithSnapshot(ITerminalConsumer)
      - ConsoleOutputConsumer
      - DbLoggingConsumer
      - WebSocketConsumer (local viewer)
      - RemoteOutputConsumer (relay path)
      - TerminalEmulatorConsumer (internal, feeds the headless emulator)
  - WriteAsync / WriteBytesAsync
  - Resize
```

Design invariants:
1. One PTY read loop per session.
2. Output fan-out is synchronous dispatch to consumers.
3. Consumers must be non-blocking.
4. Replay buffer is always maintained by `Terminal`.
5. All input and output routing should pass through `TerminalIoRouter`.
6. Attach/reconnect currently uses atomic emulator snapshots as an active experiment and is not yet a permanent guardrail for all AI CLIs.

## Control Protocol
Defined in `TerminalControlProtocol.cs`.

Commands:
- `__replay__`
- `__browser_disconnected__`
- `__disconnect_browser__[:reason]`
- `__resize__:{cols},{rows}`
- `__cmd__:{command}[:payload]` — structured command prefix framework (e.g. `__cmd__:replay`)
- PIN challenge protocol (sent as plain text, not `__cmd__:`):
  - `__PIN__:{pin}` — PIN challenge response from the viewer
  - `__LOCKED__` / `__UNLOCKED__` — lock-state frames

Validation:
- max inbound message size: `256 * 1024` bytes
- resize range: cols `10..1000`, rows `5..500`
- disconnect reasons are sanitized and truncated to 120 chars before sending

## Component Responsibilities

### `Terminal.cs`
- Spawns PTY (`pwsh.exe` on Windows, `/bin/zsh` on macOS, `bash` on Linux — resolved via `ShellDefaults`).
- Feeds all output into a headless `TerminalEmulator` (20k-line scrollback) via an internal `TerminalEmulatorConsumer`.
- Dispatches every PTY read chunk to current consumer snapshot.
- Exposes `GetGridReplay()` (ANSI byte stream from emulator grid) and `Resize(cols, rows)`.
- `SubscribeWithSnapshot(...)` / `PushSnapshotTo(...)` provide atomic snapshot + live attach for reconnect.
- `CreateAsync(..., title)` sets PTY name; supports `app`/`argv` to spawn a specific program instead of an interactive shell.
- Implements `IAsyncDisposable` and kills PTY on dispose.

### `ITerminalConsumer.cs`
- Contract: `void OnOutput(ReadOnlyMemory<byte> data)`.
- Called synchronously from `Terminal.ReadLoopAsync()`.

### Consumers

`ConsoleOutputConsumer.cs`
- Decodes UTF-8 and writes to host console.

`DbLoggingConsumer.cs`
- Routes PTY output through `TerminalIoRouter.RouteOutput(...)`.

`WebSocketConsumer.cs`
- Local viewer output consumer.
- Uses channel-backed send loop to serialize WebSocket `SendAsync` calls.
- Copies frame bytes (`ToArray`) before enqueueing.

`RemoteOutputConsumer.cs`
- Relay output consumer.
- Calls `IRemoteTerminalConnection.SendOutputAsync(...)`.
- Safe because remote connection copies payload before queueing.

`TerminalEmulatorConsumer.cs`
- Internal consumer that feeds all PTY output bytes into the headless `TerminalEmulator` so `GetGridReplay()` always reflects the current screen state.
- Thread-safe via the shared emulator lock.

`NativeConsoleOutputFilter.cs`
- Not a consumer, but lives in `Consumers/`. Strips XTWINOPS window-geometry operations from PTY output on its way to the **real OS console** of a native session, so an inner app cannot resize the outer console window out from under the geometry poll. Narrow on purpose; `DbLoggingConsumer`, `WebSocketConsumer`, and `RemoteOutputConsumer` still receive the unmodified stream.

### `TerminalRunner.cs`
Session orchestrator.

`TerminalRunner` does **not** own session preparation — it delegates to
`CommandService.PrepareSessionAsync` (see the `Commands/CommandService.cs` section below) to
build the launch command, setup commands, env vars, and proxy context. `TerminalRunner` itself
injects `ILocalToolApiContext` (root tool routing) and `ILlmProxySessionState` (per-tab proxy
gate state); the Claude/Codex proxy URL/auth env vars are built inside `CommandService` via
`ILocalLlmProxyContext`.

`CreateSessionAsync(...)`
1. Creates DB/logging session via `ITerminalStateService.CreateSessionAsync`.
2. Spawns PTY via `Terminal.CreateAsync`.
3. Subscribes `DbLoggingConsumer`.
4. If remote access is enabled and API key exists:
   - opens relay socket via `RemoteTerminalConnection.ConnectAsync`
   - subscribes `RemoteOutputConsumer`
   - wires remote input -> `TerminalIoRouter.RouteInputAsync(..., RemoteWebUi)`
   - wires remote resize -> PTY resize
   - wires remote replay request -> send replay buffer
   - tracks remote connection in `TerminalStateService`
5. Sends final CLI command to shell.

`RunCliAsync(...)`
- Creates session, subscribes `ConsoleOutputConsumer`, starts read loop, runs console input loop through `TerminalIoRouter`.

`RunCliWithWebAsync(...)`
- Same as `RunCliAsync` plus external registration for local web viewer access.
- If remote connection exists, remote takeover disconnects local viewer on replay or remote input activity.

### `TerminalSessionService.cs`
Owns active local terminal session state for `/api/v1/terminal/*`.

Shared static fields (single active session model):
- `s_terminal`
- `s_sessionId`
- `s_activeWebSocket` (current local viewer)
- `s_sessionOwnerId` (lifecycle owner while session is active)
- `s_externallyOwned` (CLI-owned session flag)

Key behavior:
- `StartSessionAsync` starts a web-owned terminal session.
- `RegisterExternalTerminal` / `UnregisterTerminalAsync` allow CLI-owned sessions to be exposed to local web UI.
- `HandleWebSocketAsync` (local viewer):
  1. validates active terminal
  2. local takeover: closes previous local viewer socket
  3. requests remote viewer disconnect (`RequestRemoteViewerDisconnectAsync`)
  4. reconnect bootstrap:
     - current branch uses `SubscribeWithSnapshot(...)` for atomic snapshot + live attach
     - this is an intentional trial replacement for the previous redraw-first reconnect path
     - if AI CLIs regress, fall back to redraw-first attach and re-test before keeping snapshot attach enabled
  5. subscribes `WebSocketConsumer`
  6. runs input loop (supports fragmentation, size guard, resize control) and routes user input through `TerminalIoRouter`
- `DisconnectLocalViewerAsync(reason)` closes local viewer with provided reason.
- `StopSessionAsync` is blocked for externally owned sessions.
- `SendInputAsync` writes tool/API input through `TerminalIoRouter` without attaching a viewer
  WebSocket or changing local/remote takeover state.

Important current behavior:
- Local and remote attach currently use atomic emulator snapshots instead of the previous redraw-first reconnect path.
- This is a trial change intended to eliminate snapshot/live interleaving races and must be re-validated against the managed AI CLIs.
- Local reconnect/takeover does not dispose PTY.
- Session activity acquires a lifecycle owner so idle local-browser watchdog does not terminate active remote sessions.

### `TerminalStateService.cs`
DB/session state + remote connection bookkeeping.

Interface:
- `CreateSessionAsync`
- `LogOutput`
- `RecordInput`
- `TrackRemoteConnection`
- `RequestRemoteViewerDisconnectAsync`
- `CompleteSessionAsync`

Notes:
- Uses static dictionaries for session accumulators and remote connections (shared across scoped instances).
- Uses `InputAccumulator` for input recording.
- On complete: closes remote connection and deregisters remote active terminal.
- Accepts source metadata in `RecordInput`/`LogOutput`.
- Publishes terminal I/O events through `ITerminalIoObserverService`.

### `TerminalIoRouter.cs`
Single I/O funnel and hook point.

Responsibilities:
- `RouteInputAsync(...)`:
  - decodes input text
  - calls `ITerminalStateService.RecordInput(...)`
  - writes bytes to PTY
- `RouteOutput(...)`:
  - decodes output text
  - calls `ITerminalStateService.LogOutput(...)`

### `TerminalIoObserverService.cs`
DI-based observer dispatch.

Hook surface:
- Implement `ITerminalIoObserver`.
- Register in DI (for example: `AddScoped<ITerminalIoObserver, MyObserver>()`).
- Events are delivered as `TerminalIoEvent` with source values such as `LocalCli`, `LocalWebUi`, `RemoteWebUi`, and `Pty`.

### `RemoteTerminalConnection.cs`
Client WebSocket from CLI app -> relay server `/ws/v1/terminal`.

Behavior:
- Sends binary PTY output and text control messages using queued send loop.
- Receives text/binary with fragmentation support and size guard.
- Raises events:
  - `OnInputReceived`
  - `OnReplayRequested`
  - `OnBrowserDisconnected`
  - `OnReconnected`
  - `OnResizeRequested`
  - `OnCommandReceived`

### `RemoteStateService.cs` (moved to `Services/Integrations/VibeCodeRemote/`)
HTTP registration with relay server:
- `POST /api/v1/terminal` on session create
- `DELETE /api/v1/terminal` on session complete

> **Note:** `RemoteStateService` now lives at `Services/Integrations/VibeCodeRemote/RemoteStateService.cs`, not inside the Terminal folder. It is consumed by `TerminalStateService` via `IRemoteStateService`.

### `Sessions/` subdirectory
- `SessionActivityState.cs` — tracks per-session input/output/activity timestamps and idle notification state; owns a `CancellationToken` for the session.
- `SessionOutputWriter.cs` — channel-backed writer that buffers and persists session output to the DB via `IRepository`; tracks alt-screen state and flushes at 5 MB threshold.
- `TerminalResizeCoordinator.cs` — centralizes PTY resize handling so resize hooks and optional debounced redraw (`Ctrl+L`) are consistent across local and remote viewer paths.

### `Commands/CommandService.cs`
Owns session preparation (`PrepareSessionAsync`) — builds the `PreparedTerminalSession` record
(launch command, setup commands, environment, optional executable/argv) for terminal sessions.
- Adds the VibeRails MCP stdio registration setup command for managed CLIs
  (Claude, Codex, Antigravity, Copilot, and OpenCode-backed CLIs incl. GLM 5.2 / Kimi K3).
- Builds base env vars (`LANG`, `LC_ALL`, `PYTHONIOENCODING`).
- Merges CLI-specific env vars via `LlmCliEnvironmentService` when an environment name is provided.
- Resolves the MCP stdio server command path (published `vb.exe mcp` vs `dotnet <dll> mcp`).
- Runs CLI MCP auto-registration commands (remove + add) via the platform shell.
- Builds Claude/Codex proxy env vars via `ILocalLlmProxyContext` (the current process's proxy URL
  and auth), so each terminal-tab child can apply its own process-local token-saver gate without
  breaking root tool routing, and records the proxied launch via `ILlmProxySessionState`.
  (`TerminalRunner` separately injects `ILocalToolApiContext` for root tool routing and also holds
  `ILlmProxySessionState` for per-tab proxy gate state.)

### `Pty/` additional files
- `KeyTranslator.cs` — translates `Console.ReadKey` results to ANSI escape sequences for the CLI terminal path.
- `ShellCommandBuilder.cs` — builds a chain of setup commands joined with `;` followed by the CLI launch command.

### `Protocol/` additional files
- `TerminalGridSerializer.cs` — converts a `TerminalEmulator` snapshot (scrollback + current screen) into an ANSI byte stream that xterm.js renders instantly on reconnect.
- `TerminalTextSanitizer.cs` — strips ANSI escape/control sequences and non-printable characters from raw terminal text to produce plain text.
- `TerminalTextWithControlPart.cs` — enum and helpers classifying common ANSI/control sequence types in PTY streams.

### `Core/` additional files
- `ChildParentWatchdogService.cs` — `BackgroundService` registered only in tab child processes (`--parent-pid`); exits the child when the root backend dies ungracefully so children don't become orphans.
- `NativeConsoleGeometry.cs` — reads the visible cell grid of the real console hosting a native session so the inner PTY uses matching dimensions.

### Observer implementations (`Observers/`)
- `GitDiffIdleCaptureObserver.cs` — forwards terminal idle + session-complete events to `IGitDiffCaptureService` for git diff capture.
- `WaitingForUserInputObserver.cs` — detects when Codex is sitting at a prompt waiting for user input by analyzing PTY chunk repetition patterns.
- `SessionStateEventObserver.cs` — publishes session lifecycle state changes to `IAppEventBus` so browser clients receive real-time updates (metadata only, no raw I/O).
- `MyTerminalObserver.cs` — debugging/development observer.

`Observers/` also holds `TerminalIoObserverService.cs`, the dispatcher described in its own section above (not an observer implementation itself).

## Session Modes

### 1) Web-owned session
Entry:
- `POST /api/v1/terminal/start` in `Routes/TerminalRoutes.cs`

Flow:
1. route validates CLI and optional environment
2. `TerminalSessionService.StartSessionAsync(...)`
3. runner creates PTY session and starts read loop
4. local viewer connects to `/api/v1/terminal/ws`

Stop:
- `POST /api/v1/terminal/stop`
- allowed only if not externally owned

### 2) CLI-owned session with web viewer
Entry:
- `Program.cs` + `CliLoop.RunTerminalWithWebAsync(...)` (when `--env`/bootstrap mode is active)

Flow:
1. runner starts PTY + console I/O
2. `RegisterExternalTerminal(...)` exposes same PTY to local web viewer endpoint
3. on CLI exit: `UnregisterTerminalAsync()` closes local viewer socket if connected

## Local API Surface
From `Routes/TerminalRoutes.cs`:
- `GET /api/v1/terminal/status`
- `POST /api/v1/terminal/start`
- `POST /api/v1/terminal/stop`
- `POST /api/v1/terminal/input`
- `GET /api/v1/terminal/snapshot`
- `GET /api/v1/terminal/bootstrap-command`
- `WS /api/v1/terminal/ws`

Agent/tool control surface (from `Routes/AgentToolRoutes.cs`):
- `GET /api/v1/agent-tools/terminal`
- `POST /api/v1/agent-tools/terminal/open`
- `POST /api/v1/agent-tools/terminal/input`
- `POST /api/v1/agent-tools/terminal/{tabId}/input`
- `POST /api/v1/agent-tools/terminal/snapshot`
- `GET /api/v1/agent-tools/terminal/{tabId}/snapshot`
- `WS /api/v1/agent-tools/ws` with JSON actions `list_terminals`, `open_terminal`,
  `send_terminal_input`, and `get_terminal_snapshot`.

These tool endpoints are non-viewer control paths. They must not call `HandleWebSocketAsync`
or connect to `/api/v1/terminal/ws`, because that route enforces viewer takeover semantics.

Terminal snapshot responses include plain `screenText` and reserved renderer hints. `xterm_ui_bytes`
contains base64 ANSI replay bytes generated from the backend `TerminalEmulator` grid serializer, so
xterm.js consumers can render the current TUI state without taking over the terminal WebSocket.
`xterm_png_string` is nullable on the backend and is intended to be filled by browser consumers after
they render/capture the xterm canvas.

## Takeover Rules (Current)

1. Local viewer A -> local viewer B:
- old local WebSocket is closed with reason `Session taken over`.

2. Local viewer connects while remote viewer is active:
- local side sends `__disconnect_browser__:{reason}` via relay socket.
- relay closes remote browser WebSocket.

3. Remote viewer connects while local viewer is active:
- relay may send `__replay__` OR input activity (for example Codex attach path using `Ctrl+L`).
- local side disconnects local viewer on first remote takeover signal with reason `Session taken over by remote viewer`.

4. Remote viewer A -> remote viewer B:
- relay service enforces one browser per session and closes old browser.

## Frontend Notes (local web UI)
`wwwroot/js/modules/terminal-multitab.js`:
- xterm.js with FitAddon
- WebSocket binary mode (`arraybuffer`)
- sends resize control `__resize__:{cols},{rows}` after fit and on resize
- de-dupes identical resize frames to reduce redraw churn/cursor jitter
- displays close reason in terminal

## Known Constraints
1. Single active terminal session (`TerminalSessionService` static state).
2. One active local web viewer at a time.
3. Grid replay is generated from the headless `TerminalEmulator` (20k-line scrollback), not a fixed byte ring buffer.
4. Input/output are raw terminal bytes; rendering correctness depends on xterm configuration and PTY dimensions.
5. `ITerminalIoObserverService` dispatch is in-process only.

## Common Failure Points
1. Concurrent `SendAsync` on same WebSocket (avoided by channel-backed send loops).
2. Shared buffer reuse corruption (avoided by copying before async send queueing).
3. Reconnect duplication from replaying partial AI CLI terminal history (previously mitigated by redraw-first reconnect policy for all current managed AI CLIs; the current branch is intentionally trialing atomic snapshot attach instead).
4. Oversized control/input payloads (guarded at 256KB).

## Regression Notes (2026-03)
These issues regressed in production-like usage and should be treated as guardrails:

1. **Remote session looked random/disconnected**
- Symptom: remote terminal would drop after a while, often around idle periods.
- Root cause: local-owner lifecycle watchdog could stop the parent process when no local browser owners existed; tab child processes then exited when the parent went away.
- Fixes:
  - `TerminalTabHostService` acquires a lifecycle owner while at least one tab child exists.
  - `TerminalSessionService` acquires a lifecycle owner while an active terminal session exists.
- Log signature:
  - `[Lifecycle] No active local browser/terminal owner for 120s. Stopping process ...`

2. **LLM terminals showed doubled/duplicated UI blocks**
- Symptom: repeated welcome cards/prompts after reconnect/takeover sequences.
- Root cause: replaying partial full-screen TUI state can conflict with redraw-based attach behavior.
- Fix:
  - local reconnect path skips replay for all current managed AI CLIs and requests redraw (`Ctrl+L`) after WebSocket consumer subscription.
- Guardrail:
  - if replay is ever reintroduced, limit it to plain shell / line-oriented sessions and re-test every AI CLI separately.
- Current experiment:
  - this branch is intentionally trying atomic snapshot attach (`SubscribeWithSnapshot` / `PushSnapshotTo`) to see whether removing snapshot/live interleaving fixes the duplication without redraw pokes.
  - treat that behavior as experimental until Claude/Codex/Antigravity/Copilot reconnect and takeover flows are re-tested.

3. **Font size / font family changes duplicated full-screen UI blocks**
- Symptom: changing terminal text size or font could leave several historical full-screen redraws visible in the browser.
- Root cause: local xterm display kept old screen content while the PTY redrew into the resized geometry.
- Fix:
  - display-metric changes reset the local xterm view first, then perform one geometry sync to the PTY.
- Guardrail:
  - re-test active Claude/Codex/Antigravity/Copilot sessions while changing font size and font family.

## If You Modify This Area
1. Update both control protocol helpers if command names or parsing rules change:
   - `VibeRails/Services/Terminal/Protocol/TerminalControlProtocol.cs`
   - `VibeRails-Front/Services/WebSockets/TerminalControlProtocol.cs`
2. Keep takeover and replay semantics consistent between local and remote paths.
3. Re-test these scenarios:
   - local reconnect
   - remote reconnect
   - local takeover from remote
   - remote takeover from local
   - resize sync in both viewers
   - remote-only session survives beyond 120s with no local browser connected
   - Claude/Codex/Antigravity/Copilot local reconnect do not show duplicated welcome/prompt blocks
    - Claude/Codex/Antigravity/Copilot font size and font family changes do not leave duplicated full-screen UI blocks

---

*Last checked: 2026-08-04T12:05:26Z by opencode (glm-5.2)*
