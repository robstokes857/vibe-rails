# Board persistence

VIBE-55 retires active use of `board-search/1`'s JSON-vector cache without deleting it.
`GetSearchDocumentsAsync` pages full live titles/descriptions, visible Comments/retained notes
and handoffs behind `IBoardStore`, preserving source boundaries and compact recall metadata.
It excludes change history and deleted/hidden discussion and never loads cached JSON vectors.
Background reconciliation copies these sources into the shared `search.db`; tokenization and
inference happen after the source read snapshot closes. UI/MCP queries use the shared index,
with no source-database enrichment or query-driven indexing. See the
[search contract](../../VibeRails/Services/BertV2/README.md).

`board-attention/1` adds `BoardAttentionRequests` and a trigger that resolves its rows when
`BoardCards.Flagged` is cleared, including by older binaries. No old rows are converted.
Agent flag updates validate `FlagReason` and write the flag, comment and request in one
transaction. Comments remain `Kind=comment`; `Changes.attention.to=true` marks them for red
rendering and survives sync/merge. Local request/session attribution is not synced. Requests
and comments remain stored after clearing. Terminal reads join current live cards and unresolved
requests, mapping Worker sessions through existing JobRuns/JobRunActions recordings.

`board-recall/1` adds `BoardHandoffs` and `BoardRecallEmbeddings` without modifying old rows.
Handoffs append with server provenance and a full Comments receipt in one scoped transaction;
the task description/history remain unchanged. Merge copies them and retains source data.
The derived BGE cache uses a version tied to current card text/handoff; stale vectors are ignored.
Candidate file names come from snapshot JSON metadata only. Keep recall reads project-scoped,
behind `IBoardStore`; historical description logs are not search documents.

`GetLocalBoardsAsync` lists existing local board identities without seeding projects.
`FindLocalCardAsync` resolves a live card by row ID or full stored permanent key across projects,
never by display ID or short-key alias. These support explicit MCP targeting (VIBE-28); writes
still use the resolved owning project and existing transactional checks. No schema change.

`board-starter-workflows/1` adds new-board recipe intents, bound to lane IDs at creation only.
Settings insert/update triggers retire an intent without changing existing settings or history.
Completion and assignment commit together behind `IBoardStore`; state recipe installation commits
independently through `IJobStore`. Never backfill old boards or reset completion after removal.

`board-lane-dispatch/1` adds a dispatch ledger and cancellation triggers that write only to that
new table. Existing queue/schema writers remain compatible. Busy events stay pending; the drain
selects the oldest entry per Job and rotates attempted Jobs through a bounded batch. Exact event
identity protects reentry from stale acknowledgments. Run commit and Board acknowledgment remain
independent. Status reads reconcile immutable trigger keys with local Job runs, including a run
committed before acknowledgment failed. Never backfill or infer execution from an empty queue.

`board-reviews/1` adds canonical review attempts/reports without changing old rows. Keep all
review access behind `IBoardStore`. Only the owning linked session can finalize evidence; the
report and its single discussion reference commit together. Saved reports are immutable.

## Remote launch sync check (VIBE-26)

`IsCardSyncAppliedAsync` scopes the live card to its project and Board, checks the complete
skipped-entry ledger by immutable card key through the requested sequence, and reuses the
incoming-sync field-protection rule. These reads share a deferred transaction; no writer lock
or schema change is needed. Retained rejected history is not a permanent launch block once
its fields have been corrected and acknowledged. Keep this check behind `IBoardStore`.

## Shared origins (board/26, VB-52)

`BoardStore.Sharing.cs` imports accepted shared boards and applies remote layouts atomically.
`BoardSharedOrigins` pins remote identity, destination/key fingerprint and prefix separately from
legacy sync fields. Setup adds the table without converting old rows. Keep imports project-scoped,
retain cards when retiring remote lanes, and preserve the expected-layout check. Legacy remote keys
are accepted only for explicit shared imports. See the Board `SYNC.md` contract.

## Card actions (board/25, VIBE-1)

`BoardStore.CardActions.cs` owns atomic merging, user-only discussion tombstones and cross-board
log delivery. `BoardDeletedComments` retains removed entries; `BoardComments.SyncBoardId` pins
each transferred event to its original board; `DiscussionHidden` hides superseded discussion
copies while leaving stored rows intact. `TransferRemoteSeq` holds transferred delivery marks;
`BoardSyncLog` reads the effective ledger. A conditional trigger keeps those rows local-only in
the legacy ledger even after an older sync backend resets it. Only moves bind existing rows;
schema setup is automatic and has no backfill.
Discussion reads/counts include legacy notes and exclude both deletion markers and superseded
copies. New notes are comments. Move and merge operations validate both endpoints in the project
inside the writer transaction. Do not expose merge or comment deletion as MCP tools.

