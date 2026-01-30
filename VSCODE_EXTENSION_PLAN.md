# Plan: VS Code Extension for viberails

## Goal
Create a VS Code extension that launches the viberails .NET backend and displays the existing SPA frontend inside a VS Code WebviewPanel.

## Approach: Load Static Files Directly into Webview

The extension reads `wwwroot/` files from disk and serves them via `webview.asWebviewUri()`. API calls are routed to `http://localhost:{port}` where the backend is running.

**Why not iframe?** VS Code webviews sandbox iframes heavily, and `localhost` doesn't resolve correctly in Remote SSH/WSL/Codespaces contexts. Loading files directly is the standard approach used by major VS Code extensions.

---

## Extension Location

New directory: `vscode-viberails/`

```
vscode-viberails/
  .vscode/
    launch.json           # F5 debug config
  src/
    extension.ts          # Entry point (activate/deactivate)
    backend-manager.ts    # Spawn .NET process, detect port, health check, shutdown
    webview-panel.ts      # Build webview HTML, manage panel lifecycle
  package.json            # Extension manifest
  tsconfig.json
```

---

## Implementation Steps

### 1. Scaffold the extension
- Create `vscode-viberails/` with `package.json`, `tsconfig.json`, `.vscode/launch.json`
- Register a single command: `viberails.open` ("viberails: Open Dashboard")
- Add a config setting `viberails.executablePath` for the backend executable path

### 2. Backend process management (`backend-manager.ts`)
- **Spawn**: `cp.spawn(exePath, [], { cwd: workspaceFolder, stdio: ['pipe', 'pipe', 'pipe'] })`
- **Port detection**: Parse stdout for `viberails server running on http://localhost:{port}` (already printed at `Program.cs:101`)
- **Health check**: Poll `GET /api/v1/IsLocal` until 200 response
- **Shutdown**: Write `\n` to stdin + close stdin (unblocks `CliLoop.RunAsync` → triggers `app.StopAsync()` at `Program.cs:112`). Fall back to SIGTERM after 3s.
- **Executable resolution**: Check config setting → bundled path → dev build paths

### 3. Webview panel (`webview-panel.ts`)
- Create `WebviewPanel` with `enableScripts: true`, `retainContextWhenHidden: true`
- Set `localResourceRoots` to the `wwwroot/` directory
- **Build HTML by**:
  - Reading `index.html` from disk
  - Extracting all `<template>` elements (regex)
  - Rewriting CSS/JS/image paths using `webview.asWebviewUri()`
  - Injecting `window.__viberails_API_BASE__ = 'http://localhost:{port}'` before app.js loads
- **CSP policy**:
  - `script-src 'nonce-{n}' ${webview.cspSource}` — allows inline + module scripts
  - `connect-src http://localhost:{port}` — allows fetch to backend
  - `style-src ${webview.cspSource} 'unsafe-inline'` — Bootstrap uses inline styles
  - `img-src ${webview.cspSource} data:`

### 4. Extension entry point (`extension.ts`)
- `activate`: Register `viberails.open` command
- On command: start backend (if not running) → create webview panel
- If panel already exists, just reveal it
- `deactivate`: Dispose panel, stop backend

### 5. One change to existing frontend (`wwwroot/app.js`)
Modify `apiCall()` at `app.js:446`:
```javascript
// Before:
const response = await fetch(endpoint, options);

// After:
const baseUrl = window.__viberails_API_BASE__ || '';
const response = await fetch(baseUrl + endpoint, options);
```
This is backward-compatible — in a browser, `__viberails_API_BASE__` is undefined, so `baseUrl` is `''` and behavior is unchanged.

### 6. Optional: Add CORS to backend (`Program.cs`)
If webview fetch calls hit CORS errors, add:
```csharp
builder.Services.AddCors();
// ...after Build()...
app.UseCors(policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
```

---

## Critical Files

| File | Role |
|------|------|
| `viberails/Program.cs` | stdout format for port detection (line 101), shutdown flow (line 112) |
| `viberails/wwwroot/app.js` | `apiCall()` method needs base URL prefix (line 446) |
| `viberails/wwwroot/index.html` | Source of all `<template>` elements the webview HTML builder extracts |

---

## Verification

1. **F5 debug**: Launch Extension Development Host, run "viberails: Open Dashboard" command
2. **Backend starts**: Check Output Channel ("viberails Backend") shows server startup logs
3. **Dashboard renders**: Webview shows the viberails SPA with correct styling
4. **API calls work**: Dashboard loads project history, agent list, environments
5. **Shutdown**: Close VS Code or deactivate extension → backend process exits cleanly
6. **Browser still works**: Open `http://localhost:{port}` in browser alongside the extension — both work
