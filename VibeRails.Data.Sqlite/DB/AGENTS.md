# SQLite Data Layer — Business Logic & Technical Reference

Implementation is in `VibeRails.Data.Sqlite`; contracts and shared records are in
`VibeRails.Data.Abstractions`. Namespaces remain stable for callers. Host composition uses
`SqliteStorage`; application code does not construct connections or concrete stores.

Schema snippets below document table shapes. Historical migration notes describe the adoption
logic, not work repeated on every launch. Add future schema changes as a new version under the
appropriate component; never edit an already shipped version to rely on it running again.

Adoption checks for already-present columns and indexes explicitly. Other SQL failures now stop
initialization and roll back that migration, leaving its completion record absent so the next
attempt can retry. Earlier versions logged and skipped those failures. For example, historical
case-variant environment names for the same provider prevent the required case-insensitive unique
index from being created; preserve both rows and correct the conflicting names before retrying.
Migration does not silently rename or delete user environments.

Pending migrations wait up to 60 seconds for database locks so startup can coexist with an older
VibeRails process finishing a write. Their log entries identify the component, version, and file.
A lock timeout reports which migration is pending and leaves it eligible for the next initialization;
it never records completion. The connection returns to its previous timeout afterward
(ordinary store operations retain the five-second policy).

### Automatic database upgrades (updated 2026-09-20)

**Installing and opening a new version must be sufficient to use it. Database upgrades are
transparent to the user. Never require a migration command, opt-in flag, acknowledgement,
manual backup, or closing other VibeRails windows as a normal upgrade step.** This supersedes
the manual-upgrade policy introduced on 2026-09-16. There is no user-facing migration command.

Debug builds and F5 use the same normal application state database as other launches:
`~/.vibe_rails/state.db`. The former separate development database was removed; never restore
`.vibe_rails_dev`, build/branch-specific data paths, or a Debug permission flag as a safety
workaround. Code must be safe for the running database. If it is not, fix the implementation
instead of redirecting the app to an alternate database or copy. Disposable databases belong
in automated tests, not in the application's debug/runtime path policy.

**Prefer additive schema changes and preserve compatibility with older versions where possible:
different versions can run on the same machine.** Removing a feature normally means stopping
reads/writes to its old tables, leaving those tables and their data in place. Do not drop tables
or columns or delete stored data merely because the UI or service no longer uses them.
Feature removal is not a request for destructive schema cleanup. Unused schema is accepted
technical debt; leave it alone unless the owner explicitly requests that cleanup.
Unless explicitly requested, do not plan or add historical-data conversion or backfill work.
Make schema setup needed by the requested change automatic. Do not expand a feature or fix into
a migration project or a manual upgrade procedure.

Every `SqliteMigrationRunner.Apply` call declares a `MigrationKind`:

- **Additive** — older binaries keep working unchanged (new table, nullable column, index, trigger
  that writes only to new tables, virtual table over a *new* content table). Runs at startup.
- **Breaking** — anything an older binary could misuse afterwards (drop, rename, re-pointed
  virtual-table content, changed trigger or constraint, row deletion). Runs automatically too.
  Existing databases receive a consistent SQLite backup under `backups/` beside the file before
  the schema changes. Fresh databases need no backup. See `SchemaUpgradePolicy`.

The runner acquires SQLite's writer transaction, rechecks the completion receipt, and then
backs up through a separate read connection before executing migration SQL. Competing starts
wait and skip completed work; open processes alone never block an upgrade. Schema changes and
the completion receipt commit together. A failed backup prevents the migration; failed SQL
rolls back and can retry on the next initialization. Retry backups never overwrite older copies.
Do not replace transaction coordination with process enumeration or user intervention.

Board migration `board/8` retires description history, WIP limits and removed-file retention
(state generation 3); `board/9` adds the current attention flag. Both apply automatically.
The owner accepted this already-shipped cleanup as an exception; it is not the template for
future feature removals. Retire future code paths while retaining unused schema by default.

Each database file carries a **generation** in `PRAGMA user_version`, bumped only by breaking
changes (`StateDatabaseSchema.Generation`, `LlmExchangeLogStore.Generation`,
`BertVectorDatabase.Generation`). Every schema entry point first calls
`RequireGenerationAtMost`: a build refuses a file newer than it understands instead of writing to
it. The shipped 1.10.10 binary predates the gate and reads `user_version < 1` as "rebuild the FTS
table", so generations stay at or above 1.

`SchemaMigrations.AppliedBy` records which binary (name, version, configuration, pid, host)
applied each step. `SqliteStorage.EnsureAllSchemas` brings every store current in one call for
`Tests/DB/SchemaSnapshotTests.cs`. That test pins the schema of
every table to `docs/schema/*.sql`; a table change is a diff there and needs the owner's sign-off.
Use the existing compatibility fixtures when changing a schema or write contract so concurrently
running versions keep working where possible. Prefer retaining unused tables and columns over
forcing an incompatible cleanup. Never require database administration to use an update.

Input recording and Git capture are orchestrated by `VibeRails/Services/UserInputRecordingService.cs`.
Session export schema v2 includes a nested proxy-exchange archive. Retention uses the existing
export acknowledgement, preserving open/unexported sessions and unattributed proxy records.


This document describes the database layer: Environments, Sandboxes, AgentMetadata, Sessions, UserInputs, and supporting tables.

