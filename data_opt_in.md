# Opt-in Session Data Drain

## Status

- **Document type:** Implementation plan
- **Feature name:** Opt-in session data drain
- **Short name:** Data opt-in
- **Primary goal:** Replace the one-shot full-`state.db` Azure export with an off-by-default setting that continuously drains session telemetry (and related capture tables) to the existing export endpoint family.
- **Decision:** Proceed. Do not reuse `Sessions.Processed` — delete it in the same change. Add nullable `ExportedUTC` cursors and a root-backend drain job.

## Executive Summary

VibeRails currently exposes an **Export Data** button in Settings. When the user has a saved API key and `VibeRails:ExportUrl` is a real absolute HTTPS URL, the button snapshots the live `state.db`, Brotli-compresses it, and uploads the whole file to `https://viberails.ai/api/v1/data-exports` (chunked when larger than 4 MiB).

That dump includes everything in the database: environments and custom prompts, job definitions, PIN/cache documents, token-saver captures, embeddings, the lot. The only thing we actually want remotely is session telemetry and the raw tool-text captures.

This feature replaces the button with an **opt-in toggle, off by default**. While it is on, a background job drains ended sessions and pending compression captures, marks each row only after the server acknowledges the upload, and leaves the local rows in place so chat history, resume, job-run links, and embeddings keep working.

## User Promise and Limits

With the toggle on, a saved API key, and a configured HTTPS export URL:

1. Ended CLI sessions are uploaded in the background, including session metadata, raw PTY logs, enriched terminal replay logs, user inputs, input file diffs, and the parsed transcript if one exists.
2. Token-saver `CompressionCaptures` rows (raw and compressed tool text) are uploaded as their own stream. They are not session-keyed, so they do not wait for a session to end.
3. Failures retry on the next tick. A row is not marked exported until the server ACKs.
4. Turning the toggle off stops new uploads. Already-sent data is not un-sent.
5. Clearing the API key, or losing a usable export URL, has the same effect as the toggle being off.

This will **not**:

- Upload a live session (`EndedUTC IS NULL`)
- Delete local rows after a successful upload
- Upload environments, jobs, GlobalCache, PIN state, embeddings, TokenSavings (already published separately), CodeAnalyzerIgnores, or ProjectCache
- Keep the full-database snapshot export as a fallback
- Send data before the user opts in

## Current State

### Full-database export (to be removed)

| Piece | Location |
|---|---|
| Service | `VibeRails/Services/Integrations/VibeCodeRemote/DataExportService.cs` |
| Progress | `DataExportProgress.cs` — polled by the modal |
| Routes | `POST /api/v1/settings/export-data`, `GET .../progress`, `GET .../db-size` |
| UI | Settings **Export Data** button (`#settings-export-data-button`) + `data-export-modal.js` |
| Gate | Saved API key **and** `DataExportConfigured` (same HTTPS URL parse the service uses) |
| Transport | Snapshot → Brotli quality 5 → SHA-256 → single POST or resumable block upload |
| Config | `VibeRails:ExportUrl` in `appsettings.json` (ships as `https://viberails.ai/api/v1/data-exports`) |
| Locking | In-process `SemaphoreSlim` plus `.data-export.lock` beside `state.db` |

The snapshot is read-only against the live database (`SqliteOpenMode.ReadOnly` + `BackupDatabase` into a temp copy). That property stays for the drain: the job may read live rows, but it must never build an export by pointing a writer at `state.db`.

### `Sessions.Processed` (delete it, do not reuse)

The column looks like a drain cursor. It is not one. It is a **dead reader with a live writer**, which is the worst shape to reuse.

- **The reader is dead.** `GetEndedUnprocessedSessionIdsAsync` (`VibeRails/DB/IRepository.cs:26`, `Repository.cs:1557`) and `SelectEndedUnprocessedSessions` (`SqlStrings.cs:928`) have **no callers**.
- **The writer is live, and it fires on read paths.** `SessionTranscriptService.GetOrBuildAsync` upserts `sessionOutPut` and sets `Processed = 1` in the same transaction (`SaveSessionOutputAndMarkProcessedAsync` → `SqlStrings.UpdateSessionProcessed`). It is reached from `ChatHistoryRoutes.cs:100`, `ChatHistoryService.cs:42`, and `SessionResumeService.cs:42` — so merely opening a session in chat history, or resuming it, would mark it “exported”.
- **Every pre-existing row is already `1`.** `Repository.cs:131-136` runs `UPDATE Sessions SET Processed = 1` immediately after the `ALTER`, so on any installed copy the whole history already reads as sent — the exact inverse of “historical local data is eligible, which is the point”.
- **It is not a pure flag.** `UpdateSessionProcessed` also backfills `SessionDisplayName` from the first user input in the same statement, so the write cannot be disentangled from load-bearing work.
- **It is a boolean.** No “when” for the status row, for retry debugging, or for a future schema-version re-export.
- **It only covers half the problem.** `CompressionCaptures` has no equivalent column, so that queue needs a new one regardless. Reusing `Processed` saves one column and buys a semantic collision to do it.

