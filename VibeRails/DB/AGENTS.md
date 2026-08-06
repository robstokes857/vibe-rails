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

**Key operations:**

| Method | Behavior |
|---|---|
| `GetEnvironmentByNameAndLlmAsync(name, llm)` | Lookup by the unique `(CustomName, LLM)` pair |
| `GetOrCreateEnvironmentAsync(name, llm)` | Lookup, then create if missing. Bumps `LastUsedUTC` if found. |
| `SaveEnvironmentAsync` | `INSERT ... RETURNING Id` |
| `UpdateEnvironmentAsync` | Full field update by `Id` (CustomName, LLM, Path, CustomArgs, CustomPrompt, LastUsedUTC, Hidden) |
| `TouchEnvironmentLastUsedAsync(id)` | Recency-only bookkeeping — stamps `LastUsedUTC` without touching any other column (launches must never use `UpdateEnvironmentAsync` for this) |
| `DeleteEnvironmentAsync` | Delete by `Id` — **no deletion guard at DB layer**. The "cannot delete Default" and "not referenced by Automations" rules are in `EnvironmentRoutes.cs`. |

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
| **Dirty files** | All dirty + untracked files from the source project are copied into the sandbox after cloning. |
| **Deletion** | Deleting a sandbox removes both the directory (`Directory.Delete(recursive: true)`) and the DB record. |
| **Name validation** | Names must match `^[a-zA-Z0-9_-]+$` (alphanumeric, hyphens, underscores, no spaces). |

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
    UNIQUE(Name, ProjectPath)
);
```

`RemoteUrl` and `SourceBranch` are added via `ALTER TABLE` migrations (safe to re-run).

**Index:** `idx_sandboxes_project` on `(ProjectPath)`

**Model:** `Sandbox` class in `DTOs/Sandbox.cs`

**Key operations:**

| Method | Behavior |
|---|---|
| `SaveSandboxAsync(sandbox)` | `INSERT ... RETURNING Id` |
| `GetSandboxesByProjectAsync(projectPath)` | All sandboxes for a project, ordered by `CreatedUTC DESC` |
| `GetSandboxByIdAsync(id)` | Lookup by primary key |
| `GetSandboxByNameAndProjectAsync(name, projectPath)` | Lookup by unique `(Name, ProjectPath)` pair |
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

Raw before/after compression captures: one row per tool_result the pipeline considered rewriting.
Uncapped by explicit product decision — no retention, no row limit, no truncation. Deduped by
`ContentHash` (partial unique index excluding legacy `''` defaults) with a `SeenCount` counter.

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
compatibility with older Environment clients.

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
Automated Jobs Tables above). Sessions reference environments and working directories by string
value only — no FK constraints. Sandboxes reference projects by `ProjectPath` string value — no
FK to any project table.

The tables with actual FK constraints point at `Sessions.Id` (`SessionLogs`, `sessionOutPut`
with `ON DELETE CASCADE`, `TerminalSessionLogs`, `UserInputs`) and at `UserInputs.Id`
(`InputFileChanges`, whose `PreviousInputId` is a second FK to `UserInputs.Id` — not
self-referential). The Automated Jobs tables add two more FK chains: `Jobs.EnvironmentId →
Environments(Id)` (`ON DELETE SET NULL`) and `JobTriggers.JobId` / `JobRuns.JobId → Jobs(Id)`.

```
Environments              AgentMetadata
+--------------+          +-------------+
| Id (PK)      |          | Id (PK)     |
| CustomName   |          | Path (UQ)   |
| LLM          |          | CustomName  |
| Path         |          +-------------+
| CustomArgs   |
| CustomPrompt |          Sandboxes
| CreatedUTC   |          +-------------------+
| LastUsedUTC  |          | Id (PK)           |
| Hidden       |          | Name              |
+--------------+          | Path              |
UQ(CustomName, LLM)      | ProjectPath       |  <-- string, not FK
                          | Branch            |
                          | CommitHash        |
                          | RemoteUrl         |
                          | SourceBranch      |
                          | CreatedUTC        |
                          +-------------------+
                          UQ(Name, ProjectPath)

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

*Last checked: 2026-08-06T17:54:34Z by opencode (glm-5.2)*
