# Board sync integration tests

These tests couple the desktop SQLite store, sync service and HTTP client to the Front
Board API controller and sync service. An in-process HTTP handler passes the actual JSON
requests and responses between them. The handler supplies a fixed test identity; these tests
do not replace the repositories' authentication, middleware or SQL Server tests.

Every test uses a disposable SQLite file and EF InMemory database. Neither application starts,
and no network request, production upload, configured account or normal application database is used.

From the desktop repository root, with `VibeRails-Front` checked out beside it:

```powershell
dotnet test Tests/headless/BoardSync.Integration/BoardSync.Integration.csproj -p:OutputPath=bin/BoardSyncIntegration/
```

For another checkout location, add `-p:FrontRepoRoot=C:/path/to/VibeRails-Front`.
This project is intentionally outside the standard `Tests.csproj` suite because it requires
both repositories. It checks the JSON contract, bidirectional edits and comments, notes,
arrival ordering after offline edits, lost acknowledgements, stable card identities,
local lane Automation events, and upload boundaries.
