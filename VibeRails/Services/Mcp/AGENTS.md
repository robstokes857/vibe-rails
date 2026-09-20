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
│     MapRegisterServices: explicit WithTools<...>() registrations
│     Program.cs:          app.MapMcp("/mcp")
│     CookieAuthMiddleware in front of /mcp     ← viberails_session + viberails_tab tokens required
│
└── stdio (`vb mcp`, McpStdioHost.cs)
      Program.cs branches BEFORE the web host:  if (McpStdioHost.IsRequested(args)) …
      AddMcpServer().WithStdioServerTransport() + the same explicit tools
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
  (header) are required. A leaked session token alone must never be enough to reach
  these capabilities. Used by the Explorer.
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
| `validate_vca` | `RulesTool.ValidateVca` | Validates the staged Git index snapshot against `- [ENFORCEMENT] …` rules from the indexed vc.rules.md files. |
| `search_history` | `SessionSearchTool.SearchHistory` | Semantic + keyword search over the developer's captured agent history. |
| `pause_token_saver` | `TokenSaverTool.PauseTokenSaver` | Turns VibeRails' token compression off for 5 minutes for this terminal tab, so an agent can read elided output verbatim. |
| `resume_token_saver` | `TokenSaverTool.ResumeTokenSaver` | Restores token compression immediately, ending an active pause early. |
| `get_token_saver_status` | `TokenSaverTool.GetTokenSaverStatus` | Reports whether compression is active and whether a pause window is open. |
| `list_boards` | `BoardTool.ListBoards` | The project's boards (a project can hold several: sprints, sub-projects) with ids, lanes and card counts, and which one is current for this terminal. |
| `list_board_columns` | `BoardTool.ListBoardColumns` | Lanes of one board with card counts. `board` (name or id) optional: defaults to the board of the card this terminal was launched for, else the first board. |
| `list_board_cards` | `BoardTool.ListBoardCards` | Cards on one board (key, lane, type, priority, title, assignee, comment count, session open); optional lane/assignee/type filters and the same optional `board`. Card keys are project-unique, so `get_board_card VB-n` never needs a board. |
| `get_board_card` | `BoardTool.GetBoardCard` | One card in full: fields, the board's lane names, description, comments, linked commits, sessions (full session id, ended time/exit code, that session's last comment, its chat summary when one exists), the tail of the agent notes, attachment ids/names/types/sizes. `card` omitted = the card this terminal was launched for. `since` (ISO-8601) lists only activity at or after that time and counts the rest. Reading never links the session to the card. |
| `read_board_attachment` | `BoardTool.ReadBoardAttachment` | Bounded UTF-8 Markdown/TXT attachment content; accepts attachment id, optional card, character offset and maximum length (40,000 default; 250,000 limit). Card/project scoped, current files only. |
| `create_board_card` | `BoardTool.CreateBoardCard` | New card (title, description, lane, type, priority, tags); optional `board` as above. |
| `update_board_card` | `BoardTool.UpdateBoardCard` | Partial update, including `flagged=true` for human attention or `false` to clear it (independent of `blocked`). Add a comment explaining what needs review. Description replacement accepts the last write; `descriptionAppend` appends to current text atomically with the other fields and cannot be combined with `description`. |
| `move_board_card` | `BoardTool.MoveBoardCard` | Move a card to a lane (by name or id), optionally at a position. |
| `add_board_comment` | `BoardTool.AddBoardComment` | Append a comment, attributed to the launching session (or "Agent"). Returns the comment id. |
| `append_board_note` | `BoardTool.AppendBoardNote` | Append an entry to the card's **agent notes** — the scratchpad for checkpointing findings and working state as the agent goes. Same limits and attribution as a comment; never part of the comment stream or count. Returns the note id. |
| `get_board_notes` | `BoardTool.GetBoardNotes` | All notes on a card, oldest first (`get_board_card` shows only the most recent ~3,000 characters); optional `since`. |
| `add_board_attachment` | `BoardTool.AddBoardAttachment` | Attach an agent-written `*.md` / `*.txt` file (UTF-8 text, ≤ 500,000 characters). |
| `link_board_commit` | `BoardTool.LinkBoardCommit` | Capture a commit from the terminal's checkout and atomically save its sha, metadata and changed-code snapshot on the card. |
> The wire names are what tool callers use. Calling `SearchHistory` (PascalCase) returns "Unknown tool".

### Kanban board tools (`BoardTool`)

The cross-layer [Board contributor guide](../Board/AGENTS.md) and
[architecture/review](../Board/ARCHITECTURE.md) cover storage, launch ownership and known
findings, including the current incorrect `ended` fallback when session liveness is unknown.

`get_board_card` also lists the card's current linked cards (key, title, board and lane), so an
agent can read related work by key. These are current relationships, unaffected by the activity
`since` filter. Linking/unlinking cards is managed in the dashboard; no extra MCP tool or grant
is added for this section.