> **Contents:**
> [Database Overview](#database-overview) | [Environments](#environments) | [Sandboxes](#sandboxes) | [AgentMetadata](#agentmetadata) | [Sessions](#sessions) | [User Input Tracking](#user-input-tracking) | [Additional Tables](#additional-tables) | [Entity Relationships](#entity-relationships) | [Repository Patterns](#repository-patterns)

---

## Database Overview

| Property | Value |
|---|---|
| **Engine** | SQLite with WAL mode and foreign keys enabled |
| **Connection** | Paths passed to `SqliteStorage`; file connections use private cache. `SqliteConnectionFactory.Configure` sets `busy_timeout=5000`, `journal_size_limit=64MB` and `synchronous=NORMAL` on every open. NORMAL is deliberate: it removes the per-commit fsync so the single writer lock is held for microseconds; a crash of the process loses nothing, an OS crash can lose the last few commits. Several `vb.exe` processes (roots, tab children, `vb mcp` hosts) write the same file, and on 2026-09-16 per-chunk fsync'd terminal-output INSERTs saturated that lock and starved every other writer. |
| **Terminal output** | `SessionOutputWriter` batches queued PTY chunks into one `IRepository.PersistTerminalOutputAsync` transaction per drain (both `SessionLogs` and `TerminalSessionLogs` rows), retrying a transient lock a few times and then dropping the batch. Never reintroduce one autocommit INSERT per chunk. |
| **Initialization** | `StateDatabaseSchema` and store-specific components run pending migrations through `SqliteMigrationRunner`; completed `(Component, Version)` entries persist in `SchemaMigrations` |
| **Timestamps** | All `DateTime` values stored as ISO 8601 round-trip strings (`"O"` format), parsed with `DateTimeStyles.RoundtripKind` |

---

## Environments

### Business Logic

An **Environment** is a reusable configuration for a specific LLM CLI (Claude, Codex, Antigravity, Copilot, or OpenCode). Environments are **global** — they are not tied to any project.

| Rule | Details |
|---|---|
| **Unique identity** | Identified by the pair `(CustomName, LLM)`. You can have "MyEnv" for Claude AND "MyEnv" for Codex, but NOT two "MyEnv" entries for the same LLM. |
| **No default environments saved** | Launching a CLI without an explicit environment name creates no row. The system uses `LLM_Environment.DefaultPrompt` directly. Only user-created named environments are persisted. |
| **Custom environments** | Created via the UI or `--env` flag. They store `CustomArgs` (CLI flags prepended to launch args) and `CustomPrompt` (system prompt override; falls back to `DefaultPrompt` if empty). |
| **Config directory** | `Path` stores the filesystem location for the environment's LLM-specific config files. Set by `LlmCliEnvironmentService`, not the DB layer. |
| **Recency tracking** | `LastUsedUTC` is bumped on every access or launch. Dashboard orders by `LastUsedUTC DESC`. |
| **Querying** | `GetCustomEnvironmentsAsync` filters out default environments and legacy bare provider rows whose `Path`, `CustomArgs`, and `CustomPrompt` are all empty. A user-created provider-named environment has a populated `Path` and remains visible. `GetAllEnvironmentsAsync` returns everything. |
| **Deletion guard** | Default environments cannot be deleted, and environments referenced by Automations cannot be deleted — both enforced in the Routes layer (`EnvironmentRoutes.cs`), not the DB layer. |

### Technical Details

**Schema:**
```sql
CREATE TABLE IF NOT EXISTS Environments (
    Id           INTEGER PRIMARY KEY AUTOINCREMENT,
    CustomName   TEXT    NOT NULL,
    LLM          INTEGER NOT NULL,
    Path         TEXT    NOT NULL DEFAULT '',
    CustomArgs   TEXT    NOT NULL DEFAULT '',
    CustomPrompt TEXT    NOT NULL DEFAULT '',
    CreatedUTC   TEXT    NOT NULL,
    LastUsedUTC  TEXT    NOT NULL,
    Hidden       INTEGER NOT NULL DEFAULT 0,
    AutomationWorker INTEGER NOT NULL DEFAULT 0,
    WorkspaceMode INTEGER NOT NULL DEFAULT 0,
    ProjectPath  TEXT,
    UNIQUE(CustomName, LLM)
);
```

**Index:** `idx_environments_name_llm` on `(CustomName, LLM)`

A case-insensitive unique index `idx_environments_name_nocase_llm` on
`(CustomName COLLATE NOCASE, LLM)` is also created via migration — the env name maps to a
case-insensitive directory (`envs/{name}/{llm}`), so "Work" and "work" for the same LLM would
share a credential directory.

**Model:** `LLM_Environment` class in `VibeRails.Data.Abstractions/DTOs/LLM_Environment.cs`

`Hidden` (0/1) hides the environment from new-launch picker choices without deleting it. It is a
UI-visibility flag, not a CLI option — it never enters `CustomArgs`. Existing saved references are
still rendered with a hidden label, and provider creation/history filters do not inherit this
visibility. Added via the `MigrateEnvironmentsAddHidden` ALTER TABLE migration.

`AutomationWorker` (0/1) marks an environment created from the Automation editor to back an
automation ("Worker"). Set at creation only (`CreateEnvironmentRequest`; no update-path field).
Workers are excluded from the LLM-picker preferences catalog entirely
(`LlmPickerPreferenceService.IsSupportedCustomEnvironment`) — regardless of `Hidden` — so they
never appear in launch pickers or the "Customize LLM list" modal; the automation editor's Worker
picker lists them from `/api/v1/environments` instead. Added via the
`MigrateEnvironmentsAddAutomationWorker` ALTER TABLE migration. Environments that predate the flag
are deliberately NOT backfilled.

`WorkspaceMode` (0/1/2) is where the environment's CLI runs — see `EnvironmentWorkspaceMode`:

| Value | Name | Behavior |
|:---:|---|---|
| 0 | `Project` | Runs in the project directory. The original behavior and the default. |
| 1 | `Persistent` | One git clone, created on first launch and reused by every launch after. |
| 2 | `PerRun` | A fresh clone per launch, pristine (no dirty-file copy), older ones pruned. |

Modes 1 and 2 are the **same mechanism at different retention** — both are backed by a row in
`Sandboxes` whose `EnvironmentId` points back here, and both reuse `SandboxService` wholesale for
the clone, path containment, and Windows read-only handling. `RunWorkspaceService` owns only the
questions SandboxService has no opinion on: which name (`WorkspaceNameSlug` — env names allow
spaces, sandbox names and git branches do not), whether to reuse, and what to prune. Added via
`MigrateEnvironmentsAddWorkspaceMode`.

`ProjectPath` scopes an environment to one project. **NULL means "predates project scoping" and
stays visible everywhere** — there is deliberately no backfill, so an environment already in use
never vanishes from a project. Every environment created from now on carries the project it was
created in. Filtering happens in the route/service layer (`ProjectPathComparer.IsVisibleIn`), not
in SQL, so `SelectAllEnvironments` and `SelectCustomEnvironments` stay project-agnostic. Added via
`MigrateEnvironmentsAddProjectPath`.

Workspace lifecycle rules worth not regressing:

- The clone is **never** made at environment-create time — only on first launch, where there is a
  progress surface and a failure can be reported against that launch.
- Changing `WorkspaceMode` **detaches** the old workspace (`EnvironmentId → NULL`) but never
  deletes it. The clone may hold uncommitted work; changing a dropdown is not consent to destroy it.
- Deleting an environment **releases** its workspaces: orphan the rows first, then best-effort
  delete each directory. A clone still locked by a running CLI simply survives as a standalone
  sandbox instead of failing the delete.
- Retention never deletes a workspace with an open session under it, one younger than
  `RunWorkspaceService.MinimumPruneAge`, or one whose in-use check failed. The cap is a
  disk-space policy, not a licence to destroy an in-flight run's working tree.
- `DELETE /api/v1/sandboxes/{id}` refuses (409) a sandbox that has an owner. A workspace is
  released by re-moding or deleting its environment, never by deleting it out from under one.

Scoping is enforced on every path that resolves an environment — list, read/update/delete by
name, launch, and automation validation — because all the underlying lookups are global. See
`ProjectPathComparer.IsVisibleIn`; the by-name routes answer 404 rather than 403 so a name's
existence in another project is not itself disclosed.

**LLM enum** (stored as integer):

| Value | Name | Notes |
|:---:|---|---|
| 0 | NotSet | |
| 1 | Codex | |
| 2 | Claude | |
| 3 | Antigravity | Binary is `agy` (mapped in CommandService) |
| 4 | Copilot | Launch-flag-only (no config-dir env var) |
| 5 | Shell | Plain shell terminal — no AI agent; spawns a real OS shell |
| 6 | OpenCode | Binary is `opencode`; config isolation via `XDG_CONFIG_HOME` |
| 7 | Glm52 | Pseudo-CLI: OpenCode launched with a pinned `--model` flag. `LlmParser` special-cases the string `"glm-5.2"` |
| 8 | Grok46 | Native Grok Build CLI. Binary is `grok` (not `grok46`). `LlmParser` special-cases the string `"grok-4.6"`. Token Saver reuses `/llm/xai` → `api.x.ai` (same OpenCode proxy flag). Do not set `GROK_HOME`. |
| 9 | Glm53 | Pseudo-CLI: OpenCode launched with `--model=zai-coding-plan/glm-5.3` (glm-5.3 exists only under the `zai-coding-plan` provider). `LlmParser` special-cases the string `"glm-5.3"`. Traffic goes direct to Z.AI — the OpenCode proxy does not remap `zai-coding-plan`. |
| 10 | DeepSeekV4Pro | Pseudo-CLI: OpenCode launched with `--model=deepseek/deepseek-v4-pro`. `LlmParser` special-cases the string `"deepseek-v4-pro"`. Traffic goes direct to DeepSeek — the OpenCode proxy does not remap the `deepseek` provider. |
| 11 | KimiK3 | Pseudo-CLI: OpenCode launched with `--model=moonshotai/kimi-k3`. `LlmParser` special-cases the string `"kimi-k3"`. Traffic goes direct to Moonshot AI — the OpenCode proxy does not remap the `moonshotai` provider. |

**Key operations:**

| Method | Behavior |
|---|---|
| `GetEnvironmentByNameAndLlmAsync(name, llm)` | Lookup by the unique `(CustomName, LLM)` pair |
| `GetOrCreateEnvironmentAsync(name, llm)` | Lookup, then create if missing. Bumps `LastUsedUTC` if found. |
| `SaveEnvironmentAsync` | `INSERT ... RETURNING Id` |
| `UpdateEnvironmentAsync` | Full field update by `Id` (CustomName, LLM, Path, CustomArgs, CustomPrompt, LastUsedUTC, Hidden, AutomationWorker, WorkspaceMode, ProjectPath) |
| `TouchEnvironmentLastUsedAsync(id)` | Recency-only bookkeeping — stamps `LastUsedUTC` without touching any other column (launches must never use `UpdateEnvironmentAsync` for this) |
| `DeleteEnvironmentAsync` | Delete by `Id` — **no deletion guard at DB layer**. The "cannot delete Default" and "not referenced by Automations" rules are in `EnvironmentRoutes.cs`. |

---

## EnvironmentSteps

### Business Logic

A **Step** is one shell command attached to an Environment, run in its own native terminal window
*before* the LLM launches or *after* its PTY exits — or, for Phase 2 (**Manual**, "only when
referenced"), never on its own: it executes hidden-and-captured when the environment's Initial
Message references it via `{{step:<id>}}`, with its output substituted into the prompt
(`PromptPlaceholderService`). Steps are the user-editable counterpart to
`PreparedTerminalSession.SetupCommands`, which is system-generated (MCP registration), `;`-joined
so failures are ignored, and not exposed anywhere.

| Rule | Details |
|---|---|
| **Ordering** | `ORDER BY Phase, Position`. `Position` is 0-based and unique within `(EnvironmentId, Phase)` — each phase counts from zero independently. |
| **Position is server-assigned** | Clients send an array; `ReplaceStepsAsync` stamps `Position` from array order. A client never sends a position, so it can never disagree with what the editor showed. |
| **One at a time, blocking** | Steps run sequentially in their own OS terminal windows, each waited on before the next fires. Deliberately outside the PTY pipeline — the user watches them in a real window. |
| **A failed pre-step aborts the launch** | No per-step override. A step exists to make a launch's preconditions true, so a launch that proceeds without them is worse than one that does not happen. |
| **Post-exit steps are advisory** | Nothing is left to abort by then; the failure is reported and the step's window stays open with the error. |
| **What "after it exits" means** | For a Worker or Job it is "the agent finished"; for a browser tab it is "the tab closed". Stated in the editor in those words. |
| **Manual steps never lifecycle-run** | Lifecycle runs query by phase (`GetEnabledStepsAsync`), so Phase 2 rows are excluded automatically. They execute only via a `{{step:<id>}}` prompt reference, best-effort: a deleted/failing/timed-out step substitutes explanatory text and the launch continues. |
| **Ids are client GUIDs** | `Id` is a GUID string generated by the editor at step creation and round-tripped through every save. Stability comes from the round-trip, not the row surviving — `ReplaceStepsAsync` still deletes and re-inserts. `{{step:<id>}}` references depend on this. |
| **Cascade** | Deleting an Environment deletes its steps (`ON DELETE CASCADE`). |

### Technical Details

**Schema:**
```sql
CREATE TABLE IF NOT EXISTS EnvironmentSteps (
    Id             TEXT    PRIMARY KEY,            -- client-generated GUID string
    EnvironmentId  INTEGER NOT NULL,
    Phase          INTEGER NOT NULL,               -- 0 = before launch, 1 = after the PTY exits, 2 = only when referenced
    Position       INTEGER NOT NULL,               -- 0-based, unique within (EnvironmentId, Phase)
    Name           TEXT    NOT NULL DEFAULT '',
    Command        TEXT    NOT NULL,
    StartMinimized INTEGER NOT NULL DEFAULT 0,
    TimeoutSeconds INTEGER NOT NULL DEFAULT 600,
    Enabled        INTEGER NOT NULL DEFAULT 1,
    CreatedUTC     TEXT    NOT NULL,
    UpdatedUTC     TEXT    NOT NULL,
    FOREIGN KEY (EnvironmentId) REFERENCES Environments(Id) ON DELETE CASCADE
);
```

**Index:** `idx_environment_steps_env` on `(EnvironmentId, Phase, Position)`

**Model:** `EnvironmentStep` class + `EnvironmentStepPhase` enum in `DTOs/EnvironmentStep.cs`

Registered in **`SqlStrings.InitStatements`, not `MigrationStatements`** — a brand-new
`CREATE TABLE IF NOT EXISTS` is correct for fresh and legacy databases alike, and
`MigrationStatements` swallows benign failures, which is exactly how a silently missing table
would happen. Only later `ALTER TABLE`s on this table belong in the migration list.

The table briefly shipped with `Id INTEGER PRIMARY KEY AUTOINCREMENT`. SQLite cannot ALTER a PK
type, so `StateDatabaseSchema` adoption (runs *before* the init loop) drops an
old-shape table — deliberately without porting rows; the int-keyed build had no adopters — and
lets `InitStatements` recreate it TEXT-keyed. The guard keys on the `Id` column's declared type,
so it is a no-op forever after.

`ON DELETE CASCADE` rather than the FK-less orphan pattern `Sandboxes` uses: a step is *part of*
its environment and owns no filesystem resource, so there is no multi-GB directory delete to keep
out of the delete transaction. Foreign keys are enabled by `SqliteConnectionFactory`, and
`SQLitePCLRaw.bundle_e_sqlite3` compiles it on by default, so it holds on every connection.

`StartMinimized` (0/1) starts the step's window minimized so it does not steal focus. Honoured on
Windows; the macOS and Linux terminal launchers have no equivalent and ignore it.

`TimeoutSeconds` is clamped to 1..3600 on write (`EnvironmentStepRoutes.ClampTimeout`) **and**
again on read by `EnvironmentStepRunner`, so a row written by an older build or edited by hand
cannot produce a zero timeout that fails every step instantly.

**Key operations:**

| Method | Behavior |
|---|---|
| `GetStepsForEnvironmentAsync(id)` | All steps for one environment, `Phase, Position` order |
| `GetStepsForEnvironmentsAsync(ids)` | Bulk read indexed by owner — the list endpoint renders a step count per row and must not pay an N+1. Parameterized `IN (...)`, never interpolated. |
| `GetEnabledStepsAsync(id, phase)` | Exactly what `IEnvironmentStepRunner` executes: enabled only, `Position` order. The runner never re-filters or re-sorts. |
| `HasEnabledStepsAsync(id, phase)` | Cheap probe. The launch path uses it to decide whether to remember a session's post-exit context, so it runs on every session created. |
| `GetStepByIdAsync(envId, stepId)` | One step by GUID, scoped to its environment — a `{{step:<id>}}` reference must not reach another environment's commands. Null = deleted (the prompt substitutes the deleted-step text). |
| `ReplaceStepsAsync(id, steps)` | `BEGIN IMMEDIATE`, delete-all, re-INSERT in array order stamping `Position` and keeping each step's client GUID (a blank/invalid one gets a fresh GUID). Same shape as `JobStore.ReplaceTriggersAsync`. |

---

## Sandboxes

### Business Logic

A **Sandbox** is an isolated git clone of a project where users can run parallel AI workflows without affecting the main codebase. Sandboxes are stored **globally** at `~/.vibe_rails/sandboxes/{name}` but scoped to projects via `ProjectPath`.

| Rule | Details |
|---|---|
| **Unique identity** | Identified by the pair `(Name, ProjectPath)`. The same sandbox name can exist for different projects. |
| **Global storage** | Sandbox directories live at `~/.vibe_rails/sandboxes/{name}`, NOT inside the project directory. |
| **Project scoping** | `ProjectPath` links a sandbox to its source project. API queries filter by current project path. |
| **Shallow clone** | Created via `git clone --depth 1 --branch {branch} --single-branch` for fast creation. |
| **Dirty files** | Copied into the sandbox after cloning — but only when `SandboxCreateOptions.CopyDirtyFiles` is true. A `PerRun` environment workspace sets it false: "fresh" means the committed tree and nothing else. |
| **Deletion** | Deleting a sandbox removes both the directory (`Directory.Delete(recursive: true)`) and the DB record. |
| **Name validation** | Names must match `^[a-zA-Z0-9_-]+$` (alphanumeric, hyphens, underscores, no spaces). |
| **Ownership** | `EnvironmentId` NULL = standalone sandbox (the pre-fold kind, created from the Sandboxes card, launchable with any CLI). NOT NULL = an environment's workspace, driven by that environment instead. |

### Technical Details

**Schema:**
```sql
CREATE TABLE IF NOT EXISTS Sandboxes (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    Name        TEXT    NOT NULL,
    Path        TEXT    NOT NULL,
    ProjectPath TEXT    NOT NULL,
    Branch      TEXT    NOT NULL DEFAULT '',
    CommitHash  TEXT,
    RemoteUrl   TEXT,
    SourceBranch TEXT,
    CreatedUTC  TEXT    NOT NULL,
    EnvironmentId INTEGER,
    UNIQUE(Name, ProjectPath)
);
```

`RemoteUrl`, `SourceBranch`, and `EnvironmentId` are added via `ALTER TABLE` migrations (safe to
re-run).

`EnvironmentId` deliberately has **no foreign key**: deleting an environment orphans its workspace
(sets this back to NULL) rather than cascading a multi-GB directory delete into the delete
transaction. Orphaning is also what makes the UI coherent — the Sandboxes card renders exactly the
rows with a NULL owner, so a released workspace simply reappears there.

**Index:** `idx_sandboxes_project` on `(ProjectPath)`, `idx_sandboxes_environment` on `(EnvironmentId)`

**Model:** `Sandbox` class in `DTOs/Sandbox.cs`

**Key operations:**

| Method | Behavior |
|---|---|
| `SaveSandboxAsync(sandbox)` | `INSERT ... RETURNING Id` |
| `GetSandboxesByProjectAsync(projectPath)` | All sandboxes for a project, ordered by `CreatedUTC DESC` |
| `GetSandboxByIdAsync(id)` | Lookup by primary key |
| `GetSandboxByNameAndProjectAsync(name, projectPath)` | Lookup by unique `(Name, ProjectPath)` pair |
| `GetSandboxesByEnvironmentIdAsync(environmentId)` | Workspaces owned by one environment, newest first |
| `OrphanSandboxesForEnvironmentAsync(environmentId)` | Releases an environment's workspaces to standalone (`EnvironmentId = NULL`) |
| `DeleteSandboxAsync(id)` | Delete by `Id` — directory cleanup handled by `SandboxService`, not the DB layer |

**API endpoints:**

| Endpoint | Method | Behavior |
|---|---|---|
| `/api/v1/sandboxes` | GET | List sandboxes for current project |
| `/api/v1/sandboxes` | POST | Create sandbox (body: `{ name }`) |
| `/api/v1/sandboxes/{id}` | DELETE | Delete sandbox + directory |
| `/api/v1/sandboxes/{id}/launch/{cli\|shell\|vscode}` | POST | Launch CLI, shell, or VS Code in sandbox directory |
| `/api/v1/sandboxes/{id}/diff` | GET | Diff sandbox against source |
| `/api/v1/sandboxes/{id}/push` · `/api/v1/sandboxes/{id}/merge` | POST | Push/merge sandbox changes |

---

## AgentMetadata

### Business Logic

**AgentMetadata** stores user-assigned display names for rule files (`vc.rules.md` files found in repositories).

| Rule | Details |
|---|---|
| **Keyed by path** | Each rule file is identified by its absolute filesystem path. |
| **Upsert behavior** | Setting a custom name for a path that already has one overwrites the previous name. |
| **Path normalization** | Paths are normalized via `Path.GetFullPath()` before storage to ensure consistent lookups. |

### Technical Details

**Schema:**
```sql
CREATE TABLE IF NOT EXISTS AgentMetadata (
    Id         INTEGER PRIMARY KEY AUTOINCREMENT,
    Path       TEXT    NOT NULL UNIQUE,
    CustomName TEXT    NOT NULL
);
```

**Index:** `idx_agent_metadata_path` on `Path`

**Key operations:**

| Method | Behavior |
|---|---|
| `GetAgentCustomNameAsync(path)` | Lookup by full path, returns `CustomName` or `null` |
| `SetAgentCustomNameAsync(path, customName)` | `INSERT ... ON CONFLICT(Path) DO UPDATE SET CustomName` |

---

## Sessions

> Managed by the `Repository`. Sessions track CLI session history for logging purposes.

### Schema

*Created in `SqlStrings.InitStatements` (run by `Repository.EnsureInitialized()`)*

```sql
CREATE TABLE IF NOT EXISTS Sessions (
    Id                 TEXT PRIMARY KEY,
    Cli                TEXT NOT NULL,
    EnvironmentName    TEXT,
    WorkingDirectory   TEXT NOT NULL,
    ProjectDisplayName TEXT NOT NULL DEFAULT '',
    StartedUTC         TEXT NOT NULL,
    EndedUTC           TEXT,
    ExitCode           INTEGER,
    Processed          INTEGER NOT NULL DEFAULT 0,
    ParentSessionId    TEXT DEFAULT '',
    SessionDisplayName TEXT DEFAULT '',
    OwnerPid           INTEGER,
    OwnershipTracked   INTEGER NOT NULL DEFAULT 1,
    JobRunId           TEXT,
    ExportedUTC        TEXT
);
```

`Processed`, `ParentSessionId`, `SessionDisplayName`, `ProjectDisplayName`, `OwnerPid`,
`OwnershipTracked`, `JobRunId`, and `ExportedUTC` are added via `ALTER TABLE` migrations (safe to
re-run). `ExportedUTC` is the independent remote-upload acknowledgement cursor; `Processed`
remains exclusively for transcript generation. Two further migration columns —
`AggregateEmbeddedUTC` and `AggregateEmbedFailureCount` — drive the session-level BERT aggregate
embedding backfill job.

When `JobRunId` is not NULL the session belongs to an Automated Job; a trigger
(`Sessions_LinkJobRunSession`) atomically backlinks the `JobRuns.SessionId` inside the INSERT
transaction and aborts if the claimed run no longer exists.

### SessionLogs

```sql
CREATE TABLE IF NOT EXISTS SessionLogs (
    Id        INTEGER PRIMARY KEY AUTOINCREMENT,
    SessionId TEXT    NOT NULL,
    Timestamp TEXT    NOT NULL,
    Content   BLOB    NOT NULL,
    IsError   INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY (SessionId) REFERENCES Sessions(Id)
);
```

> **Note:** `Content` is a **BLOB** (raw terminal bytes), not TEXT.

### Key Operations

| Method | Behavior |
|---|---|
| `CreateSessionAsync(sessionId, cli, envName, workDir, ownerPid, jobRunId)` | Insert new session when CLI launches |
| `GetProjectDisplayNameAsync(path)` | Reads the latest project display name for a working directory, falling back to the folder name |
| `UpdateLatestProjectDisplayNameAsync(path, projectDisplayName)` | Updates the newest session for that working directory |
| `LogSessionOutputAsync(sessionId, content, isError)` | Append terminal output (byte buffer) |
| `CompleteSessionAsync(sessionId, exitCode)` | Mark session as ended |
| `GetOldestUnexportedSessionIdAsync(endedBeforeUtc, ct)` | Oldest settled session without an export ACK |
| `WriteSessionExportAsync(sessionId, destination, ct)` | Stream a deterministic session envelope from one read transaction. `userInputs` is written immediately after `session` so the server can project prompts into SQL without scanning log BLOBs. |
| `MarkSessionExportedAsync(sessionId, exportedUtc, ct)` | Set `ExportedUTC` after a matching remote ACK without touching `Processed` |
| `GetRecentSessionsAsync(limit, ct)` | Recent sessions ordered by `StartedUTC DESC` |
| `GetSessionWithLogsAsync(sessionId, ct)` | Session with all log entries |
| `GetSessionOutputAsync(sessionId, ct)` | Session row joined with `sessionOutPut` aggregated text |
| `SetParentSessionIdAsync(sessionId, parentSessionId)` | Link a child session to its parent |
| `SetSessionDisplayNameAsync(sessionId, displayName)` | Override the auto-derived display name |
| `GetOpenSessionCleanupCandidatesAsync(trackedCutoff, untrackedCutoff, ct)` | Stale open sessions whose owner PID is gone |

> **Note:** `EnvironmentName` and `WorkingDirectory` are stored as plain strings — no foreign keys to other tables.

---

## User Input Tracking

> Managed by the `Repository`. Tracks what the user types during CLI sessions and correlates their inputs with code changes (git diffs).

### Business Logic

| Rule | Details |
|---|---|
| **Purpose** | Map user intent (what they typed) to code impact (what files changed) for analytics and future preloading |
| **Sequence tracking** | Each input within a session gets an incrementing sequence number (1, 2, 3...) |
| **Git state capture** | On each input, the current HEAD commit hash is recorded |
| **Diff calculation** | When a second+ input is recorded, the system calculates all file changes since the previous input's commit |
| **Fire and forget** | Recording is invoked from an `InputAccumulator` callback so it doesn't block the user's terminal; call sites `await` the method, not `Task.Run` |
| **Error tolerance** | Recording failures are logged to stderr but don't interrupt the CLI session |
| **Secret filtering** | `InputEtlFilter.Process` strips secrets before text lands in the **FTS index** only. `UserInputs` itself holds the canonical raw row (transcript replay needs it). |
| **BERT embeddings** | `BertEmbeddedUTC` / `BertEmbedFailureCount` (migration columns) drive the embedding backfill job |

### Schema

*Created in `SqlStrings.InitStatements` (run by `Repository.EnsureInitialized()`)*

```sql
CREATE TABLE IF NOT EXISTS UserInputs (
    Id            INTEGER PRIMARY KEY AUTOINCREMENT,
    SessionId     TEXT    NOT NULL,
    Sequence      INTEGER NOT NULL,
    InputText     TEXT    NOT NULL,
    GitCommitHash TEXT,
    TimestampUTC  TEXT    NOT NULL,
    FOREIGN KEY (SessionId) REFERENCES Sessions(Id)
);

CREATE TABLE IF NOT EXISTS InputFileChanges (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    UserInputId     INTEGER NOT NULL,
    PreviousInputId INTEGER,
    FilePath        TEXT    NOT NULL,
    ChangeType      TEXT    NOT NULL,
    LinesAdded      INTEGER,
    LinesDeleted    INTEGER,
    DiffContent     TEXT,
    FOREIGN KEY (UserInputId) REFERENCES UserInputs(Id),
    FOREIGN KEY (PreviousInputId) REFERENCES UserInputs(Id)
);
```

**Migration columns** (added via `ALTER TABLE`, safe to re-run):
- `BertEmbeddedUTC TEXT` — set when the BERT embedding backfill processes the row
- `BertEmbedFailureCount INTEGER NOT NULL DEFAULT 0` — poison-pill skip counter (threshold: 3 consecutive failures)

**Indexes:**
- `idx_user_inputs_session` on `UserInputs(SessionId)`
- `idx_user_inputs_session_seq` on `UserInputs(SessionId, Sequence)`
- `idx_input_file_changes_input` on `InputFileChanges(UserInputId)`
- `idx_input_file_changes_filepath` on `InputFileChanges(FilePath)`
- `idx_user_inputs_unembedded` (partial) on `UserInputs(Id) WHERE BertEmbeddedUTC IS NULL`

### FTS5 Full-Text Index

```sql
CREATE VIRTUAL TABLE IF NOT EXISTS UserInputs_fts USING fts5(
    InputText,
    content='UserInputs',
    content_rowid='Id',
    tokenize='porter unicode61'
);
```

External-content FTS5 table — the virtual table is just the inverted index; raw text stays in
`UserInputs`. FTS writes are driven from C# (after `InputEtlFilter` strips secrets), **not**
from triggers, so secrets never land in the index. Legacy auto-insert/update triggers from
earlier installs are dropped; only the delete trigger remains for sync.

### ChangeType Values

| Value | Meaning |
|:---:|---|
| A | Added (new file) |
| M | Modified |
| D | Deleted |

> Only `A`, `M`, and `D` are produced by `GitService` (determined from `--numstat` added/deleted
> counts). Untracked files are captured as `A` with no line counts or diff content.

### Key Operations

| Method | Behavior |
|---|---|
| `RecordUserInputAsync(sessionId, inputText, gitService)` | Gets current HEAD commit, inserts the `UserInputs` record, then opens a git-diff capture window via `IGitDiffCaptureService.BeginCaptureWindowAsync`. Does **not** calculate diffs inline — an idle observer re-runs the diff and replaces stored file changes until the next input finalizes the window. |
| `GetLastUserInputAsync(sessionId)` | Returns the most recent input for a session (by sequence) |
| `InsertUserInputAsync(sessionId, sequence, inputText, gitCommitHash)` | Insert a new user input record (raw text; the FTS index write is filtered separately) |
| `InsertFileChangesAsync(userInputId, previousInputId, changes)` | Append-only batch insert. **Deprecated for live capture** — kept for interface back-compat. New code uses `ReplaceFileChangesAsync`, which deletes then re-inserts a `userInputId`'s file changes in one transaction (the idle observer calls it on each re-run). |

### Data Flow

1. User presses Enter in the terminal
2. `InputAccumulator` fires callback with accumulated text
3. `RecordUserInputAsync` is called (awaited from the accumulator callback)
4. System gets current HEAD commit via `git rev-parse HEAD`
5. Insert `UserInputs` record (raw text; FTS index write is filtered separately)
6. Open a git-diff capture window via `IGitDiffCaptureService.BeginCaptureWindowAsync` (finalizes the previous input's window synchronously)
7. An idle observer re-runs `git diff --numstat {prevCommit}` and calls `ReplaceFileChangesAsync` until the next input finalizes the window

### Diff Content Storage

- **Line counts** (`LinesAdded`, `LinesDeleted`): Always captured for tracked files
- **Full diff content**: Captured when total lines changed < 500; **truncated to 50KB** if the diff is larger (not skipped)
- **Untracked files**: Captured with `ChangeType='A'` but no line counts or diff content

---

## Additional Tables

All created in `SqlStrings.InitStatements` (run by `Repository.EnsureInitialized()`). Some are
also created on demand by their respective store classes (`TokenSavingsStore`,
`CodeAnalyzerIgnoreStore`; `CompressionCapturesSchema` for the retired, schema-only
`CompressionCaptures` table) since those writers can run before the
first `Repository` initializes the schema.

### TerminalSessionLogs

Enriched per-chunk replay data for terminal session playback (cols, rows, alternate screen state).

```sql
CREATE TABLE IF NOT EXISTS TerminalSessionLogs (
    Id                INTEGER PRIMARY KEY AUTOINCREMENT,
    SessionId         TEXT    NOT NULL,
    Sequence          INTEGER NOT NULL,
    IsAlternateScreen INTEGER NOT NULL DEFAULT 0,
    Data              BLOB    NOT NULL,
    Cols              INTEGER NOT NULL DEFAULT 80,
    Rows              INTEGER NOT NULL DEFAULT 24,
    Timestamp         TEXT    NOT NULL,
    FOREIGN KEY (SessionId) REFERENCES Sessions(Id)
);
```

**Index:** `idx_terminal_session_logs_session` on `(SessionId, Sequence)`

### sessionOutPut

Aggregated plain-text output for a session (one row per session, upserted). Joined to `Sessions`
for the chat-history detail view.

```sql
CREATE TABLE IF NOT EXISTS sessionOutPut (
    Id        INTEGER PRIMARY KEY AUTOINCREMENT,
    SessionId TEXT    NOT NULL,
    Text      TEXT    NOT NULL,
    FOREIGN KEY (SessionId) REFERENCES Sessions(Id) ON DELETE CASCADE
);
```

**Index:** `idx_session_output_session` (unique) on `(SessionId)`

### ChatSummary

LLM-generated session summaries keyed by session.

```sql
CREATE TABLE IF NOT EXISTS ChatSummary (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    SessionId   TEXT    NOT NULL UNIQUE,
    SummaryText TEXT    NOT NULL DEFAULT '',
    Date        TEXT    NOT NULL
);
```

### TokenSavings

LLM-proxy token-saver tally: one row per UTC day per provider, upsert-incremented per request.

```sql
CREATE TABLE IF NOT EXISTS TokenSavings (
    Day               TEXT    NOT NULL,
    Provider          TEXT    NOT NULL,
    Requests          INTEGER NOT NULL DEFAULT 0,
    RewrittenRequests INTEGER NOT NULL DEFAULT 0,
    BytesBefore       INTEGER NOT NULL DEFAULT 0,
    BytesAfter        INTEGER NOT NULL DEFAULT 0,
    UpdatedUTC        TEXT    NOT NULL,
    PRIMARY KEY (Day, Provider)
);
```

Byte counts are the measured wire truth; "tokens saved" is derived at display time.

### CompressionCaptures

Raw before/after diagnostic captures: one row per textual output string the pipeline considered
rewriting, plus unchanged textual observations from recognized non-allowlisted Codex tools.
Array-form results can therefore produce multiple rows. Uncapped by explicit product decision —
no retention, no row limit, no truncation. Deduped by `ContentHash` (partial unique index excluding
legacy `''` defaults) with a `SeenCount` counter. `Trace` is retained for compatibility but new
rows persist `[]`; raw/compressed text and enabled IDs are the durable evidence.

```sql
CREATE TABLE IF NOT EXISTS CompressionCaptures (
    Id              TEXT    PRIMARY KEY,
    CreatedUTC      TEXT    NOT NULL,
    Provider        TEXT    NOT NULL,
    ToolName        TEXT    NOT NULL,
    Command         TEXT,
    RawText         TEXT    NOT NULL,
    CompressedText  TEXT    NOT NULL,
    CharsBefore     INTEGER NOT NULL,
    CharsAfter      INTEGER NOT NULL,
    Changed         INTEGER NOT NULL,
    RewriteAccepted INTEGER NOT NULL DEFAULT 0,
    EnabledIds      TEXT    NOT NULL,
    Trace           TEXT    NOT NULL,
    ContentHash     TEXT    NOT NULL DEFAULT '',
    SeenCount       INTEGER NOT NULL DEFAULT 1
);
```

**Index:** `idx_compression_captures_created` on `(CreatedUTC DESC)`
**Unique index (partial):** `idx_compression_captures_hash` on `(ContentHash) WHERE ContentHash != ''`

`ContentHash`, `SeenCount`, and `RewriteAccepted` are added via `ALTER TABLE` migrations.

### CodeAnalyzerIgnores

Files the user excluded from Code quality (MintLint) scans, keyed per repository. `MatchKind` is
`'file'` or `'directory'`; `ReasonKind` is `'test'`/`'config'`/`'other'` (or NULL).

```sql
CREATE TABLE IF NOT EXISTS CodeAnalyzerIgnores (
    RepositoryPath TEXT NOT NULL COLLATE NOCASE,
    Path           TEXT NOT NULL,
    MatchKind      TEXT NOT NULL DEFAULT 'file',
    ReasonKind     TEXT,
    ReasonText     TEXT,
    CreatedUTC     TEXT NOT NULL,
    PRIMARY KEY (RepositoryPath, Path)
);
```

`Path` collation follows host filesystem case semantics (NOCASE on Windows/macOS, BINARY on Linux).

### ProjectCache

Generic key-value store scoped per project path.

```sql
CREATE TABLE IF NOT EXISTS ProjectCache (
    ProjectPath TEXT NOT NULL,
    Key         TEXT NOT NULL,
    Value       TEXT NOT NULL DEFAULT '',
    UpdatedUTC  TEXT NOT NULL,
    PRIMARY KEY (ProjectPath, Key)
);
```

### GlobalCache

Generic key-value store **not** scoped to a project. Used for machine/user-wide flags that must
persist across projects.

```sql
CREATE TABLE IF NOT EXISTS GlobalCache (
    Key        TEXT NOT NULL PRIMARY KEY,
    Value      TEXT NOT NULL DEFAULT '',
    UpdatedUTC TEXT NOT NULL
);
```

#### LLM picker preferences

The customizable launch pickers store their versioned document under
`ui.llm-picker.v1`. The JSON contains the ordered base keys, ordered Environment keys, and disabled
built-in keys. Custom Environment visibility remains authoritative in `Environments.Hidden` for
compatibility with older Environment clients. Environments flagged `AutomationWorker` are excluded
from the resolved catalog entirely, so picker saves never touch a Worker's `Hidden` value.

`Repository.SaveLlmPickerStateAsync` writes or removes the `GlobalCache` document and updates all
submitted `Environments.Hidden` values in one SQLite transaction. The resolver ignores stale keys,
appends newly supported CLIs/Environments in canonical order, and returns contiguous positions to
the browser. Reset removes the cache document and makes supported custom Environments visible.

### Kanban board tables (Board*)

See the [Board architecture/data model](../../VibeRails/Services/Board/ARCHITECTURE.md) and
[contributor guide](../../VibeRails/Services/Board/AGENTS.md) for cross-layer contracts and open
review findings.

Owned by `VibeRails.Data.Sqlite/Board/BoardStore.cs` (singleton, own connection string, `EnsureSchema()` in its
constructor — the JobStore pattern), **not** by `Repository.InitStatements`. That is what lets the
stdio MCP host (`vb mcp`) construct the store without running the dashboard's migration pass.
Board/lane/card rows carry `ProjectPath`; dependent rows inherit scope through their card.
Project lookups are normalised, NOCASE on Windows/macOS, and the project path never comes from
a request — `BoardProjectResolver` derives
it (dashboard root path → the launching terminal session's card → git root of cwd → cwd).

```sql
Boards            (Id TEXT PK, ProjectPath, Name, Position, CreatedUTC, UpdatedUTC)
BoardColumns      (Id TEXT PK, ProjectPath, BoardId NULL, Name, Position, Color, CreatedUTC, UpdatedUTC)
BoardCards        (Id TEXT PK, ProjectPath, Number, ColumnId → BoardColumns, Position, Title, Description,
                   Assignee NULL, Priority, Type, Points NULL, Tags JSON, Blocked, Flagged, CreatedUTC, UpdatedUTC,
                   UNIQUE(ProjectPath, Number))
BoardCardSequences (ProjectPath TEXT PK, LastNumber)
BoardCardOptions   (CardId → BoardCards CASCADE PK, OptionsJson)
BoardCardLinks     (CardId → BoardCards CASCADE, LinkedCardId → BoardCards CASCADE,
                   PK(CardId, LinkedCardId), CHECK(CardId < LinkedCardId))
BoardComments     (Id TEXT PK, CardId → BoardCards CASCADE, AuthorKind 'user'|'agent', AuthorLabel,
                   AuthorCli NULL, SessionId NULL, Body, CreatedUTC, Kind 'comment'|'note')
BoardCardSessions (SessionId TEXT PK, CardId → BoardCards CASCADE, TabId NULL, Selection, Cli,
                   DisplayName, Origin 'launch'|'mcp'|'manual', CreatedUTC)
BoardAttachments  (Id TEXT PK, CardId → BoardCards CASCADE, Name, MimeType, Bytes, DataUrl, CreatedUTC)
BoardAttachmentContents (AttachmentId → BoardAttachments CASCADE PK, Content BLOB)
BoardCommits      (CardId → BoardCards CASCADE, Sha, Author, Message, CommittedUTC, LinkedUTC, PK(CardId, Sha))
BoardCommitSnapshots (CardId, Sha → BoardCommits CASCADE, SnapshotJson, PK(CardId, Sha))
```

- A card's display key is `VB-{Number}`; `BoardCardSequences` allocates increasing per-project
  numbers inside the card insert transaction. Its high-water mark survives deleting every card
  and restarting; schema initialization seeds it from existing cards without decreasing it.
  Lookups accept the id or the key. Positions are dense `0..n-1` per lane after every
  create/move/delete (`WriteCardPositionsAsync`).
- Migration `board/4` adds multiple boards per project. Card ownership follows the lane's
  `BoardId`; it is nullable with no FK for legacy compatibility. Startup adopts null-board
  lanes into the project's default board. Card numbers stay unique across a project's boards.
- `BoardCards.Type` is one of `task`, `bug`, `feature`, `research-spike`, or `chore`. Migration
  `board/3` adds it with the neutral `task` default so existing cards are not guessed from tags or
  title text.
- Migration `board/5` adds `BoardCardLinks`. Each undirected pair is stored once in ordinal id
  order; repeated links are idempotent and either end can unlink. Both cards are resolved in the
  same project inside the write transaction, including when they sit on different boards.
  Deleting either card (or its board) cascades the link. Reads join current titles and board/lane
  names; candidate search omits the source and existing links and returns at most 50 matches.
- `Assignee` is an LLM picker key (`base:claude` / `env:7:codex`), validated by `BoardSelection`.
  Optional base-provider model, effort and startup-mode overrides are stored in `BoardCardOptions`;
  changing the assignee clears the old overrides unless the request supplies a new valid set.
- `BoardCardSessions.SessionId` is the terminal `Sessions.Id` (no FK: sessions are written by other
  processes). "Live" is computed at read time by the root backend from its in-memory tab host.
- Deleting the last lane is refused; deleting another lane moves its cards to the left-most one.
- Cards keep only their current state. Description replacement accepts the last write; append
  reads current text and validates the combined length inside the same write transaction.
  Board context and lane Automation settings retain their separate revision checks.
- `Flagged` (board/9, default false) requests human attention independently of `Blocked`.
  Reads do not claim a card or record read events; writes can link an unlinked agent session.
- Saving never sends terminal input. Launches compose their prompt from the current card;
  sessions remain linked without revision provenance.
- Current attachments store bytes in `BoardAttachmentContents`; old raster data URLs remain
  readable. There is no byte quota, only 40 current files. Removal deletes the metadata and
  cascades the bytes; removed IDs are not readable. Content routes keep card/project scoping
  and both API credentials. Migration backups can retain retired content.
- A commit link and its changed-code snapshot are inserted in one transaction, after Git capture
  succeeds. `SnapshotJson` uses the existing AOT `SandboxDiffResponse` shape (file name, language,
  before/after content, total changes). Viewing reads only the saved snapshot, so deleting a
  workspace cannot break it. The table is created additively; older metadata-only links report
  that they must be unlinked and linked again to capture their code.

### Automated Jobs Tables

Created by `JobStore.cs` (`JobStore.SchemaSql`), **not** `SqlStrings` — but they live in the same
`state.db` and are initialized on first use by `JobStore`. These power the Automated Jobs feature
(scheduled/triggered ordered workflows).

**Jobs** — a scheduled/triggered automation definition. `EnvironmentId` remains a compatibility
mirror of the workflow's optional Worker; `JobActions` is authoritative.

```sql
CREATE TABLE IF NOT EXISTS Jobs (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    Name            TEXT    NOT NULL,
    ProjectPath     TEXT    NOT NULL,
    EnvironmentId   INTEGER,
    TimeoutMinutes  INTEGER NOT NULL DEFAULT 60,
    Enabled         INTEGER NOT NULL DEFAULT 0,
    LaunchMinimized INTEGER NOT NULL DEFAULT 0,
    CreatedUTC      TEXT    NOT NULL,
    UpdatedUTC      TEXT    NOT NULL,
    DeletedUTC      TEXT,
    FOREIGN KEY (EnvironmentId) REFERENCES Environments(Id) ON DELETE SET NULL
);
```

**Index:** `idx_jobs_project` on `(ProjectPath, Enabled, DeletedUTC)`

**JobActions** — the editable ordered workflow. `Position` is zero-based and unique within the
Automation. `Kind` is 0 Worker / 1 Script; there may be at most one Worker (enforced by
`JobService`). Script paths and working directories are repository-relative portable paths,
`ArgumentsJson` is an array of discrete argv values, and `ApprovedHash` is the SHA-256 of the
reviewed script bytes. `TimeoutSeconds = 0` means no action-specific limit.

```sql
CREATE TABLE IF NOT EXISTS JobActions (
    Id               TEXT    PRIMARY KEY,
    JobId            INTEGER NOT NULL,
    Position         INTEGER NOT NULL,
    Kind             INTEGER NOT NULL,
    EnvironmentId    INTEGER,
    ScriptPath       TEXT,
    ScriptRuntime    INTEGER,
    ArgumentsJson    TEXT    NOT NULL DEFAULT '[]',
    WorkingDirectory TEXT,
    TimeoutSeconds   INTEGER NOT NULL DEFAULT 0,
    ApprovedHash     TEXT,
    CreatedUTC       TEXT    NOT NULL,
    UpdatedUTC       TEXT    NOT NULL,
    FOREIGN KEY (JobId) REFERENCES Jobs(Id) ON DELETE CASCADE,
    FOREIGN KEY (EnvironmentId) REFERENCES Environments(Id) ON DELETE SET NULL,
    UNIQUE(JobId, Position)
);
```

**Index:** `idx_job_actions_job` on `(JobId, Position)`

**JobTriggers** — one or more triggers per Job (interval/daily/weekly schedule, before commit,
after commit). Before-commit and after-commit are mutually exclusive because the per-Job overlap
guard would otherwise discard the second event. Run now is always available and is not stored as a
trigger.

```sql
CREATE TABLE IF NOT EXISTS JobTriggers (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    JobId           INTEGER NOT NULL,
    Kind            INTEGER NOT NULL,
    ScheduleKind    INTEGER,
    IntervalMinutes INTEGER,
    LocalTime       TEXT,
    DaysOfWeekMask  INTEGER NOT NULL DEFAULT 0,
    TimeZoneId      TEXT,
    NextRunUTC      TEXT,
    LastRunUTC      TEXT,
    FOREIGN KEY (JobId) REFERENCES Jobs(Id),
    UNIQUE(JobId, Kind)
);
```

**Index:** `idx_job_triggers_due` on `(Kind, NextRunUTC)`

**JobRuns** — one row per executed workflow. The top-level LLM/Environment fields are a summary
snapshot for legacy list clients and the Worker's session backlink. `SessionId` links back to
`Sessions` (the `Sessions_LinkJobRunSession` trigger backlinks it atomically — see Sessions
above); the authoritative action snapshot is in `JobRunActions`.

```sql
CREATE TABLE IF NOT EXISTS JobRuns (
    Id               TEXT PRIMARY KEY,
    JobId            INTEGER NOT NULL,
    TriggerKind      INTEGER NOT NULL,
    TriggerKey       TEXT    NOT NULL UNIQUE,
    Status           INTEGER NOT NULL,
    JobName          TEXT    NOT NULL,
    ProjectPath      TEXT    NOT NULL,
    Llm              INTEGER NOT NULL,
    EnvironmentId    INTEGER,
    EnvironmentName  TEXT,
    TimeoutMinutes   INTEGER NOT NULL,
    SessionId        TEXT,
    QueuedUTC        TEXT    NOT NULL,
    StartedUTC       TEXT,
    EndedUTC         TEXT,
    ExitCode         INTEGER,
    ErrorMessage     TEXT,
    CancelRequested  INTEGER NOT NULL DEFAULT 0,
    OwnerProcessId   INTEGER,
    LaunchedUTC      TEXT,
    LaunchMinimized  INTEGER NOT NULL DEFAULT 0,
    DeletedUTC       TEXT,
    FOREIGN KEY (JobId) REFERENCES Jobs(Id)
);
```

**Indexes:** `idx_job_runs_queue` on `(Status, QueuedUTC)`, `idx_job_runs_job` on `(JobId, QueuedUTC DESC)`

**JobRunActions** — immutable action definitions copied transactionally when a run is queued, plus
their mutable per-run status/output. A retry copies these rows from the source run instead of
reading the currently edited `JobActions`. `SourceActionId` is informational rather than a foreign
key so later edits/deletion cannot invalidate history. Worker `SessionId` opens the normal terminal
replay; script stdout/stderr is captured here (bounded by `JobRunner`).

```sql
CREATE TABLE IF NOT EXISTS JobRunActions (
    Id               TEXT    PRIMARY KEY,
    RunId            TEXT    NOT NULL,
    SourceActionId   TEXT,
    Position         INTEGER NOT NULL,
    Kind             INTEGER NOT NULL,
    Status           INTEGER NOT NULL,
    EnvironmentId    INTEGER,
    EnvironmentName  TEXT,
    Llm               INTEGER NOT NULL DEFAULT 0,
    ScriptPath       TEXT,
    ScriptRuntime    INTEGER,
    ArgumentsJson    TEXT    NOT NULL DEFAULT '[]',
    WorkingDirectory TEXT,
    TimeoutSeconds   INTEGER NOT NULL DEFAULT 0,
    ApprovedHash     TEXT,
    SessionId        TEXT,
    StartedUTC       TEXT,
    EndedUTC         TEXT,
    ExitCode         INTEGER,
    ErrorMessage     TEXT,
    StandardOutput   TEXT    NOT NULL DEFAULT '',
    StandardError    TEXT    NOT NULL DEFAULT '',
    FOREIGN KEY (RunId) REFERENCES JobRuns(Id) ON DELETE CASCADE,
    UNIQUE(RunId, Position)
);
```

**Index:** `idx_job_run_actions_run` on `(RunId, Position)`

`JobStore` migrates an older Worker-only `Jobs.EnvironmentId` row to one Worker `JobActions` row,
and similarly backfills historical `JobRuns` with one status-mapped `JobRunActions` row. Run/action
terminal states are finalized together for cancellation, timeout, interrupted/reaped runs, stalled
launches, disabling, and deletion so no pending action remains under a terminal run.

**JobSchedulerLease** — single-writer lease so only one process runs the job scheduler at a time.

```sql
CREATE TABLE IF NOT EXISTS JobSchedulerLease (
    LeaseName  TEXT PRIMARY KEY,
    OwnerId    TEXT NOT NULL,
    ExpiresUTC TEXT NOT NULL
);
```

---

## Entity Relationships

Sandboxes, AgentMetadata, TokenSavings, CompressionCaptures, CodeAnalyzerIgnores,
ProjectCache, and GlobalCache have **no foreign key relationships** — they are fully independent
(the Board* tables relate only to each other; see their section)
tables. `Environments` is referenced by the compatibility mirror `Jobs.EnvironmentId` and by
`JobActions.EnvironmentId` (both `ON DELETE SET NULL` — see the Automated Jobs Tables above), and
by `EnvironmentSteps.EnvironmentId` (`ON DELETE CASCADE` — a
step is part of its environment and owns no filesystem resource). Sessions reference environments and working directories by string
value only — no FK constraints. Sandboxes reference projects by `ProjectPath` string value — no
FK to any project table.

The tables with actual FK constraints point at `Sessions.Id` (`SessionLogs`, `sessionOutPut`
with `ON DELETE CASCADE`, `TerminalSessionLogs`, `UserInputs`) and at `UserInputs.Id`
(`InputFileChanges`, whose `PreviousInputId` is a second FK to `UserInputs.Id` — not
self-referential). `EnvironmentSteps.EnvironmentId → Environments(Id)` is the one
`ON DELETE CASCADE` pointing at Environments. The Automated Jobs tables add these FK chains:
`Jobs.EnvironmentId` / `JobActions.EnvironmentId → Environments(Id)` (`ON DELETE SET NULL`),
`JobActions.JobId → Jobs(Id)` (`ON DELETE CASCADE`), `JobTriggers.JobId` / `JobRuns.JobId →
Jobs(Id)`, and `JobRunActions.RunId → JobRuns(Id)` (`ON DELETE CASCADE`).

```
Environments              AgentMetadata
+--------------+          +-------------+
| Id (PK)      |<---+     | Id (PK)     |
| CustomName   |    |     | Path (UQ)   |
| LLM          |    |     | CustomName  |
| Path         |    |     +-------------+
| CustomArgs   |    |
| CustomPrompt |    |     Sandboxes
| CreatedUTC   |    |     +-------------------+
| LastUsedUTC  |    |     | Id (PK)           |
| Hidden       |    |     | Name              |
| AutomationWorker* |     | Path              |
+--------------+    |     | ProjectPath       |  <-- string, not FK
UQ(CustomName, LLM) |     | Branch            |
                    |     | CommitHash        |
EnvironmentSteps    |     | RemoteUrl         |
+-------------------+     | SourceBranch      |
| Id (PK)           |     | CreatedUTC        |
| EnvironmentId(FK)-+     +-------------------+
| Phase             |     UQ(Name, ProjectPath)
| Position          |
| Name / Command    |     (ON DELETE CASCADE)
| StartMinimized    |
| TimeoutSeconds    |
| Enabled           |
| CreatedUTC        |
| UpdatedUTC        |
+-------------------+
UQ-by-convention(EnvironmentId, Phase, Position)

Sessions                  +-------------------+
+-------------------+     | Id (PK)           |
| Id (PK)           |<----| (FK targets below)|
| Cli               |     +-------------------+
| EnvironmentName   |  <-- string, not FK
| WorkingDirectory  |  <-- string, not FK
| ProjectDisplayName|
| StartedUTC        |     SessionLogs               sessionOutPut
| EndedUTC          |     +-------------------+     +-------------------+
| ExitCode          |     | Id (PK)           |     | Id (PK)           |
| Processed         |     | SessionId (FK) ---|---> | SessionId (FK) ---|---> Sessions.Id
| ParentSessionId   |     | Timestamp         |     | Text              |     (ON DELETE CASCADE)
| SessionDisplayName|     | Content (BLOB)    |     +-------------------+
| OwnerPid          |     | IsError           |
| OwnershipTracked  |     +-------------------+
| JobRunId          |
| AggregateEmbedded*|     TerminalSessionLogs
+-------------------+     +-------------------+
                          | Id (PK)           |
                          | SessionId (FK) ---|---> Sessions.Id
                          | Sequence          |
                          | IsAlternateScreen |
                          | Data (BLOB)       |
                          | Cols / Rows       |
                          | Timestamp         |
                          +-------------------+

UserInputs                InputFileChanges
+-------------------+     +-------------------+
| Id (PK)           |<----| UserInputId (FK)  |
| SessionId (FK) ---|---> Sessions.Id         |
| Sequence          |     | PreviousInputId   |----> UserInputs.Id (nullable)
| InputText         |     | FilePath          |
| GitCommitHash     |     | ChangeType        |
| TimestampUTC      |     | LinesAdded        |
| BertEmbeddedUTC*  |     | LinesDeleted      |
| BertEmbedFailCnt* |     | DiffContent       |
+-------------------+     +-------------------+

ChatSummary               TokenSavings / CompressionCaptures
+-------------------+     CodeAnalyzerIgnores / ProjectCache / GlobalCache
| Id (PK)           |     (all independent — no FKs)
| SessionId (UQ)    |
| SummaryText       |
| Date              |
+-------------------+

* = migration column added via ALTER TABLE
```

---

## Repository Patterns

- Most methods are `async` with `CancellationToken` support (some session/user-input methods omit it)
- Each method opens its own `SqliteConnection` — no shared connection or unit of work
- Reader mapping is **positional** (column index), not by column name

---

*Last checked: 2026-09-17 by Codex (added BoardCards.Type and board/3 backfill)*
