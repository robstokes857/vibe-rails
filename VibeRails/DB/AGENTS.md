# DB Layer — Business Logic & Technical Reference

This document describes the database layer: Environments, Sandboxes, AgentMetadata, Sessions, UserInputs, and supporting tables.

> **Contents:**
> [Database Overview](#database-overview) | [Environments](#environments) | [Sandboxes](#sandboxes) | [AgentMetadata](#agentmetadata) | [Sessions](#sessions) | [User Input Tracking](#user-input-tracking) | [Additional Tables](#additional-tables) | [Entity Relationships](#entity-relationships) | [Repository Patterns](#repository-patterns)

---

## Database Overview

| Property | Value |
|---|---|
| **Engine** | SQLite with WAL mode and foreign keys enabled |
| **Connection** | `Data Source={StatePath};Mode=ReadWriteCreate;Cache=Shared` |
| **Initialization** | `Repository.EnsureInitialized()` — double-check locking runs `SqlStrings.InitStatements` (table creation) and `SqlStrings.MigrationStatements` (safe to re-run) exactly once per process |
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

**Model:** `LLM_Environment` class in `DTOs/LLM_Environment.cs`

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
| 8 | Grok46 | Pseudo-CLI: OpenCode launched with `--model=xai/grok-4.6`. `LlmParser` special-cases the string `"grok-4.6"`. Token Saver rides the OpenCode proxy's <c>xai</c> override (`/llm/xai` → `api.x.ai`). |
| 9 | Glm53 | Pseudo-CLI: OpenCode launched with `--model=zai-coding-plan/glm-5.3` (glm-5.3 exists only under the `zai-coding-plan` provider). `LlmParser` special-cases the string `"glm-5.3"`. Traffic goes direct to Z.AI — the OpenCode proxy does not remap `zai-coding-plan`. |

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
type, so `Repository.MaybeRebuildEnvironmentStepsTable` (runs *before* the init loop) drops an
old-shape table — deliberately without porting rows; the int-keyed build had no adopters — and
lets `InitStatements` recreate it TEXT-keyed. The guard keys on the `Id` column's declared type,
so it is a no-op forever after.

`ON DELETE CASCADE` rather than the FK-less orphan pattern `Sandboxes` uses: a step is *part of*
its environment and owns no filesystem resource, so there is no multi-GB directory delete to keep
out of the delete transaction. `PRAGMA foreign_keys=ON` is set at `Repository.cs:53-63`, and
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

**AgentMetadata** stores user-assigned display names for agent files (e.g., `AGENTS.md` files found in repositories).

| Rule | Details |
|---|---|
| **Keyed by path** | Each agent file is identified by its absolute filesystem path. |
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
    JobRunId           TEXT
);
```

`Processed`, `ParentSessionId`, `SessionDisplayName`, `ProjectDisplayName`, `OwnerPid`,
`OwnershipTracked`, and `JobRunId` are added via `ALTER TABLE` migrations (safe to re-run). Two
further migration columns — `AggregateEmbeddedUTC` and `AggregateEmbedFailureCount` — drive the
session-level BERT aggregate embedding backfill job.

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
`CompressionCaptureStore`, `CodeAnalyzerIgnoreStore`) since those writers can run before the
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

### Automated Jobs Tables

Created by `JobStore.cs` (`JobStore.SchemaSql`), **not** `SqlStrings` — but they live in the same
`state.db` and are initialized on first use by `JobStore`. These power the Automated Jobs feature
(scheduled/triggered CLI sessions).

**Jobs** — a scheduled/triggered automation definition.

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

**JobTriggers** — one or more triggers per Job (interval, scheduled, manual).

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

**JobRuns** — one row per executed job run. `SessionId` links back to `Sessions` (the
`Sessions_LinkJobRunSession` trigger backlinks it atomically — see Sessions above).

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
tables. `Environments` is referenced by `Jobs.EnvironmentId` (`ON DELETE SET NULL` — see the
Automated Jobs Tables above) and by `EnvironmentSteps.EnvironmentId` (`ON DELETE CASCADE` — a
step is part of its environment and owns no filesystem resource). Sessions reference environments and working directories by string
value only — no FK constraints. Sandboxes reference projects by `ProjectPath` string value — no
FK to any project table.

The tables with actual FK constraints point at `Sessions.Id` (`SessionLogs`, `sessionOutPut`
with `ON DELETE CASCADE`, `TerminalSessionLogs`, `UserInputs`) and at `UserInputs.Id`
(`InputFileChanges`, whose `PreviousInputId` is a second FK to `UserInputs.Id` — not
self-referential). `EnvironmentSteps.EnvironmentId → Environments(Id)` is the one
`ON DELETE CASCADE` pointing at Environments. The Automated Jobs tables add two more FK chains:
`Jobs.EnvironmentId → Environments(Id)` (`ON DELETE SET NULL`) and `JobTriggers.JobId` /
`JobRuns.JobId → Jobs(Id)`.

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

## Vibe Rails Rules

---

*Last checked: 2026-08-11T00:00:00Z by claude (opus-5)*