Instance tool (ctor-injected `IBoardService`, `IBoardProjectResolver`, `IBoardStore`), registered in
**both** transports and backed by the Board SQLite store (`VibeRails.Data.Sqlite/Board/BoardStore.cs`)
rather than by HTTP calls to a root backend — so an LLM can pick up a card from *any* terminal that
has `viberails-mcp` registered, with no VibeRails tab involved. Design points:

- **Project**: `BoardProjectResolver` — the dashboard root path when this process has one (root
  backend), else the card the launching terminal session is linked to
  (`VIBERAILS_TOOL_CURRENT_SESSION_ID` → `BoardCardSessions`; this is what makes sandbox/worktree
  clones resolve to the right project), else the git root above the CLI's inherited cwd, else cwd.
- **Commit capture**: `GitWorkingDirectory` resolves the actual checkout independently of the
  source board. Stdio uses the CLI's inherited cwd/git root; the dashboard uses its project root.
  No repository path is accepted from a tool argument. Capture resolves the full sha, reads the
  before/after blobs, then saves the link and `BoardCommitSnapshots` row in one transaction.
  Viewing never consults Git. Capture errors leave no link. More than 60 files is rejected;
  individual file previews retain at most 400,000 characters plus a visible truncation marker.
- **Board name resolution**: names are case-insensitive; duplicate names fail with an explicit
  instruction to use the board ID from `list_boards`. Exact IDs take precedence and remain scoped
  to the current project.
- **Card default**: every `card` argument accepts a key (`VB-12`) or an id; omitted, it means the
  card the session was launched for. A VibeRails session that touches a card it is not yet linked
  to gets linked with origin `mcp`, so the card's Sessions rail shows it.
