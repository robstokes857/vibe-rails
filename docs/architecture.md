# VibeRails architecture reference

## Board editor refresh (VIBE-44)

The Board refreshes lane membership and card summaries every ten seconds while visible, and on
window focus/visibility return. It preserves loaded page depth, scroll positions and editor drafts;
stale reads are canceled or discarded. Previous work and Code quality/VCA summaries live in the
card sidebar. Description is always editable without previews; rendered Comments always use
Markdown and offer agent filtering with attention entries first. See the
[Board contract](../VibeRails/Services/Board/ARCHITECTURE.md#card-editor-and-local-refresh-vibe-44).

## Card recall (VB-13)

Board recall adds structured handoffs, curated file references, exact project-scoped card lookup,
BGE/keyword card discovery and paged linked-session discussion. Chat supports an optional question
and may coexist with implementation without changing lanes. See the
[component contract](../VibeRails/Services/Board/ARCHITECTURE.md#card-recall-vb-13) for storage,
retrieval budgets, incremental indexing and launch behavior.

## Complete backups (VIBE-29, October 2, 2026)

The root-host `CompleteBackupJob` independently drains versioned Board, state, proxy and
configuration archives through cancellable SQLite snapshots, private durable staging and
verified part/manifest receipts. Session exports and bounded Board sync continue separately.
The [component contract](../VibeRails/Services/CompleteBackups/README.md) defines cadence,
freshness, exact file/credential exclusions, retry behavior, size limits and rollout ordering.
General Settings displays per-dataset coverage. Restore/inspection instructions live in
`vibe-books/vibe-data/docs/complete-backups.md`; no source retention policy changes.

## Vibe AI navigation visibility

General Settings exposes **Show Vibe AI UI**, hidden by default. `ShowVibeAiUi` persists in the
shared settings file; GET/settings responses report it and nullable updates preserve it when
omitted. Startup and computer-name updates leave it unchanged. The frontend applies saved
visibility to both navigation layouts without disabling the inspector's search services.

## Optional Vibe Story tracking

General Settings exposes **Create Vibe Story Tracking**, enabled by default in new and older
settings files. Nullable API updates preserve the saved choice when an older client omits it.
`CommandService` reads the shared settings file for each launch and adds optional Board tracking
guidance to every managed LLM's initial prompt, including base/default launches without a custom
prompt and saved environments. Existing task/summary precedence and provider argv conventions
are preserved. The guidance asks agents to use their discretion, reuse an existing story and
record progress/results and session history when useful. A guidance-only launch waits for a user
task. Plain shells are unaffected; toggling applies to new sessions and does not grant Board tools
or change the CLI's approval policy. Tracking uses the existing Board MCP tools and storage.

Moved verbatim from the root `AGENTS.md` on 2026-09-27 so the instructions file that every
agent session loads stays small; the material below was last reviewed 2026-08-06 (v1.9.11).
Treat dates, counts and "current" statements as records of that review and check the source
before relying on them. Policies (database changes, Board attention flags, runbook locations)
remain in [AGENTS.md](../AGENTS.md); component guides are linked from there.

Contents: project overview, technology stack, project structure, application modes, component
interaction flows, key components (services, MCP, data, API routes, frontend), design patterns,
configuration and file locations, development workflows, common agent tasks, troubleshooting
and debug logging, security, performance, future enhancements, resources.

## Chat history metadata (2026-09-28)

The history API reads session pages from `state.db`. `ChatHistoryService` then obtains current
card labels for those session IDs through `IBoardStore.GetSessionCardsAsync`, which batches reads
from `board.db` and includes primary/additional attachments without deleted cards. The existing
history endpoints retain their global local-session scope and session-plus-tab authentication.
No cross-database SQL join, schema migration or historical-data rewrite is needed. The sidebar
uses these labels for automatic titles, card metadata and combined filters; see the
[frontend contract](../VibeRails/wwwroot/AGENTS.md#chat-history-sidebar).

## Project Overview

**VibeRails** is a sophisticated desktop/web application for managing and enforcing coding standards across AI-powered development workflows. It serves as a unified control panel for multiple LLM CLIs (Claude, Codex, Antigravity) with comprehensive rule enforcement, session logging, and MCP integration.

**Live Site**: [https://viberails.ai/](https://viberails.ai/)

### Core Capabilities
- **Rule File Management** - Create and manage `vc.rules.md` files with customizable coding rules
- **Rule Enforcement** - Define standards with three enforcement levels (WARN/COMMIT/STOP)
- **Multi-LLM Support** - Unified interface for Claude, Codex, Antigravity, Copilot, and OpenCode CLIs
- **Environment Management** - Configure separate environments for different LLM providers with custom args and prompts. Launch environments directly in the Web UI terminal with the "Web UI" button or select from the terminal's environment dropdown
- **Sandbox Management** - Create isolated git clone sandboxes for parallel AI workflows. Shallow clones current branch with all dirty/untracked files. Launch terminals or VS Code directly into sandbox directories.
- **Session Logging** - Track and monitor all CLI session history and outputs
- **Automations** - Ordered workflows of repository `.py`, `.ps1`, and `.sh` scripts plus at most one optional Worker, on a schedule or a commit trigger; they run while a VibeRails root backend is open
- **MCP Integration** - Custom Model Context Protocol server with specialized tools

## Technology Stack

### Backend
- **.NET 10.0** - Modern .NET with AOT compilation support
- **ASP.NET Core Slim** - Lightweight web server
- **SQLite** - Local database with WAL mode for concurrency
- **ModelContextProtocol NuGet Package** (v2.0.0) - MCP foundation with custom service layer and tools
- **ModelContextProtocol.AspNetCore** (v2.0.0) - ASP.NET Core integration for the in-process MCP server
- **Pty.Net** - Cross-platform pseudo-terminal support (inlined fork, ConPTY only)
- **PyBridge** - AOT-friendly Python process and session runner (in-tree library)

### Frontend
- **Vanilla JavaScript** - No framework dependencies
- **Bootstrap 5** - UI framework
- **Font Awesome 7** - Icon library
- **XTerm.js** - Terminal emulation in browser
- **Fetch API** - REST API communication

### Build & Testing
- **xUnit v3** (xunit.v3 3.2.2) - Unit testing framework
- **Native AOT** - Ahead-of-time compilation for standalone executables
- **PowerShell** - Build automation scripts

## Project Structure

```
vibe-rails/
├── VibeRails/                      # Main ASP.NET Core application
│   ├── Program.cs                  # Entry point (web server + CLI loop)
│   ├── Init.cs                     # Startup checks (DB init, app settings, git detection)
│   ├── MapRegisterServices.cs      # Dependency injection setup
│   ├── CliLoop.cs                  # CLI interaction loop
│   ├── Routes/                     # API endpoint definitions (split by domain)
│   │   ├── Routes.cs               # Aggregator that maps all route modules
│   │   ├── AgentRoutes.cs          # Rule file management
│   │   ├── TerminalRoutes.cs       # Web terminal session endpoints
│   │   ├── LlmSettingsRoutes.cs    # Claude/Codex per-env settings
│   │   ├── McpRoutes.cs            # MCP tool inspection/calling
│   │   └── ...                     # (SandboxRoutes, SessionRoutes, etc.)
│   │
│   ├── Services/                   # Business logic layer
│   │   ├── AgentFileService.cs    # Rule file management
│   │   ├── FileService.cs         # File system abstraction
│   │   ├── GitService.cs          # Git repository interaction
│   │   ├── RulesService.cs        # Rule parsing and enforcement
│   │   ├── SandboxService.cs      # Sandbox creation, deletion, listing
│   │   ├── Mcp/
│   │   │   └── McpClientService.cs # Custom MCP client service
│   │   └── LlmClis/               # LLM CLI environment management
│   │       ├── LlmCliEnvironmentService.cs
│   │       ├── LaunchLLMService.cs
│   │       ├── BaseLlmCliEnvironment.cs
│   │       ├── ClaudeLlmCliEnvironment.cs
│   │       ├── CodexLlmCliEnvironment.cs
│   │       ├── AntigravityLlmCliEnvironment.cs
│   │       └── Launchers/         # CLI-type-specific launchers (Claude, Codex, Antigravity, Copilot, OpenCode)
│   │
│   ├── DTOs/                       # Data transfer objects
│   │   ├── ResponseRecords.cs      # API response types
│   │   ├── Sandbox.cs              # Sandbox entity model
│   │   ├── LLM.cs                  # LLM enum (NotSet, Codex, Claude, Antigravity, Copilot, Shell, OpenCode, Glm52, Grok46 wire "grok", Glm53, DeepSeekV4Pro, KimiK3)
│   │   ├── LLM_Environment.cs      # Environment configuration
│   │   ├── McpDtos.cs              # MCP protocol DTOs
│   │   └── StateFileObject.cs
│   │
│   ├── Interfaces/                 # Service contracts
│   │   ├── IFileService.cs
│   │   ├── IBaseLlmCliEnvironment.cs
│   │   ├── IMcpService.cs
│   │   └── *LlmCliEnvironment.cs
│   │
│   ├── Utils/                      # Utility classes
│   │   ├── Config.cs               # Configuration management
│   │   ├── LaunchBrowser.cs        # Browser launcher
│   │   ├── PortFinder.cs           # Free port detection
│   │   ├── TerminalOutputFilter.cs # Terminal output filtering
│   │   └── STRINGS.cs              # String constants
│   │
│   └── wwwroot/                    # Static web assets
│       ├── index.html              # Main UI dashboard (SPA)
│       ├── app.js                  # Frontend application logic
│       ├── style.css               # Custom styling
│       ├── js/modules/
│       │   ├── terminal-multitab.js      # Web terminal (multi-tab, environment-aware)
│       │   ├── environment-controller.js # Environment CRUD + Web UI launch button
│       │   ├── sandbox-controller.js   # Sandbox CRUD + launch into sandbox directory
│       │   └── dashboard-controller.js  # Dashboard with state passing for preselection
│       └── assets/                 # Images, fonts, icons
│
├── VibeRails.Data.Abstractions/    # Storage contracts and shared records (no SQLite dependency)
├── VibeRails.Data.Sqlite/          # SQLite stores, connections, migrations, and registration
├── Pty.Net/                        # Cross-platform PTY library (inlined fork, ConPTY only)
├── PyBridge/                       # AOT-friendly Python runner library (in-tree)
│
├── Tests/                          # xUnit test suite
│   ├── AgentFileServiceTests.cs
│   └── IntegrationAgentFileTests.cs
│
└── deploy/                         # Build & deploy scripts
    ├── build.ps1
    ├── buildAndDeployVSCodeExt.ps1
    ├── deploy.ps1
    ├── local_deploy.ps1
    ├── package-platforms.ps1
    ├── prepare-binaries.ps1
    └── test-vscode-marketplace.ps1
```

## Architecture

### Application Modes

VibeRails has a deliberately small startup surface:

#### 1. Web Server Mode (Default)
```bash
vb
```
- Launches ASP.NET web server on available port
- Opens browser to dashboard UI
- Provides REST API for managing agents, environments, sessions
- Terminal sessions started from Web UI via `POST /api/v1/terminal/start`

#### 2. VS Code Extension Mode
```bash
vb --vs-code-v1 [--parent-pid <pid>]
```
- Internal mode used by the VS Code extension and terminal-tab child processes
- Prints a one-time bootstrap URL for the extension host
- Uses the same authenticated web/API backend as browser mode

#### 3. Environment Bootstrap Mode
```bash
vb --env claude                    # Launch base CLI with session tracking + web viewer
vb --env "my-research-setup"       # Launch custom environment (DB lookup)
vb --env antigravity --workdir /project # Explicit working directory
```
- Used by Web UI and VS Code launch paths when a tracked native terminal is needed
- Smart resolution: LLM name → base CLI, otherwise → custom environment DB lookup
- `--workdir` optional: uses git root if available, falls back to current directory
- Starts web server in background and prints the viewer URL
- CLI runs in foreground (Console.ReadKey input loop)
- Web viewers connect via WebSocket; both Console and WebSocket consumers can receive output
- Full session tracking: database logging, user input tracking, git change detection
- Web UI "Stop" button disabled for CLI-owned sessions
- Web server shuts down when CLI terminal exits

#### 4. Git Guard Mode
```bash
vb --git-guard
```
- Opens the authenticated `/git-guard` focused web surface
- Captures the exact staged Git index snapshot (including partially staged files)
- Streams VCA, report-only MintLint, and automated-workflow stage events live to the browser
- VCA is the only preflight stage that can block a commit; automated workflows enqueue before-commit Automations without waiting on them
- The native pre-commit hook uses the same shared pipeline and console event presentation
- The commit-msg hook always removes co-author attribution and `Claude-Session:` trailers before
  chained hooks and VCA validation. `CommitMessageCoAuthorCleaner` keeps the existing terminal-block
  and byte-preservation behavior and recognizes common co-author token spellings for any agent.
  The retired settings field remains stored for older versions; the API ignores it and reports true.
  Settings has no trailer switch or Git tab.

#### 5. Background Automations — there is no background host

Automations run only while a VibeRails root backend is open. There is no daemon, no operating-system
registration, and no `--job-daemon` process role.

A per-user background host (VibeRails Demon, "VBD") existed and was deleted on 2026-09-13, along
with the `VibeRails.Daemon` project, every `JobDaemon*` service, the seven `/api/v1/jobs/demon`
routes, and the VBD orchestration in the installer scripts. The retrospective — what it was, every
design we held, what we tried, and why none of it worked — is at `vibe-books/deamon/deamon_retro.md`.

**Read that retrospective before proposing anything that registers VibeRails with the operating
system.** We do not currently know a good way to do it, and the document is explicit that its
contents are a record of mistakes rather than a starting point.

The old CLI management commands (`vb env`, `vb validate`, `vb hooks`, etc.) are no longer part of the supported surface. Use the Web UI, VS Code extension, or REST APIs for those workflows.

> **Additional process-host modes** (internal, not user-facing): `vb mcp` (MCP stdio server),
> `vb --vca-hook <type>` (VCA hook process host used by git hooks), `vb --job-run <id>`
> (automated workflow run; script-only runs do not need `--env`), `vb --job-trigger`
> (post-commit job enqueue), and `vb --job-tick`
> (compatibility tombstone for the retired OS Jobs scheduler — recognized and exits before the
> web host starts). These are invoked internally by the main app, installers, or
> git hooks, not typed by end users.

### Component Interaction Flow

#### Rule File Management Flow
```
Browser (app.js)
  ↓ [GET /api/v1/agents]
ASP.NET Route Handler (Routes/AgentRoutes.cs)
  ↓ DI injects IAgentFileService
AgentFileService
  ↓ Uses IGitService to find repository root
  ↓ Scans for vc.rules.md files
  ↓ Uses IRulesService to parse and validate rules
  ↓ Optional: Repository for project tracking
Response [AgentFileListResponse]
  ↓ [JSON]
Browser renders agent list with rules
```

#### Session Logging Flow (CLI + Web)
```
vb --env myenv (or vb --env claude)
  ↓
Program.cs starts web server → CliLoop.RunTerminalWithWebAsync()
  ↓ Smart resolves: LLM name → base CLI, custom name → DB lookup
  ↓ Resolves working directory (--workdir or git root)
  ↓ TerminalRunner.RunCliWithWebAsync():
  ↓   Creates Terminal (spawns PTY via PtyProvider)
  ↓   Subscribes DbLoggingConsumer + ConsoleOutputConsumer
  ↓   Registers Terminal with TerminalSessionService (web access)
  ↓   Sets isolated config env vars via LlmCliEnvironmentService
  ↓     - CLAUDE_CONFIG_DIR, CODEX_HOME, XDG_*, etc.
  ↓   Sends CLI command to PTY shell: claude [args]
  ↓
Terminal.ReadLoop → dispatches to all ITerminalConsumer subscribers:
  ├── ConsoleOutputConsumer → Console.Write (CLI output)
  ├── DbLoggingConsumer → TerminalOutputFilter → TerminalStateService.LogOutput()
  └── WebSocketConsumer → WebSocket binary frames (if browser connected)
  ↓
Console.ReadKey / WebSocket input → InputAccumulator → TerminalStateService.RecordInput()
  ↓
Git changes tracked on each user input (Enter key)
  ↓
SQLite: Sessions, SessionLogs, UserInputs, InputFileChanges tables
```

#### Multi-LLM Environment Management
```
LlmCliEnvironmentService
  ├─→ IClaudeLlmCliEnvironment
  │     └─ Config: CLAUDE_CONFIG_DIR
  │
  ├─→ ICodexLlmCliEnvironment
  │     └─ Config: CODEX_HOME
  │
  ├─→ IAntigravityLlmCliEnvironment
  │     └─ Config: none (agy is launch-flag-only)
  │
  ├─→ ICopilotLlmCliEnvironment
  │     └─ Config: none (Copilot is launch-flag-only)
  │
  └─→ IOpencodeLlmCliEnvironment
        └─ Config: XDG_CONFIG_HOME

Each environment defines isolated config directories
```

#### Web Terminal Environment Integration Flow
```
User navigates to Environments page
  ↓
Clicks "Web UI" button next to custom environment
  ↓
environment-controller.js calls launchInWebUI(envId, envName)
  ↓
app.navigate('dashboard', { preselectedEnvId: envId })
  ↓
dashboard-controller.js receives data.preselectedEnvId
  ↓
Passes to terminalController.bindTerminalActions(container, envId)
  ↓ (frontend terminal wiring lives in terminal-multitab.js; dashboard-controller.js
     passes the preselected env id into the terminal selector)
  ↓
populateTerminalSelector() fetches from app.data.environments
  ↓
Renders <optgroup> for Base CLIs + <optgroup> for Custom Environments
  ↓
Preselected environment auto-selected in dropdown
  ↓
User clicks "Start" → startTerminal() parses selection
  ↓
Single API call: POST /api/v1/terminal/start
  Body: { cli: "Antigravity", environmentName: "test_g" }
  ↓
Backend: TerminalRoutes.cs resolves LLM enum, fetches custom args from DB
  ↓
TerminalSessionService.StartSessionAsync() spawns LLM CLI directly in PTY
  ↓ Creates TerminalSession for tracking
  ↓ Sets isolated environment vars for Claude/Codex (agy has none)
  ↓ Spawns: agy --dangerously-skip-permissions
  ↓
Frontend connects WebSocket to /api/v1/terminal/ws
  ↓
Bidirectional byte stream: PTY ↔ WebSocket
  ↓ Output teed to session tracking (DB logging)
  ↓ Input teed to session tracking (git change detection)
  ↓
CLI runs with full session tracking (same as CLI path)
```

#### MCP Architecture
```
vb.exe (Main App)
  ├─ AddMcpServer().WithHttpTransport().WithTools<...>()   # in-process MCP server
  ├─ app.MapMcp("/mcp")                                    # Streamable HTTP endpoint (root backend only)
  └─ Tools: validate_vca · search_history · pause_token_saver · resume_token_saver · get_token_saver_status
     (run_shell_command + web research kept in-tree but not currently exposed — security review 2026-07-02)

McpClientService — thin client wrapper used by the dashboard MCP Explorer to inspect the local
/mcp endpoint by default, or another user-supplied Streamable HTTP MCP endpoint.
```
See [VibeRails/Services/Mcp/AGENTS.md](../VibeRails/Services/Mcp/AGENTS.md) for the full design.

## Key Components

### Linking a viberails.ai account

See [the authentication flow](account-link-auth-flow.md) for the protocol sequence,
credential roles, Auth0 boundary and comparison with OAuth authorization code.

Settings can create and save a new VibeRails API key through typed-code browser approval.
`RemoteAccountLinkService` owns one temporary attempt per root backend; authenticated
`POST`/`GET`/`DELETE /api/v1/settings/remote-link` start, poll and cancel it. The root calls
the hosted `/api/v1/device-links` contract over HTTPS; only the short user code and display
state reach the dashboard. The browser opens `https://viberails.ai/link#code=ABCD-EFGH`; the
website clears the fragment before its Auth0 flow and automatically POSTs the public code
with its antiforgery token. The user checks the computer/account and approves. VS Code uses the narrow
`__viberails_openExternal__` bridge to open that same page in the system browser.

The hosted service keeps pending requests in memory for ten minutes and delivers the
approved API key once. Restarting the site expires pending requests. `ApiKeyStore` merges
the key into the current normal settings file, updates `ParserConfigs`, and resets the
HTTP relay. Current-version processes coordinate the key comparison and save through an
OS mutex, so a key change through Settings during approval prevents overwrite. Older
binaries and external file editors do not participate in that mutex. Failed local writes
retain the received key in memory for retry until cancellation, replacement or shutdown.
The masked `remote-account-linked` event updates open Settings views without replacing
their unrelated drafts. Closing the account modal stops UI polling; reopening it resumes the attempt.

The top and side navigation show **Sign in** only without a saved API key. General Settings
has an Account card with **Sign in**, or **Logged in {email}** and **Switch account**.
Both open the shared device-approval modal. `ApiKeyStore` saves the approved display email
and a SHA-256 fingerprint alongside the key in the existing settings file. The settings
response includes `remoteAccountEmail` only when the full current key matches that fingerprint;
request values for that field are ignored. This preserves the email across restarts and avoids
showing an old identity after a manual key replacement, including by older writers. Keys without
email metadata show **API key configured**. No account identity is inferred from a masked suffix.

Existing keys remain valid; the site lists a linked key with its computer name so it can be
revoked. If a one-time response is lost in transit, the user starts again and can revoke
the unused key on the site. No new local listener, callback, authentication exception,
database, daemon or OS registration is required. The hosted implementation must be deployed
before clients can use sign-in; older sites leave the manual API key input usable.

### Code report inspection

Project health's Code quality card renders the host-owned Code Atlas / Quality Lab viewer
inline (the `code-quality` route is an alias for Project health). `RuleController` supplies the
cached MintLint report; the authenticated,
root-only `POST /api/v1/code-analyzer/graph` supplies bounded working-tree structure and
lexical source references through `RepositoryCodeGraph`. Python package imports, Rust module
trees, JS/TS imports/re-exports and namespace-scoped C#/PHP mentions provide link evidence;
TypeScript interfaces and aliases appear in outlines. Coverage diagnostics distinguish source
filters from bounded omissions. Dependency folders (node_modules, vendor, assets) and C# build
output stay excluded, including report priorities. The Code graph view draws the whole snapshot
on two canvases with no DOM per entity: a 4,500-link ambient budget (tree links first, hashed
sample of references), hover and selection lighting every link of an entity, bounded signals, and
adaptive thinning or stillness on slow machines, and semantic zoom in a dense field: declarations
fade in as the camera closes while the structure, hover, selection, search and filters are
unaffected and the layout never moves. Root-only `GET /api/v1/code-analyzer/changes`
and `GET /api/v1/code-analyzer/changes/diff` list the working tree's changes against HEAD and
serve one file's before/after text for the shared diff viewer; the sidebar switches between
report files and Git changes, and the same list feeds the map's change highlight.
The request retains the existing repository containment and read limits. No preview fixtures are shipped.
Scan/exclusion controls and all rule/commit enforcement remain outside the viewer.
See the [viewer integration contract](../VibeRails/wwwroot/js/modules/code-report/README.md)
for data limits, vendor provenance, themes/CSP, lifecycle and regression tests.

### Vibe Board

VIBE-40 makes agent attention flags require `flagReason` on `update_board_card`. The store saves
the flag, red comment marker and originating-session request atomically. Additive
`board-attention/1` retains requests and resolves them when any writer clears the card flag.
`GET /api/v1/terminal/tabs` reports a nullable `needsAttention` for its own recordings and their
Automation Workers; the existing ten-second poll restores/clears red tab and robot-menu alerts.
Normal terminal statuses continue independently. Comment attention metadata survives sync and
merge; local alert attribution stays local. See `BoardStore.Attention.cs` and the Board guide.

Lane agent buttons include the first lane and open the destination lane's Automation settings.
`board-lane-agents.js` handles assignment/removal, descriptions, reviewer selection and setup state.
Its Add form can create a repository script Automation using the existing Jobs approval path.
Waiting cards show a badge backed by the durable lane queue; the card's Automations rail can skip
one exact pending entry with **Continue without this Automation**. Cards stay in their chosen lane
while entries wait for an available Automation.
New local boards prefill Review with Code quality → VCA → Switch reviewer. Durable recipe receipts
recover across the separate Board/state commits without restoring removed defaults or changing old
boards. See the [starter workflow contract](../VibeRails/Services/Board/ARCHITECTURE.md#new-board-review-defaults-vibe-23).

VB-52 adds owner invitation controls, website recipient inbox/block screens and desktop shared-board
import. The website enforces the three-collaborator cap and Board membership. Additive `board/26`
stores pinned import origins; existing sync carries portable edits both ways. See the
[sharing contract](../VibeRails/Services/Board/SYNC.md#vb-52-shared-boards) for lifecycle and limits.

VIBE-1 adds the card editor's **Move or merge** actions, one Comments stream and human-only
comment deletion. `BoardStore.CardActions.cs` keeps merge/transfer/tombstone writes atomic;
`board-card-organize.js` owns destination selection and draft protection. A merge preserves
source rows under soft deletion. Transfers keep immutable card identity and scope sync delivery
marks per board (`board/25`). The former Board sync settings section is removed; publication
still runs automatically. Navbar Sign in and the Settings Account card use the existing device approval flow.

VIBE-26 adds hosted **Start work** for the Board owner. The active root polls outbound every ten
seconds, syncs the card and reuses the normal local launch service. No local listener or daemon
is added. See the [remote launch contract](../VibeRails/Services/Board/SYNC.md#vibe-26-remote-start-work).

For Board UI, API, SQLite, launch and MCP work, start with the
[Board contributor guide](../VibeRails/Services/Board/AGENTS.md) and
[architecture and VB-18 review](../VibeRails/Services/Board/ARCHITECTURE.md).
The Board uses `~/.vibe_rails/board.db`; its contracts live in `VibeRails.Data.Abstractions/Board`
and its store/migrations in `VibeRails.Data.Sqlite/Board`. REST and MCP share `BoardService`.
VIBE-6 adds `BoardSearchService` for dashboard search, link candidates, `search_board_cards` and
card recall: all local boards share current-text BGE/keyword retrieval with a bounded repository
preference. Titles, descriptions, Comments, legacy notes and handoffs feed a versioned passage
cache behind `IBoardStore`. Foreign cards have explicit board/project warnings and an authenticated
local-card editor that resolves ownership from immutable identities. Related-card links can cross
local projects; foreign links remain outside hosted publication. Moves, merges and repository
launches keep their existing project scope. See the [search contract](../VibeRails/Services/Board/AGENTS.md#local-board-search-vibe-6).
MCP discovers all local boards, with current-project boards first and other projects separately
labeled. Explicit board IDs and full permanent card keys select other projects; omitted targets
retain the launching card/current-project defaults. See the [local discovery contract](../VibeRails/Services/Board/ARCHITECTURE.md#local-mcp-discovery-vibe-28).
`GET /api/v1/board/cards/{card}/context` (root-only, both credentials) reports the context an agent
launched on a card would receive, as characters and estimated tokens (`BoardContextEstimator`);
each launch records the same measurement in `board.db` `BoardContextSamples` and as a `context`
Card Log entry that Board sync carries (VB-63).
The review records open concurrency and workflow findings; documentation is not evidence that
those findings have been fixed.
Card text can reference repository files as `@path` (VB-35): the text is the only storage,
`GET /api/v1/board/files?q=` (root-only) backs the composer's typeahead, and the launch prompt
lists the references next to linked commits and attachments.
Keep all Board persistence behind `IBoardStore`, including pending lane Automation events, so a
future shared API-backed store can replace local storage. Local Jobs and terminal history remain
in `state.db`; queuing an Automation run and acknowledging its Board event are separate commits.
Busy lane entries stay pending and retry in order per Automation, with durable cancellation and
skip reasons on the card. See [waiting lane Automations](../VibeRails/Services/Board/ARCHITECTURE.md#waiting-lane-automations-vibe-21)
for coalescing, concurrency and the shared UI/agent status contract.

### Services Layer

#### AgentFileService ([Services/AgentFileService.cs](../VibeRails/Services/AgentFileService.cs))
**Purpose**: Manage vc.rules.md rule files with rule definitions

**Key Methods**:
- `GetAgentFilesAsync()` - Scan repository for rule files
- `GetAgentFileRulesAsync(path)` - Parse rules from a specific rule file
- `CreateAgentFileAsync()` - Create a new rule file
- `AddRuleAsync()` - Add rule with enforcement level
- `UpdateRuleEnforcementAsync()` - Change enforcement level (WARN/COMMIT/STOP)
- `DeleteRulesAsync()` - Remove rules from a rule file

**Rule Format** (full contract: [Services/VCA/AGENTS.md](../VibeRails/Services/VCA/AGENTS.md)):
```markdown
# VibeRails Rules

## Vibe Rails Rules
- Cyclomatic complexity < 20 (COMMIT)

```

The heading must be `## Vibe Rails Rules` (`## Vibe Control Rules` is still read for older files).
The section ends at the next heading, and fenced code blocks like the one above are skipped — an
example of a rule is never itself a rule.

#### RulesService ([Services/RulesService.cs](../VibeRails/Services/RulesService.cs))
**Purpose**: Define available rules and enforcement logic

**Available Rules** (16 total):
1. `LogAllFileChanges` - Log all file changes
2. `LogFileChangesOver5Lines` - Log file changes > 5 lines
3. `LogFileChangesOver10Lines` - Log file changes > 10 lines
4. `CyclomaticComplexityUnder20` - Cyclomatic complexity < 20
5. `CyclomaticComplexityUnder35` - Cyclomatic complexity < 35
6. `CyclomaticComplexityUnder60` - Cyclomatic complexity < 60
7. `CyclomaticComplexityDisabled` - Cyclomatic complexity disabled
8. `RequireTestCoverageMinimum50` - Require test coverage minimum 50%
9. `RequireTestCoverageMinimum70` - Require test coverage minimum 70%
10. `RequireTestCoverageMinimum80` - Require test coverage minimum 80%
11. `RequireTestCoverageMinimum100` - Require test coverage minimum 100%
12. `SkipTestCoverage` - Skip test coverage
13. `PackageChangeDetected` - Package file changes
14. `CheckCommitMessageForWords` - Check commit message for
15. `FileLock` - File Lock('path to file')
16. `DirectoryLock` - Directory Lock('path to directory')

**Enforcement Levels**:
- `WARN` - Log warning, allow continuation
- `COMMIT` - Require explanation in commit/PR message
- `STOP` - Block the commit/PR

#### Repository ([Repository.cs](../VibeRails.Data.Sqlite/DB/Repository.cs))
**Purpose**: SQLite data access implementation (implements `IRepository`). See
[database reference](../VibeRails.Data.Sqlite/DB/AGENTS.md) for the full schema and operation reference.

**Key Methods** (selected):
- `GetOrCreateEnvironmentAsync(name, llm)` - Get/create environment record
- `SaveSandboxAsync(sandbox)` / `DeleteSandboxAsync(id)` - Sandbox persistence
- `CreateSessionAsync(...)` - Start new CLI session
- `LogSessionOutputAsync(...)` - Append terminal output to session log
- `GetRecentSessionsAsync(limit)` - Get recent CLI sessions

#### GitService ([Services/GitService.cs](../VibeRails/Services/GitService.cs))
**Purpose**: Git repository operations

**Key Methods**:
- `IsGitRepositoryAsync(path)` - Check if directory is git repo
- `GetGitRootAsync(path)` - Find repository root directory
- `GetCurrentBranchAsync()` - Get active git branch
- `GetCurrentCommitHashAsync()` - Get current HEAD commit hash
- `GetRecentCommitsAsync()` - Retrieve commit history
- `GetFileChangesSinceAsync(commitHash)` - Get file changes since a commit

#### SandboxService ([Services/SandboxService.cs](../VibeRails/Services/SandboxService.cs))
**Purpose**: Create and manage isolated git clone sandboxes for parallel AI workflows

**Key Methods**:
- `CreateSandboxAsync(name, projectPath, options)` - Shallow clone current branch, copy dirty/untracked files (unless `options.CopyDirtyFiles` is false), save to DB
- `DeleteSandboxAsync(sandboxId)` - Remove sandbox directory and DB record
- `TryDeleteSandboxAsync(sandboxId)` - Same, but returns false instead of throwing. Used when releasing an environment's workspaces, where a clone locked by a running CLI must not fail the caller.
- `GetSandboxesAsync(projectPath)` - List sandboxes for a project

**Creation Flow**:
1. Validate name (alphanumeric, hyphens, underscores only)
2. Check for duplicate name+project in DB
3. Get current branch and commit hash from source project
4. `git clone --depth 1 --branch {branch} --single-branch "{projectPath}" "{sandboxPath}"`
5. Parse `git status --porcelain` for all dirty/untracked files
6. Copy each non-deleted file to sandbox (skips `.vibe_rails/` paths) — **skipped entirely when `CopyDirtyFiles` is false**
7. Save sandbox record to DB

**Storage**: Global at `~/.vibe_rails/sandboxes/{name}` (not project-local)

#### RunWorkspaceService ([Services/Workspaces/RunWorkspaceService.cs](../VibeRails/Services/Workspaces/RunWorkspaceService.cs))
**Purpose**: Turn an environment's `WorkspaceMode` into the directory its CLI actually runs in

A sandbox and "clone fresh each run" are one mechanism at two retentions, so this service adds no
git handling of its own — it delegates every clone to `SandboxService` and owns only the naming,
reuse, and pruning decisions.

**Key Methods**:
- `ResolveAsync(environment, projectPath)` - The directory to launch in. Pure pass-through in `Project` mode; reuses the existing clone in `Persistent` mode; clones + prunes in `PerRun` mode. Returns a user-facing `Error` rather than throwing, so a failed clone becomes a launch that never started.
- `DetachAsync(environmentId)` - Unbind workspaces without deleting (workspace mode changed)
- `ReleaseAsync(environmentId)` - Orphan then best-effort delete (environment deleted)

**Two launch choke points call it** — `EnvironmentLaunchService.LaunchAsync` (covers the
Environments page *and* every Job/Worker run) and `TerminalTabHostService.StartSessionAsync`
(in-app tabs, resolved server-side so the browser keeps sending the project root).

**Retention**: `MaxRetainedPerRunWorkspaces` (3). Every run is a full working copy, so this is the
only thing between a nightly automation and a full disk.

#### Automation workflows ([Services/Jobs](../VibeRails/Services/Jobs))

An Automation is an ordered, fail-fast list of actions. An action is either a repository script
(`.py`, `.ps1`, or `.sh`) or a Worker Environment; a workflow may contain zero or one Worker and
up to 20 total actions. Existing Worker-only Automations migrate to a one-Worker action without
changing their behavior.

- `JobService` validates and normalizes the editor payload. Script and working-directory paths are
  persisted relative to the repository, arguments stay as discrete argv values, and saving pins
  the current script bytes with SHA-256. A state-only update never approves changed bytes.
- `AutomationScriptService` rejects escapes, network/device paths, links/reparse points, wrong
  extensions, oversized files/arguments, unavailable runtimes, and changed hashes. Execution uses
  explicit Python 3, PowerShell 7, or Bash/Git Bash interpreters; no user field is concatenated
  into a shell command.
- `JobStore` snapshots `JobActions` into immutable `JobRunActions` when a run is queued. Retry
  copies the original run snapshot, not the Automation's current edited definition.
- `JobLaunchService` opens Board lane runs and runs started from a card in Terminal tabs. Other triggers (including ordinary manual runs and retries) use native terminals. The retired launch-location preference is ignored.
  A tab exists only in the process that spawned it, so a Board run opens in the root whose project matches its `ProjectPath`:
  every open root claims its own project's queued Board runs each scheduler cycle, lease or not (`LaunchQueuedProjectRunsAsync`),
  while the lease holder handles native runs and falls back to opening another project's Board run in its own window only after
  `ForeignProjectBoardRunGrace` (30 s) passes unclaimed and no root of that project is alive (`JobProjectRoots`, refreshed every
  cycle, expires after a minute). Roots launch concurrently, so the `LaunchedUTC` row claim and the machine-wide terminal cap are
  one store transaction, `IJobStore.TryClaimLaunchAsync` (VIBE-2).
  Tab workflows run the same job process inside a recorded shell tab. Native script-only workflows use the neutral
  `IJobProcessLauncher`; workflows with a Worker use `EnvironmentLaunchService`, preserving the
  Worker's Project/Persistent/PerRun workspace resolution for every script in that workflow.
- `JobRunner` executes snapshots from top to bottom, records per-action status/output, stops at the
  first failure, and retains the existing global timeout, cancellation, overlap guard, scheduler,
  and Worker terminal-session replay behavior. Native script-only workflows also record their timed stdout/stderr
  into normal session history; Board recordings link to the originating card's Automations rail.
- Finished Automation tabs retain their final screen and up to 20,000 terminal scrollback lines
  for direct read-only viewing (VB-60). The PTY exits and recording finalizes normally. The
  existing snapshot API serves retained output; live snapshots still contain only the screen.
  Tabs remain until dismissed, root shutdown, or reclamation at the 100-tab cap. Replay remains
  available from history after dismissal. CLI-private full-screen history is not scrollback.

#### McpClientService ([Services/Mcp/McpClientService.cs](../VibeRails/Services/Mcp/McpClientService.cs))
**Purpose**: Custom MCP client service layer built on ModelContextProtocol NuGet package

**Architecture**:
- Wraps `ModelContextProtocol.Client.McpClient` with custom logic
- Provides builder pattern for configuration
- Connects to the in-process `/mcp` endpoint over HTTP (Streamable HTTP transport)

**Key Methods**:
- `ConnectAsync()` - Establish connection to MCP server
- `GetAvailableToolsAsync()` - List available MCP tools
- `CallToolAsync(name, args)` - Execute MCP tool with arguments

**Usage**:
```csharp
var transport = new HttpClientTransport(new HttpClientTransportOptions
{
    Endpoint = new Uri("http://127.0.0.1:{port}/mcp"),
    TransportMode = HttpTransportMode.StreamableHttp,
    AdditionalHeaders = new Dictionary<string, string> { ["viberails_session"] = sessionToken }
});
await using var service = await McpClientService.ConnectAsync(transport);
var result = await service.CallToolAsync("search_history", args);
```

### MCP Server (in-process)

The MCP server is hosted inside `vb.exe` over HTTP at `/mcp` (root backend only). There is no
separate process. Full design: [VibeRails/Services/Mcp/AGENTS.md](../VibeRails/Services/Mcp/AGENTS.md).

**Tools** (snake_case wire names): `validate_vca` (staged-file vc.rules.md rule validation),
`search_history` (semantic + keyword search over captured agent history via the real
`IUnifiedSearchService` — BGE/sqlite-vec/RRF), and the token-saver controls
`pause_token_saver`, `resume_token_saver`, `get_token_saver_status`.

Host shell command jobs (`run_shell_command`, `get_shell_command_status`, `cancel_shell_command`) and
web research (`web_search`, `web_fetch`) remain in the codebase but are **not currently exposed** as MCP
tools (security review 2026-07-02).

### Data Layer

#### Repository ([Repository.cs](../VibeRails.Data.Sqlite/DB/Repository.cs))
**Purpose**: SQLite data access implementation

**Database Tables** (see [database reference](../VibeRails.Data.Sqlite/DB/AGENTS.md) for the full reference):
- `Environments` - Environment configurations (global, not project-scoped)
  - `Id`, `CustomName`, `LLM`, `Path`, `CustomArgs`, `CustomPrompt`, `CreatedUTC`, `LastUsedUTC`, `Hidden`
  - `UNIQUE(CustomName, LLM)`
- `Sandboxes` - Sandbox git clones (project-scoped via ProjectPath)
  - `Id`, `Name`, `Path`, `ProjectPath`, `Branch`, `CommitHash`, `RemoteUrl`, `SourceBranch`, `CreatedUTC`
  - `UNIQUE(Name, ProjectPath)`
- `Sessions` - CLI session metadata
  - `Id` (TEXT PK), `Cli`, `EnvironmentName`, `WorkingDirectory`, `ProjectDisplayName`, `StartedUTC`, `EndedUTC`, `ExitCode`, `Processed`, `ExportedUTC`, `ParentSessionId`, `SessionDisplayName`, `OwnerPid`, `OwnershipTracked`, `JobRunId`
- `SessionLogs` - Terminal output logs
  - `Id`, `SessionId`, `Timestamp`, `Content` (BLOB), `IsError`
- `UserInputs` / `InputFileChanges` - User input tracking + correlated git diffs
- `TerminalSessionLogs` - Per-terminal-session structured log rows
- `Jobs` / `JobActions` / `JobTriggers` - Automation definitions, ordered actions, and triggers
- `JobRuns` / `JobRunActions` - Immutable per-run workflow snapshots and per-action outcomes/output
- Additional tables: `AgentMetadata`, `ChatSummary`, `sessionOutPut`, `TokenSavings`, `CompressionCaptures` (retired 2026-09-17, schema-only), `CodeAnalyzerIgnores`, `ProjectCache`, `GlobalCache`

**Configuration**:
- WAL mode enabled for concurrent access
- Foreign keys enforced
- Indexes on frequently queried columns (`StartedUTC`, `LastUsedUTC`, `ProjectPath`, etc.)

**Database Location**:
- Global application state: `~/.vibe_rails/state.db` (no per-project database)
- Board state: `~/.vibe_rails/board.db` (all projects; legacy Board tables in `state.db` are unused)

Follow the [database change policy](../AGENTS.md#database-change-policy) above. Normal startup applies
pending schema changes automatically, with backups for breaking steps and SQLite transaction
coordination; see the [database reference](../VibeRails.Data.Sqlite/DB/AGENTS.md).

### API Layer

#### Routes ([Routes/Routes.cs](../VibeRails/Routes/Routes.cs))
**Purpose**: REST API endpoint definitions (split across domain-specific modules in `Routes/`)

**Rule File Management**:
- `GET /api/v1/agents` - List rule files
- `GET /api/v1/agents/rules?path={path}` - Get rules from a rule file
- `POST /api/v1/agents` - Create a rule file
- `POST /api/v1/agents/rules` - Add rule
- `PUT /api/v1/agents/rules/enforcement` - Update enforcement
- `DELETE /api/v1/agents/rules` - Delete rules
- `GET /api/v1/rules` - List available rules

**Environment & CLI**:
- `GET /api/v1/projects/name` - Get project display name
- `GET /api/v1/environments/{name}/launch` - Get environment vars
- `POST /api/v1/cli/launch/{cli}` - Launch CLI in terminal
- `POST /api/v1/cli/launch/vscode` - Launch VS Code

**Sandboxes** (project-scoped):
- `GET /api/v1/sandboxes` - List sandboxes for current project
- `POST /api/v1/sandboxes` - Create sandbox (shallow clone + dirty files)
- `DELETE /api/v1/sandboxes/{id}` - Delete sandbox (removes directory + DB record)
- `POST /api/v1/sandboxes/{id}/launch/vscode` - Launch VS Code in sandbox directory

**Python scripts** (single-file pwsh `.ps1`, bash `.sh` or python `.py` scripts in `~/.vibe_rails/scripts`,
gated by PIN-backed hash pinning; the extension picks the interpreter):
- `GET /api/v1/python-scripts` - List scripts with signing status
- `POST /api/v1/python-scripts/pin` - Create or change the signing PIN
- `POST /api/v1/python-scripts/approve` | `/revoke` - Sign / unsign a script (PIN required)
- `POST /api/v1/python-scripts/run` - Run a signed script; `GET .../runs` for history
- `GET|POST /api/v1/python-scripts/content` - Read / write a script's text. GET returns a
  raw-content version; POST requires it as `expectedVersion` so a stale editor cannot overwrite
  a newer file. No PIN is accepted and a write can never create an approval.
- `POST /api/v1/python-scripts/create` | `/rename`, `DELETE /api/v1/python-scripts?name=` - File management
- `POST /api/v1/python-scripts/import` - Copy a regular UTF-8 file from a local disk into the
  scripts folder (root-dashboard backend only, like the filesystem picker; network/device
  paths and links are rejected)

**Nav Automation launcher** (the nav "Launch" flyout; preferences persist per install in GlobalCache):
- `GET /api/v1/automation-nav/preferences` - Catalog of the current project's automations
  (`job:{id}`) plus every signed-library script (`script:{name}`, with its signing `status`), each
  with its saved order and show/hide state
- `PUT /api/v1/automation-nav/preferences` - Save order/visibility; the body must be the full
  current catalog (400 when it no longer matches, e.g. an automation was renamed meanwhile)
- `DELETE /api/v1/automation-nav/preferences` - Reset the current catalog's entries to defaults

**Session Logging**:
- `GET /api/v1/sessions/{sessionId}/logs` - Get session logs
- `GET /api/v1/sessions/recent` - Recent sessions

**Automation workflows**:
- `GET|POST /api/v1/jobs`, `GET|PUT|DELETE /api/v1/jobs/{id}` - List/create/read/update/remove workflows
- `POST /api/v1/jobs/{id}/run` - Queue Run now through the same native-terminal scheduler as every trigger
- `GET /api/v1/jobs/runs`, `GET /api/v1/jobs/runs/{runId}` - Run history and per-action detail
- `POST /api/v1/jobs/runs/{runId}/cancel` | `/retry` - Cancel an active run or retry its immutable snapshot
- `GET /api/v1/jobs/catalog`, `POST /api/v1/jobs/import` - Root backend only. List every non-deleted Automation that belongs to another repository on this machine (grouped by repository, with its Worker, per-script found/missing flags and a suggested Worker name), and copy one into the current repository: missing scripts are copied from the source repository (existing files are never overwritten), the Worker is reused when one with the same name and CLI is visible here or cloned under a new name (steps copied, `{{step:id}}` tokens remapped), and the Automation is created disabled. One-way copy; the copy only records the id of
  the root Automation it came from (`Jobs.ImportedFromJobId`) so the catalog never offers a copy back while
  its origin still exists — an Automation imported into twenty repositories is not listed twenty times
  (VB-33). Once the origin is deleted, exactly one copy (the oldest) is offered per origin

**MCP Integration**:
- `GET /api/v1/mcp/status` - MCP server status
- `GET /api/v1/mcp/tools` - List MCP tools
- `POST /api/v1/mcp/inspect` - Inspect tools on the local or a user-supplied Streamable HTTP MCP endpoint
- `POST /api/v1/mcp/tools/{name}` - Call MCP tool

**Utility**:
- `GET /api/v1/context` - Project context (IsInGit, root path, git branch/remote, sandbox flag)
- `POST /api/v1/git/preflight/stream` - Stream staged-index Git Guard events as SSE

### Frontend Layer

#### app.js ([wwwroot/app.js](../VibeRails/wwwroot/app.js))
**Purpose**: Single-page application logic

**State Management**:
```javascript
const state = {
    currentView: 'agents',  // Current active view
    agents: [],             // Rule files list
    environments: [],       // LLM environments
    sessions: [],           // CLI sessions
    mcpTools: [],          // Available MCP tools
    selectedAgent: null,    // Currently selected agent
    selectedSession: null   // Currently selected session
};
```

**Key Functions**:
- `loadAgents()` - Fetch and render rule files
- `loadEnvironments()` - Fetch environment configurations
- `loadSessions()` - Fetch recent CLI sessions
- `createAgent()` - Create a new rule file
- `addRule()` - Add rule to agent
- `updateEnforcement()` - Change rule enforcement level
- `launchCli()` - Start LLM CLI session
- `callMcpTool()` - Execute MCP tool

**View Rendering**:
- Template-based rendering using hidden `<template>` tags
- Dynamic content injection with data binding
- Event delegation for dynamic elements

#### index.html ([wwwroot/index.html](../VibeRails/wwwroot/index.html))
**Purpose**: Main UI dashboard (Single Page Application)

**Structure**:
- Navigation sidebar with icons
- Main content area with view templates
- Modal dialogs for create/edit operations
- XTerm.js terminal integration for session logs

**Views**:
1. **Rule Files View** - Rule file management
2. **Environments View** - LLM environment configuration
3. **Sessions View** - CLI session history and logs
4. **MCP View** - MCP tool management and execution

## Design Patterns

### Dependency Injection
All services registered in [MapRegisterServices.cs](../VibeRails/MapRegisterServices.cs) with appropriate lifetimes:
- **Scoped**: Services tied to request lifecycle (Repository)
- **Singleton**: Long-lived services (GitService, RulesService, MCP settings)

### Repository Pattern
Storage contracts and their records live in `VibeRails.Data.Abstractions`; SQLite implementations
live in `VibeRails.Data.Sqlite`. New consumers request the focused `ISessionStore`,
`ISessionArchiveReader`, `IUserInputStore`, `IEnvironmentStore`, `ISandboxStore`, `IMetadataStore`,
`IChatSummaryStore`, or `IEmbeddingProgressStore` interface. `IRepository` remains a compatibility
aggregate, and its scoped aliases resolve to the same instance. Board, Automations, telemetry,
proxy archives, snapshots, retention, vector storage, and search use their own contracts.

`SqliteStorage` is the provider's composition entry point. Hosts supply file paths; the provider
owns connection strings, construction, and registration. `SqliteConnectionFactory` applies private
cache to file databases, foreign keys, and busy timeouts. `SchemaMigrations(Component, Version,
AppliedUTC)` records completed changes transactionally with each migration. Existing files receive
a one-time adoption pass; ordinary starts skip completed changes. The SQLite write lock serializes
migration ownership between processes, and failed changes remain pending.

**In-memory caching over the stores is rare on purpose (VIBE-27).** `state.db` and `board.db` are
written by several processes at once (root backends, tab children, `vb mcp` hosts, `--job-run`
children) and there is no cross-process change signal, so a process-wide cache of anything another
process writes — Job runs, lane-entry queues, card activity, picker preferences, environments —
would serve stale answers to the other windows. What is cached is data that cannot change once
seen: `SqliteSchemaFeatures` remembers, per store instance, that an optional table or column of
`state.db` exists (the Board store probes before every Jobs/Sessions read so a stdio host can open
a file that never held Automations; a missing feature is still re-probed each call, and the row
reads behind the probe stay live). A session's author was considered and left uncached: pruned
history must fall back to the card-session label on the same instance.
`IGlobalCache` / `IProjectCache` are scoped, so their dictionaries are per-request memos rather than
caches; the direct `IRepository` reads beside them are correct. `BoardSyncActivityCache` (hashes,
2 h) and `BoardFileIndexService` (repo file list, 10 s) are the other deliberate caches.

Git input recording is application orchestration in `UserInputRecordingService`; storage never
invokes Git. BERT inference stays in the application while SQL and vector writes live in the
provider. Small provider JSON contexts retain Native AOT support. Root-only maintenance retries
pending lexical-index writes and applies one-month state / seven-day proxy retention to completed,
export-acknowledged sessions.

### Service Layer Pattern
Business logic isolated from HTTP concerns. Services are reusable across CLI and web modes.

### Factory Pattern
`BaseLlmCliLauncher` base class with CLI-type-specific implementations:
- `ClaudeLlmCliLauncher` → `CLAUDE_CONFIG_DIR`
- `CodexLlmCliLauncher` → `CODEX_HOME`
- `AntigravityLlmCliLauncher` → (none — launch-flag-only)
- `CopilotLlmCliLauncher` → (none — launch-flag-only)
- `OpencodeLlmCliLauncher` → `XDG_CONFIG_HOME`

### Configuration Pattern
`Config` / `ParserConfigs` static classes manage runtime configuration paths and application state.

### Strategy Pattern
Different LLM CLI environments implement `IBaseLlmCliEnvironment` with specific configuration logic.

### Builder Pattern
`McpClientService` uses builder pattern for flexible client configuration.

## Configuration & File Locations

### Application Configuration
```
~/.vibe_rails/                    # Global config directory
├── state.db                        # Application state, Automations and terminal history (all projects)
├── board.db                        # Board state and pending lane Automation events (all projects)
├── config.json                     # Application settings
├── history/                        # CLI command history
├── envs/                           # Environment configurations
├── sandboxes/                      # Sandbox git clones (one dir per sandbox)
    ├── myenv/
    │   ├── claude/                 # Claude CLI config
    │   │   └── config.json
    │   ├── codex/                  # Codex CLI config
    │   │   └── config.json
    │   └── antigravity/            # Antigravity (agy) env dir — launch-flag-only, no config file
    └── production/
        └── ...
```

### Project-Level Configuration
```
project-root/
├── .git/
├── vc.rules.md                      # VibeRails rule files (nested ones scope to their subtree)
├── .vibe_rails/                  # Optional project-specific config (no per-project database)
└── src/
```

### Environment Variables (Terminal Session Mode)

**Claude**:
```bash
CLAUDE_CONFIG_DIR=~/.vibe_rails/envs/myenv/claude
```

**Codex**:
```bash
CODEX_HOME=~/.vibe_rails/envs/myenv/codex
```

**Antigravity (agy)**: none — agy is launch-flag-only, so VibeRails injects no
per-environment config env vars (sandbox/permissions are launch flags).

**Copilot**: none — Copilot is launch-flag-only (no config-dir env var), like agy.

**OpenCode**: `XDG_CONFIG_HOME=~/.vibe_rails/envs/myenv` — OpenCode resolves its standard
config/agents/commands/plugins directory at `$XDG_CONFIG_HOME/opencode`. VibeRails does not set
the additive `OPENCODE_CONFIG_DIR`, which would still merge the user's global config. Credentials
are NOT isolated because `XDG_DATA_HOME` remains unchanged (auth stays in the user's global
OpenCode data directory, normally `~/.local/share/opencode/auth.json`).

## Development Workflows

### Adding a New Rule

1. **Define rule in RulesService.cs**:
   - Add a new value to the `Rule` enum
   - Add an entry to the `_keyValuePairs` dictionary (display string)
   - Add an entry to the `_descriptions` dictionary (description)

```csharp
// In the Rule enum:
MyNewRule,

// In _keyValuePairs:
{ Rule.MyNewRule, "My new rule display text" },

// In _descriptions:
{ Rule.MyNewRule, "Description of the rule" },
```

2. **Update vc.rules.md files**:
```markdown
## Vibe Rails Rules
- My new rule display text (COMMIT)
```

3. **Implement enforcement logic** in appropriate service

4. **Update frontend** to display new rule option

### Adding a New LLM CLI Support

1. **Create environment class** implementing `IBaseLlmCliEnvironment`:
```csharp
public class MyLlmCliEnvironment : BaseLlmCliEnvironment
{
    protected override Dictionary<string, string> GetEnvironmentVariables()
    {
        // Return env vars for this CLI
    }
}
```

2. **Register in [MapRegisterServices.cs](../VibeRails/MapRegisterServices.cs)**:
```csharp
builder.Services.AddSingleton<IMyLlmCliEnvironment, MyLlmCliEnvironment>();
```

3. **Update LLM enum** in [VibeRails.Data.Abstractions/DTOs/LLM.cs](../VibeRails.Data.Abstractions/DTOs/LLM.cs)

4. **Add launcher logic** in [Services/LlmClis/LaunchLLMService.cs](../VibeRails/Services/LlmClis/LaunchLLMService.cs)

5. **Update frontend** to support new CLI option

### Adding a New MCP Tool

1. **Create tool class** in [VibeRails/Services/Mcp/Tools/](../VibeRails/Services/Mcp/Tools/):
```csharp
[McpServerToolType]
public class MyCustomTool
{
    [McpServerTool, Description("Tool description")]
    public static string MyTool([Description("…")] string input) => /* … */;
}
```
   (Use an instance class with constructor injection if the tool needs app services —
   see `SessionSearchTool`.)

2. **Register in [MapRegisterServices.cs](../VibeRails/MapRegisterServices.cs)**: add
   `.WithTools<MyCustomTool>()` to the `AddMcpServer()` chain (and `AddScoped<MyCustomTool>()`
   if it's an instance tool).

3. **Add a test** in [Tests/Services/Mcp/](../Tests/Services/Mcp/).

> MCP exposes the method as snake_case (`MyTool` → `my_tool`); that's the name callers use.

### Testing Changes

#### Run Unit Tests
```bash
cd Tests
dotnet test
```

#### Run with Code Coverage
```bash
dotnet test /p:CollectCoverage=true
```

#### Integration Testing
```bash
# End-to-end PTY / terminal / agent-flow integration project
dotnet run --project IntegrationTest
```

## Building & Deployment

### Development Build
```bash
dotnet build
```

### Release Build (Native AOT)
```bash
dotnet publish -c Release
```

### Cross-Platform Builds
```powershell
# Windows (local AOT) + Linux (AOT via Docker) — single script handles both
.\deploy\build.ps1
```

### Docker Build
Project includes Docker support configured for Linux target OS.

## Common Tasks for AI Agents

### Task: Find all rule files in repository
```csharp
// Use: AgentFileService.GetAgentFilesAsync()
var agentFiles = await agentFileService.GetAgentFilesAsync();
```

### Task: Add rule to a rule file
```csharp
// Use: AgentFileService.AddRuleAsync()
await agentFileService.AddRuleAsync(
    agentPath: "/path/to/vc.rules.md",
    ruleName: "Cyclomatic complexity < 20",
    ruleValue: "20",
    enforcement: "COMMIT"
);
```

### Task: Launch Claude CLI with environment
```bash
vb --env claude                    # Base CLI, default config
vb --env production                # Custom environment (looked up in DB)
vb --env claude --workdir /project # With explicit working directory
```

### Task: Retrieve session logs
```csharp
// Use: Repository.GetSessionWithLogsAsync()
var session = await repository.GetSessionWithLogsAsync(sessionId);
```

### Task: Call MCP tool
```csharp
// Use: McpClientService.CallToolAsync()
var result = await mService.CallToolAsync(
    "search_history",
    new Dictionary<string, object> {
        ["query"] = "find similar code"
    }
);
```

### Task: Add custom MCP tool
See "Adding a New MCP Tool" above: add a `[McpServerToolType]` class under
`VibeRails/Services/Mcp/Tools/`, chain `.WithTools<…>()` in `MapRegisterServices.cs`, and test it.

## Troubleshooting

### Common Issues

**Issue**: Rule files not found
- **Cause**: Not in git repository or no vc.rules.md in the repo
- **Solution**: Run from git repository root, create a vc.rules.md file

**Issue**: LLM CLI not launching
- **Cause**: CLI not in PATH or incorrect environment configuration
- **Solution**: Verify CLI installation, check environment variables

**Issue**: Session logs not recording
- **Cause**: Database connection issue or insufficient permissions
- **Solution**: Check `~/.vibe_rails/` directory permissions, verify SQLite access

**Issue**: MCP tools not available / Explorer can't connect
- **Cause**: `/mcp` is auth-gated (needs the `viberails_session` token) and is hosted only by the
  root backend (not terminal-tab children).
- **Solution**: Hit `/mcp` from the dashboard (the Explorer forwards the session token); confirm
  you're talking to the root backend's port. See [VibeRails/Services/Mcp/AGENTS.md](../VibeRails/Services/Mcp/AGENTS.md).

### Debug Logging

The hidden logo triple-click modal is **Internal tools**, with About, Data uploads, and Logs
tabs. `Services/Diagnostics/FeatureLogService` is a root-backend-only, bounded asynchronous
JSONL journal, separate from Serilog and terminal transcripts. Callers explicitly opt in through
`IFeatureLog.Write(feature, eventName, message, operationId, subject, status, level)`; currently
both data-export services record attempts and outcomes under `data-upload`. Keep one operation
ID through an action and use safe metadata only. Files live in `logs/features` beside `state.db`.
`GET /api/v1/internal/logs` reads filtered retained events; `/api/v1/internal/uploads` groups
each attempt to its latest event before applying filters. The Logs route accepts `source=features`
(API default), `application`, or `daemon`. `DiagnosticLogReader` reads bounded tails of existing
Serilog `logs/vb-*.log` and `logs/vbd-*.log` files on demand, with a two-second cache and no
writer changes. The UI defaults to Application logs; upload drill-down selects the Feature journal.
Serilog writes those application and Demon files at Information and above, so a scheduler cycle
that enqueued, launched or reaped work leaves a record. A Warning-only file sink was tried and
reverted: it made a healthy run and a dead scheduler look identical on disk, and it hid the only
audit line recording that executables in `~/.vibe_rails` had been replaced.
`builder.Logging.SetMinimumLevel` does not affect these files — the host clears MEL providers and
the file sink is the static Serilog logger in `Program.cs`.
Routes are authenticated and mapped
only on active root backends. See [internal-tools.md](internal-tools.md) for
the extension recipe, retention, status semantics, and best-effort persistence limits.

## Contributing Guidelines

### Code Style
- Follow C# naming conventions (PascalCase for public, camelCase for private)
- Use nullable reference types consistently
- Prefer async/await over blocking calls
- Add XML documentation comments for public APIs

### Testing Requirements
- Write unit tests for new services
- Maintain >80% code coverage
- Include integration tests for API endpoints
- Test MCP tools independently

### Pull Request Process
1. Create feature branch from `main`
2. Implement changes with tests
3. Update documentation (this file if architecture changes)
4. Run `dotnet test` to verify all tests pass
5. Submit PR with clear description

## Security Considerations

### Web UI Authentication
VibeRails implements production-grade cookie-based authentication to prevent unauthorized localhost access:

**One-Time Bootstrap Code Flow:**
- On startup, server generates a cryptographically secure 256-bit bootstrap code (32 bytes, URL-safe base64)
- Code is printed in console: `http://localhost:PORT/auth/bootstrap?code=...`
- Code expires in 2 minutes and is single-use only
- First browser to use the code gets authenticated and code is consumed
- Subsequent attempts with same code receive 403 Forbidden

**Session Cookie Security:**
- 512-bit session tokens (64 bytes) generated per app instance
- `HttpOnly` flag prevents JavaScript access (XSS protection)
- `SameSite=Lax` blocks cross-site attacks from evil.com
- Constant-time comparisons prevent timing attacks
- Cookies validated on every request via middleware

**Attack Mitigation:**
- **Port Scanning Attack**: Evil.com can detect the server but cannot authenticate (requires bootstrap code)
- **Cookie Theft**: HttpOnly + SameSite prevents JavaScript access and cross-origin sends
- **Replay Attack**: Bootstrap codes are single-use and expire quickly
- **Timing Attack**: Constant-time comparisons via `CryptographicOperations.FixedTimeEquals()`

**VSCode Extension Compatibility:**
- Extension webview bypasses cookie auth (different origin: `vscode-webview://`)
- Origin-based trust model (VSCode is local, trusted application)
- Health check endpoint `/health` (the only deliberately unauthenticated route) allows extension/parent-process startup verification

**Browser Launch:**
```bash
vb        # Launches the dashboard and opens the browser
vb --web  # Explicit web-dashboard launch
```

### Input Validation
- All file paths validated to prevent directory traversal
- Automation scripts must be regular repository-contained `.py`, `.ps1`, or `.sh` files; saved
  SHA-256 hashes are rechecked in the resolved run workspace before execution
- Automation arguments are bounded argv elements and are never joined into a shell command
- SQL queries parameterized to prevent injection
- Rule names and values sanitized before file write
- MCP tool arguments validated before execution

### Environment Isolation
- Each environment has isolated configuration directory
- No cross-environment data leakage
- Session logs stored securely with proper permissions

### MCP Security
- `/mcp` is bound to localhost and gated by `CookieAuthMiddleware` (session token required)
- Hosted only by the root backend, not terminal-tab child processes
- Input validation on all MCP tool calls

### Process Security
- Terminal session mode uses pseudo-terminal (PTY) for safe terminal emulation
- Automation script runtimes and VibeRails child launches use explicit executables and argv; shell
  text is never constructed from script paths or arguments
- Terminal output filtered before database storage

## Performance Considerations

### Database
- WAL mode for concurrent read/write access
- Indexes on frequently queried columns (`LastUsedUTC`)
- Connection pooling via `Microsoft.Data.Sqlite`
- Batch session log writes for performance

### Frontend
- Lazy loading of session logs (fetch on demand)
- Debounced search inputs
- Virtual scrolling for large lists (XTerm.js for logs)
- Minimal DOM manipulation

### Native AOT
- Ahead-of-time compilation for faster startup
- Reduced memory footprint
- No JIT overhead
- Smaller deployment size

### MCP Performance
- In-process HTTP — no separate process to spawn, no IPC; rides the dashboard's Kestrel
- Tools executed within the running web host

## Future Enhancements

### Planned Features
- [x] Rule enforcement automation (pre-commit and commit-msg hooks)
- [ ] Multi-project workspace support
- [ ] Remote session sharing
- [ ] Advanced MCP tool development (RAG, code analysis)
- [ ] Plugin system for custom rules
- [ ] Team collaboration features
- [ ] Cloud synchronization
- [ ] MCP tool marketplace

### Technical Debt
- [ ] Expand CLI loop functionality (currently minimal)
- [ ] Add comprehensive integration test suite
- [ ] Improve error handling and user feedback
- [ ] Add telemetry and analytics
- [ ] Optimize database queries for large session logs
- [ ] Add retry logic for MCP server connection failures

## Resources

### Documentation
- [ASP.NET Core Documentation](https://docs.microsoft.com/aspnet/core)
- [Model Context Protocol Spec](https://modelcontextprotocol.io/)
- [ModelContextProtocol NuGet Package](https://www.nuget.org/packages/ModelContextProtocol)
- [Pty.Net](https://github.com/microsoft/vs-pty.net) (inlined fork, ConPTY only)
- [PyBridge](https://github.com/robstokes857/PyBridge) (in-tree library)
- [XTerm.js Documentation](https://xtermjs.org/)

### Related Projects
- **Claude CLI** - Anthropic's Claude command-line interface
- **Codex CLI** - OpenAI Codex command-line tool
- **Antigravity CLI** - Google Antigravity command-line interface (`agy`)
- **MCP SDK** - Model Context Protocol development kit

### Key Dependencies
- **Microsoft.Data.Sqlite** (v10.0.11) - SQLite database access
- **ModelContextProtocol** (v2.0.0) - MCP foundation
- **ModelContextProtocol.AspNetCore** (v2.0.0) - ASP.NET Core integration for the in-process MCP server
- **Pty.Net** (inlined fork) - Pseudo-terminal support
- **PyBridge** (in-tree library) - Python script execution, streaming, and warm worker sessions

### Board display IDs (VB-69)

The Board now shows mutable display IDs, defaulting to a four-character repository prefix plus
a persistent number. Board settings customize the prefix for future cards; Card settings can
rename a label. Immutable CardKey and row Id remain the identity used by sync and stored links.
The additive board/20 migration preserves existing displayed keys. See the Board contributor
and sync contracts for collision correction. New-card link selections persist atomically with
creation, and discussion launch controls are separate from Start work.

## Agent completion and Automation lifecycle

See [VIBE-9 in the Board architecture](../VibeRails/Services/Board/ARCHITECTURE.md#vibe-9-agent-completion-and-automation-lifecycle-2026-09-29)
for `complete_board_agent`, `get_board_agent_status`, optional Automation descriptions, clickable
running-agent indicators, and root-owned cleanup of finished Automation terminals. Final agent
reports live behind `IBoardStore` in `board.db`; process/run outcomes and recordings stay in
`state.db`. Completion reports never bypass remaining workflow actions or recording finalization.

## Shared session replay (2026-09-30)

`wwwroot/session-replay/viewer.mjs` mounts the shared isolated viewer used by desktop
history, Board, Automations and VibeRails-Front's uploaded recording page. Its component
README documents adapters, lifecycle, responsive playback and syncing the static copies.
Browser embeds use `srcdoc`; VS Code desktop loads its same-origin empty frame shell
before writing the template so its resource service worker can route assets. The
replacement document retains the webview CSP, and disposal cancels either load phase.
The desktop root maps seven read-only `/api/v1/session-replay` routes behind existing
session/tab authentication. `IReplayStore` lives in Data.Abstractions, its ReadOnly SQLite
provider in Data.Sqlite/Replay, and Board labels remain behind IBoardStore. No schema or
capture change is involved. The website adapts its existing authenticated envelope in the
browser. At mobile widths the replay displays delayed HTML snapshots from the reconstructed
terminal; live terminal rendering is independent.

## Card check evidence (VIBE-24)

Code quality and VCA are ordered Automation actions beside the optional single Worker.
`BoardCheckService` calls the individual preflight engines without the Automation-enqueuing
pipeline. `IBoardStore` retains attempts, provenance and reports in additive `BoardChecks`.
`BoardChecksReader` combines saved evidence with per-card run state; `read_board_check` supplies
reviewer evidence, and the Checks panel reuses the existing CodeReportViewer. No scan result
moves a card. See [scope, persistence, status and starter contracts](../VibeRails/Services/Board/CHECKS.md).