The frontend never reads the flag: `SessionOutputViewDtos.cs:10` surfaces it on `SessionOutputDetailResponse` and nothing in `wwwroot/js` consumes it (`data-export-modal.js`’s `processed` is `processedBytes`, unrelated). `sessionOutPut` row existence is the real transcript cache, and stays that way.

**Decision: remove `Processed` as part of this change**, rather than leaving a second half-dead cursor sitting beside `ExportedUTC` for someone to trip over later. Follow-ups for dead columns do not happen.

1. Delete `GetEndedUnprocessedSessionIdsAsync` from `IRepository` / `Repository`, and `SelectEndedUnprocessedSessions` from `SqlStrings`.
2. Drop `Processed = 1,` from `UpdateSessionProcessed`; keep the `SessionDisplayName` backfill. Rename the statement and `SaveSessionOutputAndMarkProcessedAsync` to say what they now do (`SaveSessionOutputAsync`), and update `ISessionTranscriptService` callers plus `Tests/SessionTranscriptServiceTests.cs`.
3. Remove the `Processed` field from `SessionOutputDetailResponse` (`SessionOutputViewDtos.cs:10`), drop `s.Processed` from `SelectSessionOutput`’s projection (`SqlStrings.cs:922`), and fix the reader ordinals in `Repository.cs:1552` — `Text` moves from index 7 to 6.
4. Remove `Processed INTEGER NOT NULL DEFAULT 0,` from the `Sessions` CREATE TABLE (`SqlStrings.cs:144`) so fresh databases never get it.
5. Drop the physical column and retire the add-migration together (see Schema — that ordering is not optional).

## What We Send

Two independent queues. Session-scoped data rides on the session. Compression captures have no `SessionId` and drain on their own clock.

### 1. Ended sessions

Picked when `EndedUTC IS NOT NULL` and `ExportedUTC IS NULL`. One session per upload (logs are BLOBs and can be large).

| Table | Why |
|---|---|
| `Sessions` | Metadata: id, CLI, environment name, working directory, project display name, timestamps, exit code, parent/display name, `JobRunId` |
| `SessionLogs` | Raw PTY bytes (`Content BLOB`) |
| `TerminalSessionLogs` | Same bytes plus `Sequence`, `Cols`, `Rows`, `IsAlternateScreen` — needed for faithful replay |
| `UserInputs` | Typed input, sequence, git commit hash, timestamps |
| `InputFileChanges` | Diffs tied to those inputs (`UserInputId` / `PreviousInputId`) |
| `sessionOutPut` | Parsed transcript if already materialized; omit the field when no row exists (do not force a transcript build just to export) |
| `ChatSummary` | Include when present; same “if it exists” rule |

`TerminalSessionLogs` is not a duplicate we can drop. `SessionLogs` is the raw byte stream; `TerminalSessionLogs` is the replay tape (viewport size and alternate-screen flag per chunk). Remote analysis and replay need both.

### 2. Compression captures

Picked when `ExportedUTC IS NULL`, oldest `CreatedUTC` first, in small batches.

| Table | Why |
|---|---|
| `CompressionCaptures` | Raw tool text (`RawText`), compressed text, provider/tool/command, char counts, `Changed`, `RewriteAccepted`, `EnabledIds`, `ContentHash`, `SeenCount` |

These rows do not wait for a session to end. Token-saver capture is itself opt-in (`TokenSaverCaptureEnabled`, default off), so this stream is empty unless the user also turned captures on.

**They are not immutable after insert.** `UpsertCompressionCapture` does `ON CONFLICT(ContentHash) DO UPDATE SET SeenCount = SeenCount + 1`, and `TouchCompressionCapture` flushes batched re-sight counts from `CompressionCaptureStore`. `Id`, `CreatedUTC`, and the payload stay fixed on a collision — only `SeenCount` moves. So a row exported at `SeenCount = 3` keeps climbing locally and never re-exports, and the remote copy permanently understates the one number that, per the schema remarks at `SqlStrings.cs:437`, pays for this table.

