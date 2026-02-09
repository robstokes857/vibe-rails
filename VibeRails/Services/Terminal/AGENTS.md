# Terminal Service

Embedded terminal for running LLM CLI tools (Claude, Codex, Gemini) with full session tracking and persistence.

## Architecture

### Core Components

**`TerminalStateService.cs`** - All state management for terminal sessions
- Interface: `ITerminalStateService`
- Creates sessions, logs output, records input, completes sessions
- Uses `InputAccumulator` for keystroke buffering
- Uses `TerminalOutputFilter` to skip transient ANSI content
- No PTY or WebSocket knowledge
- Room for future state management beyond DB

**`TerminalRunner.cs`** - PTY lifecycle management
- `RunCliAsync()` - CLI path: uses EzTerminal, blocks until exit, pipes Console I/O
- `StartWebAsync()` - Web UI path: spawns PTY via PtyProvider, sends CLI command, returns IPtyConnection + session ID
- `HandleWebSocketAsync()` - Web UI path: bidirectional WebSocket ↔ PTY piping with two async tasks
  - Output task: reads from `pty.ReaderStream`, logs to DB, sends to WebSocket
  - Input task: receives from WebSocket, logs to DB, writes to `pty.WriterStream`
- Configures environment variables via `LlmCliEnvironmentService`
- Both paths use same state service for tracking
- Web UI uses PtyProvider directly (NOT EzTerminalStream) for maximum control

**`TerminalSessionService.cs`** - Web UI service (minimal glue)
- Holds static `IPtyConnection` and session ID (singleton pattern for single active session)
- Thread-safe using lock for concurrent access
- Calls `TerminalRunner.StartWebAsync()` to spawn PTY and get connection
- Calls `TerminalRunner.HandleWebSocketAsync()` to pipe WebSocket ↔ PTY bidirectionally
- **Takeover pattern**: When new WebSocket connects, sends yellow ANSI message to old connection, then disconnects it
- PTY persists across WebSocket disconnects - only disposed on explicit `StopSessionAsync()` call
- Session survives viewer changes, enabling external terminal viewers to take control

### Two Terminal Paths (Identical Session Tracking)

1. **CLI Path** (`vb --env <cli>`)
   - Entry: `CliLoop.cs` → `RunTerminalSessionAsync()`
   - Creates `TerminalStateService` + `TerminalRunner`
   - Calls `runner.RunCliAsync()` - uses `EzTerminal`, blocks until exit
   - Full session tracking (DB, git, logging)

2. **Web UI Path** (Browser/VS Code terminal)
   - Entry: `POST /api/v1/terminal/start` → `TerminalSessionService.StartSessionAsync()`
   - Creates `TerminalStateService` + `TerminalRunner`
   - Calls `runner.StartWebAsync()` - spawns PTY directly via PtyProvider, returns IPtyConnection + session ID
   - WebSocket connects, calls `runner.HandleWebSocketAsync()` - spawns two tasks for bidirectional piping
   - **Same tracking as CLI path** ✅

### Bidirectional WebSocket Piping (Web UI Path)

**Critical Implementation Pattern:**

TerminalRunner.HandleWebSocketAsync() spawns TWO independent async tasks that run concurrently:

1. **Output Task (PTY → WebSocket → Browser)**
   - Reads from `pty.ReaderStream` in 4KB chunks
   - Decodes UTF-8 and logs to DB via `_stateService.LogOutput()`
   - Sends raw bytes to WebSocket as Binary messages
   - Continues until PTY exits (bytesRead == 0) or cancellation

2. **Input Task (Browser → WebSocket → PTY)**
   - Receives from WebSocket in 4KB chunks
   - Decodes UTF-8 and logs to DB via `_stateService.RecordInput()`
   - Writes raw bytes to `pty.WriterStream` and flushes immediately
   - Continues until WebSocket closes or cancellation

**Why Two Tasks:**
- PTY output and user input are independent streams that must be handled simultaneously
- Using `Task.WhenAny()` allows either task to terminate the session (PTY exit or WebSocket close)
- Each task has independent error handling (catches OperationCanceledException, WebSocketException)

**Cleanup:**
- When either task completes, WebSocket is closed gracefully
- PTY connection is **NOT** disposed in HandleWebSocketAsync - only closed on explicit StopSessionAsync()
- This allows session persistence across WebSocket disconnects (viewer takeover support)
- Session is marked complete in DB with exit code only when PTY is explicitly stopped

### Session Tracking Flow

When a session starts:
1. Session created in database (`Sessions` table)
2. `[SESSION_START]` user input recorded with git commit hash
3. Output filtered (skip ANSI-only transient content) and logged to `SessionLogs`
4. User input buffered by `InputAccumulator`, logged on Enter key
5. Git changes tracked between inputs (`InputFileChanges` table)
6. Session completed with exit code when PTY exits

