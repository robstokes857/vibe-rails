# Working on Vibe Board

Read [ARCHITECTURE.md](ARCHITECTURE.md) for the component map, wire contracts, data model,
security boundaries and open VB-18 findings. This file is the contributor guide for Board work
across UI, REST, MCP and storage. The root [AGENTS.md](../../../AGENTS.md),
[API_SEC.md](../../../API_SEC.md), and relevant directory instructions still apply.

## Find the right layer

| Change | Start here |
| --- | --- |
| Lanes, filters, editor and launches | `VibeRails/wwwroot/js/modules/board-controller.js`; read [frontend instructions](../../wwwroot/AGENTS.md) |
| Text, uploads/previews, card links, base options | Adjacent `board-text`, `board-attachments`, `board-card-links`, `board-launch-options` modules |
| Browser requests / REST shape | `board-api.js`, `VibeRails/Routes/BoardRoutes.cs`, Board DTOs in `ResponseRecords.cs` |
| Validation and orchestration | `BoardService.cs` and its partials in this directory |
| SQL and migration | `VibeRails.Data.Sqlite/Board/BoardStore*.cs`, `BoardStore.Options.cs`; contracts in `VibeRails.Data.Abstractions/Board` |
| Start work / prompt | `BoardLaunchService.cs`, `BoardPromptComposer.cs`, `BoardSelection.cs` |
| Agent tools and project context | `Services/Mcp/Tools/BoardTool.cs`, `BoardProjectResolver.cs`; read [MCP instructions](../Mcp/AGENTS.md) |
| Provider grants / child context | `Services/Terminal/Commands/BoardMcpAuthorization.cs`, OpenCode companion; read [terminal instructions](../Terminal/AGENTS.md) |

Do not add SQL to routes/tools. Board persistence lives in `~/.vibe_rails/board.db`; legacy Board
tables in `state.db` stay untouched and unused, without migration or synchronization. Keep every
Board read/write behind `IBoardStore`, including pending Automation events, to preserve a single
boundary for a future shared API-backed Board. Keep validation in the shared
service and atomic persistence in the store so REST and MCP agree. Preserve source-generated
JSON metadata in both host and storage contexts when DTOs change; do not introduce reflection
serialization or tool discovery into the Native AOT path.

## Invariants to preserve

- Derive project identity server-side. Scope **both ends** of a move, link, lookup, attachment or
  commit operation. A board/lane/card ID is not proof of access. `VB-n` is unique per project,
  across boards; its high-water sequence must survive card/board deletion.
- A card's board comes from its lane. Names are display values and can be duplicated. Prefer IDs
  for mutations; when resolving names, reject ambiguity. Current lane resolution still needs F5.
- Keep number allocation, lane renumbering, card writes, attachment membership,
  and commit link/snapshot atomic. Perform slow Git/process work outside write transactions.
- A session may attach to multiple cards within one project. Reads never attach; ordinary writes
  only auto-link an entirely unlinked session. `attach_board_session(card)` explicitly adds another
  attachment. Keep the original link as the omitted-card default, falling back to the oldest
  remaining attachment if removed. Rename/unlink acts on one card/session pair. Unknown session
  status is not ended (F6); live indicators still depend on the root-local probe.
- Cards keep one current state. Replacements accept the last write; no description revision,
  expected-description token, history rail, history MCP tool or launch/read revision events.
  Description append runs against the current text inside the same write transaction as the
  other fields, with length validation before any writes. Do not restore revision tracking.
- Attachment removal deletes its row and cascades its bytes. Only current attachments are
  readable. Database backups can retain older data; removal is not secure erasure.
- `Flagged` means **Needs your attention**, independently of `Blocked`. The editor saves it;
  the tile paints red with a flag icon. Agents set/clear `flagged` through `update_board_card`
  and explain the requested review in a comment. Omitted patch fields leave the flag unchanged.
