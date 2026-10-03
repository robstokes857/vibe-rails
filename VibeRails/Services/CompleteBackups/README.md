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
timestamps. Missing databases show `absent` and are rechecked. Configuration files are walked
only when a version may be due (or at a receipt), not on every tick, so its freshness is as of
the last walk; the status API also compares current database metadata.

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
inside their saved repository; an explicitly referenced script is captured whatever its name
(`rotate_token.py`), and a missing one or a relative stored repository path is a coverage issue,
never a silent omission. Imports, working directories and dependencies are not copied.

Every configured environment path is captured as its own required tree, so a missing one is a
coverage issue and an environment named like a credential or cache (`Auth review`,
`token-cache`) is never filtered out by its label; homes under `envs/` keep their
`installation/envs/...` entry names and are not walked twice.

CLI homes (the native homes, `envs/` and configured environment paths) contribute configuration,
instructions, skills/commands/agents, hooks, plugin manifests and Claude project `memory/`, not
runtime state: `*.jsonl` transcripts and prompt history, `sessions`, `archived_sessions`,
`session-state`, `history-session-state`, `session-env`, `file-history`, `shell-snapshots`,
`debug`, `todos`, `statsig`, `packages`, `vendor`, `vendor_imports`, GUID-named per-session
directories and `logs*`/`*history*` SQLite stores are excluded. They change whenever a CLI runs,
so a configuration fingerprint containing them never settled and every hourly version re-zipped
gigabytes; VibeRails' own session records stay in `state.db`.
`files.json` maps safe archive names to original locations, lengths and hashes. Native
`.db/.sqlite/.sqlite3` files use online snapshots including committed WAL.

Credential exclusions are matched as a family, not a list: names containing `credential`,
`secret` or `token`; names with a whole `auth`, `oauth`, `creds` or `accounts` segment
(`auth.json`, `mcp-auth.json`, `oauth_creds.json`, `mcp-oauth-tokens.json`, `google_accounts.json`);
names starting `.env`; `.claude.json`, `script_signing.json`; `.pem/.key/.pfx/.p12` files;
`signing-keys` and OS keychains. `.claude.json` is matched as a prefix, so Claude's
`backups/.claude.json.backup.*` copies are excluded too. Browser state is excluded as a whole:
directories whose name contains `browser` or is `chrome`/`chromium` (the Antigravity Chromium
profile), plus `Cookies`, `Login Data`, `Web Data` and `Local State` wherever they appear.

Every captured `.json`/`.jsonc` file is rewritten, not copied, because MCP servers live in
settings, `mcp_config.json`, `.mcp.json`, plugin manifests and `opencode.json(c)` alike (comments,
trailing commas and a BOM are tolerated). Properties are removed recursively after ignoring
case/underscores/hyphens when they contain `apikey`, `password`, `passwd`, `secret`, `credential`,
`privatekey`, `accesskey`, `cookie`, `authorization`, `bearer`, `sessionid`, `sessionkey`, end with
`token` or `auth` (or, beyond a bare `key`, with `key`/`pat`: `OPENAI_KEY`, `GITHUB_PAT`), or equal
`tokens`, `pinhash`, `pinsalt`, `remoteaccountemail`,
`remoteaccountkeyfingerprint`. Object-valued `env`, `environment`, `headers` and `http_headers`
blocks keep their key but lose every entry, because credentials there use arbitrary names
(`X-Auth`, `DATABASE_URL`). Every string value is also scanned: a URL password, and everything
after the separator of any `name=value` or `Name: value` pair whose name is a credential, however
nested (`--header=Authorization: Bearer ...`, `--env=API_KEY=...`, `...?a=1&token=...`), become
`[redacted]`; in space-separated text and in string lists (`args`, OpenCode's `command` array) so
does the word or item after a credential-named flag (`--api-key ...`) or `Bearer`/`Basic` (a value
that opens a quote is redacted to the end of the string unless its own word closes it unescaped;
shell quoting is not parsed), and the
value carried by a header or environment flag whatever its name (`--header "X-Deployment: ..."`,
`-H`, `--env NAME=...`, `-e`, the `--header=`/`--env=` forms and the attached short forms
`-HName: ...`/`-eNAME=...`), for the same reason the blocks
lose every entry: the name stays (`X-Deployment:[redacted]`, `NAME=[redacted]`), a header value
without a name is replaced whole, and a bare `-e NAME` pass-through is kept.
Every `.toml` file (Codex and Grok `config.toml`) gets the same rules line by line: comments are
dropped, an env/headers table keeps its header but no entries, inline env/headers tables become
`{}`, dotted `env.X` keys and credential-named keys are dropped, and string values and arrays go
through the same string redaction, every string item of an array (basic, literal or multi-line,
`"--api-key"` beside `"""..."""`) forming one decoded argument sequence and keeping its own quoting
when rewritten; an unterminated string is replaced whole. Names and values are
classified as the tools read them: TOML quoted keys and basic strings are unescaped first
(`"http\u005fheaders"`, `"--api\u002dkey"`, line-ending backslashes) and re-escaped only when
something was redacted, and query/flag names are also checked percent-decoded (`api%5Fkey`); a
string whose percent-decoded form reveals a credential pair the raw scan missed is replaced whole. A file that cannot be parsed is reported and left out;
its entry is never written empty. YAML is copied verbatim. Ordinary TokenSaver preferences remain.
Restored Python scripts require new approvals.

Derived exclusions: `node_modules`, `.git`, any name containing `cache`, `tmp`, `temp`, `log`,
`logs`, `telemetry`, `bin`, `models`; `.lock/.log/.tmp/.exe/.dll` names and SQLite sidecars;
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

The account service is asked before anything is staged, so a rejected key or a server without
complete backups never leaves an archive on disk. A new version's ID is checkpointed before
staging: a crash after its manifest is written resumes that version's delivery, and one before
it (a `preparing` checkpoint whose directory has no manifest) is forgotten and reclaimed. Every
capture or delivery failure, including SQLite busy errors from the stores the configuration walk
reads, is recorded with backoff instead of escaping the tick.

Restart can reclaim unfinished transport files only inside recognized version directories
without any manifest or receipt: parts, snapshots, native database snapshots with their SQLite
sidecars, and large-file copies. It never recurses into unknown directories or follows links.
Restart also revisits acknowledged versions, validating their persisted receipt, manifest and
remaining part hashes before removing at most 16 transport parts per tick. Manifests, receipts
and corrupt evidence remain. An unreadable checkpoint is preserved and retried after two minutes;
healthy datasets keep running while it is unavailable.

Settings exposes coverage at `GET /api/v1/settings/backups`, under the existing session-plus-tab
gate. General Settings shows successful receipt/source times, pending/failed work, issues
and exclusions. The server must roll out first; older servers' 404 responses remain visible
and retryable. Restore instructions are in `vibe-books/vibe-data/docs/complete-backups.md`.
