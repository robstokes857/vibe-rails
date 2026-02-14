# DB Layer — Business Logic & Technical Reference

This document describes how Environments, Sandboxes, AgentMetadata, and Sessions work at the database layer.

> **Contents:**
> [Database Overview](#database-overview) | [Environments](#environments) | [Sandboxes](#sandboxes) | [AgentMetadata](#agentmetadata) | [ProjectMetadata](#projectmetadata) | [Sessions](#sessions) | [Entity Relationships](#entity-relationships) | [Repository Patterns](#repository-patterns)

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

An **Environment** is a reusable configuration for a specific LLM CLI (Claude, Codex, or Gemini). Environments are **global** — they are not tied to any project.

| Rule | Details |
|---|---|
| **Unique identity** | Identified by the pair `(CustomName, LLM)`. You can have "MyEnv" for Claude AND "MyEnv" for Codex, but NOT two "MyEnv" entries for the same LLM. |
| **No default environments saved** | Launching a CLI without an explicit environment name creates no row. The system uses `LLM_Environment.DefaultPrompt` directly. Only user-created named environments are persisted. |
| **Custom environments** | Created via the UI or `--env` flag. They store `CustomArgs` (CLI flags prepended to launch args) and `CustomPrompt` (system prompt override; falls back to `DefaultPrompt` if empty). |
| **Config directory** | `Path` stores the filesystem location for the environment's LLM-specific config files. Set by `LlmCliEnvironmentService`, not the DB layer. |
| **Recency tracking** | `LastUsedUTC` is bumped on every access or launch. Dashboard orders by `LastUsedUTC DESC`. |
| **Querying** | `GetCustomEnvironmentsAsync` filters out default/bare environments. `GetAllEnvironmentsAsync` returns everything. |
| **Deletion guard** | Default environments cannot be deleted — enforced in the Routes layer, not the DB layer. |

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
    UNIQUE(CustomName, LLM)
);
```

**Index:** `idx_environments_name_llm` on `(CustomName, LLM)`

**Model:** `LLM_Environment` class in `DTOs/LLM_Environment.cs`

**LLM enum** (stored as integer):

| Value | Name |
|:---:|---|
| 0 | NotSet |
| 1 | Codex |
| 2 | Claude |
| 3 | Gemini |

**Key operations:**

| Method | Behavior |
|---|---|
| `GetEnvironmentByNameAndLlmAsync(name, llm)` | Lookup by the unique `(CustomName, LLM)` pair |
| `GetOrCreateEnvironmentAsync(name, llm)` | Lookup, then create if missing. Bumps `LastUsedUTC` if found. |
| `SaveEnvironmentAsync` | `INSERT ... RETURNING Id` |
| `UpdateEnvironmentAsync` | Full field update by `Id` (CustomName, LLM, Path, CustomArgs, CustomPrompt, LastUsedUTC) |
| `DeleteEnvironmentAsync` | Delete by `Id` — **no deletion guard at DB layer**. The "cannot delete Default" rule is in `Routes.cs`. |

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
    CreatedUTC  TEXT    NOT NULL,
    UNIQUE(Name, ProjectPath)
);
```

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
| `/api/v1/sandboxes/{id}/launch/vscode` | POST | Launch VS Code in sandbox directory |

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

## ProjectMetadata

### Business Logic

**ProjectMetadata** stores user-assigned display names for projects (identified by their root directory path). This allows users to set a custom name for the current project via the dashboard "Edit name" button.

| Rule | Details |
|---|---|
| **Keyed by path** | Each project is identified by its absolute filesystem root path. |
| **Upsert behavior** | Setting a custom name for a path that already has one overwrites the previous name. |
| **Path normalization** | Paths are normalized via `Path.GetFullPath()` before storage to ensure consistent lookups. |

### Technical Details

**Schema:**
```sql
CREATE TABLE IF NOT EXISTS ProjectMetadata (
    Id         INTEGER PRIMARY KEY AUTOINCREMENT,
    Path       TEXT    NOT NULL UNIQUE,
    CustomName TEXT    NOT NULL
);
```

**Index:** `idx_project_metadata_path` on `Path`

**Key operations:**

| Method | Behavior |
|---|---|
| `GetProjectCustomNameAsync(path)` | Lookup by full path, returns `CustomName` or `null` |
| `SetProjectCustomNameAsync(path, customName)` | `INSERT ... ON CONFLICT(Path) DO UPDATE SET CustomName` |

**API endpoints:**

| Endpoint | Method | Behavior |
|---|---|---|
| `/api/v1/projects/name?path={path}` | GET | Returns the custom name for a project path |
| `/api/v1/projects/name` | PUT | Sets a custom project name (body: `{ path, customName }`) |

---

## Sessions

> Managed by `DbService`, not the `Repository`. Sessions track CLI session history for logging purposes.

### Schema

*Created in `DbService.InitializeDatabase()`*

```sql
CREATE TABLE IF NOT EXISTS Sessions (
    Id               TEXT PRIMARY KEY,
    Cli              TEXT NOT NULL,
    EnvironmentName  TEXT,
    WorkingDirectory TEXT NOT NULL,
    StartedUTC       TEXT NOT NULL,
    EndedUTC         TEXT,
    ExitCode         INTEGER
);

CREATE TABLE IF NOT EXISTS SessionLogs (
    Id        INTEGER PRIMARY KEY AUTOINCREMENT,
    SessionId TEXT    NOT NULL,
    Timestamp TEXT    NOT NULL,
    Content   TEXT    NOT NULL,
    IsError   INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY (SessionId) REFERENCES Sessions(Id)
);
```

### Key Operations