- Commit viewing reads durable snapshots, never the current checkout. Capture from the caller's
  actual checkout, not automatically the source board directory. Reject capture failure before
  creating a link; preserve bounds, truncation markers and unique-prefix handling.
  A session's `link_board_commit` call shares that captured snapshot with the explicit target
  and all attached cards in the project. Resolve membership and write all pairs atomically;
  repeat calls preserve existing links and snapshots. No new unlink/edit MCP tool is exposed.
- Explicit Save/Create never starts an agent. Start work saves first, retains normal workspace
  resolution, grants only Board tools, links the session, and stays on the Board. Opening a
  terminal is a Sessions action or **Chat with:**, whose independent shared LLM/environment
  picker defaults to the assignee. Chat saves first, sends the selected launch override without
  reassigning the card, and launches with discussion intent, then focuses the returned terminal.
  Lane Automations are independent existing Jobs, queued after a 60-second settling period;
  automatic assignee launch is still a TODO.
- Current launch exclusion is root-local (F3). Do not assert cross-process mutual exclusion from
  a static dictionary or confuse a removable session-display link with execution ownership.
- Saving a card never sends terminal input. Do not reintroduce the removed notification/TUI
  handshake. Compose the launch prompt from the card read for that launch.

## Security and resource policy

Read API_SEC before changing exposure/authentication/listeners. Board HTTP routes stay under
`/api/v1/board`, behind session **and** tab credentials, active-root only. HTTP MCP stays behind
the same credential pair; stdio stays a local child with no listener. Record confirmed violations
in root `SECURITY_ERROR.md`; distinguish findings from speculative risks and accepted policy.

Board tool authorization is an explicit, default-false launch field. Keep HTTP/stdio registrations
and the exact per-tool grant allowlist in sync. No server wildcard, unrelated-tool approval,
global sandbox bypass, or rewrite of shared provider settings. Check provider-specific behavior
without assuming one CLI's switches apply to another.

Treat all card fields, comments, notes, commit messages and attachment contents as untrusted data.
Preserve prompt caps, control/bidi handling and placeholder neutralization, including generated
metadata. Prompt fences are not access control. Preserve escape-first rendering and inert file
previews: no arbitrary HTML, SVG document, iframe execution, or external image URL from card text.
Keep authenticated octet-stream downloads with `nosniff`, restrictive CSP and `no-store`.

Current limits include title 300, description 100,000, comment/note 50,000, 20 tags of 40 characters,
40 current attachments, and 500,000 characters for an MCP-written TXT/Markdown attachment.
The UI's 12-file limit is a known bug (F7), not the contract. Human file uploads deliberately have
no byte quota. Do not restore the removed byte-budget policy as an incidental “security fix”;
design streaming/concurrency/retention improvements explicitly. MCP text reads are chunked, but
the current implementation still loads/decodes the full file before returning a chunk.

## UI lifecycle

Use shared `app.apiCall`, `showModal`, `showToast`, `confirmDialog` and LLM picker. Preserve the
single editor scroll region and right rail; do not reintroduce editor tabs. Keep DOM state on the
specific editor instance, not a shared mutable card ID. Dispose Sortable, picker, link-search,
Monaco, nested preview, object URLs and pending requests on close/replacement/unload. Ignore stale
responses through request generations and cancellation.

Protect drafts across all close/navigation paths (F8); guard only linked-card navigation is
insufficient. Do not let background rail mutations overwrite the surrounding form. Account for
hidden cards when converting a drag target to a full-lane position (F4). Lanes have no WIP limits;
the header displays the full card count. Tests should use realistic asynchronous races, not only markup assertions.

## Storage changes

