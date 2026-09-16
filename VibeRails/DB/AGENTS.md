# Database code moved

SQLite implementations, migration instructions, and the schema reference are now in
[VibeRails.Data.Sqlite/DB/AGENTS.md](../../VibeRails.Data.Sqlite/DB/AGENTS.md).
Storage contracts and records live in `VibeRails.Data.Abstractions`.
Application services should depend on those contracts; hosts select the SQLite provider through
`SqliteStorage` registration methods.
