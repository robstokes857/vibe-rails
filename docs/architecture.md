# VibeRails architecture reference

## Corporate gateways for Codex and Claude

`LlmProxySettingsService` resolves each destination from the saved Enterprise Network Proxy,
then the backend's inherited `OPENAI_BASE_URL` / `ANTHROPIC_BASE_URL`, then the provider default.
Managed launches preserve their original upstream base in provider-specific environment metadata,
using an explicit marker for the public-provider default. Nested backends resolve an inherited
loopback `/llm/` URL through that metadata instead of recursing or losing the corporate destination.
Missing/invalid metadata and invalid external destinations fail closed. Both saved and inherited gateways use the ordinary authenticated local
relay and the provider's TokenSaver setting. A saved gateway plus credential overrides older
Codex provider configuration and Claude environment URLs. Without that complete choice, those
custom configurations retain their routing; explicit Codex launch profiles/providers also retain
their routing.

The Settings API accepts write-only `CodexGatewayBearerToken` / `ClaudeGatewayBearerToken` edits,
returns presence flags, and stores credentials in the existing private settings file. Tokens are
active only with an explicitly saved gateway, never with an inherited URL. The CLI gets a local
placeholder; the relay adds the real bearer token after compression and strips conflicting
provider credentials/account headers. Authentication supports Bearer or a raw API key in a
configurable header (`api-key`, `x-api-key`, or another valid non-reserved name). The existing
`*GatewayBearerToken` storage keys also hold raw API keys for compatibility. Clearing a gateway
leaves its credential dormant. No separate TokenSaver server is contacted: the relay runs its
body transform before sending the HTTP request, and emits activity even for zero savings. See
[TokenSaver](../TokenSaver/README.md#enterprise-network-proxy) and [API security](../API_SEC.md).

Settings → LLMs keeps setup to one step without changing that contract
(`settings-enterprise-gateway.js`). A credential typed while the URL is blank fills in the
inherited URL, shown in the route line. The **Anthropic API key** and **OpenAI API key** choices
fill `https://api.anthropic.com` + `x-api-key` and `https://api.openai.com/v1` + Bearer. Either
way the server receives an explicit URL and an ordinary auth mode. Claude launches behind a gateway
keep `ENABLE_TOOL_SEARCH=true` only for api.anthropic.com or with `ClaudeGatewaySupportsToolSearch`
(default off). A gateway 400 mentioning `tool_reference` logs a Warning naming that switch. The
`llm-proxy` HttpClient alone uses `CreateLlmProxyHttpMessageHandler`, which offers the Windows
account to an authenticating corporate proxy (NTLM/Negotiate/Kerberos, on challenge only).

## Incremental token-savings publishing (VIBE-215)

The existing byte-based `TokenSavings` tally remains unchanged. While a root backend is open,
`TokenSavingsPublishJob` sends increments to hosted `POST /api/v2/token-savings`, using the
configured API key. `ITokenSavingsPublishStore` keeps a stable random computer identifier,
acknowledged token position and one pending report in `state.db`. Its additive schema is
automatic and compatible with old versions.

First activation baselines existing local history. Each subsequent batch is at most 100 million
tokens and is persisted before sending. Only a matching report-id/amount receipt advances the
checkpoint. A failed request, restart, key rotation or rename retries that exact report, including
its original name; later savings form a later batch. Root processes share a cross-process publish
lock. A decreased/reset tally rebaselines after any pending report completes. Old clients keep
using v1; this client never falls back to reposting lifetime totals. Cloned state, mixed old/new
binaries and exact historical attribution are not reconciled.

## Session sharing (VIBE-35)

Terminal Share creates a public replay link through the protected root API. The existing drain
job prioritizes completed recordings with durable requests pinned to the creating API key;
already-exported sessions can be sent to the link's account without discarding prior evidence.
The `create_session_share_link` MCP tool reaches the same root route using inherited terminal
identity and scoped root tool credentials. It returns a `vibe-share:<url>` commit line. The optional VCA rule
`Require VibeRails session link` defers until commit-msg, checks those lines before scanning other
message URLs, and applies the existing enforcement levels without a network lookup. The rule
checks presence/URL shape only; upload readiness, session ownership and link expiry are not verified.
See the [sharing contract](../VibeRails/Services/Integrations/VibeCodeRemote/SessionSharing.md)
for boundaries, account changes, queue/retry behavior and server-first rollout.

## Board editor refresh (VIBE-44)

The Board refreshes lane membership and card summaries every ten seconds while visible, and on
window focus/visibility return. It preserves loaded page depth, scroll positions and editor drafts;
stale reads are canceled or discarded. Previous work and Code quality/VCA summaries live in the
card sidebar. Description is always editable without previews; rendered Comments always use
Markdown and offer agent filtering with attention entries first. See the
[Board contract](../VibeRails/Services/Board/ARCHITECTURE.md#card-editor-and-local-refresh-vibe-44).

In VS Code, `WebviewPanelManager` forwards panel visibility on `onDidChangeViewState` and
dashboard readiness. `UiVisibility` combines that state with `document.hidden` behind
`app.isUiHidden()` / `app.onUiVisible()`: Board, Jobs, card checks, lanes and the script
workbench pause background reads and refresh on return. Browser focus refresh still works;
terminal transport and lifecycle continue independently.

## Card recall (VB-13)

Board recall adds structured handoffs, curated file references, exact project-scoped card lookup,
BGE/keyword card discovery and paged linked-session discussion. Chat supports an optional question
and may coexist with implementation without changing lanes. See the
[component contract](../VibeRails/Services/Board/ARCHITECTURE.md#card-recall-vb-13) for storage,
retrieval budgets, incremental indexing and launch behavior.

## Complete backups (VIBE-29, October 2, 2026)

Automatic whole-installation Azure snapshots were retired on October 20; existing archives
and their [read/restore contract](../VibeRails/Services/CompleteBackups/README.md) remain.
Session exports and the hosted Board UI sync continue independently.

`BoardDataBackupJob` now captures every retained Board table through `IBoardStore` and
uploads queryable row revisions plus deduplicated full-content parts to the independent
PostgreSQL `BoardDataBackups` and `BoardDataBackupSnapshots` tables. It includes unpublished
boards and deleted cards and is independent of the hosted UI's deletion lifecycle. It runs
only in an open root backend, with account-scoped staging, retries and verified complete
snapshot receipts. It does not write to Azure or restart the retired whole-installation job.
See the [Board data-backup contract](../VibeRails/Services/Board/DataBackup/README.md) for exact
coverage, fifteen-minute capture cadence, retention, SQL access and server-first rollout.

## Vibe AI navigation

The Vibe AI UI is hidden: its navigation link and view loader are removed, and old
`?view=vibe-rails-ai` links fall back to Terminals. The controller and template are retained
for future use. There is no visibility setting. The retired `ShowVibeAiUi` key stays in the
shared settings file for older versions. Settings updates ignore a requested value and
preserve the stored one, startup leaves it unchanged, and responses always report `true`.

## Optional Vibe Story tracking

Board tiles use an 11rem minimum height and 252px lane floor. Their thumbtack button saves a
local `BoardCards.Pinned` marker through `PUT /api/v1/board/cards/{card}/pin`. Activity promotion
fills only unpinned slots, so edits, comments and updates to other cards preserve pins. Explicit
dragging and editor lane changes still move pinned cards; attention grouping remains authoritative.
`board-card-pins/1` adds the nullable marker automatically, preserving older writers and stored data.

General Settings exposes **Vibe Board nudge — built-in CLIs** (on by default) and
**Vibe Board nudge — Quick Launches** for saved setups launched without a message of their own (off by default). The existing
`CreateVibeStoryTracking` setting keeps the base choice; `CreateVibeStoryTrackingCustomEnvs` is
independent and defaults off in new and older settings files. Nullable API updates preserve
each saved choice when an older client omits its field.
For managed LLM launches with no nonblank initial message or continuation summary, `CommandService`
reads the shared settings file and uses optional Board tracking guidance as the default prompt.
User context is one system of four levels (VIBE-229), broadest first: **App context** (every LLM
launch, any project or none), **Board context** (launches for one board), **Card context** (agents
opened from any card on the board) and **Card type context** (a type's default, replace or append
override). `ContextLevels` resolves and formats them into one labelled block; empty levels send
nothing and each level is limited to 4,000 characters. App and Board context apply only when the
launch's nudge switch is on; Card and Card type context always reach card agents. Storage is
additive: App context is the single-row `BoardAppContextSettings` table (`board-app-context/1`,
GET/PUT `/api/v1/board/app-context`), Board context is still the Every Context columns of
BoardContextSettings (`board-every-context/1`, GET/PUT `/api/v1/board/boards/{boardId}/every-context`)
and Card/Card type context is still the Agent context JSON; each has its own revision. A card
launch composes the whole block into its prompt in the root and sets `StartTerminalRequest.ContextIncluded`
so the tab child adds nothing more. A card-triggered Automation Worker is a card agent too: `CliLoop`
composes all four levels into its startup prompt (App and Board under the saved-environment switch)
and passes `contextIncluded` the same way. Every other managed launch (Agents, card-less Automations,
continuation summaries, explicit tasks) gets App and Board context prepended by `CommandService` when
its switch is on. Browser launches carry the selected board; native launches without a selection use the first
existing project board. The ready-only tracking guidance remains a fallback for sessions without a
task. The card context estimate, launch samples and `get_board_card` render the same block as the
launch. `get_board_card` picks the Nudge switch from the calling session's recorded environment
(`Sessions.EnvironmentName`), because a session attached through MCP has no link selection; other
readers see what the card's saved selection would get. Existing task/summary precedence and provider argv conventions are preserved. The guidance asks agents to use their discretion, reuse an existing story and
record progress/results and session history when useful. A guidance-only launch replies only that
the agent is ready to help, without tools, work, or an invented request, then waits for a user task.
The prompt names that exact reply because a bare "wait" left agents to fill the forced first turn;
on 2026-10-11 a Claude session wrote a Board request in the user's voice. Plain shells are unaffected; toggling applies to new sessions and does not grant Board tools
or change the CLI's approval policy. Tracking uses the existing Board MCP tools and storage.
Focused browser save/reload coverage: from `UITests`, run
`npx playwright test --config playwright.board-nudge.config.js` (static frontend with mocked APIs).

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

`ConversationTitleService` makes one leased title request per conversation through
`IConversationTitleStore` in `state.db`: when the user has written enough, or when the session ends.
It sends the user's captured messages (`IUserInputStore`), not the Send-to transcript, so launch
prompts such as the story-tracking nudge never reach the title API. A Board-linked conversation is
titled too; its primary card's display ID, title and description go first, labeled as background.
`ConversationTitleClient.ComposeRequest` redacts each part whole, then bounds it. The VIBE-158
refresh and its `LastExcerptHash` are retired; the column stays, unused. The sidebar shows a
generated title ahead of the card label, and the card keeps its own row.

The history API reads session pages from `state.db`. `ChatHistoryService` then obtains current
card labels for those session IDs through `IBoardStore.GetSessionCardsAsync`, which batches reads
from `board.db` and includes primary/additional attachments without deleted cards. The existing
history endpoints retain their global local-session scope and session-plus-tab authentication.
No cross-database SQL join, schema migration or historical-data rewrite is needed. The sidebar
uses these labels for automatic titles and card metadata. Its provider/folder filters intersect
with a selected immutable card ID; the shared Board `Find a card` picker searches all local boards
before selection, independently of loaded history pages. See the
[frontend contract](../VibeRails/wwwroot/AGENTS.md#chat-history-sidebar).

Send To returns those card labels alongside the recap for linked-card navigation. At terminal
launch, `SessionResumeService.AppendBoardContextAsync` reads the source session's current cards
again and appends stable keys/row IDs, display labels, local card references and VibeRails MCP
retrieval/review guidance after the edited or generated recap. References remain outside the remote
summary request and cached recap. The recap retains its 6,000-character limit and the complete
launch prompt keeps the normal cap; no cards are silently omitted. This read-only enrichment
does not attach the new session or change tool authorization.

## Project Overview

**VibeRails** is a sophisticated desktop/web application for managing and enforcing coding standards across AI-powered development workflows. It serves as a unified control panel for multiple LLM CLIs (Claude, Codex, Antigravity) with comprehensive rule enforcement, session logging, and MCP integration.

**Live Site**: [https://viberails.ai/](https://viberails.ai/)

### Core Capabilities
- **Rule File Management** - Create and manage `vc.rules.md` files with customizable coding rules
- **Rule Enforcement** - Define standards with three enforcement levels (WARN/COMMIT/STOP)
- **Multi-LLM Support** - Unified interface for Claude, Codex, Antigravity, Copilot, and OpenCode CLIs
- **Agents and Quick Launches** - A saved CLI setup (model, arguments, settings, workspace) is a Quick Launch in the terminal picker; with a starting message it is an Agent that runs on demand, on a schedule, around commits or from a Board lane. Both are `Environments` rows
- **Workspace clones** - An Agent or Quick Launch can run in its own git clone or a fresh clone each run (`Sandboxes` rows; no UI of their own since 2026-10-11)
- **Session Logging** - Track and monitor all CLI session history and outputs
- **Automations** - Ordered workflows of repository `.py`, `.ps1`, and `.sh` scripts and checks (VCA, Code quality) with no LLM step, on a schedule, a commit trigger or a Board lane; they run while a VibeRails root backend is open
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
│       │   ├── environment-controller.js # Saved CLI setup form (Agent / Quick Launch) + launch
│       │   ├── launch-kind.js          # Agent / Automation / Quick Launch display rule
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
- The extension starts its root backend after a trusted workspace opens (`viberails.autoStart`, default true), without creating a dashboard panel. The sidebar uses backend readiness independently of panel visibility. Closing the panel preserves the backend; Stop, dashboard Exit, and extension shutdown stop it. No OS service or startup registration is installed.
- Prints a one-time bootstrap URL for the extension host; automatic and manual starts share its immediate token exchange and health check
- Uses the same authenticated web/API backend as browser mode
- The Explorer/editor-tab **Add to VibeRails Scripts…** command hands a selected `.ps1`,
  `.sh` or `.py` path to the dashboard after a readiness/receipt handshake. The shared
  `PythonScriptsController.importScript` shows the existing registration form and calls the
  authenticated root-only import route. It opens existing visible entries by ID and leaves new
  entries unsigned. A cold backend start uses the selected file's workspace folder; a running backend
  keeps its project and offers Global scope for files outside it.

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

Agent self-close (`end_agent_session`, VIBE-78) follows PTY exit and final output drain, appends
the closure reason as the final recording-only raw/replay frame, and finalizes the session.
After environment post-exit steps, the child publishes `agent_session_closed`. The parent checks
the exact session under the tab's start/close gate, removes the host, and publishes
`agent_terminal_closed`; the frontend removes that tab and shows the reason outside xterm.
Ordinary exits stay open. Session recordings and Board links retain their existing replay paths.
Disposal bounds reader joins and quiesces dispatch under the subscriber lock if a provider keeps
its read blocked, so no late bytes follow the recording footer. A transient child-status failure
retains the closure request for retry; every attempt checks the original child and session again.

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
Renders <optgroup> Built-in CLIs + <optgroup> Quick Launches (+ Agents for the Board)
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

The top navigation shows **Sign in** only without a saved API key. General Settings
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

### PR review tracker

The **Reviews** page (`pr-reviews` view) and six MCP tools keep a lead-review log for the
configured GitHub repositories, falling back to the current project's `origin` when the watch
list is empty. Settings > PR Reviewer edits the machine-wide watch list one repository at a
time with immediate-save Add/Remove actions in `pr_reviews.db`
(`PrReviewerRepositories`, `pr-review-repositories/1`, additive); no checkout switch is required.
List, queue and lifecycle sync aggregate watches; each PR/pass/queue entry identifies its
repository, and single-PR REST/MCP actions require repository scope with multiple targets.
Identifiers are validated and deduplicated ignoring case; removing watches retains history. Records live in `pr_reviews.db`
(`pr-reviews/1`) keyed by `owner/name` and PR number. `PrReviewService` owns the rules (derived
status, server-owned timestamps, idempotent start, draft checkpoints), `PrReviewQueue` picks the
next PR by rank then age, and `GitHubPullRequestClient` reads open PRs, lifecycle states and a
numbered PR's current head through the user's own `gh`, pinned to github.com. Settings > PR
Reviewer enables new queue/start work and outbound publishing together (off by default, shared
across projects). Completion atomically captures an immutable delivery snapshot in
`PrReviewDeliveries` (`pr-review-delivery/1`); blockers publish real REQUEST_CHANGES reviews,
other feedback COMMENT, using the authenticated user's identity and reviewed full SHA. Delivery
states/receipts stay separate from local results. SQLite reserves dispatch across processes;
unknown POSTs only reconcile and never resend. Turning off stops new work/reservations while
existing local work and reserved posts may finish. Detail-page starts capture the current head; local form drafts survive other detail
actions, with inputs locked until a save and re-read finish. Routes are root-only under
`/api/v1/pr-reviews`. Contract: [PR Reviews guide](../VibeRails/Services/PrReviews/AGENTS.md);
security notes: [API_SEC.md](../API_SEC.md#github-pull-requests-and-review-publishing-pr-reviews).

All hosts/configurations use the same dedicated file through `IPrReviewStore`. Automatic
legacy adoption reads `state.db` without changing it and atomically records per-PR fingerprints
and stable review-id mappings. Later older-version writes adopt when the target PR has not
diverged; conflicting authored edits archive both copies and offer inspection/resolution in
Reviews without blocking other operations. Lifecycle refreshes do not count as authored edits.
Resolution uses optimistic content tokens (409 on stale choices). Unchanged legacy
rows cannot overwrite local edits or resurrect deletions. See the guide for this compatibility
boundary and the independent generation-1 schema.

### Review requests before a commit (VB-50)

A review step earlier than a pull request: the developer asks a named person to look at the
staged changes and the AI sessions that produced them, and to sign off. Slice A (2026-10-23) is
the desktop core; slice C (2026-10-24) sends a request to viberails.ai and collects its decisions.
`ReviewBundleCapture` reads the staged snapshot into a bounded bundle (tree id,
base, files with base/staged text, attached sessions, reviewers); `ReviewRequestService` stores
requests and decisions in state.db (`review-requests/1`, additive, `IReviewRequestStore`) and
accepts an approval only as a `vibe-signature:` trailer over the request's tree that viberails.ai
verifies to a listed reviewer's verified email. The commit-msg hook (`IStoredCommitApprovalSource`)
places such an approval in the message before offering the signing popup and rechecks it like any
trailer, so an agent's commit can pass on a person's approval. Routes are root-only under
`/api/v1/review-requests`. The signed statement is the existing staged-tree statement, so the
`Signed Approval` rule is the gate and no new rule exists. `ReviewRequestSender` transfers a
request only after its disclosure was confirmed (`ReviewRequestTransport`: fixed origin,
`X-Api-Key`, bounded, validated answers) and queues the attached sessions through the existing
`SessionShareUploads` path under the sending key's fingerprint; `ReviewRequestPoller` runs from
`BoardSyncScheduler` on a persisted 15-minute cadence that backs off to two hours and lands hosted
decisions through the same verification, publishing `review_request_updated`. The reviewer desktop
(slice D), the card UI (slice E) and line comments with the VS Code "Review requests" view (slice F,
`review-requests/4`) followed. Contract and decisions:
[Review requests](../VibeRails/Services/ReviewRequests/README.md); security notes:
[API_SEC.md](../API_SEC.md#review-requests-before-a-commit-vb-50-slice-a).

### Code report inspection

VCA offers `Code quality minimum A`, `B`, and `C` rules. `CodeQualityRule` uses MintLint's
existing overall score for added code under the declaring rule file; individual file grades do
not decide the result. The cutoffs are health 90/80/70, with C the lowest selectable minimum.
Git Guard and MCP validate the staged snapshot, while Rules-page validation captures working-tree
additions once per request. Failures use standard WARN/COMMIT/STOP enforcement. Report-only ignore
preferences do not exempt code from the rule, and changes with no supported added code are skipped.

The full-width Quality workspace (`code-quality`) renders the host-owned Code Atlas / Quality
Lab viewer, third in the top navigation. Rules and Git Guard have the fourth destination
(`dashboard` / `agents`). Each
page runs only its own checks; `RuleController` preserves the cached MintLint report across
navigation and disposes the viewer on departure. Settings is an icon-only cog beside the
Automation play button in the top navigation. The authenticated,
root-only `POST /api/v1/code-analyzer/graph` supplies bounded working-tree structure and
lexical source references through `RepositoryCodeGraph`. Python package imports, Rust module
trees, JS/TS imports/re-exports and namespace-scoped C#/PHP mentions provide link evidence;
TypeScript interfaces and aliases appear in outlines. Coverage diagnostics distinguish source
filters from bounded omissions; they and the scan log open from the card's menu, while the
header line carries the scan's count, duration and age. The page's status colors and the map
palette are host tokens (`--quality-*`, `--node-*`, `--graph-*`) declared on `:root` and mapped
from the editor theme by the VS Code theme bridge. Dependency folders (node_modules, vendor, assets) and C# build
output stay excluded, including report priorities. The Code graph view draws the whole snapshot
on two canvases with no DOM per entity: a 4,500-link ambient budget (tree links first, hashed
sample of references), hover and selection lighting every link of an entity, bounded signals, and
adaptive thinning or stillness on slow machines, and semantic zoom in a dense field: declarations
fade in as the camera closes while the structure, hover, selection, search and filters are
unaffected and the layout never moves. Root-only `GET /api/v1/code-analyzer/changes`
and `GET /api/v1/code-analyzer/changes/diff` list the working tree's changes against HEAD and
serve one file's before/after text for the shared diff viewer; the sidebar switches between
report files and Git changes, and the same list feeds the map's change highlight. Report files
is the default list. One second after the map is ready, the viewer focuses the first report
file with Atlas's normal animation; user input or leaving the view cancels that selection.
The request retains the existing repository containment and read limits. No preview fixtures are shipped.
Scan/exclusion controls and all rule/commit enforcement remain outside the viewer.
See the [viewer integration contract](../VibeRails/wwwroot/js/modules/code-report/README.md)
for data limits, vendor provenance, themes/CSP, lifecycle and regression tests.

### Vibe Board

Lane visibility is a local browser preference: `BoardController` stores collapsed lane IDs in
`viberails.board.collapsedLanes.v1`. Hide lane reduces a column to a 52px restore strip with its
name and card count; Show lane restores its contents and in-view scroll position. This changes
no Board data or Automation behavior and requires no schema or API changes.

Jira Cloud connection controls live in the board settings modal (`board-jira.js`), bound to
the edited board ID. They save separately from board metadata; the app Settings page has no
Integrations tab. VIBE-103 separates General, Jira Cloud, Context and History with keyboard
accessible settings tabs. Pull and its report stay in Jira settings. Card summaries/detail derive
`jiraIssueKey` from the existing issue links for a Jira logo badge; the origin filter includes Jira
and excludes imports from Human-made. Assignee choices deduplicate canonical CLI identities.

Connect creates a dedicated `Jira · …` local board; existing connections do the same on the next
writing pull. A source board nothing else uses (no other cards, lane Automations, tracker or
viberails.ai sync, and another local board remains) becomes the Jira board itself instead, unless
the New board dialog asked for a new one. Otherwise `IBoardStore.EnsureDedicatedJiraBoardAsync` atomically clones the source lanes,
remaps explicit column choices and overflow, moves that connection's live imported cards still
on the source board through the normal card-log transfer, then rehomes the connection. Native
cards and existing card identities/discussion/files/session/commit links survive. Destination lanes
have no copied Automations. `board/30` adds nullable `DedicatedBoard` on the connection with no
startup backfill; older connection writers preserve it. Connect/pull returns the destination board
ID, and the UI selects it. Save, connection tests, unlink and writing pulls share the cross-process
Jira lock. Board branding and hosted-sync exclusion require the current connection's
`DedicatedBoard=true`; a legacy connection, board name or retained issue link alone does not
turn a Vibe board into a Jira board. Paused/expired dedicated connections still count as Jira,
and unlink makes the retained board local again. When a connection moves, the open source
settings refreshes its sync status while preserving other drafts. Board settings show a Jira
logo beside the board name and a fixed 32-dot saved-token
placeholder. **Unlink Jira** calls the root/project-scoped `DELETE …/boards/{boardId}/jira`,
forgets the local token and deletes only the connection through `IBoardStore`. The board,
imported cards, notes and historical issue links remain. The dialog restores local-board settings
without replacing unrelated drafts. Jira is the source of mapped story fields while connected.
New linked-card comments post to Jira by default; `syncToJira=false` keeps an internal note,
and new local session attachments queue public replay links to the issue. The explicit
`add_jira_comment` MCP tool posts a completion summary and
optional public replay links to the card's connected Jira issue, then saves an attributed
Board receipt. Both transports use the saved connection and the shared Jira client;
the tool is outside the local Board auto-grants. See the [Jira comment contract](../VibeRails/Services/Jira/Comments.md).

VIBE-102 connects a board from a pasted Jira board link plus an API token; there is no site, JQL
or story points field to type. `JiraBoardLink.Parse` (`Services/Jira`) reads the site, board id
and project from team-managed, company-managed and legacy `RapidBoard.jspa` links, and a project,
issue or issue-search link becomes a JQL source. **Connect** saves, then `POST …/jira/test` checks
the token and reads the board, its configuration (columns, kanban sub-query, estimation field),
its filter's JQL and an approximate issue count. A board pull uses the enhanced
`GET /rest/software/1.0/board/{id}/issue` and re-reads the configuration every pull: an issue's
status id names its Jira column, and the shared `TrackerColumnMap` puts the column in a lane (same name, a
whole-word match, first column to first lane, last column to Done, otherwise the "Jira" overflow
lane), with per-column picks under Advanced. Placement happens once, when the card is created.
Since VIBE-236 a pull only adds new issues and never changes a card after import
(`Services/Board/Tracker/TrackerImport.cs`): its fields, lane and board are the local workflow,
and the only ongoing link is outbound comment and session-link delivery. Each new issue link
still records the points field and status it was imported with (`BoardJiraLinks.Mapping`, written
by `TrackerLinkMapping`) for older versions; this one never reads it. Old Done issues are left
out by default (`statusCategory != Done OR updated >= -30d`). The board filter's JQL is still written to
`BoardJiraConnections.Jql`, so an older VibeRails keeps pulling the same issues. Without the
configuration (a 403), lanes fall back to status-name matching and story points stay off unless
a field is set. VB-40 site-plus-JQL connections keep working unchanged.

VIBE-167 adds the Azure DevOps pull beside it (`Services/AzureDevOps`, Jira parity over the
shared `Services/Board/Tracker` helpers). `AdoBoardLink.Parse` accepts only `https://dev.azure.com`
team board, backlog and saved-query links. `AzureDevOpsClient` (named HttpClient `azure-devops`,
api-version 7.1, PAT over Basic, redirects and cookies off) reads the board's columns and
state mappings, the team's area paths and the organization's fields every pull, then runs WIQL
(the board's types, the team scope, never Removed, newest change first) or the saved query, and
reads at most 10 `workitemsbatch` calls of 200. A 203, a 302 or a non-JSON answer means the token
was refused. `AdoPullService` places a new card once (board column, then state mapping, then state
name, then the "Azure DevOps" lane) and never changes it afterwards. `AdoPullScheduler` runs every
15 minutes, ticked beside Jira by the leased root job scheduler, under `.ado-pull.lock`; tokens
live in `ado-tokens.json`. Storage is `board-ado/1`. The Board guide's "Dedicated Azure DevOps
boards" section has the rules.

Both tracker clients use `BoundedHttpContent` to cap each JSON read at 4 MiB before parsing,
including responses without a reliable Content-Length. One 30-second deadline covers headers
and the entire body; limit and timeout failures return ordinary failed-call results. Tracker
4xx responses use fixed wording without reading remote error prose, so pull reports cannot
persist it. Caller cancellation still propagates. The update checker uses the same reader with
a 1-MiB cap and 30-second deadline. Its handler and the shared outbound handler disable redirects
and cookies; TokenSaver control also disables system proxies in both MCP transports through
`AddTokenSaverMcp`.

VIBE-168 adds the delivery side. New comments and session links on a work-item card post to the
work item (HTML paragraphs plus vetted replay links) through `AdoDeliveryService`, drained every
5 seconds by `AdoPullScheduler`; `syncToRemote=false` keeps an internal note. `add_ado_comment`
posts explicitly on both MCP transports, outside the Board auto-grants. Root/project-scoped
`/api/v1/board/boards/{boardId}/ado` GET/PUT/DELETE, `/ado/test` and `/ado/pull` back the settings
form; the PAT is write-only. Dedicated Azure DevOps boards never sync with viberails.ai. See the
[Azure DevOps comment contract](../VibeRails/Services/AzureDevOps/Comments.md) and API_SEC.md.

VIBE-169 adds the web UI. Board settings gain an **Azure DevOps** tab (`board-ado.js`,
`BoardAdoPanel`) beside Jira Cloud: a dev.azure.com board or query link, a write-only PAT with a
per-organization "Create a token" link and both required PAT scopes (Work Items: Read & write and
Project and Team: Read for the initial project-access check), the pull and
skip-old-done switches, and Advanced narrowing WIQL, points field and lane picks. Connect saves,
tests and follows the dedicated board id both calls return, and selects that board. The board
name for a saved query replaces its provisional project label once Connect discovers the
query name, only while that generated label is unchanged; custom names stay intact. The sync
status's `remoteProvider` drives the layout: an Azure DevOps board shows its connection under
General with the Azure DevOps logo, a Jira board shows Jira's, and neither shows the other
tracker's tab (`mountBoardSettingsNavigation(...).followProvider`). `board-brand.js` maps a
provider to its label and artwork (`assets/img/azure-devops.svg`) for the picker, the modal title
and card badges ("Azure DevOps · #123", linked only to an https URL without credentials). Comments
post with `syncToRemote`, entries kept off the work item show "Not sent to Azure DevOps", delivery
status reads `remoteDeliveries`, and the origin filter offers Azure DevOps (Human-made excludes both
trackers).

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
`board-card-assign.js` is the lane card's assignee menu (VIBE-145): the assignee chip (one button,
never a filter) lists every `assignee` picker item, hidden ones included, and saves `assignee` alone
through the ordinary card patch without opening the editor; the server clears old base launch options.
Waiting cards show a badge backed by the durable lane queue; the card's Automations rail can skip
one exact pending entry with **Continue without this Automation**. Cards stay in their chosen lane
while entries wait for an available Automation.
New local boards create lanes only; the special Switch reviewer is no longer automatically
installed or offered as an Agent editor preset. Existing Agents, review routing, card controls
and lane selections remain intact. Old pending starter intents are retained without recovery.
See the [starter workflow contract](../VibeRails/Services/Board/ARCHITECTURE.md#new-board-review-defaults-vibe-23).

VB-52 adds owner invitation controls and website recipient inbox/block screens. The website
enforces the three-collaborator cap and Board membership. Publishing is one-way and owner-only
(VIBE-235): collaborators read shared boards on the website, the desktop no longer imports them,
and copies earlier versions imported (pinned by additive `board/26` origins) stay local, read-only
and unsynced. See the
[sharing contract](../VibeRails/Services/Board/SYNC.md#vb-52-shared-boards) for lifecycle and limits.

The toolbar's **Remote boards** manager (`board-remote.js`, `RemoteBoardsService`) pages all
accessible hosted boards and labels their local copies across projects. Owners can rename or
delete remote boards, including orphaned copies; there is no create (VIBE-235). Deletion pauses matching local sync
while retaining local work; missing owned remote boards no longer recreate themselves on a tick.
See the [manager contract](../VibeRails/Services/Board/SYNC.md#remote-board-manager).

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
Cards optionally select an Agent workflow with a separate saved manager and default worker.
The resident manager uses existing authenticated launches, status/review polling and handoffs
to drive the pipeline while the root is open. Direct workers and lane Automations finish their
own steps; the manager chooses movement. Configuration is additive and local-only. See the
[Agent workflow contract](../VibeRails/Services/Board/ARCHITECTURE.md#agent-workflow-vibe-52-2026-10-11).
Base Codex cards persist an optional Speed choice (Default/Fast/Ultrafast) in their existing
`BaseLlmOptions` JSON. `llm-model-catalog.js` and `CodexModelCapabilities` define supported
model/tier pairs; `BaseLlmOptionsBuilder` validates them and emits session-only service-tier
arguments. Default inherits CLI settings. See the model maintenance checklist in
`vibe-books/custom_envs/CLI_OPTIONS.md` when adding models or speed tiers.
Board MCP create/update uses the same typed assignment and `BaseLlmOptions` validation as
the UI. `list_board_agent_options` and `start_board_agent` call the existing authenticated
root picker/launch routes using inherited scoped launch credentials; Start uses the card's saved
settings and returns a session ID to poll, without changing running agents or global config.
Desktop MCP work is independent of terminal sessions: `DesktopMcpActivityTracker` writes
90-second leases through `IBoardStore` into additive `BoardDesktopActivity` storage in
`board.db`. Stdio renews while connected and ends owned marks at completion/EOF; HTTP
uses recent successful same-project writes or explicit attachment. Stateful completion
ends only its own mark; stateless app-name aggregates expire naturally. Card closure ends
leases and retains their rows. Board responses/polling project `hasActiveDesktopAgent`
for the purple border/desktop icon, alongside real CLI/Automation indicators. Activity
creates no terminal navigation, session replay or hosted sync. See the
[desktop lifecycle contract](../VibeRails/Services/Mcp/AGENTS.md#desktop-client-activity).
VIBE-6 adds `BoardSearchService` for dashboard search, link candidates, `search_board_cards` and
card recall: all local boards share current-text BGE/keyword retrieval with a bounded repository
preference. VIBE-55 feeds full titles, descriptions, Comments, retained notes and handoffs through
`IBoardStore` into the shared per-user `search.db`. Existing root jobs reconcile sources and build
token-aware BGE/sqlite-vec chunks. Queries read only this search component; full lexical text,
result metadata, work claims and progress live there too. See the [shared search contract](../VibeRails/Services/BertV2/README.md). Foreign cards have explicit board/project warnings and an authenticated
local-card editor that resolves ownership from immutable identities. Related-card links can cross
local projects; foreign links remain outside hosted publication. Moves, merges and repository
launches keep their existing project scope. See the [search contract](../VibeRails/Services/Board/AGENTS.md#local-board-search-vibe-6).
The saved card's GitHub links rail stores bounded, canonical PR/commit URLs in `BoardCodeLinks`
behind `IBoardStore`. These references are independent of local commit snapshots. They appear
in agent card reads, public card captures, and session archives for attached cards at export time.
Frozen archives retain their original metadata; ordinary Board sync does not transfer the rail.
MCP discovers all local boards, with current-project boards first and other projects separately
labeled. Explicit board IDs and full permanent card keys select other projects; omitted targets
retain the launching card/current-project defaults. See the [local discovery contract](../VibeRails/Services/Board/ARCHITECTURE.md#local-mcp-discovery-vibe-28).
`GET /api/v1/board/cards/{card}/context` (root-only, both credentials) reports the context an agent
launched on a card would receive, as characters and estimated tokens (`BoardContextEstimator`);
each launch records the same measurement in `board.db` `BoardContextSamples` and as a `context`
Card Log entry that Board sync carries (VB-63).
`/api/v1/board/cards/{card}/sharing-links` (root-only, both credentials, no-store) publishes
complete read-only card shares to viberails.ai: `GET` lists, `POST` creates, `PATCH`/`DELETE /{id}`
rename and revoke, and `POST /refresh` republishes; the leased scheduler rediscovers publications
every 15 minutes with backoff. See the [card-sharing contract](../VibeRails/Services/Board/Sharing/README.md).
The review records open concurrency and workflow findings; documentation is not evidence that
those findings have been fixed.
Card text can reference repository files as `@path` (VB-35): the text is the only storage,
`GET /api/v1/board/files?q=` (root-only) backs the composer's typeahead, and the launch prompt
lists the references next to linked commits and attachments. The composer keeps `@` file-only;
`!` searches card titles/keywords and opens a session picker with agent/time/status metadata.
Only complete GUIDs use exact session-history lookup; partial GUIDs never query that catalog.
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

**Available Rules** (22 total):
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
17. `RequireVibeRailsSessionLink` - Require VibeRails session link
18. `CodeQualityMinimumA` / 19. `CodeQualityMinimumB` / 20. `CodeQualityMinimumC` - Code quality minimum A/B/C
21. `HumanVerified` - Human verified (a person signs the commit in the Git Guard popup)
22. `SignedApproval` - Signed Approval('path', 'approver@example.com'); see the
    [VCA guide](../VibeRails/Services/VCA/AGENTS.md#signed-commits-human-verified-and-signed-approval-2026-10-20)

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

#### Prompt-created Git worktrees ([Services/Workspaces/ManagedWorktreeService.cs](../VibeRails/Services/Workspaces/ManagedWorktreeService.cs))

An Initial Message may contain `{{worktree}}`. Both arguments are optional:
`{{worktree path="../my-task" delete_after_days=14}}`. Without a path, VibeRails creates
`~/.vibe_rails/worktrees/<repo>-<unique-id>`; custom paths are repository-root-relative or
absolute (`~` is supported), must be new, and must be outside the source checkout. Each launch
creates a unique `viberails/worktree-<id>` branch from committed HEAD. Dirty source files are
not copied. Repeated identical tokens share one worktree; conflicting options fail the launch.

`PromptPlaceholderService.ResolveLaunchAsync` returns the resolved message and working directory.
Web starts defer this pass until the terminal lifecycle gate is held; native launches resolve
in `CliLoop`. The token expands to the absolute worktree path. Branch tokens, prompt-output
steps, environment lifecycle steps, session recording and the CLI use the resulting directory.
Substituted values are never scanned again for tokens.

`ManagedWorktrees` in normal `state.db` records ownership, Git directories, branch, creation and
absolute expiry. The additive `managed-worktrees/1` migration runs automatically. Retention
defaults to 7 days; accepted overrides are 7–36500 days or `never`. The root-only
`WorktreeCleanupJob` checks every ten minutes while VibeRails is open; there is no separate host.
`BoardCardPostProcessingJob` follows the same pattern for the Board's run-once card pass (VIBE-185),
every five minutes on root backends; see the [Board guide](../VibeRails/Services/Board/AGENTS.md#card-post-processing-and-the-ghost-vibe-185).
Ghost code-landing checks also run for one card on an explicit recheck and after adding evidence
to a marked card. They run outside write transactions and conditionally save only the annotation;
completed cards never reenter the pending queue. Local default-branch commits count before push,
while GitHub PRs require a verified merge into the origin repository's default branch.
Overdue work is picked up after reopening. Expiry is age-based, independent of merges, and
removes uncommitted, untracked and ignored files too. Branches are retained.

Cleanup uses Git's own `worktree remove --force` for a verified owned directory. Ownership
markers, Git-directory pointers and link/junction checks prevent adoption of unrelated paths.
Shared OS file leases protect launches, PTYs and post-exit steps across root processes;
exclusive cleanup leases serialize removals. Open session rows also protect older clients.
Active sessions and user Git locks postpone removal until a later sweep. Failed removal keeps
the ownership record for retry.

#### Automation workflows ([Services/Jobs](../VibeRails/Services/Jobs))

An Automation is an ordered, fail-fast list of actions. An action is either a repository script
(`.py`, `.ps1`, or `.sh`) or a Worker Environment; a workflow may contain zero or one Worker and
up to 20 total actions. Existing Worker-only Automations migrate to a one-Worker action without
changing their behavior.

The frontend shows a Job with a Worker action as an **Agent** (Agents page) and one without as an
**Automation** (Automations page). The Agent editor creates and edits the Job and its required
Worker setup in one modal using the shared `environment-editor.js` controls; the Automation editor
has no LLM step at all. It derives the internal Worker name from the
Agent name (stepping around built-in CLI, numeric and device names the backend rejects),
validates the workflow, then saves through the existing Environment, CLI settings and Job
endpoints. Successful intermediate writes are retained for retries within the open editor, and
an existing Worker whose controls are unchanged is not written at all.
Every Agent card has an Enabled switch. Disabling an on-demand setup with no Job creates a
disabled, unscheduled Job referencing it in the current project, retaining state through the
existing Job API without changing the setup. Manual Run now remains available.
The Environments modal mounts the same editor; model catalogs and launch argument handling
remain in their existing shared modules.

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
  The claim (`StartRunAsync`) retries a transient SQLite lock; if state.db stays locked through every
  retry, the child records "Could not claim the run…" on the still-Queued run and exits with
  `CouldNotStartExitCode`. The scheduler's stalled-launch watchdog only reports that the run was never
  claimed and points at `logs/vb-*.log` (VIBE-5).
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
- `Environments` - Environment configurations (stored centrally, owned by one project)
  - `Id`, `CustomName`, `LLM`, `Path`, `CustomArgs`, `CustomPrompt`, `CreatedUTC`, `LastUsedUTC`, `Hidden`, `ProjectPath` (null before 2026-08-08 scoping; null/blank legacy rows remain stored but are excluded from project lists, pickers and launches)
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

**User scripts** (registered pwsh `.ps1`, bash `.sh` or python `.py` files, gated by PIN-backed
hash pinning; the extension picks the interpreter):
Registrations in `~/.vibe_rails/user_script_library.json` hold stable IDs, local paths, display
names, optional project roots and run-PIN requirements. Global scripts appear everywhere; Repo
scripts are filtered by the server's current root. Creation defaults to `scripts/UserScripts`
and can use a folder inside the current repository; Add from disk references the original. No folder scanning or
legacy import occurs, and removing a registration leaves its file. Signing keeps the existing
PIN document, binding each new approval to ID/path/content. Both documents share the existing
cross-process lock. Runs reuse `.vb-scripts/.vibe-rails-{signed SHA-256 hash}.{extension}` beneath
the original script's directory, retaining that original directory as cwd. Before caching,
VibeRails creates a missing `.vb-scripts/.gitignore` containing `*`, so Git ignores the cache
and its ignore file in new and existing cache folders without editing repository ignore rules.
Each launch verifies the original approval and compares cached bytes with bounded reads;
missing/changed cache files, including oversized regular files, are published atomically under
the signing lock. Original scripts retain the 5 MB limit. The cache rejects links and junctions and survives completion or
cancellation. `$PSScriptRoot` and `__file__` refer to its directory. The Scripts UI now uses a private, socket-owned PTY in a floating xterm
window through `/api/v1/python-scripts/run/terminal/ws`. Minimize preserves the live process and
shows a restore button; Stop/Close/disconnect kill the process tree. Input and output are not
recorded, remembered or attached to normal terminal tabs. The captured HTTP run API remains
available to existing callers.

Python library and Automation launches use `-I` to exclude repository/cache imports, user site
packages and PYTHON* overrides. Approved scripts must be self-contained; trusted interpreter
packages and the standard library remain usable. Library runs pass `-u -X utf8` to preserve
PyBridge IO. First signing-PIN setup verifies dashboard presence through `SettingsPresenceService`;
later changes require the existing signing PIN. All signing-PIN checks share a process-wide
five-failure/five-minute cooldown per signing document. Imported paths outside UserScripts and
the current server-derived repository require the signing PIN for saves and renames; creation
and missing-file restore stay inside those roots. Metadata scope does not grant write access.

- `GET /api/v1/python-scripts` - List scripts with signing status
- `POST /api/v1/python-scripts/settings` - Update display name, Global/Repo scope and PIN-on-each-run requirement
- `POST /api/v1/python-scripts/pin` - Create or change the signing PIN
- `POST /api/v1/python-scripts/approve` | `/revoke` - Sign / unsign a script (PIN required)
- `POST /api/v1/python-scripts/run` - Run a signed script; `GET .../runs` for history
- `GET|POST /api/v1/python-scripts/content` - Read / write a script's text. GET returns a
  raw-content version; POST requires it as `expectedVersion` so a stale editor cannot overwrite
  a newer file. External writes require `pin`; a write never creates an approval.
- `POST /api/v1/python-scripts/create` | `/rename` - File management; `DELETE /api/v1/python-scripts?name=` removes only the registration
- `POST /api/v1/python-scripts/import` - Register a regular UTF-8 file at its existing local path (root-dashboard backend only, like the filesystem picker; network/device
  paths and links are rejected)

Scripts are listed with workflows in the Automations page's one list (VIBE-159), created from
its **New › Script** and registered from **Import › Script from disk**; a workflow's script step
picks from the library scripts inside the repository or the repository's own scripts.
The script workbench edits one selected file beside its agent pane. Its editor has no file
rail; **Back** to the Automations list is where users select another file or create one.
The agent pane (`script-agent-pane.js`) is one xterm for that script's agent, built like the run
window, with no tab strip. The agent remains an ordinary recorded session in a backend terminal
tab created with `POST /api/v1/terminal/tabs?owner=script-agent:{id}`; the tab list reports the
owner, the Terminals page never lists owned tabs, and reopening the workbench reattaches to it.
Starting an agent adds the current script's absolute path, an instruction to
read it and wait for a change request, and the signing constraint to the environment's startup
prompt. The pane reads the workbench's `getLaunchContext` callback on each fresh
Start so file switches and renames update both the brief and working directory. Shell launches
omit the brief, and reconnecting an existing session does not resend it.
Installed git-hook helpers and BERT download scripts remain internal. They never appear in the user library. Signed entries are visible; unsigned and modified entries are behind a collapsed **Unsigned scripts** group.

**Nav Automation launcher** (the nav "Launch" flyout; preferences persist per install in GlobalCache):

The flyout shows visible automations and currently approved scripts; unsigned or modified scripts are omitted.

- `GET /api/v1/automation-nav/preferences` - Catalog of the current project's automations
  (`job:{id}`) plus each visible user script (`script:{id}`, with its signing `status`), each
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
- `GET /api/v1/jobs/runs`, `GET /api/v1/jobs/runs/{runId}` - Run history and per-action detail. On the
  root backend, a run that ended Failed, Cancelled, Timed out or Interrupted also carries `lastScreen`: the
  bottom of its recorded terminal, rebuilt by replaying the recording's last 256 KB through `TerminalEmulator`
  (`JobRunLastScreen`). A failing Worker's own error message ends with the last line its CLI printed.
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

Selecting a terminal tab acknowledges its current **Waiting for user input** badge and
returns the tab to **Connected**, including before xterm is mounted. `TerminalTab.setActive`
calls `TabStatusController.onTabActivated`; repeated waiting events stay suppressed until
the next local/backend submit or socket open. Other statuses and Board attention flags retain
their existing behavior. Regressions cover the state machine and the manager's activation path
in `terminal-tab-status.test.mjs` and `terminal-multitab-adoption.test.mjs`.

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

Quality, ENVs, Vibe AI, MCP and Settings share the `vb-page-header` template pattern and
`h1.vb-page-title`. Shared styles in `style.css` own title typography, theme colors, divider,
spacing and responsive stacking; page controllers retain their existing status and action controls.

**Structure**:
- Top navigation bar with icons
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

Business logic is isolated from HTTP concerns. Services are reusable across CLI and web modes.

Interactive Chat History recording responses (`raw-session`, `replay`, `terminal-replay`)
read a consistent snapshot through `ISessionStore.GetSessionRecordingAsync`. The store checks
a shared 16 MiB source-byte / 20,000-row budget in payload-free SQLite preflight queries
before selecting any contents (including native allocations). Oversized
recordings return HTTP 413 and retain all stored data. A two-request limit per process covers
loading and response transmission; extra requests receive HTTP 429. Raw ZIP entries stream
asynchronously to the response, preserving binary bytes, base64 continuity and JSON metadata.

Unified and BERT search reject queries over 1,000 characters before whitespace scanning,
embedding or lexical work. Index/search secret detection examines lines of at most 65,536
characters in inputs of at most 1,048,576 characters using non-backtracking regexes with 100 ms timeouts;
over-budget text and timeouts are treated as sensitive and excluded. Transcript redaction
keeps its separate policy.

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

### Windows installer

Windows releases also package a per-user Inno Setup EXE from the signed NativeAOT
payload. App-local VC runtime DLLs support ONNX without an elevated redistributable
install. The installer uses the stable `.vibe_rails` directory, explicit file
ownership and preflight checks, preserving configuration, models and user data
on upgrade/uninstall. Inno signs both Setup and its embedded uninstaller before
the checksum is produced. Build details and lifecycle tests are in the
[Windows installer guide](../deploy/installer/README.md).

### Release runners and Linux glibc baseline

The tag-triggered [release workflow](../.github/workflows/release.yml) pins every Linux
job to `ubuntu-24.04`, including tests, NativeAOT builds, VSIX packaging and publishing,
and GitHub release uploads. This avoids the `ubuntu-latest` move to Ubuntu 26 beginning
2026-10-19 changing the linker and apt toolchain underneath a release. The local
[build script](../deploy/build.ps1) uses `mcr.microsoft.com/dotnet/sdk:10.0-noble-aot`;
the [Dockerfile](../VibeRails/Dockerfile) also pins its .NET stages to Ubuntu 24.04 (Noble).

Measured on 2026-10-11 from the checksum-verified `vb-linux-x64.tar.gz` asset in
[v10.11.19](https://github.com/robstokes857/vibe-rails/releases/tag/v10.11.19):

| Packaged ELF | Highest required glibc symbol version |
| --- | --- |
| `vb` | `GLIBC_2.34` |
| `libonnxruntime.so` | `GLIBC_2.28` |
| `libe_sqlite3.so` | `GLIBC_2.34` |
| `vec0.so` | `GLIBC_2.14` |

The Linux release archive verification logs each requirement and rejects any of these
files requiring more than `GLIBC_2.34`, or an unreadable/missing numeric glibc requirement.
After the next patch release, confirm the runner migration annotation is absent, the
archive verification passes, and remeasure the extracted executable with version-aware sorting:

```bash
objdump -T vb | grep -oE 'GLIBC_[0-9]+(\.[0-9]+)+' | sort -Vu | tail -n 1
```

An Ubuntu 26 build remains a separate, deliberate decision about newer tools and the
minimum supported user environment. Revisit the runner/image pins only with that decision;
update the measured baseline and verification ceiling together if the floor is raised.

### Release supply chain

Every workflow action is pinned to a commit SHA with a version comment. The release token
is read-only except in the `upload` job. Signing (`build`) and Marketplace publishing
(`publish_extension`) run in the `release-signing` and `vscode-marketplace` environments,
which the owner can gate with reviewers and environment-scoped secrets.
`Tests/Deployment/ReleaseSupplyChainTests.cs` enforces the pins and the write scope.

A release carries a `.sha256` for every archive, the Windows Setup EXE and each VSIX, plus `install.ps1` and
`install.sh` with that release's tag stamped into `$ReleaseTag` / `RELEASE_TAG`. A stamped
installer installs exactly its own release. The copies on `main` keep the tag empty and
install the latest release.

On Windows, the archive's `.sha256` comes from the same release, so it proves only an intact
download. The Windows build therefore also publishes `vb-win-x64.cat`, a file catalog
(`New-FileCatalog`, SHA-256) of every payload file, signed with the same Trusted Signing
identity as `vb.exe`. `install.ps1` checks the catalog's publisher before extracting
(`Assert-PublisherSignature`), then rejects any extra, missing or altered file
(`Assert-PayloadMatchesCatalog`), and finally checks `vb.exe`'s own publisher. A release newer
than `$LastReleaseIdWithoutCatalog` (v10.13.2) that lacks the catalog is refused. The Windows
archive verification step runs these same functions on what users will download, so a signing
identity change fails the release rather than users' installs. The Linux and macOS archives
are covered only by their checksums until a signing decision is made (VB-58XPR-306).

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

Replay metadata includes nullable `tokensSaved`, estimated from the net character
reduction across proxy captures attributed to that exact session, divided by four.
The snapshot reads this total with its proxy maximum ID; reloading updates open sessions.
The website computes the same estimate from full envelopes or compact `proxySavings`
size records. Missing measurements display Unavailable, and measured zero remains zero.
See the [viewer contract](../VibeRails/wwwroot/session-replay/README.md) for the estimation
and cross-checkout compatibility details.

## Card check evidence (VIBE-24)

Code quality and VCA are ordered Automation actions beside the optional single Worker.
`BoardCheckService` calls the individual preflight engines without the Automation-enqueuing
pipeline. `IBoardStore` retains attempts, provenance and reports in additive `BoardChecks`.
`BoardChecksReader` combines saved evidence with per-card run state; `read_board_check` supplies
reviewer evidence, and the Checks panel reuses the existing CodeReportViewer. No scan result
moves a card. See [scope, persistence, status and starter contracts](../VibeRails/Services/Board/CHECKS.md).

Lane Automations always run in selected order per card. The lane popup and card rail share
per-step progress, explicit LLM verdicts and user skips; no execution-mode setting is required.
The lane popup offers **Re-run** for failed, stopped current steps, preserving their entry and
original run snapshot in a new linked terminal tab. Stale requests and overlapping runs are rejected.
See [ordered lane workflows](../VibeRails/Services/Board/ARCHITECTURE.md#ordered-lane-workflows)
for entry snapshots, review decisions, cancellation and compatibility.

### Jira activity delivery

Jira issue links are exposed in card detail. Pulls accept both ADF and plain-string descriptions;
an otherwise unchanged issue can repair an empty local description without resetting other
fields or its local lane. `BoardJiraDeliveries` records future comment and session intents in
their Board transactions. The root Jira scheduler drains the ledger independently of pulls
using `JiraDeliveryService` and the shared cross-process Jira lock. Session delivery composes
with the existing public sharing/upload service. UI and MCP support per-comment
`syncToJira=false`; see [the Jira delivery contract](../VibeRails/Services/Jira/Comments.md).

Azure DevOps work items get the same treatment through `BoardAdoDeliveries` and
`AdoDeliveryService` (VIBE-168). The web UI sends the provider-neutral `syncToRemote`, which both
comment routes accept; storage keeps the `syncToJira` key, so a note kept off one tracker stays
off the other. See [the Azure DevOps delivery contract](../VibeRails/Services/AzureDevOps/Comments.md).
