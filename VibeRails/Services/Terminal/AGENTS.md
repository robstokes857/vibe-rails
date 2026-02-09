# Terminal Service

Embedded terminal for running LLM CLI tools (Claude, Codex, Gemini) with full session tracking and persistence.

## Architecture

### Core Design: Unified PTY with Pub/Sub Output

```
Terminal (owns PTY, runs single read loop)
  ├── CircularBuffer (always captures last 16KB for replay)
  ├── Subscribe(ITerminalConsumer) → IDisposable token
  │   ├── ConsoleOutputConsumer  (CLI path: Console.Write)
  │   ├── DbLoggingConsumer      (both paths: TerminalStateService.LogOutput)
  │   └── WebSocketConsumer      (Web path: WebSocket.SendAsync)
  ├── WriteAsync(string) / WriteBytesAsync(ReadOnlyMemory<byte>)
  └── SendCommandAsync(string) → appends \r and writes
```

Both CLI and Web paths use the same `Terminal` class. The only difference is which consumers are subscribed and how input is sourced.

### Core Components

**`Terminal.cs`** - Unified PTY abstraction (the heart of the system)
- Owns `IPtyConnection` lifecycle (spawn, read, write, dispose)
- Single background read loop dispatches output to all subscribers via `ITerminalConsumer`
- Thread-safe subscriber management with `Lock` + snapshot-then-iterate pattern
- Built-in `CircularBuffer` (16KB) for output replay to new WebSocket connections
- Factory method `CreateAsync()` for async PTY spawn (no blocking `.Result`)
- `IAsyncDisposable` — cancels read loop, kills PTY, cleans up
- Uses `ReadOnlyMemory<byte>` for zero-copy output dispatch

**`ITerminalConsumer.cs`** - Output consumer interface
- Single method: `void OnOutput(ReadOnlyMemory<byte> data)`
- Synchronous — read loop must not await per-consumer (prevents backpressure stalling)

**`TerminalStateService.cs`** - All state management for terminal sessions
- Interface: `ITerminalStateService`
- Creates sessions, logs output, records input, completes sessions
- Uses `InputAccumulator` for keystroke buffering
- Uses `TerminalOutputFilter` to skip transient ANSI content
- No PTY or WebSocket knowledge

**`TerminalRunner.cs`** - Session orchestrator (thin layer)
- `PrepareSession()` — builds CLI command string + environment dictionary (shared by both paths)
- `CreateSessionAsync()` — creates DB session, spawns Terminal, subscribes DbLoggingConsumer, sends CLI command
- `RunCliAsync()` — CLI path: calls CreateSessionAsync, subscribes ConsoleOutputConsumer, runs Console.ReadKey input loop
- No WebSocket code, no EzTerminal, no raw PTY manipulation

**`TerminalSessionService.cs`** - Web UI session lifecycle
- Holds static `Terminal` instance (singleton pattern for single active session)
- Thread-safe using `Lock` for concurrent access
- Calls `TerminalRunner.CreateSessionAsync()` to create Terminal + session
- WebSocket handling: subscribe `WebSocketConsumer`, run input loop, unsubscribe on disconnect
- **Takeover pattern**: When new WebSocket connects, sends yellow ANSI message to old connection, then disconnects it
- Buffer replay + Ctrl+L on new connections for screen reconstruction
- Terminal persists across WebSocket disconnects — only disposed on explicit `StopSessionAsync()`

### Consumer Implementations

**`Consumers/ConsoleOutputConsumer.cs`** - CLI path
- Decodes UTF-8 from `ReadOnlyMemory<byte>`, writes to `Console.Write`

**`Consumers/DbLoggingConsumer.cs`** - Both paths
- Decodes UTF-8, calls `ITerminalStateService.LogOutput(sessionId, text)`
- Subscribed by `TerminalRunner.CreateSessionAsync()` for every session

