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
| [js/modules/dashboard-controller.js](js/modules/dashboard-controller.js) | Unified Project health page (Rules, VCA, Git Guard, and Code quality; no embedded terminal) |
| [js/modules/code-analyzer-dashboard.js](js/modules/code-analyzer-dashboard.js) | Compact MintLint score card plus the modal file/metric/source report |
| [js/modules/jobs-controller.js](js/modules/jobs-controller.js) | Automation page: automation CRUD + inline editor, run history, "Run now" (queues a native terminal run; `launchFromNav` for the nav launcher); owns the shared `PythonScriptsController` |
| [js/modules/python-scripts-controller.js](js/modules/python-scripts-controller.js) | "Python scripts" section of the Automation page + shared lifecycle flows; also owns the PIN-gated MCP switch/configurator and typed parameter-to-argv mapping fields |
| [js/modules/python-script-workbench.js](js/modules/python-script-workbench.js) | `python-script` view: Monaco editor beside a docked agent terminal for one script (see "Python script workbench" below) |
| [js/modules/python-run-window.js](js/modules/python-run-window.js) | The little run window: typed inputs + free arguments + stdin in, exit code / output / return value out, no terminal (see "Python script run window" below) |
| [js/modules/automation-launcher.js](js/modules/automation-launcher.js) | Nav "Launch" flyout (automations + Python scripts, unsigned ones disabled) and its order/show-hide customize modal over `/api/v1/automation-nav/preferences` |

## Reusable local File Explorer

Call `await app.pickFileSystemEntry({ mode, initialPath?, title?, includeHidden?, filters?, triggerElement? })`
from any view. `mode` is `file`, `directory`, or `any`. A selection resolves to
`{ canceled: false, path, kind, name }`; every dismissal resolves (rather than rejects) to
`{ canceled: true, path: null, kind: null, name: null }`. The component is a nested modal layer,
so it can safely open over an existing `app.showModal` form.

`filters` is optional and only honoured in `file` / `any` mode:
`[{ label: 'Python files', extensions: ['py'] }, { label: 'All files', extensions: [] }]`.
Extensions are matched case-insensitively without dots; an empty list means all files; folders are
never filtered. The first entry is the default and renders as the "Files of type" `<select>` next
to the file-name box, labelled with its pattern ("Python files (*.py)", "All files (*.*)"). Omit
the option and every file is listed, as before. Filtering is client-side over the loaded page(s),
so the footer count reads "12 of 340 items (Python files)" while a filter hides rows.

The dialog is laid out like a desktop Open / Select Folder dialog (`file-explorer.js`, styles in
the "Server-backed File Explorer" block of `style.css`): title bar; toolbar with Back / Forward /
Up, a breadcrumb address bar (click the empty part, Ctrl+L, or F4 to type a path; Enter goes,
Escape/blur revert), Refresh, and a search box; a places sidebar ("Quick access": Project, then
the server-provided Home / Desktop / Documents / Downloads that exist; "This PC" / "Drives": the
roots) that collapses to a chip strip under 860px; a details list with sortable Name / Date
modified / Type / Size headers (folders always first, type-ahead, Enter opens, Alt+Up up,
Backspace back — or up while there is no history — Alt+Left/Right history); a "File name:" row
("Folder:" in directory mode) whose Enter opens the typed name or navigates an absolute path (a
name missing from a partially loaded or searched folder is searched for server-side before it is
declared missing); and a footer with the status, "Show hidden items", and Open / Select Folder +
Cancel. In directory mode the primary button picks the highlighted folder, else the folder being
viewed (a highlighted muted file counts as nothing). Only Escape, Cancel, and the X dismiss;
clicking the backdrop does nothing. Without `initialPath` the dialog reopens at the folder the
last picker of that mode was accepted from (localStorage `viberails.fileExplorer.lastPath:<mode>`)
and falls back silently to the project root if that folder no longer loads. Nested-layer rules
apply: it appends its own layer to `#modal-container`, marks everything else inert, traps Tab, and
stands down for `confirmDialog()`.

