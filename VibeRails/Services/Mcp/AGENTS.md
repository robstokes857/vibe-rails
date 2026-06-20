# MCP Server (in-process)

VibeRails hosts a Model Context Protocol (MCP) server **inside `vb.exe`** — no separate binary to
build or ship. The same two tools are exposed over **two transports** so each consumer gets its
natural one:

- **HTTP** at `/mcp` (Streamable HTTP) — for the dashboard MCP Explorer.
- **stdio** via `vb mcp` — a spawnable child process for CLIs (`claude`/`codex` `mcp add`).

(A standalone stdio `MCP_Server/` project previously existed and was removed in favour of this.)

## Topology

```
vb.exe — Native AOT, two MCP entry points sharing the same tool classes
│
├── HTTP (dashboard's Kestrel, root backend only)
│     MapRegisterServices: AddMcpServer().WithHttpTransport().WithTools<Rules/SessionSearch>()
│     Program.cs:          app.MapMcp("/mcp")
│     CookieAuthMiddleware in front of /mcp     ← viberails_session token required
│
└── stdio (`vb mcp`, McpStdioHost.cs)
      Program.cs branches BEFORE the web host:  if (McpStdioHost.IsRequested(args)) …
      AddMcpServer().WithStdioServerTransport().WithTools<Rules/SessionSearch>()
      No web server, no port, no auth           ← inherently scoped to the spawning CLI
```

The HTTP DI registration and `MapMcp("/mcp")` are gated to the **root backend only**
(`!IsTerminalTabChildProcess`); terminal-tab children don't stand up a redundant endpoint, and the
two gates must stay in sync (mapping without the services registered throws at startup). The stdio
path registers its own minimal services in `McpStdioHost.ConfigureServices`.

## Transports & auth

- **HTTP** (`ModelContextProtocol.AspNetCore`, `MapMcp`): `/mcp` is not under `/api/`, so
  `CookieAuthMiddleware` requires only the `viberails_session` token (cookie or header), no per-tab
  token. Used by the Explorer.
- **stdio** (`ModelContextProtocol`, `WithStdioServerTransport`): the CLI spawns `vb mcp` and talks
  JSON-RPC over the child's stdin/stdout. No listening socket, so **no auth** — a stdio server is
  inherently scoped to the spawning process. `McpStdioHost` clears the default console logger and
  relies on file-only Serilog so **nothing but MCP frames reaches stdout** (ONNX/diagnostics go to
  stderr, which is fine). The child inherits the CLI's working directory, so `validate_vca` checks
  the agent's project; `search_history` reads the global BERT corpus regardless of cwd.

## Tools

MCP normalizes C# method names to **snake_case**, so the wire names differ from the method names:

| Wire name (snake_case) | Method | Description |
|------------------------|--------|-------------|
| `validate_vca` | `RulesTool.ValidateVca` | Validates staged git files against `- [ENFORCEMENT] …` rules in AGENTS.md files. |
| `search_history` | `SessionSearchTool.SearchHistory` | Semantic + keyword search over the developer's captured agent history. |

> The wire names are what tool callers use. Calling `SearchHistory` (PascalCase) returns "Unknown tool".

### `search_history` — the real search

`SessionSearchTool` is an **instance tool** with constructor injection. The MCP server resolves it
(and its `IUnifiedSearchService` dependency) from the per-request DI scope — hence the
`AddScoped<SessionSearchTool>()` registration alongside `WithTools<SessionSearchTool>()`.

It calls the same [`IUnifiedSearchService`](../BertV2/IUnifiedSearchService.cs) that powers the
"Vibe AI" inspector: BGE-small-en embeddings + sqlite-vec (per-message and per-session semantic)
+ FTS5/LIKE lexical, combined by reciprocal-rank fusion. The tool returns the **fused** group —
the single best-overall ranking — formatted as readable text (agent, kind, timestamp, session id,
preview). There is **no separate vector store for MCP**; it reuses the real corpus the app already
builds from captured sessions.

## MCP Explorer (dashboard)

The `/api/v1/mcp/*` routes ([McpRoutes.cs](../../Routes/McpRoutes.cs)) are a thin Explorer layer
for the dashboard. They connect to the in-process `/mcp` over **loopback HTTP** — exercising the
exact Streamable-HTTP path an external CLI would use — forwarding the caller's `viberails_session`
token so the loopback request clears auth. Kestrel serves the loopback request on a separate
connection, so there is no self-deadlock.

## CLI auto-registration

Managed agent launches run a VibeRails MCP setup step before the agent starts. Setup removes the
managed `viberails-mcp` entry first, then adds it back, so old configs that pointed at the removed
standalone `MCP_Server.exe` are repaired on the next launch. With the stdio transport this needs no
port and no auth token:

```
claude mcp remove viberails-mcp
claude mcp add --scope user viberails-mcp -- "<path-to-vb>" mcp
codex  mcp remove viberails-mcp
codex  mcp add viberails-mcp -- "<path-to-vb>" mcp
agy    mcp remove viberails-mcp
agy    mcp add viberails-mcp -- "<path-to-vb>" mcp
copilot mcp remove viberails-mcp
copilot mcp add viberails-mcp -- "<path-to-vb>" mcp
```

At launch time `CommandService` resolves the server command as either the published executable
(`Environment.ProcessPath`, e.g. `vb.exe mcp`) or `dotnet <path-to-vb.dll> mcp` for
framework-dependent/dev builds. Setup failures are non-blocking: commands are chained with `;`,
so the agent still launches if the server was absent, already present, or the CLI rejects an MCP
management command.

## Tests

- `Tests/Services/Mcp/McpServerHttpTests.cs` — hosts the real `AddMcpServer().WithHttpTransport()
  + MapMcp` wiring on a loopback Kestrel and drives it through `McpClientService` over Streamable
  HTTP; asserts the tool list, tool execution, and that the DI-injected `SessionSearchTool`
  resolves (with a deterministic fake `IUnifiedSearchService`).
- `Tests/Services/Mcp/McpStdioHostTests.cs` — pins the `vb mcp` trigger and that
  `McpStdioHost.ConfigureServices` registers the tools + BERT read-path. End-to-end stdio is
  verified by spawning `vb mcp` and running a handshake (also asserts stdout stays JSON-only).

## AOT notes

The whole app is Native AOT. `WithTools<T>()` (explicit types), `MapMcp`, and
`WithStdioServerTransport()` are trim/AOT-clean — verified with the trim + AOT Roslyn analyzers
(`EnableAotAnalyzer`/`EnableTrimAnalyzer`) reporting zero warnings. Avoid `WithToolsFromAssembly()`
(reflection scan) — it is the AOT-unsafe variant.
