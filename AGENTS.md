# AGENTS.md - VibeControl Project Documentation

## Project Overview

**VibeControl** is a sophisticated desktop/web application for managing and enforcing coding standards across AI-powered development workflows. It serves as a unified control panel for multiple LLM CLIs (Claude, Codex, Gemini) with comprehensive rule enforcement, session logging, and MCP integration.

**Live Site**: [https://viberails.ai/](https://viberails.ai/)

### Core Capabilities
- **Agent File Management** - Create and manage `agent.md` files with customizable coding rules
- **Rule Enforcement** - Define standards with three enforcement levels (WARN/COMMIT/STOP)
- **Multi-LLM Support** - Unified interface for Claude, Codex, and Gemini CLIs
- **Environment Management** - Configure separate environments for different LLM providers
- **Session Logging** - Track and monitor all CLI session history and outputs
- **MCP Integration** - Custom Model Context Protocol server with specialized tools

## Technology Stack

### Backend
- **.NET 10.0** - Modern .NET with AOT compilation support
- **ASP.NET Core Slim** - Lightweight web server
- **SQLite** - Local database with WAL mode for concurrency
- **ModelContextProtocol NuGet Package** (v0.5.0-preview.1) - MCP foundation with custom service layer and tools
- **Pty.Net** - Cross-platform pseudo-terminal support (git submodule)
- **ToonSharp** - Utility library (git submodule)

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

```
VibeControl/
├── VibeControl/                    # Main ASP.NET Core application
│   ├── Program.cs                  # Entry point (web server + CLI loop)
│   ├── Init.cs                     # Dependency injection setup
│   ├── LMBootstrap.cs              # Terminal-based LLM CLI wrapper mode
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
│       └── assets/                 # Images, fonts, icons
│
├── MCP_Server/                     # Standalone custom MCP server
│   ├── Program.cs                  # MCP server entry point (Stdio transport)
│   ├── Tools/                      # Custom MCP tools
│   │   ├── EchoTool.cs            # Echo/test tool
│   │   ├── RulesTool.cs           # Content validation rules
│   │   └── VectorSearchTool.cs     # Vector search capability (SharpVector)
│   └── Models/                     # MCP message models
│
├── PtyNet/                         # Git submodule: PTY library
│   └── src/Pty.Net/               # Cross-platform terminal emulation
│
├── ToonLib/                        # Git submodule: ToonSharp library
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

VibeControl operates in two primary modes:

#### 1. Web Server Mode (Default)
```bash
vibecontrol
```
- Launches ASP.NET web server on available port
- Opens browser to dashboard UI
- Provides REST API for managing agents, environments, sessions
- Runs background CLI loop (currently minimal)

#### 2. LMBootstrap Mode (CLI Wrapper)
```bash
vibecontrol --lmbootstrap claude --env myenv
```
- Wraps LLM CLI execution (claude/codex/gemini)
- Captures terminal output for session logging
- Applies environment-specific configurations
- Stores session data in SQLite database

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
vibecontrol --lmbootstrap claude --env myenv
  ↓
LMBootstrap.RunAsync()
  ↓ Resolves git repository root
  ↓ Gets/creates project record
  ↓ Loads environment configuration
  ↓ Creates EzTerminal wrapper (PtyNet)
  ↓ Sets environment variables
  ↓ Executes: claude [args]
  ↓
Terminal output → TerminalOutputFilter → DbService.LogSessionOutputAsync()
  ↓
SQLite: Sessions & SessionLogs tables
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

#### MCP Architecture
```
VibeControl (Main App)
  ↓
McpClientService (Custom service layer)
  ↓ Uses ModelContextProtocol NuGet package
  ↓ Stdio transport
  ↓
MCP_Server.exe (Separate process)
  ↓ Uses ModelContextProtocol NuGet package
  ↓ Custom tools:
  ├─→ EchoTool (test/debug)
  ├─→ RulesTool (content validation)
  └─→ VectorSearchTool (SharpVector semantic search)
```

## Key Components

### Services Layer

#### AgentFileService ([Services/AgentFileService.cs](VibeControl/Services/AgentFileService.cs))
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

#### RulesService ([Services/RulesService.cs](VibeControl/Services/RulesService.cs))
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

#### DbService ([Services/DbService.cs](VibeControl/Services/DbService.cs))
**Purpose**: High-level database operations wrapper

**Key Methods**:
- `GetOrCreateProjectAsync(repoPath)` - Get/create project record
- `GetEnvironmentAsync(name)` - Retrieve environment configuration
- `CreateSessionAsync()` - Start new CLI session
- `LogSessionOutputAsync()` - Append terminal output to session log
- `GetRecentProjectsAsync()` - Get recently used projects
- `GetRecentSessionsAsync()` - Get recent CLI sessions

#### GitService ([Services/GitService.cs](VibeControl/Services/GitService.cs))
**Purpose**: Git repository operations

**Key Methods**:
- `IsGitRepositoryAsync(path)` - Check if directory is git repo
- `GetGitRootAsync(path)` - Find repository root directory
- `GetCurrentBranchAsync()` - Get active git branch
- `GetRecentCommitsAsync()` - Retrieve commit history

#### McpClientService ([Services/Mcp/McpClientService.cs](VibeControl/Services/Mcp/McpClientService.cs))
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

##### VectorSearchTool ([MCP_Server/Tools/VectorSearchTool.cs](MCP_Server/Tools/VectorSearchTool.cs))
- **Purpose**: Semantic search using SharpVector library
- **Input**: Search query, optional context
- **Output**: Semantically similar content
- **Use Case**: Code similarity search, documentation lookup
- **Dependencies**: Build5Nines.SharpVector (v1.0.0)

### Data Layer

#### Repository ([DB/Repository.cs](VibeControl/DB/Repository.cs))
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

#### Routes ([Routes.cs](VibeControl/Routes.cs))
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

#### app.js ([wwwroot/app.js](VibeControl/wwwroot/app.js))
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

#### index.html ([wwwroot/index.html](VibeControl/wwwroot/index.html))
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
All services registered in [Init.cs](VibeControl/Init.cs) with appropriate lifetimes:
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

### Environment Variables (LMBootstrap Mode)

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

3. **Update LLM enum** in [DTOs/LLM.cs](VibeControl/DTOs/LLM.cs)

4. **Add launcher logic** in [Services/LlmClis/LaunchLLMService.cs](VibeControl/Services/LlmClis/LaunchLLMService.cs)

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
vibecontrol --lmbootstrap claude --env production
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

Enable verbose logging in [Program.cs](VibeControl/Program.cs):
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
- LMBootstrap mode uses pseudo-terminal (PTY) for safe terminal emulation
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
- Vector search uses efficient SharpVector implementation

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
- [Pty.Net GitHub](https://github.com/link-to-ptynet)
- [XTerm.js Documentation](https://xtermjs.org/)
- [SharpVector Documentation](https://github.com/Build5Nines/SharpVector)

### Related Projects
- **Claude CLI** - Anthropic's Claude command-line interface
- **Codex CLI** - OpenAI Codex command-line tool
- **Gemini CLI** - Google Gemini command-line interface
- **MCP SDK** - Model Context Protocol development kit

### Key Dependencies
- **Microsoft.Data.Sqlite** (v10.0.2) - SQLite database access
- **ModelContextProtocol** (v0.5.0-preview.1) - MCP foundation
- **Build5Nines.SharpVector** (v1.0.0) - Vector embeddings for semantic search
- **Pty.Net** (git submodule) - Pseudo-terminal support

---

**Last Updated**: 2026-01-23
**Version**: 1.0
**Maintained By**: VibeControl Development Team

## Vibe Control Rules
- Log all file changes (WARN)
