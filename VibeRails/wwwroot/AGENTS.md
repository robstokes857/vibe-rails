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

## Reusable local File Explorer

Call `await app.pickFileSystemEntry({ mode, initialPath?, title?, includeHidden?, triggerElement? })`
from any view. `mode` is `file`, `directory`, or `any`. A selection resolves to
`{ canceled: false, path, kind, name }`; every dismissal resolves (rather than rejects) to
`{ canceled: true, path: null, kind: null, name: null }`. The component is a nested modal layer,
so it can safely open over an existing `app.showModal` form.

The authenticated root backend serves one metadata-only level at
`GET /api/v1/filesystem/entries`. Cursor paging and debounced server search keep every item in a
large directory reachable. Network/device paths and navigation through links/reparse points are
rejected; linked rows are shown for context but cannot be opened or selected.

## Token Saver Integration

The token-savings meter sits at the far right of the terminal controls bar and is owned by
`terminal-token-compression.js`. The per-LLM on/off switches live in Settings (Claude, Codex,
OpenCode); the per-tab compression toggle that used to sit on each tab was removed 2026-07-19.
The meter displays accumulated savings and a `Paused m:ss` badge while any tab's compression is
paused via the `pause_token_saver` / `resume_token_saver` MCP tools.

## Terminal Environment Integration

The terminal dropdown shows two groups:
- **Base CLIs**: Claude, Codex, GLM 5.2, GLM 5.3, OpenCode, Copilot, Antigravity (each shown as "(default)") — resolved to its executable server-side (Antigravity → `agy`)
- **Custom Environments**: User-created environments — spawned directly via the tab start endpoint

## Environment Steps editor

`environment-steps.js` is the editor for an Environment's ordered shell commands — the ones that
run in their own native terminal window before the CLI launches or after its PTY exits.

`environment-controller.js` is one ~2,150-line class and `showEnvironmentForm` already composes
CLI settings, workspace mode, and args, so steps are **not** inlined into it. The form gets a
`Steps (2 before · 1 after)` summary button; the editor lives in its own module and opens over the
form.

**It opens as a nested modal layer, not a second `app.showModal`.** `app.showModal` rebuilds
`#modal-container`'s `innerHTML` wholesale (`app.js`), so a second call would destroy the
environment form underneath. `openStepsEditor` follows `llm-picker-controller.js`'s
`openCustomizationModal`: append an own `.llm-picker-modal-layer`, set `inert` + `aria-hidden` on
the existing `#modal-container` children, trap focus, restore all of it on close. Reordering
copies the same hand-rolled HTML5 DnD — drag handle only, `is-dragging` / `is-drag-target`, drop
side from `clientY > rect.top + height/2` — plus its ArrowUp/ArrowDown handling and explicit move
buttons. (The vendored `sortable.min.js` in `index.html` is still unused by any first-party
module. Keep it that way.)

Things that will otherwise bite:

- **`null` vs `[]` on the wire.** `editedSteps` stays `null` until the editor is opened *and*
  saved, and the PUT omits `steps` entirely in that case. `null` means "leave them untouched" —
  sending `[]` from a form whose steps modal was never opened would wipe a configured setup chain.
- **No `window.confirm`.** Step deletion uses `confirmDialog` from `utils.js`; a sweep test over
  every first-party JS file enforces this.
- Any capture-phase Escape listener starts with `if (isConfirmDialogOpen()) return;` — asserted as
  a literal string by the jobs-controller tests.
- Text fields write straight into state with no re-render, so the caret survives typing. Only
  structural changes (add / delete / move) re-render, and a re-render aborts any in-flight test
  stream because it replaces the row elements.
- Test output reuses `VcaConsole` (`vca-console.js`) — `begin()` / `writeLine()` /
  `finishStream()`, tone via `data-tone`. It is `textContent`-only, which is what arbitrary
  command output needs. The stream is read with `createSseParser` from `git-guard-preflight.js`
  over an authenticated POST, with an `AbortController` for cancel.
- CSS is prefixed `env-step-*` and every colour is written `var(--token, #fallback)`: an undefined
  custom property invalidates the whole declaration, which is the documented cause of the
  transparent-background bug.

A failed **pre-launch** step aborts the launch, and the reason arrives separately as an
`environment_step_failed` AppEvent handled in `terminal-multitab.js` beside the `session_*`
handlers — the step's own window shows the error, but nothing in it explains why the tab never
started.

## Customizable LLM Pickers