Options, and the v1 call:

- **Re-null `ExportedUTC` on a bump** — rejected. `RawText` can be megabytes; re-shipping a whole row to move an integer is absurd for a payload that dedupes ~96% by design.
- **A third, tiny envelope kind** (`kind: "capture-counters"`) carrying `[{ id, seenCount }]` for rows whose count moved since export — **chosen**. `Id` is stable across conflicts, so it matches the existing `(apiKey, computerName, capture.Id)` idempotency key and the server applies it as a last-write-wins field update. It needs its own cursor: either `SeenCountExportedUTC`, or cheaper, an `ExportedSeenCount INTEGER` snapshot compared against `SeenCount`.
- **Accept the drift** and document that remote `SeenCount` means “count at first export” — acceptable only if the counter envelope slips past v1, and only if the ingest contract says so explicitly rather than letting the server infer the number means something it does not.

`Trace` is retained locally for compatibility but new rows persist `[]`. Still include the column so the remote copy matches.

## What We Do Not Send

- `Environments` / `EnvironmentSteps` (custom args and prompts)
- `Jobs` / `JobTriggers` / `JobRuns` (the session’s `JobRunId` is enough to correlate)
- `GlobalCache`, `ProjectCache`
- PIN hash/salt (settings.json, not the DB, but must stay off the wire)
- `TokenSavings` — already published by `TokenSavingsPublishJob`
- BERT / sqlite-vec embeddings (`BertEmbeddedUTC` is a local index, not payload)
- `CodeAnalyzerIgnores`, sandboxes, agent metadata
- Live sessions, in-flight terminal tabs, or a session whose owner PID is still alive and `EndedUTC` is null

## Schema

Follow the existing backfill-cursor pattern (`UserInputs.BertEmbeddedUTC`, `Sessions.AggregateEmbeddedUTC`): nullable UTC timestamp, partial index on the unexported set.

```sql
ALTER TABLE Sessions ADD COLUMN ExportedUTC TEXT;
CREATE INDEX IF NOT EXISTS idx_sessions_unexported
    ON Sessions(EndedUTC)
    WHERE ExportedUTC IS NULL AND EndedUTC IS NOT NULL;

ALTER TABLE CompressionCaptures ADD COLUMN ExportedUTC TEXT;
CREATE INDEX IF NOT EXISTS idx_compression_captures_unexported
    ON CompressionCaptures(CreatedUTC)
    WHERE ExportedUTC IS NULL;

-- Only if the counter envelope ships in v1 (see “Compression captures” above):
ALTER TABLE CompressionCaptures ADD COLUMN ExportedSeenCount INTEGER;

-- Retire the half-dead cursor. See the ordering rule below.
ALTER TABLE Sessions DROP COLUMN Processed;
```

Net column count is +2 (or +3 with the counter snapshot) and −1, against removing a trap that would otherwise sit next to the new cursor meaning something entirely different.

### Retiring `Processed` without a boot loop

Two things must go in the **same commit** as the drop:

- **`AddProcessedColumn` must leave `MigrationStatements`** (`SqlStrings.cs:584`). That array re-runs in order on every startup. If the add survives alongside the drop, each boot re-adds the column — now *succeeding*, because the previous boot dropped it — re-runs `SeedProcessedColumn` (a full-table `UPDATE Sessions SET Processed = 1`), then drops it again. Permanent add/drop churn plus a whole-table write every launch.
- **`SeedProcessedColumn` and its special case go with it**: the `ReferenceEquals(migration, SqlStrings.AddProcessedColumn)` branch in `Repository.cs:131-136`.

Also extend `IsBenignMigrationError` (`Repository.cs:69-78`). It currently swallows only “duplicate column” and “already exists”; a re-run `DROP COLUMN` raises **“no such column”**, which falls through to the Warning branch and would be logged on every startup forever. Adding `no such column` to the benign set also quiets the pre-existing `ALTER TABLE ChatSummary DROP COLUMN SummaryBy;` (`SqlStrings.cs:578`), which has this same papercut today — and which is the precedent proving `DROP COLUMN` works on the bundled engine (Microsoft.Data.Sqlite 10.0.11 / SQLitePCLRaw 3.0.5, well past SQLite 3.35).

Rules:

