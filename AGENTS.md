# AGENTS.md - VibeRails Project Documentation

## Terminology Note

**"Web UI Chat"** refers to the xterm.js-based terminal interface where users interact with CLI tools (Claude, Codex, Gemini) through a browser-based terminal emulator. This is NOT a separate chat UI - it's the PTY-backed terminal that runs actual CLI sessions.

## Project Overview

**VibeRails** is a sophisticated desktop/web application for managing and enforcing coding standards across AI-powered development workflows. It serves as a unified control panel for multiple LLM CLIs (Claude, Codex, Gemini) with comprehensive rule enforcement, session logging, and MCP integration.

**Live Site**: [https://viberails.ai/](https://viberails.ai/)

### Core Capabilities
- **Agent File Management** - Create and manage `agent.md` files with customizable coding rules
- **Rule Enforcement** - Define standards with three enforcement levels (WARN/COMMIT/STOP)
- **Multi-LLM Support** - Unified interface for Claude, Codex, and Gemini CLIs
- **Environment Management** - Configure separate environments for different LLM providers with custom args and prompts. Launch environments directly in the Web UI terminal with the "Web UI" button or select from the terminal's environment dropdown
- **Session Logging** - Track and monitor all CLI session history and outputs
- **MCP Integration** - Custom Model Context Protocol server with specialized tools

## Technology Stack

### Backend
- **.NET 10.0** - Modern .NET with AOT compilation support
- **ASP.NET Core Slim** - Lightweight web server
- **SQLite** - Local database with WAL mode for concurrency
- **ModelContextProtocol NuGet Package** (v0.5.0-preview.1) - MCP foundation with custom service layer and tools
- **Pty.Net** - Cross-platform pseudo-terminal support (git submodule)

### Frontend
- **Vanilla JavaScript** - No framework dependencies
- **Bootstrap 5** - UI framework
- **Font Awesome 6** - Icon library
- **XTerm.js** - Terminal emulation in browser
- **Fetch API** - REST API communication

### Build & Testing
- **xUnit 2.9** - Unit testing framework
- **Native AOT** - Ahead-of-time compilation for standalone executables
- **PowerShell** - Build automation scripts

## Project Structure

> **Note:** The project directory is named `VibeControl2` for legacy reasons, but the project branding is **VibeRails**.

```
VibeControl2/
├── VibeRails/                      # Main ASP.NET Core application
│   ├── Program.cs                  # Entry point (web server + CLI loop)
│   ├── Init.cs                     # Dependency injection setup
│   ├── CliLoop.cs                  # CLI interaction loop
│   ├── Routes.cs                   # API endpoint definitions
│   │
│   ├── Services/                   # Business logic layer
│   │   ├── AgentFileService.cs    # Agent file management
│   │   ├── DbService.cs           # Database operations wrapper
│   │   ├── FileService.cs         # File system abstraction
│   │   ├── GitService.cs          # Git repository interaction
│   │   ├── RulesService.cs        # Rule parsing and enforcement
│   │   ├── Mcp/
│   │   │   └── McpClientService.cs # Custom MCP client service
│   │   └── LlmClis/               # LLM CLI environment management
│   │       ├── LlmCliEnvironmentService.cs
│   │       ├── LaunchLLMService.cs
│   │       ├── BaseLlmCliEnvironment.cs
│   │       ├── ClaudeLlmCliEnvironment.cs
│   │       ├── CodexLlmCliEnvironment.cs
│   │       ├── GeminiLlmCliEnvironment.cs
│   │       └── Launchers/         # Platform-specific terminal launchers
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
│   │   ├── LLM.cs                  # LLM enum (Claude, Codex, Gemini)
│   │   ├── LLM_Environment.cs      # Environment configuration
│   │   ├── McpDtos.cs              # MCP protocol DTOs
│   │   └── StateFileObject.cs
│   │
│   ├── Interfaces/                 # Service contracts
│   │   ├── IFileService.cs
│   │   ├── IDbService.cs
│   │   ├── IBaseLlmCliEnvironment.cs
│   │   ├── IMcpService.cs
│   │   └── *LlmCliEnvironment.cs
│   │
│   ├── Utils/                      # Utility classes
│   │   ├── Configs.cs              # Configuration management
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
│       │   ├── terminal-controller.js  # Web terminal with environment-aware selector
│       │   ├── environment-controller.js # Environment CRUD + Web UI launch button
│       │   └── dashboard-controller.js  # Dashboard with state passing for preselection
│       └── assets/                 # Images, fonts, icons
│
├── MCP_Server/                     # Standalone custom MCP server
│   ├── Program.cs                  # MCP server entry point (Stdio transport)
│   ├── Tools/                      # Custom MCP tools
│   │   ├── EchoTool.cs            # Echo/test tool
│   │   └── RulesTool.cs           # Content validation rules
│   └── Models/                     # MCP message models
│
├── PtyNet/                         # Git submodule: PTY library
│   └── src/Pty.Net/               # Cross-platform terminal emulation
│
├── Tests/                          # xUnit test suite
│   ├── AgentFileServiceTests.cs
│   └── IntegrationAgentFileTests.cs
│
└── build/                          # Build scripts
    ├── build.ps1
    ├── debug_ubuntu.ps1
    ├── test_ubuntu.ps1
    └── interactive_ubuntu.ps1
```

## Architecture

### Application Modes

VibeRails operates in two primary modes:

#### 1. Web Server Mode (Default)
```bash
vb
```
- Launches ASP.NET web server on available port
- Opens browser to dashboard UI
- Provides REST API for managing agents, environments, sessions
- Runs background CLI loop (currently minimal)

#### 2. Terminal Session Mode (CLI Wrapper)
```bash
vb --env claude                    # Launch base CLI with session tracking
vb --env "my-research-setup"       # Launch custom environment (DB lookup)
vb --env gemini --workdir /project # Explicit working directory
```
- Unified `--env` flag (`--environment` and `--lmbootstrap` are aliases)
- Smart resolution: LLM name → base CLI, otherwise → custom environment DB lookup
- `--workdir` optional: uses git root if available, falls back to current directory
- Wraps LLM CLI execution (claude/codex/gemini) with environment isolation
- Full session tracking: database logging, user input tracking, git change detection
- Stores session data in SQLite database with complete history

See [Cli/AGENTS.md](VibeRails/Cli/AGENTS.md) for full details.

### Component Interaction Flow

#### Agent Rule Management Flow
```
Browser (app.js)
  ↓ [GET /api/v1/agents]
ASP.NET Route Handler (Routes.cs)
  ↓ DI injects IAgentFileService
AgentFileService
  ↓ Uses IGitService to find repository root
  ↓ Scans for agent.md/agents.md files
  ↓ Uses IRulesService to parse and validate rules
  ↓ Optional: DbService for project tracking
Response [AgentFileListResponse]
  ↓ [JSON]
Browser renders agent list with rules
```

#### Session Logging Flow
```
vb --env myenv (or vb --env claude)
  ↓
CliLoop.RunTerminalSessionAsync()
  ↓ Smart resolves: LLM name → base CLI, custom name → DB lookup
  ↓ Resolves working directory (--workdir or git root)
  ↓ Creates TerminalSession (session lifecycle)
  ↓ Creates PtyService (PTY management)
  ↓ Sets isolated config env vars via LlmCliEnvironmentService
  ↓   - CLAUDE_CONFIG_DIR, CODEX_HOME, XDG_*, etc.
  ↓ Spawns PTY via EzTerminal with output/input handlers
  ↓ Executes: claude [args]
  ↓
Terminal output → TerminalOutputFilter → TerminalSession.HandleOutput()
  ↓
Terminal input → InputAccumulator → TerminalSession.HandleInput()
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
  └─→ IGeminiLlmCliEnvironment
        └─ Config: XDG_CONFIG_HOME, XDG_DATA_HOME, etc.

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
  Body: { cli: "Gemini", environmentName: "test_g" }
  ↓
Backend: TerminalRoutes.cs resolves LLM enum, fetches custom args from DB
  ↓
TerminalSessionService.StartSessionAsync() spawns LLM CLI directly in PTY
  ↓ Creates TerminalSession for tracking
  ↓ Sets isolated environment vars (XDG_CONFIG_HOME, etc.)
  ↓ Spawns: gemini --yolo (with environment isolation)
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
VibeRails (Main App)
  ↓
McpClientService (Custom service layer)
  ↓ Uses ModelContextProtocol NuGet package
  ↓ Stdio transport
  ↓
MCP_Server.exe (Separate process)
  ↓ Uses ModelContextProtocol NuGet package
  ↓ Custom tools:
  ├─→ EchoTool (test/debug)
  └─→ RulesTool (content validation)
```

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

**Rule Format**:
```markdown
# Agent Instructions

## Rules
- COMMIT: max_complexity=10
- STOP: min_coverage=80
- WARN: log_files_changed
```

#### RulesService ([Services/RulesService.cs](VibeRails/Services/RulesService.cs))
**Purpose**: Define available rules and enforcement logic

**Available Rules** (12 total):
1. `max_complexity` - Maximum cyclomatic complexity
2. `min_coverage` - Minimum test coverage percentage
3. `log_files_changed` - Log all file modifications
4. `no_console_logs` - Prevent console.log in production
5. `max_file_length` - Maximum lines per file
6. `require_tests` - Tests required for new code
7. `no_todo_comments` - Prevent TODO comments in commits
8. `enforce_naming_conventions` - Enforce naming patterns
9. `max_method_length` - Maximum lines per method
10. `require_documentation` - Documentation required
11. `no_magic_numbers` - Prevent hardcoded numbers
12. `enforce_error_handling` - Require error handling

**Enforcement Levels**:
- `WARN` - Log warning, allow continuation
- `COMMIT` - Block commits that violate rule
- `STOP` - Immediately halt execution on violation

#### DbService ([Services/DbService.cs](VibeRails/Services/DbService.cs))
**Purpose**: High-level database operations wrapper

**Key Methods**:
- `GetOrCreateProjectAsync(repoPath)` - Get/create project record
- `GetEnvironmentAsync(name)` - Retrieve environment configuration
- `CreateSessionAsync()` - Start new CLI session
- `LogSessionOutputAsync()` - Append terminal output to session log
- `GetRecentProjectsAsync()` - Get recently used projects
- `GetRecentSessionsAsync()` - Get recent CLI sessions

#### GitService ([Services/GitService.cs](VibeRails/Services/GitService.cs))
**Purpose**: Git repository operations

**Key Methods**:
- `IsGitRepositoryAsync(path)` - Check if directory is git repo
- `GetGitRootAsync(path)` - Find repository root directory
- `GetCurrentBranchAsync()` - Get active git branch
- `GetRecentCommitsAsync()` - Retrieve commit history

#### McpClientService ([Services/Mcp/McpClientService.cs](VibeRails/Services/Mcp/McpClientService.cs))
**Purpose**: Custom MCP client service layer built on ModelContextProtocol NuGet package

**Architecture**:
- Wraps `ModelContextProtocol.Client.McpClient` with custom logic
- Provides builder pattern for configuration
- Handles Stdio transport communication with MCP_Server

**Key Methods**:
- `ConnectAsync()` - Establish connection to MCP server
- `GetAvailableToolsAsync()` - List available MCP tools
- `CallToolAsync(name, args)` - Execute MCP tool with arguments
- `DisconnectAsync()` - Close MCP server connection

**Usage**:
```csharp
var service = await McpClientService.ConnectAsync(
    transport: new StdioClientTransport("MCP_Server.exe"),
    clientName: "vibecontrol-client",
    version: "1.0.0"
);
var tools = await service.GetAvailableToolsAsync();
var result = await service.CallToolAsync("vector_search", args);
```

### MCP Server

#### MCP_Server ([MCP_Server/Program.cs](MCP_Server/Program.cs))
**Purpose**: Standalone MCP server with custom tools

**Implementation**:
- Built on `ModelContextProtocol.Server` NuGet package
- Stdio transport for process-to-process communication
- Registered as hosted service in .NET Generic Host
- Logging disabled to prevent stdio corruption

**Custom Tools**:

##### EchoTool ([MCP_Server/Tools/EchoTool.cs](MCP_Server/Tools/EchoTool.cs))
- **Purpose**: Test/debug tool
- **Input**: Any message string
- **Output**: Echoes the message back
- **Use Case**: Verify MCP communication working

##### RulesTool ([MCP_Server/Tools/RulesTool.cs](MCP_Server/Tools/RulesTool.cs))
- **Purpose**: Content validation against defined rules
- **Input**: Content string, rule definitions
- **Output**: Validation results with violations
- **Use Case**: Pre-commit validation, code review automation

### Data Layer

#### Repository ([DB/Repository.cs](VibeRails/DB/Repository.cs))
**Purpose**: SQLite data access implementation

**Database Tables**:
- `RecentProjects` - Git repository tracking
  - `Id`, `Path`, `Name`, `LastUsedUTC`
- `LlmEnvironments` - Environment configurations
  - `Id`, `Name`, `LlmType`, `ConfigJson`
- `Sessions` - CLI session metadata
  - `Id`, `ProjectId`, `EnvironmentId`, `StartedUTC`, `EndedUTC`, `ExitCode`
- `SessionLogs` - Terminal output logs
  - `Id`, `SessionId`, `Timestamp`, `Content`

**Configuration**:
- WAL mode enabled for concurrent access
- Foreign keys enforced
- Indexes on `LastUsedUTC` for performance

**Database Location**:
- Global: `~/.vibe_rails/vibecontrol.db`
- Per-project: `.vibe_rails/vibecontrol.db`

### API Layer

#### Routes ([Routes.cs](VibeRails/Routes.cs))
**Purpose**: REST API endpoint definitions

**Agent Management**:
- `GET /api/v1/agents` - List agent files
- `GET /api/v1/agents/rules?path={path}` - Get agent rules
- `POST /api/v1/agents` - Create agent file
- `POST /api/v1/agents/rules` - Add rule
- `PUT /api/v1/agents/rules/enforcement` - Update enforcement
- `DELETE /api/v1/agents/rules` - Delete rules
- `DELETE /api/v1/agents?path={path}` - Delete agent file
- `GET /api/v1/rules` - List available rules

**Environment & CLI**:
- `GET /api/v1/projects/recent` - Recent projects
- `GET /api/v1/environments/{name}/launch` - Get environment vars
- `POST /api/v1/cli/launch/{cli}` - Launch CLI in terminal
- `POST /api/cli/launch/vscode` - Launch VS Code

**Session Logging**:
- `GET /api/v1/sessions/{sessionId}/logs` - Get session logs
- `GET /api/v1/sessions/recent` - Recent sessions

**MCP Integration**:
- `GET /api/v1/mcp/status` - MCP server status
- `GET /api/v1/mcp/tools` - List MCP tools
- `POST /api/v1/mcp/tools/{name}` - Call MCP tool

**Utility**:
- `GET /api/v1/IsLocal` - Check if in git repo

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
All services registered in [Init.cs](VibeRails/Init.cs) with appropriate lifetimes:
- **Scoped**: Services tied to request lifecycle (DbService, Repository)
- **Singleton**: Long-lived services (GitService, RulesService, MCP settings)

### Repository Pattern
`IRepository` interface abstracts SQLite data access, allowing easy testing and potential database swaps.

### Service Layer Pattern
Business logic isolated from HTTP concerns. Services are reusable across CLI and web modes.

### Factory Pattern
`BaseLlmCliLauncher` base class with platform-specific implementations:
- `WindowsLlmCliLauncher` - Windows Terminal, PowerShell
- `MacLlmCliLauncher` - Terminal.app, iTerm2
- `LinuxLlmCliLauncher` - gnome-terminal, konsole, xterm

### Configuration Pattern
`Configs` static class manages runtime configuration paths and application state.

### Strategy Pattern
Different LLM CLI environments implement `IBaseLlmCliEnvironment` with specific configuration logic.

### Builder Pattern
`McpClientService` uses builder pattern for flexible client configuration.

## Configuration & File Locations

### Application Configuration
```
~/.vibe_rails/                    # Global config directory
├── vibecontrol.db                  # SQLite database
├── config.json                     # Application settings
├── history/                        # CLI command history
└── envs/                           # Environment configurations
    ├── myenv/
    │   ├── claude/                 # Claude CLI config
    │   │   └── config.json
    │   ├── codex/                  # Codex CLI config
    │   │   └── config.json
    │   └── gemini/                 # Gemini CLI config
    │       └── config.json
    └── production/
        └── ...
```

### Project-Level Configuration
```
project-root/
├── .git/
├── agent.md                        # or agents.md
├── .vibe_rails/                  # Optional project-specific config
│   └── vibecontrol.db              # Project-specific database
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

**Gemini**:
```bash
XDG_CONFIG_HOME=~/.vibe_rails/envs/myenv/gemini/config
XDG_DATA_HOME=~/.vibe_rails/envs/myenv/gemini/data
XDG_STATE_HOME=~/.vibe_rails/envs/myenv/gemini/state
XDG_CACHE_HOME=~/.vibe_rails/envs/myenv/gemini/cache
```

## Development Workflows

### Adding a New Rule

1. **Define rule in RulesService.cs**:
```csharp
new Rule(
    "my_new_rule",
    "Description of the rule",
    "parameter_name",
    "WARN"
)
```

2. **Update agent.md files**:
```markdown
## Rules
- COMMIT: my_new_rule=value
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

2. **Register in Init.cs**:
```csharp
builder.Services.AddSingleton<IMyLlmCliEnvironment, MyLlmCliEnvironment>();
```

3. **Update LLM enum** in [DTOs/LLM.cs](VibeRails/DTOs/LLM.cs)

4. **Add launcher logic** in [Services/LlmClis/LaunchLLMService.cs](VibeRails/Services/LlmClis/LaunchLLMService.cs)

5. **Update frontend** to support new CLI option

### Adding a New MCP Tool

1. **Create tool class** in MCP_Server/Tools:
```csharp
public class MyCustomTool : IMcpTool
{
    public string Name => "my_tool";
    public string Description => "Tool description";

    public Task<McpToolResult> ExecuteAsync(
        Dictionary<string, object> arguments,
        CancellationToken cancellationToken)
    {
        // Tool implementation
    }
}
```

2. **Register in MCP_Server/Program.cs**:
```csharp
.WithTools<MyCustomTool>()
```

3. **Rebuild MCP_Server** project

4. **Call from client**:
```csharp
await mcpService.CallToolAsync("my_tool", arguments);
```

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
# Build and run debug mode
.\build\debug_ubuntu.ps1

# Or interactive mode
.\build\interactive_ubuntu.ps1
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

### Building MCP Server Separately
```bash
cd MCP_Server
dotnet publish -c Release
```

### Cross-Platform Builds
```powershell
# Windows
.\build\build.ps1

# Ubuntu/Linux
.\build\debug_ubuntu.ps1
.\build\test_ubuntu.ps1
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
    ruleName: "max_complexity",
    ruleValue: "10",
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
// Use: DbService.GetSessionLogsAsync()
var logs = await dbService.GetSessionLogsAsync(sessionId);
```

### Task: Call MCP tool
```csharp
// Use: McpClientService.CallToolAsync()
var result = await mcpService.CallToolAsync(
    "vector_search",
    new Dictionary<string, object> {
        ["query"] = "find similar code"
    }
);
```

### Task: Add custom MCP tool
1. Create new class in `MCP_Server/Tools/`
2. Implement `IMcpTool` interface
3. Register in `MCP_Server/Program.cs`
4. Rebuild and restart server

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

**Issue**: MCP server not connecting
- **Cause**: MCP_Server.exe not found or port conflict
- **Solution**: Ensure MCP_Server.exe built and located correctly, check process spawning

**Issue**: MCP tools not available
- **Cause**: Server not initialized or stdio transport corrupted
- **Solution**: Check server logging (if enabled for debug), verify stdio not blocked by console output

### Debug Logging

Enable verbose logging in [Program.cs](VibeRails/Program.cs):
```csharp
// Modify logging level
builder.Logging.SetMinimumLevel(LogLevel.Debug);
```

**Note**: For MCP_Server, logging is disabled by default to prevent stdio corruption. Enable only for debugging with file-based logging.

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
1. Create feature branch from `master`
2. Implement changes with tests
3. Update documentation (this file if architecture changes)
4. Run `dotnet test` to verify all tests pass
5. Submit PR with clear description

## Security Considerations

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
- MCP server runs as separate process with limited privileges
- Stdio transport prevents network-based attacks
- Tool execution sandboxed within server process
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
- Stdio transport for efficient IPC (no network overhead)
- Tools executed asynchronously
- Server process kept alive between calls

## Future Enhancements

### Planned Features
- [ ] Rule enforcement automation (pre-commit hooks)
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
- [Pty.Net (Forked)](../PtyNet/README.md)
- [XTerm.js Documentation](https://xtermjs.org/)

### Related Projects
- **Claude CLI** - Anthropic's Claude command-line interface
- **Codex CLI** - OpenAI Codex command-line tool
- **Gemini CLI** - Google Gemini command-line interface
- **MCP SDK** - Model Context Protocol development kit

### Key Dependencies
- **Microsoft.Data.Sqlite** (v10.0.2) - SQLite database access
- **ModelContextProtocol** (v0.5.0-preview.1) - MCP foundation
- **Pty.Net** (git submodule) - Pseudo-terminal support

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
   - Runs `vb --validate-vca --pre-commit`
   - Blocks commits if validation fails
   - Allows bypass with `git commit --no-verify`

2. **commit-msg-hook.sh** - Validates COMMIT-level acknowledgments
   - Runs `vb --commit-msg "$1"`
   - Ensures required acknowledgments in commit message
   - Enforces COMMIT-level rule compliance

**Installation Behavior**:
- **Auto-install on startup** - Hooks installed automatically when VibeRails starts (configurable)
- **Preserves existing hooks** - Appends to existing hook files, doesn't overwrite
- **Marker-based management** - Uses markers to track VibeRails sections
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

// Check installation status
bool IsHookInstalled(string repoPath);
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

**Configuration** ([app_config.json](VibeRails/app_config.json)):

```json
{
  "hooks": {
    "autoInstall": true,
    "installOnStartup": true
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
if (hookService.IsHookInstalled(repoPath))
{
    Console.WriteLine("Hooks are installed");
}

// Uninstall hooks
var uninstallResult = await hookService.UninstallHooksAsync(repoPath, cancellationToken);
```

**CLI Commands**:

```bash
# Check hook status
vb hooks status

# Install hooks manually (if auto-install disabled)
vb hooks install

# Uninstall hooks
vb hooks uninstall
```

**API Endpoints**:

```
GET  /api/v1/hooks/status        # Check if hooks are installed
POST /api/v1/hooks/install       # Install hooks via API
DELETE /api/v1/hooks             # Uninstall hooks via API
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
│   └── commit-msg-hook.sh           # Commit message validation script
├── Services/
│   ├── HookInstallationService.cs   # Main service implementation
│   ├── HookInstallationResult.cs    # Result types
│   └── HookConfiguration.cs         # Configuration models
└── app_config.json                   # Application configuration

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

**Last Updated**: 2026-02-06
**Version**: 1.1.5
**Maintained By**: Robert Stokes

## Vibe Control Rules
- Log all file changes (WARN)