`LlmPickerController` loads the resolved machine-wide catalog from
`/api/v1/llm-picker/preferences` before the initial view renders. It mounts the native selects,
owns their Tom Select instances, tracks mounted pickers, and refreshes them after a preference
save or reset while preserving current selections and search text. Each mount returns a disposer;
view/modal owners must call it when their select is removed.

The controller applies one of four contexts after the global ordering is resolved:

| Context | Items shown |
|---|---|
| `terminal` | Base CLIs, custom Environments, and plain Terminal |
| `sandbox` | Base CLIs and custom Environments |
| `multi-run` | Base CLIs only, excluding plain Terminal |
| `environment-provider` | Supported providers, excluding Terminal; ignores visibility preferences |

Consumers import the facades in `js/modules/pickers/` instead of touching the controller:
`llm-picker.js` (`mountLlmPicker` / `setLlmPickerValue` / `getEnabledLlmItems`) for the contexts
above, and `worker-picker.js` (`mountWorkerPicker`) for the Automation editor. The Worker picker
is NOT a controller context: it lists environments flagged `automationWorker` straight from
`app.data.environments`, ignores `hidden` entirely (a Worker can never be hidden there), and has
no customization footer. Workers, in turn, never appear in any launch context — the server
excludes them from the preferences catalog.

Environments are scoped to a project. The list endpoint filters them, so `app.data.environments`
already contains only what this project may see — with one deliberate exception: an environment
created before scoping has a null `projectPath` and appears everywhere.

Each environment also carries `workspaceMode` (0 project dir / 1 own clone / 2 fresh clone each
run) plus `workspaceSandboxId` / `workspacePath` / `workspaceBranch`, exported as `WORKSPACE_MODE`
from `environment-controller.js`. A clone-mode environment shows a `fa-code-branch` badge that
stacks alongside the worker/hidden badges — whether something is a Worker and where it runs are
independent facts. When `workspaceSandboxId` is set the row also grows Diff / Merge / Push buttons
emitting the same `data-action="sandbox-*"` handlers the Sandboxes card uses
(`bindSandboxGitActions` binds both). The Sandboxes card renders only sandboxes with no
`environmentId`, so releasing a workspace moves it back there with no extra plumbing.

The three launch contexts add a persistent **Customize LLM list** footer to their dropdowns. Its
nested modal changes visibility and within-group order globally. A disabled selection that is
already referenced is reinserted with a `(hidden)` label so editing another field cannot silently
clear it. The Environment provider picker deliberately has no customization footer, and Chat
History remains unfiltered so launch preferences never hide historical sessions.

### Flow: Launching a Custom Environment

1. User selects environment from dropdown (or clicks "Web UI" button on environments page)
2. `startFromSelection()` creates a tab via `POST /api/v1/terminal/tabs`
3. `tab.instance.startSession(body)` calls `POST /api/v1/terminal/tabs/{tabId}/start` with `{ cli, environmentName, workingDirectory, title }`. The client **always** sends the project root here — if the environment has a workspace mode, the server swaps in the clone directory (`TerminalTabHostService.ApplyWorkspaceAsync`). Do not try to resolve workspaces client-side; a failed clone comes back as a 400 with the reason.
4. Frontend opens a WebSocket to `/api/v1/terminal/tabs/{tabId}/ws`
5. Backend spawns the LLM CLI directly in a PTY inside the tab's session (with isolated env vars for the chosen environment)

### Flow: Launching a Base CLI

1. User selects e.g. "Claude (default)" from dropdown (selection value `base:claude`)
2. Same tab-based flow as above: `POST /api/v1/terminal/tabs` then `POST /api/v1/terminal/tabs/{tabId}/start` with `{ cli: "claude", workingDirectory, title }` (no `environmentName`)
3. WebSocket connects to `/api/v1/terminal/tabs/{tabId}/ws`

Both base CLI and custom environment launches share one unified tab API; the only difference is whether `environmentName` is included in the start body.

Automation's manual **Run now** also lands in this terminal surface, but the backend creates that
tab because it must spawn a `JobRunner` child rather than an ordinary blank terminal child. The
response carries `tabId`; `jobs-controller.js` stores the Worker's selection + Automation label in
the normal per-tab session metadata, then navigates to `terminal-focus` with `preferredTabId`.
Scheduled/commit/retry runs remain native background launches.

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

*Last checked: 2026-08-11T00:00:00Z by claude (opus-5)*
