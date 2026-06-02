# Remote Debug Bundle Playbook

When a remote/beta user hits a terminal bug you can't reproduce, you don't need their machine — they click **Send debug log** in the app and the failing session's raw data is uploaded (encrypted) to viberails.ai. You pull it down as a plain SQLite `.db` and run the **same** tools from `SESSION_DEBUG_PLAYBOOK.md` against it. This runbook is the glue: get the bundle local, then hand off to the existing loop.

## How a bundle gets to you

1. User clicks **Send debug log** — in the terminal tab's `⋮` menu (active session) or a Chat History row (past session) — and optionally types what went wrong.
2. The desktop app builds a minimal SQLite DB of just that session (`Sessions`, `SessionLogs`, `TerminalSessionLogs`, `UserInputs`), Brotli-compresses it, AES-256-GCM encrypts it, RSA-wraps the AES key with the bundled public key, and `POST`s the envelope to `viberails.ai/api/v1/debug-bundles` with their `X-Api-Key`.
3. The server stores it **encrypted at rest**. It is decrypted server-side (with the private key in `CERTS/uploads/`) only when you download it.

The user must have a valid API key configured — without one the action tells them to add one, and the server rejects a bad key.

## The data is real and sensitive

A bundle is one user's actual terminal session: prompts, paths, command output, possibly secrets. Treat it exactly like the local `state.db` rules in `SESSION_DEBUG_PLAYBOOK.md`:

- Pulled `bundles/*.db`, any `*.decoded.txt`, and raw captures stay **local and untracked** (`bundles/` is git-ignored).
- Only commit **minimized, sanitized** regression fixtures — never the pulled bundle.

## 1. List what's been sent

```
cd python-scripts
python pull_debug_bundle.py --list
```

Auth uses your VibeRails API key (the admin account): passed via `--api-key`, `$VIBERAILS_API_KEY`, or read from `~/.vibe_rails/settings.json` (older builds: `state.json`, still read as a fallback). A `403` means the key isn't the admin account.

Each row shows `id`, upload time, CLI, size, uploader email, session id, and the user's note. Pick the `id` you want.

> Prefer SSMS? The bundles live in the `DebugBundles` table on the VibeRails-Front SQL Server. `SELECT Id, Email, SessionId, Cli, Message, FileSizeBytes, UploadedAt FROM DebugBundles ORDER BY UploadedAt DESC;` lists them, and `DELETE FROM DebugBundles WHERE Id = …;` removes one. (Don't try to extract the blob via SSMS — it's encrypted; use the script to get a usable `.db`.)

## 2. Pull it local

```
python pull_debug_bundle.py --id <id>
```

Writes the decrypted SQLite database to `python-scripts/bundles/<session-id>.db`. (Use `--out path` to override, `--url http://localhost:5057` to hit a dev server.)

## 3. Debug it with the existing tools

Every script in `python-scripts/` takes `--db`, so point them at the pulled file and follow `SESSION_DEBUG_PLAYBOOK.md` unchanged. The session id is the `.db` filename.

```
# Decode the raw stream (ANSI spelled out)
python decode_session.py --db bundles/<session-id>.db <session-id>

# Classify redraws / double-prints
python analyze_doubleprint.py --db bundles/<session-id>.db <session-id>

# Replay the Waiting-tab observer heuristic
python replay_waiting_observer.py --db bundles/<session-id>.db <session-id>

# Pull a fixture range for a regression test
python export_chunks_fixture.py --db bundles/<session-id>.db <session-id> --from-id N --to-id M --out ../Tests/.../session_<prefix>_log.bin
```

From here, you're in `SESSION_DEBUG_PLAYBOOK.md` at **step 3 (Pull UserInputs)** / **step 4 (Classify)** — the bundle's tables are identical to a real `state.db`, just scoped to one session.

## 4. Clean up

Delete the bundle from the server once you've reproduced it locally (SSMS `DELETE`, or `python pull_debug_bundle.py` will gain a delete flag if needed). Keep the local `.db` only as long as you need it; the sanitized regression fixture is the permanent artifact, not the bundle.

## Don't

- Don't commit a pulled `bundles/*.db` or anything decoded from it — same rule as raw `state.db` captures.
- Don't ship the private key. Only `rsa_public_key.pem` ships with the client (`VibeRails/Certs/`); the private key + `pass.txt` live only on the server in `CERTS/uploads/` and are git-ignored.
- Don't write a new decode/analyze script — the `--db` flag means the existing ones already work on a pulled bundle.
