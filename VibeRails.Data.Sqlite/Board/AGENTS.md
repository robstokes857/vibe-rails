# Board persistence

Read the cross-layer [Board contributor guide](../../VibeRails/Services/Board/AGENTS.md) and
[architecture/data model](../../VibeRails/Services/Board/ARCHITECTURE.md), plus the
[database migration policy](../DB/AGENTS.md), before changing this component.

This directory owns `IBoardStore`'s SQLite implementation in shared `state.db`, including
component migrations `board/1`–`board/7`. The historical `VibeRails.Services.Board` namespace
does not move this code back into the application project. Keep DTO/contracts in
`VibeRails.Data.Abstractions/Board`; keep Git, live terminal state and UI policy in the host.

Preserve scoped transactional lookups, persistent per-project numbering, dense ordering,
immutable revisions/manifests, and atomic commit snapshots. Add a new migration for schema
changes and update schema/compatibility tests. Use temporary databases for verification.

`board/6` adds revisioned board context, lane Automation settings and a durable debounced
lane-entry queue. SQL card-insert/lane-change triggers write only to these new tables, so older
card writers also participate. `DB/JobStore.Board.cs` consumes settled entries and snapshots
ordinary Automation runs in one transaction. Card/lane deletion cascades pending entries;
editing a lane's Automation cancels its pending entries without triggering existing cards.

`board/7` adds `BoardLaneAdditionalAutomations` and `BoardPendingAdditionalAutomations` for
multiple selections. The first job stays in the board/6 tables, preserving existing selections,
pending entries and old schedulers. Additional card triggers write only to the new queue. A
header-update trigger clears additional selections (and cascades their pending entries), so old
single-job settings saves replace the whole selection and advance the same revision. New saves
rebuild the additional selections in that transaction. Each queue is consumed atomically with
its normal Job snapshots; an older scheduler consuming the first queue cannot lose extra jobs.
