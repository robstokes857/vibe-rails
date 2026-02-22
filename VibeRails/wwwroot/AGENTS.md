# Web UI Frontend

Vanilla JavaScript SPA using Bootstrap 5 and xterm.js. No build step required.

**Terminology:** "Web UI Chat" refers to the xterm.js-based terminal, NOT a separate chat UI.

## Architecture

| File | Purpose |
|------|---------|
| [app.js](app.js) | Central controller, routing, API layer |
| [js/modules/terminal-multitab.js](js/modules/terminal-multitab.js) | Reusable xterm.js terminal manager with per-tab lifecycle and environment picker |
| [js/modules/environment-controller.js](js/modules/environment-controller.js) | Environment CRUD + "Web UI" launch button |
| [js/modules/sandbox-controller.js](js/modules/sandbox-controller.js) | Sandbox CRUD + launch terminals/VS Code into sandbox dirs |
| [js/modules/dashboard-controller.js](js/modules/dashboard-controller.js) | Dashboard layout with state passing for preselection |

## Terminal Environment Integration

The terminal dropdown shows two groups:
- **Base CLIs**: Claude (default), Codex (default), Gemini (default) — sends bare CLI name to PTY
- **Custom Environments**: User-created environments — launches via LMBootstrap

### Flow: Launching a Custom Environment

1. User selects environment from dropdown (or clicks "Web UI" button on environments page)
2. `startTerminal()` calls `GET /api/v1/terminal/bootstrap-command?cli={type}&environmentName={name}`
3. Backend returns the full `vb --env "{name}" --workdir "{dir}"` command
4. Frontend sends command to PTY shell via WebSocket
5. LMBootstrap runs inside PTY, sets up env vars, launches CLI

### Flow: Launching a Base CLI

1. User selects e.g. "Claude (default)" from dropdown
2. `startTerminal()` detects `base:claude` prefix
3. Sends `claude\r` directly to PTY — no backend call needed

### Flow: "Web UI" Button

1. User clicks "Web UI" on environments page
2. `launchInWebUI(envId, envName)` calls `app.navigate('dashboard', { preselectedEnvId })`
3. Dashboard passes `preselectedEnvId` to `terminalController.bindTerminalActions()`
4. `populateTerminalSelector()` pre-selects the environment in the dropdown
5. Terminal section scrolls into view

## Sandbox Management

The sandbox section appears on the dashboard when running in a local git project context (`isLocal`).

### Flow: Creating a Sandbox

1. User clicks "+ New Sandbox" button on dashboard
2. `sandboxController.createSandbox()` shows modal with name input
3. On submit, POSTs to `/api/v1/sandboxes` with `{ name }`
4. Backend clones repo, copies dirty files, saves to DB
5. Dashboard refreshes sandbox list

### Flow: Launching Terminal in Sandbox

1. User clicks a CLI button (Claude/Codex/Gemini) on a sandbox card
2. `sandboxController.launchInWebUI(sandboxId, sandboxName, cli)` calls `terminalController.startTerminalWithOptions()`
3. `startTerminalWithOptions()` POSTs to `/api/v1/terminal/start` with `{ cli, workingDirectory: sandboxPath, title: "Sandbox: {name}" }`
4. Terminal starts in sandbox directory with title bar showing sandbox name

### Flow: Launch VS Code in Sandbox

1. User clicks VS Code button on a sandbox card
2. POSTs to `/api/v1/sandboxes/{id}/launch/vscode`
3. Backend calls `Process.Start("code", ".")` with `WorkingDirectory = sandbox.Path`

### Key Design Decisions

- **Backend builds the command** — frontend doesn't know about exe paths or working directories
- **optgroups** separate base CLIs from custom environments visually
- **Value format**: `base:cli` vs `env:id:cli` enables easy parsing in `startTerminal()`
- **Navigation data** passed as object through `app.navigate()` (same pattern as agent-edit)

### API Endpoint

```
GET /api/v1/terminal/bootstrap-command?cli={type}&environmentName={name}
```

Returns `{ command: "& \"C:\\path\\to\\vb\" --env \"MyEnv\" --workdir \"C:\\project\"" }`.

The command is platform-aware:
- **Windows**: Prefixed with `&` for PowerShell invocation
- **Linux/Mac**: Standard shell format

See also: [Cli/AGENTS.md](../Cli/AGENTS.md) for how `--env` works.
See also: [Services/Terminal/AGENTS.md](../Services/Terminal/AGENTS.md) for backend terminal service.
