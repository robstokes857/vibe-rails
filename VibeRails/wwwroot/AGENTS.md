# Web UI Frontend

Vanilla JavaScript SPA using Bootstrap 5 and xterm.js. No build step required.

**Terminology:** "Web UI Chat" refers to the xterm.js-based terminal, NOT a separate chat UI.

## Architecture

| File | Purpose |
|------|---------|
| [app.js](app.js) | Central controller, routing, API layer |
| [js/modules/internal-tools-modal.js](js/modules/internal-tools-modal.js) | Triple-click the brand icon to open Internal tools: About/version, retained upload attempts, and filterable application/Demon logs and feature journal; lazy loaded with bounded pages and no polling |
| [js/modules/settings-controller.js](js/modules/settings-controller.js) | App settings, split into General / LLMs / Git / KEYS section tabs (panels stay in the DOM so dirty tracking and the save bar keep reading hidden-tab controls; `_initSettingsTabs` owns click + arrow-key switching); includes the off-by-default **Share session data** switch gated by a saved API key and configured export endpoint, plus the legacy one-shot **Export Data** button and progress modal ([js/modules/data-export-modal.js](js/modules/data-export-modal.js)) |
| [js/modules/settings-keys.js](js/modules/settings-keys.js) | Lazy KEYS panel: create password-protected RSA-4096 keys, sync public keys to the saved API-key account, download public/encrypted private PEMs, and sign a message/file into public-verification JSON. Independent of the settings save bar. |
| [js/modules/terminal-multitab.js](js/modules/terminal-multitab.js) | Reusable xterm.js terminal manager with per-tab lifecycle and environment picker |
| [js/modules/llm-picker-controller.js](js/modules/llm-picker-controller.js) | Shared launch-picker catalog, Tom Select lifecycle, customization modal, and live preference refresh |
| [js/modules/terminal-token-compression.js](js/modules/terminal-token-compression.js) | Persistent token-savings meter and per-tab pause-badge display (the per-tab on/off toggle was removed 2026-07-19; the saver is now per-LLM in Settings) |
| [js/modules/terminal-snapshot-renderer.js](js/modules/terminal-snapshot-renderer.js) | Renders reserved `xterm_ui_bytes` payloads into xterm.js and captures PNG data URLs for MCP Explorer previews |
| [js/modules/environment-controller.js](js/modules/environment-controller.js) | Environment CRUD + "Web UI" launch button |
| [js/modules/sandbox-controller.js](js/modules/sandbox-controller.js) | Sandbox CRUD + launch terminals/VS Code into sandbox dirs |
| [js/modules/dashboard-controller.js](js/modules/dashboard-controller.js) | Unified Project health page (Rules, VCA, Git Guard, and Code quality; no embedded terminal) |
| [js/modules/code-analyzer-dashboard.js](js/modules/code-analyzer-dashboard.js) | Compact MintLint score card plus the modal file/metric/source report |
| [js/modules/project-health-fix-launcher.js](js/modules/project-health-fix-launcher.js) | Inline shared agent/environment pickers beside Project health Fix actions; synchronizes and remembers the target for direct launch |
| [js/modules/jobs-controller.js](js/modules/jobs-controller.js) | Automation page: ordered repository-script/Worker workflow editor, automation CRUD, per-action run details, recipes, and "Run now" (queues a native terminal run; `launchFromNav` for the nav launcher); owns the shared `PythonScriptsController` |
| [js/modules/python-scripts-controller.js](js/modules/python-scripts-controller.js) | "Python scripts" section of the Automation page + shared lifecycle and signing flows |
| [js/modules/python-script-workbench.js](js/modules/python-script-workbench.js) | `python-script` view: Monaco editor beside a docked agent terminal for one script (see "Python script workbench" below) |
| [js/modules/python-run-window.js](js/modules/python-run-window.js) | The little run window: argument rows + stdin in, exit code / output / return value out, no terminal (see "Python script run window" below) |
| [js/modules/automation-launcher.js](js/modules/automation-launcher.js) | Nav "Launch" flyout (automations + Python scripts, unsigned ones disabled) and its order/show-hide customize modal over `/api/v1/automation-nav/preferences` |
| [js/modules/board-controller.js](js/modules/board-controller.js) | `board` view: the lane board — drag cards between lanes, drag lanes to reorder, filter, and the card editor with comments |
| [js/modules/board-api.js](js/modules/board-api.js) | Board data layer: a thin client over `/api/v1/board/*` (every call rides `app.apiCall`, so cookie + tab header apply). `BoardApi.attach(app)` once from the controller |
| [js/modules/board-card-links.js](js/modules/board-card-links.js) | Linked cards rail: project-wide key/title search, immediate link/unlink, and navigation through the card editor's unsaved-edit guard |
| [js/modules/board-text.js](js/modules/board-text.js) | Renders a comment/description body. **Escape-first**: the input is escaped before any transform, so no sanitizer is needed and none is present |
| [js/modules/diff-modal.js](js/modules/diff-modal.js) | Shared Monaco diff viewer as a nested modal layer. Used by Board commits and the sandbox "View Diff" |

