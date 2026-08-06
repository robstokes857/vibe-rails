# Web UI Frontend

Vanilla JavaScript SPA using Bootstrap 5 and xterm.js. No build step required.

**Terminology:** "Web UI Chat" refers to the xterm.js-based terminal, NOT a separate chat UI.

## Architecture

| File | Purpose |
|------|---------|
| [app.js](app.js) | Central controller, routing, API layer |
| [js/modules/terminal-multitab.js](js/modules/terminal-multitab.js) | Reusable xterm.js terminal manager with per-tab lifecycle and environment picker |
| [js/modules/llm-picker-controller.js](js/modules/llm-picker-controller.js) | Shared launch-picker catalog, Tom Select lifecycle, customization modal, and live preference refresh |
| [js/modules/terminal-token-compression.js](js/modules/terminal-token-compression.js) | Persistent token-savings meter and per-tab pause-badge display (the per-tab on/off toggle was removed 2026-07-19; the saver is now per-LLM in Settings) |
| [js/modules/terminal-snapshot-renderer.js](js/modules/terminal-snapshot-renderer.js) | Renders reserved `xterm_ui_bytes` payloads into xterm.js and captures PNG data URLs for MCP Explorer previews |
| [js/modules/environment-controller.js](js/modules/environment-controller.js) | Environment CRUD + "Web UI" launch button |
| [js/modules/sandbox-controller.js](js/modules/sandbox-controller.js) | Sandbox CRUD + launch terminals/VS Code into sandbox dirs |
| [js/modules/dashboard-controller.js](js/modules/dashboard-controller.js) | Dashboard layout with state passing for preselection |
| [js/modules/code-analyzer-dashboard.js](js/modules/code-analyzer-dashboard.js) | Interactive MintLint scan dashboard with Monaco code evidence for the Rules page |

## Token Saver Integration

The token-savings meter sits at the far right of the terminal controls bar and is owned by
`terminal-token-compression.js`. The per-LLM on/off switches live in Settings (Claude, Codex,
OpenCode); the per-tab compression toggle that used to sit on each tab was removed 2026-07-19.
The meter displays accumulated savings and a `Paused m:ss` badge while any tab's compression is
paused via the `pause_token_saver` / `resume_token_saver` MCP tools.

## Terminal Environment Integration

The terminal dropdown shows two groups:
- **Base CLIs**: Claude, Codex, GLM 5.2, OpenCode, Copilot, Antigravity (each shown as "(default)") — resolved to its executable server-side (Antigravity → `agy`)
- **Custom Environments**: User-created environments — spawned directly via the tab start endpoint

## Customizable LLM Pickers

`LlmPickerController` loads the resolved machine-wide catalog from
`/api/v1/llm-picker/preferences` before the initial view renders. It mounts the native selects,
owns their Tom Select instances, tracks mounted pickers, and refreshes them after a preference
save or reset while preserving current selections and search text. Each mount returns a disposer;
view/modal owners must call it when their select is removed.

The controller applies one of five contexts after the global ordering is resolved:

| Context | Items shown |
|---|---|
| `terminal` | Base CLIs, custom Environments, and plain Terminal |
| `sandbox` | Base CLIs and custom Environments |
| `automation` | Custom Environments only |
| `multi-run` | Base CLIs only, excluding plain Terminal |
| `environment-provider` | Supported providers, excluding Terminal; ignores visibility preferences |

The four launch contexts add a persistent **Customize LLM list** footer to their dropdowns. Its
nested modal changes visibility and within-group order globally. A disabled selection that is
already referenced is reinserted with a `(hidden)` label so editing another field cannot silently
clear it. The Environment provider picker deliberately has no customization footer, and Chat
History remains unfiltered so launch preferences never hide historical sessions.

### Flow: Launching a Custom Environment

1. User selects environment from dropdown (or clicks "Web UI" button on environments page)
2. `startFromSelection()` creates a tab via `POST /api/v1/terminal/tabs`
3. `tab.instance.startSession(body)` calls `POST /api/v1/terminal/tabs/{tabId}/start` with `{ cli, environmentName, workingDirectory, title }`
4. Frontend opens a WebSocket to `/api/v1/terminal/tabs/{tabId}/ws`
5. Backend spawns the LLM CLI directly in a PTY inside the tab's session (with isolated env vars for the chosen environment)

