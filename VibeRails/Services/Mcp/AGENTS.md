# MCP Server (in-process)

VibeRails hosts a Model Context Protocol (MCP) server **inside `vb.exe`** — no separate binary to
build or ship. The same tools are exposed over **two transports** so each consumer gets its
natural one:

- **HTTP** at `/mcp` (Streamable HTTP) — for the dashboard MCP Explorer.
- **stdio** via `vb mcp` — a spawnable child process for CLIs (`claude`/`codex` `mcp add`).

(A standalone stdio `MCP_Server/` project previously existed and was removed in favour of this.)

## Topology

```
vb.exe — Native AOT, two MCP entry points sharing the same tool classes
│
├── HTTP (dashboard's Kestrel, root backend only)
│     MapRegisterServices: AddMcpServer().WithHttpTransport().WithTools<Rules/SessionSearch/TokenSaver>()
│     Program.cs:          app.MapMcp("/mcp")
│     CookieAuthMiddleware in front of /mcp     ← viberails_session + viberails_tab tokens required
│
└── stdio (`vb mcp`, McpStdioHost.cs)
      Program.cs branches BEFORE the web host:  if (McpStdioHost.IsRequested(args)) …
      AddMcpServer().WithStdioServerTransport().WithTools<Rules/SessionSearch/TokenSaver>()
      No web server, no port, no auth           ← inherently scoped to the spawning CLI
```

The HTTP DI registration and `MapMcp("/mcp")` are gated to the **root backend only**
(`!IsTerminalTabChildProcess`); terminal-tab children don't stand up a redundant endpoint, and the
two gates must stay in sync (mapping without the services registered throws at startup). The stdio
path registers its own minimal services in `McpStdioHost.ConfigureServices`.

## Transports & auth

- **HTTP** (`ModelContextProtocol.AspNetCore`, `MapMcp`): `/mcp` is not under `/api/`, but
  `CookieAuthMiddleware` gates it at the **same bar as `/api/`** — **both** the
  `viberails_session` token (cookie or header) **and** the `viberails_tab` per-tab token
  (header) are required. MCP tools can open a host shell and send input, so a leaked session
  token alone must never be enough to reach them. Used by the Explorer.
- **stdio** (`ModelContextProtocol`, `WithStdioServerTransport`): the CLI spawns `vb mcp` and talks
  JSON-RPC over the child's stdin/stdout. The MCP transport itself has no listening socket or auth
  challenge because it is scoped to the spawning process. `McpStdioHost` clears the default console
  logger and relies on file-only Serilog so **nothing but MCP frames reaches stdout**
  (ONNX/diagnostics go to stderr, which is fine). The child inherits the CLI's working directory,
  so `validate_vca` checks the agent's project; `search_history` reads the global BERT corpus
  regardless of cwd.

## Tools

MCP normalizes C# method names to **snake_case**, so the wire names differ from the method names:

| Wire name (snake_case) | Method | Description |
|------------------------|--------|-------------|
| `validate_vca` | `RulesTool.ValidateVca` | Validates the staged Git index snapshot against `- [ENFORCEMENT] …` rules from the indexed AGENTS.md files. |
| `search_history` | `SessionSearchTool.SearchHistory` | Semantic + keyword search over the developer's captured agent history. |
| `pause_token_saver` | `TokenSaverTool.PauseTokenSaver` | Turns VibeRails' token compression off for 5 minutes for this terminal tab, so an agent can read elided output verbatim. |
| `resume_token_saver` | `TokenSaverTool.ResumeTokenSaver` | Restores token compression immediately, ending an active pause early. |
| `get_token_saver_status` | `TokenSaverTool.GetTokenSaverStatus` | Reports whether compression is active and whether a pause window is open. |

> The wire names are what tool callers use. Calling `SearchHistory` (PascalCase) returns "Unknown tool".

> **Not currently exposed (security review 2026-07-02):** `run_shell_command` / `get_shell_command_status` /
> `cancel_shell_command` (`HostShellTools`) and `web_search` / `web_fetch` (`WebResearchTools`) are kept in the
> tree but no longer registered or listed via `WithTools<...>()` in either transport. The sections below
> describe the retained implementations.

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

