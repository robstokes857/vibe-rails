# vscode-viberails

A VS Code extension that embeds the VibeRails dashboard directly inside Visual Studio Code as a webview panel. Launch and control VibeRails — an AI agent management dashboard — without leaving your editor.

## What It Does

- **Embedded Dashboard**: Opens the VibeRails dashboard in a VS Code webview panel (not a separate browser window)
- **Automatic Backend Management**: Finds and launches the VibeRails .NET backend server with dynamic port allocation
- **Status Bar Button**: Quick access via the `$(circuit-board) VibeRails` button in the bottom status bar
- **Runtime Install Flow**: Uses `~/.vibe_rails` and installs VibeRails from GitHub releases when missing
- **Workspace-Aware**: Runs the backend in the context of your current workspace folder for local environment isolation

## Architecture

Three main source files in `src/`:

| File | Purpose |
|---|---|
| `extension.ts` | Activation, command registration, status bar, lifecycle |
| `backend-manager.ts` | Spawns/manages the .NET backend, port detection, health checks, graceful shutdown |
| `webview-panel.ts` | Creates the webview panel, loads UI from `wwwroot/`, rewrites asset paths, injects CSP |

## Local Development

Open this folder in VS Code and hit **F5** to launch an Extension Development Host with the extension loaded. Changes can be tested by restarting the debug session.

## Packaging

```bash
npm install
npx vsce package
```

This produces a `.vsix` file you can install via `Extensions: Install from VSIX...` in VS Code or `code --install-extension <file>.vsix` from the command line.

## Backend Installer Commands

VibeRails app install commands (for docs/site):

```powershell
# Windows
irm https://raw.githubusercontent.com/robstokes857/vibe-rails/main/Scripts/install.ps1 | iex
```

```bash
# Linux
wget -qO- https://raw.githubusercontent.com/robstokes857/vibe-rails/main/Scripts/install.sh | bash

# macOS
curl -fsSL https://raw.githubusercontent.com/robstokes857/vibe-rails/main/Scripts/install.sh | bash
```

## Versioning and Release

Use semantic versioning:

- `patch`: bug fixes, no breaking changes
- `minor`: new backward-compatible features
- `major`: breaking changes

Unified release flow (recommended):

```bash
# Run from repository root
pwsh ./Scripts/deploy.ps1
```

The root release script requires:
- extension version (for example `1.4.0`)
- `VS_PAT` environment variable (Visual Studio Marketplace PAT)

Then it always:
1. syncs backend + extension version
2. commits and tags `vX.Y.Z`
3. triggers GitHub Actions release build for all platforms
4. publishes the VS Code extension to Marketplace

Extension-only release flow (legacy/manual):

```bash
npm run release
```