### API Routes

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/api/v1/terminal/status` | GET | Check if terminal session is active, returns session ID |
| `/api/v1/terminal/start` | POST | Launch LLM CLI with session tracking (`{cli, environmentName, workingDirectory}`) |
| `/api/v1/terminal/stop` | POST | Stop the active PTY session and complete session tracking |
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
4. TerminalSessionService calls `TerminalRunner.StartWebAsync()`:
   - Creates session in DB via `TerminalStateService.CreateSessionAsync()`
   - Configures environment variables (config dir isolation + UTF-8 encoding)
   - Spawns shell PTY via `PtyProvider.SpawnAsync()` (pwsh.exe on Windows, bash on Linux/Mac)
   - Sends CLI command as text: `gemini --yolo\r`
   - Returns `(IPtyConnection pty, string sessionId)`
5. TerminalSessionService stores static `s_pty` and `s_sessionId`
6. Returns session ID to frontend

**Frontend Flow:**
1. Receives success response with session ID
2. Shows terminal UI and connects WebSocket to `/api/v1/terminal/ws`
3. TerminalSessionService.HandleWebSocketAsync() is called:
   - Retrieves static `s_pty` and `s_sessionId`
   - Calls `TerminalRunner.HandleWebSocketAsync()` which spawns two tasks:

     **Output Task (PTY → WebSocket):**
     ```csharp
     while (!ct.IsCancellationRequested && webSocket.State == WebSocketState.Open)
     {
         var bytesRead = await pty.ReaderStream.ReadAsync(buffer, ct);
         if (bytesRead == 0) break;

         // Log to database
         var text = System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead);
         _stateService.LogOutput(sessionId, text);

         // Send to WebSocket
         await webSocket.SendAsync(new ArraySegment<byte>(buffer, 0, bytesRead),
                                   WebSocketMessageType.Binary, true, ct);
     }
     ```

     **Input Task (WebSocket → PTY):**
     ```csharp
     while (!ct.IsCancellationRequested && webSocket.State == WebSocketState.Open)
     {
         var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
         if (result.MessageType == WebSocketMessageType.Close) break;
         if (result.Count > 0)
         {
             // Log input to database
             var input = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
             _stateService.RecordInput(sessionId, input);

             // Write to PTY
             await pty.WriterStream.WriteAsync(buffer, 0, result.Count, ct);
             await pty.WriterStream.FlushAsync(ct);
         }
     }
     ```
4. xterm.js terminal receives binary output and displays to user
5. User keystrokes sent to WebSocket → PTY → running CLI
6. Full bidirectional byte stream + session tracking active

## Frontend (`terminal-controller.js`)

xterm.js terminal emulator with full PTY communication:

- **xterm.js** - Terminal UI with Unicode 11 support and VS Code theme
- **Cascadia Code font** - Proper rendering of box-drawing characters and Unicode glyphs
- **WebSocket client** - Binary message handling for PTY communication
  - `socket.onmessage` - Receives binary data, decodes UTF-8, writes to xterm.js
  - `terminal.onData` - Captures user input, sends to WebSocket
  - Null safety: checks `if (this.terminal)` before writing on disconnect
- **CLI dropdown** - Select base CLIs (`base:claude`) or custom environments (`env:123:gemini`) via optgroups
- **Single API call flow** - `POST /api/v1/terminal/start` launches CLI directly, then WebSocket connects
- **Session restoration** - On page load, checks `/api/v1/terminal/status` and reconnects if active session exists

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
5. Old WebSocket is closed gracefully, new WebSocket takes over
6. PTY session persists - only the viewer changes
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
    fontFamily: '"Cascadia Code", "Cascadia Mono", Consolas, monospace',
    fontSize: 14,
    allowProposedApi: true, // Enable proposed xterm.js APIs for better rendering
    unicodeVersion: '11',   // Proper Unicode box-drawing characters
    disableStdin: false,    // Enable input
    convertEol: false,      // Don't convert EOL (raw PTY data)
    cursorStyle: 'block',
    theme: { /* VS Code dark theme colors */ }
})
```

**Why This Matters:**
- **cols/rows mismatch** causes broken box-drawing characters and UI corruption (especially Gemini CLI)
- **unicodeVersion: '11'** required for proper rendering of `▀▄╭╮│` and other Unicode glyphs
- **allowProposedApi: true** enables advanced xterm.js features for better rendering
- **convertEol: false** prevents xterm from manipulating raw PTY data
- **cursorBlink: false** prevents phantom cursor at bottom (PTY controls cursor via ANSI codes)

### Connection Flow for External Viewers

1. **User enters port** (5000-5999) and clicks "Check Session"
2. **Session discovery**: `GET /api/v1/terminal/status`
   - Returns: `{ hasActiveSession: true, sessionId: "abc123" }`
3. **User clicks Connect**
4. **WebSocket connection**: `ws://localhost:{port}/api/v1/terminal/ws`
5. **Takeover occurs**: Old viewer gets yellow message and disconnects
6. **Screen refresh**: External viewer sends Ctrl+L (`\x0C`) to trigger shell redraw
   - Without this, terminal appears blank (joining mid-session means no buffer replay)
   - Ctrl+L forces shell to redraw its current state (prompt, UI elements, etc.)