### `pause_token_saver` / `resume_token_saver` / `get_token_saver_status` — per-tab compression control

`TokenSaverTool` is an **instance tool** with constructor injection (`IHttpClientFactory`). It is
registered `AddScoped<TokenSaverTool>()` alongside `WithTools<TokenSaverTool>()` in both transports
(the two transports must expose the same tools).

The interesting part is which process answers. This tool usually runs inside a `vb mcp` child that
the CLI spawned, while the proxy doing the compressing lives in the terminal tab's own child
`vb.exe` — a different process entirely. The link between them is the environment: the CLI was
launched with the proxy's base-URL env var and its two proxy tokens, and a spawned MCP server
inherits that environment, so it can call the exact proxy whose output it is reading. That also
makes the pause per-tab for free: the only proxy this tool can reach is its own tab's.

`CommandService.AddProxyContactDetails` stamps the same three variables on every proxied launch,
regardless of provider:

| Variable | Purpose |
|---|---|
| `VIBERAILS_LLM_PROXY_BASE` | The proxy host to call. |
| `VIBERAILS_LLM_PROXY_SESSION_TOKEN` | Session half of the proxy auth contract. |
| `VIBERAILS_LLM_PROXY_TAB_TOKEN` | Tab half — required; session alone is not enough. |

Stating them uniformly is the point: each CLI learns the proxy differently (Claude via
`ANTHROPIC_BASE_URL`, Codex via a `--config` arg, OpenCode via JSON in `OPENCODE_CONFIG_CONTENT`),
and none of those is readable by a generic child.

When those env vars are absent — the dashboard's MCP Explorer, or a CLI launched with the proxy
off — there is nothing to pause, and the tools say so rather than silently succeeding. Calls are
loopback POSTs with a 10-second timeout; switching compression on or off invalidates the provider's
prompt cache, so the descriptions tell the agent not to call speculatively. See
[`TokenSaver/README.md`](../../../TokenSaver/README.md) § *Pausing* for the pause's lifetime and
its single application point.

### Host shell command tools

> **Not currently exposed** (security review 2026-07-02) — kept in-tree, not registered. Details retained below.

`HostShellTools` is an **instance tool** backed by `IHostShellCommandService`. It is separate from
the human terminal-tab tools: it runs host commands through a bounded reusable shell worker pool
implemented with `Channel<T>`, `ConcurrentDictionary`, `CancellationTokenSource`, and async process
I/O. Workers execute one command at a time and are recycled after cancellation/timeout because shell
state may be dirty. Supported shells are PowerShell 7+ (`pwsh`) on Windows, `bash` on Linux, and
`zsh` on macOS. These tools execute as the current OS user; they are for managed agents that already
run on the host, not a sandbox.

### Web research tools

> **Not currently exposed** (security review 2026-07-02) — kept in-tree, not registered. Details retained below.

`WebResearchTools` is an **instance tool** backed by `IWebResearchService` (registered as a typed
`HttpClient` via `AddHttpClient<IWebResearchService, WebResearchService>`).
It provides no-key web search (DuckDuckGo HTML) and HTTP/HTTPS page fetch/cleaning. Fetches are
limited in size, but they are not network-target filtered: the request runs as the local VibeRails
process and can target localhost or private-network URLs. This tool is intended for trusted local
agents with host-level capabilities.

## MCP Explorer (dashboard)

The `/api/v1/mcp/*` routes ([McpRoutes.cs](../../Routes/McpRoutes.cs)) are a thin Explorer layer
for the dashboard. They default to the in-process `/mcp` over **loopback HTTP** — exercising the
exact Streamable-HTTP path an external CLI would use — and can also inspect/call another
Streamable HTTP MCP endpoint supplied by the user. The `viberails_session` **and**
`viberails_tab` tokens are both forwarded automatically for the local loopback `/mcp`
endpoint so the request clears auth (which requires both); external targets receive only the
headers explicitly supplied in the Explorer. Kestrel serves local
loopback requests on a separate connection, so there is no self-deadlock.

