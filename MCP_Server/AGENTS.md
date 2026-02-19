# MCP Server

Standalone Model Context Protocol server providing AI-accessible tools for the VibeRails ecosystem.

## Architecture

### Core Design: Stdio MCP Transport with Tool Dispatch

```
MCP Client (VibeRails McpClientService)
  │ stdin/stdout (JSON-RPC)
  ▼
MCP_Server.exe (Native AOT)
  ├── Host.CreateApplicationBuilder()
  ├── AddMcpServer() + WithStdioServerTransport()
  ├── Serilog → ~/.vibe_rails/log/mcp/mcp-server.log (Warning+ only)
  └── Tool Registry
      ├── EchoTool          (connectivity test)
      ├── RulesTool          (VCA validation + content rules)
      └── VectorSearchTool   (user terms + conversation history)
          ├── SimpleVectorDb  (feature-hashing vector index)
          └── JSONL storage   (~/.vibe_rails/vector/)
```

The server communicates exclusively via stdio (stdin/stdout JSON-RPC). All diagnostic output goes to a Serilog file sink to avoid corrupting the MCP transport channel.

### Core Components

**`Program.cs`** - Entry point and host configuration
- Configures Serilog file logger (Warning+ to `~/.vibe_rails/log/mcp/mcp-server.log`)
- Clears default logging providers to protect stdio channel
- Registers MCP server with stdio transport via `ModelContextProtocol` NuGet package
- Registers all tool classes with `WithTools<T>()`

**`Tools/EchoTool.cs`** - Connectivity test tool
- Single static method: `Echo(message)` returns `"Echo: {message}"`
- Used to verify MCP communication is working

**`Tools/RulesTool.cs`** - VCA rule validation (most complex tool)
- `CheckRules(content)` — Static content validation:
  - Detects hardcoded secrets (API keys, passwords, tokens)
  - Enforces max length (2000 chars)
  - Blocks unresolved TODO comments
- `ValidateVca(workingDirectory?)` — Staged file validation against AGENTS.md rules:
  - Finds git root by walking parent directories
  - Gets staged files via `git diff --cached --name-only`
  - Discovers AGENTS.md files (common paths + recursive search up to 4 levels)
  - Parses `- [ENFORCEMENT] Rule text` patterns from `## Vibe Control Rules` sections
  - Enforcement levels: WARN, COMMIT, STOP, SKIP, DISABLED
  - Rule checks: file documentation, cyclomatic complexity, test coverage, large file changes
  - Returns PASS/FAIL with acknowledgment format for COMMIT-level violations
- Uses `RegexOptions.Compiled` patterns (AOT-safe)

**`Tools/VectorSearchTool.cs`** - Vector-based search and storage
- Lazy-initialized static state with double-check locking
- Persists data as JSONL to `~/.vibe_rails/vector/`
- Fire-and-forget disk writes with retry logic (3 attempts, exponential backoff)
- Tools:
  - `SearchUserTerms(query)` — Find code files by informal terminology
  - `AddUserTermMapping(userTerm, targetPath)` — Map user terms to file paths
  - `SearchConversations(query)` — Search past conversation context
  - `AddConversationEntry(role, content)` — Store conversation entries
  - `GetVectorStats()` — Index size statistics

**`Services/SimpleVectorDb.cs`** - AOT-compatible in-memory vector database
- Feature hashing vectorizer (FNV-1a hash, word tokens + char n-grams)
- L2-normalized embeddings in configurable dimensions (default: 2048)
- Cosine similarity search via dot product on normalized vectors
- Thread-safe with lock-based synchronization
- No external ML dependencies — pure math implementation

**`Models/VectorModels.cs`** - Data models with AOT-safe serialization
- `UserTermEntry` — User terminology to file path mapping
- `ConversationHistoryEntry` — Stored conversation entries with role, content, timestamp
- `VectorJsonContext` — `JsonSerializerContext` source generator for AOT-compatible JSON

### Logging Architecture

```
Tool catch blocks
  │ Log.Warning(...) / Log.Error(...)
  ▼
Serilog Static Logger (Log.Logger)
  │ MinimumLevel: Warning
  ▼
Serilog.Sinks.File
  └── ~/.vibe_rails/log/mcp/mcp-server.log
      ├── Rolling: Daily
      ├── Retention: 7 days
      └── Size limit: 10 MB per file
```

Logging is file-only by design. Console/debug providers are cleared to prevent any stdout/stderr writes that would corrupt the stdio MCP transport. Tools use Serilog's static `Log` API since all tool methods are static.

### AOT Constraints

| Constraint | How It's Handled |
|------------|-----------------|
| No reflection | JSON uses `JsonSerializerContext` source generators; Serilog configured in code (not from config files) |
| No dynamic assembly loading | All tools registered at compile time via `WithTools<T>()` |
| Invariant globalization | No culture-specific formatting in logs or tools |
| Trimming | Serilog 4.3.0 includes trimming annotations; log messages use scalar placeholders only (no `{@Object}` destructuring) |

### Data Flow: VCA Validation

```
LLM CLI calls ValidateVca tool
  ↓
Find git root (walk parent directories for .git/)
  ↓
git diff --cached --name-only (get staged files)
  ↓
Search for AGENTS.md files (common paths + recursive up to 4 levels)
  ↓
Parse rules: - [WARN|COMMIT|STOP|SKIP|DISABLED] Rule text
  ↓
For each active rule:
  ├── "log all file changes" → Check files documented in ## Files section
  ├── "file changes > N lines" → Check large files documented
  ├── "cyclomatic complexity < N" → Estimate via keyword counting
  └── "test coverage minimum N%" → Check test files staged alongside code
  ↓
Build result: PASS / WARNINGS / COMMIT violations (with acknowledgment format) / STOP violations
```

### Data Flow: Vector Search

```
User calls SearchUserTerms("the repo class")
  ↓
EnsureInitialized() → lazy load from JSONL files (once)
  ↓
SimpleVectorDb.Search(query, pageCount)
  ├── Vectorize query (feature hashing: word tokens + char n-grams → FNV-1a → L2 normalize)
  ├── Dot product against all stored vectors (cosine similarity on normalized vectors)
  └── Return top-K results sorted by similarity
  ↓
Look up UserTermEntry by ID → return file path mapping
```

## Files

| File | Purpose | Key Details |
|------|---------|-------------|
| `Program.cs` | Entry point, host config | Serilog setup, MCP server registration, stdio transport |
| `MCP_Server.csproj` | Project configuration | net10.0, PublishAot, InvariantGlobalization, Serilog packages |
| `Tools/EchoTool.cs` | Echo test tool | Static method, `[McpServerTool]` attribute |
| `Tools/RulesTool.cs` | Content + VCA validation | Compiled regex, git process spawning, AGENTS.md parsing |
| `Tools/VectorSearchTool.cs` | Vector search + storage | Lazy-init, JSONL persistence, fire-and-forget writes |
| `Services/SimpleVectorDb.cs` | In-memory vector index | Feature hashing, FNV-1a, cosine similarity, thread-safe |
| `Models/VectorModels.cs` | Data models + JSON context | `JsonSerializerContext` for AOT-safe serialization |
| `about.md` | Project description | Human-readable overview for developers |