7. **Full control**: External viewer now has bidirectional terminal I/O

### CORS Configuration

External viewers (file:// protocol) require permissive CORS policy:

```csharp
builder.Services.AddCors(options =>
{
    options.AddPolicy("VSCodeWebview", policy =>
    {
        policy.AllowAnyOrigin()     // Allows null origin (file://)
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});
```

**Why AllowAnyOrigin:** file:// protocol sends `Origin: null`, which requires either `AllowAnyOrigin()` or explicit null origin handling. `AllowAnyOrigin()` is simpler and works for development/testing scenarios.

### Limitations and Future Enhancements

**Current Limitations:**
- No output buffer/history replay - joining mid-session shows blank screen until new output
- Single active session model - only one PTY session can run at a time
- Takeover is destructive - old viewer is disconnected, not maintained in parallel

**Future Enhancements:**
- **Output buffering**: Store last N bytes of PTY output, replay to new connections
- **Remote relay**: Outbound WebSocket to cloud relay for mobile/webapp access
- **Multi-session support**: Track multiple PTY sessions with session IDs
- **Broadcast mode**: Multiple viewers watch same session (read-only or collaborative input)

## Environment Isolation

Environment variables are configured via `LlmCliEnvironmentService.GetEnvironmentVariables()`:
- Looks up environment by name from database
- Resolves `~/.config/{cli}/{env}` config directory path
- Sets `XDG_CONFIG_HOME` (Linux/Mac) or `APPDATA` (Windows) to isolated config dir
- Returns full environment dictionary to merge with process env
- Both CLI and Web UI paths apply same isolation in their respective PTY spawning code

**UTF-8 Encoding (Web UI only):**
Web UI path explicitly sets UTF-8 encoding for proper Unicode rendering:
```csharp
environment["LANG"] = "en_US.UTF-8";
environment["LC_ALL"] = "en_US.UTF-8";
environment["PYTHONIOENCODING"] = "utf-8";
```

**Both paths use the same environment isolation mechanism - conda-like model with config directory environment variables, NOT CLI flags.**

## Database Schema

- **Sessions** - Session metadata (id, cli, env name, working dir, start/end times, exit code)
- **SessionLogs** - Output logs (session id, timestamp, content, is_error)
- **UserInputs** - User input records (session id, sequence, input text, git commit hash, timestamp)
- **InputFileChanges** - Git diffs between inputs (user input id, file path, change type, lines added/deleted, diff content)
- **ClaudePlans** - Claude Code plan tracking (session id, user input id, plan content, status)

## Files

| File | Purpose | Key Implementation Details |
|------|---------|---------------------------|
| `PtyNet/src/Pty.Net/EzTerminal.cs` | PTY wrapper for Console I/O (CLI path) | Handles Console.ReadKey, translates to ANSI escape codes, pipes to PTY stdin/stdout, blocks until exit |
| `Services/Terminal/TerminalStateService.cs` | All state management for terminal sessions | Creates/completes sessions in DB, logs output via TerminalOutputFilter, manages InputAccumulator per session, integrates git tracking |
| `Services/Terminal/TerminalRunner.cs` | PTY lifecycle + I/O piping for both paths | CLI: uses EzTerminal blocking model, Web: uses PtyProvider direct with 2 async tasks for bidirectional piping |
| `Services/Terminal/TerminalSessionService.cs` | Web UI glue service (minimal wrapper) | Singleton pattern with static IPtyConnection, thread-safe locking, prevents multiple WebSocket connections |
| `wwwroot/js/modules/terminal-controller.js` | xterm.js frontend with WebSocket client | Binary message handling (Blob → ArrayBuffer → UTF-8), null-safe disconnect, session restoration on page load |
| `CliLoop.cs` | CLI path entry point | `RunTerminalSessionAsync()` creates TerminalStateService + TerminalRunner, resolves LLM enum or DB environment |
| `Routes/TerminalRoutes.cs` | API endpoint definitions | /start (POST), /stop (POST), /status (GET), /ws (WebSocket), /bootstrap-command (GET for external terminals) |
| `DTOs/ResponseRecords.cs` | Request/response types | StartTerminalRequest, TerminalStatusResponse, BootstrapCommandResponse |
| `Utils/InputAccumulator.cs` | Keystroke buffering and Enter detection | Buffers input until \r or \n detected, then flushes to DB with git diff tracking via callback |
| `Utils/TerminalOutputFilter.cs` | ANSI-only content filtering | Skips transient ANSI escape sequences (cursor positioning, clearing, etc.) to reduce DB log noise |
| `Services/LlmClis/LlmCliEnvironmentService.cs` | Environment variable resolution | Resolves `~/.config/{cli}/{env}` path, sets XDG_CONFIG_HOME (Linux/Mac) or APPDATA (Windows) for config isolation |
