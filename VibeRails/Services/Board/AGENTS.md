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
| `@path` file references (typeahead, rendering, prompt line) | `board-file-refs.js`, the `@` token in `board-text.js`, `BoardFileReferences.cs`, `BoardFileIndexService.cs` |
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
  commit operation. A board/lane/card ID is not proof of access. A card key (`VB-n`, `VR-n`) is
  unique per project, across boards; its high-water sequence must survive card/board deletion.
  The prefix is the project's, fixed by its first card (`BoardProjectKeys`, see
  [ARCHITECTURE.md](ARCHITECTURE.md#identity-ownership-and-state)) and never rewritten; existing
  projects keep `VB`. Keys are computed on read from `Number` and that prefix; do not store
  them, and never build a key from a literal `'VB-'` in SQL or JS — use `card.key`/`Key`.
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
  following the root [attention policy](../../../AGENTS.md#board-attention-flags). Reserve it for
  important unresolved owner decisions or intervention, such as an unauthorized breaking database
  change or a confirmed security/data-loss problem; explain the issue and requested action in a
  comment. Routine PR review, completion and compatible additive schema changes do not warrant
  a flag. Clear an agent-set flag when its reason is resolved and no other reason remains.
  Omitted patch fields leave the flag unchanged.
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
  automatic assignee launch is still a TODO. Agents are told about them (VB-34): every lane list
  annotates on-entry Automations from the Job definition, `move_board_card` appends what the
  entry queued or skipped and why (never silent), and `skipAutomations` deletes the entries a
  move recorded in that same transaction, recording the skip as a comment by the caller.
- Current launch exclusion is root-local (F3). Do not assert cross-process mutual exclusion from
  a static dictionary or confuse a removable session-display link with execution ownership.
- Saving a card never sends terminal input. Do not reintroduce the removed notification/TUI
  handshake. Compose the launch prompt from the card read for that launch.
- The card's **Run automation** action runs an enabled project Automation against the saved
  card, preserving editor drafts. Its immutable manual trigger is `board-card:<key>:<event>`;
  `JobBoardContext` carries the card context into its terminal tab and recording link. Queued
  and failed-before-launch runs remain visible through the card Automations endpoint. Ordinary
  manual runs/retries retain their own context and native-terminal behavior.
- New cards and ordinary card updates go to the top of their lane. Comments, notes, session,
  attachment, commit and linked-card changes promote the affected cards in the same transaction;
  dense position rewrites do not count as activity on the other cards. Explicit drag positions
  remain authoritative. A lane move without a position goes to the top of its destination.
- With an active session, Start work becomes **Go to agent** and focuses its existing terminal.
  Lane-triggered Automations link their full workflow recording to the originating card's
  Automations rail, directly below Sessions. Board runs always open terminal tabs; other triggers
  (including manual retries) open native terminals. Older native recordings remain visible.
  The run's immutable Board trigger and project scope determine the card; manual retries do not
  inherit the original trigger's Board context.

## Card text syntax: `@path` file references

Card descriptions and comments can name a repository file as `@relative/path/from/repo/root` or
`@"path with spaces"` (VB-35). The text is the only storage: no link table, no unlink UI, no
existence check, and agents already receive the description verbatim through `get_board_card`
and the launch prompt. A reference counts only at the start of the text or after whitespace, never
inside inline code or a fence, and a bare reference must contain a `/` or a `.` so an email or
`@claude` in prose stays prose. Three places implement that rule and must stay in step:
`board-text.js` renders the token as an inert `<code class="board-file-ref">` built from escaped
text only (no href in v1); `BoardFileReferences.cs` extracts distinct references for the launch
prompt's `Referenced files (relative to repo root):` line (inside the fence, cap 20, `SanitizeLine`
each, `+N more`); and `formatFileReference` in `board-file-refs.js` decides whether the popup
inserts the bare or the quoted form. `GET /api/v1/board/files?q=` (root-only, both credentials)
backs the composer typeahead through `BoardFileIndexService`: `git ls-files --cached --others
--exclude-standard` filtered to files that still exist, a bounded directory walk that skips
`.git`/`bin`/`obj`/`node_modules` when git is unavailable, a ten-second in-memory cache, file-name
hits before directory hits, 50 results, `q` at most 256 characters. Names only, never contents,
never outside the dashboard's root path.

## Security and resource policy

Read API_SEC before changing exposure/authentication/listeners. Board HTTP routes stay under
`/api/v1/board`, behind session **and** tab credentials, active-root only. HTTP MCP stays behind
the same credential pair; stdio stays a local child with no listener. Record confirmed violations
in root `SECURITY_ERROR.md`; distinguish findings from speculative risks and accepted policy.

Board tool authorization is an explicit, default-false launch field. Keep HTTP/stdio registrations
and the exact per-tool grant allowlist in sync. No server wildcard, unrelated-tool approval,
or rewrite of shared provider settings. The separate, default-off card YOLO option may add the
selected base provider's documented global bypass/auto-approve launch flag; do not conflate that
user choice with the narrow Board-tool grants. Check provider-specific behavior without assuming
one CLI's switches apply to another.

Treat all card fields, comments, notes, commit messages and attachment contents as untrusted data.
Preserve prompt caps, control/bidi handling and placeholder neutralization, including generated
metadata. Prompt fences are not access control. Preserve escape-first rendering and inert file
previews: no arbitrary HTML, SVG document, iframe execution, or external image URL from card text.
Keep authenticated octet-stream downloads with `nosniff`, restrictive CSP and `no-store`.

Current limits include title 300, description 100,000, comment/note 50,000, 20 tags of 40 characters,
40 current attachments, and 500,000 characters for an MCP-written TXT/Markdown attachment.
`read_board_attachment` returns raster images through MCP only up to 5 MiB; larger images stay
available through the Board viewer. That is a tool-result/transport bound, not an upload or storage
quota. The metadata preflight must happen before reading the attachment BLOB.
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
`board/1`–`board/13` already exist. `board/12` adds the Jira connection and issue-link tables; `board/13` adds the trigger that deletes a board's Jira connection with the board. `board/8` is a breaking retirement (generation 3)
that drops history tables, WIP limits and removed-file retention through an automatic, backed-up upgrade;
`board/9` adds the current-state attention flag. `board/10` adds `BoardAdditionalCardSessions`,
leaving primary links in `BoardCardSessions` and reading both through the store without a backfill.
`board/11` adds `BoardProjectKeys` (per-project card key prefix), seeded with `VB` for every
project that already numbers cards so that no existing key changes.
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
Lane-entry triggers write one pending row per selected Automation and card in the move transaction;
a caller-requested skip deletes those rows before that transaction commits.
`DescribeLaneAutomationsAsync` reads Job definitions from `state.db` for wording only and must keep
tolerating absent tables, because a stdio MCP host can open a state.db without Automations.
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
| `@path` references / file index | `BoardFileIndexServiceTests`, the referenced-files cases in `BoardPromptComposerTests`, the files route case in `BoardRoutesTests`, `board-text.test.mjs`, `board-file-refs.test.mjs`, the typeahead case in `board-ux.spec.js` |
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

For Board-card sessions, read the full card and earlier activity before resuming. Checkpoint findings in notes
and inspect the comments explaining any attention flag, including review findings and later resolutions.
Launch prompts explicitly report flag status and require this check even when activity counts are zero.
Record user-facing decisions in comments, and link commits after capture succeeds. Move work to
Review when it is ready for human review; do not treat open implementation findings in a research
spike as fixed. Component documentation belongs here; runbooks and diagnostic history belong in
the private `vibe-books` repository or the active card's notes.
