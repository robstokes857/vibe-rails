# Compression runbook — judging a capture

**Purpose.** This is the jump-off for using an LLM (usually Claude, in this repo) as a
judge of the token saver's output. You found something that looks wrong. You have a
capture GUID. You point an agent at this file and that GUID, and it comes back with a
verdict.

**The one-line invocation:**

> Read `runbooks/compress_runbook.md` and judge capture `a3f1c8e2-…`.

Everything below is written for the agent that gets that message.

---

## 0. What a capture is

When diagnostic capture is enabled, every allowlisted `tool_result` string the token saver
*considers* — rewritten or not — is offered to the background writer for the
`CompressionCaptures` table in `~/.vibe_rails/state.db`. When the Codex saver is active, it also
offers recognized textual outputs from non-allowlisted tools as unchanged diagnostic observations;
those rows have identical raw/compressed text, `RewriteAccepted = false`, and an empty trace.
Images and other non-text blocks are excluded. Capture is best-effort: the bounded in-memory queue
drops diagnostics under sustained database backpressure rather than blocking the proxy relay.

A capture holds:

| Field | Meaning |
| ----- | ------- |
| `Id` | The GUID. The handle for everything. |
| `Provider` | `anthropic` / `openai`. |
| `ToolName` | `Bash`, `PowerShell`, `Read`, … The tool whose output this is. |
| `Command` | The shell command, when we could see it. **`NULL` is meaningful** — see §4. |
| `RawText` | The unescaped string the rewriter observed. Re-runnable. |
| `CompressedText` | The pipeline's candidate output. It was forwarded only when `RewriteAccepted` is true. |
| `RewriteAccepted` | Whether that candidate was actually forwarded. `false` with changed text means the wire-size guard rejected it and sent `RawText`; `false` with unchanged text can be a diagnostic-only Codex observation. |
| `Trace` | Legacy persisted column; new DB rows leave it empty. Live in-memory captures carry pipeline entries when compression runs and an empty list for diagnostic-only observations. |
| `EnabledIds` | The exact stage/scope set active for this observation. |

Background: [`TokenSaver/README.md`](../../TokenSaver/README.md). Read it first if you
have not — you cannot judge a stage without knowing its invariants.

Worked example: [`truncation_file_reads.md`](truncation_file_reads.md) — the file-read
truncation investigation (2026-08-29). Read it if you are judging a `truncate-long`
capture, if you are about to trust the savings meter's headline number, or if you want a
worked example of this loop end to end: a suspicion, a de-duplicated measurement over
`proxy_exchanges.db`, a real hole, a fix, and a replay that scores it. §7 there is the
recipe for re-running that measurement.

## 1. Fetching the capture


```sql
-- sqlite3 ~/.vibe_rails/state.db
SELECT Provider, ToolName, Command, CharsBefore, CharsAfter, Changed,
       RewriteAccepted, EnabledIds
FROM CompressionCaptures WHERE Id = '<GUID>';

SELECT RawText FROM CompressionCaptures WHERE Id = '<GUID>';
SELECT CompressedText FROM CompressionCaptures WHERE Id = '<GUID>';
SELECT Trace FROM CompressionCaptures WHERE Id = '<GUID>';
```

## 1. What and why.
  - I want to save tokens and make it easy to do that.
  - This is really just a context jump off place.
  - I will give you a task and you can use this as a starting point.

## 2. History

- **2026-07-15** — Runbook created alongside the stage-catalog rework. The old
  `TokenSaverLevel` tiers (off/safest/safe/medium/high) were replaced by independently
  toggleable stages; `cr-collapse` and `ansi-strip` were moved OFF by default at the
  owner's request (preference about terminal-shaped output, not a safety finding);
  `scope-read`/`scope-grep` added as off-by-default toggles so their safety could be
  settled with captures instead of argument. Persisted captures are uncapped by explicit
  decision, while the best-effort in-memory writer is bounded so database backpressure cannot
  block or exhaust the relay — see `Config.TokenSaverCaptureEnabled`.
- **2026-07-18** — Curated-set simplification. The per-stage settings UI was removed;
  the user-facing surface is now one on/off switch per LLM (Claude/Codex/OpenCode),
  and an enabled saver always runs `CompressionCatalog.DefaultSelection`.
  `dedupe-lines` and `truncate-long` joined the defaults (both leave explicit
  markers); `cr-collapse`/`ansi-strip` stay off (terminal-shaped output, owner
  preference reaffirmed), `scope-read`/`scope-grep` stay off (failed-Edit risk).
  For bisecting a misbehaving stage on live traffic, hand-edit
  `TokenSaverStageOverride` in settings.json (null = curated set; the old
  `TokenSaverStages` key is dead). This runbook's preview-based workflows are
  unchanged — `POST /api/v1/compression/preview` still takes any `enabledIds` set.
- **2026-07-19** — New stage `elide-passed-tests` (Lossy, in the curated defaults),
  rtk-inspired. A new `CommandShape.TestRun` recognises test-runner commands
  (pytest / python -m pytest / uv run pytest, dotnet test, go test, cargo test,
  npm/pnpm/yarn/bun test scripts, npx jest/vitest/mocha, playwright test); the
  filter replaces each run of individual passing-test lines with a
  `[... N passed ...]` marker and keeps failures, errors, skips, logs and
  summaries verbatim, in place. Per-line patterns require runner-specific
  structure (`::…PASSED`, `✓ `, `Passed … [ms]`, `--- PASS:`, `… ... ok`), so
  summaries and prose never match; pipes/redirects/wrappers decline via the
  existing whole-command rule; ESC/BEL/CR still fail-open the whole string.
  Ordering bonus: running before `truncate-long` keeps a big suite's failure
  section out of the truncated middle. Condenser stages renumbered 9-10 → 10-11.
  **2026-07-28** — I Robert the human cam in this file and didn't like most of it so I deleted most of it.
- **2026-08-29** — `truncate-long` learned about file reads. `scope-read` is off because
  rewriting file contents breaks the model's `old_string` matching, but `scope-shell` is on
  and `cat`/`sed`/`Get-Content` through a shell tool is a file read — so T was cutting 250+
  line holes in the middle of source, 71% of everything removed from Claude on 2026-08-28.
  New `CommandShapes.ReadsFileContents` (deliberately permissive — inverse safety polarity
  to `Classify`, read both doc comments before "harmonising" them) widens T's keep budget
  from 150/50 to 1200/200 on those payloads. Full writeup, evidence and known gaps:
  [`truncation_file_reads.md`](truncation_file_reads.md).
