# Terminal Service

Embedded terminal for running LLM CLI tools (Claude, Codex, Gemini) in the browser and VS Code webview.

## Architecture

### Backend (`TerminalSessionService.cs`)

Minimal PTY + WebSocket bridge:

- **Pty.Net** - Spawns pseudo-terminal (pwsh.exe on Windows, bash on Linux/Mac)
- **WebSocket** - Bidirectional communication between browser and PTY
- **Static state** - Single terminal session per backend instance
- **No dependencies** - No database, no logging, no session tracking

Key implementation details:
- Uses Windows ConPTY (PseudoConsole) for modern terminal support
- Forces UTF-8 encoding via PowerShell command on startup
- Inherits full environment from parent process
- Single WebSocket client at a time (rejects additional connections)

### Frontend (`terminal-controller.js`)

xterm.js terminal emulator:

- **xterm.js** - Terminal UI with Unicode 11 support
- **Cascadia Code font** - Proper rendering of box-drawing characters
- **WebSocket client** - Connects to backend PTY
- **CLI dropdown** - Select base CLIs or custom environments via optgroups
- **Bootstrap command** - Custom environments call backend to get the full `vb --env` command

### API Routes

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/api/v1/terminal/status` | GET | Check if terminal session is active |
| `/api/v1/terminal/start` | POST | Start a new PTY session |
| `/api/v1/terminal/stop` | POST | Stop the active PTY session |
| `/api/v1/terminal/ws` | WebSocket | Bidirectional PTY communication |
| `/api/v1/terminal/bootstrap-command` | GET | Get the `vb --env` command for launching a CLI with environment config |

### Bootstrap Command Endpoint

```
GET /api/v1/terminal/bootstrap-command?cli={type}&environmentName={name}
```

Returns the full command to send to the PTY shell. The frontend sends this command
to the already-running PTY via WebSocket. Inside the PTY, `vb --env` starts LMBootstrap,
which sets up isolated config env vars and launches the CLI.

See [Cli/AGENTS.md](../../Cli/AGENTS.md) for how `--env` / LMBootstrap works.

## What We Did

1. **Simplified from ~300 lines to ~160 lines** - Removed all dependencies (IDbService, IGitService, IClaudeAgentSyncService)
2. **Fixed Unicode rendering** - UTF-8 encoding on both backend (PowerShell) and frontend (TextDecoder)
3. **Added CLI selector** - Dropdown to pick which LLM CLI to launch
4. **Auto-launch CLI** - Selected CLI runs automatically after terminal connects
5. **Proper fonts** - Cascadia Code/Mono for box-drawing characters
6. **Custom environment support** - Dropdown shows optgroups for base CLIs and custom environments
7. **Bootstrap command endpoint** - Backend builds the `vb --env` command so frontend doesn't need to know about exe paths or working directories
8. **"Web UI" button** - Environments page has a button that navigates to dashboard with environment pre-selected

## What Was Removed (Intentionally)

- Session tracking and database logging
- Input accumulator for keystroke tracking
- Claude agent sync (CLAUDE.md/AGENTS.md)
- Git commit hash tracking

Note: Environment config isolation is handled by LMBootstrap (runs inside the PTY), not by the terminal service itself.

## Future Enhancements

### Session Tracking (Optional)
- Create sessions in database when terminal starts
- Track which CLI was used
- Store start/end times

### Input/Output Logging (Optional)
- Frontend can intercept user input (Enter key triggers)
- Frontend can capture LLM output (debounced after 500ms silence)
- Send to backend API for storage
- Custom events available: `terminal-user-input`, `terminal-llm-output`

### Multiple Sessions
- Support multiple concurrent PTY sessions
- Session tabs in UI
- Session isolation

### Terminal Resize
- Handle window resize events
- Send resize to PTY via `IPtyConnection.Resize(cols, rows)`

## Files

| File | Purpose |
|------|---------|
| `Services/Terminal/TerminalSessionService.cs` | PTY + WebSocket backend |
| `wwwroot/js/modules/terminal-controller.js` | xterm.js frontend |
| `Routes.cs` | API endpoint definitions |
| `DTOs/ResponseRecords.cs` | Request/response types |
