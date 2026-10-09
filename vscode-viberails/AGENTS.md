# VibeRails VS Code Extension - Agent Integration

This VS Code extension provides seamless integration with VibeRails, a dashboard for managing AI agents, environments, and CLI configurations.

## Features

- **Embedded Dashboard**: Opens the VibeRails dashboard directly inside VS Code as a webview panel
- **Backend Management**: Automatically starts and stops the VibeRails .NET backend server
- **Status Bar Integration**: A `$(terminal) VibeRails` button in the bottom left opens the dashboard; a `$(close)` Stop item appears beside it while the backend is running
- **Activity Bar launcher**: A play icon in the Activity Bar opens the **Launch** view, the side-bar twin of the dashboard's nav Play button. It lists the project's automations and signed scripts in the nav launcher's saved order; clicking a row runs it, and the view title offers Refresh, Customize list… and Manage automations. While the dashboard is closed the view shows an Open Dashboard button instead (the backend only runs with the panel open)
- **Local Context**: Runs in the context of your current workspace folder for project-specific configurations

## Usage

1. Click the **"VibeRails"** button in the status bar (bottom left), or press `Ctrl+Alt+V` (`Cmd+Alt+V` on macOS)
2. The dashboard will open in a new VS Code panel
3. Manage your agents, environments, and rules directly from VS Code
4. Stop the backend by clicking the `$(close)` status bar item, running `VibeRails: Stop Dashboard`, clicking the dashboard's own **Exit** button, or closing the panel — all four stop the backend server

## Commands

- `VibeRails: Open Dashboard` (`viberails.open`) — starts the backend if needed and opens the dashboard panel
- `VibeRails: Stop Dashboard` (`viberails.stop`) — closes the panel and stops the backend
- `Add to VibeRails Scripts…` (`viberails.addScript`) — Explorer and editor-tab context menu
  for local `.ps1`, `.sh` and `.py` files; the command palette uses the active editor.
  Opens the existing registration form with that path. A cold launch uses the selected file's
  workspace folder (or its containing folder outside a workspace). An already open dashboard
  stays in its current project; external files offer Global scope and explain how to use Repo.
  Existing visible registrations open by ID. Adding never signs or runs a script.
- `VibeRails: Run now` (`viberails.launcher.run`) — tree-row click and inline play icon in the
  Launch view; hidden from the palette because it needs the row as its argument. Automations are
  queued straight from the extension host (`POST /api/v1/jobs/{id}/run`, the same call as the
  dashboard's "Run now"; a status-bar message confirms "Automation queued." and the dashboard, when
  open, shows its usual toast). Scripts are handed to the dashboard (`runScript` bridge message)
  because their run window collects arguments, stdin and a PIN; the panel is revealed for that.
- `VibeRails: Refresh Launcher` (`viberails.launcher.refresh`), `VibeRails: Customize Launcher List…`
  (`viberails.launcher.customize`) and `VibeRails: Manage Automations` (`viberails.launcher.manage`) —
  Launch view title actions. Customize and Manage open the dashboard when needed and hand it the
  flyout footer's actions (`openLauncherCustomize`, `manageAutomations`).

## Settings

- `viberails.startupTimeoutMs` (default `30000`) — how long to wait for the backend to start before giving up

## Architecture

The extension consists of these components plus a shared constants module:

1. **Extension** (`extension.ts`) - Main activation logic, command registration, bootstrap/health handshake, the Launch view's wiring (`registerLauncherView`)
2. **Backend Manager** (`backend-manager.ts`) - Manages the .NET backend server lifecycle
3. **Webview Panel Manager** (`webview-panel.ts`) - Handles the VS Code webview panel and content; `postWhenReady` holds host → dashboard messages until the page's bridge is installed
4. **Launcher view** (`launcher-view.ts`) - `LauncherTreeDataProvider` plus the pure catalog rules (`normalizeLauncherItems`, `isLauncherItemRunnable`, `launcherEmptyMessage`: ports of `wwwroot/js/modules/automation-launcher.js`, keep them in step) and `runLauncherItem`, all driven through injected dependencies so they are unit-tested without a backend
5. **Backend API** (`backend-api.ts`) - `requestJson` for authenticated host → backend calls (session + tab headers, `ErrorResponse { error }` surfaced as `BackendRequestError`). Must not import `vscode`. The shutdown ladder keeps its own never-throwing `postShutdown`
6. **Constants** (`constants.ts`) - Command ids, token header names, backend paths, timeouts. Must not import `vscode`.

