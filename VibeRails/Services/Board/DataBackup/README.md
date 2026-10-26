# PostgreSQL Board data backup

Owner request: VB-BSS1H-294. This is an independent data-backup stream, not the hosted
Board UI projection and not an Azure archive. Existing Board sync and Azure archives
are unchanged. The companion server migration and release must precede the desktop release.

`BoardDataBackupJob` runs only in an open root backend, under the normal low-priority
resource gate. With a configured account key it captures immediately on its first
minute tick, then checks again fifteen minutes after the preceding complete receipt.
Each tick delivers bounded batches. Large initial captures can span multiple ticks.
No daemon, opt-in, manual migration, or source-data deletion is introduced.

## Coverage and retention

`IBoardStore.ReadDataBackupAsync` reads **every retained table in board.db** in a single
deferred SQLite read snapshot. This includes every project/board regardless of UI sync,
soft-deleted cards, hidden discussion, rejected/unsent entries, saved reviews/options,
relationships, attachments, full commit snapshots, schema definitions and future tables.
No UI pagination, preview limits or remote-publication eligibility apply. Hard-deleted
local data cannot be recovered before its first capture. State/session recordings continue
through their existing separate backup path; external `@path` files and tracker tokens
outside board.db are not Board data.

Rows are queryable JSON objects with `table`, `key` (JSON of the SQLite primary-key values,
or rowid for a table without a primary key) and `values`. Original column names and native
null/integer/real/string values are preserved. BLOBs and text above 64 KiB become
`{"$type":"blob|text","parts":["sha256",...],"bytes":N}`. Parts contain original bytes,
at most 2 MiB each; text uses UTF-8. Repeated parts and row revisions are stored once per
owner. The `$schema` record contains table, index, view and trigger SQL. Text values that
look like JSON remain strings, preserving their exact original content.

The new PostgreSQL tables are `BoardDataBackups` (immutable row revisions and binary
parts) and `BoardDataBackupSnapshots` (immutable complete inventories). They have no
foreign key to the UI Board tables. UI card/board deletion and unpublishing cannot delete
them. There is no backup deletion/expiry endpoint or retention job. Normal whole-account
deletion still applies through the owner relationship. Do not add UI lifecycle cascades.

Each manifest names its computer, capture times, every row hash and every content hash.
Only after the server verifies their presence and all row content references does it
return the complete snapshot receipt. Older versions remain queryable after local changes
or deletion. A manifest records membership in that exact snapshot; do not reconstruct a
snapshot by selecting the latest timestamp independently for each row.

## Transport and recovery

The fixed HTTPS destination is `https://viberails.ai/api/v1/data-exports/board/`.
Account, inventory, content, rows and snapshots require an owner-bound Sessions-capable
API key. The client rejects redirects and validates all acknowledgements. No Azure write
or public/browser download path is used. The server applies the existing backup admission
gate and SQL owner lock, with separate PostgreSQL quotas (500 GB, one million immutable
items, 100,000 snapshots, one GB of manifests per owner). Limit exhaustion fails visibly;
it never truncates a backup or deletes an old one.

Private staging under `~/.vibe_rails/board-data-backups` is partitioned by account-key
fingerprint, with one stable computer ID, a cross-process lock and atomic checkpoints.
Failed uploads retain their prepared snapshot and resume with backoff. Lost commit replies
retry the same immutable version. New account keys cannot reuse another key's checkpoint.
Only hash-named local transport files are cleaned after a matching durable receipt; source
data and old remote versions are retained. An unchanged capture needs no new snapshot.

`GET /api/v1/settings/backups` includes `boardDataBackup`, separate from the retired full
Azure snapshot status. The ordinary data-upload feature journal records successes/failures.

## Reading the data later

This retained source is intended for future Run Books generation and BERT indexing.
Keep generated runbooks and embeddings separate, linked to their source snapshot/row
hashes; generated summaries must not replace the original archived card material.

For an explicitly authorized owner, PostgreSQL can query a row's original card fields:

```sql
SELECT "RecordKey", "PayloadJson"::jsonb -> 'values' ->> 'Title' AS title
FROM "BoardDataBackups"
WHERE "UserId" = :owner AND "Kind" = 'row' AND "TableName" = 'BoardCards';
```

This returns retained revisions, not just the current snapshot. Select an owned
`BoardDataBackupSnapshots` manifest first for exact point-in-time membership. Resolve its
row hashes against owner-scoped `BoardDataBackups`, verify SHA-256 over exact UTF-8
`PayloadJson`, and concatenate referenced `Content` parts after verifying each hash and
the declared byte length. This produces full original values for analysis or restoration.
For SQLite restoration, create tables first, insert values without running archived
triggers, then recreate indexes, views and triggers. Restore only into an explicit
disposable/owner-selected recovery target, never over the active application database.