Read [database migration instructions](../../../VibeRails.Data.Sqlite/DB/AGENTS.md).
`board/1`–`board/10` already exist. `board/8` is a breaking retirement (generation 3)
that drops history tables, WIP limits and removed-file retention through an automatic, backed-up upgrade;
`board/9` adds the current-state attention flag. `board/10` adds `BoardAdditionalCardSessions`,
leaving primary links in `BoardCardSessions` and reading both through the store without a backfill.
Historical migration SQL stays immutable. Add the next numbered migration rather than editing applied SQL.
Honor generation checks, automatic backups and transactional migration coordination. Users must
never need a special command or manual preparation to use a new version. Prefer additive changes
for concurrently running versions; retire a feature by stopping reads/writes, retaining its tables.
Do not add historical-data conversion or backfills unless explicitly requested. Update schema
snapshots and relevant compatibility tests when the schema changes. Do not create a writer transaction
merely to read; use a deferred read snapshot if coherence requires a transaction. Connection
policy belongs in the shared SQLite factory, not a Board-specific timeout/WAL workaround.

Automated regression tests use temporary database fixtures and fake tab hosts, not the user's
stored data. Running/debugging uses the normal `state.db` and `board.db`; do not introduce a
separate development database to conceal unsafe code. Live-provider launches and other real
application actions are not required setup for unit tests.

Board context and lane Automation settings still use their own expected revisions. Settings writes must remain project-scoped and reject stale revisions.
Lane-entry triggers write one pending row per selected Automation and card in the move transaction.
The first selection uses the board/6 tables; additional selections use the additive board/7 tables.
The existing leased root scheduler reads pending events through `IBoardStore`, commits normal
Job run/action snapshots in `state.db`, then acknowledges each exact event in `board.db`. The
run TriggerKey deduplicates retries; acknowledgement must not delete a subsequent lane entry.
The two commits are deliberately independent, with best-effort behavior for concurrent moves.
Never replace the durable queue with a browser timer or an in-memory queue.

## Validation by change

| Change | Relevant coverage |
| --- | --- |
| Store/service/current state | `Tests/Services/Board/BoardStoreTests.cs`, `BoardServiceTests.cs`, `BoardCurrentStateTests.cs`, `BoardSimplificationMigrationTests.cs`, attachment and commit tests |
| Routes/scope/auth | `Tests/Routes/BoardRoutesTests.cs`, `CookieAuthMiddlewareTests` |
| MCP / grants | `BoardToolTests`, `McpServerHttpTests`, `McpStdioHostTests`, `BoardToolAuthorizationTests`, OpenCode/command Board tests |
| Launch concurrency/prompt | `BoardLaunchConcurrencyTests`, `BoardPromptComposerTests`, launch cases in `BoardServiceTests` |
| UI behavior | `Tests/wwwroot/js/board-*.test.mjs`; `UITests/tests/board-ux.spec.js` and `board-attachment-security.spec.js` |
| Schema changes | `Tests/DB/SchemaSnapshotTests.cs`, `PreviousReleaseCompatibilityTests.cs`, migration/Board adoption tests |
| Database separation / host composition | `Tests/DB/BoardDatabaseIsolationTests.cs`, split-file `BoardSettingsTests`, route and MCP Board tests |

Common focused commands from repository root:

```powershell
dotnet test Tests/Tests.csproj --no-restore --filter "FullyQualifiedName~Board|FullyQualifiedName~CookieAuthMiddlewareTests|FullyQualifiedName~McpServerHttpTests|FullyQualifiedName~McpStdioHostTests" -p:OutputPath=bin/BoardChecks/ --verbosity quiet
node --test Tests/wwwroot/js/board-*.test.mjs
```

From `UITests`: `npx playwright test --config playwright.board.config.js`. That browser suite uses
mock APIs and no user database or real CLI. Passing it does not verify the production launch
handshake. Report the actual validation scope and unresolved findings, not just “tests pass.”

For Board-card sessions, read the full card and earlier activity before resuming, checkpoint findings in notes,
record user-facing decisions in comments, and link commits after capture succeeds. Move work to
Review when it is ready for human review; do not treat open implementation findings in a research
spike as fixed. Component documentation belongs here; runbooks and diagnostic history belong in
the private `vibe-books` repository or the active card's notes.
