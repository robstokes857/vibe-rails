# Automatic complete backups

`CompleteBackupJob` joins the existing resource-aware `JobBase` lifecycle beside session
data drain. Only active root backends register it. There is no daemon, startup prompt or
opt-in. A configured account starts initial captures. A separate cross-process file lock
coordinates roots; session export and Board sync keep their independent locks.

## Datasets and freshness

| Dataset | Archive | Update cadence |
| --- | --- | --- |
| Board | Complete `board.db`: every board, retained rows, original attachments, full commit snapshots, relationships, options, discussion, context and lane Automations | Changed source, at most every 15 minutes |
| State | Complete `state.db`: environments, Automation definitions/results, sessions and retained legacy tables | Daily |
| Proxy | Complete `proxy_exchanges.db`, including NULL SessionId and later records | Daily |
| Configuration | Settings/configuration, scripts, installation history, custom/native CLI homes and directly referenced Automation scripts | Changed source, at most hourly |

Each 15-second tick checks one dataset. Database/WAL sizes and modification times, and
sorted file inventories, detect edits. Daily forced versions catch writers that preserve
timestamps. Missing databases show `absent` and are rechecked. Configuration freshness is
as of the last queue check; the status API also compares current database metadata.

SQLite snapshots pin a read transaction and copy 256 pages per step through
`IDatabaseSnapshotStore`, yielding and checking cancellation. WAL writers remain available;
capture can pin WAL pages until it finishes. Reserve disk for snapshot, compression and
concurrent WAL growth. Each database has its own capture time. File trees are not atomic;
files which change during copy are reported as incomplete. Preparation has a 30-minute
deadline. Delivery yields after 16 parts or about 45 seconds between requests; an individual
request has a two-minute deadline. Archives exceeding 50,000 parts (roughly 200 GiB compressed)
fail explicitly. Resource pressure defers the job.

## Exact file and credential policy

Included: installation-root `.json/.toml/.yaml/.yml/.md` files, `scripts/`, `envs/`, `history/`,
configured environment paths, default `.codex/.claude/.copilot/.opencode/.grok/.gemini` homes,
and `.config/opencode` plus `.local/share/opencode`. Automation ScriptPath files must resolve
inside their saved repository. Imports, working directories and dependencies are not copied.
`files.json` maps safe archive names to original locations, lengths and hashes. Native
`.db/.sqlite/.sqlite3` files use online snapshots including committed WAL.

Credential exclusions: names containing `credential`, `secret` or `token`; names starting
`.env` or `auth.`; `.claude.json`, `accounts.json`, `oauth_creds.json`, `script_signing.json`;
`.pem/.key/.pfx/.p12` files; `signing-keys` and OS keychains. In files named `settings.json`
or `config.json`, properties are removed recursively after ignoring case/underscores/hyphens
when they contain `apikey`, `password`, `secret`, end with `token`, or equal `tokens`,
`authorization`, `pinhash`, `pinsalt`, `remoteaccountemail`, `remoteaccountkeyfingerprint`.
Ordinary TokenSaver preferences remain. Restored Python scripts require new approvals.

Derived exclusions: `node_modules`, `.git`, `cache`, `caches`, `.cache`, `tmp`, `temp`, `log`,
`logs`, `telemetry`, `bin`, `models`; `.lock/.log/.exe/.dll` files and SQLite sidecars;
deployment `appsettings*` and runtime `vb.*` files. Full database snapshots retain derived
tables already inside them. Separate mining/vector stores, models, sandboxes, repositories,
arbitrary card `@path` targets, unknown standalone files elsewhere and filesystem links are
unsupported. Missing/unreadable/unstable required files produce manifest coverage issues.

These filters are not a general secret detector. Database rows, prompts/history, scripts
and other text configuration may contain embedded credentials and remain private account
data. Credentials must be re-established after recovery.

## Receipts and retry

Private staging lives in `complete-backups/{key-fingerprint}/{dataset}/{version}`. A stable
computer ID distinguishes machines with identical names. Account identity comes from the
authenticated fixed `https://viberails.ai` service. Key changes use a fresh local partition;
old-account receipts are never reused. Key rotation can create redundant versions.

The archive consists of ordered 4 MiB content-addressed parts. The server hashes every
part before its receipt, then validates an immutable UTF-8 manifest containing part hashes,
lengths, source times/fingerprint, format and coverage issues. The archive checksum is the
manifest SHA-256, not a claim of SHA-256 over concatenated payload bytes.

Atomic checkpoints retain progress, retry times and validated receipts. The client checks
account, computer, dataset, version, hash, size and source times before acknowledging.
Interrupted/lost replies retry the same identities. Corrupt staging/checkpoints are retained
for inspection and recaptured. Acknowledged transport parts can be removed locally only
after the receipt is persisted; manifests/receipts remain. Source data and remote versions
are never deleted. Session and bounded Board-sync receipts never enter this state machine.

Restart can reclaim unfinished transport files only inside recognized version directories
without any manifest or receipt. It never recurses into unknown directories or follows links.
Restart also revisits acknowledged versions, validating their persisted receipt, manifest and
remaining part hashes before removing at most 16 transport parts per tick. Manifests, receipts
and corrupt evidence remain. An unreadable checkpoint is preserved and retried after two minutes;
healthy datasets keep running while it is unavailable.

Settings exposes coverage at `GET /api/v1/settings/backups`, under the existing session-plus-tab
gate. General Settings shows successful receipt/source times, pending/failed work, issues
and exclusions. The server must roll out first; older servers' 404 responses remain visible
and retryable. Restore instructions are in `vibe-books/vibe-data/docs/complete-backups.md`.