## Settings signing keys

KEYS initializes and fetches only when selected. Its create, backup, and sign forms are separate
from `app-settings-form` and excluded from settings dirty tracking. Reopening KEYS refreshes the
saved API-key status. Public keys sync on creation when that credential exists; existing keys have
an explicit Sync public key action, which asks for the key password so the backend can prove
possession with a registration challenge. The account link opens `https://viberails.ai/Keys` for stored
public keys and validation history. The panel explains that successful public verification reveals
the signer's account email.

PIN/password validation counts Unicode code points (4–128, nonblank); confirmation must match
exactly. Never normalize or trim the password, save it in browser storage, or retain it in panel
state. Password fields clear after every attempted operation, on section changes, on pagehide,
and on unload. Unload aborts pending requests and prevents late results from triggering downloads.
Only encrypted private PEM backup bytes may leave the backend. Public metadata is escaped before
rendering. Signing preserves exact UTF-8 message bytes or file bytes, bounded to 64 KiB before
base64 encoding. The resulting JSON is for
`POST https://viberails.ai/public/api/v1/signatures/verify`, using RSA-PSS-SHA256.

## Board

Read the cross-layer [Board contributor guide](../Services/Board/AGENTS.md) and
[architecture/review](../Services/Board/ARCHITECTURE.md) for API/storage contracts and open
concurrency findings. Card saves intentionally accept the last write;
filtered drag positions need full-lane semantics, and the current 12-file UI limit disagrees
with the server's 40-current-attachment contract.

The `board` nav destination (`board-template` in index.html, `BoardController`) is a lane board
for the current project. It rides the `.vb-rules-workspace-active` flowing shell, so the lanes
fill the viewport height and the board scrolls sideways while the page itself does not. Lanes are
`flex: 1 1 0` with a 232px floor: they share the width evenly and only start scrolling once they
cannot all fit.

The page heading is **Vibe Board**, using the same centered, uppercase gradient heading as
Application Settings. New card lives in the board toolbar. Top-left of the heading sits the
**board picker** (a project can hold several boards — sprints, sub-projects): a select, a `+`
that creates one (default lanes; the new board opens at once), and a settings button for
rename/delete. The settings modal also includes **Agent context**: a default message plus default-only,
type-only, or combined messages for each card type. `board-settings.js` owns these asynchronous,
abortable editors and revision-checked saves. Context is sent for both Start work and Chat with
agent. **Save context** is independent of **Save name**.
The selection persists in `localStorage` (`viberails.board.selected.v1`), every
list call carries the board id. Refresh generations discard stale catalog, lane, card, and error
responses; switching boards clears the previous lanes/cards until the new board loads. The card editor's Lane field groups every board's lanes so a
card moves between boards by saving it into another board's lane. Card keys stay per project.