## CLI auto-registration

Managed agent launches always run a VibeRails MCP setup step before the agent starts. CLIs with a
remove command delete the managed `viberails-mcp` entry first, then add it back; OpenCode replaces
the named entry with one add command. Either form repairs old configs that pointed at the removed
standalone `MCP_Server.exe`. With the stdio transport this needs no port and no auth token:

```
claude mcp remove viberails-mcp          # output suppressed by CommandService
claude mcp add --scope user viberails-mcp -- "<path-to-vb>" mcp
codex  mcp remove viberails-mcp          # output suppressed by CommandService
codex  mcp add viberails-mcp -- "<path-to-vb>" mcp
agy    mcp remove viberails-mcp          # output suppressed by CommandService
agy    mcp add viberails-mcp -- "<path-to-vb>" mcp
copilot mcp remove viberails-mcp         # output suppressed by CommandService
copilot mcp add viberails-mcp -- "<path-to-vb>" mcp
opencode mcp add viberails-mcp -- "<path-to-vb>" mcp
```

OpenCode 1.18.8 supports the non-interactive local-command form shown above. It has no matching
`mcp remove` command, but adding the same name replaces that entry, so OpenCode and the
OpenCode-backed pseudo-CLIs (GLM 5.2 / GLM 5.3 / Grok 4.6)
launches run one add command immediately before launch. On Windows, `CommandService` invokes
the npm `opencode.cmd` shim because PowerShell consumes the `--` separator when routing through
`opencode.ps1`; Unix launches use `opencode`.

At launch time `CommandService` resolves the server command as either the published executable
(`Environment.ProcessPath`, e.g. `vb.exe mcp`) or `dotnet <path-to-vb.dll> mcp` for
framework-dependent/dev builds. Setup failures are non-blocking: commands are chained with `;`,
so the agent still launches if the server was absent, already present, or the CLI rejects an MCP
management command. Registration diagnostics are written to the normal VibeRails file log
(`~/.vibe_rails/logs/vb-*.log`) with the `[MCP]` prefix; CLI-specific command errors still appear
in the launching terminal. The remove step is quiet because "not registered" is a harmless repair
case; the add step is intentionally not quiet.

## Tests

- `Tests/Services/Mcp/McpServerHttpTests.cs` — hosts the real `AddMcpServer().WithHttpTransport()
  + MapMcp` wiring on a loopback Kestrel and drives it through `McpClientService` over Streamable
  HTTP; asserts the exact five-tool list (`validate_vca`, `search_history`, `pause_token_saver`,
  `resume_token_saver`, `get_token_saver_status`), tool execution, and that the DI-injected
  `SessionSearchTool` resolves (with a deterministic fake `IUnifiedSearchService`).
- `Tests/Services/Mcp/McpStdioHostTests.cs` — pins the `vb mcp` trigger and that
  `McpStdioHost.ConfigureServices` registers the tools (`SessionSearchTool`,
  `TokenSaverTool`) and the BERT read-path. These are service-descriptor assertions only;
  there is no end-to-end stdio handshake test in this file.

## AOT notes

The whole app is Native AOT (`<PublishAot>true</PublishAot>` in `VibeRails.csproj`, which
implicitly enables the AOT/trim Roslyn analyzers). `WithTools<T>()` (explicit types), `MapMcp`,
and `WithStdioServerTransport()` are themselves trim/AOT-clean. A small, known set of analyzer
warnings (`IL2026`/`IL2057`/`IL2070`/`IL2075`/`IL2080`/`IL2104`/`IL3050`) for reflection-based
paths is suppressed via `<NoWarn>` rather than refactored away; don't add new reflection, and
avoid `WithToolsFromAssembly()` (reflection scan) — it is the AOT-unsafe variant.

---

**Last checked**: 2026-08-06T16:53:19Z by opencode (glm-5.2)