The authenticated root backend serves one metadata-only level at
`GET /api/v1/filesystem/entries`; the payload's `places` array (label, path,
kind ∈ home|desktop|documents|downloads) feeds the sidebar and lists only existing local
directories that pass the same eligibility rules as roots. Cursor paging and debounced server
search keep every item in a large directory reachable. Network/device paths and navigation
through links/reparse points are rejected; linked rows are shown for context but cannot be opened
or selected.

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

Automations never land in this terminal surface. Every Automation run — manual **Run now**, retry,
schedule, commit trigger — is launched by the backend scheduler into its own native OS terminal
window, so `runNow` just POSTs, toasts and refreshes the run history. Python scripts are the
exception that does use tabs (see the interactive-script flow).

### Flow: "Web UI" Button

1. User clicks "Web UI" on environments page
2. `launchInWebUI(envId, envName, cli)` calls `terminalController.launchInFocus(...)`
3. `launchInFocus` navigates to `terminal-focus` carrying one-shot `launchOptions`
4. `loadTerminalFocusView` mounts and binds the terminal manager, consumes those options, and calls `startTerminalWithOptions`
5. A fresh tab starts the selected environment. Project health remains terminal-free.

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
2. `sandboxController.launchInWebUI(sandboxId, sandboxName, cli, environmentName)` calls `terminalController.launchInFocus()`
3. The focused terminal consumes the launch options, creates a tab (`POST /api/v1/terminal/tabs`), and starts the session (`POST /api/v1/terminal/tabs/{tabId}/start`) with `{ cli, environmentName, workingDirectory: sandboxPath, title: "Sandbox: {name}" }`
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

## Python script workbench

- **View** `python-script` (data `{ name }`), module `js/modules/python-script-workbench.js`
  (`PythonScriptWorkbench`, constructed in `app.js`; Automation stays the highlighted nav
  entry and a duplicated tab lands on `jobs`). Opened from the Automation page's Python
  scripts section: the row's **Edit** button (outline-primary, first in the row) and the
  script name navigate here in every host; "Open in VS Code" is a secondary menu item
  when the extension bridge exists.
- **Layout**: Back bar (`data-action="go-back"`, bound globally) + identity/status pill +
  Run / Sign / kebab; a `.rules-section` card with a script rail and Monaco
  (`viberails-dark`, Ctrl/⌘+S saves in place); an optional last-run drawer; a draggable
  splitter (`role="separator"`, Arrow keys ±24px); and the agent terminal
  (`renderTerminalPanel({ workingDirectory })` + `bindTerminalActions(host, null,
  { defaultWorkingDirectory: scriptsDirectory })`, so sessions start in the **scripts
  directory**, not the project root). **Side by side is the layout**: from 880px up
  (`isSideBySideLayout()` = the CSS `@media (min-width: 880px)`) the terminal is a grid
  column BESIDE the editor, full working height, and the (vertical) splitter sets its
  width — `--python-workbench-terminal-width`, persisted in localStorage
  `viberails.pythonWorkbench.terminalWidth`, ArrowLeft/Right. The panes claim most of the
  viewport as a minimum height so a short window scrolls the page instead of squeezing
  either pane. 880px is the floor at which both columns clear their minimums
  (`EDITOR_MIN_WIDTH` 380 + 12 + `TERMINAL_MIN_WIDTH` 320) and is deliberately low so a
  docked VS Code webview still gets columns; **only below it do the panes stack**
  (horizontal splitter, `--python-workbench-terminal-height`, localStorage
  `viberails.pythonWorkbench.terminalHeight`, ArrowUp/Down; the floor drops to 180px on
  viewports ≤ 720px tall). The script rail collapses to a chip strip under 1100px, because
  side by side the editor column cannot spare 180px for it. The shell class
  `vb-rules-workspace-active` is applied to this view too.