**The board is per project and server-backed.** `board-api.js` is a thin client over
`/api/v1/board/*` (`VibeRails/Routes/BoardRoutes.cs`, root backend only); the server scopes every
call to the open workspace, so the client never sends a project path. The list endpoint returns
card **summaries** (including the canonical `type`, but no comments/commits/sessions/attachments,
plus `commentCount`, `activeSessionId`, `activeTabId`); `getBoardCardAsync` returns the full card, which is why
`openCardEditor` always re-fetches — an LLM may have commented on or moved the card since the
board loaded. There is no "Reset sample" any more; a new project starts with five empty lanes.

**Cards are work items for LLMs.** `card.assignee` is an LLM picker key (`base:claude`,
`env:7:codex`), never a person: the editor's Assignee field is `mountLlmPicker(app, select,
{ context: 'sandbox' })` from `pickers/llm-picker.js` (environments + bare CLIs, no shell, no
Automation Workers) with an unassign button beside it; `assigneeInfo()` turns a key into a label
(from the picker catalog) and a CLI-brand avatar (`getCliBrand`), and the toolbar's assignee
filter lists the keys present on the board. **Start work** (`startWork`) saves the form, POSTs
`/cards/{id}/launch`, remembers the tab with `taskKey: 'board-card:<cardId>'`, and refreshes
the board without adopting/focusing the terminal or navigating away. Creating or saving a
card never immediately launches an agent (a lane Automation can queue after its delay). Sessions
opens a linked terminal. The
**Chat with:** action uses the same shared LLM/environment picker and connected control styling
as Project health's **Fix rules with:**. It defaults to the card's assignee (or the first enabled
target for an unassigned card); its selection is independent of the saved assignment. It saves
the card and uses the same launch route with the selected target and `intent: 'chat'`,
then adopts/focuses the returned tab (or navigates to `terminal-focus`). The prompt asks for a
status/history review and discussion, waiting for the user before implementation. Both launch
actions share the in-flight guard and are disabled for a known running session. The
server composes the LLM's first message from the card and prepends it to the selected environment's
Initial Message (`Services/Board/BoardPromptComposer.cs`), then links the session to the card.
Both card pickers are disposed on modal replacement/close and Board unload.
Task-key namespace: `board-card:<cardId>` (keep it distinct from `python-script*:`).

Every card also has one canonical work type: `task` (the neutral default and legacy backfill),
`bug`, `feature`, `research-spike`, or `chore` (shown as **Chore / tech debt**). The fixed taxonomy
lives in `BoardCardTypes`; the editor, tile chip, toolbar filter, REST responses, MCP tools, and
launch prompt all use those same storage values rather than deriving type from tags.

Card headings, launch session names, and remembered terminal labels use `KEY · Card title`, where
`KEY` is the server's `card.key` (`VB-n` for existing projects, the project's own prefix such as
`VR-n` for projects that got their first card after 1.10.19). Never rebuild a key from `'VB-'`.
Base assignees show `board-launch-options.js` controls for model, effort, and start mode;
saved environments keep their own configuration. The pinned model catalog is shared with
Environments through `llm-model-catalog.js`. Choices persist on the card and reach the backend
as typed `baseLlmOptions`, never browser-built CLI argument strings. Changing/unassigning the
provider clears those controls. Picker mount is asynchronous: initialize controls using the
saved assignee, and explicitly clear them after the picker's silent unassign operation.

Start work becomes a disabled **Agent running** button when the card has an active linked
session. The save response is checked again before launching, and the backend refuses a launch
when its live tab list already contains one of the card's linked sessions.

`TODO(board)`: an **Auto Launch** option (per card and/or per lane) so dropping an assigned card
into a lane starts work by itself — noted in the controller header and `BoardLaunchService`;
not built.

Lane **Automations** are separate from that assignee-launch TODO. The lane settings modal
selects any number of existing project Automations using checkboxes, with a separate **Save automations** action. The server
waits 60 seconds after a card enters the lane; another move replaces the pending trigger. The
browser owns no debounce timer. New cards count as lane entries; same-lane edits/reorders do
not. Saved settings affect future entries and cancel pending entries for the lane. Disabled
Automations and active-job overlap are skipped independently for each selection. Clear all
checkboxes to disable lane Automations; selected disabled/deleted jobs remain removable. The ordinary root Automation scheduler and
native-terminal run lifecycle apply.

The view uses the app's shared surfaces rather than its own: `app.showModal` (upgraded to
`modal-xl` for the card editor, the same way the rule and quality modals do it), `confirmDialog`
for every delete, and `app.showToast` for results. It owns no toast stack, no Bootstrap modal, and
no theme toggle. Drag and drop is the globally loaded Sortable; `unload()` destroys those
instances, because they attach document-level listeners that outlive the view's DOM.

### The card editor

Laid out like a Jira / older Azure DevOps work item, not a tabbed dialog. The dialog fills the
viewport (`height: calc(100% - 2rem)`); `app.showModal`'s `modal-dialog-scrollable` class is
stripped so `.modal-body` does not grow a second scrollbar. One region scrolls:
`.board-editor-scroll` (both columns together). The body flows top to bottom — title, description,
then the comment thread at the bottom. Everything *about* the card lives in the right rail: the
fields, then Commits (git-commit icons) and Sessions (terminal icons — commits use `fa-code-branch`,
which stays legible at 0.78rem where `fa-code-commit` reads as a faint dot). Save/Delete sit in a
footer sibling of that scroller, so they stay reachable and never paint over the commit list.
The viewport-fill + single-scroller layout is pinned in
`Tests/wwwroot/js/board-card-modal.test.mjs`.

Buttons follow the app's outline register (`btn-outline-primary` / `-secondary` / `-danger`) rather
than solid fills; there are no solid `btn-primary` buttons in this view. The composer's submit sits
in a footer **below** the textarea, not in its toolbar.

**Do not reintroduce tabs here** — they were tried and rejected.

**Linked cards** sits below the fields in the right rail. Saved cards can link to cards on any
board in the same project; each relation appears on both cards. The full card response carries
`linkedCards[]` with current key/title, board and lane names. `board-card-links.js` owns the
search picker (up to 50 matches by key or title), immediate link/unlink calls, and its abortable
search lifecycle. Self/already-linked cards are omitted. All displayed metadata is escaped.
Opening a linked card checks for unsaved fields, description or comment text first; cancel
leaves the draft intact. Link mutations never save or reload the surrounding form. New cards
must be saved before links can be added. Dispose the rail on modal close/replacement and unload.

**Shared sessions (VB-25)**: the same session ID may appear on several cards in one project.
Agents use `attach_board_session`; users can paste the same ID into each Sessions rail. Each card
uses its returned `activeSessionId`/`activeTabId` for the live dot and running-agent controls.
Both rails open the same terminal/replay; unlink removes only that card's entry. Linked-card
relationships do not automatically share sessions or commits. Refresh to see MCP attachments.
Once the session is attached, one agent `link_board_commit` call shares the snapshot with all
its attached cards; the frontend reads the ordinary commit lists and needs no extra request.

Descriptions open as rendered text (including attached images), with an Edit/Preview toggle
in the composer toolbar. Empty descriptions start in edit mode. The textarea remains the source
for Save and Start work in either mode; preview uses the same attachment-aware renderer as comments.

The description textarea grows in normal document flow. Do not make the new-card description
block, composer, or textarea a `flex: 1` chain constrained to leftover viewport height: the
auto-grow routine can then make the textarea taller than its composer and its text paints over the
Attachments section. `.board-editor-scroll` is the one viewport overflow owner.

**Comment and description text is not Markdown.** `board-text.js` supports a deliberately tiny
syntax: fenced code, inline code, `http(s)` autolinks and images. It escapes the entire input
*before* any transform runs, so raw HTML never enters the pipeline and every tag in the output is
one the renderer wrote itself. That is why there is no sanitizer here, and why adding a transform
that interpolates unescaped user text would break the whole security story. The invariants are
pinned in `Tests/wwwroot/js/board-text.test.mjs` — the CSP sets `script-src 'unsafe-inline'` with
no nonce, so an injected handler *would* run; this renderer is the only thing standing in the way.

Layout rules worth keeping: `pre.board-code` uses `white-space: pre` + `overflow-x: auto`, and
every ancestor carries `min-width: 0` (including `grid-template-columns: 28px minmax(0, 1fr)` on
`.board-comment`). Without that chain a wide stack trace widens the whole dialog. Long comment
bodies clamp with a Show more expander, and the clamp class must be applied **before** measuring
overflow — an unclamped body always reports `scrollHeight === clientHeight`, so measuring first
detects nothing.

**Measure synchronously, not in `requestAnimationFrame`.** Reading `scrollHeight` forces layout, so
no frame is needed — and a frame never arrives while the page is occluded, which a backgrounded VS
Code webview routinely is. The clamps, the composer's initial auto-size and the scroll-to-new-comment
all depend on this; putting any of them behind rAF silently breaks them in the webview with nothing
to re-measure later. (Same failure class as the cold-start `setTimeout` throttling documented in
TERMINAL.md.)

Attachments accept any file, preserving its original bytes. There is **no size limit**, per file
or per card — only the count is bounded (12 current files), because that is a list someone has to
scan rather than a number of bytes. The upload route lifts Kestrel's body limit in middleware; a
`RequestSizeLimitAttribute` on a minimal-API endpoint does nothing, because only the MVC filter
pipeline honours it, so the documented cap used to be a no-op sitting under Kestrel's 30 MB
default. New-card uploads queue in the editor until Save; existing-card uploads persist
immediately. Small verified raster images keep
their inline `data:` previews for `![name](attachment:<id>)`; other content loads with both
credentials through `/attachments/{id}/content` using `app.apiCall(..., {responseType:'blob'})`.
No credential is placed in a URL, and no uploaded file is served from static assets.

`board-attachments.js` owns a nested, disposable viewer: TXT **and Markdown** both reach the
DOM through textContent, so Markdown previews as its own source and no Markdown parser or
HTML sanitizer is vendored; PDF.js paints canvas pages without document scripting,
annotations, XFA, or eval. Other file types only download as octet-stream. Uploaded
filenames and metadata remain untrusted. Close/unload aborts requests, terminates PDF work,
and revokes Blob URLs. Image previews use tracked Blob URLs directly, avoiding a full base64 copy;
both the browser and VS Code image CSP allow these URLs.

`assets/board` holds exactly one dependency — PDF.js, as `pdf.min.js` plus
`pdf.worker.min.js`. Its CMaps, standard fonts and Wasm decoders are deliberately not
vendored (194 files and 6.4 MB against 1.7 MB for the renderer), so CJK text and PDFs that
omit the base-14 fonts fall back to system faces. Read `assets/board/README.md` before
upgrading or before adding a frontend library here: rendered Markdown costs a parser plus a
sanitizer to re-earn what textContent gives for free.

Cards have one current state: no History rail or expected-description token. Attachment changes
do not require a revision refresh. Failed queued uploads retain the card ID and unfinished queue
so Save retries only remaining files. Removing an attachment deletes its stored bytes.

**Needs your attention** is the `flagged` editor checkbox. A flagged tile is red with a small
flag beside its key and an accessible attention label. `blocked` remains a separate field.
Both the REST editor and MCP can set or clear the flag. Lane settings have no WIP option;
lane headers show the full card count without warnings or limits.

**Agent notes** (2026-09-17) are a collapsed rail section. They
are the scratchpad agents write over MCP (`append_board_note`) to checkpoint findings while they
work; the server keeps them out of `comments[]` and the comment count (`BoardComments.Kind`).
The card response carries `notes[]`, so the section renders from the loaded card with the same
escape-first `renderCommentHtml` as comments and no extra fetch (`renderNotesPanel`);
`board-api.js` has `getCardNotesAsync` / `addCardNoteAsync` for a refresh or a user note. Do not
merge notes into the comment thread: the thread is what the human reads, the notes are working
state.

**Saving a description never sends terminal input.** There is no notify endpoint: it was removed
2026-09-15 because the sequence it sent — two Escapes, the text, Enter — opens Claude Code's rewind
menu on an idle prompt rather than clearing it, so the message and its Enter landed in that menu and
could restore a checkpoint. Nothing else on the board types into a terminal either: the Codex
plan-mode handshake that did was removed the same day, so every launch option is a command-line
argument decided before the CLI starts. Do not reintroduce a "tell the running agent" action, or
any other write into a live TUI.

### Commits, sessions, and the diff viewer

Linking a commit sends only the sha. The server captures metadata and before/after file contents
from the current checkout and saves the link and code snapshot atomically. The diff endpoint
reads only that snapshot in the shape the sandbox viewer uses, so deleted workspaces do not
break saved code history. A failed capture creates no link. More than 60 changed files is
rejected; large text previews keep 400,000 characters and a truncation marker. Older links
without snapshots must be unlinked and linked again while their checkout is available.
Sessions are the terminal sessions linked to the card — by Start work
(origin `launch`), by an LLM touching the card over MCP (`mcp`), or by hand (`manual`); a live
one (`session.active`, computed server-side from the open tabs) shows the pulsing
`.board-live-dot` on the lane card and its row jumps to the tab (`focusSessionTab`, adopt or
navigate). Clicking a commit opens `diff-modal.js`; clicking an ended session opens the shared
terminal replay (`session-viewer.js` `showReplayModal`, the chat-history sidebar's "Replay
Session"), which mounts its own overlay on `document.body` — the controller wraps it so Escape
closes the replay (captured) and `app.hasActiveNestedModalLayer` recognises
`.vb-session-replay-layer`. Card session rows offer open/replay and unlink. Pasting a
session id into the Sessions form links that session by hand. Agent comments carry
`author.sessionId`; their "in session" pill opens the same replay with `seekToUtc: createdAt` —
the player fast-forwards to 1.5 s before that wall-clock instant (offset against the replay's
`startedUtc`) and plays on from there, so the reader sees the tool call that wrote the comment. Comments carry `author: { kind: 'user'|'agent', label, cli }` — the UI posts as
"You", agent comments come from the MCP tools and get an "agent" pill. Both are **nested modal layers**, not `app.showModal` calls:
`showModal` rebuilds `#modal-container` wholesale, which would destroy the card editor underneath
and, on close, restore focus past both dialogs. The layers append themselves, `inert` the existing
children, own their focus trap and Escape, and restore on close — the same pattern as
`environment-steps.js`. Their classes are registered in `app.js`'s `hasActiveNestedModalLayer`.

