# MCP Server (vibecontrol-mcp-server)

## Overview

Standalone Model Context Protocol (MCP) server for the VibeRails ecosystem. Provides AI-accessible tools for VCA rule validation, vector-based code search, and conversation history retrieval.

## Transport

Uses **stdio** (stdin/stdout) for MCP communication. Spawned as a child process by `McpClientService` in the main VibeRails application. Because the MCP protocol runs over stdio, console logging is disabled — all logging goes to `~/.vibe_rails/mcp-server.log` via Serilog.

## Build

- **.NET 10.0** console application
- **Native AOT** (`PublishAot=true`) for fast startup and small binary
- **Invariant globalization** enabled (no ICU dependency)
- **Serilog** file-based logging (Warning+ only, no console output)

## Tools

| Tool | Method | Description |
|------|--------|-------------|
| Echo | `Echo(message)` | Test connectivity — echoes message back |
| CheckRules | `CheckRules(content)` | Validates content against safety and style rules (secrets, length, TODOs) |
| ValidateVca | `ValidateVca(workingDirectory?)` | Validates staged git files against VCA rules in AGENTS.md files |
| SearchUserTerms | `SearchUserTerms(query, maxResults?)` | Search code files by informal user terminology |
| AddUserTermMapping | `AddUserTermMapping(userTerm, targetPath, description?)` | Map user terminology to actual file paths |
| SearchConversations | `SearchConversations(query, maxResults?)` | Search past conversation history for context |
| AddConversationEntry | `AddConversationEntry(role, content, projectPath?)` | Store conversation entries for future retrieval |
| GetVectorStats | `GetVectorStats()` | Returns vector index statistics |

## Data Storage

- **Vector index**: `~/.vibe_rails/vector/` (JSONL files for user terms and conversation history)
- **Logs**: `~/.vibe_rails/mcp-server.log` (daily rolling, 7-day retention, 10MB limit)

## Building

```bash
cd MCP_Server
dotnet build                     # Debug build
dotnet publish -c Release        # Release AOT build
```