- `ExportedUTC IS NULL` means “not acknowledged by the server”.
- Set it to UTC now only after a successful ACK, in the same SQLite transaction as nothing else of consequence (a single `UPDATE`).
- Never seed existing rows to a sent value. Opt-in later means historical local data is eligible, which is the point.
- Do not let the transcript writer touch `ExportedUTC`. That coupling — an export cursor written by a read path — is precisely what made `Processed` unusable.

Claim-before-upload is not required if the drain job holds the same cross-process lock as today’s export (only one uploader on the machine). If we later allow concurrent drainers, add a short-lived `ExportClaimedUTC` and reclaim stale claims; not in v1.

## Drain Job

New `JobBase` hosted service, same registration gate as `TokenSavingsPublishJob`: **active root backend only**, not terminal-tab children, not `--env` bootstrap hosts.

Suggested names:

- `SessionDataDrainJob`
- lock file `.session-data-drain.lock` beside `state.db` (do not reuse `.data-export.lock` — that file goes away with the snapshot export)

Tick shape, in order:

1. If the opt-in setting is off, return.
2. If there is no saved API key, return.
3. If `VibeRails:ExportUrl` is not a usable absolute HTTPS URL (same parser as `DataExportService.TryParseExportUri`), return.
4. Acquire the cross-process lock; if another root holds it, skip the tick.
5. Drain **one ended session** (oldest `EndedUTC` first). On ACK, set `Sessions.ExportedUTC`.
6. Drain **one batch of compression captures** (oldest `CreatedUTC`, batch size ~25, same order of magnitude as `BertEmbeddingBackfillJob`). On ACK, set those rows’ `ExportedUTC`.
7. Swallow non-cancel failures (network, 5xx, SQLite busy). Leave cursors null so the same work comes back. Log at Warning, not Error, matching `TokenSavingsPublishJob`.
8. Honor `JobPriority.Low` and `ISystemResourceService` deferral already provided by `JobBase`.

Interval: start at 1 minute. Session ends are bursty; a 15-minute token-savings interval is too slow for “the session I just closed”. If a tick uploads a large session, that is fine — the next tick waits a full interval.

A session whose logs are still being written must not be selected: `EndedUTC IS NOT NULL` is the gate. `StaleSessionCleanupJob` already closes orphaned open sessions, after which they become eligible.

Do not build a transcript in order to export. If `sessionOutPut` is empty, send the logs and inputs and let the server live without a parsed transcript.

## Payload and Transport

The current ingest contract is “here is a Brotli-compressed copy of my entire `state.db`”. Incremental drain cannot use that as the document type. The **chunked upload mechanics** (probe / block PUT / commit, SHA-256, `X-Api-Key`, computer name, no redirects) should be reused so large `SessionLogs` / `TerminalSessionLogs` payloads do not depend on a single 30-minute POST.

### Envelope

Each upload is one JSON document, Brotli-compressed, content type `application/json` (or `application/octet-stream` if we keep the existing attachment headers). Discriminated by `kind`:

```text
{
  "kind": "session" | "compression-captures" | "capture-counters",
  "schemaVersion": 1,
  "computerName": "...",
  "exportedAtUtc": "...",
  "session": { ... } | null,
  "captures": [ ... ],
  "counters": [ { "id": "...", "seenCount": 12 } ]
}
```

`session` includes the `Sessions` row plus nested arrays: `logs`, `terminalLogs`, `userInputs` (each with nested `fileChanges`), optional `transcript`, optional `summary`. BLOBs are Base64 in JSON, or a binary framing if a single session’s logs make JSON silly — v1 can stay JSON+Base64 and rely on Brotli; revisit if a session regularly exceeds a few hundred MiB uncompressed.

`captures` is an array of `CompressionCaptures` rows for the batch kind. `counters` carries `SeenCount` updates for rows already exported (see “Compression captures”) and is an absolute value, not a delta — last write wins, so a dropped counter envelope self-heals on the next tick rather than losing a count.

Idempotency key for the server:

- Session: `(apiKey, computerName, session.Id)`
- Capture: `(apiKey, computerName, capture.Id)`
- Counter: same key as the capture it updates; applying one for an unknown id is a no-op, not an error (the capture upload may not have ACKed yet)

Re-uploading a already-ACKed id must be a no-op success, so a crash between ACK and local `ExportedUTC` write does not duplicate data.

### Endpoints

Keep using `VibeRails:ExportUrl` as the **base**. Today it is a single POST. The drain needs either:

- `POST {ExportUrl}` with the envelope (server switches on `kind`), or
- `POST {ExportUrl}/sessions` and `POST {ExportUrl}/compression-captures`

