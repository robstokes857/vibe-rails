# Opt-in Completed-Session Data Export

## Status

- **Document type:** Implemented POC contract
- **Implemented:** 2026-09-03
- **Client:** `C:\source\vibe-rails`
- **Server:** `C:\source\VibeRails-Front`

This document records the POC that was actually implemented. It supersedes the earlier design
that proposed deleting `Sessions.Processed`, exporting compression captures, or treating an HTTP
409 as acknowledgement.

## Decision

Replace the manual full-`state.db` upload with an off-by-default background export of one completed
session at a time.

> **Note (2026-09-04):** "Replace" is the product direction, not a code-removal instruction. The
> legacy one-shot export (`DataExportService`, `DataExportRoutes`, `data-export-modal.js`, and
> `GET /api/v1/settings/db-size`) is deliberately kept in the codebase beside this POC until the
> new system is proven. Do not delete it.

- The client sends session data; it never sends the SQLite database.
- The client constructs the session JSON and Brotli-compresses it.
- The server owns identity, authorization, blob naming, and the SQL manifest.
- `Sessions.Processed` keeps its existing transcript-cache meaning.
- `Sessions.ExportedUTC` independently records a successful remote acknowledgement.
- Compression captures and their counters are not part of this POC.

Azure Blob Storage remains the right payload store for this design. Session envelopes contain large
binary terminal streams and can grow beyond comfortable relational-row sizes. Azure SQL stores only
the small ownership/idempotency manifest needed to authorize and deduplicate them.

## User Promise

The **Share session data** switch is off by default. When it is on, and a saved API key plus a valid
HTTPS export URL are present, VibeRails uploads existing and future completed sessions in the
background.

- Clearing the API key clears consent.
- Turning the switch off stops future attempts; it does not delete data already sent.
- Live sessions are never eligible.
- A session must have been ended for at least one minute before it is eligible.
- The job attempts at most one session per one-minute tick.
- Local session rows remain in place after export.
- There is no whole-database fallback.

## Session Envelope

Each schema-v1 envelope is deterministic JSON with:

- `schemaVersion`, `kind`, and `sourceId`
- session metadata: CLI, environment, working directory, project display name, timestamps, exit
  code, parent/display name, and optional job-run ID
- `SessionLogs`, including the original raw BLOB bytes
- `TerminalSessionLogs`, including bytes, sequence, columns, rows, and alternate-screen state
- `UserInputs`
- `InputFileChanges`
- the parsed transcript when `sessionOutPut` already exists
- `ChatSummary` when it already exists

The exporter does not build a transcript merely to send it. JSON BLOB values are base64 strings,
which preserves their bytes exactly.

The repository streams this envelope from one deferred SQLite read transaction opened with a
private cache. WAL writers therefore remain unblocked while the export snapshot stays internally
consistent.

## Local Durability and Acknowledgement

The JSON is streamed through Brotli quality 5 into a private durable spool file:

```text
~/.vibe_rails/.session-data-spool/v1/{sessionGuid}.json.br
```

The file is hashed with SHA-256 after it is closed. A failed request or lost acknowledgement reuses
the exact same compressed bytes and digest. Preparation uses one fixed `.json.br.tmp` path per
session, so a crash cannot create unbounded random fragments.

`ExportedUTC` is set only when the final single-upload or chunk-commit response:

1. has status 200 or 201;
2. contains valid JSON; and
3. exactly matches the expected kind, source ID, schema version, SHA-256, compressed length, and
   accepted status (`stored` or `already_exists`).

HTTP 409 and all mismatched or malformed responses are failures, never acknowledgements. The spool
is deleted after `ExportedUTC` is saved. If the remote upload succeeds but the local row disappeared
or was already marked, the spool is also deleted so sensitive data is not stranded indefinitely.

## Transport

The configured base remains `VibeRails:ExportUrl`; it must be an absolute HTTPS URL without query
or fragment. The shipped base is:

```text
https://viberails.ai/api/v1/data-exports
```

All requests send `X-Api-Key`, `X-Computer-Name`, and `X-Envelope-Schema-Version: 1`. Redirects are
disabled so a redirect cannot replay the API key or payload to another host.

Small envelope:

```http
POST /api/v1/data-exports/sessions/{sessionId}
X-Content-SHA256: {sha256}
Content-Type: application/octet-stream
```

Envelopes larger than 4 MiB use resumable blocks:

```http
GET  /api/v1/data-exports/sessions/{sessionId}/chunks/{sha256}?length={bytes}
PUT  /api/v1/data-exports/sessions/{sessionId}/chunks/{sha256}/{index}
POST /api/v1/data-exports/sessions/{sessionId}/chunks/{sha256}/commit
```

The probe reports the server block size and already-staged indices. The client retries transient
block failures and re-probes once if commit reports missing blocks.

## Server Storage and Identity

The API-key authorization filter resolves the key to its owning server-side `UserId`. No user ID is
accepted from the client. The immutable blob name is:

```text
users/{userId}/session-data/sessions/{sessionId}/{sha256}.json.br
```

The `data-exports` container is created with anonymous access disabled, and its access policy is
also explicitly set to private when an existing container is first used by the process.

Azure SQL stores only `DataEnvelopes`:

- `UserId`
- `Kind`
- `SourceId`
- `ComputerName`
- `SchemaVersion`
- `Sha256`
- `Sha256Verified`
- `BlobName`
- `CompressedBytes`
- `ReceivedUtc`

`(UserId, Kind, SourceId)` is unique. A new identity returns 201 `stored`; an exact retry returns
200 `already_exists`; different content for that identity returns 409. The response echoes the
server-recorded identity fields so the client can validate its acknowledgement.

## Local Schema and Scheduling

The local `Sessions` table adds:

```sql
ExportedUTC TEXT NULL
```

and a partial eligibility index on ended, unexported sessions. Existing rows remain `NULL`, making
historical completed sessions eligible after explicit opt-in. `Processed` is neither removed nor
read or written by the exporter.

`SessionDataDrainJob` is registered only in active root web hosts, including browser, Web UI,
Git Guard, and VS Code root modes. It is excluded from terminal children, `--env` bootstrap hosts,
daemon/maintenance hosts, worker job-run hosts, and script-only job-run hosts. An in-process gate
plus a cross-process lock prevents duplicate drainers on one machine.

## Verification

Coverage includes:

- local schema migration, eligibility ordering, settle delay, and one-session-per-tick behavior
- a snapshot-consistent envelope containing both raw-byte log streams, inputs, file changes,
  transcript, and summary
- Brotli/spool creation, exact retry reuse, single and chunked routes, and ACK validation
- 409 and mismatched-ACK rejection
- default-off settings, stale-client preservation, API-key clearing, and UI disclosure
- root/non-root process-role registration
- server authorization metadata, request validation, status semantics, idempotency conflicts,
  blob paths, manifests, single uploads, and resumable blocks
- EF model/migration consistency

## Deferred Beyond the POC

- Compression-capture and counter export
- Normalized remote session/log/input tables and replay UI
- Installation IDs and multi-install identity
- Per-user quotas and rate-limit claims
- Retention, user deletion, orphan-blob reconciliation, and lifecycle policies
- Server-side Brotli/JSON/schema validation
- Whole-payload SHA-256 verification for resumable uploads (`Sha256Verified` is `false` there)
- Azure-emulator integration and full two-user HTTP-pipeline isolation tests

The deterministic user/session/digest path and SQL manifest leave room for those features without
changing the client acknowledgement rule.
