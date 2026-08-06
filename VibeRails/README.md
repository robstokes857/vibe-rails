# VibeRails

**VibeRails** is an opinionated framework that helps keep AI coding assistants from going off the rails.

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![TypeScript](https://img.shields.io/badge/TypeScript-7.0-3178C6)](https://www.typescriptlang.org/)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

---
## Status: This repo is a lightweight, local-focused version of my personal setup. I'm stripping out multi-GPU/cluster support, heavy eval tooling, and other framework dependencies so it runs fast with Claude, Codex, and Antigravity CLIs. I'm rebuilding it around the features I think most people will actually want for local workflows.

## Overview
- **Environment Isolation** - Like Conda for LLMs. Create separate environments to experiment with Claude, Codex, or Antigravity settings without breaking your primary setup
- **Cross-LLM Learning** - Share context and learnings between different LLM providers (Claude, Codex, Antigravity)
- **RAG (Without The Rot) For Your Code** - Track things like repeated fixes the LLM forgets, including when you have to tell it the same thing 6 or 7 times in one session and it still doesn't understand, how you describe a feature and where that code lives, and file change summaries with commits, then only provide what’s useful at call time to prevent context rot.
- **Few Shot Prompting** - Get Antigravity or codex to code like Claude for code that has been done before with few shot prompting... Making them up to 20% better (research paper and eval data coming soon.)
- **Rule Enforcement** - Define and enforce coding standards like test coverage, cyclomatic complexity, logging practices, and more. LLMs fix their errors before code can be pushed or before the tech debt get astronomical.
- **Token Savings** - Learn your codebase and how you describe it, providing LLMs with smart file hints to reduce token usage and costs
- **AGENTS.md Management** - Create and manage agent instruction files following the [agents.md specification](https://agents.md/)

---

## Table of Contents

- [Quick Start](#quick-start)
- [Architecture](#architecture)
- [Components](#components)
- [Installation](#installation)
- [Usage](#usage)
- [Configuration](#configuration)
- [Development](#development)
- [API Reference](#api-reference)
- [Contributing](#contributing)
- [License](#license)

---

## Quick Start

### Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download) or later
- [Node.js 24 LTS+](https://nodejs.org/) (for VS Code extension development)
- Git
- One or more LLM CLIs: Claude, Codex, or Antigravity

### Installation

#### Option 1: Standalone Web Server

```bash
# Clone the repository
git clone https://github.com/robstokes857/vibe-rails.git
cd vibe-rails

# Build and run
cd VibeRails
dotnet run
```

The dashboard will open in your default browser at `http://localhost:<auto-detected-port>`.

#### Option 2: VS Code Extension

```bash
# Navigate to extension directory
cd vscode-viberails

# Install dependencies
npm install

# Compile TypeScript
npm run compile

# Open in VS Code
code .
```

Press `F5` to launch the extension in development mode, then click the VibeRails icon in the editor toolbar.

### First Steps

1. **Create an agent file** in your repository root named `agent.md`
2. **Add coding rules** with enforcement levels:
   ```markdown
   ## Vibe Rails Rules
   - Cyclomatic complexity < 20 (COMMIT)
   - Require test coverage minimum 80% (STOP)
   - Log all file changes (WARN)
   ```
3. **Launch a CLI** from the dashboard Web Terminal or from an environment's **Web UI** / native terminal action.

---

## Architecture

VibeRails consists of four main components:

```
┌─────────────────────────────────────────────────────────┐
│                   Browser / VS Code                      │
│              (Vanilla JS + Bootstrap 5)                  │
└────────────────────┬────────────────────────────────────┘
                     │ REST API
┌────────────────────▼────────────────────────────────────┐
│                  VibeRails Backend                       │
│              (ASP.NET Core + .NET 10.0)                  │
│  ┌──────────────┬──────────────┬──────────────────────┐ │
│  │  Services    │  Repository  │  LLM Environments    │ │
│  └──────────────┴──────────────┴──────────────────────┘ │
│  ┌──────────────────────────────────────────────────────┐ │
│  │  In-process MCP server  →  HTTP /mcp                  │ │
│  │  validate_vca | search_history | token-saver │ │
│  └──────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────┘
```

### Technology Stack

**Backend**
- **.NET 10.0** with Native AOT compilation
- **ASP.NET Core Slim** for lightweight web serving
- **SQLite** with WAL mode for concurrent access
- **ModelContextProtocol** for MCP integration
- **Pty.Net** for cross-platform terminal support

**Frontend**
- **Vanilla JavaScript** (ES6 modules, no frameworks)
- **Bootstrap 5** for UI components
- **Font Awesome 7** for icons
- **XTerm.js** for terminal emulation

**Extension**
- **TypeScript 7.0**
- **VS Code API 1.125+**

---

## Components

### 1. VibeRails Backend

The core ASP.NET application providing:

- **Web Server** - Auto-detects available port, serves REST API and static files
- **Service Layer** - Business logic for agents, rules, sessions, and MCP
- **Data Layer** - SQLite repository with WAL mode
- **LLM CLI Support** - Environment-specific configurations for Claude, Codex, Antigravity, Copilot, and OpenCode

**Key Services:**

| Service | Purpose |
|---------|---------|
| `AgentFileService` | Find and manage agent.md files |
| `RulesService` | Define and validate 14 built-in coding rules |
| `Repository` | SQLite data access (state.db) |
| `GitService` | Git repository interactions |
| `McpClientService` | MCP client with custom tools |
| `LlmCliEnvironmentService` | Multi-LLM environment management |

### 2. Web Dashboard

Single-page application with:

- **Agent Files View** - Browse and manage coding rules
- **Dashboard View** - Quick actions for launching CLIs
- **Session History** - Recent CLI sessions with terminal playback
- **Configuration** - API keys and environment settings

**Frontend Architecture:**
```
wwwroot/
├── app.js              # Main application (~1,430 lines)
├── js/modules/         # ~40 ES modules, key ones:
│   ├── terminal-multitab.js       (~121KB)  # Web terminal (multi-tab)
│   ├── environment-controller.js  (~93KB)   # Environment CRUD + Web UI launch
│   ├── rule-controller.js         (~70KB)   # Rule management
│   ├── code-analyzer-dashboard.js (~67KB)   # Code quality scan UI
│   ├── jobs-controller.js         (~84KB)   # Automated jobs
│   ├── agent-controller.js        (~64KB)   # Agent file management
│   ├── chat-history-sidebar.js    (~59KB)   # Session chat history
│   ├── vibe-rails-ai-controller.js(~56KB)   # Vibe AI inspector
│   ├── sandbox-controller.js      (~32KB)   # Sandbox CRUD + launch
│   ├── git-guard-preflight.js     (~35KB)   # Git Guard SSE dashboard
│   ├── settings-controller.js     (~38KB)   # App settings
│   ├── mcp-controller.js          (~22KB)   # MCP Explorer
│   ├── dashboard-controller.js    (~2KB)    # Dashboard view + preselection
│   └── utils.js                   (~23KB)
└── assets/             # Bootstrap, XTerm.js, icons
```

### 3. VS Code Extension

TypeScript extension that:

- Launches VibeRails backend process
- Detects port from stdout
- Creates webview panel with proper CSP
- Provides status bar button for quick access

**Key Features:**
- Automatic executable detection
- Health checks via `/health` (unauthenticated readiness probe; there is no `/api/v1/health`)
- Graceful shutdown (POST /api/v1/shutdown → close stdin → taskkill /T /F (Windows) or SIGTERM/SIGKILL)
- Asset path rewriting for webview sandbox

### 4. MCP Server

Hosted **in-process inside `vb.exe`** over HTTP at `/mcp` (Streamable HTTP, root backend only).
Tools (snake_case wire names):

| Tool | Purpose |
|------|---------|
| `validate_vca` | Validate staged git files against AGENTS.md enforcement rules |
| `search_history` | Semantic + keyword search over captured agent history (real BGE/sqlite-vec/RRF — the same `IUnifiedSearchService` as the "Vibe AI" inspector) |
| `pause_token_saver` / `resume_token_saver` / `get_token_saver_status` | Control the LLM-proxy token saver |

> `run_shell_command` / `get_shell_command_status` / `cancel_shell_command` and `web_search` / `web_fetch` are
> present in the codebase but **not currently exposed** as MCP tools (security review 2026-07-02).

**Architecture:**
- Streamable HTTP transport, served by the dashboard's Kestrel — no separate process
- Behind `CookieAuthMiddleware` (session token required)
- See [`Services/Mcp/AGENTS.md`](Services/Mcp/AGENTS.md) for the full design

---

## Installation

### Building from Source

#### Development Build

```bash
# Clone with submodules
git clone --recursive https://github.com/robstokes857/vibe-rails.git
cd vibe-rails

# Build backend
cd VibeRails
dotnet build

# Run tests
cd ../Tests
dotnet test

# Run application
cd ../VibeRails
dotnet run
```

#### Release Build (Native AOT)

```bash
cd VibeRails
dotnet publish -c Release -r win-x64 --self-contained

# Output: bin/Release/net10.0/win-x64/publish/vb.exe
```

**Platform-specific builds:**
```bash
# Windows
dotnet publish -c Release -r win-x64

# Linux
dotnet publish -c Release -r linux-x64

# macOS
dotnet publish -c Release -r osx-x64
```

### Building VS Code Extension

```bash
cd vscode-viberails

# Install dependencies
npm install

# Compile TypeScript
npm run compile

# Package extension
npx vsce package

# Install .vsix file
code --install-extension vscode-viberails-0.1.0.vsix
```

> The MCP server is built into `vb.exe` (in-process) — there is no separate MCP build step.

---

## Usage

### Application Modes

#### 1. Web Server Mode (Default)

```bash
vb
```

- Launches ASP.NET web server on auto-detected port
- Opens browser to dashboard UI
- Provides REST API at `http://localhost:<port>/api/v1/`

#### 2. Environment Bootstrap Mode (Internal Native-Terminal Wrapper)

```bash
vb --env production --workdir /path/to/repo
```

- Used by Web UI and VS Code launch paths when a tracked native terminal is needed
- Wraps LLM CLI execution with environment isolation and session tracking
- Captures terminal output for session logging
- Applies environment-specific configurations
- Stores session data in SQLite

#### 3. VS Code Extension Mode

1. Click **VibeRails icon** in editor toolbar
2. Extension spawns backend process
3. Dashboard loads in webview panel

#### 4. Git Guard Mode

```bash
vb --git-guard
```

- Opens the authenticated `/git-guard` focused web surface
- Captures the exact staged Git index snapshot (including partially staged files)
- Streams VCA, report-only MintLint, and automated-workflow stage events live to the browser via SSE (`POST /api/v1/git/preflight/stream`)
- VCA is the only preflight stage that can block a commit
- The native pre-commit hook uses the same shared pipeline

### Managing Agent Files

#### Create Agent File

```bash
# Via CLI (in repository root)
echo "## Vibe Rails Rules\n- WARN: log_files_changed" > agent.md

# Via Web UI
# Navigate to Agents → Create New Agent
```

#### Add Rules

14 built-in rules available (see [`RulesService.cs`](Services/RulesService.cs)):

| Rule (display text) | Description |
|------|-------------|
| Log all file changes | Document every changed file in AGENTS.md |
| Log file changes > 5 lines | Document files with > 5 changed lines |
| Log file changes > 10 lines | Document files with > 10 changed lines |
| Cyclomatic complexity < 20 | Enforce complexity under 20 |
| Cyclomatic complexity < 35 | Enforce complexity under 35 |
| Cyclomatic complexity < 60 | Lenient threshold (under 60) |
| Cyclomatic complexity disabled | Disable complexity checking |
| Require test coverage minimum 50% | At least 50% coverage for changed code |
| Require test coverage minimum 70% | At least 70% coverage for changed code |
| Require test coverage minimum 80% | At least 80% coverage for changed code |
| Require test coverage minimum 100% | 100% coverage for changed code |
| Skip test coverage | Disable coverage checking |
| Package file changes | Detect changes to dependency files |
| Check commit message for | Check commit message for forbidden words (CSV list) |

**Enforcement Levels:**
- `WARN` - Log warning, allow continuation
- `COMMIT` - Require explanation/acknowledgment in commit or PR message
- `STOP` - Block the commit/PR

#### Example agent.md

```markdown
# Development Agent

## Vibe Rails Rules
- Cyclomatic complexity < 20 (COMMIT)
- Require test coverage minimum 80% (STOP)
- Log all file changes (WARN)
- Package file changes (COMMIT)
- Check commit message for: typo,fixme (COMMIT)
```

### Environment Management

Create isolated environments for different contexts:

Use the Environments page to create named Claude, Codex, Antigravity, or Copilot profiles. Launch them through the Web Terminal, the environment row's **Web UI** button, or the native terminal action.

**Environment Configuration:**
```
~/.vibe_rails/envs/
├── development/
│   ├── claude/
│   │   └── config.json
│   ├── codex/
│   │   └── config.json
│   └── antigravity/        # Antigravity (agy) — launch-flag-only (no config file)
└── production/
    └── ...
```

### Session Logging

All CLI sessions are logged with full terminal output:

Launch from the Web Terminal or an environment action, then navigate to Sessions and select a session to view terminal output.

**Database Storage:**
- Global: `~/.vibe_rails/state.db` (single shared database; no per-project database)

### MCP Integration

The MCP server runs in-process at `/mcp` (Streamable HTTP). The dashboard Explorer defaults to that
local endpoint, but its REST routes can also inspect/call another user-supplied Streamable HTTP MCP
endpoint:

```bash
# Via REST API (requires the auth session + tab tokens from /auth/bootstrap)
curl -X POST http://localhost:5000/api/v1/mcp/tools/search_history \
  -H "Content-Type: application/json" \
  -d '{"arguments": {"query": "find authentication code", "maxResults": 5}}'
```

```csharp
// Via C# SDK — connect to the in-process HTTP endpoint
var transport = new HttpClientTransport(new HttpClientTransportOptions
{
    Endpoint = new Uri("http://127.0.0.1:5000/mcp"),
    TransportMode = HttpTransportMode.StreamableHttp,
    AdditionalHeaders = new Dictionary<string, string> { ["viberails_session"] = sessionToken }
});
await using var service = await McpClientService.ConnectAsync(transport);

var result = await service.CallToolAsync("search_history", new Dictionary<string, object?> {
    ["query"] = "find authentication code"
});
```

---

## Configuration

### Application Settings

**Global Configuration:**
```
~/.vibe_rails/
├── state.db                # SQLite database (single shared DB)
├── settings.json           # Application settings
└── envs/                   # Environment configurations
```

**settings.json Example:**
```json
{
  "RemoteAccess": false,
  "UseVsCodeTheme": false,
  "McpEnabled": true,
  "ClaudeTokenSaverEnabled": true,
  "RemoveCoAuthorTrailers": true
}
```

### Environment Variables

#### Claude
```bash
CLAUDE_CONFIG_DIR=~/.vibe_rails/envs/myenv/claude
```

#### Codex
```bash
CODEX_HOME=~/.vibe_rails/envs/myenv/codex
```

#### Antigravity (agy)
Launch-flag-only — VibeRails injects no per-environment config env vars for agy
(sandbox/permissions are launch flags).

#### Copilot
Launch-flag-only — no per-environment config env var (like agy).

#### OpenCode
```bash
XDG_CONFIG_HOME=~/.vibe_rails/envs/myenv
```
OpenCode resolves its config/agents/commands/plugins directory at `$XDG_CONFIG_HOME/opencode`.
Credentials are **not** isolated (`XDG_DATA_HOME` is unchanged, so auth stays in the user's global
OpenCode data directory).

---

## Development

### Project Structure

```
vibe-rails/
├── VibeRails/                  # ASP.NET Core backend
│   ├── Services/               # Business logic
│   ├── DB/                     # Data access layer
│   ├── DTOs/                   # Data transfer objects
│   ├── Routes/                 # API endpoint definitions (30 route files)
│   ├── wwwroot/                # Static web assets
│   └── VibeRails.csproj
│
├── vscode-viberails/           # VS Code extension
│   ├── src/
│   │   ├── extension.ts
│   │   ├── backend-manager.ts
│   │   ├── webview-panel.ts
│   │   └── constants.ts
│   └── package.json
│
├── TerminalEmulator/           # Terminal emulator library (project reference)
├── Pty.Net/                    # Cross-platform PTY library (inlined fork, ConPTY only)
├── PyBridge/                   # AOT-friendly Python runner (in-tree)
├── MintLint/                   # Maintainability grading library
├── TokenSaver/                 # Token-saver library
├── Tests/                      # xUnit unit tests
├── TerminalEmulator.Tests/     # Terminal emulator tests
├── IntegrationTest/            # Integration tests
├── UITests/                    # Playwright E2E / UI tests
├── deploy/                     # Build & release scripts
├── Scripts/                    # Install scripts
├── runbooks/                   # Operational documentation
└── python-scripts/             # Python helper scripts
```

### Running Tests

```bash
# Unit tests
cd Tests
dotnet test

# With coverage
dotnet test /p:CollectCoverage=true

# UI/E2E tests (Playwright, Node)
cd UITests
npm test
```

### Adding a New Rule

1. **Define in [RulesService.cs](VibeRails/Services/RulesService.cs)**:
   - Add a value to the `Rule` enum
   - Add a display string to `RuleParser._keyValuePairs`
   - Add a description to `RuleParser._descriptions`
```csharp
// In the Rule enum:
MyNewRule,

// In _keyValuePairs:
{ Rule.MyNewRule, "My new rule display text" },

// In _descriptions:
{ Rule.MyNewRule, "Description of the rule" },
```

2. **Update frontend** in [rule-controller.js](VibeRails/wwwroot/js/modules/rule-controller.js)

3. **Implement enforcement logic** in appropriate service

### Adding a New LLM CLI

1. **Create environment class**:
```csharp
public class MyLlmCliEnvironment : BaseLlmCliEnvironment
{
    protected override Dictionary<string, string> GetEnvironmentVariables()
    {
        return new Dictionary<string, string> {
            ["MY_CLI_HOME"] = GetConfigPath()
        };
    }
}
```

2. **Register in [MapRegisterServices.cs](VibeRails/MapRegisterServices.cs)**:
```csharp
builder.Services.AddSingleton<IMyLlmCliEnvironment, MyLlmCliEnvironment>();
```

3. **Update [LLM.cs](VibeRails/DTOs/LLM.cs)** enum

4. **Add launcher logic** in [LaunchLLMService.cs](VibeRails/Services/LlmClis/LaunchLLMService.cs)

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

2. **Register** in [MapRegisterServices.cs](VibeRails/MapRegisterServices.cs) — add
   `.WithTools<MyCustomTool>()` to the `AddMcpServer()` chain (and `AddScoped<MyCustomTool>()`
   if it needs app services injected, like `SessionSearchTool`).

3. **Add a test** in [Tests/Services/Mcp/](Tests/Services/Mcp/). See
   [Services/Mcp/AGENTS.md](VibeRails/Services/Mcp/AGENTS.md) for the full design.

### Build Scripts

```powershell
# Full build + deploy packaging
.\deploy\build.ps1

# Backend-only dev build
dotnet build

# Run tests
cd Tests
dotnet test
```

---

## API Reference

### Base URL

```
http://localhost:<auto-detected-port>/api/v1/
```

### Endpoints

> The full surface lives in [`VibeRails/Routes/`](Routes/) (30 route files). The major groups:

#### Agent & Rule Management

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/agents` | List all agent files |
| GET | `/agents/rules?path={path}` | Get rules from agent file |
| GET | `/agents/content` / `/agents/files` | Agent file content / file tree |
| POST | `/agents` | Create agent file |
| POST | `/agents/rules` | Add rule to agent |
| POST | `/agents/validate` | Validate agent file |
| PUT | `/agents/rules/enforcement` | Update rule enforcement level |
| PUT | `/agents/name` | Rename agent file |
| DELETE | `/agents/rules` | Delete rules |
| GET | `/rules` / `/rules/details` | List available rule definitions |

#### Environments & CLI Launch

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/environments` | List all environments |
| GET | `/environments/{name}` | Get a single environment |
| POST | `/environments` | Create environment |
| PUT | `/environments/{name}` | Update environment |
| DELETE | `/environments/{name}` | Delete environment |
| GET | `/environments/{name}/launch` | Get environment launch variables |
| POST | `/cli/launch/{cli}` | Launch CLI in terminal |
| POST | `/cli/launch/vscode` | Launch VS Code |
| GET/PUT | `/claude/settings/{envName}` · `/codex/settings/{envName}` | Per-env Claude/Codex settings |

#### Web Terminal

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/terminal/start` | Start a terminal session |
| POST | `/terminal/stop` | Stop a terminal session |
| POST | `/terminal/input` | Send input to terminal |
| GET | `/terminal/snapshot` | Get terminal snapshot |
| GET | `/terminal/status` | Get terminal status |
| GET | `/terminal/bootstrap-command` | Get bootstrap command |
| GET/POST/DELETE | `/terminal/tabs[/{tabId}]` | Multi-tab terminal management |

#### Sandboxes

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/sandboxes` | List sandboxes for current project |
| POST | `/sandboxes` | Create sandbox (shallow clone + dirty files) |
| DELETE | `/sandboxes/{id}` | Delete sandbox (dir + DB record) |
| POST | `/sandboxes/{id}/launch/{cli\|shell\|vscode}` | Launch into sandbox |
| GET | `/sandboxes/{id}/diff` | Diff sandbox against source |
| POST | `/sandboxes/{id}/push` · `/sandboxes/{id}/merge` | Push/merge sandbox changes |

#### Hooks, Git Guard & Code Analyzer

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/hooks/status` | Check if git hooks are installed |
| POST | `/hooks/install` | Install git hooks |
| DELETE | `/hooks` | Uninstall git hooks |
| POST | `/hooks/preview` · `/hooks/validate` | Run/validate the pre-commit pipeline |
| POST | `/git/preflight/stream` | Stream staged-index Git Guard events as SSE |
| POST | `/git/preflight/console` | Run preflight and capture console output |
| GET/POST/DELETE | `/code-analyzer` · `/code-analyzer/ignores` · `/code-analyzer/source` | Code quality scan + ignore rules |

#### Jobs & Automation

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET/POST/PUT/DELETE | `/jobs[/{id}]` | CRUD for automated jobs |
| POST | `/jobs/{id}/run` | Run a job |
| GET | `/jobs/runs[/{runId}]` | List/get job runs |
| POST | `/jobs/runs/{runId}/cancel` · `/retry` | Cancel/retry a job run |

#### Session Logging & Chat History

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/sessions/{sessionId}/logs` | Get session terminal logs |
| GET | `/sessions/{sessionId}/inputs` | Get session user inputs |
| GET | `/sessions/{sessionId}/output` | Get session output |
| GET | `/sessions/recent` | Get recent CLI sessions |
| GET/DELETE | `/chatHistory[/{sessionId}]` | Chat history list / per-session |
| GET | `/chatHistory/{sessionId}/summary` · `/transcript` · `/replay` | Summary, transcript, replay |

#### MCP Integration

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/mcp/status` | MCP server connection status |
| GET | `/mcp/tools` | List available MCP tools |
| POST | `/mcp/inspect` | Inspect tools on a local or remote MCP endpoint |
| POST | `/mcp/tools/{name}` | Execute MCP tool |

#### Search, Settings & Utility

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/context` | Get project context (replaces the old `/IsLocal`) |
| GET | `/projects/name` · PUT | Get/set project display name |
| GET | `/health` | Health check (extension startup verification) |
| GET/POST | `/settings` · `/settings/db-size` · `/settings/pin` | App settings, DB size, pin status |
| POST | `/settings/export-data` | Export all data as a zip |
| POST | `/search` · `/bert/search` | Unified / BERT semantic search |
| GET | `/version` · `/update/check` · `/update/version` | Version + update checks |
| GET | `/token-savings` | Token savings metrics |
| GET/POST/DELETE | `/compression/captures` · `/compression/catalog` | Compression capture records |
| POST | `/shutdown` · `/lifecycle/ping` · `/lifecycle/disconnect` | Lifecycle control |

### Example Requests

#### Create Agent File

```http
POST /api/v1/agents
Content-Type: application/json

{
  "path": "/path/to/repo",
  "initialRules": [
    {
      "name": "max_complexity",
      "value": "10",
      "enforcement": "COMMIT"
    }
  ]
}
```

#### Add Rule

```http
POST /api/v1/agents/rules
Content-Type: application/json

{
  "agentPath": "/path/to/agent.md",
  "ruleName": "min_coverage",
  "ruleValue": "80",
  "enforcement": "STOP"
}
```

#### Launch CLI

```http
POST /api/v1/cli/launch/claude
Content-Type: application/json

{
  "environment": "production",
  "workingDirectory": "/path/to/repo"
}
```

---

## Contributing

We welcome contributions! Please follow these guidelines:

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

1. Fork the repository
2. Create feature branch: `git checkout -b feature/amazing-feature`
3. Implement changes with tests
4. Update documentation (including this README if needed)
5. Run tests: `dotnet test`
6. Commit changes: `git commit -m 'Add amazing feature'`
7. Push to branch: `git push origin feature/amazing-feature`
8. Open Pull Request

### Development Setup

```bash
# Clone with submodules
git clone --recursive https://github.com/robstokes857/vibe-rails.git
cd vibe-rails

# Install .NET 10.0 SDK
# https://dotnet.microsoft.com/download

# Build solution
dotnet build

# Run tests
cd Tests
dotnet test

# Start development server
cd ../VibeRails
dotnet run
```

---

## Troubleshooting

### Common Issues

**Issue: Agent files not found**
- **Cause**: Not in git repository or agent.md not at repo root
- **Solution**: Run from git repository root, create `agent.md` file

**Issue: LLM CLI not launching**
- **Cause**: CLI not in PATH or incorrect environment configuration
- **Solution**: Verify CLI installation with `which claude`, check environment variables

**Issue: Session logs not recording**
- **Cause**: Database connection issue or insufficient permissions
- **Solution**: Check `~/.vibe_rails/` directory permissions, verify SQLite access

**Issue: MCP tools not available / Explorer can't connect**
- **Cause**: `/mcp` is auth-gated (needs the `viberails_session` token) and hosted only by the root backend.
- **Solution**: Use it from the dashboard (the Explorer forwards the session token). See [Services/Mcp/AGENTS.md](Services/Mcp/AGENTS.md).

### Debug Logging

Enable verbose logging in [Program.cs](VibeRails/Program.cs:1):
```csharp
builder.Logging.SetMinimumLevel(LogLevel.Debug);
```

---

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

---

## Resources

### Documentation

- [ASP.NET Core Documentation](https://docs.microsoft.com/aspnet/core)
- [Model Context Protocol Spec](https://modelcontextprotocol.io/)
- [ModelContextProtocol NuGet](https://www.nuget.org/packages/ModelContextProtocol)
- [XTerm.js Documentation](https://xtermjs.org/)
- [VS Code Extension API](https://code.visualstudio.com/api)

### Related Projects

- [Claude CLI](https://docs.anthropic.com/claude/docs/cli) - Anthropic's Claude command-line interface
- [OpenAI Codex](https://openai.com/blog/openai-codex) - OpenAI Codex command-line tool
- [Google Antigravity](https://antigravity.google/) - Google Antigravity command-line interface (`agy`)

### Key Dependencies

- **Microsoft.Data.Sqlite** (v10.0.10) - SQLite database access
- **ModelContextProtocol / ModelContextProtocol.AspNetCore** (v2.0.0) - in-process MCP server + client
- **Microsoft.ML.OnnxRuntime + sqlite-vec** - BGE embeddings + vector search (the real `search_history` backend)
- **Pty.Net** (inlined fork, ConPTY only) - Pseudo-terminal support
- **PyBridge** (in-tree) - AOT-friendly Python process and session runner

---

## Acknowledgments

Built with modern .NET 10.0, Native AOT compilation, and the Model Context Protocol for next-generation AI development workflows.

**Last Updated**: 2026-08-06
**Version**: 1.9.11
**Maintained By**: Robert Stokes

---

*Last checked: 2026-08-06T17:54:34Z by opencode (glm-5.2)*