Read the cross-layer [Board contributor guide](../../VibeRails/Services/Board/AGENTS.md) and
[architecture/data model](../../VibeRails/Services/Board/ARCHITECTURE.md), plus the
[database migration policy](../DB/AGENTS.md), before changing this component.

This directory owns `IBoardStore`'s SQLite implementation in `~/.vibe_rails/board.db`, including
component migrations `board/1`–`board/26`. The historical `VibeRails.Services.Board` namespace
does not move this code back into the application project. Keep DTO/contracts in
`VibeRails.Data.Abstractions/Board`; keep Git, live terminal state and UI policy in the host.

`SqliteStorage` chooses the same `board.db` beside `state.db` for root, stdio MCP and lane Automation
scheduling, including Debug. It starts fresh; legacy Board tables/data in `state.db` remain untouched
and unused. Do not import, merge, synchronize or clean them up without a new owner request.
All Board reads/writes, including the durable lane-entry queue, stay behind `IBoardStore` so a
future shared API-backed Board can implement that boundary. Jobs and terminal session history
remain local in `state.db`; `BoardStore` reads those references through a separate connection.

Only scheduler compositions opt in to Board event consumption (`consumeBoardEvents: true`).
Commit hooks and non-scheduling Automation hosts resolve a state-only `JobStore` and must work
without opening `board.db`, even if that file is newer, busy or damaged.

Reads that reach into `state.db` (`GetRunningAutomationsAsync`, `GetAutomationSessionIdsAsync`,
`DescribeLaneAutomationsAsync`, `GetAgentRunsAsync`, `FindSessionAuthorAsync`,
`FindSessionOutcomeAsync`, `GetSyncSessionOutcomesAsync`) still probe the schema first so a
stdio host can open a file that never held Automations or Sessions, but they go through the
store's `SqliteSchemaFeatures` memo (VIBE-27): a table or column seen once is never probed again
for the life of the instance, a missing one is asked for on every call. Use `_stateFeatures`
for any new state.db probe on a read path; keep `SqliteSchema.HasColumn` for migration SQL,
which must see the file as it is inside its transaction. Only the probe is remembered: the row
reads behind it stay live, so pruned `Sessions` history still falls back to the card-session
label (`BoardDatabaseIsolationTests` pins that) and a run's status is never served from memory.

Preserve scoped transactional lookups, persistent per-project numbering, dense ordering,
current-state card/attachment writes, and atomic commit snapshots. Add a new migration for schema
changes and update schema/compatibility tests. Use temporary databases for verification.

For future feature removals, stop the current code's reads/writes and retain unused tables,
columns, and data. Feature removal does not request destructive database cleanup; unused schema
is accepted technical debt. The owner accepted the already-shipped `board/8` retirement below
for that release only. It is not a precedent for dropping other retired storage.

A project's first board is named by `BoardStore.ChooseDefaultBoardName`: the custom project
name (latest `Sessions.ProjectDisplayName` for the working directory), otherwise the repository
folder name, never the retired `"Main"`. Existing boards are left as they are. Lane adoption
during schema reconcile uses the folder name, since that pass cannot read the custom name.
The custom-name lookup uses `OpenStateAsync`, retaining the shared SQLite configuration and
five-second timeout used by other state reads.

`board/6` adds revisioned board context, lane Automation settings and a durable debounced
lane-entry queue. SQL card-insert/lane-change triggers write only to these new tables, so older
card writers also participate. `DB/JobStore.Board.cs` reads settled entries through `IBoardStore`,
commits ordinary Automation run/action snapshots in `state.db`, then acknowledges the exact event
in `board.db`. The unique run TriggerKey deduplicates retries after a failed acknowledgement.
No transaction spans both files; a move/settings edit racing an already-read event can still
queue a run, an accepted best-effort tradeoff. Card/lane deletion cascades pending entries;
editing a lane's Automation cancels its pending entries without triggering existing cards.

`board/7` adds `BoardLaneAdditionalAutomations` and `BoardPendingAdditionalAutomations` for
multiple selections. The first job stays in the board/6 tables, preserving existing selections,
pending entries and old schedulers. Additional card triggers write only to the new queue. A
header-update trigger clears additional selections (and cascades their pending entries), so old
single-job settings saves replace the whole selection and advance the same revision. New saves
rebuild the additional selections in that transaction. Both queues use the same separate-commit
handoff. An old binary still using `state.db` cannot see or consume this database's queue.

`board/8` is breaking: it drops the three description-history tables, purges soft-deleted
attachments (content cascades), removes `DeletedUTC` and `WipLimit`, and stamps generation 3.
Existing databases upgrade automatically, with a backup and SQLite transaction coordination. Current descriptions,
files, comments, notes, sessions, commits and automation settings survive. Old migration SQL
remains in `BoardStore.LegacySchema.cs` only for upgrade sequencing, never runtime history.
`board/9` adds `BoardCards.Flagged` (default false). It is separate from blocked and is returned
in list/detail responses. Replacements accept the last write; append uses the transaction's
current text and validates the combined length before changing any fields.

