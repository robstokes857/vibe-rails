# VibeRails VS Code Extension - Agent Integration

This VS Code extension provides seamless integration with VibeRails, a dashboard for managing AI agents, environments, and CLI configurations.

## Features

- **Embedded Dashboard**: Opens the VibeRails dashboard directly inside VS Code as a webview panel
- **Backend Management**: Automatically starts and stops the VibeRails .NET backend server
- **Status Bar Integration**: A `$(terminal) VibeRails` button in the bottom left opens the dashboard; a `$(close)` Stop item appears beside it while the backend is running
- **Local Context**: Runs in the context of your current workspace folder for project-specific configurations

## Usage

1. Click the **"VibeRails"** button in the status bar (bottom left), or press `Ctrl+Alt+V` (`Cmd+Alt+V` on macOS)
2. The dashboard will open in a new VS Code panel
3. Manage your agents, environments, and rules directly from VS Code
4. Stop the backend by clicking the `$(close)` status bar item, running `VibeRails: Stop Dashboard`, clicking the dashboard's own **Exit** button, or closing the panel — all four stop the backend server

## Commands

- `VibeRails: Open Dashboard` (`viberails.open`) — starts the backend if needed and opens the dashboard panel
- `VibeRails: Stop Dashboard` (`viberails.stop`) — closes the panel and stops the backend

## Settings

- `viberails.startupTimeoutMs` (default `30000`) — how long to wait for the backend to start before giving up

## Architecture

The extension consists of three main components plus a shared constants module:

1. **Extension** (`extension.ts`) - Main activation logic, command registration, bootstrap/health handshake
2. **Backend Manager** (`backend-manager.ts`) - Manages the .NET backend server lifecycle
3. **Webview Panel Manager** (`webview-panel.ts`) - Handles the VS Code webview panel and content
4. **Constants** (`constants.ts`) - Command ids, token header names, backend paths, timeouts. Must not import `vscode`.

### Backend Server

- Automatically finds and starts the VibeRails backend
- Uses dynamic port allocation to avoid conflicts
- Announces itself on stdout with a single `vs-code-v1=<bootstrapUrl>` line
- Runs in the context of your workspace folder
- Shutdown ladder: `POST /api/v1/shutdown` → close stdin → `taskkill /T /F` (Windows) or `SIGTERM`/`SIGKILL`

### Authentication

- The bootstrap code in the URL is **single-use with a two-minute expiry**. Retry only `ECONNREFUSED`, which proves the request was not accepted; timeouts, resets, and HTTP responses may all occur after the code was consumed.
- The resulting session and tab tokens are instance-wide and valid for the whole backend process lifetime. They are sent as the `viberails_session` and `viberails_tab` headers; every `/api/*` route requires both.

### Security

- Content Security Policy (CSP) enforced for webview
- CORS configured for localhost and vscode-webview origins
- No inline scripts - all event handlers use proper addEventListener
- The `fonts.googleapis.com` / `fonts.gstatic.com` CSP entries are load-bearing: `assets/bootstrap.min.css` `@import`s the Lato family, which HTML `<link>` stripping does not remove

### Load-bearing manifest entries

The `shift+enter` and `escape` keybinding entries in `package.json` (including the one whose `command` is the empty string) exist to stop VS Code from swallowing those keys inside the webview terminal. **Do not remove or "clean up" any of them.**

## Development

To build and test the extension, from the repository root:

```powershell
.\Scripts\test-vscode-extension-smoke.ps1
```

This builds the .NET backend, stages the bundled binaries, compiles the TypeScript extension, and runs the Electron smoke test against a real backend. It does not package or install a `.vsix`.

For an interactive loop, open `vscode-viberails/` in VS Code and press **F5** to launch an Extension Development Host.

## Agent Management

The extension integrates with VibeRails agent system:

- **Agents**: Custom AI configurations with specific instructions and rules
- **Environments**: Isolated CLI environments (Claude, Codex, Antigravity, Copilot, OpenCode, etc.) with unique settings
- **Rules**: Per-agent behavioral rules and constraints
- **History**: Session tracking and management

All agent configurations are stored in `~/.vibe_rails/` directory.