Chunked upload stays on the same path-append rules `DataExportService` already enforces (no query, no fragment). Coordinate the exact paths with the viberails.ai ingest change; the client must not ship until the server accepts the envelope. A probe that returns “not supported” should **not** fall back to uploading the whole `state.db`.

### Auth and safety

Same rules as today’s export:

- API key only in `X-Api-Key`
- HTTPS only
- `AllowAutoRedirect = false` (`CreateNoRedirectHttpMessageHandler`)
- Never log the key
- Computer name via `ComputerNameFormatter` (same as token-savings and the snapshot export)

## Settings and UI

### Persistence

Add `DataExportOptIn` to `Settings` (`VibeRails/Utils/Config.cs`), default `false`. Persist in `settings.json` like the other toggles.

Add a nullable `bool? DataExportOptIn` at the **end** of `AppSettingsDto` (append-only; inserting in the middle rebinds positional arguments). Nullable is the stale-client guard: an older cached `app.js` that omits the field must not flip the stored value on an unrelated save. `AppSettingsRoutes` writes the field only when `HasValue`.

Effective “may drain” predicate, evaluated every tick and whenever the settings page renders:

```text
settings.DataExportOptIn
  && non-empty saved API key
  && DataExportService.TryParseExportUri(ExportUrl)
```

Turning remote access, HTTP relay, or the API key off does not have to clear the stored opt-in; the predicate simply fails closed. Clearing the API key should keep the stored opt-in so restoring the key resumes the drain without another click — unless product wants a re-consent. Re-consent is safer; **v1 re-consents**: if the API key is cleared, set `DataExportOptIn = false` in the same save. A new key does not silently resume shipping session logs.

### UI

Settings, under the API key field, replace the Export Data button and the progress modal with:

- A switch, **Share session data**, off by default
- Helper text that names what leaves the machine: ended session logs (including replay tapes), typed inputs and their file diffs, and token-saver raw tool-text captures when capture is also on
- Visible only when `dataExportConfigured` is true (same as the button today). If the URL is a placeholder, do not show a switch whose only outcome is “not configured”
- Enabled only when a saved API key is present (the typed-but-unsaved key does not count, matching `_updateDataExportAvailability`)
- Enabling with no saved key: refuse, toast “Save a valid API key first”, leave the switch off
- No progress modal. Optional quiet status on the same row: “N sessions pending” / “Last sent {relative time}” from a cheap `GET /api/v1/settings/data-export/status` if we want it; not required for v1

Remove:

- `#settings-export-data-button` and `#settings-export-data-wrapper` as they exist today
- `wwwroot/js/modules/data-export-modal.js`
- `POST /api/v1/settings/export-data` and `GET .../progress`
- `GET /api/v1/settings/db-size` if nothing else uses the size (today it only labels the button)
- Snapshot / Brotli-whole-file / progress types that exist only for the button

Keep and reuse:

- HTTPS URL parser
- No-redirect HttpClient registration
- API key header helper
- Cross-process file lock helper
- `DataExportConfigured` on the settings DTO (the switch’s visibility still depends on it)

## Process Roles

| Process | Runs the drain? |
|---|---|
| Browser / VS Code / Git Guard **root backend** (`isActiveRootBackendProcess`) | Yes, one at a time via the lock file |
| Terminal-tab child (`--parent-pid`) | No |
| `vb --env` bootstrap | No |
| `vb --job-run` / VBD | No — they write sessions; the next root tick drains them after `EndedUTC` is set |

Multiple roots (browser + VS Code) can coexist. The lock file is the duplicate-upload barrier. Row-level `ExportedUTC` is the duplicate-mark barrier.

## Failure, Retry, Opt-out

| Event | Behaviour |
|---|---|
| 401/403 | Treat as invalid API key; skip the rest of the tick; do not mark rows. UI can surface this the next time Settings loads |
| 409 / already-exists | Treat as success; set `ExportedUTC` (idempotent replay) |
| 5xx / timeout / no network | Leave cursor null; retry next tick |
| SQLite busy/locked | Transient; retry next tick (do not invent a failure counter) |
| Toggle off mid-upload | Finish the in-flight request (or cancel on host shutdown); do not start another; do not un-mark |
| User deletes a session in chat history before it drains | Row is gone; nothing to send. Do not resurrect it |
| Capture row deleted via `DELETE /api/v1/compression/captures` | Same: gone locally, skip |
| Schema / envelope rejected | Log and skip that item if the body names an id; otherwise skip the tick so a poison payload cannot tight-loop |

