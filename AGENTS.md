# AGENTS.md - VibeRails Project Documentation

## Terminology Note

**"Web UI Chat"** refers to the xterm.js-based terminal interface where users interact with CLI tools (Claude, Codex, Antigravity) through a browser-based terminal emulator. This is NOT a separate chat UI - it's the PTY-backed terminal that runs actual CLI sessions.

## Project Overview

**VibeRails** is a sophisticated desktop/web application for managing and enforcing coding standards across AI-powered development workflows. It serves as a unified control panel for multiple LLM CLIs (Claude, Codex, Antigravity) with comprehensive rule enforcement, session logging, and MCP integration.

**Live Site**: [https://viberails.ai/](https://viberails.ai/)

### Core Capabilities
- **Agent File Management** - Create and manage `agent.md` files with customizable coding rules
- **Rule Enforcement** - Define standards with three enforcement levels (WARN/COMMIT/STOP)
- **Multi-LLM Support** - Unified interface for Claude, Codex, Antigravity, Copilot, and OpenCode CLIs
- **Environment Management** - Configure separate environments for different LLM providers with custom args and prompts. Launch environments directly in the Web UI terminal with the "Web UI" button or select from the terminal's environment dropdown
- **Sandbox Management** - Create isolated git clone sandboxes for parallel AI workflows. Shallow clones current branch with all dirty/untracked files. Launch terminals or VS Code directly into sandbox directories.
- **Session Logging** - Track and monitor all CLI session history and outputs
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
│   │   ├── AgentRoutes.cs          # Agent file management
│   │   ├── TerminalRoutes.cs       # Web terminal session endpoints
│   │   ├── LlmSettingsRoutes.cs    # Claude/Codex per-env settings
│   │   ├── McpRoutes.cs            # MCP tool inspection/calling
│   │   └── ...                     # (SandboxRoutes, SessionRoutes, etc.)
│   │
│   ├── Services/                   # Business logic layer
│   │   ├── AgentFileService.cs    # Agent file management
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
│   ├── DB/                         # Data access layer
│   │   ├── Repository.cs           # SQLite data access implementation
│   │   ├── IRepository.cs          # Repository interface
│   │   ├── SqlStrings.cs           # SQL statement definitions
│   │   └── DBModels/
│   │       └── Project.cs          # Project entity model
│   │
│   ├── DTOs/                       # Data transfer objects
│   │   ├── ResponseRecords.cs      # API response types
│   │   ├── Sandbox.cs              # Sandbox entity model
│   │   ├── LLM.cs                  # LLM enum (NotSet, Codex, Claude, Antigravity, Copilot, Shell, OpenCode, Glm52, Grok46, Glm53)
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
- VCA is the only preflight stage that can block a commit; automated workflows are currently a placeholder
- The native pre-commit hook uses the same shared pipeline and console event presentation

The old CLI management commands (`vb env`, `vb validate`, `vb hooks`, etc.) are no longer part of the supported surface. Use the Web UI, VS Code extension, or REST APIs for those workflows.

> **Additional process-host modes** (internal, not user-facing): `vb mcp` (MCP stdio server),
> `vb --vca-hook <type>` (VCA hook process host used by git hooks), `vb --job-run <id>`
> (automated job run), `vb --job-trigger` (post-commit job enqueue), and `vb --job-tick`
> (compatibility tombstone for the retired OS Jobs scheduler — recognized and exits before the
> web host starts). These are invoked internally by the main app or git hooks, not typed by end users.

### Component Interaction Flow

#### Agent Rule Management Flow
```
Browser (app.js)
  ↓ [GET /api/v1/agents]
ASP.NET Route Handler (Routes/AgentRoutes.cs)
  ↓ DI injects IAgentFileService
AgentFileService
  ↓ Uses IGitService to find repository root
  ↓ Scans for agent.md/agents.md files
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
See [VibeRails/Services/Mcp/AGENTS.md](VibeRails/Services/Mcp/AGENTS.md) for the full design.

## Key Components

### Services Layer

#### AgentFileService ([Services/AgentFileService.cs](VibeRails/Services/AgentFileService.cs))
**Purpose**: Manage agent.md files with rule definitions

**Key Methods**:
- `GetAgentFilesAsync()` - Scan repository for agent files
- `GetAgentFileRulesAsync(path)` - Parse rules from specific agent file
- `CreateAgentFileAsync()` - Create new agent file
- `AddRuleAsync()` - Add rule with enforcement level
- `UpdateRuleEnforcementAsync()` - Change enforcement level (WARN/COMMIT/STOP)
- `DeleteRulesAsync()` - Remove rules from agent file

**Rule Format** (full contract: [Services/VCA/AGENTS.md](VibeRails/Services/VCA/AGENTS.md)):
```markdown
# Agent Instructions

## Vibe Rails Rules
- Cyclomatic complexity < 20 (COMMIT)

```

The heading must be `## Vibe Rails Rules` (`## Vibe Control Rules` is still read for older files).
The section ends at the next heading, and fenced code blocks like the one above are skipped — an
example of a rule is never itself a rule.

#### RulesService ([Services/RulesService.cs](VibeRails/Services/RulesService.cs))
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

#### Repository ([DB/Repository.cs](VibeRails/DB/Repository.cs))
**Purpose**: SQLite data access implementation (implements `IRepository`). See
[VibeRails/DB/AGENTS.md](VibeRails/DB/AGENTS.md) for the full schema and operation reference.

**Key Methods** (selected):
- `GetOrCreateEnvironmentAsync(name, llm)` - Get/create environment record
- `SaveSandboxAsync(sandbox)` / `DeleteSandboxAsync(id)` - Sandbox persistence
- `CreateSessionAsync(...)` - Start new CLI session
- `LogSessionOutputAsync(...)` - Append terminal output to session log
- `GetRecentSessionsAsync(limit)` - Get recent CLI sessions

#### GitService ([Services/GitService.cs](VibeRails/Services/GitService.cs))
**Purpose**: Git repository operations

**Key Methods**:
- `IsGitRepositoryAsync(path)` - Check if directory is git repo
- `GetGitRootAsync(path)` - Find repository root directory
- `GetCurrentBranchAsync()` - Get active git branch
- `GetCurrentCommitHashAsync()` - Get current HEAD commit hash
- `GetRecentCommitsAsync()` - Retrieve commit history
- `GetFileChangesSinceAsync(commitHash)` - Get file changes since a commit

#### SandboxService ([Services/SandboxService.cs](VibeRails/Services/SandboxService.cs))
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

#### RunWorkspaceService ([Services/Workspaces/RunWorkspaceService.cs](VibeRails/Services/Workspaces/RunWorkspaceService.cs))
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

#### McpClientService ([Services/Mcp/McpClientService.cs](VibeRails/Services/Mcp/McpClientService.cs))
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
separate process. Full design: [VibeRails/Services/Mcp/AGENTS.md](VibeRails/Services/Mcp/AGENTS.md).

**Tools** (snake_case wire names): `validate_vca` (staged-file AGENTS.md rule validation),
`search_history` (semantic + keyword search over captured agent history via the real
`IUnifiedSearchService` — BGE/sqlite-vec/RRF), and the token-saver controls
`pause_token_saver`, `resume_token_saver`, `get_token_saver_status`.

Host shell command jobs (`run_shell_command`, `get_shell_command_status`, `cancel_shell_command`) and
web research (`web_search`, `web_fetch`) remain in the codebase but are **not currently exposed** as MCP
tools (security review 2026-07-02).

### Data Layer

#### Repository ([DB/Repository.cs](VibeRails/DB/Repository.cs))
**Purpose**: SQLite data access implementation

**Database Tables** (see [VibeRails/DB/AGENTS.md](VibeRails/DB/AGENTS.md) for the full reference):
- `Environments` - Environment configurations (global, not project-scoped)
  - `Id`, `CustomName`, `LLM`, `Path`, `CustomArgs`, `CustomPrompt`, `CreatedUTC`, `LastUsedUTC`, `Hidden`
  - `UNIQUE(CustomName, LLM)`
- `Sandboxes` - Sandbox git clones (project-scoped via ProjectPath)
  - `Id`, `Name`, `Path`, `ProjectPath`, `Branch`, `CommitHash`, `RemoteUrl`, `SourceBranch`, `CreatedUTC`
  - `UNIQUE(Name, ProjectPath)`
- `Sessions` - CLI session metadata
  - `Id` (TEXT PK), `Cli`, `EnvironmentName`, `WorkingDirectory`, `ProjectDisplayName`, `StartedUTC`, `EndedUTC`, `ExitCode`, `Processed`, `ParentSessionId`, `SessionDisplayName`, `OwnerPid`, `OwnershipTracked`, `JobRunId`
- `SessionLogs` - Terminal output logs
  - `Id`, `SessionId`, `Timestamp`, `Content` (BLOB), `IsError`
- `UserInputs` / `InputFileChanges` - User input tracking + correlated git diffs
- `TerminalSessionLogs` - Per-terminal-session structured log rows
- Additional tables: `AgentMetadata`, `ChatSummary`, `sessionOutPut`, `TokenSavings`, `CompressionCaptures`, `CodeAnalyzerIgnores`, `ProjectCache`, `GlobalCache`

**Configuration**:
- WAL mode enabled for concurrent access
- Foreign keys enforced
- Indexes on frequently queried columns (`StartedUTC`, `LastUsedUTC`, `ProjectPath`, etc.)

**Database Location**:
- Global: `~/.vibe_rails/state.db` (single shared database; no per-project database)

### API Layer

#### Routes ([Routes/Routes.cs](VibeRails/Routes/Routes.cs))
**Purpose**: REST API endpoint definitions (split across domain-specific modules in `Routes/`)

**Agent Management**:
- `GET /api/v1/agents` - List agent files
- `GET /api/v1/agents/rules?path={path}` - Get agent rules
- `POST /api/v1/agents` - Create agent file
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

**Session Logging**:
- `GET /api/v1/sessions/{sessionId}/logs` - Get session logs
- `GET /api/v1/sessions/recent` - Recent sessions

**MCP Integration**:
- `GET /api/v1/mcp/status` - MCP server status
- `GET /api/v1/mcp/tools` - List MCP tools
- `POST /api/v1/mcp/inspect` - Inspect tools on the local or a user-supplied Streamable HTTP MCP endpoint
- `POST /api/v1/mcp/tools/{name}` - Call MCP tool

**Utility**:
- `GET /api/v1/context` - Project context (IsInGit, root path, git branch/remote, sandbox flag)
- `POST /api/v1/git/preflight/stream` - Stream staged-index Git Guard events as SSE

### Frontend Layer

#### app.js ([wwwroot/app.js](VibeRails/wwwroot/app.js))
**Purpose**: Single-page application logic

**State Management**:
```javascript
const state = {
    currentView: 'agents',  // Current active view
    agents: [],             // Agent files list
    environments: [],       // LLM environments
    sessions: [],           // CLI sessions
    mcpTools: [],          // Available MCP tools
    selectedAgent: null,    // Currently selected agent
    selectedSession: null   // Currently selected session
};
```

**Key Functions**:
- `loadAgents()` - Fetch and render agent files
- `loadEnvironments()` - Fetch environment configurations
- `loadSessions()` - Fetch recent CLI sessions
- `createAgent()` - Create new agent file
- `addRule()` - Add rule to agent
- `updateEnforcement()` - Change rule enforcement level
- `launchCli()` - Start LLM CLI session
- `callMcpTool()` - Execute MCP tool

**View Rendering**:
- Template-based rendering using hidden `<template>` tags
- Dynamic content injection with data binding
- Event delegation for dynamic elements

#### index.html ([wwwroot/index.html](VibeRails/wwwroot/index.html))
**Purpose**: Main UI dashboard (Single Page Application)

**Structure**:
- Navigation sidebar with icons
- Main content area with view templates
- Modal dialogs for create/edit operations
- XTerm.js terminal integration for session logs

**Views**:
1. **Agents View** - Agent file management
2. **Environments View** - LLM environment configuration
3. **Sessions View** - CLI session history and logs
4. **MCP View** - MCP tool management and execution

## Design Patterns

### Dependency Injection
All services registered in [MapRegisterServices.cs](VibeRails/MapRegisterServices.cs) with appropriate lifetimes:
- **Scoped**: Services tied to request lifecycle (Repository)
- **Singleton**: Long-lived services (GitService, RulesService, MCP settings)

### Repository Pattern
`IRepository` interface abstracts SQLite data access, allowing easy testing and potential database swaps.

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
├── state.db                        # SQLite database (single shared DB, no per-project database)
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
├── agent.md                        # or agents.md
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

2. **Update agent.md files**:
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

2. **Register in [MapRegisterServices.cs](VibeRails/MapRegisterServices.cs)**:
```csharp
builder.Services.AddSingleton<IMyLlmCliEnvironment, MyLlmCliEnvironment>();
```

3. **Update LLM enum** in [DTOs/LLM.cs](VibeRails/DTOs/LLM.cs)

4. **Add launcher logic** in [Services/LlmClis/LaunchLLMService.cs](VibeRails/Services/LlmClis/LaunchLLMService.cs)

5. **Update frontend** to support new CLI option

### Adding a New MCP Tool

1. **Create tool class** in [VibeRails/Services/Mcp/Tools/](VibeRails/Services/Mcp/Tools/):
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

2. **Register in [MapRegisterServices.cs](VibeRails/MapRegisterServices.cs)**: add
   `.WithTools<MyCustomTool>()` to the `AddMcpServer()` chain (and `AddScoped<MyCustomTool>()`
   if it's an instance tool).

3. **Add a test** in [Tests/Services/Mcp/](Tests/Services/Mcp/).

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

### Task: Find all agent files in repository
```csharp
// Use: AgentFileService.GetAgentFilesAsync()
var agentFiles = await agentFileService.GetAgentFilesAsync();
```

### Task: Add rule to agent file
```csharp
// Use: AgentFileService.AddRuleAsync()
await agentFileService.AddRuleAsync(
    agentPath: "/path/to/agent.md",
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

**Issue**: Agent files not found
- **Cause**: Not in git repository or agent.md not at repo root
- **Solution**: Run from git repository root, create agent.md file

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
  you're talking to the root backend's port. See [VibeRails/Services/Mcp/AGENTS.md](VibeRails/Services/Mcp/AGENTS.md).

### Debug Logging

Enable verbose logging in [Program.cs](VibeRails/Program.cs):
```csharp
// Modify logging level
builder.Logging.SetMinimumLevel(LogLevel.Debug);
```

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
- No shell injection vulnerabilities in CLI launching
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
- **Microsoft.Data.Sqlite** (v10.0.10) - SQLite database access
- **ModelContextProtocol** (v2.0.0) - MCP foundation
- **ModelContextProtocol.AspNetCore** (v2.0.0) - ASP.NET Core integration for the in-process MCP server
- **Pty.Net** (inlined fork) - Pseudo-terminal support
- **PyBridge** (in-tree library) - Python script execution, streaming, and warm worker sessions

---

## Git Hook Installation System

### Overview

VibeRails includes a sophisticated git hook installation system that automatically enforces VCA (Vibe Control Architecture) rules at commit time. The system has been completely refactored from hardcoded scripts to a modular, testable, and maintainable architecture.

### Architecture

#### HookInstallationService ([Services/HookInstallationService.cs](VibeRails/Services/HookInstallationService.cs))

**Purpose**: Manage installation and uninstallation of git hooks for VCA enforcement

**Key Improvements (Refactored 2026-02-06)**:
- ✅ **Extracted scripts to files** - Hook scripts moved from C# strings to [scripts/](VibeRails/scripts/) directory
- ✅ **Proper error handling** - Returns detailed `HookInstallationResult` with specific error types
- ✅ **Structured logging** - Integrated with `ILogger<T>` for comprehensive diagnostics
- ✅ **Atomic operations** - Rollback support if installation partially fails
- ✅ **Configuration support** - Respects `app_config.json` settings for auto-install behavior
- ✅ **Cross-platform safe** - Handles Windows, Linux, and macOS correctly
- ✅ **Comprehensive tests** - Full test coverage in [Tests/Services/HookInstallationServiceTests.cs](Tests/Services/HookInstallationServiceTests.cs)

**Hook Scripts**:
1. **pre-commit-hook.sh** - Validates VCA rules before commit
   - Runs the standalone `vb --vca-hook pre-commit` host against the Git index snapshot (not unstaged working-tree edits)
   - Shows a dedicated console when a Windows Git GUI captures hook output
   - Blocks STOP violations and validation errors; COMMIT violations continue to commit-msg
   - Allows bypass with `git commit --no-verify`

2. **commit-msg-hook.sh** - Validates COMMIT-level acknowledgments
   - Runs `vb --vca-hook commit-msg` through the same validation engine
   - Ensures required acknowledgment tokens include a non-empty reason
   - Enforces COMMIT-level rule compliance

**Installation Behavior**:
- **Auto-install on startup** - Hooks installed automatically when VibeRails starts (configurable)
- **Preserves existing hooks** - Inserts VCA ahead of existing shell-hook exits and chains non-shell/binary/symlink hooks through a preserved sidecar
- **App-versioned health checks** - Installed hooks carry the running VibeRails version (for example `1.9.8`); startup detects missing, disabled, stale/older-version, partial, missing-launcher, or mismatched-launcher hooks and replaces them
- **Git-aware path resolution** - Honors linked worktrees and `core.hooksPath`
- **Safe hook chaining** - Runs before existing shell hooks and preserves non-shell hooks as executable sidecars
- **Safe uninstallation** - Removes only VibeRails sections, keeps other hooks intact

**Key Methods**:

```csharp
// Install both pre-commit and commit-msg hooks
Task<HookInstallationResult> InstallHooksAsync(string repoPath, CancellationToken ct);

// Uninstall both hooks
Task<HookInstallationResult> UninstallHooksAsync(string repoPath, CancellationToken ct);

// Install individual hooks
Task<HookInstallationResult> InstallPreCommitHookAsync(string repoPath, CancellationToken ct);
Task<HookInstallationResult> UninstallPreCommitHookAsync(string repoPath, CancellationToken ct);

// Resolve Git's effective hook path and inspect both hooks
Task<GitHooksStatus> GetStatusAsync(string repoPath, CancellationToken ct);
```

**Error Handling**:

The service returns detailed error information via `HookInstallationResult`:

```csharp
public enum HookInstallationError
{
    HooksDirectoryNotFound,
    HooksDirectoryCreationFailed,
    PermissionDenied,
    FileReadError,
    FileWriteError,
    ChmodExecutionFailed,
    ScriptResourceNotFound,
    PartialInstallationFailure,
    UnknownError
}
```

**Configuration** ([appsettings.json](VibeRails/appsettings.json)):

```json
{
  "VibeRails": {
    "Hooks": {
      "AutoInstall": true,
      "InstallOnStartup": true
    }
  }
}
```

**Usage Examples**:

```csharp
// Install hooks
var result = await hookService.InstallHooksAsync(repoPath, cancellationToken);
if (!result.Success)
{
    Console.Error.WriteLine($"Installation failed: {result.ErrorMessage}");
    if (result.Details != null)
    {
        Console.Error.WriteLine($"Details: {result.Details}");
    }
}

// Check if installed
if ((await hookService.GetStatusAsync(repoPath, cancellationToken)).IsInstalled)
{
    Console.WriteLine("Hooks are installed");
}

// Uninstall hooks
var uninstallResult = await hookService.UninstallHooksAsync(repoPath, cancellationToken);
```

**API Endpoints**:

```
GET  /api/v1/hooks/status        # Check if hooks are installed
POST /api/v1/hooks/install       # Install hooks via API
DELETE /api/v1/hooks             # Uninstall hooks via API
POST /api/v1/hooks/preview       # Run the real pre-commit pipeline and capture its console
```

**Testing**:

Comprehensive test suite covers:
- ✅ Fresh installation in empty repository
- ✅ Creating hooks directory if it doesn't exist
- ✅ Appending to existing hooks from other tools
- ✅ Replacing old VibeRails hook versions
- ✅ Uninstalling while preserving other hooks
- ✅ Permission error handling
- ✅ Logging verification
- ✅ Atomic rollback on partial failures

Run tests:
```bash
cd Tests
dotnet test --filter "HookInstallationServiceTests"
```

**Design Patterns**:
- **Dependency Injection** - `ILogger<T>` injected for structured logging
- **Result Pattern** - Methods return `HookInstallationResult` instead of bool
- **Template Method** - Common installation logic extracted to `InstallHookAsync()`
- **Atomic Operations** - Rollback on failure ensures consistent state

**File Locations**:
```
VibeRails/
├── scripts/                          # Hook script templates
│   ├── pre-commit-hook.sh           # Pre-commit validation script
│   ├── commit-msg-hook.sh           # Commit message validation script
│   └── post-commit-hook.sh          # Post-commit (job trigger) script
├── Services/
│   ├── HookInstallationService.cs   # Main service implementation
│   └── HookInstallationResult.cs    # Result types
└──appsettings.json                   # Application configuration

.git/hooks/                           # Git hooks directory (per repo)
├── pre-commit                        # Installed pre-commit hook
└── commit-msg                        # Installed commit-msg hook
```

**Logging Output**:

The service provides comprehensive logging:
- Information: Hook installation start/completion
- Debug: Script loading, file operations, hook content details
- Warning: Missing end markers, partial content
- Error: Permission issues, file I/O failures, chmod failures

**Cross-Platform Behavior**:
- **Windows**: Hooks work via Git Bash (no chmod needed)
- **Linux/macOS**: Hooks made executable via `chmod +x`
- **All platforms**: Scripts use `#!/bin/sh` shebang for POSIX compatibility

**Security Considerations**:
- Scripts loaded from application directory, not user input
- File paths validated to prevent directory traversal
- Markers prevent accidental corruption of other hooks
- No shell injection vulnerabilities in hook execution

**Migration Notes**:

Previous implementation had these issues (fixed):
❌ Scripts hardcoded as C# strings (hard to maintain)
❌ Returns bool only (no error context)
❌ No logging (silent failures)
❌ No configuration support
❌ chmod failures ignored
❌ No tests
❌ No rollback on partial failure

Current implementation:
✅ Scripts in separate files (easy to edit and test)
✅ Detailed error results with error types
✅ Structured logging throughout
✅ Configurable auto-install behavior
✅ chmod failures reported
✅ Comprehensive test coverage
✅ Atomic operations with rollback

---

**Last Updated**: 2026-08-06
**Version**: 1.9.11
**Maintained By**: Robert Stokes

## Vibe Rails Rules
- Log all file changes (WARN)

---

*Last checked: 2026-08-06T17:22:10Z by opencode (glm-5.2)*
