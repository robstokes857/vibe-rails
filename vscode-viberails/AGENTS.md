# VibeRails VS Code Extension - Agent Integration

This VS Code extension provides seamless integration with VibeRails, a dashboard for managing AI agents, environments, and CLI configurations.

## Features

- **Embedded Dashboard**: Opens the VibeRails dashboard directly inside VS Code as a webview panel
- **Backend Management**: Automatically starts and stops the VibeRails .NET backend server
- **Status Bar Integration**: Quick access button in the bottom left corner with `$(circuit-board) VibeRails`
- **Local Context**: Runs in the context of your current workspace folder for project-specific configurations

## Usage

1. Click the **"VibeRails"** button in the status bar (bottom left)
2. The dashboard will open in a new VS Code panel
3. Manage your agents, environments, and rules directly from VS Code
4. Close the panel or click "Exit" to stop the backend server

## Commands

- `VibeRails: Open Dashboard` - Opens the VibeRails dashboard

## Configuration

- `viberails.executablePath` - Path to the VibeRails executable (optional, auto-detected by default)

## Architecture

The extension consists of three main components:

1. **Extension** (`extension.ts`) - Main activation logic and command registration
2. **Backend Manager** (`backend-manager.ts`) - Manages the .NET backend server lifecycle
3. **Webview Panel Manager** (`webview-panel.ts`) - Handles the VS Code webview panel and content

### Backend Server

- Automatically finds and starts the VibeRails backend
- Uses dynamic port allocation to avoid conflicts
- Runs in the context of your workspace folder
- Stops cleanly when the panel is closed

### Security

- Content Security Policy (CSP) enforced for webview
- CORS configured for localhost and vscode-webview origins
- No inline scripts - all event handlers use proper addEventListener

## Development

To build and test the extension:

```powershell
.\Scripts\test_vscode_extension.ps1
```

This will:
1. Build the .NET backend
2. Compile the TypeScript extension
3. Package as .vsix
4. Install in VS Code

Then reload VS Code window (Ctrl+Shift+P → "Developer: Reload Window")

## Agent Management

The extension integrates with VibeRails agent system:

- **Agents**: Custom AI configurations with specific instructions and rules
- **Environments**: Isolated CLI environments (Claude, Aider, etc.) with unique settings
- **Rules**: Per-agent behavioral rules and constraints
- **History**: Session tracking and management

All agent configurations are stored in `~/.viberails/` directory.