| Method | Behavior |
|---|---|
| `CreateSessionAsync(sessionId, cli, envName, workDir)` | Insert new session when CLI launches |
| `LogSessionOutputAsync(sessionId, content, isError)` | Append terminal output line |
| `CompleteSessionAsync(sessionId, exitCode)` | Mark session as ended |
| `GetRecentSessionsAsync(limit)` | Recent sessions ordered by `StartedUTC DESC` |
| `GetSessionWithLogsAsync(sessionId)` | Session with all log entries |

> **Note:** `EnvironmentName` and `WorkingDirectory` are stored as plain strings — no foreign keys to other tables.

---

## User Input Tracking

> Managed by `DbService`. Tracks what the user types during CLI sessions and correlates their inputs with code changes (git diffs).

### Business Logic

| Rule | Details |
|---|---|
| **Purpose** | Map user intent (what they typed) to code impact (what files changed) for analytics and future preloading |
| **Sequence tracking** | Each input within a session gets an incrementing sequence number (1, 2, 3...) |
| **Git state capture** | On each input, the current HEAD commit hash is recorded |
| **Diff calculation** | When a second+ input is recorded, the system calculates all file changes since the previous input's commit |
| **Fire and forget** | Recording happens asynchronously in the background so it doesn't block the user's terminal |
| **Error tolerance** | Recording failures are logged to stderr but don't interrupt the CLI session |

### Schema

*Created in `DbService.InitializeDatabase()`*

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

**Indexes:**
- `idx_user_inputs_session` on `UserInputs(SessionId)`
- `idx_user_inputs_session_seq` on `UserInputs(SessionId, Sequence)`
- `idx_input_file_changes_input` on `InputFileChanges(UserInputId)`
- `idx_input_file_changes_filepath` on `InputFileChanges(FilePath)`

### ChangeType Values

| Value | Meaning |
|:---:|---|
| A | Added (new file) |
| M | Modified |
| D | Deleted |
| R | Renamed |

### Key Operations

| Method | Behavior |
|---|---|
| `RecordUserInputAsync(sessionId, inputText, gitService)` | Orchestrates the full flow: gets commit hash, calculates diffs, stores everything |
| `GetLastUserInputAsync(sessionId)` | Returns the most recent input for a session (by sequence) |
| `InsertUserInputAsync(sessionId, sequence, inputText, gitCommitHash)` | Insert a new user input record |
| `InsertFileChangesAsync(userInputId, previousInputId, changes)` | Batch insert file changes in a transaction |

### Data Flow

1. User presses Enter in the terminal
2. `InputAccumulator` fires callback with accumulated text
3. `RecordUserInputAsync` is called (fire-and-forget via `Task.Run`)
4. System gets current HEAD commit via `git rev-parse HEAD`
5. If there's a previous input, calculate diff via `git diff --numstat {prevCommit}`
6. Insert `UserInputs` record
7. Insert `InputFileChanges` records (one per changed file)

### Diff Content Storage

- **Line counts** (`LinesAdded`, `LinesDeleted`): Always captured for tracked files
- **Full diff content**: Captured only when total lines changed < 500 AND diff size < 50KB
- **Untracked files**: Captured with `ChangeType='A'` but no line counts or diff content

---

## Entity Relationships

Environments, Sandboxes, AgentMetadata, and ProjectMetadata have **no foreign key relationships** — they are fully independent tables.
Sessions reference environments and working directories by string value only — no FK constraints.
Sandboxes reference projects by `ProjectPath` string value — no FK to any project table.

```
Environments              AgentMetadata         ProjectMetadata
+--------------+          +-------------+       +-----------------+
| Id (PK)      |          | Id (PK)     |       | Id (PK)         |
| CustomName   |          | Path (UQ)   |       | Path (UQ)       |
| LLM          |          | CustomName  |       | CustomName      |
| Path         |          +-------------+       +-----------------+
| CustomArgs   |
| CustomPrompt |          Sandboxes
| CreatedUTC   |          +-------------------+
| LastUsedUTC  |          | Id (PK)           |
+--------------+          | Name              |
UQ(CustomName, LLM)      | Path              |
                          | ProjectPath       |  <-- string, not FK
                          | Branch            |
                          | CommitHash        |
                          | CreatedUTC        |
                          +-------------------+
                          UQ(Name, ProjectPath)

                          Sessions
| CreatedUTC   |          +-------------------+
| LastUsedUTC  |          | Id (PK)           |
+--------------+          | Cli               |
UQ(CustomName, LLM)      | EnvironmentName   |  <-- string, not FK
                          | WorkingDirectory  |  <-- string, not FK
                          | StartedUTC        |
SessionLogs               | EndedUTC          |
+-------------------+     | ExitCode          |
| Id (PK)           |     +-------------------+
| SessionId (FK) ---|---> Sessions.Id
| Timestamp         |
| Content           |
| IsError           |
+-------------------+

UserInputs                InputFileChanges
+-------------------+     +-------------------+
| Id (PK)           |<----| UserInputId (FK)  |
| SessionId (FK) ---|---> Sessions.Id         |
| Sequence          |     | PreviousInputId   |----> UserInputs.Id (nullable)
| InputText         |     | FilePath          |
| GitCommitHash     |     | ChangeType        |
| TimestampUTC      |     | LinesAdded        |
+-------------------+     | LinesDeleted      |
                          | DiffContent       |
                          +-------------------+
```

---

## Repository Patterns

- All methods are `async` with `CancellationToken` support
- Each method opens its own `SqliteConnection` — no shared connection or unit of work
- Reader mapping is **positional** (column index), not by column name

## Vibe Rails Rules
- Log all file changes (STOP)
