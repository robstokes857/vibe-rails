# Board persistence

Read the cross-layer [Board contributor guide](../../VibeRails/Services/Board/AGENTS.md) and
[architecture/data model](../../VibeRails/Services/Board/ARCHITECTURE.md), plus the
[database migration policy](../DB/AGENTS.md), before changing this component.

This directory owns `IBoardStore`'s SQLite implementation in `~/.vibe_rails/board.db`, including
component migrations `board/1`–`board/9`. The historical `VibeRails.Services.Board` namespace
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

Preserve scoped transactional lookups, persistent per-project numbering, dense ordering,
current-state card/attachment writes, and atomic commit snapshots. Add a new migration for schema
changes and update schema/compatibility tests. Use temporary databases for verification.

For future feature removals, stop the current code's reads/writes and retain unused tables,
columns, and data. Feature removal does not request destructive database cleanup; unused schema
is accepted technical debt. The owner accepted the already-shipped `board/8` retirement below
for that release only. It is not a precedent for dropping other retired storage.

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
