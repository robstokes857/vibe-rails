# Vibe-Rails

**Vibe-Rails** is an opinionated framework that helps keep AI coding assistants from going off the rails.

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![TypeScript](https://img.shields.io/badge/TypeScript-5.3-3178C6)](https://www.typescriptlang.org/)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

---

## Overview
- **Environment Isolation** - Like Conda for LLMs. Create separate environments to experiment with Claude, Codex, or Gemini settings without breaking your primary setup
- **Cross-LLM Learning** - Share context and learnings between different LLM providers (Claude, Codex, Gemini)
- **RAG (Without The Rot) For Your Code** - Track things like repeated fixes the LLM forgets, including when you have to tell it the same thing 6 or 7 times in one session and it still doesn't understand, how you describe a feature and where that code lives, and file change summaries with commits, then only provide what’s useful at call time to prevent context rot.
- **Few Shot Prompting** - Get Gemini or codex to code like Claude for code that has been done before with few shot prompting... Making them up to 20% better (research paper and eval data coming soon.)
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
- [Node.js 20+](https://nodejs.org/) (for VS Code extension development)
- Git
- One or more LLM CLIs: Claude, Codex, or Gemini

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
   ## Rules
   - COMMIT: max_complexity=10
   - STOP: min_coverage=80
   - WARN: log_files_changed
   ```
3. **Launch a CLI** with environment isolation:
   ```bash
   vb --lmbootstrap claude --env development
   ```

---

## Architecture

Vibe-Rails consists of four main components:

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
└────────────────────┬────────────────────────────────────┘
                     │ Stdio Transport
┌────────────────────▼────────────────────────────────────┐
│                   MCP Server                             │
│           (ModelContextProtocol + Tools)                 │
│    Echo | Rules Validation | Vector Search              │
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
- **Font Awesome 6** for icons
- **XTerm.js** for terminal emulation

**Extension**
- **TypeScript 5.3**
- **VS Code API 1.85+**

---

## Components

### 1. VibeRails Backend

The core ASP.NET application providing:

- **Web Server** - Auto-detects available port, serves REST API and static files
- **Service Layer** - Business logic for agents, rules, sessions, and MCP
- **Data Layer** - SQLite repository with WAL mode
- **LLM CLI Support** - Environment-specific configurations for Claude, Codex, Gemini

**Key Services:**

| Service | Purpose |
|---------|---------|
| `AgentFileService` | Find and manage agent.md files |
| `RulesService` | Define and validate 12 built-in coding rules |
| `DbService` | High-level database operations |
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
├── app.js              # Main application (519 lines)
├── js/modules/
│   ├── agent-controller.js      (46KB)
│   ├── environment-controller.js (26KB)
│   ├── rule-controller.js
│   ├── dashboard-controller.js   (10KB)
│   └── utils.js
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
- Health checks via `/api/v1/IsLocal`
- Graceful shutdown (stdin newline → SIGTERM → SIGKILL)
- Asset path rewriting for webview sandbox

### 4. MCP Server

Standalone process with custom tools:

| Tool | Purpose |
|------|---------|
| **EchoTool** | Test/debug MCP communication |
| **RulesTool** | Validate content against coding rules |
| **VectorSearchTool** | Semantic search using SharpVector |

**Architecture:**
- Stdio transport (no network overhead)
- Async tool execution
- Process kept alive between calls

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

### Building MCP Server

```bash
cd MCP_Server
dotnet publish -c Release

# Output: bin/Release/net10.0/publish/MCP_Server.exe
```

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

#### 2. LMBootstrap Mode (CLI Wrapper)

```bash
vb --lmbootstrap claude --env production
```

- Wraps LLM CLI execution with environment isolation
- Captures terminal output for session logging
- Applies environment-specific configurations
- Stores session data in SQLite

#### 3. VS Code Extension Mode

1. Click **VibeRails icon** in editor toolbar
2. Extension spawns backend process
3. Dashboard loads in webview panel

### Managing Agent Files

#### Create Agent File

```bash
# Via CLI (in repository root)
echo "## Rules\n- WARN: log_files_changed" > agent.md

# Via Web UI
# Navigate to Agents → Create New Agent
```

#### Add Rules

12 built-in rules available:

| Rule | Description | Example Value |
|------|-------------|---------------|
| `max_complexity` | Maximum cyclomatic complexity | `10` |
| `min_coverage` | Minimum test coverage | `80` |
| `log_files_changed` | Log all file modifications | (no value) |
| `no_console_logs` | Prevent console.log in production | (no value) |
| `max_file_length` | Maximum lines per file | `500` |
| `require_tests` | Tests required for new code | (no value) |
| `no_todo_comments` | Prevent TODO comments | (no value) |
| `enforce_naming_conventions` | Enforce naming patterns | `PascalCase` |
| `max_method_length` | Maximum lines per method | `50` |
| `require_documentation` | Documentation required | (no value) |
| `no_magic_numbers` | Prevent hardcoded numbers | (no value) |
| `enforce_error_handling` | Require error handling | (no value) |

**Enforcement Levels:**
- `WARN` - Log warning, allow continuation
- `COMMIT` - Block commits that violate rule
- `STOP` - Immediately halt execution on violation

#### Example agent.md

```markdown
# Development Agent

## Rules
- COMMIT: max_complexity=10
- COMMIT: min_coverage=80
- STOP: no_console_logs
- WARN: log_files_changed
- COMMIT: require_tests
- WARN: max_file_length=500
```

### Environment Management

Create isolated environments for different contexts:

```bash
# Development environment
vb --lmbootstrap claude --env development

# Production environment (stricter rules)
vb --lmbootstrap claude --env production

# Team-specific environment
vb --lmbootstrap codex --env team-alpha
```

**Environment Configuration:**
```
~/.vibe_control/envs/
├── development/
│   ├── claude/
│   │   └── config.json
│   ├── codex/
│   │   └── config.json
│   └── gemini/
│       └── config.json
└── production/
    └── ...
```

### Session Logging

All CLI sessions are logged with full terminal output:

```bash
# Launch with logging
vb --lmbootstrap claude --env dev

# View logs in Web UI
# Navigate to Sessions → Select session → View terminal output
```

**Database Storage:**
- Global: `~/.vibe_control/vibecontrol.db`
- Project-specific: `<repo>/.vibe_control/vibecontrol.db`

### MCP Integration

Call MCP tools via REST API or programmatically:

```bash
# Via REST API
curl -X POST http://localhost:5000/api/v1/mcp/tools/vector_search \
  -H "Content-Type: application/json" \
  -d '{"query": "find authentication code"}'
```

```csharp
// Via C# SDK
var service = await McpClientService.ConnectAsync(
    transport: new StdioClientTransport("MCP_Server.exe"),
    clientName: "vibecontrol-client",
    version: "1.0.0"
);

var result = await service.CallToolAsync("vector_search", new Dictionary<string, object> {
    ["query"] = "find authentication code"
});
```

---

## Configuration

### Application Settings

**Global Configuration:**
```
~/.vibe_control/
├── vibecontrol.db          # SQLite database
├── config.json             # Application settings
└── envs/                   # Environment configurations
```

**config.json Example:**
```json
{
  "defaultEnvironment": "development",
  "enableSessionLogging": true,
  "mcpServerPath": "MCP_Server.exe",
  "browserAutoLaunch": true
}
```

### Environment Variables

#### Claude
```bash
CLAUDE_CONFIG_DIR=~/.vibe_control/envs/myenv/claude
```

#### Codex
```bash
CODEX_HOME=~/.vibe_control/envs/myenv/codex
```

#### Gemini
```bash
XDG_CONFIG_HOME=~/.vibe_control/envs/myenv/gemini/config
XDG_DATA_HOME=~/.vibe_control/envs/myenv/gemini/data
XDG_STATE_HOME=~/.vibe_control/envs/myenv/gemini/state
XDG_CACHE_HOME=~/.vibe_control/envs/myenv/gemini/cache
```

### VS Code Extension Settings

```json
{
  "viberails.executablePath": "/path/to/vb.exe"
}
```

If empty, extension searches:
1. Workspace `VibeRails/bin/Debug/net10.0/vb.exe`
2. Development paths
3. System PATH

---

## Development

### Project Structure

```
VibeControl2/
├── VibeRails/                  # ASP.NET Core backend
│   ├── Services/               # Business logic
│   ├── DB/                     # Data access layer
│   ├── DTOs/                   # Data transfer objects
│   ├── wwwroot/                # Static web assets
│   └── VibeRails.csproj
│
├── vscode-viberails/           # VS Code extension
│   ├── src/
│   │   ├── extension.ts
│   │   ├── backend-manager.ts
│   │   └── webview-panel.ts
│   └── package.json
│
├── MCP_Server/                 # MCP server
│   ├── Tools/
│   └── Program.cs
│
├── PtyNet/                     # Git submodule
├── Tests/                      # xUnit tests
└── Scripts/                    # PowerShell scripts
```

### Running Tests

```bash
# Unit tests
cd Tests
dotnet test

# With coverage
dotnet test /p:CollectCoverage=true

# Integration tests
cd UITests
dotnet test
```

### Adding a New Rule

1. **Define in [RulesService.cs](VibeRails/Services/RulesService.cs)**:
```csharp
new Rule(
    "my_new_rule",
    "Description of the rule",
    "parameter_name",
    "WARN"
)
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

2. **Register in [Init.cs](VibeRails/Init.cs)**:
```csharp
builder.Services.AddSingleton<IMyLlmCliEnvironment, MyLlmCliEnvironment>();
```

3. **Update [LLM.cs](VibeRails/DTOs/LLM.cs)** enum

4. **Add launcher logic** in [LaunchLLMService.cs](VibeRails/Services/LlmClis/LaunchLLMService.cs)

### Adding a New MCP Tool

1. **Create tool class** in [MCP_Server/Tools/](MCP_Server/Tools/):
```csharp
public class MyCustomTool : IMcpTool
{
    public string Name => "my_tool";
    public string Description => "Tool description";

    public async Task<McpToolResult> ExecuteAsync(
        Dictionary<string, object> arguments,
        CancellationToken cancellationToken)
    {
        // Implementation
        return new McpToolResult { Success = true };
    }
}
```

2. **Register in [MCP_Server/Program.cs](MCP_Server/Program.cs)**:
```csharp
.WithTools<MyCustomTool>()
```

3. **Rebuild MCP_Server**:
```bash
cd MCP_Server
dotnet build
```

### Build Scripts

```powershell
# Development build (Windows)
.\Scripts\build.ps1

# Ubuntu/Linux build
.\Scripts\debug_ubuntu.ps1

# Run tests on Ubuntu
.\Scripts\test_ubuntu.ps1

# Interactive shell
.\Scripts\interactive_ubuntu.ps1
```

---

## API Reference

### Base URL

```
http://localhost:<auto-detected-port>/api/v1/
```

### Endpoints

#### Agent Management

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/agents` | List all agent files |
| GET | `/agents/rules?path={path}` | Get rules from agent file |
| POST | `/agents` | Create agent file |
| POST | `/agents/rules` | Add rule to agent |
| PUT | `/agents/rules/enforcement` | Update rule enforcement level |
| DELETE | `/agents/rules` | Delete rules |
| DELETE | `/agents?path={path}` | Delete agent file |
| GET | `/rules` | List available rule definitions |

#### Environment & CLI

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/projects/recent` | Get recent projects |
| GET | `/environments/{name}/launch` | Get environment variables |
| POST | `/cli/launch/{cli}` | Launch CLI in terminal |
| POST | `/cli/launch/vscode` | Launch VS Code |

#### Session Logging

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/sessions/{id}/logs` | Get session terminal logs |
| GET | `/sessions/recent` | Get recent CLI sessions |

#### MCP Integration

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/mcp/status` | MCP server connection status |
| GET | `/mcp/tools` | List available MCP tools |
| POST | `/mcp/tools/{name}` | Execute MCP tool |

#### Utility

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/IsLocal` | Check if in git repository |

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
- **Solution**: Check `~/.vibe_control/` directory permissions, verify SQLite access

**Issue: MCP server not connecting**
- **Cause**: MCP_Server.exe not found or stdio transport corruption
- **Solution**: Ensure MCP_Server.exe is built and in correct location

**Issue: VS Code extension not finding backend**
- **Cause**: Incorrect `viberails.executablePath` setting
- **Solution**: Set absolute path in VS Code settings or add to PATH

### Debug Logging

Enable verbose logging in [Program.cs](VibeRails/Program.cs:1):
```csharp
builder.Logging.SetMinimumLevel(LogLevel.Debug);
```

**Note**: For MCP_Server, logging is disabled by default to prevent stdio corruption.

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
- [Google Gemini](https://ai.google.dev/) - Google Gemini command-line interface

### Key Dependencies

- **Microsoft.Data.Sqlite** (v10.0.2) - SQLite database access
- **ModelContextProtocol** (v0.5.0-preview.1) - MCP foundation
- **Build5Nines.SharpVector** (v1.0.0) - Vector embeddings
- **Pty.Net** (git submodule) - Pseudo-terminal support

---

## Acknowledgments

Built with modern .NET 10.0, Native AOT compilation, and the Model Context Protocol for next-generation AI development workflows.

**Last Updated**: 2026-01-30
**Version**: 2.0
**Maintained By**: VibeControl Development Team