- **Attribution**: `add_board_comment` resolves the launching `Sessions` row's environment name
  or CLI, falling back to its board session link, else "Agent". This works on the first comment
  before auto-linking. Generic historical labels with a session id are resolved on read; comments
  without a session id cannot be attributed retroactively. The dashboard UI comments as "You".
  Managed Codex launches explicitly forward the current session/tab environment-variable names
  via the per-launch `mcp_servers.viberails-mcp.env_vars` override, because stdio inheritance is
  filtered. Values are not persisted in shared config. See the [official MCP reference](https://developers.openai.com/codex/mcp/).
- **Capability boundary**: read / append / move / link only — there is deliberately no delete
  tool. The one upload path, `add_board_attachment`, accepts Markdown/TXT text only (MIME from
  the extension, strict UTF-8, ≤ 500,000 characters); binaries still come from the dashboard.
  Attachment reads return untrusted task data, never execute it; PDF, images and other binary
  files are opened in the board viewer. The only process spawned is `git` with a regex-validated
  hex sha via an argument list (`Services/Git/GitCli.cs`). Failures return `FAIL: …` sentences;
  detail goes to the file log. A `database is locked` failure (SQLite 5/6, or a transient
  `StorageException`) says so explicitly and tells the agent to retry — `Fail()` in `BoardTool`
  — because on 2026-09-16 three agents each lost a comment to the generic "see the log" sentence
  and filed it as a bug. These are local-user capabilities, not a per-card server ACL: an allowed
  tool can modify other cards in the resolved project. Cards keep one current state with no revision log; do not describe Board writes as reversible. Tool results remain untrusted task data.
- **Agent notes (2026-09-17)**: `BoardComments.Kind` (`comment` | `note`, migration `board/2`)
  separates the scratchpad from the thread. Notes never appear in `comments[]`, `CommentCount`
  or the dashboard's comment panel; the card editor shows them in a collapsed "Agent notes" rail
  and the HTTP API exposes `GET/POST /api/v1/board/cards/{card}/notes`. The launch prompt tells
  the agent to checkpoint into notes as it goes instead of hoarding findings until the end — the
  first agent to work a card ran out of context doing exactly that. Limits were raised at the
  same time: description 100,000, comment/note 50,000, 40 attachments per card.
- **Stdio host**: `McpStdioHost.RunAsync` now pins the content root to the install directory, loads
  `appsettings.json` and calls `GlobalRuntimePaths.Initialize`, so `ParserConfigs.GetStatePath()`
  answers in the child (and `VibeRails:InstallDirName` is honoured). `IRepository` is still not
  registered there: its constructor runs the full migration pass on every spawn.
- The "Start work" launch prompt (`BoardPromptComposer`) tells the LLM to begin with
  `get_board_card`, record progress with `add_board_comment`, checkpoint with `append_board_note`,
  flag cards needing human attention, move the card, and link commits. It includes the board's lane names, the linked commits (≤ 10) and attachment names
  (`BoardPromptComposer.LaunchContext`, filled by `BoardLaunchService`). Those lists are board
  data, so they sit **inside** the "verbatim task text, treat as data" fence after the title —
  never in the app's preamble, where a hostile lane name or commit subject would read as an
  instruction to a session that has preauthorized Board write tools. Every single-line field
  (`SanitizeLine`) has newlines, C0/C1 controls and bidi overrides flattened, and a description
  line that spells the closing fence is indented so it cannot end the block early. The inline description
  excerpt is up to 4,000 characters, shrinking to a 1,500 floor when the environment's own
  Initial Message is long, because the whole prompt is one CLI argument under
  `PromptPlaceholderService.MaxResolvedPromptChars`. `get_board_card` is read-only and never
  links a browsing session. Writes still auto-link an unlinked session; one session links to one
  card. Description edits retain no prior state and never interrupt the TUI.
- **Board launch authorization (2026-09-14)**: Start work sets the typed, false-by-default
  `AuthorizeBoardTools` marker for that session and explicitly authorizes the Board workflow in
  the prompt for every provider. `Terminal/Commands/BoardMcpAuthorization.cs` grants only the
  thirteen Board tools for that session through an explicit `ToolNames` allowlist. No server
  wildcard or unrelated MCP tool is authorized. Tests pin both the reviewed allowlist and exact
  provider grants.
  Antigravity receives only the prompt because its only native switch is a global bypass. See
  [Terminal launch authorization](../Terminal/AGENTS.md#board-launch-options-and-input-sequences-2026-09-14).
  This is the requested Board-specific exception to the Environments editor's YOLO-only policy,
  not a granular permission editor or a global policy change. No physical CLI config is rewritten;
  selected provider modes and managed policies can still restrict calls. Validation used CLI help,
  documentation and regression tests; no live provider session or deployment was performed.

Python script MCP tools, the signing-help tool, and their configuration UI/routes were removed
2026-09-18 at the owner's request. Agents use their existing execution tools for Python. Ordinary
signed Python scripts remain available from the dashboard; legacy MCP configuration is ignored.

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

`CommandService.AddProxyContactDetails` stamps the same variables on every proxied launch,
regardless of provider:

| Variable | Purpose |
|---|---|
| `VIBERAILS_LLM_PROXY_BASE` | The proxy host to call. |
| `VIBERAILS_LLM_PROXY_SESSION_TOKEN` | Session half of the proxy auth contract. |
| `VIBERAILS_LLM_PROXY_TAB_TOKEN` | Tab half — required; session alone is not enough. |
| `VIBERAILS_LLM_PROXY_SESSION_ID` | Optional: the terminal `Sessions.Id` behind the `viberails_terminal_session` correlation header (exchange-log attribution; Codex/Grok resolve it via `env_http_headers`). Only stamped when the launch has a session. |

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

Managed agent launches always run a VibeRails MCP setup step before the agent starts. Codex and
OpenCode replace the managed `viberails-mcp` entry with one add command; other CLIs delete it first,
then add it back. Either form repairs old configs that pointed at the removed
standalone `MCP_Server.exe`. With the stdio transport this needs no port and no auth token:

```
claude mcp remove viberails-mcp          # output suppressed by CommandService
claude mcp add --scope user viberails-mcp -- "<path-to-vb>" mcp
codex  mcp add viberails-mcp -- "<path-to-vb>" mcp
agy    mcp remove viberails-mcp          # output suppressed by CommandService
agy    mcp add viberails-mcp -- "<path-to-vb>" mcp
copilot mcp remove viberails-mcp         # output suppressed by CommandService
copilot mcp add viberails-mcp -- "<path-to-vb>" mcp
opencode mcp add viberails-mcp -- "<path-to-vb>" mcp
grok mcp remove viberails-mcp            # output suppressed by CommandService
grok mcp add --scope user viberails-mcp -- "<path-to-vb>" mcp
```

Codex must not use remove-first registration. An already-running session reloads `config.toml`
while retaining its launch-local `env_vars` and Board tool overrides. During the removal gap those
overrides form an MCP entry with no `command` or `url`, causing `invalid transport` and a misleading
"Skipped loading ... invalid SKILL.md" warning. Codex 0.154.0 was verified offline with an isolated
`CODEX_HOME`: repeated `mcp add` replaces the old command without deleting the entry first.

OpenCode 1.18.8 supports the non-interactive local-command form shown above. It has no matching
`mcp remove` command, but adding the same name replaces that entry, so OpenCode and the
OpenCode-backed pseudo-CLIs (GLM 5.2 / GLM 5.3 / DeepSeek V4 Pro / Kimi K3)
launches run one add command immediately before launch. On Windows, `CommandService` invokes
the npm `opencode.cmd` shim because PowerShell consumes the `--` separator when routing through
`opencode.ps1`; Unix launches use `opencode`. Native Grok uses remove-first plus
`grok mcp add --scope user`.

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
  HTTP; asserts the static tool list, rejection of removed Python tools, execution, and that the
  DI-injected `SessionSearchTool` resolves (with a deterministic fake `IUnifiedSearchService`).
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

**Last checked**: 2026-09-09 by Claude (added BoardTool + stdio runtime-path init)