**`Consumers/WebSocketConsumer.cs`** - Web path
- Sends raw bytes to WebSocket as binary frames
- Fire-and-forget send to avoid blocking read loop on WebSocket backpressure
- Subscribed/unsubscribed per WebSocket connection in `TerminalSessionService`

### Three Terminal Paths (Same Terminal Class, Same Tracking)

1. **CLI-Only Path** (`vb --env <cli>` without web server — standalone mode, not currently used)
   - Entry: `CliLoop.cs` → `TerminalRunner.RunCliAsync()`
   - RunCliAsync: `CreateSessionAsync()` → subscribe ConsoleOutputConsumer → `StartReadLoop()` → Console.ReadKey input loop
   - Blocks until cancelled or PTY exits
   - Full session tracking (DB, git, logging)

2. **CLI + Web Path** (`vb --env <cli>` — default behavior)
   - Entry: `Program.cs` detects `IsLMBootstrap`, starts web server, calls `CliLoop.RunTerminalWithWebAsync()`
   - Calls `TerminalRunner.RunCliWithWebAsync()` which:
     - Creates Terminal + ConsoleOutputConsumer (same as CLI-only)
     - Calls `sessionService.RegisterExternalTerminal()` to populate static state
     - Web server runs concurrently — browser can connect to `/api/v1/terminal/ws`
     - Both ConsoleOutputConsumer and WebSocketConsumer active simultaneously (pub/sub)
     - On CLI exit: `sessionService.UnregisterTerminalAsync()` sends "[CLI session ended]" to web viewer
   - Web server shuts down after terminal exits
   - Web UI "Stop" button is disabled for CLI-owned sessions (`IsExternallyOwned`)

3. **Web UI Path** (Browser/VS Code terminal)
   - Entry: `POST /api/v1/terminal/start` → `TerminalSessionService.StartSessionAsync()`
   - Calls `TerminalRunner.CreateSessionAsync()` → `terminal.StartReadLoop()`
   - WebSocket connects → subscribe WebSocketConsumer → WebSocket input loop → unsubscribe on disconnect
   - Terminal persists across WebSocket reconnects

### Session Tracking Flow

When a session starts:
1. Session created in database (`Sessions` table) via `TerminalRunner.CreateSessionAsync()`
2. `[SESSION_START]` user input recorded with git commit hash
3. `DbLoggingConsumer` subscribed — output filtered (skip ANSI-only transient content) and logged to `SessionLogs`
4. User input buffered by `InputAccumulator`, logged on Enter key
5. Git changes tracked between inputs (`InputFileChanges` table)
6. Session completed with exit code when Terminal is disposed