### Flow: Launching a Base CLI

1. User selects e.g. "Claude (default)" from dropdown (selection value `base:claude`)
2. Same tab-based flow as above: `POST /api/v1/terminal/tabs` then `POST /api/v1/terminal/tabs/{tabId}/start` with `{ cli: "claude", workingDirectory, title }` (no `environmentName`)
3. WebSocket connects to `/api/v1/terminal/tabs/{tabId}/ws`

Both base CLI and custom environment launches share one unified tab API; the only difference is whether `environmentName` is included in the start body.

### Flow: "Web UI" Button

1. User clicks "Web UI" on environments page
2. `launchInWebUI(envId, envName, cli)` navigates to dashboard (if not already there) with `{ preselectedEnvId }`
3. Dashboard passes `preselectedEnvId` to `terminalController.bindTerminalActions(container, preselectedEnvId)`
4. `bindTerminalActions` pre-selects the environment in the dropdown
5. Terminal section scrolls into view
6. `startTerminal(terminalContent, 'env:${envId}:${cli}')` auto-starts the terminal session

## Sandbox Management

The sandbox section appears on the dashboard when running in a local git project context (`isLocal`).

### Flow: Creating a Sandbox

1. User clicks "+ New Sandbox" button on dashboard
2. `sandboxController.createSandbox()` shows modal with name input
3. On submit, POSTs to `/api/v1/sandboxes` with `{ name }`
4. Backend clones repo, copies dirty files, saves to DB
5. Dashboard refreshes sandbox list

### Flow: Launching Terminal in Sandbox

1. User selects a CLI/environment from the dropdown on a sandbox card, then clicks the Web Terminal launch button
2. `sandboxController.launchInWebUI(sandboxId, sandboxName, cli, environmentName)` calls `terminalController.startTerminalWithOptions()`
3. `startTerminalWithOptions()` creates a tab (`POST /api/v1/terminal/tabs`) and starts the session (`POST /api/v1/terminal/tabs/{tabId}/start`) with `{ cli, environmentName, workingDirectory: sandboxPath, title: "Sandbox: {name}" }`
4. Terminal starts in sandbox directory with title bar showing sandbox name

### Flow: Launch VS Code in Sandbox

1. User clicks VS Code button on a sandbox card
2. POSTs to `/api/v1/sandboxes/{id}/launch/vscode`
3. Backend calls `Process.Start("code", ".")` with `WorkingDirectory = sandbox.Path`

### Key Design Decisions

- **Backend spawns the CLI directly** — the frontend sends the CLI type + optional environment to the tab start endpoint; the backend spawns the LLM CLI in a PTY (no command string is sent to a shell by the frontend)
- **optgroups** separate base CLIs from custom environments visually
- **Value format**: `base:cli` vs `env:id:cli` enables easy parsing in `startTerminal()`
- **Navigation data** passed as object through `app.navigate()` (same pattern as agent-edit)

### API Endpoints

The terminal UI is tab-based. Each tab is a blank container until a session is started in it:

```
POST   /api/v1/terminal/tabs                  # Create a blank tab
GET    /api/v1/terminal/tabs                   # List tabs (and max tab count)
DELETE /api/v1/terminal/tabs/{tabId}           # Close a tab
GET    /api/v1/terminal/tabs/{tabId}/status    # Tab + session status
POST   /api/v1/terminal/tabs/{tabId}/start     # Start a CLI session in the tab
POST   /api/v1/terminal/tabs/{tabId}/stop      # Stop the session in a tab
WS     /api/v1/terminal/tabs/{tabId}/ws        # Bidirectional PTY byte stream
```

The `start` body: `{ cli, environmentName?, workingDirectory?, title?, initialPrompt?, resumeSessionId?, resumeSummary?, makeRemote? }`.
The WebSocket URL accepts `?cols=&rows=` so the backend can resize the PTY before
replaying the session buffer (avoids the stale-geometry "double print" bug).

See also: [Services/Terminal/AGENTS.md](../Services/Terminal/AGENTS.md) for backend terminal service.

---

*Last checked: 2026-08-06T17:22:10Z by opencode (glm-5.2)*