`diff-modal.js` is shared with `sandbox-controller.js`. Two behaviours must not regress: Monaco
does **not** dispose externally-set models with the editor (each holds a whole file's text), so
they are disposed explicitly and always before the container is removed; and an Escape raised from
inside `.monaco-editor` belongs to Monaco's own widgets and must be left alone. Note that
`monaco.editor.getDiffEditors()` accumulates and is never pruned on dispose — use
`getModels().length` as the leak signal, which is what the e2e test asserts.

Filters (search, assignee, type, priority, tag) persist per browser in
`localStorage['viberails.board.filters.v1']`. Clicking a card's tag or avatar toggles that filter,
which is why those two controls stop propagation before the card's own open handler runs.

## Internal tools modal

Three clicks on the brand icon within 900ms opens `Internal tools`. `app.js` dynamically imports
`internal-tools-modal.js`, so this surface adds no requests to normal startup. Its `SECTIONS`
registry owns each tab's markup and lazy load function; add new diagnostic/CRUD screens there.
The modal uses `app.showModal` with `onClose` cleanup, preserving the shared focus trap,
background inert state, Escape behavior and focus restoration. Arrow keys, Home and End switch
the About, Data uploads and Logs tabs.

About reads `/api/v1/update/version`. Data uploads and Logs read the authenticated
`/api/v1/internal/uploads` and `/api/v1/internal/logs` endpoints in pages of 100. The upload
screen shows the latest retained event for each attempt; Details and operation-linked View logs
work, while Create/Edit/Delete are explicit disabled placeholders. Logs defaults to existing
application files (`source=application`); the source selector also offers VibeRails Demon files
(`source=daemon`) and the new feature journal (`source=features`). Existing files are read on
demand without copying or backfilling them. Filter by feature/category, level, and search text;
status and operation ID apply only to the feature journal and are disabled for other sources.
Changing sources clears incompatible filters, categories, and old results and starts on page 1.
An upload's View logs explicitly selects the feature journal and its operation ID.
Filter submits, selection changes and Refresh are the only reload triggers. Requests abort on
tab changes or modal close; there are no polling timers or global subscriptions. Render server
text with escaping/textContent, including multiline exception details and the source filename.
Keep endpoint results bounded and show `truncated`/logger health notices so a bounded window is
not presented as complete history. Upload history and the feature journal remain forward-only.
CSS uses the `vb-internal-*` prefix and theme tokens with fallback colors.

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
- **Base CLIs**: Claude, Codex, GLM 5.2, GLM 5.3, DeepSeek V4 Pro, Kimi K3, Grok, OpenCode, Copilot, Antigravity (each shown as "(default)") — resolved to its executable server-side (Antigravity → `agy`, Grok → `grok`). Grok's model (`grok-4.7` or `grok-4.6`) is chosen on the environment or Board launch, not by a separate CLI entry.
- **Custom Environments**: User-created environments — spawned directly via the tab start endpoint

## Terminal auto-reconnect

Navigation destroys the `TerminalManager` (every xterm + WebSocket). On re-entry the restored
manager reconnects the active tab at once and then `scheduleBackgroundReconnect()` brings the
other restored tabs back one at a time, each hidden xterm pinned to the visible tab's PTY geometry
(`terminal-reconnect.js` → `resolveHiddenConnectGeometry`): a `display:none` host cannot be
fitted, and shipping xterm's 120×40 constructor default in the WS URL would resize the PTY to a
size nothing on screen has. An unexpected socket close retries with backoff (2 s doubling to 30 s,
five attempts) through `TerminalAutoReconnect`; a "Session taken over" close is never retried
(two viewers would steal the session from each other forever), and plain tab clicks still pass
`connectIfNeeded: false` — selection is not a reconnect. Kill switch: Terminal settings →
Rendering → Auto-reconnect (`viberails_terminal_autoReconnect` = `off`). Tests:
`Tests/wwwroot/js/terminal-reconnect.test.mjs`, `UITests/tests/terminal-reconnect.spec.js`
(the fake CLI echoes its command line, so assert replay fidelity as text equality, never as a
marker count).

## Automation workflow editor

`jobs-controller.js` owns an ordered workflow made of repository Script actions and at most one
Worker action. Script actions select `.py`, `.ps1`, or `.sh`, an explicit matching runtime,
optional repository-relative working directory, optional per-action timeout, and zero or more
argument rows. Each row is one argv value; never replace the rows with a command-line textbox or
join them into shell text. The backend remains authoritative for containment, links, runtime
availability, and SHA-256 approval.

The workflow is state-backed in `editorActions`. Text/select input updates the matching object
without rerendering so the caret survives. Structural operations (add/remove/move action, add/remove
argument, file/folder picker) rerender the list. A rerender also recreates the one Worker picker,
so `renderEditorActions` must dispose/remount its Tom Select instance and restore the selected
Environment. Add Worker is disabled as soon as one Worker exists; scripts may appear before or
after it, or form a script-only workflow.

Run details fetch the individual run and render its immutable action snapshot: per-action status,
argv, error, and captured stdout/stderr. Worker actions link to their own normal terminal replay.
All arbitrary output uses escaped text/pre content. A completed workflow with no Worker therefore
has useful history even though it has no `Sessions` recording.

Recipes export V2 action order plus portable script configuration, never action ids, Environment
ids, or approval hashes. V1 one-Worker recipes normalize to V2 on import. Imports are untrusted:
the confirmation shows Worker arguments/instructions and script paths, creates the Automation
disabled, and lets the backend resolve and pin the local repository bytes. Do not accept a hash
from the recipe as local approval.

Card/run-detail styles use the `job-action-*`, `job-script-*`, and `job-run-action-*` prefixes and
must keep explicit `var(--token, #fallback)` colors in every theme scope.

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

## Rule management forms

`agent-controller.js` owns the Manage rules modal, full editor, and new-file wizard.
The manager shows searchable directory paths and scope. Add uses the same form in the
manager and full editor; the editor has explicit per-rule Edit/Remove actions. Back from
creation/details restores the selected manager through the parent route's
`reopenRuleManager` data, consumed after that route loads. Do not bind `go-back` locally:
`app.js` already handles it globally, and a second listener pops history twice.
The full editor keeps individual file cards visible above Rules, using each file's type
icon and filename. Large scopes scroll within the card grid. Display-name controls sit
beside the name and use Set/Edit display name; the value is a friendly searchable label
only, and does not rename `vc.rules.md` or change its path/scope.

Parameterized rules need their arguments before any write. File Lock and Directory Lock
use the shared `app.pickFileSystemEntry` with file/directory mode; `relativeRulePath`
converts the absolute selection relative to the declaring `vc.rules.md` directory and
rejects selections outside it. `Directory Lock('.')` includes that directory and every
subfolder; `/` is absolute and invalid. Keep Browse and these examples in Add and wizard
forms. `Check commit message for` requires a plain comma-separated list of forbidden
whole words/phrases (case insensitive), such as `WIP, fix later, temporary`. The frontend
materializes `Check commit message for: ...`; empty lists/entries, control characters,
and CSV quote wrappers are rejected. Backend write validation remains authoritative.

## Customizable LLM Pickers

Project health's Fix actions use inline selectors with the shared `sandbox` picker context,
including custom environments. Selecting an agent synchronizes all three Fix selectors and
remembers the choice; each Fix button launches directly without an intermediate dialog.
The page disposes its pickers on unload. The quality report keeps file context and healthy metric
groups collapsed, with an `Inspect metric` selector beside the read-only source. On smaller
windows, source precedes the detailed metrics; narrow windows also offer `Inspect file`.
Keep the report's `code-analyzer-panel` class: it supplies theme variables and hides the
source-loading overlay when Monaco is ready. Metric clicks retarget the existing editor;
closing the report disposes the editor and file-rail listeners.

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

The three launch contexts add a persistent **View/Edit all LLMs** footer to their dropdowns. Its
nested modal changes visibility and within-group order globally. Every row also offers **Launch**,
including hidden items, through the existing focused Web Terminal flow. Launch never saves draft
preferences or enables an item; custom environments are resolved by id from the current catalog's
environment data. Picker owners can pass `getLaunchWorkingDirectory` to retain their sandbox or
terminal host directory; otherwise launch uses the project directory. A disabled selection that is
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
window, so `runNow` just POSTs, toasts and refreshes the run history. This includes `.py` actions
inside an Automation. The separate signed Python-script workbench is the exception that can use a
Web UI tab for its **Run in terminal…** flow (see below).

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
- **Inputs**. Scripts take **argument** rows (optional flag + value) and a **Standard input**
  box. Values persist per script in localStorage `viberails.pythonRun.<name>`. A script without
  remembered inputs runs when the window opens. Legacy remembered typed MCP inputs suppress that
  automatic run so the user can review argument rows first.
- **The command line** under the inputs is the payload: `resolveArgv()` builds one array,
  and the window prints and posts the same values.
- **Output**. `POST /api/v1/python-scripts/run` with `{ name, arguments, standardInput }`
  returns exit code, duration, stdout/stderr and `returnJson` — the JSON object or array
  the script printed as the whole of stdout or on its last line
  (`PythonScriptService.ExtractReturnJson`; a bare scalar is output, not a return value).
  The window shows **Returned** (pretty-printed, copyable) only when there is one, then
  **Output**. Argv and stdin are bounded server-side (64 args, 8k chars each, 256k stdin).
  The result also lands in the row's last-run drawer through `recordRun`.
- **Keys**: Escape closes, Ctrl/⌘+Enter runs from anywhere including the stdin box.
- **Python MCP removed (2026-09-18)**: the exposure switch, configurator, and special Explorer
  group are gone. Signed scripts remain available for human-initiated runs.
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

*Last checked: 2026-09-01T00:00:00Z by Codex*