`board/10` adds `BoardAdditionalCardSessions` for multiple cards per session within one project.
Keep primary rows in `BoardCardSessions`; do not convert/backfill them. Reads union both tables,
preferring the primary row for a duplicate pair from an older writer. The original link stays
the default, with oldest remaining attachment as fallback. Link validation runs inside the
write transaction; rename/unlink scopes both card and session, and deleting a primary card does
not cascade additional attachments. Old versions keep reading/writing the primary table.

`board/11` adds `BoardProjectKeys(ProjectPath, Prefix, CreatedUTC)`: the prefix a project's card
keys display (`BoardStore.ProjectKeys.cs`). The migration seeds `VB` for every project present in
`BoardCardSequences` or `BoardCards`, so upgrading rewrites no key. `CreateCardAsync` assigns a
missing prefix inside the same write transaction, before the number: `VB` when the project already
numbers cards (an older binary numbered them and shows `VB-n`), otherwise the first of the
folder-name candidates from `BoardKeys.DerivePrefixCandidates`, then random letters, that no other
project's row uses. Card reads `LEFT JOIN` the table and `COALESCE` to `VB`; the lane-automation
TriggerKey and link-candidate search build the key the same way. Key lookups match the number
plus either the project's prefix or the `VB` alias. A prefix is never updated or deleted by the
current code; there is no override, and old versions ignore the table entirely.

`board/12` adds `BoardJiraConnections` (one row per board) and `BoardJiraLinks` (one row per
mirrored Jira issue, unique on site and numeric issue id, cascading with the card). The API
token is not a column; it stays in `jira-tokens.json` beside `state.db`. No backfill. An older
binary ignores both tables. A link's `SiteId` is the connection id, and Jira issue ids are only
unique within one site, so changing a board's site gives the connection a new id (the upsert
updates `Id`); old-site cards keep their old links and are no longer updated. A pulled card and
its link are written in one transaction (`CreateJiraCardAsync`). Saving a connection checks the
board exists in the same statement.

`board/13` adds the `Boards_DeleteJiraConnection` trigger: deleting a board, from any binary,
deletes its Jira connection. It is a trigger rather than a foreign key because `board/12` already
shipped the table. No cleanup of existing rows; `GetJiraConnectionsAsync` skips a connection whose
board is gone, and the next scheduled pull prunes tokens that no longer have a connection.