### API Routes

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/api/v1/terminal/status` | GET | Check if terminal session is active, returns session ID |
| `/api/v1/terminal/start` | POST | Launch LLM CLI with session tracking (`{cli, environmentName, workingDirectory}`) |
| `/api/v1/terminal/stop` | POST | Stop the active terminal session and complete session tracking |
| `/api/v1/terminal/ws` | WebSocket | Bidirectional PTY communication - supports takeover (disconnects previous viewer) |

### Terminal Start Flow (Web UI)

**Single API Call:**
```javascript
POST /api/v1/terminal/start
Body: {
  cli: "Gemini",
  environmentName: "test_g",  // optional
  workingDirectory: "..."     // optional
}
```

**Backend Flow:**
1. Route handler validates CLI type and resolves LLM enum (`Gemini`)
2. Looks up custom environment in DB to get `CustomArgs` if environmentName provided
3. Calls `TerminalSessionService.StartSessionAsync(LLM.Gemini, workDir, "test_g", ["--yolo"])`
4. TerminalSessionService calls `TerminalRunner.CreateSessionAsync()`:
   - Creates session in DB via `TerminalStateService.CreateSessionAsync()`
   - Builds environment dictionary (UTF-8 encoding + config dir isolation)
   - Spawns shell PTY via `Terminal.CreateAsync()` (pwsh.exe on Windows, bash on Linux/Mac)
   - Subscribes `DbLoggingConsumer` for output logging
   - Sends CLI command: `gemini --yolo\r`
   - Returns `(Terminal, sessionId)`
5. TerminalSessionService calls `terminal.StartReadLoop()` and stores static reference
6. Returns session ID to frontend

**Frontend Flow:**
1. Receives success response with session ID
2. Shows terminal UI and connects WebSocket to `/api/v1/terminal/ws`
3. `TerminalSessionService.HandleWebSocketAsync()`:
   - Handles WebSocket takeover if previous viewer exists
   - Replays buffered output from `terminal.GetReplayBuffer()`
   - Sends Ctrl+L to force screen redraw
   - Subscribes `WebSocketConsumer` for output → WebSocket
   - Runs WebSocket input loop: receives input → `terminal.WriteBytesAsync()` + DB logging
   - On disconnect: `subscription.Dispose()` auto-unsubscribes consumer

## External Terminal Viewers (Takeover Feature)

External HTML pages can discover and connect to active terminal sessions, taking over control from the main UI.

### Use Cases
- **Testing**: Verify terminal WebSocket functionality independently
- **Remote Access**: Future foundation for mobile/webapp relay via outbound WebSocket
- **Multi-Viewer**: Switch between different viewers (desktop, mobile, external tools)

### How Takeover Works
1. External viewer calls `GET /api/v1/terminal/status` to discover active session
2. External viewer connects to `ws://localhost:{port}/api/v1/terminal/ws`
3. `TerminalSessionService` detects existing WebSocket connection
4. Backend sends yellow ANSI message to old terminal: `[Session taken over by another viewer]`
5. Old WebSocket is closed gracefully, old `WebSocketConsumer` is unsubscribed
6. New `WebSocketConsumer` subscribed, buffer replayed, Ctrl+L sent
7. New viewer has full bidirectional control (input/output)

### Test Pages

**`VibeRails/wwwroot/test-terminal.html`** - Simple local test page
- Minimal UI for quick WebSocket testing
- Port input (defaults to 5000, validates 5000-5999 range)
- Session status check and connect buttons
- xterm.js terminal matching main app configuration

**`UITests/terminal-tests/index.html`** - Full external viewer
- Complete UI with session discovery and status display
- Configurable port input (5000-5999)
- xterm.js with FitAddon for responsive terminal sizing
- Session info panel showing active session ID
- Proper disconnect handling with reconnect capability

### Terminal Configuration (Critical for Proper Rendering)

Both external viewers use **identical xterm.js configuration** to main app:

```javascript
new Terminal({
    cols: 120,              // MUST match PTY spawn dimensions
    rows: 30,               // MUST match PTY spawn dimensions
    cursorBlink: false,     // Disable local cursor (PTY handles cursor)
    fontFamily: '"Cascadia Code", "Cascadia Mono", Consolas, "DejaVu Sans Mono", monospace',
    fontSize: 14,
    allowProposedApi: true, // Enable proposed xterm.js APIs for better rendering
    unicodeVersion: '11',   // Proper Unicode box-drawing characters
    disableStdin: false,    // Enable input
    convertEol: false,      // Don't convert EOL (raw PTY data)
    cursorStyle: 'block',
    theme: { /* VS Code dark theme colors */ }
})
```

### Connection Flow for External Viewers

1. **User enters port** (5000-5999) and clicks "Check Session"
2. **Session discovery**: `GET /api/v1/terminal/status`
   - Returns: `{ hasActiveSession: true, sessionId: "abc123" }`
3. **User clicks Connect**
4. **WebSocket connection**: `ws://localhost:{port}/api/v1/terminal/ws`
5. **Takeover occurs**: Old viewer gets yellow message, WebSocketConsumer unsubscribed
6. **Buffer replay**: Last 16KB of output replayed to new viewer
7. **Screen refresh**: Ctrl+L sent to force shell redraw
8. **Full control**: New WebSocketConsumer subscribed, bidirectional I/O active