No local delete after ACK. Chat history, resume, BERT backfill, and job-run session links all need the rows. Disk growth is a separate retention feature.

## Implementation Plan

1. **Schema** — `ExportedUTC` + partial indexes on `Sessions` and `CompressionCaptures`. Repository methods: pick next ended unexported session (with nested reads), mark session exported, pick next unexported capture batch, mark captures exported.
2. **Retire `Processed`** — the five-step removal under “`Sessions.Processed`”, plus dropping `AddProcessedColumn` / `SeedProcessedColumn` from the migration list and widening `IsBenignMigrationError`. Do this in the same commit as step 1 so the add and the drop never coexist in `MigrationStatements`.
3. **Envelope + uploader** — New type next to the current export service (or a replacement of `IDataExportService`). Reuse chunked transport, HTTPS parse, API-key header, no-redirect client. Do not snapshot `state.db`.
4. **Job** — `SessionDataDrainJob` registered only on the active root backend, `JobPriority.Low`, cross-process lock, one session + one capture batch per tick.
5. **Settings** — `DataExportOptIn` on `Settings` and nullable on `AppSettingsDto`; write only when `HasValue`; force off when the API key is cleared.
6. **UI** — Switch + copy; hide without a configured export URL; disable without a saved key. Delete the button, modal, progress poll, and db-size label path.
7. **Remove the snapshot export** — Routes, service snapshot/compress-whole-file path, progress singleton if unused, UI tests for the button.
8. **Server ingest** (viberails.ai, sibling change) — Accept the envelope, idempotent upsert by `(apiKey, computerName, id)`, ACK. Client must not fall back to a full DB dump.

Suggested order in the VibeRails repo: schema + `Processed` removal, repository, uploader with tests against a fake HTTP handler, job, settings/UI, then delete the old export. The UI can ship disabled behind the same `dataExportConfigured` flag until the server understands `kind`.

## Tests

- Repository: unexported pick respects `EndedUTC`, ignores live sessions, ignores already-exported rows, orders by `EndedUTC` / `CreatedUTC`; mark is a no-op on missing ids.
- `Processed` removal: building a transcript no longer writes any export cursor — `GetOrBuildAsync` on a session with `ExportedUTC` set leaves it set, and on an unexported one leaves it null (this is the regression the whole section exists to prevent). `SessionDisplayName` is still backfilled. `SessionOutputDetailResponse` round-trips without the field.
- Migration idempotence: initializing the same database twice in a row leaves `Processed` absent, logs no warning on the second pass, and does not run a full-table `UPDATE`. Cheap version: open a repository twice against one temp file and assert `PRAGMA table_info(Sessions)` has no `Processed` both times.
- Capture counters: a re-sight after export bumps `SeenCount` and makes the row eligible for the counter envelope without re-uploading `RawText`; if the counter envelope is deferred, assert the documented drift instead so the behaviour is pinned either way.
- Uploader: no API key / bad URL does no I/O; 401 maps to invalid key; 200 sets nothing itself (job does the mark after ACK); 409 counts as ACK; cancellation does not mark; redirects are not followed.
- Job: toggle off / missing key / missing URL / lock held → no HTTP; after ACK the next pick does not return the same session or capture ids.
- Settings route: omitted `dataExportOptIn` leaves stored value; `true` without a key does not enable drain; clearing the API key forces opt-in off.
- UI (`UITests/tests/settings-data-export.spec.js`): rewrite from button visibility to switch visibility/enablement; no modal.
- Serializer: `AppSettingsDto` still round-trips with the new field at the end (`AppJsonSerializerContextTests`).
- Existing `DataExportServiceTests` / `DataExportRoutesTests` either move to the new uploader or go away with the snapshot path.

## Out of Scope

- Deleting or compacting local session data after export
- Exporting embeddings, environments, jobs, or settings.json
- A “send now” button
- Un-sending or a remote delete API
- Claiming rows for concurrent drainers
- Forcing transcript generation for export
- Changing token-saver capture retention (still uncapped locally, still opt-in via `TokenSaverCaptureEnabled`)
- VBD / `--job-daemon` running the drain (root backend only in v1)

## Open Coordination

The viberails.ai ingest today stores one compressed database per machine. Incremental envelopes need a new document type and idempotent upserts. Until that ships, `DataExportConfigured` can stay true (the URL is real) but the drain job should treat an unknown-kind / 404 probe as “not configured” and skip, never as “upload `state.db` instead”.
