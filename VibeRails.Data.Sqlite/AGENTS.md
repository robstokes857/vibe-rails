# Working in the SQLite provider

The root [database change policy](../AGENTS.md#database-change-policy) applies to this entire
project, including the migration runner and stores outside `DB/` and `Board/`.

- Opening a new version must just work. Required schema setup runs automatically. Never add
  a user-run migration command, opt-in gate, manual backup step, or process-count gate.
- Prefer additive schema changes. Different VibeRails versions can run on the same machine;
  preserve their read/write compatibility where possible.
- Debug/F5/application runs use the normal `~/.vibe_rails/state.db`. Do not introduce a separate
  development database, build/branch-specific paths, or a flag permitting access to the normal
  database. Fix code that is unsafe for the running database instead of redirecting it elsewhere.
- Board-owned state uses `~/.vibe_rails/board.db` in every configuration. The old Board tables
  in `state.db` are retained but unused: no import, cleanup or synchronization. Keep Board access
  behind `IBoardStore` so future shared API storage can replace this implementation.
- Removing a feature means retiring its current code paths. Leave unused tables and columns,
  and their stored data, in place. Feature removal alone is not permission to drop or purge them.
- Unused schema is accepted technical debt. Do not add cleanup, historical-data conversion,
  or backfill work unless the owner explicitly asks for it.
- The owner accepted the existing `board/8` cleanup for its release. Do not use that historical
  exception as a pattern for future removals or rewrite shipped migrations as incidental cleanup.

Read [DB/AGENTS.md](DB/AGENTS.md) for transaction, backup, generation, schema snapshot, and
storage contracts. For Board persistence also read [Board/AGENTS.md](Board/AGENTS.md).
Automated tests use disposable databases to keep fixtures and assertions isolated. This is test
infrastructure, not permission to change the database used when running/debugging the app.