### Future Enhancements

- **Remote relay**: Outbound WebSocket to cloud relay for mobile/webapp access (just another ITerminalConsumer)
- **Multi-session support**: Track multiple Terminal instances with session IDs
- **Broadcast mode**: Multiple WebSocketConsumers subscribed simultaneously (read-only or collaborative)
- **Resize support**: `terminal.Resize()` propagates to PTY

## Environment Isolation

Environment variables configured via `TerminalRunner.PrepareSession()` + `LlmCliEnvironmentService.GetEnvironmentVariables()`:
- UTF-8 encoding always set (`LANG`, `LC_ALL`, `PYTHONIOENCODING`)
- Looks up custom environment by name from database
- Resolves `~/.config/{cli}/{env}` config directory path
- Sets `XDG_CONFIG_HOME` (Linux/Mac) or `APPDATA` (Windows) to isolated config dir
- Both CLI and Web paths use same `PrepareSession()` method

## Database Schema

- **Sessions** - Session metadata (id, cli, env name, working dir, start/end times, exit code)
- **SessionLogs** - Output logs (session id, timestamp, content, is_error)
- **UserInputs** - User input records (session id, sequence, input text, git commit hash, timestamp)
- **InputFileChanges** - Git diffs between inputs (user input id, file path, change type, lines added/deleted, diff content)
- **ClaudePlans** - Claude Code plan tracking (session id, user input id, plan content, status)

## Files

| File | Purpose | Key Implementation Details |
|------|---------|---------------------------|
| `Services/Terminal/Terminal.cs` | Unified PTY abstraction | Factory spawn, single read loop, pub/sub consumers, CircularBuffer, IAsyncDisposable, ReadOnlyMemory<byte> |
| `Services/Terminal/ITerminalConsumer.cs` | Output consumer interface | `void OnOutput(ReadOnlyMemory<byte>)` — synchronous, zero-copy |
| `Services/Terminal/Consumers/ConsoleOutputConsumer.cs` | Console output (CLI) | UTF-8 decode → Console.Write |
| `Services/Terminal/Consumers/DbLoggingConsumer.cs` | DB logging (both paths) | UTF-8 decode → TerminalStateService.LogOutput |
| `Services/Terminal/Consumers/WebSocketConsumer.cs` | WebSocket output (Web) | Fire-and-forget binary send |
| `Services/Terminal/KeyTranslator.cs` | ANSI key translation (CLI) | Console.ReadKey → escape sequences (arrows, F-keys, etc.) |
| `Services/Terminal/CircularBuffer.cs` | Output replay buffer | Thread-safe circular byte buffer, ReadOnlySpan<byte> append |
| `Services/Terminal/TerminalStateService.cs` | DB session tracking | Creates/completes sessions, logs output via filter, manages InputAccumulator |
| `Services/Terminal/TerminalRunner.cs` | Session orchestrator | PrepareSession, CreateSessionAsync (shared), RunCliAsync (CLI) |
| `Services/Terminal/TerminalSessionService.cs` | Web UI session lifecycle | Static Terminal storage, WebSocket takeover, subscribe/unsubscribe, buffer replay |
| `wwwroot/js/modules/terminal-controller.js` | xterm.js frontend | Binary WebSocket, session restoration, reconnect button |
| `CliLoop.cs` | CLI entry point | `RunTerminalSessionAsync()` resolves LLM/env, calls runner.RunCliAsync |
| `Routes/TerminalRoutes.cs` | API endpoints | /start, /stop, /status, /ws, /bootstrap-command |
| `Utils/InputAccumulator.cs` | Keystroke buffering | Buffers until Enter, flushes to DB with git diff tracking |
| `Utils/TerminalOutputFilter.cs` | ANSI filtering | Skips transient escape sequences to reduce DB noise |
| `Services/LlmClis/LlmCliEnvironmentService.cs` | Env var resolution | Config dir isolation (XDG_CONFIG_HOME / APPDATA) |