- **Shared flows**: signing (PIN prompt), revoke, rename, delete, duplicate, copy path,
  run and `saveContent` are public methods on `PythonScriptsController`
  (`app.jobController.pythonScripts`) that work unmounted; the workbench follows list
  updates through `onStateChange`. There are two run paths, and both refuse an unsigned or
  unsaved script:
  - **Run** (primary everywhere) → `PythonScriptsController.run(name)` → the run window
    below. Captured, no terminal.
  - **Run in terminal…** (kebab menu, and the run window's footer) →
    `runInTerminal(name, button)` → `/api/v1/python-scripts/run/interactive`: the backend
    verifies the signature, creates a shell tab and invokes the verified-byte helper inside
    its PTY, so stdin, prompts, live output and Ctrl+C stay interactive. The tab is adopted
    in place when a terminal panel is already on screen, otherwise `terminal-focus` opens.

## Python script run window

- **Module** `js/modules/python-run-window.js` (`PythonRunWindow`); one instance lives on
  `PythonScriptsController.runWindow`, so the Automation row, the workbench and the nav
  Launch flyout all drive the same surface. Styles: the "The run window" block in
  `style.css` (`.vb-run-*`). It is the small-space answer to the interactive tab — inputs,
  output and return value in one modal, nothing spawned.
- **Inputs**. A script exposed to MCP has already declared its parameters (name, type,
  required, default, positional-or-`--flag`); those render as typed fields with a locked
  shape chip. Any script can also take free **argument** rows (optional flag + value) and
  a **Standard input** box. Everything typed is remembered per script in localStorage
  `viberails.pythonRun.<name>`, so re-running is one click. A script that declares nothing
  and remembers nothing **runs the moment the window opens**.
- **The command line** under the inputs is the payload, not a picture of it: `resolveArgv()`
  builds one array, the window prints it and posts it, so a preview cannot drift from what
  runs. It mirrors `PythonScriptMcpService.BuildArguments` (positional values in order,
  then named options; a false boolean option is the absence of its flag), so a script
  behaves the same whether a human or an agent calls it.
- **Output**. `POST /api/v1/python-scripts/run` with `{ name, arguments, standardInput }`
  returns exit code, duration, stdout/stderr and `returnJson` — the JSON object or array
  the script printed as the whole of stdout or on its last line
  (`PythonScriptService.ExtractReturnJson`; a bare scalar is output, not a return value).
  The window shows **Returned** (pretty-printed, copyable) only when there is one, then
  **Output**. Argv and stdin are bounded server-side (64 args, 8k chars each, 256k stdin).
  The result also lands in the row's last-run drawer through `recordRun`.
- **Keys**: Escape closes, Ctrl/⌘+Enter runs from anywhere including the stdin box.
- **MCP exposure**: each script row has an MCP switch. Enabling/editing opens a PIN-gated dialog
  for tool name, usage description, and zero or more typed parameters (required/default plus
  positional or named-option argv mapping). Enabled tools render under a separate **Python script
  tools** heading in the local MCP Explorer; disabling needs no PIN. Backend validation and the
  signed hash remain authoritative.
- **Ask agent**: with a live session (open socket) the brief naming the absolute script
  path is pasted with `injectText` **without submitting** (`…\n\nChange: `); otherwise
  `startTerminalWithOptions` starts the panel's picked CLI (default `claude`) in the
  scripts directory with a read-and-wait `initialPrompt` (auto-submitted, so it never
  carries the half-finished sentence), `taskKey: 'python-script:<name>'` reuses the tab.
- **Live reload**: while mounted and visible, `GET /api/v1/python-scripts/content` is
  polled every ~4s (and on focus / visibilitychange); a new `version` swaps the text
  preserving cursor + scroll when the editor is clean, or raises an inline banner
  (Reload / Keep my edits) when dirty. Stale saves (400 from the server) show the same
  banner; a file deleted on disk offers "Re-create from my edits". Never polls while a
  save is in flight; everything stops in `unload()`.
- **Guards**: an app navigation guard (retry-replay via `confirmDialog`) and
  `beforeunload` protect unsaved edits. `app.js` no longer treats Escape as Back / close
  modal when the key was already handled or its target sits in `.xterm`, `.monaco-editor`,
  `input`, `textarea`, `select` or `[contenteditable="true"]` (Claude Code uses Esc to
  interrupt).

---

*Last checked: 2026-08-25T00:00:00Z by claude (opus-5)*