`board/28` (VIBE-102, additive) adds six nullable `BoardJiraConnections` columns for a connection
made from a pasted Jira board link: `BoardLink`, `JiraBoardId`, `JiraBoardName`, `ColumnMap`
(JSON: the board's columns in order, each with a picked lane id or null for automatic),
`NarrowJql` and `SkipOldDone`. No backfill: a VB-40 row (site plus JQL) keeps pulling as before.
A board connection still writes the board filter's JQL into `Jql`, so an older binary, which reads
only `Jql`, keeps pulling the same issues. An older binary's upsert names only the board/12
columns, so it leaves these as the current version wrote them.

`board/29` (VIBE-102, additive) adds nullable `BoardJiraLinks.Mapping`: where the last pull
placed that issue (a board connection's lane target and story points field). A pull skips an
issue whose Jira `updated` is unchanged only while this also matches, so a changed lane pick,
points field or JQL-to-board switch re-applies existing cards without editing them in Jira. A
NULL row (written before board/29, or by a JQL connection without a points field) still skips
on a JQL connection and is re-applied once by a board connection. No backfill.

`board/14` (VB-51, additive) adds `BoardCards.CardKey` (the stored random key of a new card,
unique where not NULL), `BoardCards.DeletedUTC` (soft delete), `BoardComments.Changes` (the
Card Log's field diff JSON), `BoardComments.RemoteSeq` (sync state; `IX_BoardComments_Unsent`
covers the unsent rows) and `BoardSyncLinks` (one row per board published to viberails.ai,
cascading with the board). Card Log rows are written by `InsertLogEntryAsync`
(`BoardStore.CardLog.cs`) inside the same transaction as the card write. Every read filters
`DeletedUTC IS NULL`. No backfill; an older binary ignores the columns, reads `comment`/`note`
rows only, and shows soft-deleted cards.

`board/15` adds `BoardHistory` and additive board/lane-change triggers. `board/16` adds the
nullable `BoardSyncLinks.DestinationKey` fingerprint. Neither rewrites historical rows. Human
History reads are explicit and paged; normal card detail does not query change history. See
[SYNC.md](../../VibeRails/Services/Board/SYNC.md) for conflict ordering and upload boundaries.

`board/17` adds `BoardSyncRejectedFields` for per-field protection of rejected sync entries
(`RemoteSeq = -1`); only acknowledgement of a later local correction releases those fields.
Rejected rows and their content stay in the Card Log. Rejected creations keep dependent edits
local while other cards sync. See the rejection policy in `SYNC.md` for repair and status behavior.
It also creates the partial index `IX_BoardComments_RemoteSeq` (`RemoteSeq > 0`).

`board/18` drops and recreates the five board/15 history triggers from the corrected constant
(`BoardStore.History.cs`): the lane triggers now `COALESCE(BoardId, '')`, because a lane's
`BoardId` is NULL until adoption while `BoardHistory.BoardId` is NOT NULL, and a trigger must
never make an older binary's lane write fail. No row is read, rewritten or deleted.

`board/19` (VB-63, additive) adds `BoardContextSamples`: one row per card launch with the session
id, intent, CLI, selection, the estimated tokens (total, prompt, card read), characters and the
breakdown JSON the host measured (`BoardStore.ContextSamples.cs`). `RecordContextSampleAsync`
inserts the row and, in the same transaction, a Card Log `change` entry by the system author whose
`Changes` is `{"context":{"to":{tokens, chars, prompt, cardRead, intent, cli, breakdown}}}` with a
readable "Agent launch context ≈ N tokens" body, so the sample syncs and shows in History. The
card is not touched or promoted. `GetLatestContextSampleAsync` reads the newest row for the card
editor. No backfill; an older binary ignores the table and the `context` field.

Session commit linking reads this membership inside the commit-write transaction and writes the
snapshot to the target and all same-project attachments atomically. One failed write rolls back
every new link. Repeats leave existing metadata/snapshots unchanged and fill missing links.

`board/20` (VB-69, additive) adds nullable `Boards.DisplayPrefix` and `BoardCards.DisplayId`,
a case-insensitive project/display-ID unique index, and `BoardDisplaySequences`. It backfills
DisplayId to the existing immutable/legacy key as requested. Allocation, local collision rename,
Card Log corrections, and draft-link creation run inside the card write transaction. NULL values
from older writers continue to display their immutable key. Nothing changes CardKey or row Id.
A label may never spell another card's key or a short form of it (`ShortKeyMatchSql`, shared with
lookup): the owner check reserves them, a manual collision is a 409 and an incoming label that
spells a key goes to a new label. A label prefix that keys also answer to (the project's key
prefix, or `VB`) numbers from the card high-water mark, and local key minting skips a number
whose short form a label already holds. A pulled web card's number is adopted only within
`MaxAdoptedCardNumberGap` of the high-water mark; an exhausted sequence is a validation error.

`board/21` (additive) adds `BoardSyncSkippedEntries`: pulled sync entries the desktop can never
apply, recorded so the cursor moves past them and the status view counts them. It cascades with
the board and is cleared when a re-publish lands on a different remote board. No backfill.

`board/22` (additive) adds the nullable `BoardSyncSkippedEntries.Version`: the desktop version that
last tried the entry. `RetrySkippedSyncEntriesAsync` moves the link cursor back to just before the
earliest entry an earlier version tried (NULL counts as earlier) and records the current version on
those rows in the same transaction; the stamped Card Log writers delete an entry's row in the
transaction that applies it. No backfill; older binaries never name the column.

`board/23` adds `BoardSyncLinks.ActivitySchema` (default 0) and `ActivityAfter`. Old publications
keep these columns for compatibility; VIBE-13 upgrades publications automatically when an API
key is configured. `GetBoardsForSyncAsync` enumerates board metadata for the root scheduler;
activity and card reads still use each board's project scope.
The cursor makes bounded card refresh fair across root processes/restarts under the sync lock.
Activity reads remain behind `IBoardStore`, with SQL row/actual-content bounds before loading
payloads. Existing stored snapshots and unlimited local attachment uploads are unchanged.
`GetSyncSessionOutcomesAsync` accepts at most 200 IDs obtained from scoped linked-session metadata.
It uses one state-database connection and a joined query after one batch of schema checks; summaries
are bounded before materialization. The single-session reader still serves ordinary card context.


## Dedicated Jira boards (VIBE-103)

`board/30` adds nullable `BoardJiraConnections.DedicatedBoard` with no schema-time backfill.
Connect and writing pulls call `IBoardStore.EnsureDedicatedJiraBoardAsync`: one transaction creates
a separate local board, clones lanes without Automations, remaps lane choices, moves only that
connection's live imports still on the source board using the ordinary log-transfer path, and
moves the connection. IDs, comments, attachments, sessions and commits survive; local cards stay.
Repeated calls use the existing dedicated board. Old writers preserve the new column. Card reads
project the Jira issue key from retained `BoardJiraLinks`; no card-origin backfill is required.