The Launch view only ever lists items while the dashboard panel exists: the backend starts in `createDashboard` and stops when the panel closes, and the `viberails.dashboardOpen` context key (set in `createDashboard` / `closeDashboard`) switches the view between its welcome content and the tree. The tree re-reads the catalog when the dashboard opens, when the view becomes visible, on Refresh, after a 404 on run, and whenever the dashboard posts `launcherChanged`.

### Webview bridge

The dashboard and the extension talk through injected globals, not DOM structure:
`__viberails_VSCODE__`, `__viberails_close__`, `__viberails_setTitle__`, and
`__viberails_openFile__` (opens a path in an editor tab beside the panel — the Python
scripts section uses it instead of its in-app Monaco editor), and `__viberails_openExternal__`
(opens the account sign-in page in the user's browser). The dashboard
feature-detects each one, so an older extension host degrades instead of breaking.

Script import uses `__viberails_scriptImportReady__` and
`__viberails_scriptImportReceived__` for the dashboard-to-extension handshake. The extension
sends `{ command: 'importScript', requestId, path }` only after the dashboard installs its
handler. Receipt is acknowledged before user input, so the startup timeout never times out
an open form. Concurrent requests preserve the first dialog; closing rejects pending delivery.
The dashboard calls the existing root-only authenticated import API through its shared controller.

The Launch view reuses that readiness signal: `WebviewPanelManager.postWhenReady` queues
`{ command: 'runScript', name }`, `{ command: 'openLauncherCustomize' }`,
`{ command: 'manageAutomations' }` and `{ command: 'automationQueued', jobId, message }` until the
page has posted `scriptImportReady`, which is why `app.js` installs `setupVSCodeLauncherBridge`
(`wwwroot/js/modules/vscode-launcher-bridge.js`) before `setupVSCodeScriptImport`. The dashboard
answers with `__viberails_launcherChanged__` (posts `{ command: 'launcherChanged' }`) after it saved
or reset the launcher list, reloaded its Automation catalog, or saw a script's id, display name,
file name or approval change (the bridge follows `PythonScriptsController.onStateChange`), and the
view re-reads the catalog.

A tree row's command is delivered once for a click and again for a double-click, up to the OS
double-click time later. `runLauncherItem` keeps the row in its guard set for
`LAUNCHER_CLICK_COOLDOWN_MS` (1 s) after the run settles, not only while it is in flight, so a
quick run cannot be queued twice; the backend rejects the overlapping run otherwise.

`external-sign-in.ts` accepts only `https://viberails.ai/link`, optionally followed by the strict
public user-code fragment `#code=ABCD-EFGH`, before calling
`vscode.env.openExternal`. Reject other schemes, hosts, paths, explicit ports, credentials,
queries and any other fragments. The dashboard stays in its webview; account authentication runs in
the external browser. The website clears the fragment before login and automatically POSTs the
public code with its antiforgery token. Users still approve the request; API keys and device secrets
remain in the backend and never enter bridge messages. No URI callback handler is needed.

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

Session replay needs `frame-src 'self'` to load VS Code's empty same-origin frame shell
before writing its template. A `srcdoc` frame cannot resolve local resources through
VS Code's service worker. The replay frame explicitly retains the parent CSP; keep the
real-webview regression in `src/test/suite/session-replay-webview.test.ts` when changing it.

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

Rule files (`vc.rules.md`) live in the **project (git repository)**, not under `~/.vibe_rails/`. Environments, session history (`state.db`), and sandboxes are stored under `~/.vibe_rails/`.

---

*Last checked: 2026-08-06T17:54:34Z by opencode (glm-5.2)*
