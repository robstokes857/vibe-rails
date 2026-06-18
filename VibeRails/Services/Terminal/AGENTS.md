# Terminal Service

Current implementation reference for `VibeRails/Services/Terminal`.
Verified against source on 2026-03-05.

## Scope
This folder owns PTY lifecycle, session tracking hooks, local WebSocket viewer handling, and remote relay integration.

Primary files (reorg'd into subdirectories):
- `Services/Terminal/Pty/Terminal.cs`
- `Services/Terminal/Core/TerminalRunner.cs`
- `Services/Terminal/Core/TerminalSessionService.cs`
- `Services/Terminal/Core/TerminalStateService.cs`
- `Services/Terminal/Core/TerminalTabHostService.cs`
- `Services/Terminal/Protocol/TerminalIoRouter.cs`
- `Services/Terminal/Protocol/TerminalControlProtocol.cs`
- `Services/Terminal/Remote/RemoteTerminalConnection.cs`
- `Services/Terminal/Consumers/*.cs`
- `Services/Terminal/Observers/*.cs`
- `Services/Terminal/Interfaces/*.cs` (interface contracts, namespace stays flat)

Related routes/UI:
- `Routes/TerminalRoutes.cs`
- `wwwroot/js/modules/terminal-multitab.js`

Remote relay server (other repo):
- `C:\source\VibeRailsFrontEnd\VibeRails-Front\VibeRails-Front`

## Core Architecture

```
Terminal (PTY owner, single read loop)
  - CircularBuffer (16KB replay)
  - Subscribe(ITerminalConsumer)
      - ConsoleOutputConsumer
      - DbLoggingConsumer
      - WebSocketConsumer (local viewer)
      - RemoteOutputConsumer (relay path)
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

Validation:
- max inbound message size: `256 * 1024` bytes
- resize range: cols `10..1000`, rows `5..500`
- disconnect reasons are sanitized and truncated to 120 chars before sending

## Component Responsibilities

### `Terminal.cs`
- Spawns PTY (`pwsh.exe` on Windows, `/bin/zsh` on macOS, `bash` on Linux).
- Stores last 16KB output in `CircularBuffer`.
- Dispatches every PTY read chunk to current consumer snapshot.
- Exposes `GetReplayBuffer()` and `Resize(cols, rows)`.
- `CreateAsync(..., title)` sets PTY name and sends terminal title ANSI sequence when provided.
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

### `TerminalRunner.cs`
Session orchestrator.

`PrepareSession(...)`
- Builds launch command and optional extra args.
- Adds the VibeRails MCP stdio registration setup command for managed CLIs
  (Claude, Codex, Antigravity, Copilot).
- Builds base env vars (`LANG`, `LC_ALL`, `PYTHONIOENCODING`).
- Merges CLI-specific env vars via `LlmCliEnvironmentService` when environment name is provided.

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
  - `OnResizeRequested`

### `RemoteStateService.cs`
HTTP registration with relay server:
- `POST /api/v1/terminal` on session create
- `DELETE /api/v1/terminal` on session complete

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
- `GET /api/v1/terminal/bootstrap-command`
- `WS /api/v1/terminal/ws`

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
3. Replay buffer is byte-based (16KB), not line-aware.
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
