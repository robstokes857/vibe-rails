# Working on Vibe Board

## Attention requests (VIBE-40)

Agent `flagged=true` updates require `flagReason`: the important unresolved issue and the
specific user action needed. Validation precedes every write; the flag, ordinary `comment` row
with attention metadata, and local session request commit together. Reserve alerts for major
bugs, security/data-loss issues or missing information/decisions that block the task. Routine
progress, completion and review stay unflagged. Clear only when no unresolved reason remains.

`BoardStore.Attention.cs` owns persistence behind `IBoardStore`. Clearing Flagged resolves all
requests for that card, including when an older writer clears it. Historical comments stay red;
sync and merge preserve their marker. Terminal polling matches the requesting session, including
an Automation's Worker recording, and never colors every session attached to a flagged card.

## Card recall (VB-13)

Use `get_board_card` first for named-card questions. `save_board_handoff` records concise previous
work and curated entry points without editing the task; it also posts the handoff in Comments.
Keep recall persistence behind `IBoardStore`. `read_board_session` may read only linked sessions;
its previews and full-document reads are paged. `search_history` keeps explicit keys project-scoped
and labels broader-history fallback. Discussion sessions may coexist with work and never count as
working agents. Preserve short bootstraps, input limits, lane state and the prohibition on injecting
generated input into a live TUI. See [the recall contract](ARCHITECTURE.md#card-recall-vb-13).

## Local MCP board discovery (VIBE-28)

`list_boards` shows current-project boards first, then other local projects with their paths.
Explicit board IDs and unambiguous names can select any local board; duplicate names require IDs.
Full stored permanent card keys and row IDs resolve across local projects. Short keys and display
IDs keep their current-project meaning. Resolve the target's project from `IBoardStore` metadata
before calling scoped services; REST and transactional membership checks stay project-scoped.
Omitted targets keep the launching card/current-project defaults. Cross-project writes never
auto-attach a session, and explicit session attachments remain within the current project.

## New-board review defaults (VIBE-23)

The single local lane template records a `BoardStarterWorkflows` intent with the new Review
lane's ID in the board-creation transaction. `BoardStarterWorkflowService` recovers it through
`IJobStore.EnsureBoardReviewRecipeAsync`, then assigns it through `IBoardStore`. Workers, Jobs
and the stable recipe receipt commit together in state.db. Root Board access and the existing
scheduler recover incomplete installs; a lean stdio first-board creation only records intent.
Never seed by runtime lane name, retrofit existing boards, refresh installed recipes, or restore
a removed assignment. A settings write, even an empty selection from an older writer, cancels
pending installation. Removing a lane or assignment keeps the shared Automation and Worker.
See [the starter contract](ARCHITECTURE.md#new-board-review-defaults-vibe-23).

## Waiting lane Automations (VIBE-21)

VIBE-42 adds a waiting tile badge and an exact-entry skip action in the card's Automations rail.
Keep the card visible in its selected lane; only its Automation entry waits. Bulk activity must
exclude committed runs even before acknowledgment. A skip must not affect a newer entry or stop
a committed run. Scripts added in the lane picker use ordinary Jobs and existing script approvals.

Busy lane demand stays in the existing Board queues. Keep the oldest pending entry per Job
first, with one pending entry per card/Job and a fresh 60-second delay after reentry. Moves and
settings edits cancel uncommitted entries; committed runs keep their own lifecycle. The additive
dispatch ledger retains reasons, while actual Job run status wins after acknowledgment failures.
UI, get_board_card, get_board_agent_status and review discovery share that state. Manual card
runs still reject overlap; do not apply lane waiting to other trigger policies. See
[the waiting contract](ARCHITECTURE.md#waiting-lane-automations-vibe-21).

## Code reviews (VIBE-20)

Purpose is explicit on the Worker/Environment and snapshotted onto each queued Job run.
`BoardReviewService` and `BoardStore.Reviews.cs` own direct attempts and canonical reports;
UI and MCP share that contract. Keep process outcome separate from report result. Never infer
review purpose from names, retag history, or turn a successful exit into approval. See
[the review contract](ARCHITECTURE.md#explicit-code-reviews-vibe-20) for scope, freshness and storage.
Both worker/reviewer prompts must name `get_board_reviews` and explain polling and fixing agreed
blocking findings. High (including critical) and medium-high findings block completion of reviewed
work. Medium-low and low findings are non-blocking notes: the worker chooses to fix now or defer
to a backlog card, referencing the originating card/finding there and linking the backlog key in
an originating-card comment. These notes alone do not require another review. Judge severity by
concrete impact and keep optional refactors and style preferences non-blocking. Save evidence/handoff
before any human/LLM-chosen movement; keep existing lane execution.

VIBE-49 adds optional testing/building/deploying/documentation/other purposes alongside work and
code_review. New comments and handoff receipts snapshot purpose in existing `Changes.agentPurpose`
metadata; review receipts use code_review. Queued Automations use their immutable run purpose;
direct saved-environment sessions use the current Environment purpose when posting. Existing
comments are never reclassified. The DTO and sync portable-field allowlist carry the metadata.
Filters preserve attention comments regardless of purpose. No schema change or historical backfill.

Completion instructions explicitly order report → handoff → intended movement → complete_board_agent
→ end_agent_session → final response. Wait for any required review before ending your own session.
Standalone Automation Workers get the self-exit guidance without requiring Board tools or a card.
See the [terminal contract](../Terminal/AGENTS.md#agent-self-completion-and-terminal-menu-vibe-49).

The lane add form has a separate body and type/Automation selectors. Lane VCA/Code quality additions
use working-tree scope. `GET /api/v1/jobs/scripts` discovers bounded repository script candidates
through the existing file index and validates them using `AutomationScriptService`. Saved content
hashes identify approved scripts; discovery never approves a file. The normal save route still
approves current contents. Catalog limits (200 candidates, 32 MiB total inspection) are explicit.
Optional `q` searches the cached script path index before either limit (256 characters maximum),
so a valid script beyond the initial prefix can still be found without entering its whole path.

## Switch reviewer (VIBE-22)

Keep coding attribution explicit per card. Never derive it from assignment, latest linked session
or a review/chat session. `ReviewRoutingService` owns resolution for direct reviews and existing
Workers/Jobs; retries retain the queued routing and scope. Preserve the stated project checkout,
surface unavailable targets, and never substitute providers or permission flags. See
[the routing contract](ARCHITECTURE.md#switch-reviewer-vibe-22) for snapshots, steps and recipe limits.

## Card checks (VIBE-24)

Read [CHECKS.md](CHECKS.md) for scope, immutable evidence, UI lifecycle and starter review defaults.
Keep deterministic checks outside the Git preflight pipeline and all evidence behind IBoardStore.
Results never move cards. VIBE-23 consumes `ReviewCheckDefaults.Create()` when seeding new templates.

## Workflow intent (VIBE-17)

Keep the existing automatic lane-entry execution and settling delay. Humans and LLM agents
decide card movement through the UI or Board MCP tools. Give agents the lane context, purpose,
expected output and workflow instructions so they can choose the appropriate next action.
Do not add deterministic application rules that move cards based on review or Automation outcomes,
or impose a stay-in-lane default. Lane names, order and the meaning of Done remain user-defined.
New template review defaults use Switch reviewer (Claude to Codex, Codex to Claude), with editable
mappings and a fixed Codex review alternative; the implementation scope is tracked on VIBE-20–25.

## Board sharing (VB-52)

See [SYNC.md](SYNC.md#vb-52-shared-boards) for the POC contract. Persistence stays behind
`IBoardStore`. Imported boards retain their remote/destination identity and never fall back to
owner publication after access ends. The hosted API enforces owner-only collaborator management.
Keep recipient-private declines/blocks and the response that does not disclose email registration.
Imported activity must not overwrite owner snapshots.

## Unified discussion and card organization (VIBE-1)

Comments is the one discussion stream, including retained legacy `note` rows. New
`append_board_note` calls are compatibility aliases for comments; `get_board_notes` reads the
whole discussion. The old `notes[]` response remains empty for older clients. Checkpoints and
handoffs both use `add_board_comment`. History stays separate and absent from agent context.

The card editor's **Move or merge** section can move to any lane in this project, or merge into
another project card. Both ends are checked transactionally. Moving keeps identity, attachments,
sessions and commits; the destination's normal lane Automations still apply. Merge keeps the
destination fields, appends the source description, copies discussion/files/sessions/commits/links,
then soft deletes the source and clears its pending lane events. Duplicate sessions/commits/links
are deduplicated; attachment IDs are copied and references rewritten. Over-limit merges fail
before writing. Source rows remain retained. New destructive MCP tools are not exposed.

Only the human REST/UI path can delete discussion entries, including agent entries; the store
rejects agent authors. `board/25` adds comment tombstones plus `SyncBoardId`, `TransferRemoteSeq`
and `DiscussionHidden` on log rows. No startup backfill or historical rewrite. Older binaries can
still show deleted discussion. See SYNC.md for transfer delivery and hosted comment deletion.
The Board settings sync section is removed; automatic background publication continues.

Read [ARCHITECTURE.md](ARCHITECTURE.md) for the component map, wire contracts, data model,
security boundaries and open VB-18 findings. This file is the contributor guide for Board work
across UI, REST, MCP and storage. The root [AGENTS.md](../../../AGENTS.md),
[API_SEC.md](../../../API_SEC.md), and relevant directory instructions still apply.

## First board name (VIBE-16)

A project's first board takes the custom project name — the latest `Sessions.ProjectDisplayName`
for that working directory — and otherwise the repository folder name. The retired `"Main"`
default is never written; a folder that is itself called Main gets `Board`. Boards that already
exist keep their names, and a board the user creates still uses the name they type.
`BoardStore.ChooseDefaultBoardName` is the one rule. Lane adoption during schema setup uses the
folder name only, because that pass has no custom-name lookup.

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
| Agent context size (VB-63): the editor's "Agent context" section, launch samples, `get_board_card` activity budget | `BoardContextEstimator.cs`, `ContextTokenEstimator.cs`, `BoardStore.ContextSamples.cs`, `BoardTool.FormatCard`, `wwwroot/js/modules/board-card-context.js`; plan in `vibe-books/vibe_board_context_rot/about.md` |
| Provider grants / child context | `Services/Terminal/Commands/BoardMcpAuthorization.cs`, OpenCode companion; read [terminal instructions](../Terminal/AGENTS.md) |

Do not add SQL to routes/tools. Board persistence lives in `~/.vibe_rails/board.db`; legacy Board
tables in `state.db` stay untouched and unused, without migration or synchronization. Keep every
Board read/write behind `IBoardStore`, including pending Automation events, to preserve a single
boundary for a future shared API-backed Board. Keep validation in the shared
service and atomic persistence in the store so REST and MCP agree. Preserve source-generated
JSON metadata in both host and storage contexts when DTOs change; do not introduce reflection
serialization or tool discovery into the Native AOT path.

## Invariants to preserve

- Derive project identity server-side (MCP explicit targets use the local discovery contract above). Scope **both ends** of a move, link, lookup, attachment or
  commit operation. A board/lane/card ID is not proof of access. A card key (`VB-n`, `VR-n`) is
  unique per project, across boards; its high-water sequence must survive card/board deletion.
  The prefix is the project's, fixed by its first card (`BoardProjectKeys`, see
  [ARCHITECTURE.md](ARCHITECTURE.md#identity-ownership-and-state)) and never rewritten; existing
  projects keep `VB`. Since VB-51 a new card stores its key once, at creation, in
  `BoardCards.CardKey`: `{PREFIX}-{5 crypto-random [A-Z0-9]}-{Number}`, upper-case, immutable and
  unique in `board.db`, so the same key can name the card on viberails.ai. A NULL `CardKey` is a
  card an older binary created; its key is still computed from the prefix and `Number`. Lookups
  try the stored key (case-insensitive), then prefix + number (so `VB-12` still finds
  `VB-7K2QZ-12`), then the id. Never build a key from a literal `'VB-'` in SQL or JS — use
  `card.key`/`Key`.
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
  expected-description token or launch/read revision events. Description append runs against the
  current text inside the same write transaction as the other fields, with length validation
  before any writes.
- **Card Log (VB-51).** Every card write also appends a `BoardComments` row in the same
  transaction: `Kind` `created` / `change` / `deleted` (`restored` is reserved for VB-54), the
  author, a readable `Body` summary (at most 2,000 characters) and a `Changes` JSON of
  `{field: {from, to}}` for sync. It is a log of what changed, not a revision store: nothing is
  restored from it. A description change records the full new description (up to 100,000
  characters) in `Changes` by design, so the log grows with description size on every edit and
  every `descriptionAppend`. Reordering within a lane and launch options are not logged. Older binaries
  read `comment`/`note` rows only, and `CommentCount` counts `comment` rows only. The card
  response contains only Comments and Agent notes. History is a separate, explicit settings
  view for both cards and boards, loaded on demand. Never expose History through MCP or agent
  launch context. See [SYNC.md](SYNC.md) for the portable sync and sharing contract.
- **Agent context is measured, recorded and budgeted (VB-63).** `GET /api/v1/board/cards/{card}/context`
  (`BoardContextEstimator`) renders the real launch prompt (`BoardLaunchService.ComposePromptAsync`)
  and the real `get_board_card` / `list_board_columns` output (`BoardTool.RenderCardAsync`,
  `RenderLanesAsync`) and counts them with `ContextTokenEstimator` (characters ÷ 4, always shown
  as ≈); the **Agent context** block in the card editor's collapsed **Advanced** section shows the result. Every Start work / Chat
  launch records a `BoardContextSamples` row (board/19) and, in the same transaction, a Card Log
  `change` entry whose `Changes` is `{"context":{"to":{…}}}`, so the number syncs (the `context`
  field is in `PortableChanges`; the hosted contract keeps unknown fields verbatim) and shows in
  History. A failed measurement never fails a launch. `get_board_card` lists comments and notes
  newest first: when comments + notes fit `BoardTool.ActivityBudgetCharacters` (24,000) nothing is
  hidden; otherwise comments fill first, notes keep `NotesReservedCharacters` (8,000), the newest
  entry is always shown (cut with a marker if it alone exceeds the allowance), and older entries
  become one-line previews with ids. `before=<comment/note id>` (the reply names it; an ISO-8601
  timestamp also works, time-only) pages a window back without skipping entries that share an
  instant, `activity=all` lifts the budget, `get_board_notes` lists every note; sessions/commits
  list the newest 10/30. The card editor measures when Advanced is opened and re-measures after every rail mutation
  (comment, commit, session, attachment, linked card) while it stays open; a closed section only
  marks the numbers stale.
  Change the numbers in one place and update the plan in `vibe-books/vibe_board_context_rot`.
- **Deleting a card is a soft delete (VB-51).** It sets `BoardCards.DeletedUTC` and writes a
  `deleted` log entry; every read, count, lookup, pull and lane Automation skips it, and its rows
  (comments, attachments, links, sessions) stay. A Jira pull never recreates a soft-deleted card.
  Restore and the 30-day purge are VB-54. An older binary still shows soft-deleted cards.
- Attachment removal deletes its row and cascades its bytes. Only current attachments are
  readable. Database backups can retain older data; removal is not secure erasure.
- **Agent-made (VIBE-11).** `BoardCards.AgentMade` is set once, inside the create transaction, and
  only when `create_board_card` runs with an agent author. The board UI, a Jira pull, a synced
  card and every card that already exists stay human-made (`0`). No update path changes it, so an
  agent editing a human card cannot relabel it, and the field is absent from `update_board_card`
  and from the sync change set. The lane tile shows a robot mark beside the key; the toolbar's
  origin filter (`agent` / `human`, query `origin`) applies to the whole board, including cards
  the browser has not loaded. `get_board_card` says `Agent-made` or `Human-made`.
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
  terminal is a Sessions action or **Chat with agent**, whose independent shared LLM/environment
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
- With a live working agent, Start work becomes **Go to agent** and focuses its existing terminal.
  The working agent (`activeSessionId`/`activeTabId`) is the first live linked session that is not
  an Automation's. A lane Automation and the CLI it spawned stay linked to the originating card but
  never count, and never block a launch, so once the launched agent's tab is gone the card returns
  to Start work instead of opening the review terminal (VB-6Q8ZS-68 follow-up). Start work may
  therefore launch an agent while the card's Automation is still running beside it; that is
  intended (86527e0). The card list, card detail, activity poll and launch gate all decide what an
  Automation's session is through `BoardService.IsAutomationSession`; change the rule there.
  Lane-triggered Automations link their full workflow recording to the originating card's
  Automations rail, directly below Sessions. Board runs always open terminal tabs; other triggers
  (including manual retries) open native terminals. Older native recordings remain visible.
  The run's immutable Board trigger and project scope determine the card. Code review retries
  retain it through `board-review-retry:` independently of routing, preserving prompts, grants,
  linking and canonical reports. Work retries do not inherit the original Board context.

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
`board/1`–`board/24` already exist. `board/24` (VIBE-11, additive) adds `BoardCards.AgentMade`
(default 0, no backfill): set only when `create_board_card` runs as an agent, left alone by every
later edit, and ignored by an older binary. `board/23` adds the activity schema version and durable rotation
cursor on `BoardSyncLinks`. VIBE-13 automatically publishes all local boards and activity whenever
an API key is configured; the old switch columns remain for compatibility. The scheduler-only
`GetBoardsForSyncAsync` reads board identity metadata across projects through `IBoardStore`.
See [SYNC.md](SYNC.md) for the bounded activity transfer and automatic recovery.
`board/22` (additive) adds `BoardSyncSkippedEntries.Version`, so
a newer version retries what an earlier one skipped; `board/21` (additive) adds `BoardSyncSkippedEntries` (see
[SYNC.md](SYNC.md)); `board/20` (VB-69) adds display IDs. `board/19` (VB-63, additive) adds `BoardContextSamples`. `board/12` adds the Jira connection and issue-link tables; `board/13` adds the trigger that deletes a board's Jira connection with the board. `board/8` is a breaking retirement (generation 3)
that drops history tables, WIP limits and removed-file retention through an automatic, backed-up upgrade;
`board/9` adds the current-state attention flag. `board/10` adds `BoardAdditionalCardSessions`,
leaving primary links in `BoardCardSessions` and reading both through the store without a backfill.
`board/11` adds `BoardProjectKeys` (per-project card key prefix), seeded with `VB` for every
project that already numbers cards so that no existing key changes. `board/14` (VB-51, additive)
adds `BoardCards.CardKey` / `DeletedUTC`, `BoardComments.Changes` / `RemoteSeq` (NULL unsent,
0 local-only, >0 the viberails.ai sequence; -1 is a retained rejection since board/17) and `BoardSyncLinks` (one row per published board),
with no backfill.
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

## Display IDs and draft links (VB-69)

`CardKey` / `Key` and the card row `Id` stay immutable. `DisplayId` is the mutable label shown
in the UI and accepted by project-scoped lookup/search. Prefer row IDs for mutations. Board
settings supply a display prefix (1–8 ASCII letters/digits, letter first); the default is the
first four usable repository-name characters, padded with X for short names. Prefix changes
apply to future cards. Sequences are persistent per project/prefix, shared across its boards.
Existing cards retain their old visible key; older writers' NULL display IDs fall back to Key.
`board/20` is additive and performs only the expressly requested display-ID backfill.

Display IDs and the board prefix sync. The server's first accepted label wins; a collision
creates a separate correction event without changing the received event or immutable identity.
Incoming labels can rename a local occupant and queue its correction atomically. The sibling
Front change must ship for hosted label projection and server collision resolution.

The new-card link picker searches through `GET /api/v1/board/cards/link-candidates`; selected
`linkedCardIds` are saved in the creation transaction, with both ends scoped to the project.
Canceling the draft writes nothing. Existing-card links still save immediately. Discussion has
its own section with the shared agent picker followed by a text-style Chat button; its intent,
independent assignment, save-before-launch and active-session guard remain unchanged.
